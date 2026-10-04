using System;
using Scalpal.Exercises.Data;
using Scalpal.Voice;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.EncounterOffice
{
    [Serializable] public sealed class EncounterSurgeryHandoff
    {
        public string patientId, encounterId, procedureId, procedureTitle, serviceUrl, sharedSessionId, attemptId;
        public EncounterAssessment assessment;
        public EncounterScore scorecard;
    }

    // The explore producer supplies a canonical subject. This scene consumer preserves one scored encounter into OR.
    public static class EncounterOfficeRoute
    {
        public const string OfficeScene = "DiagnosisOffice", SurgeryScene = "NativeSession";
        static string selectedPatient = "", selectedEndpoint = "";
        static EncounterSurgeryHandoff surgery;

        public static void SelectPatient(string patientId, string serviceUrl = "")
        {
            if (!EncounterContract.ValidPatientId(patientId)) throw new ArgumentException("Choose a canonical patient subject ID.", nameof(patientId));
            if (!string.IsNullOrEmpty(serviceUrl) && !ValidEndpoint(serviceUrl)) throw new ArgumentException("Encounter service needs an HTTP(S) URL.", nameof(serviceUrl));
            selectedPatient = patientId; selectedEndpoint = serviceUrl; surgery = null;
        }

        public static void ShowPatients(string serviceUrl)
        {
            if (!ValidEndpoint(serviceUrl)) throw new ArgumentException("Encounter service needs an HTTP(S) URL.", nameof(serviceUrl));
            selectedPatient = ""; selectedEndpoint = serviceUrl; surgery = null;
        }

        public static bool CanEnter(PatientListEntry entry) => entry != null && EncounterContract.ValidPatientId(entry.patientId)
            && !string.IsNullOrEmpty(entry.procedureId) && (entry.status == "ready" || entry.status == "needs_review");

        public static bool TakePatient(out string patientId, out string serviceUrl)
        {
            patientId = selectedPatient; serviceUrl = selectedEndpoint;
            selectedPatient = selectedEndpoint = "";
            return !string.IsNullOrEmpty(patientId);
        }

        public static bool OpenOffice(PatientListEntry entry, string serviceUrl, out string reason)
        {
            if (!CanEnter(entry)) { reason = "This patient is unavailable for an encounter."; return false; }
            if (!Application.CanStreamedLevelBeLoaded(OfficeScene)) { reason = "The diagnosis office is not included in this player."; return false; }
            SelectPatient(entry.patientId, serviceUrl);
            SceneManager.LoadScene(OfficeScene); reason = ""; return true;
        }

        public static bool PrepareSurgery(EncounterState state, EncounterScore score, string authoredProcedureId, string endpoint, out string reason, string sharedSessionId = "", string attemptId = "")
        {
            surgery = null;
            if (state == null || state.phase != "scored" || !EncounterContract.ValidPatientId(state.patientId)
                || !EncounterContract.ValidOfficeId(state.encounterId) || state.assessment == null
                || EncounterContract.IsInterview(state.encounterId) && (score.kind != "interview" || score.carryoverItems == null)
                || score == null || score.patientId != state.patientId || score.max != 100 || score.total < 0 || score.total > 100 || string.IsNullOrEmpty(score.grade)
                || string.IsNullOrEmpty(sharedSessionId) != string.IsNullOrEmpty(attemptId)
                || string.IsNullOrEmpty(score.procedureId) || score.procedureId != authoredProcedureId || !ValidEndpoint(endpoint))
            { reason = "The scored encounter and reviewed surgery must match before entering the OR."; return false; }
            // Stephen's policy: authored surgery after feedback. Keep the learner's exact committed proposal and score.
            surgery = JsonUtility.FromJson<EncounterSurgeryHandoff>(JsonUtility.ToJson(new EncounterSurgeryHandoff
            {
                patientId = state.patientId, encounterId = state.encounterId, procedureId = score.procedureId,
                procedureTitle = score.procedureTitle, serviceUrl = endpoint, sharedSessionId = sharedSessionId, attemptId = attemptId, assessment = state.assessment, scorecard = score
            }));
            reason = ""; return true;
        }

        public static bool TakeSurgery(out EncounterSurgeryHandoff handoff)
        {
            handoff = surgery; surgery = null;
            return handoff != null;
        }

        public static void ClearSurgery() => surgery = null;
        static bool ValidEndpoint(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
    }
}
