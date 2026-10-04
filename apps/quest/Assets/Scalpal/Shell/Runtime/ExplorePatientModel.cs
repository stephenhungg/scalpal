// Presentation state over the existing preop DTOs. No second clinical contract or progression engine.
using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;

namespace Scalpal.Shell
{
    public sealed class ExplorePatientModel
    {
        // Packaged demo IDs used to audit cached coverage. Live eligibility comes from the service.
        static readonly HashSet<string> AuthoredIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "patient-demo-multi-source", "patient-demo-pediatric-asthma", "patient-demo-sparse",
            "patient-demo-polypharmacy", "patient-demo-001", "patient-demo-source-unavailable",
            "patient-demo-messy-coding", "patient-demo-consent-partial"
        };
        readonly Dictionary<string, SurgicalCase> cases = new Dictionary<string, SurgicalCase>(StringComparer.Ordinal);
        PatientListEntry[] patients = Array.Empty<PatientListEntry>();
        bool hasPatientList;
        public PatientListEntry[] Patients => patients;
        public string ProcedureFilter { get; private set; } = "";
        public string UrgencyFilter { get; private set; } = "";
        public string SelectedPatientId { get; private set; } = "";
        public PatientListEntry Selected => patients.FirstOrDefault(p => !string.IsNullOrEmpty(SelectedPatientId) && p.patientId == SelectedPatientId);
        public SurgicalCase SelectedCase => CaseFor(SelectedPatientId);
        public PreopBrief SelectedBrief { get; private set; }
        public bool DetailLoading { get; private set; }
        public string DetailError { get; private set; } = "";
        public bool CanBegin => Selected != null && CanSelect(Selected) &&
            (Selected.status == "ready" || Selected.status == "needs_review") &&
            Selected.encounterAvailable && HasNativeEncounter(Selected.patientId) && !DetailLoading && string.IsNullOrEmpty(DetailError) &&
            SelectedBrief?.patient != null && SelectedBrief.patientId == SelectedPatientId && SelectedBrief.synthetic &&
            SelectedBrief.dataSource == "demo";

        public string AvailabilityReason => Selected == null ? "Choose a patient" :
            !CanSelect(Selected) ? StatusReason(Selected) :
            Selected.status == "retry" ? "Chart unavailable. Try again." :
            !Selected.encounterAvailable || !HasNativeEncounter(Selected.patientId) ? "Interview coming soon" :
            DetailLoading ? "Loading chart…" :
            !string.IsNullOrEmpty(DetailError) ? DetailError :
            SelectedBrief == null ? "Load the chart to begin" : "";

        public void ApplyBundle(ScalpalBundle bundle)
        {
            if (bundle == null) return;
            cases.Clear();
            foreach (var item in bundle.cases ?? Array.Empty<SurgicalCase>())
                if (item != null && !string.IsNullOrEmpty(item.patientId)) cases[item.patientId] = item;
            // A slower bundle response cannot overwrite a fresher /patients response.
            if (!hasPatientList) ReplacePatients(bundle.patients);
        }

        public void ApplyPatients(PatientList list)
        {
            if (list?.patients == null) return;
            hasPatientList = true;
            ReplacePatients(list.patients);
        }

        public bool ApplyCase(SurgicalCase patientCase)
        {
            if (patientCase == null || string.IsNullOrEmpty(patientCase.patientId)) return false;
            var entry = patients.FirstOrDefault(p => p.patientId == patientCase.patientId);
            if (entry == null) return false;
            cases[patientCase.patientId] = patientCase;
            // Preserve the existing list DTO and actions while adopting the case endpoint's newer state.
            entry.status = patientCase.status;
            entry.procedureId = patientCase.procedureId;
            entry.procedureTitle = patientCase.procedure?.title ?? entry.procedureTitle;
            entry.urgency = patientCase.urgency;
            entry.displayLabel = patientCase.patient?.displayLabel ?? entry.displayLabel;
            patients = patients.OrderBy(p => StatusOrder(p.status)).ThenBy(p => p.displayLabel, StringComparer.Ordinal).ToArray();
            if (SelectedPatientId == patientCase.patientId && !CanSelect(entry)) ClearSelection();
            return true;
        }

