using System;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEngine;

namespace Scalpal.Anatomy
{
    // Optional desktop harness for inspecting the real case-to-anatomy wiring.
    // It simulates inputs explicitly; it never establishes participant registration.
    public sealed class AnatomyDemoPanel : MonoBehaviour
    {
        public AnatomyExerciseBinding exercise;
        public TextAsset caseBundle;
#if UNITY_EDITOR || (!UNITY_ANDROID && !UNITY_IOS)
        ScalpalBundle bundle;
        string feedback = "Choose the adult appendectomy case first. Inputs here are simulated.";
        Vector2 scroll;
        void Start()
        {
            if (caseBundle == null) { feedback = "Missing packaged case bundle."; return; }
            try { bundle = JsonUtility.FromJson<ScalpalBundle>(caseBundle.text); }
            catch (Exception error) { feedback = "Could not read case bundle: " + error.Message; }
        }
        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, Math.Min(420f, Screen.width - 24f), Math.Max(100f, Screen.height - 24f)), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("Scalpal | desktop exercise integration");
            GUILayout.Label("Simulated inputs and registration. Not headset validation.");
            if (exercise == null || exercise.anatomy == null || bundle == null)
            {
                GUILayout.Label(feedback);
                GUILayout.EndScrollView(); GUILayout.EndArea(); return;
            }
            var caseIds = new[] { "case_patient-demo-multi-source_lap_appendectomy",
                "case_patient-demo-001_lap_cholecystectomy", "case_patient-demo-messy-coding_lap_sigmoid_colectomy" };
            var labels = new[] { "Review and start appendectomy (first demo)", "Start gallbladder case", "Review and start colectomy" };
            GUILayout.Label("Review-required cases below are synthetic fixtures. Starting acknowledges that review for this local demo.");
            for (int i = 0; i < caseIds.Length; i++)
                if (GUILayout.Button(labels[i]))
                {
                    exercise.anatomy.SetRegistrationValid(false);
                    exercise.requireCoachSynchronization = false;
                    feedback = exercise.SelectCase(bundle, caseIds[i], true, out var reason) ? "Case loaded. Enable simulated registration to send inputs." : reason;
                }
            if (exercise.SelectedCase != null)
            {
                if (GUILayout.Button(exercise.anatomy.RegistrationValid ? "Simulate tracking loss" : "Simulate valid registration"))
                    exercise.anatomy.SetRegistrationValid(!exercise.anatomy.RegistrationValid);
                var step = exercise.Current;
                GUILayout.Label(exercise.CanScore ? "Input enabled" : "Input paused");
                if (exercise.Completed) GUILayout.Label("Authored case completed. No tissue cutting or physiology was simulated.");
                else if (step != null)
                {
                    GUILayout.Label(step.title);
                    GUILayout.Label(step.instruction);
                    GUILayout.Label("Selected tool: " + exercise.SelectedInstrumentId);
                    foreach (var instrument in exercise.SelectedCase.instruments ?? Array.Empty<Instrument>())
                        if (GUILayout.Button("Select " + instrument.displayName))
                            feedback = exercise.SelectInstrument(instrument.id) ? "Tool selected." : "Tool unavailable.";
                    if (step.check.type == "place_ports")
                        foreach (var port in step.check.targets ?? Array.Empty<string>())
                            if (GUILayout.Button("Simulate port placement: " + port)) Send(CaseEvent.PlacePort(port));
                    if (step.check.type == "confirm" && GUILayout.Button("Confirm step")) Send(CaseEvent.Confirm());
                    foreach (var part in exercise.anatomy.Parts)
                    {
                        if (!part.IsVisible) continue;
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("Highlight " + part.displayName))
                            feedback = exercise.anatomy.Highlight(part.stableId) ? part.displayName : "Highlight unavailable.";
                        if (GUILayout.Button("Identify")) Send(CaseEvent.Identify(part.stableId));
                        if (GUILayout.Button("Simulate contact"))
                        {
                            var collider = part.GetComponent<Collider>();
                            feedback = exercise.TouchCollider(collider, out var result, out var reason)
                                ? Describe(result) : reason;
                        }
                        GUILayout.EndHorizontal();
                    }
                }
            }
            GUILayout.Label(feedback);
            GUILayout.EndScrollView(); GUILayout.EndArea();
        }
        void Send(CaseEvent input)
        {
            feedback = exercise.Submit(input, out var result, out var reason) ? Describe(result) : reason;
        }
        static string Describe(CaseResult result) => result.mistake != null ? result.mistake.feedback :
            result.completed ? "Case completed." : result.advanced ? "Step advanced." : "Input handled; step requirements remain.";
#endif
    }
}
