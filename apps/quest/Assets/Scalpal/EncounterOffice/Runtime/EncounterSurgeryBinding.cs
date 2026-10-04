using System;
using Scalpal.Exercises.Data;
using Scalpal.Voice;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    // Recheck the service's committed office result before the existing OR consumer adopts it.
    // This binds HTTP encounter provenance; shared-session attempt provenance is a separate contract.
    public static class EncounterSurgeryBinding
    {
        [Serializable] sealed class CoachCreateRequest { public string patientId, encounterId, mode; }

        public static bool Validate(EncounterSurgeryHandoff handoff, SurgicalCase liveCase,
            EncounterReply liveEncounter, EncounterReply liveScore, out string reason)
        {
            if (handoff == null || !EncounterContract.ValidPatientId(handoff.patientId)
                || !QuestJarvisVoice.ValidEncounterId(handoff.encounterId) || string.IsNullOrEmpty(handoff.procedureId)
                || handoff.assessment == null || handoff.scorecard == null)
                return Reject("The office handoff is missing its committed encounter or surgery.", out reason);
            if (liveCase == null || liveCase.patientId != handoff.patientId || string.IsNullOrEmpty(liveCase.caseId)
                || liveCase.procedureId != handoff.procedureId || liveCase.procedure == null
                || liveCase.procedure.id != handoff.procedureId || liveCase.brief == null
                || liveCase.brief.patientId != handoff.patientId || !liveCase.brief.synthetic
                || (liveCase.status != "ready" && liveCase.status != "needs_review"))
                return Reject("The live surgical case is unavailable or differs from the selected synthetic patient.", out reason);
            if (EncounterContract.HasError(liveEncounter) || EncounterContract.HasError(liveScore)
                || !Matches(liveEncounter?.state, handoff) || !Matches(liveScore?.state, handoff)
                || liveEncounter.state.version != liveScore.state.version)
                return Reject("The service has not confirmed the same scored office encounter.", out reason);
            if (!Same(handoff.assessment, liveEncounter.state.assessment)
                || !Same(handoff.assessment, liveScore.state.assessment))
                return Reject("The committed learner assessment differs from the office handoff.", out reason);
            var score = liveScore.scorecard;
            if (score == null || score.max != 100 || score.total < 0 || score.total > 100
                || string.IsNullOrEmpty(score.grade) || score.patientId != handoff.patientId
                || score.procedureId != handoff.procedureId
                || !Same(handoff.scorecard, score))
                return Reject("The live score, authored surgery, or chart risks differ from the office handoff.", out reason);
            reason = ""; return true;
        }

        public static string CoachCreateJson(EncounterSurgeryHandoff handoff, string mode)
        {
            if (handoff == null || !EncounterContract.ValidPatientId(handoff.patientId)
                || !QuestJarvisVoice.ValidEncounterId(handoff.encounterId))
                throw new ArgumentException("A confirmed office encounter is required.", nameof(handoff));
            if (mode != "virtual")
                throw new ArgumentException("The office handoff requires virtual presentation.", nameof(mode));
            return JsonUtility.ToJson(new CoachCreateRequest { patientId = handoff.patientId, encounterId = handoff.encounterId, mode = mode });
        }

        static bool Matches(EncounterState state, EncounterSurgeryHandoff handoff) => state != null
            && state.phase == "scored" && state.encounterId == handoff.encounterId && state.patientId == handoff.patientId;
        static bool Same(object left, object right) => left != null && right != null
            && JsonUtility.ToJson(left) == JsonUtility.ToJson(right);
        static bool Reject(string message, out string reason) { reason = message; return false; }
    }
}
