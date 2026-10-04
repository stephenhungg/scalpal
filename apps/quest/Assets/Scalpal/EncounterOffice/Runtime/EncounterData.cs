using System;
using System.Text;
using Scalpal.Exercises.Data;
using Scalpal.Voice;
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
    // Choice-based office interview (/interviews, services/preop/src/interview.ts). Grades never reach the client
    // before the scorecard.
    [Serializable] public sealed class InterviewChoiceView { public string key, text; }
    [Serializable] public sealed class InterviewRoundView { public string roundId, stage, prompt; public int number, of; public InterviewChoiceView[] choices; }
    [Serializable] public sealed class InterviewFinding { public string label, text; public bool abnormal; }
    [Serializable] public sealed class InterviewPatientCue { public string clinicianMove, direction, closing; }
    [Serializable] public sealed class InterviewPick { public string roundId, key, grade, via, heard; }
    [Serializable] public sealed class InterviewPickedChoice { public string key, text, grade, feedback; }
    [Serializable] public sealed class InterviewRoundResult { public string roundId, stage, prompt; public InterviewPickedChoice picked, best; public float points; public int max; }
    [Serializable] public sealed class InterviewStateView { public string interviewId, phase, patientId, patientName; }
    [Serializable] public sealed class InterviewReply
    {
        public string interviewId, phase, patientId, patientName, speakerName, speaker, openingLine, heard;
        public string patientSex, speakerSex;
        public int patientAge, speakerAge;
        public InterviewRoundView round, next;
        public InterviewChoiceView choice;
        public InterviewPick pick;
        public InterviewFinding finding;
        public InterviewPatientCue patient;
        public InterviewStateView state;
        public EncounterScore scorecard;
        public EncounterError error;
    }
    [Serializable] public sealed class InterviewAnswerKey { public string key; }
    [Serializable] public sealed class InterviewAnswerAudio { public string audio, mimeType; }
    [Serializable] public sealed class EncounterScore
    {
        // kind "interview" for the choice-based office; rounds carry each pick against the best move.
        public string kind;
        public InterviewRoundResult[] rounds;
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
        public const string AttendingLabel = "Scalpal · Attending", LearnerLabel = "You";
        // The patient agent and Jarvis are separate voices; every line names who actually said it.
        public static string SpeakerLabel(EncounterState state, string role)
        {
            if (role == "attending") return AttendingLabel;
            if (state == null) return "Patient";
            string patientName = string.IsNullOrWhiteSpace(state.patientName) ? "the patient" : state.patientName.Trim();
            if (state.speaker == "parent")
                return (string.IsNullOrWhiteSpace(state.speakerName) ? "Parent" : state.speakerName.Trim()) + " · Parent of " + patientName.Split(' ')[0];
            return (string.IsNullOrWhiteSpace(state.patientName) ? "Patient" : patientName) + " · Patient";
        }
        public static bool ValidOfficeId(string id) => QuestJarvisVoice.ValidEncounterId(id) || QuestJarvisVoice.ValidInterviewId(id);
        public static bool IsInterview(string id) => QuestJarvisVoice.ValidInterviewId(id);
        // The service route for an office result: /interviews/:id for the choice interview, /encounters/:id before it.
        public static string OfficePath(string id) => (IsInterview(id) ? "/interviews/" : "/encounters/") + Uri.EscapeDataString(id ?? "");
        // Reads GET /interviews/:id or /interviews/:id/score into the encounter reply the OR handoff checks. The
        // interview has no free-text assessment or version; state carries only identity and phase.
        public static EncounterReply ReadOffice(string json, string id)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var reply = JsonUtility.FromJson<EncounterReply>(json);
            if (reply == null || !IsInterview(id)) return reply;
            var interview = JsonUtility.FromJson<InterviewReply>(json);
            reply.patient = null;
            if (!string.IsNullOrEmpty(interview.interviewId))
                reply.state = new EncounterState { encounterId = interview.interviewId, phase = interview.phase, patientId = interview.patientId, patientName = interview.patientName };
            else reply.state = null;
            return reply;
        }
        // The learner's plan and diagnosis picks from an interview scorecard, for the theatre challenge.
        public static string Picked(EncounterScore score, string stage)
        {
            if (score?.rounds == null) return "";
            foreach (var round in score.rounds) if (round != null && round.stage == stage && round.picked != null) return round.picked.text ?? "";
            return "";
        }
        public static string Best(EncounterScore score, string stage)
        {
            if (score?.rounds == null) return "";
            foreach (var round in score.rounds) if (round != null && round.stage == stage && round.best != null) return round.best.text ?? "";
            return "";
        }
        // Skip to surgery: the run context for the OR without an interview. Not a scorecard: kind "skipped", diagnosis
        // "skipped", no total or grade. Carryover risks are the case brief's chart flags, so Time-Out still reviews them.
        public static EncounterScore SkippedRunContext(SurgicalCase kase)
        {
            if (kase == null || string.IsNullOrEmpty(kase.procedureId)) throw new ArgumentException("Skipping to surgery needs the patient's case.");
            return new EncounterScore
            {
                kind = "skipped", diagnosisResult = "skipped", patientId = kase.patientId, patientName = kase.patient?.name ?? kase.patient?.displayLabel ?? "",
                procedureId = kase.procedureId, procedureTitle = kase.procedure?.title ?? kase.procedureId, procedureChosenCorrectly = true,
                urgency = kase.urgency ?? "", site = Site(kase.procedureId), carryoverItems = ChartRisks(kase.brief), rounds = new InterviewRoundResult[0],
                sections = new EncounterScoreSection[0], feedback = new string[0], spoken = ""
            };
        }
        public static bool IsSkipped(EncounterScore score) => score?.kind == "skipped";
        // Chart risk flags from the case brief, as Time-Out carryover items ("chart context": nobody elicited them).
        public static EncounterCarryoverItem[] ChartRisks(PreopBrief brief)
        {
            var flags = brief?.flags ?? new RiskFlag[0];
            var items = new System.Collections.Generic.List<EncounterCarryoverItem>();
            foreach (var flag in flags)
                if (flag != null && !string.IsNullOrEmpty(flag.type))
                    items.Add(new EncounterCarryoverItem { flagId = flag.id, type = flag.type, severity = flag.severity, label = flag.title, detail = flag.detail, status = "chart",
                        historyTopics = new string[0], testIds = new string[0], stepIds = new string[0] });
            return items.ToArray();
        }
        // Mirrors procedureSite in services/preop/src/encounter-carryover.ts for runs that never reach the interview scorer.
        public static string Site(string procedureId)
        {
            switch (procedureId)
            {
                case "open_appendectomy": return "Abdomen — open appendix incision / right lower quadrant";
                case "lap_appendectomy": return "Abdomen — appendix / right lower quadrant";
                case "lap_cholecystectomy": return "Abdomen — gallbladder / right upper quadrant";
                case "lap_sigmoid_colectomy": return "Abdomen — sigmoid colon / left lower quadrant";
                default: return "Site unavailable — confirm before proceeding";
            }
        }
        // One line for cards and the recap: the interview score, or that the learner skipped it.
        public static string ReasoningLine(EncounterScore score) => IsSkipped(score) ? "Clinical reasoning: Skipped" : "Clinical reasoning: " + score.total + "/100 · " + score.grade;
        // The compact scorecard card after the last round (on screen only): up to three key feedback lines, then each
        // round the learner missed or half-got with the right answer. Total and grade go in the card title.
        public static string CompactScore(EncounterScore score)
        {
            if (score == null) return "";
            var text = new StringBuilder();
            int lines = 0;
            if (score.feedback != null)
                foreach (var line in score.feedback)
                    if (lines < 3 && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("Missed:", StringComparison.Ordinal) && !line.StartsWith("Close:", StringComparison.Ordinal))
                    { text.AppendLine(line.Trim()); lines++; }
            if (score.rounds != null)
                foreach (var round in score.rounds)
                    if (round?.picked != null && round.picked.grade != "correct")
                        text.AppendLine((round.picked.grade == "partial" ? "Close · " : "Missed · ") + round.prompt + " Right answer: " + round.best?.text);
            return text.ToString().TrimEnd();
        }
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
