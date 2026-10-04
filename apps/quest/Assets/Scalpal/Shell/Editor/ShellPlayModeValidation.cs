using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Scalpal.EncounterOffice;
using Scalpal.Realtime;
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
        const string RateLimited = "patient-demo-rate-limited";
        static int rateLimitedCases, retryBaseline; // Play Mode only; no domain reload happens between these stages.
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static ShellPlayModeValidation()
        {
            EditorApplication.update += Tick;
            SceneManager.sceneLoaded += IsolateRealtimeForComponentTest;
        }

        static void IsolateRealtimeForComponentTest(Scene scene, LoadSceneMode mode)
        {
            if (!SessionState.GetBool(Prefix + "active", false) || !Application.isPlaying || scene.name != "DiagnosisOffice") return;
            // sceneLoaded runs after OnEnable and before Start. This gate isolates the shell's
            // actual service HTTP handoff; invite pairing belongs to the separate realtime gate.
            var bridges = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<QuestSessionBridge>(true)).ToArray();
            foreach (var bridge in bridges) { bridge.autoConnect = false; bridge.enabled = false; }
            var offices = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<NativeEncounterSession>(true)).ToArray();
            if (offices.Length == 0) throw new InvalidOperationException("HTTP scene fixture has no office session.");
            foreach (var office in offices)
            {
                if (office.realtime) { office.realtime.autoConnect = false; office.realtime.enabled = false; }
                office.realtime = null;
            }
            SessionState.SetBool(Prefix + "httpOnlyOffice", true);
            UnityEngine.Debug.Log("SCALPAL_SHELL_PLAY_FIXTURE isolatedRealtime=true scene=DiagnosisOffice disabledBridges=" + bridges.Length);
        }

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Start the shell play-mode gate from Edit Mode.");
            Process server = null;
            try
            {
                string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
                string service = Path.Combine(repo, "services/preop");
                var info = new ProcessStartInfo("/usr/bin/env", "node --import " + Quote(Path.Combine(service, "node_modules/tsx/dist/loader.mjs")) + " " + Quote(Path.Combine(repo, "scripts/quest/shell-check/server.ts")))
                { WorkingDirectory = service, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                server = Process.Start(info);
                var ready = server.StandardOutput.ReadLineAsync();
                if (!ready.Wait(15000)) throw new InvalidOperationException("Shell play-mode service fixture did not become ready.");
                const string marker = "SCALPAL_SHELL_TEST_ENDPOINT=";
                string line = ready.Result ?? "";
                if (!line.StartsWith(marker, StringComparison.Ordinal)) throw new InvalidOperationException("Shell play-mode fixture failed startup.");
                string endpoint = line.Substring(marker.Length);
                if (new Uri(endpoint).Host != "127.0.0.1") throw new InvalidOperationException("Shell play-mode fixture must be isolated loopback.");
                SessionState.SetInt(Prefix + "pid", server.Id);
                SessionState.SetString(Prefix + "endpoint", endpoint);
                SessionState.SetInt(Prefix + "checks", 0);
                SessionState.SetInt(Prefix + "result", 1);
                SessionState.SetBool(Prefix + "httpOnlyOffice", false);
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
                        if (!hub || hub.Model.Patients.Length != 9 || hub.Model.CaseFor(Female) == null || hub.service.IsOffline) return;
                        Check(hub.service.BaseUrl == endpoint && hub.Model.UnavailableCount == 1 && hub.Model.Patients.All(patient => !string.IsNullOrEmpty(patient.patientId) && patient.status != "blocked"), "live runtime catalog keeps nine selectable records with cached metadata and one unavailable record");
                        Check(hub.Model.VisiblePatients().Length == 7 && Cards(hub).Length == 7 && !hub.content.GetComponentsInChildren<Transform>(true).Any(item => item.name == "Filters"), "live explore shows seven playable cards and no filter bar");
                        // Controlled variant of the live catalog: the rate-limited record with an authored interview.
                        var catalog = JsonUtility.FromJson<Scalpal.Exercises.Data.PatientList>(JsonUtility.ToJson(new Scalpal.Exercises.Data.PatientList { patients = hub.Model.Patients }));
                        catalog.patients.Single(patient => patient.patientId == RateLimited).encounterAvailable = true;
                        rateLimitedCases = 0; hub.service.CaseLoaded += value => { if (value?.patientId == RateLimited) rateLimitedCases++; };
                        hub.PatientsLoaded(catalog);
                        Check(hub.Model.Recoverable(hub.Model.Patients.Single(patient => patient.patientId == RateLimited)) && !hub.Select(RateLimited), "recoverable rate-limited record without a procedure is hidden but retried in the background");
                        Stage("retry"); break;
                    case "retry":
                        if (!hub || hub.Remaining(RateLimited) <= 0 || PendingCount(hub) != 0 || rateLimitedCases < 1) return;
                        Check(hub.Model.Patients.Single(patient => patient.patientId == RateLimited).status == "retry", "background case request (no Refresh press) returns the actual HTTP200 retry state and its Retry-After cooldown");
                        hub.ServiceRetries();
                        Check(PendingCount(hub) == 0, "background retry starts no request during the service-provided cooldown");
                        retryBaseline = rateLimitedCases;
                        Stage("retryAgain"); break;
                    case "retryAgain":
                        if (!hub || (PendingCount(hub) == 0 && rateLimitedCases <= retryBaseline)) return;
                        Check(rateLimitedCases > retryBaseline || hub.Remaining(RateLimited) <= 0, "background retry fires again on its own after Retry-After elapses");
                        hub.Reload();
                        Stage("reloaded"); break;
                    case "reloaded":
                        if (!hub || hub.Model.Patients.Length != 9 || hub.Model.CaseFor(Female) == null || PendingCount(hub) != 0 || hub.service.IsOffline) return;
                        Check(!hub.Model.Recoverable(hub.Model.Patients.Single(patient => patient.patientId == RateLimited)), "the live catalog's rate-limited record without an interview is not retried");
                        var row = hub.Model.Patients.Single(patient => patient.patientId == Female);
                        Cards(hub).Single(button => button.name == "Patient_" + row.scenarioId).Press();
                        Check(hub.Model.SelectedPatientId == Female && !hub.Transitioning, "one patient-card press selects detail without scene activation");
                        Stage("detail"); break;
                    case "detail":
                        if (!hub || hub.Model.DetailLoading) return;
                        Check(hub.Model.SelectedBrief?.patientId == Female && hub.CanBegin && hub.BeginButton && hub.BeginButton.interactable && hub.SkipButton && hub.SkipButton.interactable, "live matching brief enables explicit Begin and Skip to surgery");
                        Check(!hub.content.GetComponentsInChildren<TMPro.TextMeshPro>().Any(text => text.name == "Availability"), "an enabled Begin shows no availability message");
                        hub.BeginButton.Press();
                        Check(hub.Transitioning && ShellTransition.Busy, "actual Begin button starts async fade and scene handoff");
                        Stage("office"); break;
                    case "office":
                        if (SceneManager.GetActiveScene().name != "DiagnosisOffice" || ShellTransition.Busy) return;
                        var office = UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
                        if (!office || office.State == null) return;
                        Check(SessionState.GetBool(Prefix + "httpOnlyOffice", false) && office.realtime == null && UnityEngine.Object.FindObjectsByType<QuestSessionBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None).All(bridge => !bridge.enabled), "component fixture disables realtime invite pairing before office Start; no realtime integration is claimed");
                        Check(string.IsNullOrEmpty(ShellTransition.LastError), "asynchronous office handoff completed without transition error");
                        Check(office.baseUrl == endpoint && office.State.patientId == Female && !string.IsNullOrEmpty(office.State.encounterId) && office.State.phase == "interview", "loaded office creates actual authoritative encounter for exact selected patient and endpoint");
                        Check(office.VoiceEnabled, "Begin keeps the patient's live voice enabled so she answers each pick");
                        Check(!ShellTransition.TryConsumeSelection(out _), "scene activation consumed the selected-patient handoff exactly once");
                        var pause = ShellPause.Instance;
                        Check(pause && !pause.IsPaused && Mathf.Approximately(Time.timeScale, 1) && !AudioListener.pause, "global pause survives hub-to-office transition in resumed state");
                        pause.Pause();
                        Check(pause.IsPaused && Mathf.Approximately(Time.timeScale, 0) && AudioListener.pause, "office pause stops simulation time and authored audio output");
                        MenuButton(pause, "Resume").Press();
                        Check(!pause.IsPaused && Mathf.Approximately(Time.timeScale, 1) && !AudioListener.pause, "actual Resume control restores simulation time and audio output");
                        pause.Pause();
                        MenuButton(pause, "Back to explore").Press();
                        Check(pause.IsPaused && !ShellTransition.Busy && SceneManager.GetActiveScene().name == "DiagnosisOffice" && Menu(pause).GetComponentsInChildren<TMPro.TextMeshPro>().Any(text => text.text == "Leave this encounter?"), "first Back press opens confirmation and cannot leave encounter");
                        MenuButton(pause, "Back to explore").Press();
                        Check(!pause.IsPaused && ShellTransition.Busy, "second confirmed Back press starts actual return transition");
                        Stage("return"); break;
                    case "return":
                        if (SceneManager.GetActiveScene().name != "Launch" || ShellTransition.Busy || !hub || !hub.Exploring) return;
                        Check(hub.Model.Selected == null && !hub.Transitioning && Mathf.Approximately(Time.timeScale, 1), "confirmed return opens fresh Explore with no prior patient selected");
                        Check(UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>() == null, "office encounter component is unloaded after Back to Explore");
                        UnityEngine.Debug.Log("SCALPAL_SHELL_PLAY_VERIFY_OK checks=" + SessionState.GetInt(Prefix + "checks", 0) + " actualAsyncSceneLoad=true actualEncounterPost=true sharedAttempt=false isolatedHttpFixture=true isolatedRealtime=true provider=false headset=false");
                        Finish(0); break;
                }
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogError("SCALPAL_SHELL_PLAY_VERIFY_FAILED stage=" + SessionState.GetString(Prefix + "stage", "") + " " + error);
                Finish(1);
            }
        }

        static ShellButton[] Cards(HubController hub) => hub.content.GetComponentsInChildren<ShellButton>().Where(button => button.name.StartsWith("Patient_", StringComparison.Ordinal)).ToArray();
        static int PendingCount(HubController hub) => ((System.Collections.IEnumerable)hub.service.GetType().GetField("pending", Private).GetValue(hub.service)).Cast<object>().Count();
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
