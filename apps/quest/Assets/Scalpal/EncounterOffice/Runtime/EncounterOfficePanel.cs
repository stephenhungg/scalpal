using System;
using System.Collections.Generic;
using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    public sealed class EncounterOfficePanel : MonoBehaviour
    {
        public NativeEncounterSession session;
        // The conversation itself is shown by the shared Shell DialogueBox; these panels hold controls and findings.
        public TextMeshPro heading, status, chart, draft;
        public EncounterOfficeButton[] options;
        public EncounterOfficeButton microphoneMode, surgery, suggestions;
        // History questions are suggestions for a voice-first interview: collapsed until asked for.
        public bool SuggestionsOpen { get; private set; }
        public Transform keyboard, assessment;
        public string Page { get; private set; } = "patients";
        public int Offset { get; private set; }
        public string Field { get; private set; } = "diagnosis";
        int chartOffset, draftOffset;
        string lastDraftValue;
        bool feedbackVisible = true;
        string lastPatientId = "";
        EncounterScore lastScore;
        EncounterOfficeButton voiceToggle;
        readonly string[] diagnoses = { "Acute cholecystitis", "Symptomatic cholelithiasis", "Sigmoid diverticulitis", "Acute appendicitis", "Perforated appendicitis", "Gastroenteritis", "Ectopic pregnancy", "Ovarian torsion", "Urinary tract infection", "Diverticulitis", "Small bowel obstruction", "Mesenteric ischemia", "Ureteric stone", "Perforated peptic ulcer", "Crohn's disease" };
        readonly string[] plans = { "Laparoscopic cholecystectomy", "Laparoscopic sigmoid colectomy", "Laparoscopic appendectomy", "Appendectomy and washout", "Fluids and antibiotics before surgery", "Observation and reassessment", "Further diagnostic evaluation", "Nonoperative treatment" };
        readonly string[] timing = { "Emergency / immediately", "Urgent / within hours", "Elective / outpatient" };
        void OnEnable() { if (session) session.Changed += Refresh; Refresh(); }
        void OnDisable() { if (session) session.Changed -= Refresh; }
        public void Act(string command, string argument)
        {
            switch (command)
            {
                case "patient": session.StartPatient(argument); Page = "history"; Offset = 0; chartOffset = 0; break;
                case "reload_patients":
                case "patients": session.LoadPatients(); Page = "patients"; Offset = 0; break;
                case "surgery": session.ContinueToSurgery(); break;
                case "page": Page = argument; Offset = 0; SuggestionsOpen = false; if (Page == "patients" && session.Patients.Length == 0) session.LoadPatients(); break;
                case "next": Offset += options.Length; break;
                case "previous": Offset = Mathf.Max(0, Offset - options.Length); break;
                case "suggestions": SuggestionsOpen = !SuggestionsOpen; Offset = 0; break;
                case "chart_toggle": feedbackVisible = !feedbackVisible; chartOffset = 0; break;
                case "chart_next": chartOffset += 10; break;
                case "chart_previous": chartOffset = Mathf.Max(0, chartOffset - 10); break;
                case "voice": session.ToggleVoice(); break;
                case "stop": session.StopVoice(); break;
                case "mic_mode": session.ToggleOpenMicrophone(); break;
                case "refresh": session.RefreshState(); break;
                case "attending": session.SeeAttending(); Page = "assessment"; Offset = 0; break;
                case "submit": session.SubmitAssessment(); break;
                case "summary": session.RequestSummary(); break;
                case "field": Field = argument; Offset = 0; draftOffset = 0; break;
                case "draft_previous": draftOffset = Mathf.Max(0,draftOffset-3); break;
                case "draft_next": draftOffset += 3; break;
                case "option": Option(argument); break;
                case "key": Edit(argument); break;
                case "keyboard": if (keyboard) keyboard.gameObject.SetActive(!keyboard.gameObject.activeSelf); break;
            }
            Refresh();
        }
        void Option(string value)
        {
            switch (Page)
            {
                case "patients":
                    var entry = Array.Find(session.Patients, item => item != null && item.patientId == value);
                    if (EncounterOfficeRoute.CanEnter(entry)) { session.StartPatient(value); Page = "history"; Offset = 0; }
                    break;
                case "history": session.Ask(value); SuggestionsOpen = false; break;
                case "exam": session.Examine(value); break;
                case "tests": session.OrderTest(value); break;
                case "assessment":
                    if (Field == "differential")
                    {
                        var values = new List<string>(session.Draft.differential ?? new string[0]);
                        if (!values.Remove(value)) values.Add(value); session.Draft.differential = values.ToArray();
                    }
                    else SetField(value);
                    break;
            }
        }
        string FieldValue() => Field == "diagnosis" ? session.Draft.diagnosis : Field == "procedure" ? session.Draft.procedure : Field == "urgency" ? session.Draft.urgency : string.Join("; ", session.Draft.differential ?? new string[0]);
        void SetField(string text)
        {
            if (Field == "diagnosis") session.Draft.diagnosis = text;
            else if (Field == "procedure") session.Draft.procedure = text;
            else if (Field == "urgency") session.Draft.urgency = text;
            else session.Draft.differential = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
        void Edit(string value)
        {
            var text = FieldValue() ?? "";
            if (value == "clear") text = "";
            else if (value == "back") text = text.Length > 0 ? text.Substring(0, text.Length - 1) : text;
            else if (text.Length < 400) text += value == "space" ? " " : value;
            SetField(text);
        }
        public void Refresh()
        {
            if (!session) return;
            if (lastPatientId != session.SelectedPatientId) { lastPatientId = session.SelectedPatientId; Page = "history"; Offset = 0; }
            if (heading) heading.text = "Scalpal\n" + (session.State?.patientName ?? "Practice a patient encounter");
            if (status) status.text = Wrap((session.Busy ? "Working… " : "") + session.Status, 42);
            if (surgery) surgery.gameObject.SetActive(session.SurgeryReady);
            if (microphoneMode && microphoneMode.label) microphoneMode.label.text = session.OpenMicrophone ? "Hold grip" : "Open mic";
            if (!voiceToggle) foreach (var button in GetComponentsInChildren<EncounterOfficeButton>(true)) if (button.command == "voice") voiceToggle = button;
            if (voiceToggle && voiceToggle.label) voiceToggle.label.text = session.VoiceEnabled && session.VoiceLive ? "Voice off" : "Voice on";
            if (lastScore != session.Score) { lastScore = session.Score; chartOffset = 0; feedbackVisible = true; }
            var chartContent = session.Score != null && feedbackVisible ? "Attending feedback · " + session.Score.total + "/" + session.Score.max + " " + session.Score.grade + "\n" + string.Join("\n", session.Score.feedback ?? new string[0]) : EncounterContract.Chart(session.State);
            var lines = Wrap(chartContent, 42).Split('\n');
            if (chartOffset >= lines.Length) chartOffset = Mathf.Max(0, lines.Length - 10);
            if (chart) chart.text = "Gathered findings · " + (chartOffset + 1) + "\n" + string.Join("\n", lines, chartOffset, Math.Min(10, lines.Length - chartOffset));
            if (draft)
            {
                string value=FieldValue();var draftLines=Wrap(string.IsNullOrWhiteSpace(value)?"Choose an option or use the keyboard.":value,42).Split('\n');
                if(lastDraftValue!=value){lastDraftValue=value;draftOffset=Mathf.Max(0,draftLines.Length-3);}
                if(draftOffset>=draftLines.Length)draftOffset=0;
                draft.text = "Assessment · " + Field + "\n" + string.Join("\n",draftLines,draftOffset,Math.Min(3,draftLines.Length-draftOffset));
                if (session.Score != null) draft.text = "Attending assessment · " + session.Score.total + "/" + session.Score.max + " " + session.Score.grade + "\nFull feedback is paged in Your findings.";
            }
            if(assessment)assessment.gameObject.SetActive(Page=="assessment"||session.State?.phase=="attending"||session.State?.phase=="scored");
            foreach(var fit in GetComponentsInChildren<ScalpalTextFit>(true))fit.Fit();
            string[] items = Page == "patients" ? Array.ConvertAll(session.Patients, item => item?.patientId ?? "") : Page == "history" ? EncounterContract.History : Page == "exam" ? EncounterContract.Exams : Page == "tests" ? EncounterContract.Tests : Field == "procedure" ? plans : Field == "urgency" ? timing : diagnoses;
            if (Offset >= items.Length) Offset = 0;
            bool collapsed = Page == "history" && !SuggestionsOpen && suggestions;
            if (suggestions) suggestions.gameObject.SetActive(collapsed);
            for (int i = 0; i < options.Length; i++)
            {
                var button = options[i]; int index = i + Offset; button.gameObject.SetActive(index < items.Length && !collapsed);
                if (index >= items.Length) continue;
                button.argument = items[index];
                var entry = Page == "patients" ? session.Patients[index] : null;
                button.enabledAction = Page != "patients" || EncounterOfficeRoute.CanEnter(entry);
                if (button.label) { button.label.text = Page == "patients" ? (entry?.displayLabel ?? entry?.title ?? "Connect patient") + (button.enabledAction ? "" : " · unavailable") : EncounterContract.Label(items[index]);button.label.GetComponent<ScalpalTextFit>()?.Fit(); }
            }
        }
        // Each line is labelled with whoever actually said it; the patient and Jarvis are never one blended voice.
        public static string ResponseHeader(string speaker, int offset) =>
            (string.IsNullOrWhiteSpace(speaker) ? "Waiting for the patient" : speaker) + " · " + (offset + 1);
        public static string Wrap(string text, int width)
        {
            var output = new System.Text.StringBuilder();
            foreach (var line in (text ?? "").Split('\n'))
            {
                int column = 0;
                foreach (var word in line.Split(' '))
                {
                    if (column > 0 && column + word.Length + 1 > width) { output.Append('\n'); column = 0; }
                    if (column > 0) { output.Append(' '); column++; }
                    output.Append(word); column += word.Length;
                }
                output.Append('\n');
            }
            return output.ToString().TrimEnd('\n');
        }
    }
}
