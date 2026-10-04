using System;
using System.Collections;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Exercises.Coach;
using Scalpal.Voice;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Surgery
{
    // Delivers the existing service's authored coaching. Owns neither steps nor another voice agent.
    public sealed class OpenSurgeryCoach : MonoBehaviour
    {
        CoachRelay relay;
        AnatomyExerciseBinding exercise;
        QuestJarvisVoice voice;
        TextMesh caption;
        AudioSource speaker;
        AudioClip activeClip;
        GameObject ownedCaption;
        readonly Queue<CoachAlertDto> queued = new Queue<CoachAlertDto>();
        Coroutine playback, pulse;
        AnatomyPart pulsedPart;
        int generation;
        float captionUntil;
        bool warningPlaying, safetyPlaying;
        public string LastCaption { get; private set; } = "";

        public void Initialize(CoachRelay source, AnatomyExerciseBinding binding, QuestJarvisVoice jarvis = null, TextMesh text = null)
        {
            Unsubscribe(); ResetDelivery();
            if (ownedCaption) Release(ownedCaption);
            ownedCaption = null;
            relay = source; exercise = binding; voice = jarvis; caption = text;
            if (!speaker) speaker = gameObject.AddComponent<AudioSource>();
            speaker.playOnAwake = false; speaker.spatialBlend = 0; speaker.loop = false;
            if (relay)
            {
                relay.AlertReceived += Receive;
                relay.SessionReset += ResetDelivery;
                relay.SyncFailed += Failed;
            }
        }

        // Old-step guidance cannot reappear after progress, retry, or registration loss.
        // activeBleed reads the latest local body state; null when the case has no body.
        public static bool Relevant(CoachAlertDto alert, string currentStep, bool registrationValid, bool completed, Func<string, bool> activeBleed = null)
        {
            if (alert == null || string.IsNullOrEmpty(alert.say)) return false;
            if (alert.kind == "tracking_lost") return !registrationValid;
            if (alert.kind == "tracking_restored") return registrationValid;
            if (alert.kind == "case_complete") return completed;
            if (!registrationValid || completed) return false;
            // Mirrors services/preop/src/jarvis/arbiter.js: a bleeding alert is stale once the latest state
            // no longer lists that structure as an active bleed.
            if (alert.kind == "bleeding")
                return activeBleed == null || (alert.highlight != null && alert.highlight.Length > 0 && activeBleed(alert.highlight[0]));
            // Body consequences are independent of the next suggested milestone.
            if (alert.kind == "mistake" || alert.kind == "bleeding_controlled") return true;
            return string.IsNullOrEmpty(alert.stepId) || alert.stepId == currentStep;
        }

        bool Relevant(CoachAlertDto alert) => relay && relay.IsSynchronized && exercise &&
            Relevant(alert, exercise.Current?.id ?? "", exercise.anatomy && exercise.anatomy.RegistrationValid, exercise.Completed,
                exercise.Body == null ? (Func<string, bool>)null : id => exercise.Body.Get(id, "bleeding") > 0);

        // Only a safety warning may cut off Jarvis mid-turn; other warnings wait for the turn to end.
        public static bool InterruptsAgent(CoachAlertDto alert) => alert != null && alert.tier == "warning" &&
            (alert.kind == "mistake" || alert.kind == "bleeding" || alert.kind == "tracking_lost");

        void Receive(CoachAlertDto alert)
        {
            if (!isActiveAndEnabled || !Relevant(alert)) return;
            if (InterruptsAgent(alert))
            {
                StopPlayback(); queued.Clear();
                if (voice && voice.CoachSessionId == relay.SessionId) voice.InterruptPlayback();
                Show(alert);
                playback = StartCoroutine(Play(alert, generation));
                return;
            }
            if (alert.tier == "advisory") { if (!warningPlaying) Show(alert); return; }
            if (queued.Count < 8) queued.Enqueue(alert);
        }

        void Update()
        {
            if (playback != null && safetyPlaying && voice && relay && voice.CoachSessionId == relay.SessionId) voice.InterruptPlayback();
            if (caption && Time.unscaledTime >= captionUntil) caption.text = "";
            if (playback != null || !relay || !relay.IsSynchronized) return;
            // A conversational turn finishes before a caution; urgent clips interrupt above.
            if (voice && voice.CoachSessionId == relay.SessionId && voice.Mode == "speaking") return;
            while (queued.Count > 0)
            {
                var alert = queued.Dequeue();
                if (!Relevant(alert)) continue;
                Show(alert);
                playback = StartCoroutine(Play(alert, generation));
                break;
            }
        }

        void Show(CoachAlertDto alert)
        {
            LastCaption = alert.say;
            StopPulse();
            if (alert.kind == "stuck" && alert.highlight != null && alert.highlight.Length > 0)
                pulse = StartCoroutine(PulseTarget(alert, generation));
            if (!caption && Camera.main)
            {
                ownedCaption = new GameObject("Open surgery coach caption");
                ownedCaption.transform.SetParent(Camera.main.transform, false);
                ownedCaption.transform.localPosition = new Vector3(0, -.22f, .8f);
                caption = ownedCaption.AddComponent<TextMesh>();
                caption.anchor = TextAnchor.MiddleCenter; caption.alignment = TextAlignment.Center;
                caption.characterSize = .0035f; caption.fontSize = 48;
            }
            if (caption)
            {
                caption.color = alert.tier == "warning" ? new Color(1f, .5f, .35f) : Color.white;
                caption.text = Wrap(alert.say, 62);
            }
            captionUntil = Time.unscaledTime + Mathf.Clamp(alert.say.Length / 12f, 5, 14);
        }

        IEnumerator Play(CoachAlertDto alert, int epoch)
        {
            // Yield once so even a caption-only response clears the caller's coroutine handle.
            yield return null;
            warningPlaying = alert.tier == "warning"; safetyPlaying = InterruptsAgent(alert);
            if (relay && relay.TryReflexUrl(alert.reflexRoute, out string url))
            {
                for (int attempt = 0; attempt < 3 && epoch == generation && Relevant(alert); attempt++)
                using (var request = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG))
                {
                    request.timeout = 8;
                    yield return request.SendWebRequest();
                    if (epoch != generation) yield break;
                    if (!Relevant(alert)) { FinishPlayback(); yield break; }
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        activeClip = DownloadHandlerAudioClip.GetContent(request);
                        if (activeClip)
                        {
                            speaker.clip = activeClip; speaker.Play();
                            while (speaker.isPlaying && epoch == generation && Relevant(alert)) yield return null;
                            break;
                        }
                    }
                    if (attempt < 2) yield return new WaitForSecondsRealtime(.25f * (attempt + 1));
                    // Failed/unconfigured audio keeps the caption; never claims speech was heard.
                }
            }
            else if (!warningPlaying && voice && voice.Connected && relay && voice.CoachSessionId == relay.SessionId
                && !string.IsNullOrEmpty(alert.simEvent)) voice.SendUserMessage(alert.simEvent);
            if (epoch == generation) FinishPlayback();
        }

        IEnumerator PulseTarget(CoachAlertDto alert, int epoch)
        {
            yield return null;
            if (!exercise || !exercise.anatomy) yield break;
            foreach (string id in alert.highlight)
            {
                if (!exercise.anatomy.TryGetPart(id, out var part) || !part.IsVisible || part.IsHighlighted) continue;
                pulsedPart = part;
                for (int i = 0; i < 3 && epoch == generation && Relevant(alert); i++)
                {
                    if (!part.SetHighlight(new Color(1f, .65f, .1f))) break;
                    yield return new WaitForSecondsRealtime(.35f);
                    if (part) part.ClearHighlight();
                    yield return new WaitForSecondsRealtime(.25f);
                }
                break;
            }
            if (pulsedPart) pulsedPart.ClearHighlight();
            pulsedPart = null; pulse = null;
        }

        void StopPulse()
        {
            if (pulse != null) StopCoroutine(pulse);
            if (pulsedPart) pulsedPart.ClearHighlight();
            pulsedPart = null; pulse = null;
        }

        static string Wrap(string value, int width)
        {
            var lines = new List<string>(); string line = "";
            foreach (var word in value.Split(' '))
            {
                if (line.Length > 0 && line.Length + word.Length + 1 > width) { lines.Add(line); line = ""; }
                line += (line.Length > 0 ? " " : "") + word;
            }
            if (line.Length > 0) lines.Add(line);
            return string.Join("\n", lines);
        }

        void FinishPlayback()
        {
            if (speaker) { speaker.Stop(); speaker.clip = null; }
            if (activeClip) Release(activeClip);
            activeClip = null; warningPlaying = safetyPlaying = false; playback = null;
        }
        void StopPlayback() { if (playback != null) StopCoroutine(playback); FinishPlayback(); }
        void Failed(string reason) => ResetDelivery();
        void ResetDelivery()
        {
            generation++; StopPlayback(); StopPulse(); queued.Clear(); LastCaption = "";
            if (caption) caption.text = "";
        }
        void Unsubscribe()
        {
            if (!relay) return;
            relay.AlertReceived -= Receive; relay.SessionReset -= ResetDelivery; relay.SyncFailed -= Failed;
        }
        void OnDisable() { ResetDelivery(); }
        static void Release(UnityEngine.Object value){if(!value)return;if(Application.isPlaying)Destroy(value);else DestroyImmediate(value);}
        void OnDestroy() { Unsubscribe(); ResetDelivery(); if (ownedCaption) Release(ownedCaption); }
    }
}
