using System;
using System.Collections;
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
        string voicePrompt, firstMessage;
        [Serializable] sealed class PromptReply { public string prompt, firstMessage, reactionQuestion, selfAssessmentQuestion; }
        [Serializable] public sealed class GatewayReplay
        {
            public string schemaVersion, sessionId, attemptId, jobId, status, reason, sourceVideoUrl, replayVideoUrl, replayKind;
        }
        void Start()
        {
            context = RecapRunContext.Ensure();
            var source = context.result;
            if (source == null && previewResult) source = RunResultContract.Parse(previewResult.text);
            if (source == null) { Notice = "No run result. Finish an OR attempt first."; panel.Refresh(); return; }
            Load(source);
        }
        public void Load(RunResult result)
        {
            StopAllCoroutines(); generation++;
            RunResultContract.Validate(result); Result = result; Phase = "replay";
            if (!context) context = RecapRunContext.Ensure();
            replay.Bind(result); panel.Refresh();
            StartCoroutine(FetchPrompt(generation));
            StartCoroutine(Poll(generation));
        }
        IEnumerator FetchPrompt(int version)
        {
            using (var request = UnityWebRequest.Get(context.gatewayUrl.TrimEnd('/') + "/v1/recap/voice-prompt"))
            {
                request.timeout = 10; yield return request.SendWebRequest();
                if (version != generation) yield break;
                if (request.result != UnityWebRequest.Result.Success) { Notice = "Jarvis voice unavailable; reflection questions remain on screen."; panel.Refresh(); yield break; }
                PromptReply data = null;
                try { data = JsonUtility.FromJson<PromptReply>(request.downloadHandler.text); } catch (ArgumentException) { }
                if (string.IsNullOrWhiteSpace(data?.prompt)) yield break;
                voicePrompt = data.prompt; firstMessage = data.firstMessage;
                ReactionQuestion = data.reactionQuestion; SelfAssessmentQuestion = data.selfAssessmentQuestion;
                panel.Refresh();
            }
        }
        IEnumerator Poll(int version)
        {
            var r = Result;
            while (version == generation)
            {
                if (context.result != null && context.result != r) yield break;
                if (string.IsNullOrWhiteSpace(r.replay.jobId) || string.IsNullOrWhiteSpace(context.clientToken))
                {
                    if (!r.isSample) { Notice = "Waiting for the capture job and client session credential. Sample remains available."; panel.Refresh(); }
                    yield return new WaitForSecondsRealtime(3); continue;
                }
                var url = context.gatewayUrl.TrimEnd('/') + "/v1/sessions/" + Uri.EscapeDataString(r.sessionId) + "/replay/" + Uri.EscapeDataString(r.replay.jobId);
                using (var request = UnityWebRequest.Get(url))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + context.clientToken); request.timeout = 15;
                    yield return request.SendWebRequest();
                    if (version != generation) yield break;
                    if (request.result != UnityWebRequest.Result.Success) Notice = "Gateway unavailable (" + request.responseCode + "). Last confirmed state retained; sample available.";
                    else
                    {
                        GatewayReplay reply = null;
                        try { reply = JsonUtility.FromJson<GatewayReplay>(request.downloadHandler.text); } catch (ArgumentException) { }
                        if (ApplyGateway(r, reply)) { Notice = ""; replay.Bind(r); }
                        else Notice = "Gateway returned an incompatible run or replay state.";
                    }
                    panel.Refresh();
                }
                // Refresh grants even after ready, so expired URLs are recoverable on replay.
                yield return new WaitForSecondsRealtime(r.replay.status == "ready" ? 120 : 3);
            }
        }
        public static bool ApplyGateway(RunResult result, GatewayReplay reply)
        {
            if (reply == null || reply.schemaVersion != "scalpal.replay.v1" || reply.sessionId != result.sessionId || reply.attemptId != result.attemptId || reply.jobId != result.replay.jobId) return false;
            if (reply.status != "queued" && reply.status != "processing" && reply.status != "ready" && reply.status != "failed") return false;
            if (reply.status == "ready" && (reply.replayKind != "kinematic" || string.IsNullOrWhiteSpace(reply.replayVideoUrl))) return false;
            if (reply.status == "failed" && string.IsNullOrWhiteSpace(reply.reason)) return false;
            result.replay.status = reply.status; result.replay.failureReason = reply.reason;
            result.replay.sourceVideoUrl = reply.sourceVideoUrl; result.replay.replayVideoUrl = reply.replayVideoUrl;
            result.replay.source = "learner";
            return true;
        }
        public void Advance()
        {
            if (Result == null) return;
            if (Phase == "replay")
            {
                replay.Pause(); Phase = "reaction";
                if (voice && !string.IsNullOrEmpty(voicePrompt) && !string.IsNullOrEmpty(context.coachSessionId))
                {
                    voice.Disconnect(); voice.ConfigureEndpoint(context.voiceServiceUrl);
                    voice.ConfigureConversation(voicePrompt, firstMessage); voice.MicrophoneMuted = true; voice.Connect(context.coachSessionId);
                }
                else Notice = "Jarvis voice unavailable; use the reflection question on screen.";
            }
            else if (Phase == "reaction") { Phase = "self"; if (voice) voice.Disconnect(); }
            else if (Phase == "self") Phase = "scores";
            panel.Refresh();
        }
        public void SetTalkHeld(bool held) { if (voice) voice.MicrophoneMuted = !held || Phase != "reaction"; }
        public void ToggleDemo()
        {
            if (Result == null) return;
            context.SetDemoMode(!DemoEnabled); Result.demo = context.demo; replay.ApplyWindow(); panel.Refresh();
        }
        public void Navigate(bool retry)
        {
            if (voice) voice.Disconnect(); replay.Pause();
            if (context.RequestNavigation(retry)) return;
            // Standalone scene may navigate only when its owner explicitly includes destination scenes.
            string target = retry ? context.surgeryScene : context.exploreScene;
            if (Application.CanStreamedLevelBeLoaded(target))
            {
                // No stale procedure score or replay may survive a retry, even without a shell callback.
                if (retry) { Notice = "Retry needs the shell to create a fresh attempt. No stale result was reused."; panel.Refresh(); return; }
                context.result = null; SceneManager.LoadSceneAsync(target);
            }
            else { Notice = "Scene handoff not connected: " + target + ". Connect the run-context navigation callback."; panel.Refresh(); }
        }
        void OnDisable() { generation++; StopAllCoroutines(); if (voice) voice.Disconnect(); if (replay) replay.Pause(); }
    }
}
