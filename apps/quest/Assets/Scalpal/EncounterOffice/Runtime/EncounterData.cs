using System;
using System.Text;
using Scalpal.Exercises.Data;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    [Serializable] public sealed class EncounterAssessment { public string diagnosis = ""; public string[] differential = new string[0]; public string procedure = ""; public string urgency = ""; }
    [Serializable] public sealed class EncounterItem { public string id, label, finding, result; public bool abnormal; }
    [Serializable] public sealed class EncounterState
    {
        public string encounterId, phase, patientId, patientName, speakerName, speaker;
        public string patientSex, speakerSex;
        public int patientAge, speakerAge;
        public int version, elapsedSeconds;
        public EncounterItem[] historyAsked, exams, tests;
        public EncounterAssessment assessment;
    }
    [Serializable] public sealed class EncounterCarryoverItem
    {
        public string flagId, type, severity, label, detail, status;
        public string[] historyTopics, testIds, stepIds;
    }
    [Serializable] public sealed class EncounterFoundItem { public string kind, id, label, why; }
    [Serializable] public sealed class EncounterScoreSection { public string id, label; public int score, max; public string[] found, missed; }
    [Serializable] public sealed class EncounterScore
    {
        public int total, max;
        public string patientId, patientName, urgency, site, grade, spoken, diagnosisGiven, diagnosisExpected, diagnosisResult, procedureId, procedureTitle;
        public bool procedureChosenCorrectly;
        public string[] feedback;
        public string[] differentialNamed, differentialSuggestions;
        public EncounterCarryoverItem[] carryoverItems;
        public EncounterFoundItem[] criticalFound, criticalMissed;
        public EncounterScoreSection[] sections;
    }
    [Serializable] public sealed class EncounterError { public string code, message; }
    [Serializable] public sealed class EncounterReply
    {
        public string encounterId, patientName, speakerName, speaker, voiceId, patientPrompt, patientFirstMessage;
        public string attendingPrompt, attendingFirstMessage, result, display;
        // GET /patients and GET /patients/:id/case metadata; the office never reconstructs a clinical case locally.
        public PatientListEntry[] patients;
        public string patientId, procedureId, status, statusReason;
        public PatientSummary patient;
        public PreopBrief brief;
        public EncounterState state;
        public EncounterScore scorecard;
        public EncounterError error;
    }
    [Serializable] public sealed class PatientRequest { public string patientId; }
    [Serializable] public sealed class TopicRequest { public string topic; }
    [Serializable] public sealed class ExamRequest { public string maneuver; }
    [Serializable] public sealed class TestRequest { public string test; }
    [Serializable] public sealed class TranscriptRequest { public string speaker, text; }

    public static class EncounterContract
    {
        public const string FemalePatientId = "patient-demo-multi-source", MalePatientId = "patient-demo-sparse";
        public static bool ValidPatientId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
            foreach (char c in id) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return false;
            return true;
        }
        public static readonly string[] History = { "chief_complaint", "onset", "location", "migration", "character", "severity", "aggravating_relieving", "nausea_vomiting", "appetite", "fever", "bowel", "urinary", "menstrual_pregnancy", "last_meal", "past_medical", "past_surgical", "medications", "allergies", "social", "family", "recent_illness" };
        public static readonly string[] Exams = { "general_appearance", "vitals", "abdomen_inspection", "abdomen_palpation", "mcburney_point", "rebound", "guarding_rigidity", "rovsing", "psoas", "obturator", "murphy", "cva_tenderness", "chest_lungs", "genitourinary", "pelvic" };
        public static readonly string[] Tests = { "cbc", "crp", "bmp", "lactate", "lipase", "lfts", "urinalysis", "pregnancy_test", "ultrasound", "ct_abdomen_pelvis", "type_and_screen" };
        public static bool ToolAllowed(string role, string phase, string tool) =>
            role == "patient" && phase == "interview" && (tool == "answer" || tool == "examine" || tool == "order_test") ||
            role == "attending" && (tool == "get_encounter_summary" || phase == "attending" && tool == "record_assessment");
        public static bool HasError(EncounterReply reply) => reply?.error != null && (!string.IsNullOrEmpty(reply.error.code) || !string.IsNullOrEmpty(reply.error.message));
        public static bool StateMatches(EncounterState state, string id, string patient, int version) =>
            state != null && state.encounterId == id && state.patientId == patient && state.version >= version;
        public static string Label(string id)
        {
            switch(id)
            {
                case "chief_complaint": return "Main concern";
                case "aggravating_relieving": return "What changes the pain";
                case "nausea_vomiting": return "Nausea and vomiting";
                case "menstrual_pregnancy": return "Periods and pregnancy";
                case "last_meal": return "Last food or drink";
                case "past_medical": return "Medical history";
                case "past_surgical": return "Surgical history";
                case "cbc": return "CBC";
                case "crp": return "CRP";
                case "bmp": return "Metabolic panel";
                case "ct_abdomen_pelvis": return "CT abdomen and pelvis";
                case "mcburney_point": return "McBurney point";
                case "cva_tenderness": return "CVA tenderness";
                default: return (id??"").Replace('_',' ');
            }
        }
        public static string Chart(EncounterState state)
        {
            if (state == null) return "Start a synthetic encounter. Findings appear only after examination or test orders.";
            var text = new StringBuilder();
            text.AppendLine(state.patientName + " | " + state.phase + " | synthetic teaching case");
            text.Append("Asked: ");
            if (state.historyAsked == null || state.historyAsked.Length == 0) text.Append("none");
            else foreach (var item in state.historyAsked) text.Append(item.label + "; ");
            text.AppendLine("\nEXAMINATION");
            if (state.exams == null || state.exams.Length == 0) text.AppendLine("No examinations performed.");
            else foreach (var item in state.exams) text.AppendLine(item.label + ": " + item.finding);
            text.AppendLine("TEST RESULTS");
            if (state.tests == null || state.tests.Length == 0) text.AppendLine("No tests ordered.");
            else foreach (var item in state.tests) text.AppendLine(item.label + ": " + item.result);
            return text.ToString();
        }
    }
}
