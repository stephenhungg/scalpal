using System;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using UnityEngine;

namespace Scalpal.Handoff
{
    [Serializable] public sealed class TheatrePreflight
    {
        public bool volunteerConsented, cameraGranted, sceneGranted, poseServiceOk, coachServiceOk, demoMode;
        public string UnavailableReason => !volunteerConsented ? "No volunteer checked in and consented" :
            !cameraGranted || !sceneGranted ? "Camera and spatial permissions are off" :
            !poseServiceOk ? "Body detection offline" : !coachServiceOk ? "Coach service offline" : "";
        public bool ArAvailable => UnavailableReason.Length == 0;
        public string DefaultMode => ArAvailable ? "mixed_reality" : "virtual";
    }

    // Scene-local objects may disappear; this pointer and cached display data live for one player run.
    // No footage, credentials, device poses or raw transcript is persisted here.
    [Serializable] public sealed class HandoffTicket
    {
        public string schema = "scalpal.handoff.v1", runId, patientId, encounterId, procedureId, procedureTitle;
        public string presentationMode = "virtual", modeChosenBy = "learner", contentVersion = "0.1.0";
        public string sharedSessionId = "", attemptId = "", serviceUrl, issuedAt, learnerProcedure;
        public EncounterSurgeryHandoff sourceOffice;
        public EncounterScore scorecard;
        public bool escalated, challengeSeen, consequenceSeen, practiceStarted, timeOutConfirmed, revisedAfterPrompt;
        public bool patientConfirmed, procedureConfirmed, siteConfirmed, risksConfirmed, antibioticsReviewed, imagingReviewed;
        public bool AllConfirmed => patientConfirmed && procedureConfirmed && siteConfirmed && risksConfirmed && antibioticsReviewed && imagingReviewed;
        public SurgicalCase verifiedCase;
        public PreopCheckResult preopResult;
        public void ResetTimeOut()
        {
            timeOutConfirmed = patientConfirmed = procedureConfirmed = siteConfirmed = risksConfirmed = antibioticsReviewed = imagingReviewed = false;
        }
    }

    public static class HandoffRun
    {
        public static HandoffTicket Current { get; private set; }
        public static TheatrePreflight Preflight { get; private set; } = new TheatrePreflight();
        public static bool NeedsEscalation(EncounterScore score) => score != null && (!score.procedureChosenCorrectly || score.diagnosisResult != "correct");
        public static bool Supported(string procedure) => procedure == "lap_appendectomy";
        public static bool SwitchNeedsNewAttempt(HandoffTicket ticket) => ticket != null && ticket.practiceStarted;
        public static bool CanChoose(string mode, TheatrePreflight preflight) => mode == "virtual" || mode == "mixed_reality" && preflight != null && preflight.ArAvailable;
        public static HandoffTicket Begin(EncounterState state, EncounterScore score, string endpoint)
        {
            if (state == null || state.phase != "scored" || string.IsNullOrEmpty(state.encounterId) || score == null ||
                score.patientId != state.patientId || string.IsNullOrEmpty(score.procedureId) || score.carryoverItems == null)
                throw new ArgumentException("Scored encounter identity and structured risks are required. Refresh the scorecard.");
            Current = new HandoffTicket { runId = Guid.NewGuid().ToString("N"), encounterId = state.encounterId,
                patientId = state.patientId, procedureId = score.procedureId, procedureTitle = score.procedureTitle,
                scorecard = score, learnerProcedure = state.assessment?.procedure ?? "", escalated = NeedsEscalation(score), serviceUrl = endpoint.TrimEnd('/'),
                presentationMode = Preflight.DefaultMode, issuedAt = DateTime.UtcNow.ToString("O") };
            return Current;
        }
        public static void BindOfficeSource(HandoffTicket ticket, EncounterSurgeryHandoff source)
        {
            if (ticket == null || source == null || source.patientId != ticket.patientId || source.encounterId != ticket.encounterId
                || source.procedureId != ticket.procedureId || string.IsNullOrEmpty(source.sharedSessionId) || string.IsNullOrEmpty(source.attemptId))
                throw new ArgumentException("The scored office shared attempt is required before theatre.");
            ticket.sourceOffice = JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(source));
            ticket.sharedSessionId = source.sharedSessionId; ticket.attemptId = source.attemptId;
        }
        public static bool SourceBindingMatches(HandoffTicket ticket, string session, string attempt, string patient, string phase)
            => ticket?.sourceOffice != null && session == ticket.sourceOffice.sharedSessionId
                && attempt == ticket.sourceOffice.attemptId && patient == ticket.patientId && phase == "scored";
        public static bool Verify(HandoffTicket ticket, EncounterState state, EncounterScore score, SurgicalCase candidate, out string reason)
        {
            reason = "Encounter or case identity changed. Return to the office and refresh the scorecard.";
            if (ticket == null || ticket.schema != "scalpal.handoff.v1" || ticket.contentVersion != "0.1.0" ||
                state == null || state.encounterId != ticket.encounterId || state.patientId != ticket.patientId || state.phase != "scored" ||
                score == null || score.patientId != ticket.patientId || score.procedureId != ticket.procedureId || score.carryoverItems == null ||
                candidate == null || candidate.patientId != ticket.patientId || candidate.procedureId != ticket.procedureId ||
                candidate.procedure == null || candidate.procedure.id != ticket.procedureId || candidate.brief == null || !candidate.brief.synthetic ||
                (candidate.status != "ready" && candidate.status != "needs_review")) return false;
            reason = ""; return true;
        }
        public static void Clear() => Current = null;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { Current = null; Preflight = new TheatrePreflight(); }
    }
}
