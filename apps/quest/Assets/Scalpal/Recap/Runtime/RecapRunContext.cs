using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Recap
{
    // Add to the shell's persistent run root, or use Ensure(). This is a handoff, not a grader.
    public sealed class RecapRunContext : MonoBehaviour
    {
        public static RecapRunContext Current { get; private set; }
        public RunResult result;
        public DemoFlags demo = new DemoFlags();
        public string gatewayUrl = "http://127.0.0.1:8788", voiceServiceUrl = "http://127.0.0.1:8787";
        public string coachSessionId, exploreScene = "Launch", surgeryScene = "NativeSession";
        [NonSerialized] public string clientToken;
        public event Action<RunResult> SegmentEnded;
        public event Action MotionJobAttached;
        public event Action ChooseAnotherPatient, RetrySurgery;
        readonly HashSet<string> usedAttempts = new HashSet<string>();
        DemoFlags frozenDemo;
        public bool SamplePreviewRequested { get; private set; }
        public bool SegmentClosed { get; private set; }
        void Awake()
        {
            if (Current && Current != this) { Destroy(this); return; }
            Current = this; if (Application.isPlaying) DontDestroyOnLoad(gameObject);
        }
        void OnDestroy() { if (Current == this) Current = null; }
        public static RecapRunContext Ensure() => Current ? Current : new GameObject("RecapRunContext").AddComponent<RecapRunContext>();
        public void Begin(RunResult next)
        {
            RunResultContract.Validate(next);
            if (usedAttempts.Contains(next.attemptId)) throw new ArgumentException("Retry requires a new, confirmed attempt ID.");
            result = RunResultContract.Parse(JsonUtility.ToJson(next));
            frozenDemo = JsonUtility.FromJson<DemoFlags>(JsonUtility.ToJson(next.demo));
            result.demo = JsonUtility.FromJson<DemoFlags>(JsonUtility.ToJson(frozenDemo));
            if (result.surgery == null) result.surgery = new SurgeryGrade();
            result.surgery.demoAssisted |= frozenDemo.enabled;
            usedAttempts.Add(next.attemptId); SegmentClosed = false; SamplePreviewRequested = false;
        }
        public void SetDemoMode(bool enabled) { if (result == null) demo = DemoFlags.JudgePath(enabled); }
        public bool SelectSamplePreview() { if (result != null) return false; demo = DemoFlags.JudgePath(true); SamplePreviewRequested = true; return true; }
        // OR calls once. Capture adapter subscribes to stop/finalize/upload and requestMotionJob(motion-v1).
        // This does not fabricate capture or enqueue an artifact before upload verification.
        public bool EndSurgery(string attemptId, SurgeryGrade grade)
        {
            if (result == null || result.attemptId != attemptId || SegmentClosed) return false;
            var previous = result.surgery; result.surgery = grade == null ? new SurgeryGrade() : JsonUtility.FromJson<SurgeryGrade>(JsonUtility.ToJson(grade));
            result.surgery.demoAssisted |= frozenDemo?.enabled == true;
            try { RunResultContract.Validate(result); } catch { result.surgery = previous; throw; }
            SegmentClosed = true;
            if (SegmentEnded == null) { result.replay.status = "failed"; result.replay.failureReason = "Capture adapter is not connected; showing the sample replay."; }
            else SegmentEnded.Invoke(result);
            return true;
        }
        public bool AttachMotionJob(string attemptId, string jobId, double captureStartRunSeconds, bool clockAligned, string source = "unknown", string sourceArtifactId = "", string replayArtifactId = "", uint jobRun = 0, string eventClock = "run")
        {
            if (result == null || result.attemptId != attemptId || !SegmentClosed || string.IsNullOrWhiteSpace(jobId) || double.IsNaN(captureStartRunSeconds) || double.IsInfinity(captureStartRunSeconds) || captureStartRunSeconds < 0) return false;
            if (source != "learner" && source != "rehearsal" && source != "sample" && source != "unknown") return false;
            result.replay = new ReplayResult { eventClock = eventClock, source = source, sourceArtifactId = sourceArtifactId, replayArtifactId = replayArtifactId, jobRun = jobRun, jobId = jobId, status = "queued", failureReason = "", captureStartRunSeconds = captureStartRunSeconds, clockAligned = clockAligned };
            MotionJobAttached?.Invoke();
            return true;
        }
        public string ExportResultJson()
        {
            if (result != null && frozenDemo != null) result.demo = JsonUtility.FromJson<DemoFlags>(JsonUtility.ToJson(frozenDemo));
            RunResultContract.Validate(result); return JsonUtility.ToJson(result, true);
        }
        public bool RequestNavigation(bool retry)
        {
            var callback = retry ? RetrySurgery : ChooseAnotherPatient;
            if (callback == null) return false;
            callback.Invoke(); return true;
        }
    }
}
