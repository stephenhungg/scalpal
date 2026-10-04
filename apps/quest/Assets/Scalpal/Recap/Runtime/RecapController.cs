using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Scalpal.Voice;

namespace Scalpal.Recap
{
    public sealed class RecapController : MonoBehaviour
    {
        public TextAsset previewResult;
        public QuestJarvisVoice voice;
        public RecapPanel panel;
        public RecapVideo replay;
        public RunResult Result { get; private set; }
        public string Phase { get; private set; } = "replay";
        public string Notice { get; private set; } = "";
        public string ReactionQuestion { get; private set; } = "How did that feel?";
        public string SelfAssessmentQuestion { get; private set; } = "One thing you would do differently?";
        public bool DemoEnabled => Result?.demo?.enabled == true;
        RecapRunContext context;
        int generation;
        bool resolving;
        readonly HashSet<UnityWebRequest> requests = new HashSet<UnityWebRequest>();
        [Serializable] sealed class PromptReply { public string runId, reactionQuestion, selfAssessmentQuestion, reactionAudioRoute; public bool voiceConfigured; }
        [Serializable] public sealed class GatewayReplay
        {
            public string schemaVersion, sessionId, attemptId, jobId, status, reason, sourceVideoUrl, replayVideoUrl, replayKind, source, sourceArtifactId, replayArtifactId;
            public uint jobRun;
            public long expiresAtUnixMs;
        }
        public static bool SafeEndpoint(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback));
        void OnEnable() { context = RecapRunContext.Ensure(); context.MotionJobAttached += JobAttached; if (replay) replay.AccessRefreshRequested += RefreshAccess; }
        void JobAttached() { if (context.result != null) Load(context.result); }
        void Start()
        {
            context = RecapRunContext.Ensure();
            if (context.result != null) Load(context.result);
            else if (context.SamplePreviewRequested && previewResult) Load(RunResultContract.Parse(previewResult.text));
            else { Result = null; Notice = "No result for this run"; panel.Refresh(); }
        }
        public void SelectSamplePreview()
        {
            if (!context) context = RecapRunContext.Ensure();
            if (context.SelectSamplePreview() && previewResult) Load(RunResultContract.Parse(previewResult.text));
        }
        void CancelRequests()
        {
            generation++; StopAllCoroutines();
            foreach (var request in requests) { try { request.Abort(); request.Dispose(); } catch (ObjectDisposedException) { } }
            requests.Clear(); resolving = false;
        }
        public void Load(RunResult result)
        {
            CancelRequests(); RunResultContract.Validate(result); Result = result; Phase = "replay"; Notice = "";
            if (!context) context = RecapRunContext.Ensure();
            replay.Bind(result); panel.Refresh(); StartCoroutine(Poll(generation));
        }
        IEnumerator SpeakReaction(int version)
        {
            if (!voice || Result.isSample || !SafeEndpoint(context.voiceServiceUrl))
            { Notice = "Jarvis voice unavailable; reflect using the question on screen."; panel.Refresh(); yield break; }
            string route = (Result.runId == context.coachSessionId ? "/coach/sessions/" : "/coach/runs/") + Uri.EscapeDataString(Result.runId) + "/recap";
            PromptReply data = null;
            using (var request = new UnityWebRequest(context.voiceServiceUrl.TrimEnd('/') + route, "POST"))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 10; requests.Add(request);
                yield return request.SendWebRequest(); requests.Remove(request);
                if (version != generation || Phase != "reaction") yield break;
                if (request.result == UnityWebRequest.Result.Success)
                    try { data = JsonUtility.FromJson<PromptReply>(request.downloadHandler.text); } catch (ArgumentException) { }
            }
            if (data == null || data.runId != Result.runId || string.IsNullOrWhiteSpace(data.reactionQuestion) || string.IsNullOrWhiteSpace(data.selfAssessmentQuestion))
            { Notice = "Run-bound Jarvis voice unavailable; use the question on screen."; panel.Refresh(); yield break; }
            ReactionQuestion = data.reactionQuestion; SelfAssessmentQuestion = data.selfAssessmentQuestion; panel.Refresh();
            if (!data.voiceConfigured || data.reactionAudioRoute != route + "/reaction.mp3")
            { Notice = "Jarvis voice unavailable; use the question on screen."; panel.Refresh(); yield break; }
            using (var request = UnityWebRequestMultimedia.GetAudioClip(context.voiceServiceUrl.TrimEnd('/') + data.reactionAudioRoute, AudioType.MPEG))
            {
                request.timeout = 20; requests.Add(request); yield return request.SendWebRequest(); requests.Remove(request);
                if (version != generation || Phase != "reaction") yield break;
                AudioClip clip = request.result == UnityWebRequest.Result.Success ? DownloadHandlerAudioClip.GetContent(request) : null;
                bool played = clip && voice.PlayLocalSpeech(clip);
                if (clip) Destroy(clip);
                Notice = played ? "Reflect silently. Your reaction is unscored." : "Jarvis voice unavailable; use the question on screen."; panel.Refresh();
            }
        }
        public static float PollDelay(int failures) => Mathf.Min(10, 3 + failures * 2);
        IEnumerator Poll(int version)
        {
            int polls = 0;
            while (version == generation && Phase == "replay")
            {
                var r = Result;
                // Resolve an imported ready result once. Never rotate a playing video's URL on a timer.
                if (r.replay.status == "failed" || (r.replay.status == "ready" && !string.IsNullOrEmpty(r.replay.replayVideoUrl))) yield break;
                if (string.IsNullOrEmpty(r.replay.jobId) || string.IsNullOrEmpty(context.clientToken))
                { Notice = r.isSample ? "" : "No capture job or paired session credential. Labeled sample replay available."; panel.Refresh(); yield break; }
                yield return Resolve(version, false);
                if (Result.replay.status == "ready" || Result.replay.status == "failed" || Notice.StartsWith("Replay access denied")) yield break;
                yield return new WaitForSecondsRealtime(PollDelay(polls++));
            }
        }
        void RefreshAccess() { if (Result != null && !resolving && Phase == "replay") StartCoroutine(Resolve(generation, true)); }
        IEnumerator Resolve(int version, bool refresh)
        {
            if (resolving) yield break;
            if (!SafeEndpoint(context.gatewayUrl) || string.IsNullOrEmpty(context.clientToken))
            { Notice = "Replay needs an HTTPS gateway and paired session credential."; if (refresh) replay.RefreshAccess(null); panel.Refresh(); yield break; }
            resolving = true;
            var r = Result;
            var url = context.gatewayUrl.TrimEnd('/') + "/v1/sessions/" + Uri.EscapeDataString(r.sessionId) + "/replay/" + Uri.EscapeDataString(r.replay.jobId);
            using (var request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Authorization", "Bearer " + context.clientToken); request.timeout = 15; requests.Add(request);
                yield return request.SendWebRequest(); requests.Remove(request);
                if (version != generation) yield break;
                bool accepted = false;
                if (request.responseCode == 401 || request.responseCode == 403) Notice = "Replay access denied. Pair this session again.";
                else if (request.result != UnityWebRequest.Result.Success) Notice = "Gateway unavailable (" + request.responseCode + "). Last confirmed state retained.";
                else
                {
                    GatewayReplay reply = null;
                    try { reply = JsonUtility.FromJson<GatewayReplay>(request.downloadHandler.text); } catch (ArgumentException) { }
                    accepted = ApplyGateway(r, reply);
                    Notice = accepted ? "" : "Gateway returned an incompatible run or replay state.";
                }
                if (refresh) replay.RefreshAccess(accepted ? r : null); else if (accepted) replay.Bind(r);
                panel.Refresh();
            }
            resolving = false;
        }
        public static bool ApplyGateway(RunResult result, GatewayReplay reply)
        {
            if (reply == null || reply.schemaVersion != "scalpal.replay.v1" || reply.sessionId != result.sessionId || reply.attemptId != result.attemptId || reply.jobId != result.replay.jobId) return false;
            if (result.replay.jobRun != 0 && result.replay.jobRun != reply.jobRun) return false;
            if (result.replay.status == "ready" && !string.IsNullOrEmpty(result.replay.replayArtifactId) && reply.status == "ready" && result.replay.replayArtifactId != reply.replayArtifactId) return false;
            if (reply.status != "queued" && reply.status != "processing" && reply.status != "ready" && reply.status != "failed") return false;
            if (reply.source != "learner" && reply.source != "rehearsal" && reply.source != "sample" && reply.source != "unknown") return false;
            if (result.isSample && reply.source == "learner") return false;
            if (reply.status == "ready" && (reply.source == "unknown" || reply.replayKind != "kinematic" || string.IsNullOrWhiteSpace(reply.replayArtifactId) || !SafeEndpoint(reply.replayVideoUrl))) return false;
            if (reply.status == "failed" && string.IsNullOrWhiteSpace(reply.reason)) return false;
            result.replay.status = reply.status; result.replay.failureReason = reply.reason;
            result.replay.sourceVideoUrl = reply.sourceVideoUrl; result.replay.replayVideoUrl = reply.replayVideoUrl;
            result.replay.source = reply.source; result.replay.sourceArtifactId = reply.sourceArtifactId; result.replay.replayArtifactId = reply.replayArtifactId;
            result.replay.jobRun = reply.jobRun; result.replay.expiresAtUnixMs = reply.expiresAtUnixMs;
            return true;
        }
        public void Advance()
        {
            if (Result == null) { SelectSamplePreview(); return; }
            if (Phase == "replay") { replay.Pause(); Phase = "reaction"; StartCoroutine(SpeakReaction(generation)); }
            else if (Phase == "reaction") { Phase = "self"; if (voice) voice.Disconnect(); }
            else if (Phase == "self") Phase = "scores";
            panel.Refresh();
        }
        public void SetTalkHeld(bool held) { if (voice) voice.MicrophoneMuted = true; }
        public void ToggleDemo() { if (Result == null) { SelectSamplePreview(); return; } replay.SetHighlight(!replay.HighlightEnabled); panel.Refresh(); }
        public void Navigate(bool retry)
        {
            if (voice) voice.Disconnect(); replay.Pause();
            if (context.RequestNavigation(retry))
            {
                var integration = context.GetComponent<RecapSessionIntegration>();
                if (integration && !string.IsNullOrEmpty(integration.LastNavigationError)) { Notice = integration.LastNavigationError; panel.Refresh(); }
                return;
            }
            // Standalone scene may navigate only when its owner explicitly includes destination scenes.
            string target = retry ? context.surgeryScene : context.exploreScene;
            if (Application.CanStreamedLevelBeLoaded(target))
            {
                // No stale procedure score or replay may survive a retry, even without a shell callback.
                if (retry) { Notice = "Retry needs the shell to create a fresh attempt. No stale result was reused."; panel.Refresh(); return; }
                context.result = null;
                var transition = Scalpal.Shell.ShellTransition.Ensure();
                transition.StartCoroutine(transition.Load(target, "Choose another patient", () =>
                {
                    var hub = FindFirstObjectByType<Scalpal.Shell.HubController>();
                    if (hub) hub.Enter();
                }));
            }
            else { Notice = "Scene handoff not connected: " + target + ". Connect the run-context navigation callback."; panel.Refresh(); }
        }
        void OnDisable() { if (context) context.MotionJobAttached -= JobAttached; if (replay) replay.AccessRefreshRequested -= RefreshAccess; CancelRequests(); if (voice) voice.Disconnect(); if (replay) replay.Pause(); }
    }
}
