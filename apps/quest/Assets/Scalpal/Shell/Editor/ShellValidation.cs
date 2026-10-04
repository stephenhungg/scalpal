using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Generated;
using Scalpal.Exercises.Preop;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Shell.Editor
{
    // Throws on a failed assertion so Unity -executeMethod exits unsuccessfully. These are
    // component/HTTP checks, never a substitute for the physical Quest acceptance pass.
    public static class ShellValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const string Female = "patient-demo-multi-source";
        const string Male = "patient-demo-sparse";
        static int checks;

        [MenuItem("Scalpal/Shell/Validate Launch and Explore")]
        public static void Run()
        {
            checks = ShellInputValidation.Run();
            ValidateModel();
            ValidateExchange();
            ValidateScene();
            UnityEngine.Debug.Log("SCALPAL_SHELL_VERIFY_OK checks=" + checks + " actualCoroutineHttp=true provider=false headset=false");
        }

        public static void RunPlayMode() => ShellPlayModeValidation.Run();

        static void ValidateModel()
        {
            var model = new ExplorePatientModel();
            var ready = Entry(Female, "ready", "lap_appendectomy", "urgent");
            var review = Entry(Male, "needs_review", "lap_cholecystectomy", "elective");
            var retry = Entry("patient-demo-rate-limited", "retry", "lap_appendectomy", "urgent");
            var blocked = Entry("patient-demo-consent-revoked", "blocked", "", "");
            var unsupported = Entry("patient-validation-unauthored", "ready", "lap_cholecystectomy", "urgent");
            unsupported.encounterAvailable = false;
            var pediatric = Entry("patient-demo-pediatric-asthma", "ready", "lap_appendectomy", "urgent");
            model.ApplyPatients(new PatientList { patients = new[] { blocked, review, retry, ready, unsupported, pediatric } });
            Check(model.Patients.Take(3).All(p => p.status == "ready"), "ready cases sort before review, retry and blocked");
            Check(ExplorePatientModel.StatusLabel("ready") == "Ready" && ExplorePatientModel.StatusLabel("needs_review") == "Chart has gaps" && ExplorePatientModel.StatusLabel("blocked") == "Locked" && ExplorePatientModel.StatusLabel("retry") == "Try again", "all four service statuses have explicit human-readable text");
            Check(model.Patients.Length == 5 && model.UnavailableCount == 1 && model.Patients.All(patient => patient.status != "blocked"), "blocked record is omitted from patient grid and counted once as unavailable");
            model.ApplyCase(new SurgicalCase { patientId = retry.patientId, status = "retry", procedureId = retry.procedureId, urgency = retry.urgency });
            Check(model.UnavailableCount == 1, "case refresh preserves count of previously hidden unavailable patients");
            Check(!model.Select(blocked.patientId) && !model.CanBegin, "blocked patient cannot select or begin");
            Check(!model.Select("unknown-patient") && !model.CanBegin, "unknown patient cannot select or begin");
            Check(model.Select(ready.patientId) && model.DetailLoading && !model.CanBegin, "first card selection opens loading detail without beginning");
            Check(!model.ApplyBrief(Brief(Male)) && model.DetailLoading && !model.CanBegin, "different patient's late chart cannot unlock selected card");
            Check(model.ApplyBrief(Brief(Female)) && model.CanBegin, "matching synthetic demo brief unlocks supported adult encounter");
            model.Select(Male);
            Check(model.SelectedBrief == null && !model.CanBegin, "changing selection clears prior patient's chart and begin gate");
            model.FailDetail(Female, "old request failed");
            Check(model.DetailLoading && model.DetailError.Length == 0, "late failure for old selection cannot replace current detail state");
            model.ApplyBrief(Brief(Male));
            Check(model.CanBegin, "needs_review remains selectable and begin-capable after chart review");
            model.FailDetail(Male, "Chart unavailable. Try again.");
            Check(!model.CanBegin && !model.DetailLoading && model.DetailError.Contains("Try again"), "failed detail closes begin gate with retry explanation");
            model.Select(retry.patientId); model.ApplyBrief(Brief(retry.patientId));
            Check(!model.CanBegin, "retry state never begins an encounter from an old or cached chart");
            model.Select(unsupported.patientId); model.ApplyBrief(Brief(unsupported.patientId));
            Check(!model.CanBegin && model.AvailabilityReason == "Interview coming soon", "server capability false disables begin even for a ready matching synthetic brief");
            unsupported.encounterAvailable = true;
            Check(model.CanBegin, "service-confirmed canonical subject outside the packaged demo set can begin without a fixed ID catalog");
            unsupported.encounterAvailable = false;
            model.Select(pediatric.patientId); model.ApplyBrief(Brief(pediatric.patientId));
            Check(model.CanBegin, "server capability true enables matching synthetic patient without a hardcoded adult ID gate");
            pediatric.encounterAvailable = false; model.ApplyPatients(new PatientList { patients = new[] { blocked, review, retry, ready, unsupported, pediatric } });
            Check(!model.CanBegin, "fresh catalog capability revocation immediately closes an already-loaded detail begin gate");
            pediatric.encounterAvailable = true; model.ApplyPatients(new PatientList { patients = new[] { blocked, review, retry, ready, unsupported, pediatric } });
            model.Select(pediatric.patientId); model.ApplyBrief(Brief(pediatric.patientId));
            Check(model.CanBegin, "fresh catalog capability restoration admits the matching newly selected brief");
            foreach (var id in new string[] { null, "", "patient/invalid", "patient invalid" })
            {
                var malformed = new ExplorePatientModel();
                malformed.ApplyPatients(new PatientList { patients = new[] { Entry(id, "ready", "lap_appendectomy", "urgent") } });
                malformed.Select(id); malformed.ApplyBrief(Brief(id));
                Check(!malformed.CanBegin && !EncounterContract.ValidPatientId(id) && !ShellTransition.TryStageSelection(id, "http://127.0.0.1:8787"), "malformed or missing subject cannot begin or stage despite otherwise ready synthetic capability: " + id);
            }
            model.Select(Female); var missingPatient = Brief(Female); missingPatient.patient = null;
            Check(model.ApplyBrief(missingPatient) && model.SelectedBrief == null && !model.DetailLoading && !model.CanBegin && model.DetailError.Length > 0, "matching-ID brief with missing patient demographics is handled as a visible failure and cannot begin");
            model.Select(Female); var nonsynthetic = Brief(Female); nonsynthetic.synthetic = false; model.ApplyBrief(nonsynthetic);
            Check(!model.CanBegin && model.DetailError.Length > 0, "non-synthetic detail cannot begin");
            model.Select(Female); var sandbox = Brief(Female); sandbox.dataSource = "sandbox"; model.ApplyBrief(sandbox);
            Check(model.CanBegin, "server-capable synthetic sandbox brief can begin without pretending to be a demo record");
            model.Select(Female); model.ApplyBrief(Brief(Female));
            model.SetFilters("lap_cholecystectomy");
            Check(model.VisiblePatients().Length == 2 && model.VisiblePatients().All(p => p.procedureId == "lap_cholecystectomy") && model.Selected == null, "procedure filter returns matching cards and clears hidden selection");
            model.SetFilters("", "urgent");
            Check(model.VisiblePatients().Length == 4 && model.VisiblePatients().All(p => p.urgency == "urgent"), "urgency filter returns only matching cards");
            model.SetFilters("lap_cholecystectomy", "elective");
            Check(model.VisiblePatients().Length == 1 && model.VisiblePatients()[0].patientId == Male, "procedure and urgency filters combine");
            model.SetFilters(); Check(model.VisiblePatients().Length == 5, "All restores the complete patient grid");
            model.ApplyBundle(new ScalpalBundle { patients = new[] { Entry(Female, "blocked", "", "") }, cases = Array.Empty<SurgicalCase>() });
            Check(model.Patients.Length == 5 && model.Patients.Single(p => p.patientId == Female).status == "ready", "slow bundle cannot replace fresher live patient statuses");
            model.Select(Female); model.ApplyBrief(Brief(Female)); ready.status = "blocked";
            model.ApplyPatients(new PatientList { patients = new[] { ready } });
            Check(model.Selected == null && !model.CanBegin, "catalog refresh revoking selected card clears detail and begin gate");
            model.ApplyPatients(new PatientList { patients = new[] { Entry(Female, "retry", "", "") } });
            model.ApplyCase(new SurgicalCase { patientId = Female, status = "ready", procedureId = "lap_appendectomy", urgency = "urgent" });
            model.Select(Female); model.ApplyBrief(Brief(Female));
            Check(model.CanBegin && model.Selected.procedureId == "lap_appendectomy", "successful retry adopts returned ready status and can unlock matching detail");
            var revokedEntry = model.Selected;
            model.ApplyCase(new SurgicalCase { patientId = Female, status = "blocked", statusReason = "Consent revoked" });
            Check(!model.CanBegin && model.Selected == null && model.Patients.Length == 0 && model.UnavailableCount == 1 && model.StatusReason(revokedEntry) == "Consent revoked", "retry response revoking consent removes card, clears selection and preserves actual reason");
            model.ApplyPatients(new PatientList { patients = new[] { new PatientListEntry { patientId = "", scenarioId = "connect-cancelled", status = "connect" }, new PatientListEntry { patientId = "", scenarioId = "connect-failed", status = "connect" } } });
            Check(model.Patients.Length == 0 && model.UnavailableCount == 0 && !model.Select("") && !model.CanBegin, "connection-only scenarios are omitted without counting them as patient records");
        }

        static void ValidateExchange()
        {
            Process server = null;
            GameObject fixture = null;
            try
            {
                string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
                string servicePath = Path.Combine(repo, "services/preop");
                var info = new ProcessStartInfo("/usr/bin/env", "node --import " + Quote(Path.Combine(servicePath, "node_modules/tsx/dist/loader.mjs")) + " " + Quote(Path.Combine(repo, "scripts/quest/shell-check/server.ts")))
                { WorkingDirectory = servicePath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                server = Process.Start(info);
                var ready = server.StandardOutput.ReadLineAsync();
                if (!ready.Wait(15000)) throw new InvalidOperationException("Shell service fixture did not become ready");
                const string marker = "SCALPAL_SHELL_TEST_ENDPOINT=";
                string line = ready.Result ?? "";
                Check(line.StartsWith(marker, StringComparison.Ordinal), "isolated actual service fixture started");
                string endpoint = line.Substring(marker.Length);
                Check(new Uri(endpoint).Host == "127.0.0.1", "HTTP tests use isolated loopback; fixture prohibits external provider requests");
                EditorSceneManager.OpenScene("Assets/Scalpal/Shell/Scenes/Launch.unity", OpenSceneMode.Single);
                fixture = new GameObject("ShellClientValidation");
                fixture.SetActive(false);
                var client = fixture.AddComponent<ScalpalPreopService>();
                Configure(client, endpoint);
                PatientList patients = null; ScalpalBundle bundle = null; PreopBrief brief = null; ErrorResponse error = null;
                client.PatientsLoaded += value => patients = value;
                bool bundleResponseOffline=false;
                client.BundleLoaded += value => { bundle=value;bundleResponseOffline=client.LastResponseOffline; };
                client.BriefLoaded += value => brief = value;
                client.RequestFailed += value => error = value;
                string failedRoute = "";
                client.RequestFailedForRoute += (route, value) => failedRoute = route;
                string retryRoute = ""; int retrySeconds = 0;
                client.RequestRetryAfterForRoute += (route, seconds) => { retryRoute = route; retrySeconds = seconds; };
                bool patientResponseOffline = false, briefResponseOffline = false;
                client.PatientsLoaded += value => patientResponseOffline = client.LastResponseOffline;
                client.BriefLoaded += value => briefResponseOffline = client.LastResponseOffline;
                client.LoadCachedBundle();
                Check(bundle?.cases?.Length == 11 && bundle.cases.Select(item => item.patientId).Distinct().Count() == 10 && bundleResponseOffline, "cached bundle metadata loads locally with an explicit per-response offline marker");
                bool nestedMetadataRestoredLive = false;
                Action<PatientList> nestedMetadata = value =>
                {
                    bool before = client.LastResponseOffline;
                    client.LoadCachedBundle();
                    nestedMetadataRestoredLive = !before && !client.LastResponseOffline && bundleResponseOffline;
                };
                client.PatientsLoaded += nestedMetadata;
                var getPatients = Send(client, "/patients"); getPatients.Run();
                client.PatientsLoaded -= nestedMetadata;
                Check(nestedMetadataRestoredLive && !client.IsOffline, "nested cached metadata callback reports offline locally and restores enclosing live patient response provenance");
                Check(getPatients.paths.SequenceEqual(new[] { "/patients" }) && patients?.patients?.Length == 12 && !client.IsOffline && !patientResponseOffline, "production client fetches all 12 scenario rows over actual HTTP");
                Check(patients.patients.Count(patient => patient.encounterAvailable) == 8, "actual service catalog supplies eight encounter capability flags");
                foreach (var status in new[] { "ready", "needs_review", "blocked", "retry" })
                    Check(patients.patients.Any(p => p.status == status), "actual fixture returns renderable " + status + " status");
                var getBundle = Send(client, "/unity/bundle"); getBundle.Run();
                // Case count may grow (alternate procedures per patient); every playable patient must still be covered.
                Check(getBundle.paths.SequenceEqual(new[] { "/unity/bundle" }) && bundle?.cases != null && bundle.cases.Select(item => item.patientId).Distinct().Count() == 10, "production bundle route covers all 10 patients with reused DTOs");
                var liveHub = UnityEngine.Object.FindFirstObjectByType<HubController>();
                Check(liveHub != null, "live HTTP boundary binds real generated hub controller");
                liveHub.service = client; liveHub.Initialize(); liveHub.Enter();
                liveHub.BundleLoaded(bundle); liveHub.PatientsLoaded(patients);
                typeof(HubController).GetMethod("Wire", Private).Invoke(liveHub, null);
                var model = liveHub.Model;
                Check(model.Patients.Length == 9 && model.UnavailableCount == 1, "actual catalog shows nine available patient cards plus one unavailable patient, excluding connection scenarios");
                Check(model.Select(Female), "live selected patient opens detail");
                var getBrief = Send(client, "/patients/" + Female + "/brief"); getBrief.Run();
                Check(getBrief.paths.Single() == "/patients/" + Female + "/brief" && brief?.patientId == Female && brief.synthetic && brief.chart.Length > 0, "real brief carries matching synthetic identity and actual chart highlights");
                Check(model.CanBegin && model.Name(model.Selected) == "Priya Ramaswamy" && liveHub.BeginButton && liveHub.BeginButton.interactable, "actual live brief callback renders supported adult detail and enables explicit Begin");
                Check(ShellTransition.TryStageSelection(model.SelectedPatientId, client.BaseUrl) && ShellTransition.TryConsumeSelection(out var liveSelection) && liveSelection.patientId == Female && liveSelection.serviceUrl == endpoint, "live service UI selection stages the exact production office handoff identity");
                var available = patients.patients.Where(patient => (patient.status == "ready" || patient.status == "needs_review") && patient.encounterAvailable).ToArray();
                Check(available.Length == 8, "live HTTP catalog exposes all eight service-authorized subjects for the explicit shell handoff");
                foreach (var patient in available)
                {
                    Check(model.Select(patient.patientId), "every available live canonical subject can select detail: " + patient.patientId);
                    Send(client, "/patients/" + patient.patientId + "/brief").Run();
                    Check(model.CanBegin && model.SelectedBrief?.patientId == patient.patientId && liveHub.BeginButton.interactable, "matching real synthetic brief enables all available authored interviews: " + patient.patientId);
                    Check(ShellTransition.TryStageSelection(patient.patientId, client.BaseUrl) && ShellTransition.TryConsumeSelection(out var handoff) && handoff.patientId == patient.patientId && handoff.serviceUrl == endpoint, "one-shot shell handoff preserves each available canonical subject: " + patient.patientId);
                }
                model.Select(Female); Send(client, "/patients/" + Female + "/brief").Run();
                Check(!briefResponseOffline && !client.LastResponseOffline, "selected live brief callback carries its own live provenance");
                client.Configure(endpoint, true, 2); client.LoadCachedBundle();
                Check(liveHub.CanBegin && liveHub.BeginButton.interactable, "late offline metadata callback cannot disable already verified live selected brief");
                model.Select(Female); client.LoadBrief(Female);
                Check(model.CanBegin && !liveHub.CanBegin && briefResponseOffline, "offline selected brief is browsable but cannot begin network-required encounter");
                client.Configure(endpoint, false, 2);
                Send(client, "/patients/" + Female + "/brief").Run();
                Check(liveHub.CanBegin && liveHub.BeginButton.interactable && !client.IsOffline && !briefResponseOffline, "live brief recovery unlocks same selection without Refresh after offline fallback");
                string alias = patients.patients.Single(patient => patient.patientId == Female).scenarioId;
                Check(alias == "multi-source-overlap", "brief scenario alias comes from actual catalog mapping");
                brief = null; error = null;
                var aliasBrief = Send(client, "/patients/" + alias + "/brief"); aliasBrief.Run();
                Check(aliasBrief.paths.Single() == "/patients/multi-source-overlap/brief" && brief?.patientId == Female && brief.patient != null && error == null, "production GET brief accepts known scenario alias mapped to exact canonical patient identity");
                brief = null; error = null;
                typeof(ScalpalPreopService).GetMethod("Answer", Private).Invoke(client, new object[] { RouteKind.Brief, "/patients/" + alias + "/brief", JsonUtility.ToJson(Brief(Male)), null });
                Check(brief == null && error?.error?.code == "invalid_response" && failedRoute == "/patients/" + alias + "/brief", "known alias never permits another patient's response identity");
                foreach (var failure in new[] { new[] { "patient-demo-consent-revoked", "consent_inactive" }, new[] { "patient-demo-rate-limited", "rate_limited" }, new[] { "patient-not-found", "patient_not_found" } })
                {
                    brief = null; error = null;
                    Send(client, "/patients/" + failure[0] + "/brief").Run();
                    Check(brief == null && error?.error?.code == failure[1] && !client.IsOffline, "HTTP error remains visible, never replaced with cached chart: " + failure[1]);
                    Check(error.actions != null && error.actions.Length > 0, "service failure retains real recovery actions: " + failure[1]);
                    Check(failedRoute == "/patients/" + failure[0] + "/brief", "failure callback preserves the exact request identity: " + failure[1]);
                    if (failure[1] == "rate_limited") Check(retryRoute == failedRoute && retrySeconds == 1, "actual HTTP 429 brief preserves Retry-After seconds and route identity");
                }
                error = null; retryRoute = ""; retrySeconds = 0;
                SurgicalCase rateLimitedCase = null; client.CaseLoaded += value => rateLimitedCase = value;
                Send(client, "/patients/patient-demo-rate-limited/case").Run();
                Check(rateLimitedCase?.status == "retry" && rateLimitedCase.retryAfterSeconds == 1 && error == null, "production case endpoint returns HTTP200 renderable retry state with retryAfterSeconds");
                rateLimitedCase = null; error = null; retryRoute = ""; retrySeconds = 0;
                const string legacyPatientId = "patient-validation-legacy429";
                string legacyRoute = "/patients/" + legacyPatientId + "/case";
                var legacyRow = Entry(legacyPatientId, "retry", "", ""); legacyRow.encounterAvailable = false;
                liveHub.PatientsLoaded(new PatientList { patients = patients.patients.Concat(new[] { legacyRow }).ToArray() });
                Check(liveHub.Select(legacyPatientId) && !model.DetailLoading && liveHub.RetryRemaining == 0, "controlled legacy retry patient can select recovery detail without loading a brief or inheriting another patient's cooldown");
                Send(client, legacyRoute).Run();
                Check(rateLimitedCase == null && error?.error?.code == "rate_limited" && retryRoute == legacyRoute && retrySeconds == 3, "controlled canonical legacy HTTP429 case response preserves Retry-After header despite never invoking CaseLoaded");
                Check(model.SelectedPatientId == legacyPatientId && liveHub.RetryRemaining > 0, "actual canonical HTTP429 route sets the selected retry patient's cooldown");
                liveHub.Retry();
                Check(((System.Collections.IEnumerable)typeof(ScalpalPreopService).GetField("pending", Private).GetValue(client)).Cast<object>().Count() == 0 && (string)typeof(HubController).GetField("retrying", Private).GetValue(liveHub) == "", "Retry during the canonical HTTP429 cooldown emits no additional request");
                model.Select(Female);
                liveHub.Select(legacyPatientId);
                Check(liveHub.RetryRemaining > 0, "leaving and reselecting canonical legacy retry card cannot bypass its per-patient cooldown");
                int callbacksAfterCancellation = 0;
                Action<PreopBrief> cancellationObserver = value => callbacksAfterCancellation++;
                client.BriefLoaded += cancellationObserver;
                var cancelled = Send(client, "/patients/" + Female + "/brief");
                Check(cancelled.Step(), "cancellable actual HTTP brief request starts");
                client.CancelPendingRequests(); cancelled.Run();
                client.BriefLoaded -= cancellationObserver;
                Check(callbacksAfterCancellation == 0, "cancelled request cannot publish stale success after request abort/disposal");
                error = null; brief = null;
                typeof(ScalpalPreopService).GetMethod("Answer", Private).Invoke(client, new object[] { RouteKind.Brief, "/patients/" + Female + "/brief", JsonUtility.ToJson(Brief(Male)), null });
                Check(brief == null && error?.error?.code == "invalid_response" && failedRoute == "/patients/" + Female + "/brief", "200 payload with wrong patient identity is rejected by actual client response handler");
                error = null; patients = null;
                typeof(ScalpalPreopService).GetMethod("Answer", Private).Invoke(client, new object[] { RouteKind.Patients, "/patients", "{}", null });
                Check(patients == null && error?.error?.code == "invalid_response" && failedRoute == "/patients", "200 payload missing patient array becomes recoverable failure rather than a false empty catalog");
                // ConnectionError must traverse the actual client fallback path, not a mocked model.
                string closedEndpoint = endpoint;
                server.Kill(); server.WaitForExit(5000); server.Dispose(); server = null;
                Configure(client, closedEndpoint);
                typeof(ScalpalPreopService).GetField("offlineBundle", Private).SetValue(client, null);
                patients = null; bundle = null; brief = null; error = null;
                Send(client, "/patients").Run();
                Check(client.IsOffline && patients?.patients?.Length == 12, "network loss loads bundled patient list and signals Offline data");
                Send(client, "/unity/bundle").Run();
                Check(bundle?.cases?.Length == 11 && bundle.cases.Any(item => item.caseId == "case_patient-demo-multi-source_lap_appendectomy"), "offline bundle retains primary case metadata and the owner's advanced variant");
                Send(client, "/patients/" + Female + "/brief").Run();
                Check(brief?.patientId == Female && brief.synthetic && brief.chart.Length > 0, "offline selected detail comes from packaged matching brief");
                brief = null; error = null;
                Send(client, "/patients/not-packaged/brief").Run();
                Check(brief == null && error?.error?.code == "patient_not_found", "missing offline patient fails visibly rather than inventing data");
            }
            finally
            {
                if (fixture) UnityEngine.Object.DestroyImmediate(fixture);
                if (server != null) { if (!server.HasExited) server.Kill(); server.WaitForExit(5000); server.Dispose(); }
            }
        }

        static void ValidateScene()
        {
            const string scenePath = "Assets/Scalpal/Shell/Scenes/Launch.unity";
            var originalScenes = EditorBuildSettings.scenes.Select(item => new EditorBuildSettingsScene(item.path, item.enabled)).ToArray();
            const string extraScene = "Assets/Scalpal/Quest/Scenes/NativeWorkbench.unity";
            const string sandboxScene = "Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity";
            var requiredScenes = new[] { scenePath, "Assets/Scalpal/EncounterOffice/Scenes/DiagnosisOffice.unity", "Assets/Scalpal/Quest/Scenes/NativeSession.unity", "Assets/Scalpal/Recap/Scenes/RunEnding.unity" };
            try
            {
                EditorBuildSettings.scenes = originalScenes.Where(item => item.path != extraScene && item.path != sandboxScene)
                    .Concat(new[] { new EditorBuildSettingsScene(extraScene, false), new EditorBuildSettingsScene(sandboxScene, true) }).ToArray();
                var expectedExtras = EditorBuildSettings.scenes.Where(item => !requiredScenes.Contains(item.path)).GroupBy(item => item.path).Select(group => group.First().path + ":" + group.First().enabled).ToArray();
                ShellBuild.EnsureSceneOrder();
                var once = EditorBuildSettings.scenes.Select(item => item.path + ":" + item.enabled).ToArray();
                ShellBuild.EnsureSceneOrder();
                var twice = EditorBuildSettings.scenes;
                Check(twice.Take(requiredScenes.Length).Select(item => item.path).SequenceEqual(requiredScenes) && twice.Take(requiredScenes.Length).All(item => item.enabled), "EnsureSceneOrder keeps enabled Launch, Office, NativeSession and RunEnding first in exact order");
                Check(twice.Any(item => item.path == extraScene && !item.enabled) && twice.Any(item => item.path == sandboxScene && item.enabled), "EnsureSceneOrder preserves disabled NativeWorkbench and enabled InstrumentSandbox extras");
                Check(twice.Skip(requiredScenes.Length).Select(item => item.path + ":" + item.enabled).SequenceEqual(expectedExtras), "EnsureSceneOrder preserves every extra scene's order and enabled flag");
                Check(twice.Select(item => item.path).Distinct(StringComparer.Ordinal).Count() == twice.Length && once.SequenceEqual(twice.Select(item => item.path + ":" + item.enabled)), "EnsureSceneOrder is idempotent and cannot duplicate required or extra entries");
            }
            finally { EditorBuildSettings.scenes = originalScenes; }
            Check(originalScenes.Select(item => item.path + ":" + item.enabled).SequenceEqual(EditorBuildSettings.scenes.Select(item => item.path + ":" + item.enabled)), "scene-order regression restores the caller's original build settings");
            var enabled = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
            Check(enabled.Length >= 3 && enabled[0] == scenePath && enabled.Any(path => path.EndsWith("DiagnosisOffice.unity", StringComparison.Ordinal)) && enabled.Any(path => path.EndsWith("NativeSession.unity", StringComparison.Ordinal)), "Android build starts at Launch and retains Office and NativeSession");
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            foreach (var root in scene.GetRootGameObjects())
            {
                Check(!root.GetComponentsInChildren<Component>(true).Any(component => component == null), "launch scene has no missing scripts");
                Check(!root.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.sharedMaterials.Any(material => !material || !material.shader)), "launch renderers bind materials and shaders");
            }
            var hub = UnityEngine.Object.FindFirstObjectByType<HubController>();
            Check(hub && hub.service && hub.input && hub.input.head && hub.input.origin && hub.content, "scene binds hub, existing service, tracked camera, origin and world presentation root");
            Check(hub.input.head.transform.IsChildOf(hub.input.origin), "tracked camera shares shell's floor origin");
            hub.Initialize();
            hub.service.Configure("http://127.0.0.1:8787", true, 2);
            var start = hub.content.GetComponentsInChildren<ShellButton>(true).Single(button => button.name == "Button_Start");
            Check(!hub.Exploring && start.gameObject.activeInHierarchy, "launch starts on one active Start control");
            start.Press();
            Check(hub.Exploring && !start.gameObject.activeInHierarchy, "actual Start action opens explore without scene launch");
            hub.Reload(); // Real dispatch path with forceOffline synchronously loads Resources/scalpal_bundle.
            var patientCards = hub.content.GetComponentsInChildren<ShellButton>(true).Where(button => button.name.StartsWith("Patient_", StringComparison.Ordinal)).ToArray();
            Check(patientCards.Length == 9 && hub.Model.Patients.Length == 9 && hub.Model.UnavailableCount == 1, "offline callbacks render nine available patient cards and count the unavailable patient separately");
            Check(patientCards.Select(button => button.transform.localPosition.x).Distinct().Count() == 4 && patientCards.Select(button => button.transform.localPosition.y).Distinct().Count() == 3, "nine patient cards occupy four columns and three rows on the first page");
            Check(patientCards.All(button => { var bounds = button.GetComponent<BoxCollider>().size; return Mathf.Abs(bounds.x - .50f) < .001f && Mathf.Abs(bounds.y - .32f) < .001f; }), "each expanded grid card is a 0.50 by 0.32 metre ray target");
            Check(hub.content.GetComponentsInChildren<ShellButton>(true).All(button => { var bounds = button.GetComponent<BoxCollider>().size; return bounds.x >= .022f && bounds.y >= .022f; }), "all shell buttons meet the 22 mm minimum target size");
            var adult = hub.Model.Patients.Single(patient => patient.patientId == Female);
            patientCards.Single(button => button.name == "Patient_" + adult.scenarioId).Press();
            Check(hub.Model.SelectedPatientId == Female && hub.Model.SelectedBrief?.patientId == Female && hub.BeginButton && !hub.BeginButton.interactable && !hub.Transitioning && hub.Model.CanBegin, "actual card action loads matching offline chart but disables network-required Begin without transitioning");
            Check(!hub.Begin(), "offline chart browsing never attempts to create a network-only office encounter");
            MeasureAndValidateTypography(hub, patientCards);
            Check(!hub.Model.Patients.Any(patient => patient.status == "blocked" || string.IsNullOrEmpty(patient.patientId)), "blocked and connection-only rows produce no patient ray targets");
            Check(!hub.Select("patient-demo-consent-revoked") && !hub.Begin(), "direct blocked selection and begin commands fail closed even without a visible card");
            var availableOffline = hub.Model.Patients.Where(patient => patient.status == "ready" || patient.status == "needs_review").ToArray();
            Check(availableOffline.Length == 8, "cached grid retains all eight available authored subjects alongside the retry record");
            foreach (var patient in availableOffline)
            {
                Check(hub.Select(patient.patientId) && hub.Model.SelectedBrief?.patientId == patient.patientId && !hub.CanBegin && hub.BeginButton && !hub.BeginButton.interactable, "every available subject loads matching offline chart without establishing live begin authority: " + patient.patientId);
                hub.BeginButton.Press();
                Check(!hub.Begin() && !hub.Transitioning, "offline browsing never transitions for an otherwise available canonical subject: " + patient.patientId);
            }
            var capabilityFixture = JsonUtility.FromJson<PatientList>(JsonUtility.ToJson(new PatientList { patients = hub.Model.Patients }));
            var unsupported = capabilityFixture.patients.First(patient => patient.encounterAvailable && patient.status != "retry" && patient.patientId != Female);
            unsupported.encounterAvailable = false;
            hub.PatientsLoaded(capabilityFixture);
            Check(hub.Select(unsupported.patientId) && hub.BeginButton && !hub.BeginButton.interactable && hub.Model.AvailabilityReason == "Interview coming soon", "unsupported interview detail has explicit coming-soon reason and disabled Begin");
            hub.BeginButton.Press();
            Check(!hub.Begin() && !hub.Transitioning, "both UI press and direct Begin reject unsupported interview");
            hub.Reload();
            var appendixChip = hub.content.GetComponentsInChildren<ShellButton>(true).Single(button => button.label && button.label.text == "Appendix");
            appendixChip.Press();
            Check(hub.Model.VisiblePatients().Length == 3 && hub.Model.VisiblePatients().All(patient => patient.procedureId == "open_appendectomy") && hub.Model.ProcedureFilter == "appendectomy", "actual Appendix chip admits all three current open appendix patients and excludes other procedures");
            Check(hub.Model.CaseFor(Female).procedureId == "open_appendectomy" && !hub.Model.Complaint(adult).StartsWith("Advanced offline"), "packaged advanced variant cannot overwrite the catalog primary display case");
            var variantModel = new ExplorePatientModel();
            var variantRows = new[] { Entry(Female, "ready", "open_appendectomy", "urgent") };
            var primary = new SurgicalCase { patientId=Female, procedureId="open_appendectomy" };
            var advanced = new SurgicalCase { patientId=Female, procedureId="lap_appendectomy" };
            foreach (var variants in new[] { new[] { primary, advanced }, new[] { advanced, primary } })
            {
                variantModel.ApplyBundle(new ScalpalBundle { patients=variantRows, cases=variants });
                Check(variantModel.CaseFor(Female) == primary, "primary metadata remains selected in either bundle variant order");
            }
            variantModel.ApplyPatients(new PatientList { patients=new[] { variantRows[0], Entry(Male,"ready","lap_appendectomy","urgent"), Entry("patient-validation-colon","ready","lap_sigmoid_colectomy","elective") } });
            variantModel.SetFilters("appendectomy");
            Check(variantModel.VisiblePatients().Length == 2, "Appendix category includes open and advanced laparoscopic subjects but excludes colon");
            hub.Filter();
            hub.Select(Female);
            Check(ShellTransition.TryStageSelection(hub.Model.SelectedPatientId, hub.service.BaseUrl), "the production Begin handoff contract accepts supported selected patient plus explicit endpoint");
            Check(ShellTransition.TryConsumeSelection(out var selected) && selected.patientId == Female && selected.serviceUrl == hub.service.BaseUrl, "office consumes exact selected patient and service endpoint once");
            Check(!ShellTransition.TryConsumeSelection(out _), "consumed selection cannot start a second encounter");
            Check(!ShellTransition.TryStageSelection("patient/invalid", hub.service.BaseUrl) && !ShellTransition.TryStageSelection("", hub.service.BaseUrl) && !ShellTransition.TryStageSelection(Female, "file:///not-a-service"), "handoff rejects malformed or subjectless patient IDs and non-HTTP service endpoints");
            ValidatePaging(hub);
            Check(hub.content.GetComponentsInChildren<EncounterOfficeText>(true).All(fit => !fit.enabled), "shell text fitters are disabled after event-driven fitting rather than marshaling text every frame");
            var position = hub.input.head.transform.position; var rotation = hub.input.head.transform.rotation;
            hub.input.Recenter();
            Check(hub.input.head.transform.position == position && hub.input.head.transform.rotation == rotation, "recenter moves presentation while preserving tracked camera pose");
            var grid = hub.content.GetComponentsInChildren<Transform>(true).Single(transform => transform.name == "PatientCards");
            float forwardDistance = Vector3.Dot(grid.position - position, Vector3.ProjectOnPlane(hub.input.head.transform.forward, Vector3.up).normalized);
            Check(Mathf.Abs(forwardDistance - 1.3f) < .02f, "recenter preserves the authored 1.3 metre grid distance without stacking local and world offsets");
        }

        static ShellButton[] PatientCards(HubController hub) => hub.content.GetComponentsInChildren<ShellButton>()
            .Where(button => button.name.StartsWith("Patient_", StringComparison.Ordinal)).ToArray();
        static void ValidatePaging(HubController hub)
        {
            var entries = Enumerable.Range(0, 25).Select(index => Entry("patient-validation-" + index.ToString("D2"), "ready", "lap_appendectomy", "urgent")).ToArray();
            hub.PatientsLoaded(new PatientList { patients = entries });
            Check(hub.Page == 0 && hub.PageCount == 3 && PatientCards(hub).Length == 12, "catalog expansion to 25 patients starts on a bounded first page of twelve");
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int page = 0; page < 3; page++)
            {
                hub.SetPage(page); var cards = PatientCards(hub);
                Check(cards.Length == (page < 2 ? 12 : 1) && cards.All(card => card.transform.localPosition.y - card.GetComponent<BoxCollider>().size.y * .5f >= -.581f), "page " + page + " stays within three rows above footer controls");
                Check(cards.All(card => seen.Add(card.name)), "page " + page + " introduces no duplicate patient from another page");
            }
            Check(seen.Count == 25, "all 25 patients are reachable across pages without dropping rows");
            hub.SetPage(999); Check(hub.Page == 2, "page index clamps to the last available page");
            hub.SetPage(-1); Check(hub.Page == 0, "negative page index clamps to the first page");
            hub.SetPage(2); hub.Filter("lap_appendectomy", "urgent");
            Check(hub.Page == 0 && hub.PageCount == 3, "filter changes return to first page instead of leaving a stale page index");
            hub.Filter(); hub.Reload();
        }

        // Read-only physical measurements are logged separately from bounds assertions: passing
        // overlap checks does not establish Quest readability or compliance with the requested sizes.
        static void MeasureAndValidateTypography(HubController hub, ShellButton[] cards)
        {
            var cardFits = cards.SelectMany(card => card.GetComponentsInChildren<EncounterOfficeText>())
                .Where(fit => !string.IsNullOrWhiteSpace(fit.GetComponent<TextMesh>().text)).ToArray();
            foreach (var fit in cardFits) fit.Fit();
            var cardMm = cardFits.Select(fit => fit.MeasuredSize().y / Mathf.Max(1, fit.GetComponent<TextMesh>().text.Split('\n').Length) / Vector3.Distance(fit.GetComponentInParent<ShellButton>().transform.position, hub.input.head.transform.position) * 1000).ToArray();
            bool minimumCardSizes = true;
            foreach (var card in cards)
            {
                var lines = card.GetComponentsInChildren<EncounterOfficeText>().Where(fit => !string.IsNullOrWhiteSpace(fit.GetComponent<TextMesh>().text)).OrderByDescending(fit => fit.transform.position.y).ToArray();
                float distance = Vector3.Distance(card.transform.position, hub.input.head.transform.position);
                minimumCardSizes &= lines.Length == 5 && lines.Select((fit, index) => fit.MeasuredSize().y / distance * 1000 >= (index == 0 ? 32 : 24) - .05f).All(value => value);
            }
            Check(minimumCardSizes, "all five card text rows meet 32 mm name and 24 mm body at one-metre equivalent using each card's actual viewing distance");
            var panel = hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart");
            var detailFits = panel.GetComponentsInChildren<EncounterOfficeText>().Where(fit => !string.IsNullOrWhiteSpace(fit.GetComponent<TextMesh>().text)).ToArray();
            foreach (var fit in detailFits) fit.Fit();
            var detailMm = detailFits.Select(fit => fit.MeasuredSize().y / Mathf.Max(1, fit.GetComponent<TextMesh>().text.Split('\n').Length) / Vector3.Distance(panel.position, hub.input.head.transform.position) * 1000).ToArray();
            UnityEngine.Debug.Log("SCALPAL_SHELL_TEXT_MEASURE cardTargets=" + cards.Length +
                " selectableTargets=" + cards.Count(card => card.interactable) +
                " cardLineMmAt1m=" + cardMm.Min().ToString("F2") + ".." + cardMm.Max().ToString("F2") +
                " detailLineMmAt1m=" + detailMm.Min().ToString("F2") + ".." + detailMm.Max().ToString("F2") +
                " detailLineCounts=" + string.Join(",", detailFits.Select(fit => fit.GetComponent<TextMesh>().text.Split('\n').Length)) +
                " cardAndDetailNormalization=actualPanelCenterDistance requestedBodyMm=24 requestedLabelMm=32 headset=false readabilityPassClaim=false");
            foreach (var card in cards) ValidateTextRegions(card.transform, .16f, "patient card " + card.name);
            ValidateTextRegions(panel, .36f, "selected detail");
            var originalCase = hub.Model.SelectedCase;
            string originalPresentation = originalCase.presentation;
            var originalBrief = hub.Model.SelectedBrief;
            try
            {
                // Stress the actual two-line complaint and seven-line chart render path, preserving
                // the original resource data and restoring the real selected chart immediately after.
                originalCase.presentation = string.Join(" ", Enumerable.Repeat("Authored presentation with additional context", 12));
                var longBrief = JsonUtility.FromJson<PreopBrief>(JsonUtility.ToJson(originalBrief));
                longBrief.chart = new[] { new ChartLine { section = "Regression fixture", text = string.Join(" ", Enumerable.Repeat("Returned chart context", 60)) } };
                hub.BriefLoaded(longBrief);
                panel = hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart");
                var directText = panel.GetComponentsInChildren<TextMesh>().Where(text => text.transform.parent == panel).ToArray();
                Check(directText.Single(text => Mathf.Abs(text.transform.localPosition.y - .205f) < .001f).text.Split('\n').Length == 2 &&
                    directText.Single(text => Mathf.Abs(text.transform.localPosition.y - .105f) < .001f).text.Split('\n').Length == 7,
                    "long complaint and chart exercise actual bounded two-line and seven-line detail layouts");
                ValidateTextRegions(panel, .36f, "maximum-line detail regression");
            }
            finally { originalCase.presentation = originalPresentation; hub.BriefLoaded(originalBrief); }
        }
        static void ValidateTextRegions(Transform panel, float halfHeight, string description)
        {
            var regions = panel.GetComponentsInChildren<EncounterOfficeText>()
                .Where(fit => !string.IsNullOrWhiteSpace(fit.GetComponent<TextMesh>().text)).Select(fit =>
                {
                    fit.Fit(); var bounds = fit.GetComponent<Renderer>().localBounds;
                    var first = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.min));
                    var second = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.max));
                    return new Vector2(Mathf.Min(first.y, second.y), Mathf.Max(first.y, second.y));
                }).OrderByDescending(region => region.y).ToArray();
            Check(regions.All(region => region.x >= -halfHeight - .0005f && region.y <= halfHeight + .0005f), description + " measured glyph bounds stay inside panel");
            bool separated = Enumerable.Range(1, Math.Max(0, regions.Length - 1)).All(index => regions[index - 1].x >= regions[index].y - .0005f);
            if (!separated) foreach (var fit in panel.GetComponentsInChildren<EncounterOfficeText>())
            {
                var bounds = fit.GetComponent<Renderer>().localBounds;
                var first = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.min));
                var second = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.max));
                UnityEngine.Debug.Log("SCALPAL_SHELL_TEXT_OVERLAP " + description + " y=" + first.y.ToString("F4") + ".." + second.y.ToString("F4") + " text=" + fit.GetComponent<TextMesh>().text.Replace("\n", " / "));
            }
            Check(separated, description + " measured text rows do not vertically overlap");
        }

        static PatientListEntry Entry(string id, string status, string procedure, string urgency) => new PatientListEntry { patientId = id, scenarioId = id, displayLabel = id, status = status, procedureId = procedure, urgency = urgency, encounterAvailable = true };
        static PreopBrief Brief(string id) => new PreopBrief { patientId = id, synthetic = true, dataSource = "demo", patient = new PatientSummary { name = "Synthetic fixture", age = 40, sex = "female" }, chart = new[] { new ChartLine { section = "Fixture", text = "Authored test chart" } } };
        static void Configure(ScalpalPreopService client, string endpoint)
        {
            typeof(ScalpalPreopService).GetField("baseUrl", Private).SetValue(client, endpoint);
            typeof(ScalpalPreopService).GetField("timeoutSeconds", Private).SetValue(client, 2);
        }
        static Pump Send(ScalpalPreopService client, string route)
        {
            var action = new ScalpalAction { method = "GET", route = route, id = "navigate", label = "Validation" };
            var routine = (IEnumerator)typeof(ScalpalPreopService).GetMethod("Send", Private).Invoke(client, new object[] { ScalpalRoutes.Resolve(action.method, route), action, null, null });
            return new Pump(routine);
        }
        static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Shell validation: " + message);
            checks++;
        }
        sealed class Pump
        {
            readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
            AsyncOperation waiting;
            public readonly List<string> paths = new List<string>();
            public Pump(IEnumerator routine) { stack.Push(routine); }
            public bool Step()
            {
                if (waiting != null && !waiting.isDone) return true;
                waiting = null;
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    if (!top.MoveNext()) { stack.Pop(); continue; }
                    if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                    if (top.Current is UnityWebRequestAsyncOperation operation) paths.Add(new Uri(operation.webRequest.url).AbsolutePath);
                    waiting = top.Current as AsyncOperation;
                    return true;
                }
                return false;
            }
            public void Run()
            {
                var elapsed = Stopwatch.StartNew();
                while (Step()) { if (elapsed.ElapsedMilliseconds > 20000) throw new InvalidOperationException("Shell HTTP coroutine timed out"); Thread.Sleep(5); }
            }
        }
    }
}
