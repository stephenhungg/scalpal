using System;
using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    public sealed class EncounterOfficePanel : MonoBehaviour
    {
        public NativeEncounterSession session;
        // The diagnosis room is minimal: during the interview the shared Shell DialogueBox is the only panel (patient
        // line, "Result:" line, round prompt and choices). This panel is only the patient picker when no patient is
        // selected; voice and recenter live in the pause menu, the scorecard and theatre in the handoff card.
        public TextMeshPro heading, status, chart, draft;
        public EncounterOfficeButton[] options;
        public EncounterOfficeButton microphoneMode, surgery, suggestions;
        public Transform keyboard, assessment;
        public string Page { get; private set; } = "patients";
        public int Offset { get; private set; }
        string lastPatientId = "";
        // Only the patient picker remains: list paging and reload. Everything else (question pages, Present to Jarvis,
        // assessment, keyboard, open mic, voice/stop/refresh, findings paging) is retired.
        public static bool Retired(string command, string argument) =>
            !(command == "reload_patients" || command == "option" || command == "next" || command == "previous" || command == "page" && argument == "patients");
        public Transform PickerConsole => heading ? heading.transform.parent : null;
        public Transform FindingsConsole => chart ? chart.transform.parent : null;
        void OnEnable() { if (session) session.Changed += Refresh; Refresh(); }
        void OnDisable() { if (session) session.Changed -= Refresh; }
        public void Act(string command, string argument)
        {
            if (Retired(command, argument)) return;
            switch (command)
            {
                case "reload_patients": session.LoadPatients(); Page = "patients"; Offset = 0; break;
                case "page": Page = argument; Offset = 0; if (session.Patients.Length == 0) session.LoadPatients(); break;
                case "next": Offset += options.Length; break;
                case "previous": Offset = Mathf.Max(0, Offset - options.Length); break;
                case "option":
                    if (Page != "patients") break;
                    var entry = Array.Find(session.Patients, item => item != null && item.patientId == argument);
                    if (EncounterOfficeRoute.CanEnter(entry)) { session.StartPatient(argument); Page = "interview"; Offset = 0; }
                    break;
            }
            Refresh();
        }
        public void Refresh()
        {
            if (!session) return;
            if (lastPatientId != session.SelectedPatientId) { lastPatientId = session.SelectedPatientId; Page = string.IsNullOrEmpty(lastPatientId) ? "patients" : "interview"; Offset = 0; }
            // One panel during the interview: the dialogue box. The picker shows only while no patient is in the room.
            bool picking = session.State == null;
            if (PickerConsole) PickerConsole.gameObject.SetActive(picking);
            if (FindingsConsole) FindingsConsole.gameObject.SetActive(false);
            if (assessment) assessment.gameObject.SetActive(false);
            if (keyboard) keyboard.gameObject.SetActive(false);
            if (session.patient && session.patient.stateLabel) session.patient.stateLabel.gameObject.SetActive(false);
            if (!picking) return;
            if (heading) heading.text = "Scalpal\nChoose a patient";
            if (status) status.text = Wrap((session.Busy ? "Working… " : "") + session.Status, 42);
            foreach (var button in GetComponentsInChildren<EncounterOfficeButton>(true))
                if (Retired(button.command, button.argument)) button.gameObject.SetActive(false);
            if (Page != "patients") Page = "patients";
            string[] items = Array.ConvertAll(session.Patients, item => item?.patientId ?? "");
            if (Offset >= items.Length) Offset = 0;
            for (int i = 0; i < options.Length; i++)
            {
                var button = options[i]; int index = i + Offset; button.gameObject.SetActive(index < items.Length);
                if (index >= items.Length) continue;
                button.argument = items[index];
                var entry = session.Patients[index];
                button.enabledAction = EncounterOfficeRoute.CanEnter(entry);
                if (button.label) { button.label.text = (entry?.displayLabel ?? entry?.title ?? "Connect patient") + (button.enabledAction ? "" : " · unavailable"); button.label.GetComponent<ScalpalTextFit>()?.Fit(); }
            }
            foreach (var fit in GetComponentsInChildren<ScalpalTextFit>(true)) fit.Fit();
        }
        // Each line is labelled with whoever actually said it.
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