        void ReplacePatients(PatientListEntry[] source)
        {
            // Keep the two connection-only scenarios visible but disabled, without inventing subjects.
            patients = (source ?? Array.Empty<PatientListEntry>())
                .Where(p => p != null)
                .GroupBy(p => string.IsNullOrEmpty(p.patientId) ? "scenario:" + p.scenarioId : p.patientId, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(p => StatusOrder(p.status)).ThenBy(p => p.displayLabel, StringComparer.Ordinal).ToArray();
            if (Selected == null || !CanSelect(Selected)) ClearSelection();
        }

        public PatientListEntry[] VisiblePatients() => patients.Where(p =>
            (ProcedureFilter.Length == 0 || p.procedureId == ProcedureFilter) &&
            (UrgencyFilter.Length == 0 || p.urgency == UrgencyFilter)).ToArray();

        public void SetFilters(string procedureId = "", string urgency = "")
        {
            ProcedureFilter = procedureId ?? "";
            UrgencyFilter = urgency ?? "";
            if (!VisiblePatients().Any(p => p.patientId == SelectedPatientId)) ClearSelection();
        }

        public bool Select(string patientId)
        {
            var patient = patients.FirstOrDefault(p => p.patientId == patientId);
            if (patient == null || !CanSelect(patient))
            {
                ClearSelection();
                return false;
            }
            SelectedPatientId = patientId;
            SelectedBrief = null;
            DetailError = "";
            DetailLoading = true;
            return true;
        }

        // The view passes the request identity for both success and failure. A late response for a
        // previous card must never populate or unlock the current selection.
        public bool ApplyBrief(PreopBrief brief)
        {
            if (brief == null || brief.patientId != SelectedPatientId || Selected == null) return false;
            if (brief.patient == null)
            {
                FailDetail(brief.patientId, "The chart is missing patient details. Try again.");
                return true;
            }
            SelectedBrief = brief;
            DetailLoading = false;
            DetailError = brief.synthetic && brief.dataSource == "demo" ? "" : "Only synthetic demo records can begin an encounter.";
            return true;
        }

        public void FailDetail(string patientId, string message)
        {
            if (patientId != SelectedPatientId) return;
            SelectedBrief = null;
            DetailLoading = false;
            DetailError = string.IsNullOrWhiteSpace(message) ? "Chart unavailable. Try again." : message;
        }

        public void ClearSelection()
        {
            SelectedPatientId = "";
            SelectedBrief = null;
            DetailLoading = false;
            DetailError = "";
        }

        public SurgicalCase CaseFor(string patientId) => patientId != null && cases.TryGetValue(patientId, out var result) ? result : null;
        // Eligibility comes from the matching synthetic demo brief and the office's authoritative
        // case lookup. Generic avatar availability does not restrict clinical encounter subjects.
        public static bool HasNativeEncounter(string patientId) => EncounterContract.ValidPatientId(patientId);
        public static bool HasAuthoredEncounter(string patientId) => patientId != null && AuthoredIds.Contains(patientId);
        public static bool CanSelect(PatientListEntry patient) => patient != null && !string.IsNullOrEmpty(patient.patientId) &&
            (patient.status == "ready" || patient.status == "needs_review" || patient.status == "retry");
        public static string StatusLabel(string status)
        {
            switch (status)
            {
                case "ready": return "Ready";
                case "needs_review": return "Chart has gaps";
                case "blocked": return "Locked";
                case "retry": return "Try again";
                case "connect": return "No patient record";
                default: return "Unavailable";
            }
        }
        public string StatusReason(PatientListEntry patient) => NonEmpty(CaseFor(patient?.patientId)?.statusReason,
            patient?.status == "blocked" ? "Chart access is blocked." : StatusLabel(patient?.status));
        public string Name(PatientListEntry patient) => NonEmpty(CaseFor(patient?.patientId)?.patient?.name,
            NonEmpty(patient?.displayLabel, "Unavailable patient"));
        public string Demographics(PatientListEntry patient)
        {
            var summary = CaseFor(patient?.patientId)?.patient;
            return summary != null && summary.age >= 0 ? summary.age + " · " + NonEmpty(summary.sex, "Unknown sex") : "Demographics unavailable";
        }
        public string Complaint(PatientListEntry patient) => NonEmpty(CaseFor(patient?.patientId)?.presentation,
            NonEmpty(patient?.title, "Chart unavailable"));
        static string NonEmpty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        static int StatusOrder(string status) => status == "ready" ? 0 : status == "needs_review" ? 1 : status == "retry" ? 2 : 3;
    }
}
