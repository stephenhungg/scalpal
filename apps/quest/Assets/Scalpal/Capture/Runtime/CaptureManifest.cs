using System;
using System.Text;
using UnityEngine;

namespace Scalpal.Capture
{
    // capture_manifest artifact. The first five fields are the gateway's provenance
    // contract (services/api src/recap.ts replaySource, <= 16 KiB). `capture` adds
    // the raw-camera metadata. Per-frame timing/poses are a separate artifact because
    // a long segment would exceed the gateway's manifest bound.
    [Serializable] public sealed class CaptureProvenanceManifest
    {
        public string schemaVersion = CaptureContract.ProvenanceSchema;
        public string sessionId = "", attemptId = "", inputArtifactId = "", source = "learner";
        public QuestCaptureInfo capture = new QuestCaptureInfo();
    }
    [Serializable] public sealed class QuestCaptureInfo
    {
        public string schema = CaptureContract.CaptureSchema;
        public string runId = "", presentationMode = "", frameSource = "";
        public bool rawCameraFrames = true, compositedFrames, subjectInFrame;
        public CaptureConsentRecord consent = new CaptureConsentRecord();
        public CaptureCameraRecord camera = new CaptureCameraRecord();
        public CaptureVideoRecord video = new CaptureVideoRecord();
        public CaptureClockRecord clock = new CaptureClockRecord();
        public CaptureFrameSummary frames = new CaptureFrameSummary();
        public CaptureSegmentRecord segment = new CaptureSegmentRecord();
    }
    [Serializable] public sealed class CaptureConsentRecord
    {
        public bool learnerCaptureConsented, volunteerConsented;
        public string recordedBy = "operator_theatre_setup", scope = "learner hands for the robot replay of this attempt";
    }
    [Serializable] public sealed class CaptureCameraRecord
    {
        public string api = "", position = "";
        public int width, height, sensorWidth, sensorHeight;
        public float fx, fy, cx, cy;
        public string intrinsicsUnits = "PassthroughCameraAccess.Intrinsics: pixels of sensorWidth x sensorHeight; MRUK maps them to the recorded width x height (see its ViewportPointToRay).";
        public float[] lensOffsetPosition = new float[3], lensOffsetRotation = { 0, 0, 0, 1 };
        public string extrinsics = "lensOffset: camera pose relative to the headset center; cameraPose in the timing artifact: per-frame world pose at the sensor timestamp. Unity convention: meters, left-handed, +y up, rotation xyzw.";
    }
    [Serializable] public sealed class CaptureVideoRecord
    {
        public string container = "avi", codec = "mjpeg", contentType = CaptureContract.VideoContentType, sha256 = "";
        public int width, height, frameCount, jpegQuality;
        public long bytes;
        public double nominalFps;
        public string timing = "Container frame rate is the measured average only; use the timing artifact for per-frame device timestamps.";
    }
    [Serializable] public sealed class CaptureClockRecord
    {
        public string frameTimestamps = "", receipt = "unity_realtime_since_startup_s", runClock = "practice_start";
        public long firstFrameSensorUs, lastFrameSensorUs;
        public double segmentStartRealtimeSeconds, firstFrameRealtimeSeconds, captureStartRunSeconds;
        public bool clockAligned;
        public string alignment = "Not aligned to the surgery active_interaction clock; pauses change that mapping.";
    }
    [Serializable] public sealed class CaptureFrameSummary
    {
        public string timingArtifactId = "", timingSha256 = "";
        public bool monotonic;
        public int droppedNonMonotonic, droppedQueueFull, droppedInvalid, poseValidFrames;
    }
    [Serializable] public sealed class CaptureSegmentRecord
    {
        public string startedBy = "practice_started", stoppedBy = "";
        public bool truncated;
        public double durationSeconds, maxDurationSeconds;
    }

    // Separate `other` artifact: one entry per AVI frame, in container order.
    [Serializable] public sealed class CaptureTiming
    {
        public string schemaVersion = CaptureContract.TimingSchema;
        public string sessionId = "", attemptId = "", inputArtifactId = "", clock = "", receipt = "unity_realtime_since_startup_s";
        public long[] sensorTimeUs = Array.Empty<long>();
        public double[] receivedRealtimeSeconds = Array.Empty<double>();
        public float[] cameraPose = Array.Empty<float>(); // 7 per frame: px py pz qx qy qz qw
        public bool[] poseValid = Array.Empty<bool>();
    }

    public static class CaptureContract
    {
        public const string ProvenanceSchema = "scalpal.capture-provenance.v1";
        public const string CaptureSchema = "scalpal.quest-capture.v1";
        public const string TimingSchema = "scalpal.capture-frames.v1";
        public const string VideoContentType = "video/x-msvideo";
        public const string JsonContentType = "application/json";
        public const string ConfigVersion = "motion-v1"; // the only worker configuration services/motion accepts
        public const int MaxManifestBytes = 16384;       // gateway readSmall bound

