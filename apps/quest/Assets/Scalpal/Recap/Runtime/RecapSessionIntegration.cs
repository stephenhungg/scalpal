using System;
using Scalpal.Anatomy;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.Recap
{
    // Adapts the existing native session and scored office handoff. It never grades surgery.
    public sealed class RecapSessionIntegration : MonoBehaviour
    {
        public const string EndingScene = "RunEnding";
        RecapRunContext context;
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
        void OnDestroy() { if (context) context.RetrySurgery -= Retry; }

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
        public void Begin(EncounterSurgeryHandoff handoff, SurgicalCase selected, string sessionId,
            string attemptId, string coachSessionId, string voiceServiceUrl, string clientToken, string encounterAttemptId = "")
        {
            context.Begin(FromSession(handoff, selected, sessionId, attemptId, coachSessionId, context.demo));
            office = handoff == null ? null : JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(handoff));
            officeAttempt = string.IsNullOrEmpty(encounterAttemptId) ? handoff?.attemptId ?? "" : encounterAttemptId;
            context.coachSessionId = coachSessionId;
            context.voiceServiceUrl = voiceServiceUrl;
            context.clientToken = clientToken;
        }
        public bool Complete(AnatomyExerciseBinding exercise, string attemptId, SurgeryGrade grade = null)
        {
            if (!exercise || !exercise.Completed || exercise.SelectedCase == null || context.result == null
                || context.result.patientId != exercise.SelectedCase.patientId || context.result.procedureId != exercise.SelectedCase.procedureId) return false;
            // The open-body aggregate grader has not landed. Missing data stays unavailable.
            return context.EndSurgery(attemptId, grade);
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
