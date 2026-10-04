using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Scalpal.Capture
{
    // One attempt's recording. Frames are accepted only while the segment is open,
    // must carry strictly increasing sensor timestamps, are JPEG-encoded on one
    // background thread and streamed to disk. At most QueueLimit raw images are
    // held in memory; overflow is dropped and counted, never buffered unboundedly.
    public sealed class CaptureSegment : IDisposable
    {
        public const int QueueLimit = 3, JpegQuality = 80;
        public readonly string directory, clipPath, timingPath, manifestPath;
        public readonly string sessionId, attemptId, runId, presentationMode;
        public readonly double segmentStartRealtime, maxDurationSeconds;
        public readonly long maxBytes;
        public readonly int queueLimit;
        // Test seam: holds the encoder so queue bounds can be checked deterministically.
        public bool HoldEncoder { get { lock (gate) return hold; } set { lock (gate) { hold = value; Monitor.Pulse(gate); } } }
        bool hold;
        public string clipArtifactId = "", timingArtifactId = "", manifestArtifactId = "";
        public string StopReason { get; private set; } = "";
        public bool Accepting { get; private set; } = true;
        public bool Capped { get; private set; }
        public int FrameCount { get { lock (gate) return sensorUs.Count; } }
        public int DroppedNonMonotonic { get; private set; }
        public int DroppedQueueFull { get; private set; }
        public int DroppedInvalid { get; private set; }
        public string Fault { get { lock (gate) return fault; } }
        public bool Finalized { get; private set; }

        readonly object gate = new object();
        readonly Queue<CaptureFrame> queue = new Queue<CaptureFrame>();
        readonly List<long> sensorUs = new List<long>();
        readonly List<double> received = new List<double>();
        readonly List<float> poses = new List<float>();
        readonly List<bool> poseValid = new List<bool>();
        readonly Thread worker;
        MjpegAviWriter writer;
        int width, height, queuedBytes;
        long lastSensorUs = long.MinValue;
        bool closed;
        string fault = "";

        public CaptureSegment(string root, string sessionId, string attemptId, string runId, string presentationMode,
            double segmentStartRealtime, double maxDurationSeconds, long maxBytes, int queueLimit = QueueLimit)
        {
            this.queueLimit = Math.Max(1, queueLimit);
            this.sessionId = sessionId; this.attemptId = attemptId; this.runId = runId; this.presentationMode = presentationMode;
            this.segmentStartRealtime = segmentStartRealtime; this.maxDurationSeconds = maxDurationSeconds; this.maxBytes = maxBytes;
            directory = Path.Combine(root, Safe(attemptId) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(directory);
            clipPath = Path.Combine(directory, "hands.avi");
            timingPath = Path.Combine(directory, "capture-frames.json");
            manifestPath = Path.Combine(directory, "capture-manifest.json");
            worker = new Thread(Encode) { IsBackground = true, Name = "ScalpalCaptureEncoder" };
            worker.Start();
        }
        static string Safe(string id) { var b = new StringBuilder(); foreach (char c in id ?? "") b.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'); return b.Length == 0 ? "attempt" : b.ToString(); }

        // Returns false for any frame outside the open segment or failing a check.
        public bool Submit(CaptureFrame frame)
        {
            if (!Accepting || frame == null) { frame?.recycle?.Invoke(frame.rgba); return false; }
            if (frame.rgba == null || frame.width <= 0 || frame.height <= 0 || frame.rgba.Length < frame.width * frame.height * 4
                || (width != 0 && (frame.width != width || frame.height != height)) || frame.receivedRealtime < segmentStartRealtime)
            { DroppedInvalid++; frame.recycle?.Invoke(frame.rgba); return false; }
            if (frame.sensorTimeUs <= lastSensorUs) { DroppedNonMonotonic++; frame.recycle?.Invoke(frame.rgba); return false; }
            lock (gate)
            {
                if (fault.Length > 0) { frame.recycle?.Invoke(frame.rgba); return false; }
                if (sensorUs.Count > 0 && (frame.sensorTimeUs - sensorUs[0]) / 1e6 > maxDurationSeconds) { CapLocked("duration_cap"); frame.recycle?.Invoke(frame.rgba); return false; }
                long estimate = (writer?.Length ?? 0) + queuedBytes + frame.width * frame.height;
                if (estimate > maxBytes) { CapLocked("size_cap"); frame.recycle?.Invoke(frame.rgba); return false; }
                if (queue.Count >= queueLimit) { DroppedQueueFull++; frame.recycle?.Invoke(frame.rgba); return false; }
                width = frame.width; height = frame.height; lastSensorUs = frame.sensorTimeUs;
                sensorUs.Add(frame.sensorTimeUs); received.Add(frame.receivedRealtime); poseValid.Add(frame.poseValid);
                var p = frame.cameraPose;
                poses.AddRange(new[] { p.position.x, p.position.y, p.position.z, p.rotation.x, p.rotation.y, p.rotation.z, p.rotation.w });
                queue.Enqueue(frame); queuedBytes += frame.width * frame.height;
                Monitor.Pulse(gate);
            }
            return true;
        }
        void CapLocked(string reason) { Capped = true; Accepting = false; StopReason = reason; }

        public void StopAccepting(string reason) { if (Accepting) { Accepting = false; StopReason = reason; } }

        void Encode()
        {
            while (true)
            {
                CaptureFrame frame;
                lock (gate)
                {
                    while ((queue.Count == 0 || hold) && !closed) Monitor.Wait(gate);
                    if (queue.Count == 0) return;
                    frame = queue.Peek();
                }
                try
                {
                    if (writer == null) writer = new MjpegAviWriter(clipPath, frame.width, frame.height);
                    // ImageConversion.EncodeArrayToJPG is documented thread safe.
                    byte[] jpeg = ImageConversion.EncodeArrayToJPG(frame.rgba, GraphicsFormat.R8G8B8A8_UNorm, (uint)frame.width, (uint)frame.height, 0, JpegQuality);
                    writer.AppendFrame(jpeg, jpeg.Length);
                }
                catch (Exception e) { lock (gate) { if (fault.Length == 0) fault = "encoding failed: " + e.Message; } }
                frame.recycle?.Invoke(frame.rgba);
                lock (gate) { if (queue.Count > 0 && ReferenceEquals(queue.Peek(), frame)) { queue.Dequeue(); queuedBytes -= frame.width * frame.height; } }
            }
        }

        // Drains the encoder, closes the container and writes timing + manifest.
        // Safe to call off the main thread. Returns "" or a failure reason.
        public string Finish(CaptureProvenanceManifest manifest, int timeoutMs = 30000)
        {
            StopAccepting(StopReason.Length > 0 ? StopReason : "case_end");
            lock (gate) { closed = true; Monitor.Pulse(gate); }
            if (!worker.Join(timeoutMs)) return "the encoder did not drain in time";
            if (Fault.Length > 0) return Fault;
            int n = sensorUs.Count;
            if (n == 0 || writer == null) return "no camera frames were recorded";
            if (writer.FrameCount != n) return "encoded frame count differs from timing";
            double duration = (sensorUs[n - 1] - sensorUs[0]) / 1e6;
            double fps = n > 1 && duration > 0 ? (n - 1) / duration : 15;
            try { writer.Finish(fps); writer.Dispose(); }
            catch (Exception e) { return "closing the clip failed: " + e.Message; }
            var timing = new CaptureTiming { sessionId = sessionId, attemptId = attemptId, inputArtifactId = clipArtifactId, clock = manifest.capture.clock.frameTimestamps,
                sensorTimeUs = sensorUs.ToArray(), receivedRealtimeSeconds = received.ToArray(), cameraPose = poses.ToArray(), poseValid = poseValid.ToArray() };
            File.WriteAllText(timingPath, JsonUtility.ToJson(timing), new UTF8Encoding(false));
            var c = manifest.capture;
            manifest.sessionId = sessionId; manifest.attemptId = attemptId; manifest.inputArtifactId = clipArtifactId;
            c.runId = runId; c.presentationMode = presentationMode;
            c.video.width = width; c.video.height = height; c.video.frameCount = n; c.video.jpegQuality = JpegQuality;
            c.video.nominalFps = Math.Round(fps, 4); c.video.bytes = new FileInfo(clipPath).Length; c.video.sha256 = Sha256(clipPath);
            c.clock.firstFrameSensorUs = sensorUs[0]; c.clock.lastFrameSensorUs = sensorUs[n - 1];
            c.clock.segmentStartRealtimeSeconds = segmentStartRealtime; c.clock.firstFrameRealtimeSeconds = received[0];
            c.clock.captureStartRunSeconds = Math.Max(0, received[0] - segmentStartRealtime); c.clock.clockAligned = false;
            c.frames.timingArtifactId = timingArtifactId; c.frames.timingSha256 = Sha256(timingPath); c.frames.monotonic = true;
            c.frames.droppedNonMonotonic = DroppedNonMonotonic; c.frames.droppedQueueFull = DroppedQueueFull; c.frames.droppedInvalid = DroppedInvalid;
            int valid = 0; foreach (var v in poseValid) if (v) valid++; c.frames.poseValidFrames = valid;
            c.segment.stoppedBy = StopReason; c.segment.truncated = Capped; c.segment.durationSeconds = duration; c.segment.maxDurationSeconds = maxDurationSeconds;
            if (!CaptureContract.Validate(manifest, timing, out var reason)) return "capture manifest invalid: " + reason;
            File.WriteAllText(manifestPath, CaptureContract.ToJson(manifest), new UTF8Encoding(false));
            Finalized = true;
            return "";
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(s);
                var b = new StringBuilder(64); foreach (var x in hash) b.Append(x.ToString("x2")); return b.ToString();
            }
        }

        public void Delete()
        {
            Dispose();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception e) { Debug.LogWarning("SCALPAL_CAPTURE could not delete local recording " + directory + ": " + e.Message); }
        }

        public void Dispose()
        {
            Accepting = false;
            lock (gate) { closed = true; queue.Clear(); queuedBytes = 0; Monitor.Pulse(gate); }
            worker.Join(2000);
            try { writer?.Dispose(); } catch (ObjectDisposedException) { }
        }
    }
}
