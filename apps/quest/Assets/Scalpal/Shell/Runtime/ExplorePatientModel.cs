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
        readonly Dictionary<string, SurgicalCase> cases = new Dictionary<string, SurgicalCase>(StringComparer.Ordinal);
        PatientListEntry[] patients = Array.Empty<PatientListEntry>();
        PatientListEntry[] allRecords = Array.Empty<PatientListEntry>();
        bool hasPatientList;
        public int UnavailableCount { get; private set; }
        public PatientListEntry[] Patients => patients;
        public string SelectedPatientId { get; private set; } = "";
        public PatientListEntry Selected => patients.FirstOrDefault(p => !string.IsNullOrEmpty(SelectedPatientId) && p.patientId == SelectedPatientId);
        public SurgicalCase SelectedCase => CaseFor(SelectedPatientId);
        public PreopBrief SelectedBrief { get; private set; }
        public bool DetailLoading { get; private set; }
        public string DetailError { get; private set; } = "";
        public bool CanBegin => Selected != null && Listed(Selected) &&
            (Selected.status == "ready" || Selected.status == "needs_review") &&
            Selected.encounterAvailable && EncounterContract.ValidPatientId(Selected.patientId) && !DetailLoading && string.IsNullOrEmpty(DetailError) &&
            SelectedBrief?.patient != null && SelectedBrief.patientId == SelectedPatientId && SelectedBrief.synthetic &&
            (SelectedBrief.dataSource == "demo" || SelectedBrief.dataSource == "sandbox");

        public string AvailabilityReason => Selected == null ? "Choose a patient" :
            !EncounterContract.ValidPatientId(Selected.patientId) ? "Patient record ID is invalid." :
            !CanSelect(Selected) ? StatusReason(Selected) :
            Selected.status == "retry" ? "Chart unavailable · retrying" :
            !Selected.encounterAvailable ? "Interview coming soon" :
            DetailLoading ? "Loading chart…" :
            !string.IsNullOrEmpty(DetailError) ? DetailError :
            SelectedBrief == null ? "Load the chart to begin" : "";

        public void ApplyBundle(ScalpalBundle bundle)
        {
            if (bundle == null) return;
            cases.Clear();
            foreach (var item in bundle.cases ?? Array.Empty<SurgicalCase>())
                if (item != null && !string.IsNullOrEmpty(item.patientId))
                {
                    // Bundles may append an advanced offline variant for the same subject.
                    // Prefer the catalog's primary procedure, independent of variant ordering.
                    var row = patients.FirstOrDefault(p => p.patientId == item.patientId)
                        ?? (bundle.patients ?? Array.Empty<PatientListEntry>()).FirstOrDefault(p => p != null && p.patientId == item.patientId);
                    if (!cases.ContainsKey(item.patientId) || item.procedureId == row?.procedureId)
                        cases[item.patientId] = item;
                }
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
            ReplacePatients(allRecords);
            if (SelectedPatientId == patientCase.patientId && !Listed(entry)) ClearSelection();
            return true;
        }

        void ReplacePatients(PatientListEntry[] source)
        {
            var records = (source ?? Array.Empty<PatientListEntry>()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.patientId))
                .GroupBy(p => p.patientId, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            allRecords=records;
            UnavailableCount = records.Count(p => p.status == "blocked");
            patients = records.Where(p => p.status != "blocked")
                .OrderBy(p => StatusOrder(p.status)).ThenBy(p => p.displayLabel, StringComparer.Ordinal).ToArray();
            if (Selected == null || !Listed(Selected)) ClearSelection();
        }

        // The explore grid: only rows a clinician can actually open. Connect-only scenarios, blocked
        // consent, records without a procedure, unnamed placeholders and subjects without an authored
        // interview stay out of the grid entirely. A retry row stays only while it can still recover.
        public PatientListEntry[] VisiblePatients() => patients.Where(Listed).ToArray();
        public bool Listed(PatientListEntry patient) => CanSelect(patient) && EncounterContract.ValidPatientId(patient.patientId) &&
            patient.encounterAvailable && ProcedureShort(patient).Length > 0 && !Placeholder(Name(patient));
        // Rows whose case should be re-requested in the background (the grid has no Refresh button).
        public bool Recoverable(PatientListEntry patient) => patient != null && patient.status == "retry" &&
            patient.encounterAvailable && EncounterContract.ValidPatientId(patient.patientId);
        static bool Placeholder(string name) => string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("Unnamed", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase);

        public bool Select(string patientId)
        {
            var patient = patients.FirstOrDefault(p => p.patientId == patientId);
            if (patient == null || !Listed(patient))
            {
                ClearSelection();
                return false;
            }
            SelectedPatientId = patientId;
            SelectedBrief = null;
            DetailError = "";
            DetailLoading = patient.status != "retry";
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
            DetailError = brief.synthetic && (brief.dataSource == "demo" || brief.dataSource == "sandbox") ? "" : "Only synthetic records can begin an encounter.";
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
        public string Name(PatientListEntry patient) => NonEmpty((SelectedBrief?.patientId == patient?.patientId ? SelectedBrief.patient : null)?.name, NonEmpty(CaseFor(patient?.patientId)?.patient?.name,
            NonEmpty(patient?.displayLabel, "Unavailable patient")));
        public string Demographics(PatientListEntry patient)
        {
            var summary = SelectedBrief?.patientId == patient?.patientId ? SelectedBrief.patient : CaseFor(patient?.patientId)?.patient;
            return summary != null && summary.age >= 0 ? summary.age + " · " + NonEmpty(summary.sex, "Unknown sex") : "Demographics unavailable";
        }
        public string Complaint(PatientListEntry patient) => NonEmpty(CaseFor(patient?.patientId)?.presentation,
            NonEmpty(patient?.title, "Chart unavailable"));
        // The one quiet card line: "40 · Appendix" (age is omitted when the record has none).
        public string CardLine(PatientListEntry patient)
        {
            var summary = SelectedBrief?.patientId == patient?.patientId ? SelectedBrief.patient : CaseFor(patient?.patientId)?.patient;
            string procedure = ProcedureShort(patient);
            return summary != null && summary.age >= 0 ? summary.age + " · " + procedure : procedure;
        }
        public static string ProcedureShort(PatientListEntry patient)
        {
            string id = patient?.procedureId ?? "";
            return id == "open_appendectomy" || id == "lap_appendectomy" ? "Appendix" : id == "lap_cholecystectomy" ? "Gallbladder" :
                id == "lap_sigmoid_colectomy" ? "Colon" : id.Length > 0 ? NonEmpty(patient.procedureTitle, "") : "";
        }
        // Detail panel lines.
        public string AgeSex(PatientListEntry patient)
        {
            var summary = SelectedBrief?.patientId == patient?.patientId ? SelectedBrief.patient : CaseFor(patient?.patientId)?.patient;
            var parts = new List<string>();
            if (summary != null && summary.age >= 0) parts.Add(summary.age.ToString());
            if (!string.IsNullOrWhiteSpace(summary?.sex)) parts.Add(Capitalize(summary.sex));
            return string.Join(" · ", parts);
        }
        public string Presenting(PatientListEntry patient)
        {
            string text = Complaint(patient).Trim();
            int end = text.IndexOf(". ", StringComparison.Ordinal);
            return end > 0 ? text.Substring(0, end + 1) : text;
        }
        public string ProcedureAndUrgency(PatientListEntry patient)
        {
            string procedure = NonEmpty(CaseFor(patient?.patientId)?.procedure?.title, NonEmpty(patient?.procedureTitle, ProcedureShort(patient)));
            return string.IsNullOrWhiteSpace(patient?.urgency) ? procedure : procedure + " · " + Capitalize(patient.urgency);
        }
        // First line of each distinct chart section (the patient line repeats the header), at most three.
        public string[] Highlights(int count = 3) => (SelectedBrief?.chart ?? Array.Empty<ChartLine>())
            .Where(line => line != null && !string.IsNullOrWhiteSpace(line.text) && !string.Equals(line.section, "Patient", StringComparison.OrdinalIgnoreCase))
            .GroupBy(line => line.section ?? "").Select(group => group.First()).Take(count)
            .Select(line => string.IsNullOrWhiteSpace(line.section) ? line.text : line.section + ": " + line.text).ToArray();
        static string Capitalize(string value) => string.IsNullOrEmpty(value) ? "" : char.ToUpperInvariant(value[0]) + value.Substring(1);
        static string NonEmpty(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
        static int StatusOrder(string status) => status == "ready" ? 0 : status == "needs_review" ? 1 : status == "retry" ? 2 : 3;
    }
}
