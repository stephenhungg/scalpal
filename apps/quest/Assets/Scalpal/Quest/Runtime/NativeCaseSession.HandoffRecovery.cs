using System;
using System.Collections;
using System.Collections.Generic;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Engine;
using UnityEngine;

namespace Scalpal.Quest
{
    public sealed partial class NativeCaseSession
    {
        // An offline attempt keeps the SAME local CaseRunner. A recovered coach receives the exact
        // authored events before voice resumes; it never resets or independently advances the learner.
        readonly List<(CaseEvent action, string step)> localCoachEvents = new List<(CaseEvent, string)>();
        bool localCaptionAttempt, recoveringLocalCoach, localCoachReady;
        float recoverCoachAt;
        void ResetHandoffRecovery()
        {
            localCoachEvents.Clear(); localCaptionAttempt = recoveringLocalCoach = localCoachReady = false; recoverCoachAt = 0;
        }
        void RecordLocalCoachEvent(CaseEvent action, string step)
        {
            if (!localCaptionAttempt) return;
            localCoachEvents.Add((action, step));
            if (localCoachReady) coach.Forward(action, step);
        }
        void UpdateHandoffCoachRecovery()
        {
            if (!localCaptionAttempt || !Practicing || !SharedMatches || !RegistrationReady || recoveringLocalCoach) return;
            if (localCoachReady)
            {
                // An uncertain delivery is retried by the existing relay. If it fails permanently,
                // continue the same local runner and rebuild a fresh coach from all authored events.
                if (!string.IsNullOrEmpty(coach.SyncFailureReason))
                { localCoachReady = false; coach.UseSession(""); coachSessionId = ""; voice.Disconnect(); recoverCoachAt = Time.unscaledTime + 10; }
                return;
            }
            if (Time.unscaledTime >= recoverCoachAt)
            { recoverCoachAt = Time.unscaledTime + 10; StartCoroutine(RecoverLocalCoach(generation)); }
        }
        IEnumerator RecoverLocalCoach(int epoch)
        {
            string json = null;
            yield return Request("POST", "/coach/sessions", JsonUtility.ToJson(CoachRequest()), value => json = value);
            if (epoch != generation || !localCaptionAttempt || !Practicing || !RegistrationReady) yield break;
            Created created = null;
            try { if (json != null) created = JsonUtility.FromJson<Created>(json); } catch (ArgumentException) { }
            if (created?.snapshot == null || created.snapshot.patientId != SelectedPatientId || created.snapshot.procedureId != SelectedProcedureId
                || created.snapshot.caseId != candidate.caseId || created.snapshot.mode != PresentationMode || string.IsNullOrEmpty(created.sessionId)) yield break;
            recoveringLocalCoach = true;
            coach.AdoptSession(created.sessionId, SelectedPatientId, SelectedProcedureId, candidate.caseId, PresentationMode, candidate.procedure.firstStep);
            coach.Tracking(true);
            float deadline = Time.unscaledTime + 12;
            while (epoch == generation && !coach.IsSynchronized && Time.unscaledTime < deadline && string.IsNullOrEmpty(coach.SyncFailureReason)) yield return null;
            if (epoch != generation) yield break;
            if (!coach.IsSynchronized || !Practicing) { AbandonRecovery(); yield break; }
            foreach (var item in localCoachEvents) coach.Forward(item.action, item.step);
            deadline = Time.unscaledTime + 15;
            while (epoch == generation && !coach.DeliveryIdle && Time.unscaledTime < deadline && string.IsNullOrEmpty(coach.SyncFailureReason)) yield return null;
            if (epoch != generation) yield break;
            if (!coach.IsSynchronized || !coach.DeliveryIdle || !RegistrationReady || !SharedMatches || !Practicing) { AbandonRecovery(); yield break; }
            string contextJson = null;
            yield return Request("GET", "/coach/sessions/" + Uri.EscapeDataString(created.sessionId), null, value => contextJson = value);
            if (epoch != generation) yield break;
            ContextReply context = null;
            try { if (contextJson != null) context = JsonUtility.FromJson<ContextReply>(contextJson); } catch (ArgumentException) { }
            if (context?.snapshot?.sessionId != created.sessionId || context.snapshot.patientId != SelectedPatientId || context.snapshot.procedureId != SelectedProcedureId
                || context.snapshot.caseId != candidate.caseId || context.snapshot.mode != PresentationMode || context.snapshot.step?.id != exercise.Current?.id || !RegistrationReady || !SharedMatches || !Practicing)
            { AbandonRecovery(); yield break; }
            coachSessionId = created.sessionId; voicePrompt = created.systemPrompt; voiceGreeting = "Scrubbed in with you. Your authored practice progress is restored."; voiceContext = context.context;
            localCoachReady = true; recoveringLocalCoach = false;
            Message = "Coach restored on the same attempt";
            ConnectVoice();
        }
        void AbandonRecovery()
        {
            recoveringLocalCoach = localCoachReady = false; coach.UseSession(""); recoverCoachAt = Time.unscaledTime + 10;
            Message = "Coach offline. Captions and authored local scoring continue.";
        }
    }
}
