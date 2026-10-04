using System;
using Scalpal.Anatomy;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using Scalpal.Handoff;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.Recap
{
    // Adapts the existing native session and scored office handoff. It never grades surgery.
    public sealed class RecapSessionIntegration : MonoBehaviour
    {
        public const string EndingScene = "RunEnding";
        RecapRunContext context;
        BodyGradeAdapter bodyGrade;
        EncounterSurgeryHandoff office;
        string officeAttempt = "";
        static EncounterSurgeryHandoff pendingOffice;
        static string pendingAttempt, pendingSession, pendingOfficeAttempt;
        static DemoFlags pendingDemo;
        public string LastNavigationError { get; private set; } = "";

        public static RecapSessionIntegration Ensure()
        {
            var context = RecapRunContext.Ensure();
            var value = context.GetComponent<RecapSessionIntegration>();
            if (!value) value = context.gameObject.AddComponent<RecapSessionIntegration>();
            value.Initialize(context);
            return value;
        }
        void Awake() => Initialize(GetComponent<RecapRunContext>());
        void Initialize(RecapRunContext next)
        {
            if (context == next) return;
            if (context) context.RetrySurgery -= Retry;
            context = next;
            if (context) context.RetrySurgery += Retry;
        }
        void OnDestroy() { bodyGrade?.Dispose(); if (context) context.RetrySurgery -= Retry; }

        public static RunResult FromSession(EncounterSurgeryHandoff handoff, SurgicalCase selected,
            string sessionId, string attemptId, string coachSessionId, DemoFlags demo)
        {
            if (selected == null || string.IsNullOrWhiteSpace(coachSessionId))
                throw new ArgumentException("Recap requires the real selected case and coach session.");
            if (handoff != null && (handoff.sharedSessionId != sessionId || handoff.attemptId != attemptId
                || handoff.patientId != selected.patientId || handoff.procedureId != selected.procedureId))
                throw new ArgumentException("The office handoff differs from the active native session.");
            return new RunResult
            {
                runId = coachSessionId, sessionId = sessionId, attemptId = attemptId,
                encounterId = handoff?.encounterId ?? "", patientId = selected.patientId, procedureId = selected.procedureId,
                diagnosisAvailable = handoff?.scorecard != null,
                diagnosis = handoff?.scorecard == null ? null : JsonUtility.FromJson<DiagnosisScorecard>(JsonUtility.ToJson(handoff.scorecard)),
                surgery = new SurgeryGrade { available = false }, demo = demo ?? new DemoFlags(),
                replay = new ReplayResult { source = "unknown", status = "failed", failureReason = "No capture adapter has supplied a recording for this attempt." }
            };
        }
        public static RunResult FromHandoff(HandoffTicket ticket, SurgicalCase selected, string sessionId, string attemptId)
        {
            if (ticket == null || string.IsNullOrWhiteSpace(ticket.runId) || ticket.sourceOffice == null || ticket.scorecard == null
                || ticket.sharedSessionId != sessionId || ticket.attemptId != attemptId || selected == null
                || ticket.patientId != selected.patientId || ticket.procedureId != selected.procedureId)
                throw new ArgumentException("The canonical handoff must match the current surgical attempt.");
            // sourceOffice belongs to the original scored encounter. Adapt a copy for this
            // surgical attempt without mutating that provenance, including on repeated retries.
            var source = JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(ticket.sourceOffice));
            source.sharedSessionId = sessionId; source.attemptId = attemptId; source.scorecard = ticket.scorecard;
            var result = FromSession(source, selected, sessionId, attemptId, ticket.runId, DemoFlags.JudgePath(ticket.demoMode));
            result.runId = ticket.runId;
            return result;
        }
        public void Begin(HandoffTicket ticket, SurgicalCase selected, string sessionId, string attemptId,
            string coachSessionId, string voiceServiceUrl, string clientToken)
        {
            context.Begin(FromHandoff(ticket, selected, sessionId, attemptId));
            Observe(null);
            office = JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(ticket.sourceOffice));
            officeAttempt = ticket.sourceOffice.attemptId;
            context.coachSessionId = coachSessionId; context.voiceServiceUrl = voiceServiceUrl; context.clientToken = clientToken;
        }
        public void Begin(EncounterSurgeryHandoff handoff, SurgicalCase selected, string sessionId,
            string attemptId, string coachSessionId, string voiceServiceUrl, string clientToken, string encounterAttemptId = "")
        {
            context.Begin(FromSession(handoff, selected, sessionId, attemptId, coachSessionId, context.demo));
            Observe(null);
            office = handoff == null ? null : JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(handoff));
            officeAttempt = string.IsNullOrEmpty(encounterAttemptId) ? handoff?.attemptId ?? "" : encounterAttemptId;
            context.coachSessionId = coachSessionId;
            context.voiceServiceUrl = voiceServiceUrl;
            context.clientToken = clientToken;
        }
        public void Observe(AnatomyExerciseBinding exercise)
        {
            bodyGrade?.Dispose(); bodyGrade = exercise ? new BodyGradeAdapter(exercise) : null;
        }
        public bool Complete(AnatomyExerciseBinding exercise, string attemptId, SurgeryGrade grade = null)
        {
            if (!exercise || !exercise.Completed || exercise.SelectedCase == null || context.result == null
                || context.result.patientId != exercise.SelectedCase.patientId || context.result.procedureId != exercise.SelectedCase.procedureId) return false;
            // Consume the final body rubric, including learner-finished incomplete runs.
            // Legacy exercises without that producer keep the supplied grade or unavailable.
            var produced = exercise.Grade == null ? grade : BodyGradeAdapter.Snapshot(exercise, bodyGrade);
            return context.EndSurgery(attemptId, produced);
        }
        public static bool IsFreshRetry(string previousSession, string previousAttempt, string session, string attempt)
            => !string.IsNullOrWhiteSpace(previousSession) && previousSession == session
                && !string.IsNullOrWhiteSpace(previousAttempt) && !string.IsNullOrWhiteSpace(attempt) && previousAttempt != attempt;

        void Retry()
        {
            LastNavigationError = "";
            if (!Application.CanStreamedLevelBeLoaded(context.surgeryScene))
            { LastNavigationError = "Retry unavailable: include the OR scene."; return; }
            if (!PrepareRetry(out var reason)) { LastNavigationError = reason; return; }
            // Reloading destroys old tool/tissue/body/coach instances. A confirmed different
            // shared attempt must then create a fresh coach session before Begin succeeds.
            SceneManager.LoadScene(context.surgeryScene);
        }
        public bool PrepareRetry(out string reason)
        {
            if (!context.SegmentClosed || context.result == null)
            { reason = "Retry unavailable: finish the current attempt."; return false; }
            pendingOffice = office == null ? null : JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(office));
            pendingSession = context.result.sessionId; pendingAttempt = context.result.attemptId; pendingOfficeAttempt = officeAttempt;
            pendingDemo = JsonUtility.FromJson<DemoFlags>(JsonUtility.ToJson(context.result.demo));
            if (HandoffRun.Current != null)
            {
                var ticket = HandoffRun.Current;
                if (ticket.runId != context.result.runId || ticket.sharedSessionId != context.result.sessionId || ticket.attemptId != context.result.attemptId)
                { pendingOffice = null; pendingSession = pendingAttempt = pendingOfficeAttempt = ""; pendingDemo = null; reason = "Retry context no longer matches the canonical run."; return false; }
                ticket.attemptId = ""; ticket.practiceStarted = false; ticket.ResetTimeOut();
            }
            reason = ""; return true;
        }
        public static bool TakeRetry(out EncounterSurgeryHandoff handoff, out string sessionId, out string previousAttempt, out string encounterAttemptId)
        {
            handoff = pendingOffice; sessionId = pendingSession; previousAttempt = pendingAttempt; encounterAttemptId = pendingOfficeAttempt;
            bool valid = !string.IsNullOrWhiteSpace(previousAttempt) && !string.IsNullOrWhiteSpace(sessionId);
            if (valid && RecapRunContext.Current) RecapRunContext.Current.demo = pendingDemo ?? new DemoFlags();
            pendingOffice = null; pendingSession = pendingAttempt = pendingOfficeAttempt = ""; pendingDemo = null;
            return valid;
        }
    }
}
