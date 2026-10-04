using System;
using System.IO;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.EncounterOffice.Editor
{
    [InitializeOnLoad]
    public static class EncounterOfficePlayModeValidation
    {
        const string Running = "Scalpal.OfficePlayMode.Running", Passed = "Scalpal.OfficePlayMode.Passed";
        static EncounterOfficePlayModeValidation() => EditorApplication.playModeStateChanged += Changed;
        public static void Run()
        {
            string path = Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Use scripts/quest/office-playmode-check/run.py with the isolated fixture.");
            var config = JsonUtility.FromJson<NativeCaseSession.DevelopmentConfig>(File.ReadAllText(path));
            if (config == null || string.IsNullOrEmpty(config.database) || !config.database.StartsWith("scalpal-test-office-", StringComparison.Ordinal)
                || new Uri(config.coachBaseUrl).Host != "127.0.0.1" || new Uri(config.uri).Host != "127.0.0.1")
                throw new InvalidOperationException("Office Play Mode requires a fresh loopback test configuration.");
            EncounterOfficeRoute.TakePatient(out _, out _); EncounterOfficeRoute.ClearSurgery();
            EditorSceneManager.OpenScene(EncounterOfficeBuild.ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(Running, true); SessionState.SetBool(Passed, false);
            EditorApplication.EnterPlaymode();
        }
        static void Changed(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Running, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
                new GameObject("EncounterOfficePlayModeDriver").AddComponent<EncounterOfficePlayModeDriver>();
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                bool passed = SessionState.GetBool(Passed, false); SessionState.SetBool(Running, false);
                XRInput.RestoreUnitySource(); EditorApplication.Exit(passed ? 0 : 1);
            }
        }
    }
}
