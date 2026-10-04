using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Scalpal.EncounterOffice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.Shell.Editor
{
    // Run with -batchmode -executeMethod Scalpal.Shell.Editor.ShellValidation.RunPlayMode,
    // without -quit. SessionState survives Unity's enter/leave-play domain reloads; the
    // watchdog and process cleanup always finish with an explicit editor exit status.
    [InitializeOnLoad]
    public static class ShellPlayModeValidation
    {
        const string Prefix = "ScalpalShellPlayValidation.";
        const string Female = "patient-demo-multi-source";
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static ShellPlayModeValidation() { EditorApplication.update += Tick; }

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Start the shell play-mode gate from Edit Mode.");
            Process server = null;
            try
            {
                string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
                string service = Path.Combine(repo, "services/preop");
                var info = new ProcessStartInfo("/usr/bin/env", "node --import " + Quote(Path.Combine(service, "node_modules/tsx/dist/loader.mjs")) + " " + Quote(Path.Combine(repo, "scripts/quest/native-coach-check/server.ts")))
                { WorkingDirectory = service, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                server = Process.Start(info);
                var ready = server.StandardOutput.ReadLineAsync();
                if (!ready.Wait(15000)) throw new InvalidOperationException("Shell play-mode service fixture did not become ready.");
                const string marker = "SCALPAL_COACH_TEST_ENDPOINT=";
                string line = ready.Result ?? "";
                if (!line.StartsWith(marker, StringComparison.Ordinal)) throw new InvalidOperationException("Shell play-mode fixture failed startup.");
                string endpoint = line.Substring(marker.Length);
                if (new Uri(endpoint).Host != "127.0.0.1") throw new InvalidOperationException("Shell play-mode fixture must be isolated loopback.");
                SessionState.SetInt(Prefix + "pid", server.Id);
                SessionState.SetString(Prefix + "endpoint", endpoint);
                SessionState.SetInt(Prefix + "checks", 0);
                SessionState.SetInt(Prefix + "result", 1);
                SessionState.SetString(Prefix + "deadline", DateTime.UtcNow.AddSeconds(60).Ticks.ToString());
                SessionState.SetString(Prefix + "stage", "launch");
                SessionState.SetBool(Prefix + "active", true);
                EditorSceneManager.OpenScene("Assets/Scalpal/Shell/Scenes/Launch.unity", OpenSceneMode.Single);
                server.Dispose(); server = null; // PID retained across domain reload for explicit cleanup.
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error)
            {
                if (server != null) { if (!server.HasExited) server.Kill(); server.Dispose(); }
                UnityEngine.Debug.LogException(error);
                SessionState.SetBool(Prefix + "active", false);
                CleanupServer();
                EditorApplication.Exit(1);
            }
        }

        static void Tick()
        {
            if (!SessionState.GetBool(Prefix + "active", false)) return;
            try
            {
                string stage = SessionState.GetString(Prefix + "stage", "");
                if (stage == "leaving")
                {
                    if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                    int result = SessionState.GetInt(Prefix + "result", 1);
                    CleanupServer(); SessionState.SetBool(Prefix + "active", false);
                    EditorApplication.Exit(result); return;
                }
                if (long.TryParse(SessionState.GetString(Prefix + "deadline", "0"), out long deadline) && DateTime.UtcNow.Ticks > deadline)
                    throw new TimeoutException("Shell play-mode gate exceeded 60 seconds at stage " + stage + ". " + DescribeState());
                if (!EditorApplication.isPlaying) return;
                string endpoint = SessionState.GetString(Prefix + "endpoint", "");
                var hub = UnityEngine.Object.FindFirstObjectByType<HubController>();
                switch (stage)
                {
                    case "launch":
                        // Confirm is installed at the end of Start, after endpoint/config and initial load.
                        if (!hub || hub.input.Confirm == null) return;
                        Check(!hub.Exploring && !hub.Transitioning, "fresh runtime opens Launch before explicit Start");
                        hub.service.Configure(endpoint); hub.Reload();
                        hub.content.GetComponentsInChildren<ShellButton>(true).Single(button => button.name == "Button_Start").Press();
                        Check(hub.Exploring, "actual Start button opens Explore in Play Mode");
                        Stage("catalog"); break;
                    case "catalog":
                        if (!hub || hub.Model.Patients.Length != 12 || hub.Model.CaseFor(Female) == null || hub.service.IsOffline) return;
                        Check(hub.service.BaseUrl == endpoint && hub.Model.Patients.Count(patient => !string.IsNullOrEmpty(patient.patientId)) == 10, "runtime HTTP list and bundle return all ten synthetic patients at isolated endpoint");
                        var row = hub.Model.Patients.Single(patient => patient.patientId == Female);
                        hub.content.GetComponentsInChildren<ShellButton>(true).Single(button => button.name == "Patient_" + row.scenarioId && button.gameObject.activeInHierarchy).Press();
                        Check(hub.Model.SelectedPatientId == Female && !hub.Transitioning, "one patient-card press selects detail without scene activation");
                        Stage("detail"); break;
                    case "detail":
                        if (!hub || hub.Model.DetailLoading) return;
                        Check(hub.Model.SelectedBrief?.patientId == Female && hub.CanBegin && hub.BeginButton && hub.BeginButton.interactable, "live matching brief enables explicit encounter begin");
                        hub.BeginButton.Press();
                        Check(hub.Transitioning && ShellTransition.Busy, "actual Begin button starts async fade and scene handoff");
                        Stage("office"); break;
                    case "office":
                        if (SceneManager.GetActiveScene().name != "DiagnosisOffice" || ShellTransition.Busy) return;
                        var office = UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
                        if (!office || office.State == null) return;
                        Check(string.IsNullOrEmpty(ShellTransition.LastError), "asynchronous office handoff completed without transition error");
                        Check(office.baseUrl == endpoint && office.State.patientId == Female && !string.IsNullOrEmpty(office.State.encounterId) && office.State.phase == "interview", "loaded office creates actual authoritative encounter for exact selected patient and endpoint");
                        Check(!ShellTransition.TryConsumeSelection(out _), "scene activation consumed the selected-patient handoff exactly once");
                        var pause = ShellPause.Instance;
                        Check(pause && !pause.IsPaused && Mathf.Approximately(Time.timeScale, 1) && !AudioListener.pause, "global pause survives hub-to-office transition in resumed state");
                        pause.Pause();
                        Check(pause.IsPaused && Mathf.Approximately(Time.timeScale, 0) && AudioListener.pause, "office pause stops simulation time and authored audio output");
                        MenuButton(pause, "Resume").Press();
                        Check(!pause.IsPaused && Mathf.Approximately(Time.timeScale, 1) && !AudioListener.pause, "actual Resume control restores simulation time and audio output");
                        pause.Pause();
                        MenuButton(pause, "Back to explore").Press();
                        Check(pause.IsPaused && !ShellTransition.Busy && SceneManager.GetActiveScene().name == "DiagnosisOffice" && Menu(pause).GetComponentsInChildren<TextMesh>().Any(text => text.text == "Leave this encounter?"), "first Back press opens confirmation and cannot leave encounter");
                        MenuButton(pause, "Back to explore").Press();
                        Check(!pause.IsPaused && ShellTransition.Busy, "second confirmed Back press starts actual return transition");
                        Stage("return"); break;
                    case "return":
                        if (SceneManager.GetActiveScene().name != "Launch" || ShellTransition.Busy || !hub || !hub.Exploring) return;
                        Check(hub.Model.Selected == null && !hub.Transitioning && Mathf.Approximately(Time.timeScale, 1), "confirmed return opens fresh Explore with no prior patient selected");
                        Check(UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>() == null, "office encounter component is unloaded after Back to Explore");
                        UnityEngine.Debug.Log("SCALPAL_SHELL_PLAY_VERIFY_OK checks=" + SessionState.GetInt(Prefix + "checks", 0) + " actualAsyncSceneLoad=true actualEncounterPost=true provider=false headset=false");
                        Finish(0); break;
                }
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogError("SCALPAL_SHELL_PLAY_VERIFY_FAILED stage=" + SessionState.GetString(Prefix + "stage", "") + " " + error);
                Finish(1);
            }
        }

        static Transform Menu(ShellPause pause) => (Transform)typeof(ShellPause).GetField("panel", Private).GetValue(pause);
        static ShellButton MenuButton(ShellPause pause, string label) => Menu(pause).GetComponentsInChildren<ShellButton>().Single(button => button.label && button.label.text == label);
        static string DescribeState()
        {
            var hub = UnityEngine.Object.FindFirstObjectByType<HubController>();
            var office = UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
            return "scene=" + SceneManager.GetActiveScene().name + " busy=" + ShellTransition.Busy + " transitionError=" + ShellTransition.LastError + " selected=" + hub?.Model.SelectedPatientId + " detailError=" + hub?.Model.DetailError + " officeStatus=" + office?.Status;
        }
        static void Stage(string value) => SessionState.SetString(Prefix + "stage", value);
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            SessionState.SetInt(Prefix + "checks", SessionState.GetInt(Prefix + "checks", 0) + 1);
        }
        static void Finish(int result)
        {
            SessionState.SetInt(Prefix + "result", result); Stage("leaving");
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorApplication.ExitPlaymode();
        }
        static void CleanupServer()
        {
            int pid = SessionState.GetInt(Prefix + "pid", 0); SessionState.EraseInt(Prefix + "pid");
            if (pid <= 0) return;
            try { using (var server = Process.GetProcessById(pid)) { if (!server.HasExited) server.Kill(); server.WaitForExit(5000); } }
            catch (ArgumentException) { } // Already exited.
            catch (InvalidOperationException) { }
        }
        static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    }
}
