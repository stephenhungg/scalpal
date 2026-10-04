using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Recap
{
    // The run ending: two scores and the simulated robot's replay of the demo step. The robot result comes from
    // the coach (GET /coach/sessions/:id/robot-result); it is polled while pending and times out quietly.
    public sealed class RecapController : MonoBehaviour
    {
        public enum RobotState { Pending, Ready, Unavailable }
        [Serializable] public sealed class RobotDemos { public int human, synthetic; }
        [Serializable] public sealed class RobotResult
        {
            public string status, stepId, stepTitle, videoUrl;
            public bool success, synthetic;
            public float pathErrorMm;
            public RobotDemos demos;
        }

        public RecapPanel panel;
        public RecapVideo replay;
        public float robotPollSeconds = 3, robotTimeoutSeconds = 150;
        public RunResult Result { get; private set; }
        public RobotState Robot { get; private set; } = RobotState.Pending;
        public RobotResult RobotReply { get; private set; }
        public bool RobotPathErrorKnown { get; private set; }
        RecapRunContext context;
        int generation;
        UnityWebRequest pending;

        void Start()
        {
            context = RecapRunContext.Ensure();
            Load(context.result);
        }
        public void Load(RunResult result)
        {
            CancelRequests();
            if (result != null) RunResultContract.Validate(result);
            Result = result; Robot = RobotState.Pending; RobotReply = null; RobotPathErrorKnown = false;
            replay.Stop();
            if (!context) context = RecapRunContext.Ensure();
            if (result == null || string.IsNullOrEmpty(context.coachSessionId) || !SafeEndpoint(CoachUrl)) Robot = RobotState.Unavailable;
            else StartCoroutine(PollRobot(generation));
            panel.Refresh();
        }
        string CoachUrl => context && !string.IsNullOrEmpty(context.voiceServiceUrl) ? context.voiceServiceUrl.TrimEnd('/') : "http://localhost:8787";
        public static bool SafeEndpoint(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback));
        void CancelRequests()
        {
            generation++; StopAllCoroutines();
            if (pending != null) { try { pending.Abort(); pending.Dispose(); } catch (ObjectDisposedException) { } pending = null; }
        }
        IEnumerator PollRobot(int version)
        {
            float deadline = Time.realtimeSinceStartup + robotTimeoutSeconds;
            string url = CoachUrl + "/coach/sessions/" + Uri.EscapeDataString(context.coachSessionId) + "/robot-result";
            while (version == generation && Robot == RobotState.Pending)
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = 8; pending = request;
                    yield return request.SendWebRequest();
                    pending = null;
                    if (version != generation) yield break;
                    // A failed poll (coach restarting, USB reconnect) just retries until the deadline.
                    if (request.result == UnityWebRequest.Result.Success) ApplyRobot(request.downloadHandler.text);
                    else Debug.LogWarning("[Scalpal.Recap] robot result poll failed (" + request.responseCode + "): " + request.error);
                }
                if (Robot != RobotState.Pending) yield break;
                if (Time.realtimeSinceStartup >= deadline) { RobotTimedOut(); yield break; }
                yield return new WaitForSecondsRealtime(robotPollSeconds);
            }
        }
        // Applies one robot-result reply. Anything malformed or unknown is unavailable, never guessed.
        public void ApplyRobot(string json)
        {
            RobotResult reply = null;
            try { reply = JsonUtility.FromJson<RobotResult>(json); } catch (ArgumentException) { }
            if (reply?.status == "pending") { Robot = RobotState.Pending; panel.Refresh(); return; }
            if (reply?.status == "ready" && !string.IsNullOrWhiteSpace(reply.stepId))
            {
                RobotReply = reply; Robot = RobotState.Ready;
                // JsonUtility reads null as 0; only a measured error is shown.
                RobotPathErrorKnown = !json.Replace(" ", "").Contains("\"pathErrorMm\":null") && json.Contains("\"pathErrorMm\"");
                if (VideoPathValid(reply.videoUrl)) replay.Play(CoachUrl + reply.videoUrl);
                else replay.Stop();
            }
            else { Robot = RobotState.Unavailable; replay.Stop(); }
            panel.Refresh();
        }
        // Coach-relative replay paths only (/robot/replays/<file>.mp4); never an absolute or traversing URL.
        public static bool VideoPathValid(string path) => !string.IsNullOrEmpty(path) && path.StartsWith("/robot/replays/", StringComparison.Ordinal)
            && path.EndsWith(".mp4", StringComparison.Ordinal) && !path.Contains("..") && !path.Contains("://");
        public void RobotTimedOut()
        {
            if (Robot != RobotState.Pending) return;
            CancelRequests(); Robot = RobotState.Unavailable; replay.Stop(); panel.Refresh();
        }
        // Video failed to play: the result line stays only if it is still meaningful without its replay.
        public void ReplayFailed() { if (Robot == RobotState.Ready) { Robot = RobotState.Unavailable; panel.Refresh(); } }
        public void Navigate(bool retry)
        {
            replay.Stop();
            if (context.RequestNavigation(retry))
            {
                var integration = context.GetComponent<RecapSessionIntegration>();
                if (integration && !string.IsNullOrEmpty(integration.LastNavigationError)) Debug.LogWarning("[Scalpal.Recap] " + integration.LastNavigationError);
                return;
            }
            // Standalone scene may navigate only when its owner explicitly includes destination scenes.
            string target = retry ? context.surgeryScene : context.exploreScene;
            if (retry || !Application.CanStreamedLevelBeLoaded(target))
            { Debug.LogWarning("[Scalpal.Recap] Scene handoff not connected: " + target); return; }
            context.result = null;
            var transition = Scalpal.Shell.ShellTransition.Ensure();
            transition.StartCoroutine(transition.Load(target, "Choose another patient", () =>
            {
                var hub = FindFirstObjectByType<Scalpal.Shell.HubController>();
                if (hub) hub.Enter();
            }));
        }
        void OnDisable() { CancelRequests(); if (replay) replay.Stop(); }
    }
}
