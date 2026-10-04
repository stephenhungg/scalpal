using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Scalpal.Handoff;
using Scalpal.Recap;
using UnityEngine;

namespace Scalpal.Capture
{
    // Records raw passthrough-camera frames of the learner's hands for exactly one
    // surgery segment: RecapRunContext.Begin (practice start after Time-Out) to
    // EndSurgery (case end), or until the attempt is abandoned. Then uploads the
    // clip + manifests through the gateway grant flow, requests the motion-v1 job
    // and hands the job to the recap with AttachMotionJob. Every unavailable or
    // failed path ends as a labeled replay failure; nothing is substituted.
    [DisallowMultipleComponent]
    public sealed class HandCaptureRecorder : MonoBehaviour
    {
        public enum RecorderState { Idle, WaitingForFrames, Recording, Capped, Finalizing, Uploading, Submitted, Unavailable, Failed, Discarded }
        public const double DefaultTargetFps = 15, DefaultMaxSeconds = 300, FirstFrameTimeout = 5;
        public const long DefaultMaxBytes = 400L * 1024 * 1024;
        public const string VrUnavailable = "Capture unavailable in VR: the headset delivered no passthrough camera frames while the virtual OR hides passthrough. Replay skipped.";

        public static HandCaptureRecorder Current { get; private set; }
        // Test seams. Production uses the right Meta passthrough camera and SpacetimeDB.
        public static Func<HandCaptureRecorder, string, ICaptureFrameSource> SourceFactory;
        public static Func<ICaptureTransport> TransportFactory;
        public static Func<string, string> NewId = prefix => prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 24);
        public static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;
        public static Func<HandoffTicket> CurrentTicket = () => HandoffRun.Current;
        public static Func<TheatrePreflight> CurrentPreflight = () => HandoffRun.Preflight;
        public static void ResetSeams()
        {
            SourceFactory = null; TransportFactory = null;
            NewId = prefix => prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 24);
            Clock = () => Time.realtimeSinceStartupAsDouble; CurrentTicket = () => HandoffRun.Current; CurrentPreflight = () => HandoffRun.Preflight;
        }

        [NonSerialized] public string realtimeUri = "", realtimeDatabase = "";
        public double targetFps = DefaultTargetFps, maxSeconds = DefaultMaxSeconds, firstFrameTimeout = FirstFrameTimeout;
        public long maxBytes = DefaultMaxBytes;
        public int queueLimit = CaptureSegment.QueueLimit;
        public string storageRoot = "";
        public RecorderState State { get; private set; } = RecorderState.Idle;
        public string Reason { get; private set; } = "";
        public string AttemptId => segment?.attemptId ?? attemptId;
        public CaptureSegment Segment => segment;
        public CaptureUploadFlow Upload => upload;
        public CaptureProvenanceManifest LastManifest { get; private set; }
        public int FramesSeen { get; private set; }

        RecapRunContext context;
        ICaptureFrameSource source;
        CaptureSegment segment;
        CaptureUploadFlow upload;
        ICaptureTransport transport;
        Task<string> finishing;
        CaptureProvenanceManifest pendingManifest;
        string attemptId = "", mode = "", provenance = "learner";
        bool subjectInFrame, volunteerConsented;
        HandoffTicket ticketRef;
        TheatrePreflight preflightRef;
        double segmentStart, deadline;

        public static HandCaptureRecorder Ensure()
        {
            var run = RecapRunContext.Ensure();
            var recorder = run.GetComponent<HandCaptureRecorder>();
            if (!recorder) recorder = run.gameObject.AddComponent<HandCaptureRecorder>();
            recorder.Bind(run);
            return recorder;
        }
        void Awake() { if (Current && Current != this) { Destroy(this); return; } Current = this; Bind(GetComponent<RecapRunContext>()); }
        public void Bind(RecapRunContext next)
        {
            Current = this;
            if (context == next) return;
            Unbind();
            context = next;
            if (!context) return;
            context.SegmentStarted += SegmentStarted; context.SegmentEnded += SegmentEnded; context.SegmentAbandoned += SegmentAbandoned;
        }
        void Unbind() { if (context) { context.SegmentStarted -= SegmentStarted; context.SegmentEnded -= SegmentEnded; context.SegmentAbandoned -= SegmentAbandoned; } }
        void OnDestroy()
        {
            Unbind();
            if (segment != null && State != RecorderState.Uploading) Discard("The run root was destroyed; recording discarded.");
            CloseTransport();
            if (Current == this) Current = null;
        }
        void OnApplicationQuit() { if (segment != null && (State == RecorderState.Recording || State == RecorderState.WaitingForFrames)) Discard("App closed during the segment; recording discarded."); }

        // ---- consent -------------------------------------------------------
        public static bool ConsentGranted(HandoffTicket ticket, TheatrePreflight preflight, out string reason)
        {
            reason = "";
            if (ticket == null) { reason = "Recording unavailable: this run has no theatre handoff, so no hand-recording consent was recorded. Replay skipped."; return false; }
            if (preflight == null || !preflight.learnerCaptureConsented) { reason = "Recording unavailable: learner hand-recording consent is not recorded in Theatre setup. Replay skipped."; return false; }
            if (ticket.presentationMode == "mixed_reality" && !preflight.volunteerConsented) { reason = "Recording unavailable: the AR volunteer would be in frame without recorded consent. Replay skipped."; return false; }
            return true;
        }
        public static string PlannedNotice(HandoffTicket ticket, TheatrePreflight preflight)
            => ConsentGranted(ticket, preflight, out var reason)
                ? "Recording hands begins with practice and stops at case end: the camera records your hands for the robot replay." + (ticket.presentationMode == "mixed_reality" ? " The volunteer is in frame; their consent covers this clip." : " In VR the camera may be unavailable; that is reported, not faked.")
                : reason;
        public static string StatusLine()
        {
            var r = Current;
            if (!r) return "Replay unavailable: hand capture is not connected.";
            switch (r.State)
            {
                case RecorderState.Recording: return "Recording hands (" + r.FramesSeen + " frames).";
                case RecorderState.WaitingForFrames: return "Recording hands: waiting for camera frames.";
                case RecorderState.Capped: return "Recording stopped at the " + r.maxSeconds.ToString("0") + " s cap; the recorded part will be uploaded.";
                case RecorderState.Finalizing: case RecorderState.Uploading: return "Uploading the hand recording for robot replay.";
                case RecorderState.Submitted: return "Hand recording uploaded; robot replay queued.";
                case RecorderState.Idle: return "Hand capture idle.";
                default: return r.Reason;
            }
        }

        // ---- lifecycle -----------------------------------------------------
        void SegmentStarted(RunResult run)
        {
            try { Open(run, CurrentTicket(), CurrentPreflight(), Clock()); }
            catch (Exception e)
            {
                // Never let capture break practice start.
                Debug.LogError("SCALPAL_CAPTURE start failed: " + e);
                StopSource(); segment?.Delete(); segment = null;
                SetState(RecorderState.Failed, "Recording failed to start (" + e.GetType().Name + "). Replay skipped.");
            }
        }

        public void Open(RunResult run, HandoffTicket ticket, TheatrePreflight preflight, double now)
        {
            if (segment != null) Discard("A new attempt began before this segment ended; recording discarded.");
            CloseTransport(); upload = null; finishing = null; LastManifest = null; FramesSeen = 0;
            attemptId = run.attemptId; mode = ticket?.presentationMode ?? "";
            ticketRef = ticket; preflightRef = preflight; cameraRecord = null; frameSourceName = clockName = "";
            if (!ConsentGranted(ticket, preflight, out var refusal)) { SetState(RecorderState.Unavailable, refusal); return; }
            subjectInFrame = mode == "mixed_reality"; volunteerConsented = preflight.volunteerConsented;
            source = SourceFactory != null ? SourceFactory(this, mode) : new PassthroughCaptureSource(gameObject, targetFps);
            if (!source.Start(out var reason))
            {
                StopSource();
                SetState(RecorderState.Unavailable, mode == "virtual" && reason.StartsWith("Capture unavailable:") ? reason.Replace("Capture unavailable:", "Capture unavailable in VR:") : reason);
                return;
            }
            provenance = source.Provenance;
            string root = string.IsNullOrEmpty(storageRoot) ? Path.Combine(Application.temporaryCachePath, "scalpal-capture") : storageRoot;
            segmentStart = now;
            segment = new CaptureSegment(root, run.sessionId, run.attemptId, run.runId, mode, now, maxSeconds, maxBytes, queueLimit)
            { clipArtifactId = NewId("art"), timingArtifactId = NewId("art"), manifestArtifactId = NewId("art") };
            deadline = now + firstFrameTimeout;
            SetState(RecorderState.WaitingForFrames, "");
            Debug.Log("SCALPAL_CAPTURE segment opened attempt=" + run.attemptId + " mode=" + mode);
        }

        void Update() => Pump(Clock());

        public void Pump(double now)
        {
            if (segment != null && (State == RecorderState.WaitingForFrames || State == RecorderState.Recording))
            {
                // The live ticket/preflight objects: withdrawal or a mode switch ends capture immediately.
                if (!ConsentGranted(ticketRef, preflightRef, out _) || ticketRef.presentationMode != mode)
                { Discard("Consent was withdrawn or the theatre mode changed during the segment; recording discarded."); return; }
                source.Pump(now);
                while (source != null && source.TryDequeue(out var frame))
                    if (segment.Submit(frame)) { FramesSeen++; if (State == RecorderState.WaitingForFrames) SetState(RecorderState.Recording, ""); }
                if (segment.Capped) { StopSource(); SetState(RecorderState.Capped, "Recording reached its " + segment.StopReason.Replace('_', ' ') + "."); }
                else if (State == RecorderState.WaitingForFrames && now > deadline)
                {
                    string reason = mode == "virtual" ? VrUnavailable : "Capture unavailable: no passthrough camera frames arrived within " + firstFrameTimeout.ToString("0") + " s. Replay skipped.";
                    Debug.LogWarning("SCALPAL_CAPTURE " + reason);
                    StopSource(); segment.Delete(); segment = null;
                    SetState(RecorderState.Unavailable, reason);
                }
                else if (segment.Fault.Length > 0)
                { string fault = segment.Fault; StopSource(); segment.Delete(); segment = null; SetState(RecorderState.Failed, "Recording failed: " + fault + ". Replay skipped."); }
            }
            if (State == RecorderState.Finalizing && finishing != null && finishing.IsCompleted) StartUpload();
            if (State == RecorderState.Uploading && upload != null)
            {
                upload.Step(now);
                if (upload.State == CaptureUploadFlow.Outcome.Submitted) Submitted();
                else if (upload.State == CaptureUploadFlow.Outcome.Failed) UploadFailed(upload.Failure);
            }
        }

        void SegmentEnded(RunResult run)
        {
            try { Close(run); }
            catch (Exception e) { Debug.LogError("SCALPAL_CAPTURE end failed: " + e); FailEnded(run.attemptId, "Recording could not be finalized (" + e.GetType().Name + "). Replay skipped."); }
        }

        public void Close(RunResult run)
        {
            if (segment == null || segment.attemptId != run.attemptId)
            {
                string reason = attemptId == run.attemptId && Reason.Length > 0 ? Reason : "No hand recording exists for this attempt. Replay skipped.";
                if (segment != null) Discard("Segment belonged to another attempt; recording discarded.");
                FailEnded(run.attemptId, reason); return;
            }
            if (State == RecorderState.WaitingForFrames || segment.FrameCount == 0)
            {
                StopSource(); segment.Delete(); segment = null;
                FailEnded(run.attemptId, mode == "virtual" ? VrUnavailable : "Capture unavailable: no passthrough camera frames were recorded. Replay skipped.");
                return;
            }
            // Stop accepting before the camera stops: late readbacks are rejected.
            segment.StopAccepting(segment.Capped ? segment.StopReason : "case_end");
            StopSource();
            pendingManifest = new CaptureProvenanceManifest { source = provenance };
            var c = pendingManifest.capture;
            c.subjectInFrame = subjectInFrame;
            c.consent.learnerCaptureConsented = true; c.consent.volunteerConsented = volunteerConsented;
            c.camera = cameraRecord ?? new CaptureCameraRecord();
            c.frameSource = frameSourceName; c.clock.frameTimestamps = clockName;
            var finishingSegment = segment; var manifest = pendingManifest;
            finishing = Task.Run(() => finishingSegment.Finish(manifest));
            SetState(RecorderState.Finalizing, "");
            context?.MarkCaptureUploading(run.attemptId, "Uploading your hand recording; robot replay queues when the gateway verifies it.");
        }

        // Called by tests that run without a frame loop.
        public bool WaitForFinalize(int timeoutMs) { if (finishing == null) return false; bool done = finishing.Wait(timeoutMs); Pump(Clock()); return done; }

        void StartUpload()
        {
            string fault = finishing.Exception != null ? finishing.Exception.GetBaseException().Message : finishing.Result;
            finishing = null;
            if (fault.Length > 0) { UploadFailed("finalizing: " + fault); return; }
            LastManifest = pendingManifest;
            var items = new List<CaptureUploadFlow.Item>
            {
                Item(segment.clipArtifactId, "raw_clip", "hands.avi", CaptureContract.VideoContentType, segment.clipPath, pendingManifest.capture.video.sha256),
                Item(segment.timingArtifactId, "other", "capture-frames.json", CaptureContract.JsonContentType, segment.timingPath, pendingManifest.capture.frames.timingSha256),
                Item(segment.manifestArtifactId, "capture_manifest", "capture-manifest.json", CaptureContract.JsonContentType, segment.manifestPath, CaptureSegment.Sha256(segment.manifestPath)),
            };
            transport = TransportFactory != null ? TransportFactory() : new SpacetimeCaptureTransport();
            upload = new CaptureUploadFlow(transport, realtimeUri, realtimeDatabase, context ? context.clientToken : "", segment.sessionId, segment.attemptId, NewId("job"), items);
            SetState(RecorderState.Uploading, "");
        }
        static CaptureUploadFlow.Item Item(string id, string kind, string name, string type, string path, string sha)
            => new CaptureUploadFlow.Item { artifactId = id, grantId = NewId("grant"), kind = kind, filename = name, contentType = type, path = path, sha256 = sha, bytes = (ulong)new FileInfo(path).Length };

        void Submitted()
        {
            var done = segment; segment = null;
            bool attached = context && context.AttachMotionJob(done.attemptId, upload.JobId, LastManifest.capture.clock.captureStartRunSeconds, false,
                provenance, done.clipArtifactId, "", upload.JobRun, "run");
            done.Delete(); CloseTransport();
            if (attached) { SetState(RecorderState.Submitted, ""); Debug.Log("SCALPAL_CAPTURE motion job " + upload.JobId + " attached to attempt " + done.attemptId); }
            else SetState(RecorderState.Failed, "Motion job " + upload.JobId + " was queued but this attempt is no longer the active run; it was not attached.");
        }

        void UploadFailed(string failure)
        {
            var done = segment; segment = null;
            string reason = "Replay unavailable: hand recording upload failed (" + failure + "). Your diagnosis and surgery results are kept; the local recording was deleted.";
            Debug.LogWarning("SCALPAL_CAPTURE " + reason);
            if (done != null) { FailEnded(done.attemptId, reason); done.Delete(); }
            CloseTransport();
            SetState(RecorderState.Failed, reason);
        }

        void SegmentAbandoned(string attempt, string reason)
        {
            if (segment != null && segment.attemptId == attempt) Discard(reason);
            else if (attemptId == attempt && (State == RecorderState.Unavailable || State == RecorderState.Idle)) SetState(RecorderState.Discarded, reason);
        }

        void Discard(string reason)
        {
            StopSource();
            segment?.Delete(); segment = null; finishing = null; upload = null; CloseTransport();
            SetState(RecorderState.Discarded, reason);
            Debug.Log("SCALPAL_CAPTURE discarded: " + reason);
        }

        void FailEnded(string attempt, string reason)
        {
            if (State != RecorderState.Failed && State != RecorderState.Unavailable) SetState(RecorderState.Failed, reason);
            if (context) context.ReportCaptureFailure(attempt, reason);
        }

        CaptureCameraRecord cameraRecord;
        string frameSourceName = "", clockName = "";
        void StopSource()
        {
            if (source == null) return;
            // Intrinsics/extrinsics are only valid while the camera runs; capture them first.
            try { cameraRecord = source.Describe(); frameSourceName = source.FrameSource; clockName = source.Clock; } catch (Exception e) { Debug.LogWarning("SCALPAL_CAPTURE describe failed: " + e.Message); }
            try { source.Stop(); source.Dispose(); } catch (Exception e) { Debug.LogWarning("SCALPAL_CAPTURE stop failed: " + e.Message); }
            source = null;
        }
        void CloseTransport() { try { transport?.Dispose(); } catch (Exception e) { Debug.LogWarning("SCALPAL_CAPTURE transport close: " + e.Message); } transport = null; }
        void SetState(RecorderState next, string reason) { State = next; Reason = reason ?? ""; }
    }
}
