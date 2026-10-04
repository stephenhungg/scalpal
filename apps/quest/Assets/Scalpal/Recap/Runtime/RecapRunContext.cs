using System;
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
        public event Action ChooseAnotherPatient, RetrySurgery;
        public bool SegmentClosed { get; private set; }
        void Awake()
        {
            if (Current && Current != this) { Destroy(gameObject); return; }
            Current = this; if (Application.isPlaying) DontDestroyOnLoad(gameObject);
        }
        void OnDestroy() { if (Current == this) Current = null; }
        public static RecapRunContext Ensure() => Current ? Current : new GameObject("RecapRunContext").AddComponent<RecapRunContext>();
        public void Begin(RunResult next)
        {
            RunResultContract.Validate(next); result = next; result.demo = demo; SegmentClosed = false;
        }
        public void SetDemoMode(bool enabled) { demo = DemoFlags.JudgePath(enabled); if (result != null) result.demo = demo; }
        // OR calls once. Capture adapter subscribes to stop/finalize/upload and requestMotionJob(motion-v1).
        // This does not fabricate capture or enqueue an artifact before upload verification.
        public bool EndSurgery(string attemptId, SurgeryGrade grade)
        {
            if (result == null || result.attemptId != attemptId || SegmentClosed) return false;
            var previous = result.surgery; result.surgery = grade ?? new SurgeryGrade();
            try { RunResultContract.Validate(result); } catch { result.surgery = previous; throw; }
            SegmentClosed = true;
            if (SegmentEnded == null) { result.replay.status = "failed"; result.replay.failureReason = "Capture adapter is not connected; showing the sample replay."; }
            else SegmentEnded.Invoke(result);
            return true;
        }
        public bool AttachMotionJob(string attemptId, string jobId, double captureStartRunSeconds, bool clockAligned)
        {
            if (result == null || result.attemptId != attemptId || !SegmentClosed || string.IsNullOrWhiteSpace(jobId) || double.IsNaN(captureStartRunSeconds) || double.IsInfinity(captureStartRunSeconds) || captureStartRunSeconds < 0) return false;
            result.replay = new ReplayResult { jobId = jobId, status = "queued", failureReason = "", captureStartRunSeconds = captureStartRunSeconds, clockAligned = clockAligned };
            return true;
        }
        public string ExportResultJson() { RunResultContract.Validate(result); return JsonUtility.ToJson(result, true); }
        public bool RequestNavigation(bool retry)
        {
            var callback = retry ? RetrySurgery : ChooseAnotherPatient;
            if (callback == null) return false;
            callback.Invoke(); return true;
        }
    }
}