        public static string ToJson(CaptureProvenanceManifest manifest) => JsonUtility.ToJson(manifest);

        public static bool Validate(CaptureProvenanceManifest m, CaptureTiming t, out string reason)
        {
            reason = Check(m, t);
            return reason.Length == 0;
        }

        static string Check(CaptureProvenanceManifest m, CaptureTiming t)
        {
            if (m == null || t == null || m.capture == null) return "manifest or timing missing";
            if (m.schemaVersion != ProvenanceSchema || m.capture.schema != CaptureSchema || t.schemaVersion != TimingSchema) return "schema version mismatch";
            if (Blank(m.sessionId) || Blank(m.attemptId) || Blank(m.inputArtifactId) || Blank(m.capture.runId)) return "identity fields are required";
            if (m.source != "learner" && m.source != "rehearsal" && m.source != "sample") return "unknown source";
            if (t.sessionId != m.sessionId || t.attemptId != m.attemptId || t.inputArtifactId != m.inputArtifactId) return "timing belongs to another attempt or clip";
            var c = m.capture;
            if (!c.rawCameraFrames || c.compositedFrames) return "only raw camera frames are motion input";
            if (c.presentationMode != "mixed_reality" && c.presentationMode != "virtual") return "presentation mode is required";
            if (!c.consent.learnerCaptureConsented) return "learner capture consent is required";
            if (c.subjectInFrame && !c.consent.volunteerConsented) return "a volunteer in frame requires volunteer consent";
            if (Blank(c.frames.timingArtifactId) || c.frames.timingArtifactId == m.inputArtifactId) return "timing artifact id is required";
            int n = c.video.frameCount;
            if (n <= 0 || t.sensorTimeUs.Length != n || t.receivedRealtimeSeconds.Length != n || t.poseValid.Length != n || t.cameraPose.Length != 7 * n) return "timing arrays do not match the frame count";
            for (int i = 1; i < n; i++)
            {
                if (t.sensorTimeUs[i] <= t.sensorTimeUs[i - 1]) return "sensor timestamps are not strictly increasing at frame " + i;
                if (t.receivedRealtimeSeconds[i] < t.receivedRealtimeSeconds[i - 1]) return "receipt times go backwards at frame " + i;
            }
            if (!c.frames.monotonic || c.clock.firstFrameSensorUs != t.sensorTimeUs[0] || c.clock.lastFrameSensorUs != t.sensorTimeUs[n - 1]) return "clock summary differs from timing";
            if (Blank(c.clock.frameTimestamps) || c.clock.frameTimestamps != t.clock) return "frame clock is unnamed";
            if (c.clock.clockAligned) return "capture clock alignment to surgery events is not verified";
            if (!Finite(c.clock.captureStartRunSeconds) || c.clock.captureStartRunSeconds < 0 || c.clock.firstFrameRealtimeSeconds < c.clock.segmentStartRealtimeSeconds) return "capture starts before the segment";
            if (t.receivedRealtimeSeconds[0] < c.clock.segmentStartRealtimeSeconds) return "frame received before the segment started";
            if (Math.Abs((t.sensorTimeUs[n - 1] - t.sensorTimeUs[0]) / 1e6 - c.segment.durationSeconds) > 1e-3) return "duration differs from frame timestamps";
            if (c.segment.durationSeconds > c.segment.maxDurationSeconds + 1e-3) return "segment exceeds the duration cap";
            if (Blank(c.segment.stoppedBy)) return "stop reason is required";
            if (!Finite(c.video.nominalFps) || c.video.nominalFps <= 0 || c.video.width <= 0 || c.video.height <= 0 || c.video.bytes <= 0 || !Hex(c.video.sha256) || !Hex(c.frames.timingSha256)) return "video summary is incomplete";
            if (c.video.contentType != VideoContentType) return "unexpected video content type";
            if (c.camera.width != c.video.width || c.camera.height != c.video.height) return "camera and video size differ";
            int poses = 0; foreach (var valid in t.poseValid) if (valid) poses++;
            if (poses != c.frames.poseValidFrames) return "pose count differs from timing";
            if (Encoding.UTF8.GetByteCount(ToJson(m)) > MaxManifestBytes) return "manifest exceeds the gateway's 16 KiB limit";
            return "";
        }
        static bool Blank(string s) => string.IsNullOrWhiteSpace(s);
        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        static bool Hex(string s) { if (s == null || s.Length != 64) return false; foreach (char ch in s) if (!(ch >= '0' && ch <= '9' || ch >= 'a' && ch <= 'f')) return false; return true; }
    }
}
