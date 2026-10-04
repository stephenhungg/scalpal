using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Scalpal.Brand;
using Scalpal.Brand.Editor;
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
            checks += ScalpalBrandValidation.Run();
            ValidateModel();
            ValidateExchange();
            ValidateScene();
            DialogueBoxValidation.Run();
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
            Check(!model.Select(unsupported.patientId) && !model.CanBegin && !model.VisiblePatients().Contains(unsupported), "server capability false hides the subject from the grid and refuses selection or begin");
            unsupported.encounterAvailable = true;
            model.Select(unsupported.patientId); model.ApplyBrief(Brief(unsupported.patientId));
            Check(model.CanBegin && model.VisiblePatients().Contains(unsupported), "service-confirmed canonical subject outside the packaged demo set is listed and can begin without a fixed ID catalog");
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
            ValidateGridVisibility();
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

        // The explore grid shows only rows a clinician can open: everything else is hidden, not greyed.
        static void ValidateGridVisibility()
        {
            var model = new ExplorePatientModel();
            var playable = Entry("patient-validation-playable", "ready", "lap_appendectomy", "urgent"); playable.displayLabel = "Ada Playable";
            var review = Entry("patient-validation-review", "needs_review", "lap_cholecystectomy", "elective"); review.displayLabel = "Ben Review";
            var recovering = Entry("patient-validation-recovering", "retry", "lap_sigmoid_colectomy", "elective"); recovering.displayLabel = "Cy Recovering";
            var noProcedure = Entry("patient-validation-no-procedure", "ready", "", "urgent"); noProcedure.displayLabel = "Dee Noprocedure";
            var unnamed = Entry("patient-validation-unnamed", "needs_review", "lap_cholecystectomy", "elective"); unnamed.displayLabel = "Unnamed patient (limited chart)";
            var unavailable = Entry("patient-validation-unavailable", "retry", "lap_appendectomy", "urgent"); unavailable.displayLabel = "Unavailable patient";
            var noInterview = Entry("patient-validation-no-interview", "ready", "lap_appendectomy", "urgent"); noInterview.displayLabel = "Eve Nointerview"; noInterview.encounterAvailable = false;
            var blocked = Entry("patient-validation-blocked", "blocked", "lap_appendectomy", "urgent"); blocked.displayLabel = "Fay Blocked";
            var unrecoverable = Entry("patient-validation-unrecoverable", "retry", "lap_appendectomy", "urgent"); unrecoverable.displayLabel = "Gus Unrecoverable"; unrecoverable.encounterAvailable = false;
            var connect = new PatientListEntry { patientId = "", scenarioId = "connect-failed", displayLabel = "Connect session failed", status = "connect" };
            model.ApplyPatients(new PatientList { patients = new[] { connect, blocked, unrecoverable, noInterview, unavailable, unnamed, noProcedure, recovering, review, playable } });
            var visible = model.VisiblePatients().Select(p => p.patientId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            Check(visible.SequenceEqual(new[] { playable.patientId, recovering.patientId, review.patientId }), "grid lists playable rows (ready, needs review, recoverable retry) and nothing else: " + string.Join(",", visible));
            foreach (var hidden in new[] { noProcedure, unnamed, unavailable, noInterview, blocked, unrecoverable, connect })
                Check(!model.Select(hidden.patientId) && !model.CanBegin, "hidden row cannot be selected or begun: " + hidden.displayLabel);
            Check(model.Recoverable(recovering) && !model.Recoverable(unrecoverable) && !model.Recoverable(playable), "only retry rows with an authored interview are retried in the background");
            model.ApplyBundle(new ScalpalBundle { patients = Array.Empty<PatientListEntry>(), cases = new[] { new SurgicalCase { patientId = playable.patientId, procedureId = "lap_appendectomy", patient = new PatientSummary { name = "Ada Playable", age = 40, sex = "female" }, presentation = "Right lower quadrant pain since last night. Nausea." } } });
            Check(model.CardLine(playable) == "40 · Appendix" && model.CardLine(review) == "Gallbladder" && model.CardLine(recovering) == "Colon", "card line is age · procedure short name only (age omitted when unknown)");
            Check(ExplorePatientModel.ProcedureShort(Entry(Female, "ready", "open_appendectomy", "")) == "Appendix" && ExplorePatientModel.ProcedureShort(Entry(Female, "ready", "lap_appendectomy", "")) == "Appendix", "open and laparoscopic appendectomy share the Appendix short name");
            Check(model.Presenting(playable) == "Right lower quadrant pain since last night." && model.AgeSex(playable) == "40 · Female" && model.ProcedureAndUrgency(playable) == "Appendix · Urgent", "detail panel lines: one-sentence complaint, age · sex, procedure · urgency");
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
                Check(model.Patients.Length == 9 && model.UnavailableCount == 1, "actual catalog keeps nine selectable records plus one unavailable patient, excluding connection scenarios");
                Check(model.VisiblePatients().Length == 7 && !model.VisiblePatients().Any(p => p.patientId == "patient-demo-consent-partial" || p.patientId == "patient-demo-rate-limited"), "actual catalog grid hides the unnamed limited chart and the rate-limited record without an interview");
                Check(model.Select(Female), "live selected patient opens detail");
                var getBrief = Send(client, "/patients/" + Female + "/brief"); getBrief.Run();
                Check(getBrief.paths.Single() == "/patients/" + Female + "/brief" && brief?.patientId == Female && brief.synthetic && brief.chart.Length > 0, "real brief carries matching synthetic identity and actual chart highlights");
                Check(model.CanBegin && model.Name(model.Selected) == "Priya Ramaswamy" && liveHub.BeginButton && liveHub.BeginButton.interactable && liveHub.SkipButton && liveHub.SkipButton.interactable, "actual live brief callback renders supported adult detail and enables explicit Begin and Skip to surgery");
                Check(!DetailText(liveHub).Any(text => text.name == "Availability"), "an enabled Begin shows no availability message");
                Check(ShellTransition.TryStageSelection(model.SelectedPatientId, client.BaseUrl) && ShellTransition.TryConsumeSelection(out var liveSelection) && liveSelection.patientId == Female && liveSelection.serviceUrl == endpoint, "live service UI selection stages the exact production office handoff identity");
                var available = patients.patients.Where(patient => (patient.status == "ready" || patient.status == "needs_review") && patient.encounterAvailable).ToArray();
                Check(available.Length == 8, "live HTTP catalog exposes all eight service-authorized subjects for the explicit shell handoff");
                Check(available.Count(model.Listed) == 7 && !model.Listed(available.Single(p => p.patientId == "patient-demo-consent-partial")), "every named, interview-capable subject with a procedure is listed; the unnamed limited chart is not");
                foreach (var patient in available.Where(model.Listed))
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
                var legacyRow = Entry(legacyPatientId, "retry", "", "");
                liveHub.PatientsLoaded(new PatientList { patients = patients.patients.Concat(new[] { legacyRow }).ToArray() });
                Check(!liveHub.Select(legacyPatientId) && model.Recoverable(legacyRow) && !model.VisiblePatients().Contains(legacyRow) && liveHub.Remaining(legacyPatientId) == 0, "retry record without a procedure stays out of the grid but remains a background recovery target with no inherited cooldown");
                Send(client, legacyRoute).Run();
                Check(rateLimitedCase == null && error?.error?.code == "rate_limited" && retryRoute == legacyRoute && retrySeconds == 3, "controlled canonical legacy HTTP429 case response preserves Retry-After header despite never invoking CaseLoaded");
                Check(liveHub.Remaining(legacyPatientId) > 2, "actual canonical HTTP429 Retry-After sets that record's background retry cooldown");
                liveHub.ServiceRetries();
                var inFlight = (HashSet<string>)typeof(HubController).GetField("retryInFlight", Private).GetValue(liveHub);
                Check(((System.Collections.IEnumerable)typeof(ScalpalPreopService).GetField("pending", Private).GetValue(client)).Cast<object>().Count() == 0 && inFlight.Count == 0, "background retry emits no request during the service's Retry-After");
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
            ValidateLaunchBrand(hub);
            // The physical path: left controller aim pose -> ray -> Start collider -> trigger press.
            var startPress = ScalpalPointerProbe.Press(0, hub.input.origin, start.GetComponent<Collider>(), () => hub.input.Pointer(0), hub.input.StepPointers);
            Check(startPress.RayMatchesAim && startPress.rayVisible && (startPress.lineStart - startPress.expectedOrigin).magnitude < 1e-4f, "launch ray starts at the left controller's aim pose and points along its forward");
            Check(startPress.hovered && startPress.accentOnHover, "aiming at Start focuses it with the brand accent");
            Check(hub.Exploring && !start.gameObject.activeInHierarchy, "left trigger on Start opens explore without scene launch");
            hub.Reload(); // Real dispatch path with forceOffline synchronously loads Resources/scalpal_bundle.
            var patientCards = hub.content.GetComponentsInChildren<ShellButton>(true).Where(button => button.name.StartsWith("Patient_", StringComparison.Ordinal)).ToArray();
            Check(patientCards.Length == 7 && hub.Model.Patients.Length == 9 && hub.Model.UnavailableCount == 1, "offline callbacks render seven playable patient cards; the unnamed and rate-limited records are hidden");
            ValidateMinimalExplore(hub, patientCards);
            Check(hub.content.GetComponentsInChildren<ShellButton>(true).All(button => { var bounds = button.GetComponent<BoxCollider>().size; return bounds.x >= .022f && bounds.y >= .022f; }), "all shell buttons meet the 22 mm minimum target size");
            var adult = hub.Model.Patients.Single(patient => patient.patientId == Female);
            var cardPress = ScalpalPointerProbe.Press(1, hub.input.origin, patientCards.Single(button => button.name == "Patient_" + adult.scenarioId).GetComponent<Collider>(), () => hub.input.Pointer(1), hub.input.StepPointers);
            Check(cardPress.RayMatchesAim && cardPress.hovered && cardPress.accentOnHover, "right controller aim ray focuses a patient card with the neutral rim");
            Check(hub.Model.SelectedPatientId == Female && hub.Model.SelectedBrief?.patientId == Female && hub.BeginButton && !hub.BeginButton.interactable && !hub.Transitioning && hub.Model.CanBegin, "actual card action loads matching offline chart but disables network-required Begin without transitioning");
            Check(!hub.Begin(), "offline chart browsing never attempts to create a network-only office encounter");
            ValidateBeginPointer(hub);
            ValidatePausePointer(hub);
            MeasureAndValidateTypography(hub, patientCards);
            Check(!hub.Model.Patients.Any(patient => patient.status == "blocked" || string.IsNullOrEmpty(patient.patientId)), "blocked and connection-only rows produce no patient ray targets");
            Check(!hub.Select("patient-demo-consent-revoked") && !hub.Begin(), "direct blocked selection and begin commands fail closed even without a visible card");
            var availableOffline = hub.Model.VisiblePatients();
            Check(availableOffline.Length == 7 && availableOffline.All(patient => patient.status == "ready" || patient.status == "needs_review"), "cached grid lists all seven named authored subjects");
            Check(!hub.Select("patient-demo-consent-partial") && !hub.Select("patient-demo-rate-limited"), "hidden unnamed and rate-limited records cannot be selected directly");
            foreach (var patient in availableOffline)
            {
                Check(hub.Select(patient.patientId) && hub.Model.SelectedBrief?.patientId == patient.patientId && !hub.CanBegin && hub.BeginButton && !hub.BeginButton.interactable && !hub.SkipButton.interactable, "every available subject loads matching offline chart without establishing live begin authority: " + patient.patientId);
                Check(DetailText(hub).Count(text => text.name == "Availability") == 1 && DetailText(hub).Single(text => text.name == "Availability").text == "Offline · reconnect to begin", "a disabled Begin explains itself in one short line: " + patient.patientId);
                hub.BeginButton.Press();
                Check(!hub.Begin() && !hub.Transitioning, "offline browsing never transitions for an otherwise available canonical subject: " + patient.patientId);
            }
            var capabilityFixture = JsonUtility.FromJson<PatientList>(JsonUtility.ToJson(new PatientList { patients = hub.Model.Patients }));
            var unsupported = capabilityFixture.patients.First(patient => patient.encounterAvailable && patient.status != "retry" && patient.patientId != Female);
            unsupported.encounterAvailable = false;
            hub.PatientsLoaded(capabilityFixture);
            Check(!hub.Select(unsupported.patientId) && !PatientCards(hub).Any(card => card.name == "Patient_" + unsupported.scenarioId) && !hub.Begin() && !hub.Transitioning, "a subject without an authored interview has no card and cannot be selected or begun");
            hub.Reload();
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
            hub.Select(Female);
            Check(ShellTransition.TryStageSelection(hub.Model.SelectedPatientId, hub.service.BaseUrl), "the production Begin handoff contract accepts supported selected patient plus explicit endpoint");
            Check(ShellTransition.TryConsumeSelection(out var selected) && selected.patientId == Female && selected.serviceUrl == hub.service.BaseUrl, "office consumes exact selected patient and service endpoint once");
            Check(!ShellTransition.TryConsumeSelection(out _), "consumed selection cannot start a second encounter");
            Check(!ShellTransition.TryStageSelection("patient/invalid", hub.service.BaseUrl) && !ShellTransition.TryStageSelection("", hub.service.BaseUrl) && !ShellTransition.TryStageSelection(Female, "file:///not-a-service"), "handoff rejects malformed or subjectless patient IDs and non-HTTP service endpoints");
            ValidatePaging(hub);
            Check(hub.content.GetComponentsInChildren<ScalpalTextFit>(true).All(fit => !fit.enabled), "shell text fitters are disabled after event-driven fitting rather than marshaling text every frame");
            Check(!hub.content.GetComponentsInChildren<TextMesh>(true).Any(), "shell renders no legacy TextMesh (SDF TextMeshPro only)");
            var position = hub.input.head.transform.position; var rotation = hub.input.head.transform.rotation;
            hub.input.head.transform.rotation = Quaternion.Euler(28, 15, 22);
            hub.input.Recenter();
            Check(ScalpalPlacement.IsLevel(hub.content), "recenter with a pitched and rolled head keeps the hub level (yaw only)");
            hub.input.head.transform.rotation = rotation;
            hub.input.Recenter();
            Check(hub.input.head.transform.position == position && hub.input.head.transform.rotation == rotation, "recenter moves presentation while preserving tracked camera pose");
            var grid = hub.content.GetComponentsInChildren<Transform>(true).Single(transform => transform.name == "PatientCards");
            float forwardDistance = Vector3.Dot(grid.position - position, Vector3.ProjectOnPlane(hub.input.head.transform.forward, Vector3.up).normalized);
            Check(Mathf.Abs(forwardDistance - 1.3f) < .02f, "recenter preserves the authored 1.3 metre grid distance without stacking local and world offsets");
        }

        // Launch lockup in the site's brand: dither mark, "Scalpal." in Instrument Serif, Geist Mono body, readable at 1.3 m.
        static void ValidateLaunchBrand(HubController hub)
        {
            var launch = hub.content.Find("Launch");
            var brand = ScalpalBrand.Active;
            var wordmark = launch.GetComponentsInChildren<TMPro.TextMeshPro>(true).Single(text => text.name == "Wordmark");
            Check(wordmark.text == "Scalpal." && wordmark.font == brand.display && wordmark.characterSpacing < 0, "launch wordmark is \"Scalpal.\" in tight-tracked Instrument Serif");
            Check(launch.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.sharedMaterial == brand.mark), "launch shows the dither logo mark");
            Check(launch.GetComponentsInChildren<ShellButton>(true).Length == 1 && launch.GetComponentsInChildren<TMPro.TextMeshPro>(true).Length == 2, "launch is only the wordmark and one Start button (no hints or meta text)");
            Check(launch.GetComponentsInChildren<TMPro.TextMeshPro>(true).Where(text => text != wordmark).All(text => text.font == brand.body || text.font == brand.label), "launch copy and buttons use Geist Mono");
            Check(!launch.GetComponentsInChildren<TMPro.TextMeshPro>(true).Any(text => text.text.Contains("SCALPAL") || text.text.Contains("S C A L")), "no spaced or uppercase SCALPAL wordmark");
            Check(ScalpalPlacement.IsLevel(launch), "launch panel is level with the horizon");
            var measured = ScalpalBrandLayout.Measure(launch, hub.input.head.transform.position);
            foreach (var item in measured) Check(item.Passes, "launch text meets its readability floor: '" + item.fit.Text.text + "' " + item.mmAt1m.ToString("F1") + " mm/m < " + item.minimum);
            UnityEngine.Debug.Log("SCALPAL_SHELL_LAUNCH_TEXT " + string.Join(" | ", measured.Select(item => item.fit.Text.text.Replace("\n", " ") + "=" + item.mmAt1m.ToString("F1") + "mm/m")));
        }
        static void ValidateBeginPointer(HubController hub)
        {
            var begin = hub.BeginButton;
            var disabled = ScalpalPointerProbe.Press(1, hub.input.origin, begin.GetComponent<Collider>(), () => hub.input.Pointer(1), hub.input.StepPointers);
            Check(disabled.RayMatchesAim && !disabled.hovered && !hub.Transitioning, "offline (disabled) Begin cannot be focused or pressed by the ray");
            var action = begin.action; int begun = 0;
            try
            {
                begin.interactable = true; begin.action = () => begun++;
                var enabled = ScalpalPointerProbe.Press(0, hub.input.origin, begin.GetComponent<Collider>(), () => hub.input.Pointer(0), hub.input.StepPointers);
                Check(enabled.hovered && enabled.accentOnHover && begun == 1, "an enabled Begin on the angled chart panel is hit by the left aim ray and pressed once: hovered=" + enabled.hovered + " accent=" + enabled.accentOnHover + " begun=" + begun + " visible=" + enabled.rayVisible + " end=" + enabled.lineEnd + " target=" + begin.transform.position);
            }
            finally { begin.action = action; begin.interactable = hub.CanBegin; }
            var skip = hub.SkipButton;
            Check(skip && skip.ghost && !skip.primary && Mathf.Abs(skip.transform.position.y - begin.transform.position.y) < .001f, "Skip to surgery is the ghost action on Begin's row");
            var skipDisabled = ScalpalPointerProbe.Press(1, hub.input.origin, skip.GetComponent<Collider>(), () => hub.input.Pointer(1), hub.input.StepPointers);
            Check(skipDisabled.RayMatchesAim && !skipDisabled.hovered && !hub.Transitioning, "offline (disabled) Skip to surgery cannot be focused or pressed by the ray");
            var skipAction = skip.action; int skipped = 0;
            try
            {
                skip.interactable = true; skip.action = () => skipped++;
                var enabled = ScalpalPointerProbe.Press(1, hub.input.origin, skip.GetComponent<Collider>(), () => hub.input.Pointer(1), hub.input.StepPointers);
                Check(enabled.hovered && enabled.accentOnHover && skipped == 1 && !hub.Transitioning, "an enabled Skip to surgery is hit by the right aim ray and pressed once without pressing Begin");
            }
            finally { skip.action = skipAction; skip.interactable = hub.CanBegin; }
        }
        static void ValidatePausePointer(HubController hub)
        {
            var host = new GameObject("ValidationPause");
            var instance = typeof(ShellPause).GetProperty("Instance");
            var previous = instance.GetValue(null);
            float timeScale = Time.timeScale;
            try
            {
                // Awake uses DontDestroyOnLoad (play mode only); wire the same fields it would.
                var pause = host.AddComponent<ShellPause>();
                var pauseInput = host.AddComponent<ShellInput>(); pauseInput.enabled = false;
                typeof(ShellPause).GetField("pauseInput", Private).SetValue(pause, pauseInput);
                instance.GetSetMethod(true).Invoke(null, new object[] { pause });
                pause.Configure(hub.input); pause.Pause();
                var menu = (Transform)typeof(ShellPause).GetField("panel", Private).GetValue(pause);
                Check(menu && ScalpalPlacement.IsLevel(menu), "pause menu spawns level with the horizon");
                hub.input.StepPointers();
                Check(hub.input.Pointer(0).Visual == null || !hub.input.Pointer(0).Visual.Visible, "paused hub hides its own rays");
                var resume = menu.GetComponentsInChildren<ShellButton>().Single(button => button.name == "Button_Resume");
                Check(resume.primary, "Resume is the primary (accent) action");
                var pressed = ScalpalPointerProbe.Press(1, pauseInput.origin, resume.GetComponent<Collider>(), () => pauseInput.Pointer(1), pauseInput.StepPointers);
                Check(pressed.RayMatchesAim && pressed.hovered && !pause.IsPaused, "right controller aim ray + trigger presses pause Resume");
            }
            finally
            {
                Time.timeScale = timeScale;
                UnityEngine.Object.DestroyImmediate(host);
                instance.GetSetMethod(true).Invoke(null, new[] { previous });
            }
        }

        static ShellButton[] PatientCards(HubController hub) => hub.content.GetComponentsInChildren<ShellButton>()
            .Where(button => button.name.StartsWith("Patient_", StringComparison.Ordinal)).ToArray();
        static void ValidatePaging(HubController hub)
        {
            var entries = Enumerable.Range(0, 25).Select(index => Entry("patient-validation-" + index.ToString("D2"), "ready", "lap_appendectomy", "urgent")).ToArray();
            hub.PatientsLoaded(new PatientList { patients = entries });
            Check(hub.Page == 0 && hub.PageCount == 3 && PatientCards(hub).Length == HubController.PageSize, "catalog expansion to 25 patients starts on a bounded first page of nine");
            float glassBottom = -GlassSize(hub).y / 2;
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int page = 0; page < 3; page++)
            {
                hub.SetPage(page); var cards = PatientCards(hub);
                var pagers = hub.content.GetComponentsInChildren<ShellButton>().Where(button => button.transform.parent.name == "Paging").ToArray();
                Check(cards.Length == (page < 2 ? 9 : 7) && pagers.Length == 2 && cards.All(card => pagers.All(pager => card.transform.localPosition.y - HubController.CardHeight * .5f > pager.transform.localPosition.y + .04f)) && pagers.All(pager => pager.transform.localPosition.y - .04f > glassBottom), "page " + page + " stays within three rows above the paging controls inside the glass");
                Check(cards.All(card => seen.Add(card.name)), "page " + page + " introduces no duplicate patient from another page");
            }
            Check(seen.Count == 25, "all 25 patients are reachable across pages without dropping rows");
            hub.SetPage(999); Check(hub.Page == 2, "page index clamps to the last available page");
            hub.SetPage(-1); Check(hub.Page == 0, "negative page index clamps to the first page");
            hub.Reload();
        }

        // Read-only physical measurements are logged separately from bounds assertions: passing
        // overlap checks does not establish Quest readability or compliance with the requested sizes.
        static void MeasureAndValidateTypography(HubController hub, ShellButton[] cards)
        {
            var cardFits = cards.SelectMany(card => card.GetComponentsInChildren<ScalpalTextFit>())
                .Where(fit => !string.IsNullOrWhiteSpace(fit.Text.text)).ToArray();
            foreach (var fit in cardFits) fit.Fit();
            var cardMm = cardFits.Select(fit => fit.MeasuredSize().y / Mathf.Max(1, fit.LineCount) / Vector3.Distance(fit.GetComponentInParent<ShellButton>().transform.position, hub.input.head.transform.position) * 1000).ToArray();
            bool minimumCardSizes = true;
            foreach (var card in cards)
            {
                var lines = card.GetComponentsInChildren<ScalpalTextFit>().Where(fit => !string.IsNullOrWhiteSpace(fit.Text.text)).OrderByDescending(fit => fit.transform.position.y).ToArray();
                float distance = Vector3.Distance(card.transform.position, hub.input.head.transform.position);
                minimumCardSizes &= lines.Length == 2 && lines.Select((fit, index) => fit.MeasuredSize().y / distance * 1000 >= (index == 0 ? 32 : 24) - .05f).All(value => value);
            }
            Check(minimumCardSizes, "both card text rows meet 32 mm name and 24 mm body at one-metre equivalent using each card's actual viewing distance: " + string.Join(" ", cards.Select(card => string.Join(",", card.GetComponentsInChildren<ScalpalTextFit>().Where(fit => !string.IsNullOrWhiteSpace(fit.Text.text)).OrderByDescending(fit => fit.transform.position.y).Select(fit => (fit.MeasuredSize().y / Vector3.Distance(card.transform.position, hub.input.head.transform.position) * 1000).ToString("F1"))))));
            var panel = hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart");
            var detailFits = panel.GetComponentsInChildren<ScalpalTextFit>().Where(fit => !string.IsNullOrWhiteSpace(fit.Text.text)).ToArray();
            foreach (var fit in detailFits) fit.Fit();
            var detailMm = detailFits.Select(fit => fit.MeasuredSize().y / Mathf.Max(1, fit.LineCount) / Vector3.Distance(panel.position, hub.input.head.transform.position) * 1000).ToArray();
            UnityEngine.Debug.Log("SCALPAL_SHELL_TEXT_MEASURE cardTargets=" + cards.Length +
                " selectableTargets=" + cards.Count(card => card.interactable) +
                " cardLineMmAt1m=" + cardMm.Min().ToString("F2") + ".." + cardMm.Max().ToString("F2") +
                " detailLineMmAt1m=" + detailMm.Min().ToString("F2") + ".." + detailMm.Max().ToString("F2") +
                " detailLineCounts=" + string.Join(",", detailFits.Select(fit => fit.LineCount)) +
                " cardAndDetailNormalization=actualPanelCenterDistance requestedBodyMm=24 requestedLabelMm=32 headset=false readabilityPassClaim=false");
            Check(detailMm.Min() >= ScalpalBrand.BodyMinimumMmAt1m - .05f, "selected chart detail text meets the 24 mm/m body floor at its viewing distance: min=" + detailMm.Min().ToString("F2"));
            foreach (var card in cards) ValidateTextRegions(card.transform, HubController.CardHeight / 2, "patient card " + card.name);
            ValidateTextRegions(panel, hub.DetailHalfHeight, "selected detail");
            ValidateDetailBesideGrid(hub);
            var originalCase = hub.Model.SelectedCase;
            string originalPresentation = originalCase.presentation;
            var originalBrief = hub.Model.SelectedBrief;
            try
            {
                // Stress the actual two-line complaint and seven-line chart render path, preserving
                // the original resource data and restoring the real selected chart immediately after.
                originalCase.presentation = string.Join(" ", Enumerable.Repeat("Authored presentation with additional context", 12));
                var longBrief = JsonUtility.FromJson<PreopBrief>(JsonUtility.ToJson(originalBrief));
                longBrief.chart = Enumerable.Range(0, 5).Select(index => new ChartLine { section = "Section " + index, text = string.Join(" ", Enumerable.Repeat("Returned chart context", 60)) }).ToArray();
                hub.BriefLoaded(longBrief);
                panel = hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart");
                var directText = DetailText(hub);
                Check(directText.Single(text => text.name == "Complaint").text.Split('\n').Length == 3 &&
                    directText.Single(text => text.name == "Highlights").text.Split('\n').Length == 3,
                    "long complaint and chart are bounded to three complaint lines and three one-line highlights");
                ValidateTextRegions(panel, hub.DetailHalfHeight, "maximum-line detail regression");
                ValidateDetailBesideGrid(hub);
            }
            finally { originalCase.presentation = originalPresentation; hub.BriefLoaded(originalBrief); }
        }
        static TMPro.TextMeshPro[] DetailText(HubController hub) => hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart")
            .GetComponentsInChildren<TMPro.TextMeshPro>().Where(text => !string.IsNullOrWhiteSpace(text.text) && !text.GetComponentInParent<ShellButton>()).ToArray();
        static Vector2 GlassSize(HubController hub) => hub.content.GetComponentsInChildren<Transform>(true).Single(item => item.name == "ExploreGlass").GetComponent<MeshFilter>().sharedMesh.bounds.size;

        // Clean, minimal explore: the serif title "Patients" and cards that show a name and one quiet line.
        static void ValidateMinimalExplore(HubController hub, ShellButton[] cards)
        {
            var explore = hub.content.Find("Explore");
            var brand = ScalpalBrand.Active;
            string[] removed = { "All", "Appendix", "Gallbladder", "Colon", "Any", "Emergency", "Urgent", "Elective", "Refresh", "Try again", "Previous", "Next" };
            Check(!explore.GetComponentsInChildren<Transform>(true).Any(item => item.name == "Filters") &&
                !explore.GetComponentsInChildren<ShellButton>(true).Any(button => button.label && removed.Contains(button.label.text)), "explore has no filter bar, Refresh, retry or paging controls for a single page");
            var chrome = explore.GetComponentsInChildren<TMPro.TextMeshPro>(true).Where(text => !string.IsNullOrWhiteSpace(text.text) && !text.GetComponentInParent<ShellButton>() && !text.transform.IsChildOf(explore.Find("PatientDetail"))).ToArray();
            Check(chrome.Length == 1 && chrome[0].text == "Patients" && chrome[0].font == brand.display, "the only explore text outside the cards is the serif title \"Patients\" (no count, hint or status footer): " + string.Join(" | ", chrome.Select(text => text.text)));
            // Independent of the model's filter: the packaged catalog's seven named, interview-capable patients.
            var expected = new[] { "Dolores Marchetti", "Harriet Lindqvist", "Ingrid Solano", "Jonah Okoye", "Morgan Rivera", "Priya Ramaswamy", "Theo Abernathy" };
            var names = cards.Select(card => card.GetComponentsInChildren<TMPro.TextMeshPro>().Single(text => text.name == "Name").text).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Check(names.SequenceEqual(expected), "every playable patient has exactly one card and no placeholder is shown: " + string.Join(", ", names));
            var line = new System.Text.RegularExpressions.Regex("^(\\d+ · )?(Appendix|Gallbladder|Colon)$");
            foreach (var card in cards)
            {
                var texts = card.GetComponentsInChildren<TMPro.TextMeshPro>().Where(text => !string.IsNullOrWhiteSpace(text.text)).ToArray();
                Check(texts.Length == 2 && texts[0].name == "Name" && texts[0].font == brand.display && texts[1].name == "Line" && line.IsMatch(texts[1].text) && Mathf.Abs(texts[1].color.a - .6f) < .01f,
                    "card shows only the serif name and one quiet age · procedure line: " + card.name + " " + string.Join(" / ", texts.Select(text => text.text)));
                var row = hub.Model.VisiblePatients().Single(patient => card.name == "Patient_" + patient.scenarioId);
                int marks = card.GetComponentsInChildren<LineRenderer>().Length;
                Check(marks == (row.status == "retry" ? 1 : 0), "a status mark appears only on a card that is still retrying: " + card.name);
                Check(card.lift && !card.primary && !card.ghost, "cards lift on hover with the neutral brand rim");
            }
            Check(cards.Single(card => card.name == "Patient_multi-source-overlap").GetComponentsInChildren<TMPro.TextMeshPro>().Single(text => text.name == "Line").text == "40 · Appendix", "Priya's card line reads \"40 · Appendix\"");
            var rects = cards.Select(card => new Rect((Vector2)card.transform.localPosition - new Vector2(HubController.CardWidth, HubController.CardHeight) / 2, new Vector2(HubController.CardWidth, HubController.CardHeight))).ToArray();
            Check(rects.SelectMany((a, i) => rects.Skip(i + 1).Select(b => (a, b))).All(pair => !pair.a.Overlaps(pair.b) &&
                (Mathf.Abs(pair.a.center.x - pair.b.center.x) >= HubController.CardWidth + HubController.ColumnGap - .001f || Mathf.Abs(pair.a.center.y - pair.b.center.y) >= HubController.CardHeight + HubController.RowGap - .001f)), "cards never overlap and keep the full column and row gutters");
            Check(cards.Select(card => card.transform.localPosition.x).Distinct().Count() == HubController.Columns && cards.Select(card => card.transform.localPosition.y).Distinct().Count() == 3, "seven cards occupy three columns and three rows");
            Check(Mathf.Abs(rects.Min(r => r.xMin) + rects.Max(r => r.xMax)) < .001f && rects.Where(r => Mathf.Approximately(r.center.y, rects.Min(m => m.center.y))).All(r => Mathf.Abs(r.center.x) < .001f), "grid is centred and a short last row is centred under it");
            var glass = GlassSize(hub);
            float side = glass.x / 2 - rects.Max(r => r.xMax), bottom = rects.Min(r => r.yMin) + glass.y / 2;
            Check(Mathf.Abs(side - bottom) < .001f && side >= .1f, "consistent side and bottom margins around the grid: side=" + side.ToString("F3") + " bottom=" + bottom.ToString("F3"));
            var title = chrome[0].GetComponent<ScalpalTextFit>(); var titleBounds = title.LocalBounds();
            Check(title.transform.localPosition.y - titleBounds.size.y > rects.Max(r => r.yMax) + .03f, "title sits clear above the first row of cards");
            Check(cards.All(button => { var bounds = button.GetComponent<BoxCollider>().size; return Mathf.Abs(bounds.x - HubController.CardWidth) < .001f && Mathf.Abs(bounds.y - HubController.CardHeight) < .001f; }), "each grid card is a 0.50 by 0.21 metre ray target");
            Check(hub.Model.Selected == null && !explore.Find("PatientDetail").GetComponentsInChildren<Transform>().Any(item => item.name == "SelectedChart"), "the side panel appears only once a card is selected");
        }

        // The side panel opens to the right of the grid and never overlaps it from the viewer's eye.
        static void ValidateDetailBesideGrid(HubController hub)
        {
            var panel = hub.content.GetComponentsInChildren<Transform>().Single(item => item.name == "SelectedChart");
            var glass = hub.content.GetComponentsInChildren<Transform>(true).Single(item => item.name == "ExploreGlass");
            var eye = hub.input.head.transform.position;
            float Yaw(Vector3 point) { var flat = Vector3.ProjectOnPlane(point - eye, Vector3.up); return Vector3.SignedAngle(Vector3.ProjectOnPlane(hub.input.head.transform.forward, Vector3.up), flat, Vector3.up); }
            float gridRight = Yaw(glass.TransformPoint(new Vector3(GlassSize(hub).x / 2, 0, 0)));
            float panelLeft = Yaw(panel.TransformPoint(new Vector3(-HubController.DetailWidth / 2, 0, 0)));
            Check(panelLeft > gridRight + 1, "detail panel sits to the right of the grid without overlapping it: grid edge " + gridRight.ToString("F1") + " deg, panel edge " + panelLeft.ToString("F1") + " deg");
            Check(Vector3.Angle(Vector3.ProjectOnPlane(-panel.forward, Vector3.up), Vector3.ProjectOnPlane(eye - panel.position, Vector3.up)) < 3, "detail panel faces the viewer");
            var texts = DetailText(hub).Select(text => text.name).ToArray();
            Check(texts.Take(4).SequenceEqual(new[] { "Name", "AgeSex", "Complaint", "Procedure" }) && texts.All(name => new[] { "Name", "AgeSex", "Complaint", "Procedure", "Highlights", "Availability" }.Contains(name)), "detail reads name, age · sex, complaint, procedure · urgency, highlights, then an availability line only when needed: " + string.Join(",", texts));
            Check(hub.BeginButton.transform.localPosition.y < DetailText(hub).Min(text => text.transform.localPosition.y - text.GetComponent<ScalpalTextFit>().MeasuredSize().y), "Begin and Skip sit below all detail text");
        }

        static void ValidateTextRegions(Transform panel, float halfHeight, string description)
        {
            var regions = panel.GetComponentsInChildren<ScalpalTextFit>()
                .Where(fit => !string.IsNullOrWhiteSpace(fit.Text.text)).Select(fit =>
                {
                    fit.Fit(); var bounds = fit.LocalBounds();
                    var first = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.min));
                    var second = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.max));
                    return new Rect(Mathf.Min(first.x, second.x), Mathf.Min(first.y, second.y), Mathf.Abs(second.x - first.x), Mathf.Abs(second.y - first.y));
                }).OrderByDescending(region => region.yMax).ToArray();
            Check(regions.All(region => region.yMin >= -halfHeight - .0005f && region.yMax <= halfHeight + .0005f), description + " measured glyph bounds stay inside panel");
            // Side-by-side buttons (Begin + Skip to surgery) share a row; text only collides if both axes overlap.
            bool separated = regions.SelectMany((a, i) => regions.Skip(i + 1).Select(b => (a, b))).All(pair =>
                pair.a.xMax <= pair.b.xMin + .0005f || pair.b.xMax <= pair.a.xMin + .0005f ||
                pair.a.yMin >= pair.b.yMax - .0005f || pair.b.yMin >= pair.a.yMax - .0005f);
            if (!separated) foreach (var fit in panel.GetComponentsInChildren<ScalpalTextFit>())
            {
                var bounds = fit.LocalBounds();
                var first = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.min));
                var second = panel.InverseTransformPoint(fit.transform.TransformPoint(bounds.max));
                UnityEngine.Debug.Log("SCALPAL_SHELL_TEXT_OVERLAP " + description + " y=" + first.y.ToString("F4") + ".." + second.y.ToString("F4") + " text=" + fit.Text.text.Replace("\n", " / "));
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
