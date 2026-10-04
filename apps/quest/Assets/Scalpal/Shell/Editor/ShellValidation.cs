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
            checks = 0;
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
            var unsupported = Entry("patient-demo-001", "ready", "lap_cholecystectomy", "urgent");
            var pediatric = Entry("patient-demo-pediatric-asthma", "ready", "lap_appendectomy", "urgent");
            model.ApplyPatients(new PatientList { patients = new[] { blocked, review, retry, ready, unsupported, pediatric } });
            Check(model.Patients.Take(3).All(p => p.status == "ready"), "ready cases sort before review, retry and blocked");
            Check(ExplorePatientModel.StatusLabel("ready") == "Ready" && ExplorePatientModel.StatusLabel("needs_review") == "Chart has gaps" && ExplorePatientModel.StatusLabel("blocked") == "Locked" && ExplorePatientModel.StatusLabel("retry") == "Try again", "all four service statuses have explicit human-readable text");
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
            Check(!model.CanBegin && !ExplorePatientModel.HasAuthoredEncounter(unsupported.patientId), "patient without authored interview cannot begin");
            model.Select(pediatric.patientId); model.ApplyBrief(Brief(pediatric.patientId));
            Check(!model.CanBegin, "authored child case is gated while office has adult-only presentation");
            model.Select(Female); var nonsynthetic = Brief(Female); nonsynthetic.synthetic = false; model.ApplyBrief(nonsynthetic);
            Check(!model.CanBegin && model.DetailError.Length > 0, "non-synthetic detail cannot begin");
            model.Select(Female); var sandbox = Brief(Female); sandbox.dataSource = "sandbox"; model.ApplyBrief(sandbox);
            Check(!model.CanBegin, "same-shaped sandbox data cannot impersonate exact authored demo identity");
            model.Select(Female); model.ApplyBrief(Brief(Female));
            model.SetFilters("lap_cholecystectomy");
            Check(model.VisiblePatients().Length == 2 && model.VisiblePatients().All(p => p.procedureId == "lap_cholecystectomy") && model.Selected == null, "procedure filter returns matching cards and clears hidden selection");
            model.SetFilters("", "urgent");
            Check(model.VisiblePatients().Length == 4 && model.VisiblePatients().All(p => p.urgency == "urgent"), "urgency filter returns only matching cards");
            model.SetFilters("lap_cholecystectomy", "elective");
            Check(model.VisiblePatients().Length == 1 && model.VisiblePatients()[0].patientId == Male, "procedure and urgency filters combine");
            model.SetFilters(); Check(model.VisiblePatients().Length == 6, "All restores the complete patient grid");
            model.ApplyBundle(new ScalpalBundle { patients = new[] { Entry(Female, "blocked", "", "") }, cases = Array.Empty<SurgicalCase>() });
            Check(model.Patients.Length == 6 && model.Patients.Single(p => p.patientId == Female).status == "ready", "slow bundle cannot replace fresher live patient statuses");
            model.Select(Female); model.ApplyBrief(Brief(Female)); ready.status = "blocked";
            model.ApplyPatients(new PatientList { patients = new[] { ready } });
            Check(model.Selected == null && !model.CanBegin, "catalog refresh revoking selected card clears detail and begin gate");
            model.ApplyPatients(new PatientList { patients = new[] { Entry(Female, "retry", "", "") } });
            model.ApplyCase(new SurgicalCase { patientId = Female, status = "ready", procedureId = "lap_appendectomy", urgency = "urgent" });
            model.Select(Female); model.ApplyBrief(Brief(Female));
            Check(model.CanBegin && model.Selected.procedureId == "lap_appendectomy", "successful retry adopts returned ready status and can unlock matching detail");
            model.ApplyCase(new SurgicalCase { patientId = Female, status = "blocked", statusReason = "Consent revoked" });
            Check(!model.CanBegin && model.Selected == null && model.StatusReason(model.Patients[0]) == "Consent revoked", "retry response revoking consent clears selection and preserves actual reason");
            model.ApplyPatients(new PatientList { patients = new[] { new PatientListEntry { patientId = "", scenarioId = "connect-cancelled", status = "connect" }, new PatientListEntry { patientId = "", scenarioId = "connect-failed", status = "connect" } } });
            Check(model.Patients.Length == 2 && !model.Select("") && !model.CanBegin, "both connection-only scenarios remain distinct disabled rows without fake patients");
        }

        static void ValidateExchange()
        {
            Process server = null;
            GameObject fixture = null;
            try
            {
                string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
                string servicePath = Path.Combine(repo, "services/preop");
                var info = new ProcessStartInfo("/usr/bin/env", "node --import " + Quote(Path.Combine(servicePath, "node_modules/tsx/dist/loader.mjs")) + " " + Quote(Path.Combine(repo, "scripts/quest/native-coach-check/server.ts")))
                { WorkingDirectory = servicePath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                server = Process.Start(info);
                var ready = server.StandardOutput.ReadLineAsync();
                if (!ready.Wait(15000)) throw new InvalidOperationException("Shell service fixture did not become ready");
                const string marker = "SCALPAL_COACH_TEST_ENDPOINT=";
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
                client.BundleLoaded += value => bundle = value;
                client.BriefLoaded += value => brief = value;
                client.RequestFailed += value => error = value;
                string failedRoute = "";
                client.RequestFailedForRoute += (route, value) => failedRoute = route;
                var getPatients = Send(client, "/patients"); getPatients.Run();
                Check(getPatients.paths.SequenceEqual(new[] { "/patients" }) && patients?.patients?.Length == 12 && !client.IsOffline, "production client fetches all 12 scenario rows over actual HTTP");
                foreach (var status in new[] { "ready", "needs_review", "blocked", "retry" })
                    Check(patients.patients.Any(p => p.status == status), "actual fixture returns renderable " + status + " status");
                var getBundle = Send(client, "/unity/bundle"); getBundle.Run();
                Check(getBundle.paths.SequenceEqual(new[] { "/unity/bundle" }) && bundle?.cases?.Length == 10, "production bundle route supplies all 10 patient cases and reused DTOs");
                var liveHub = UnityEngine.Object.FindFirstObjectByType<HubController>();
                Check(liveHub != null, "live HTTP boundary binds real generated hub controller");
                liveHub.service = client; liveHub.Initialize(); liveHub.Enter();
                liveHub.BundleLoaded(bundle); liveHub.PatientsLoaded(patients);
                client.BriefLoaded += liveHub.BriefLoaded;
                var model = liveHub.Model;
                Check(model.Patients.Count(p => !string.IsNullOrEmpty(p.patientId)) == 10, "all ten FinchNode patients remain available on the grid");
                Check(model.Select(Female), "live selected patient opens detail");
                var getBrief = Send(client, "/patients/" + Female + "/brief"); getBrief.Run();
                Check(getBrief.paths.Single() == "/patients/" + Female + "/brief" && brief?.patientId == Female && brief.synthetic && brief.chart.Length > 0, "real brief carries matching synthetic identity and actual chart highlights");
                Check(model.CanBegin && model.Name(model.Selected) == "Priya Ramaswamy" && liveHub.BeginButton && liveHub.BeginButton.interactable, "actual live brief callback renders supported adult detail and enables explicit Begin");
                Check(ShellTransition.TryStageSelection(model.SelectedPatientId, client.BaseUrl) && ShellTransition.TryConsumeSelection(out var liveSelection) && liveSelection.patientId == Female && liveSelection.serviceUrl == endpoint, "live service UI selection stages the exact production office handoff identity");
                client.BriefLoaded -= liveHub.BriefLoaded;
                foreach (var failure in new[] { new[] { "patient-demo-consent-revoked", "consent_inactive" }, new[] { "patient-demo-rate-limited", "rate_limited" }, new[] { "patient-not-found", "patient_not_found" } })
                {
                    brief = null; error = null;
                    Send(client, "/patients/" + failure[0] + "/brief").Run();
                    Check(brief == null && error?.error?.code == failure[1] && !client.IsOffline, "HTTP error remains visible, never replaced with cached chart: " + failure[1]);
                    Check(error.actions != null && error.actions.Length > 0, "service failure retains real recovery actions: " + failure[1]);
                    Check(failedRoute == "/patients/" + failure[0] + "/brief", "failure callback preserves the exact request identity: " + failure[1]);
                }
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
                Check(bundle?.cases?.Length == 10, "offline bundle request loads packaged complete case metadata");
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
            Check(patientCards.Length == 12 && hub.Model.Patients.Length == 12, "offline service callbacks render the complete 4 by 3 grid");
            Check(patientCards.Select(button => button.transform.localPosition.x).Distinct().Count() == 4 && patientCards.Select(button => button.transform.localPosition.y).Distinct().Count() == 3, "twelve scenario cards occupy four columns and three rows without paging");
            Check(patientCards.All(button => { var bounds = button.GetComponent<BoxCollider>().size; return Mathf.Abs(bounds.x - .22f) < .001f && Mathf.Abs(bounds.y - .16f) < .001f; }), "each grid card is a 0.22 by 0.16 metre ray target");
            Check(hub.content.GetComponentsInChildren<ShellButton>(true).All(button => { var bounds = button.GetComponent<BoxCollider>().size; return bounds.x >= .022f && bounds.y >= .022f; }), "all shell buttons meet the 22 mm minimum target size");
            var adult = hub.Model.Patients.Single(patient => patient.patientId == Female);
            patientCards.Single(button => button.name == "Patient_" + adult.scenarioId).Press();
            Check(hub.Model.SelectedPatientId == Female && hub.Model.SelectedBrief?.patientId == Female && hub.BeginButton && !hub.BeginButton.interactable && !hub.Transitioning && hub.Model.CanBegin, "actual card action loads matching offline chart but disables network-required Begin without transitioning");
            Check(!hub.Begin(), "offline chart browsing never attempts to create a network-only office encounter");
            MeasureAndValidateTypography(hub, patientCards);
            var blocked = hub.Model.Patients.Single(patient => patient.status == "blocked");
            var blockedCard = patientCards.Single(button => button.name == "Patient_" + blocked.scenarioId);
            blockedCard.Press();
            Check(!blockedCard.interactable && hub.Model.SelectedPatientId == Female && !hub.Transitioning, "locked card's actual press cannot replace selection or launch");
            Check(!hub.Select(blocked.patientId) && !hub.Begin(), "direct blocked selection and begin commands also fail closed");
            var unsupported = hub.Model.Patients.First(patient => ExplorePatientModel.CanSelect(patient) && !ExplorePatientModel.HasAuthoredEncounter(patient.patientId) && patient.status != "retry");
            Check(hub.Select(unsupported.patientId) && hub.BeginButton && !hub.BeginButton.interactable && hub.Model.AvailabilityReason == "Interview coming soon", "unsupported interview detail has explicit coming-soon reason and disabled Begin");
            hub.BeginButton.Press();
            Check(!hub.Begin() && !hub.Transitioning, "both UI press and direct Begin reject unsupported interview");
            var appendixChip = hub.content.GetComponentsInChildren<ShellButton>(true).Single(button => button.label && button.label.text == "Appendix");
            appendixChip.Press();
            Check(hub.Model.VisiblePatients().All(patient => patient.procedureId == "lap_appendectomy") && hub.Model.ProcedureFilter == "lap_appendectomy", "actual procedure chip action filters rendered patient set");
            hub.Filter();
            hub.Select(Female);
            Check(ShellTransition.TryStageSelection(hub.Model.SelectedPatientId, hub.service.BaseUrl), "the production Begin handoff contract accepts supported selected patient plus explicit endpoint");
            Check(ShellTransition.TryConsumeSelection(out var selected) && selected.patientId == Female && selected.serviceUrl == hub.service.BaseUrl, "office consumes exact selected patient and service endpoint once");
            Check(!ShellTransition.TryConsumeSelection(out _), "consumed selection cannot start a second encounter");
            Check(!ShellTransition.TryStageSelection(unsupported.patientId, hub.service.BaseUrl) && !ShellTransition.TryStageSelection(Female, "file:///not-a-service"), "handoff rejects unsupported patients and non-HTTP service endpoints");
            var position = hub.input.head.transform.position; var rotation = hub.input.head.transform.rotation;
            hub.input.Recenter();
            Check(hub.input.head.transform.position == position && hub.input.head.transform.rotation == rotation, "recenter moves presentation while preserving tracked camera pose");
            var grid = hub.content.GetComponentsInChildren<Transform>(true).Single(transform => transform.name == "PatientCards");
            float forwardDistance = Vector3.Dot(grid.position - position, Vector3.ProjectOnPlane(hub.input.head.transform.forward, Vector3.up).normalized);
            Check(Mathf.Abs(forwardDistance - 1.3f) < .02f, "recenter preserves the authored 1.3 metre grid distance without stacking local and world offsets");
        }

        // Read-only physical measurements are logged separately from bounds assertions: passing
        // overlap checks does not establish Quest readability or compliance with the requested sizes.
        static void MeasureAndValidateTypography(HubController hub, ShellButton[] cards)
        {
            var cardFits = cards.SelectMany(card => card.GetComponentsInChildren<EncounterOfficeText>())
                .Where(fit => !string.IsNullOrWhiteSpace(fit.GetComponent<TextMesh>().text)).ToArray();
            foreach (var fit in cardFits) fit.Fit();
            var cardMm = cardFits.Select(LineMmAtOneMetre).ToArray();
            var panel = hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart");
            var detailFits = panel.GetComponentsInChildren<EncounterOfficeText>().Where(fit => !string.IsNullOrWhiteSpace(fit.GetComponent<TextMesh>().text)).ToArray();
            foreach (var fit in detailFits) fit.Fit();
            var detailMm = detailFits.Select(LineMmAtOneMetre).ToArray();
            UnityEngine.Debug.Log("SCALPAL_SHELL_TEXT_MEASURE cardTargets=" + cards.Length +
                " selectableTargets=" + cards.Count(card => card.interactable) +
                " cardLineMmAt1m=" + cardMm.Min().ToString("F2") + ".." + cardMm.Max().ToString("F2") +
                " detailLineMmAt1m=" + detailMm.Min().ToString("F2") + ".." + detailMm.Max().ToString("F2") +
                " detailLineCounts=" + string.Join(",", detailFits.Select(fit => fit.GetComponent<TextMesh>().text.Split('\n').Length)) +
                " normalizationDistanceM=1.3 requestedBodyMm=24 requestedLabelMm=32 headset=false readabilityPassClaim=false");
            foreach (var card in cards) ValidateTextRegions(card.transform, .08f, "patient card " + card.name);
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
        static float LineMmAtOneMetre(EncounterOfficeText fit) => fit.MeasuredSize().y / Mathf.Max(1, fit.GetComponent<TextMesh>().text.Split('\n').Length) / 1.3f * 1000;
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
            Check(regions.All(region => region.x >= -halfHeight - .0005f && region.y <= halfHeight + .0005f), description + " measured glyph bounds stay inside panel, including bottom coming-soon label");
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

        static PatientListEntry Entry(string id, string status, string procedure, string urgency) => new PatientListEntry { patientId = id, scenarioId = id, displayLabel = id, status = status, procedureId = procedure, urgency = urgency };
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
