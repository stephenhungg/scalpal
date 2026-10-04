using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Brand;
using Scalpal.Brand.Editor;
using Scalpal.EncounterOffice;
using Scalpal.EncounterOffice.Editor;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using Scalpal.Quest.Editor;
using Scalpal.Voice;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Handoff.Editor
{
    // Real scene/consumer boundary with synthetic server DTOs; never opens a socket or microphone.
    public static class HandoffValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;

        [MenuItem("Scalpal/Handoff/Validate Office to OR Contract")]
        public static void Verify()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var oldTicket = HandoffRun.Current;
            string preflight = JsonUtility.ToJson(HandoffRun.Preflight);
            checks = 0;
            try
            {
                Preflight();
                TimeOutGates();
                TheatreCardPointer();
                var state = new EncounterState { encounterId = "enc-handoff-fixture", patientId = "patient-handoff-fixture", phase = "scored",
                    assessment = new EncounterAssessment { procedure = "colectomy", diagnosis = "incorrect fixture" } };
                var score = Score();
                var candidate = new SurgicalCase { patientId = state.patientId, procedureId = score.procedureId, status = "ready",
                    procedure = new Procedure { id = score.procedureId }, brief = new PreopBrief { synthetic = true } };
                var ticket = HandoffRun.Begin(state, score, "http://localhost:8787/");
                Assert(ticket.procedureId == "lap_appendectomy" && ticket.procedureId != state.assessment.procedure && ticket.escalated,
                    "wrong plan loads the server's correct procedure and escalates");
                Assert(ticket.encounterId == state.encounterId && ticket.patientId == state.patientId && !string.IsNullOrEmpty(ticket.runId), "run retains encounter/patient identity");
                Assert(ticket.serviceUrl == "http://localhost:8787" && !ticket.practiceStarted && !ticket.AllConfirmed, "new run has endpoint and starts gated");
                Assert(HandoffRun.Supported(ticket.procedureId) && !HandoffRun.Supported("lap_cholecystectomy") && !HandoffRun.Supported("colectomy"), "unsupported procedures never masquerade as appendectomy");
                var copy = JsonUtility.FromJson<HandoffTicket>(JsonUtility.ToJson(ticket));
                Assert(copy.runId == ticket.runId && copy.encounterId == ticket.encounterId && copy.procedureId == ticket.procedureId, "ticket JSON preserves scene transport identity");
                Assert(copy.scorecard.criticalMissed.Select(item => item.id).SequenceEqual(new[] { "allergies" }) && copy.scorecard.criticalFound.Select(item => item.id).SequenceEqual(new[] { "medications" }), "critical findings survive JSON");
                Assert(copy.scorecard.carryoverItems.Length == 2 && copy.scorecard.carryoverItems[0].status == "found"
                    && copy.scorecard.carryoverItems[1].status == "missed" && copy.scorecard.carryoverItems[0].historyTopics[0] == "medications", "structured risk chips retain found/missed status and evidence");
                Assert(!copy.scorecard.procedureChosenCorrectly && copy.scorecard.diagnosisResult == "incorrect" && copy.scorecard.urgency == "urgent" && copy.scorecard.site == "Abdomen", "score DTO retains procedure decision, diagnosis, urgency and site");
                string risks = HandoffFlow.RiskText(copy.scorecard);
                Assert(risks.Contains("On apixaban (you asked)") && risks.Contains("Penicillin allergy (you missed this)"), "actual Time-Out copy renders structured found/missed labels");
                Assert(!risks.Contains("No chart risk chips returned"), "populated structured risks do not use empty-state copy");
                Assert(HandoffFlow.RiskText(new EncounterScore { carryoverItems = Array.Empty<EncounterCarryoverItem>() }).Contains("No chart risk chips returned"), "empty risk list has an explicit honest state");
                Identity(ticket, state, score, candidate);
                SceneBoundary(ticket, candidate);
                HandoffShellValidation.Verify();
                HandoffLifecycleValidation.Verify();
                HandoffOfficeBindingValidation.Verify();
                Debug.Log("SCALPAL_HANDOFF_VALIDATION_OK checks=" + checks + " synthetic DTOs and real scene consumers; no HTTP, reducer commit, voice-provider or headset verification");
            }
            finally
            {
                typeof(HandoffRun).GetProperty(nameof(HandoffRun.Current)).SetValue(null, oldTicket);
                JsonUtility.FromJsonOverwrite(preflight, HandoffRun.Preflight);
                if (previous.Any(item => item.isLoaded && item.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static EncounterScore Score() => JsonUtility.FromJson<EncounterScore>(@"{
            ""patientId"":""patient-handoff-fixture"",""patientName"":""Fixture"",""total"":52,""max"":100,""grade"":""D"",
            ""procedureId"":""lap_appendectomy"",""procedureTitle"":""Laparoscopic appendectomy"",""procedureChosenCorrectly"":false,
            ""diagnosisResult"":""incorrect"",""urgency"":""urgent"",""site"":""Abdomen"",""criticalMissed"":[{""kind"":""history"",""id"":""allergies"",""label"":""Allergies"",""why"":""Medication planning""}],""criticalFound"":[{""kind"":""history"",""id"":""medications"",""label"":""Medications"",""why"":""Bleeding risk""}],
            ""carryoverItems"":[{""flagId"":""anticoagulant"",""label"":""On apixaban"",""status"":""found"",""historyTopics"":[""medications""]},
            {""flagId"":""allergy"",""label"":""Penicillin allergy"",""status"":""missed"",""historyTopics"":[""allergies""]}]} ");

        static void Preflight()
        {
            for (int bits = 0; bits < 32; bits++)
            {
                var p = new TheatrePreflight { volunteerConsented = (bits & 1) != 0, cameraGranted = (bits & 2) != 0,
                    sceneGranted = (bits & 4) != 0, poseServiceOk = (bits & 8) != 0, coachServiceOk = (bits & 16) != 0 };
                bool ready = bits == 31;
                Assert(p.ArAvailable == ready && (p.DefaultMode == "mixed_reality") == ready, "AR requires every preflight gate: " + bits);
                Assert(HandoffRun.CanChoose("mixed_reality", p) == ready && HandoffRun.CanChoose("virtual", p), "AR is disabled and VR remains available: " + bits);
                Assert(ready ? p.UnavailableReason == "" : !string.IsNullOrEmpty(p.UnavailableReason), "red preflight supplies a reason: " + bits);
            }
            Assert(!HandoffRun.CanChoose("other", new TheatrePreflight()) && !HandoffRun.CanChoose("mixed_reality", null), "invalid modes/preflight rejected");
            Assert(new TheatrePreflight().UnavailableReason == "No volunteer checked in and consented", "consent failure reason");
            Assert(new TheatrePreflight { volunteerConsented = true }.UnavailableReason == "Camera and spatial permissions are off", "permission failure reason");
            Assert(new TheatrePreflight { volunteerConsented = true, cameraGranted = true, sceneGranted = true }.UnavailableReason == "Body detection offline", "pose failure reason");
        }

        static void TimeOutGates()
        {
            for (int bits = 0; bits < 64; bits++)
            {
                var ticket = new HandoffTicket { patientConfirmed = (bits & 1) != 0, procedureConfirmed = (bits & 2) != 0,
                    siteConfirmed = (bits & 4) != 0, risksConfirmed = (bits & 8) != 0,
                    antibioticsReviewed = (bits & 16) != 0, imagingReviewed = (bits & 32) != 0 };
                Assert(ticket.AllConfirmed == (bits == 63), "all six Time-Out rows gate practice: " + bits);
            }
            var complete = new HandoffTicket { patientConfirmed = true, procedureConfirmed = true, siteConfirmed = true,
                risksConfirmed = true, antibioticsReviewed = true, imagingReviewed = true, timeOutConfirmed = true,
                encounterId = "enc-preserved", attemptId = "attempt-preserved", scorecard = Score() };
            var score = complete.scorecard;
            complete.ResetTimeOut();
            Assert(!complete.AllConfirmed && !complete.timeOutConfirmed && !complete.patientConfirmed && !complete.procedureConfirmed
                && !complete.siteConfirmed && !complete.risksConfirmed && !complete.antibioticsReviewed && !complete.imagingReviewed,
                "reset clears all six rows and final confirmation");
            Assert(complete.encounterId == "enc-preserved" && complete.attemptId == "attempt-preserved" && ReferenceEquals(complete.scorecard, score),
                "Time-Out reset preserves encounter, attempt and diagnosis result");
        }

        static void Identity(HandoffTicket ticket, EncounterState state, EncounterScore score, SurgicalCase candidate)
        {
            Assert(HandoffRun.Verify(ticket, state, score, candidate, out var reason) && reason == "", "matching authoritative identities accepted");
            var mutations = new Action<HandoffTicket>[] { t => t.schema = "bad", t => t.contentVersion = "bad", t => t.patientId = "other", t => t.encounterId = "other", t => t.procedureId = "colectomy" };
            foreach (var mutation in mutations)
            {
                var altered = JsonUtility.FromJson<HandoffTicket>(JsonUtility.ToJson(ticket)); mutation(altered);
                Assert(!HandoffRun.Verify(altered, state, score, candidate, out reason) && reason.Length > 0, "tampered ticket rejected visibly");
            }
            state.phase = "attending";
            Assert(!HandoffRun.Verify(ticket, state, score, candidate, out reason), "unscored encounter rejected");
            AssertThrows(() => HandoffRun.Begin(state, score, "http://localhost:8787"), "Begin rejects unscored encounter");
            state.phase = "scored";
            candidate.brief.synthetic = false;
            Assert(!HandoffRun.Verify(ticket, state, score, candidate, out reason), "non-synthetic patient rejected");
            candidate.brief.synthetic = true; candidate.procedure.id = "other";
            Assert(!HandoffRun.Verify(ticket, state, score, candidate, out reason), "embedded procedure mismatch rejected");
            candidate.procedure.id = score.procedureId; candidate.status = "unavailable";
            Assert(!HandoffRun.Verify(ticket, state, score, candidate, out reason), "unavailable case rejected");
            candidate.status = "ready";
            var withoutRisks = Score(); withoutRisks.carryoverItems = null;
            Assert(!HandoffRun.Verify(ticket, state, withoutRisks, candidate, out reason), "missing structured risks rejected");
            AssertThrows(() => HandoffRun.Begin(state, withoutRisks, "http://localhost:8787"), "Begin requires structured risks");
        }

        static void SceneBoundary(HandoffTicket ticket, SurgicalCase candidate)
        {
            var office = EditorSceneManager.OpenScene(EncounterOfficeBuild.ScenePath, OpenSceneMode.Single);
            Assert(office.GetRootGameObjects().Any(root => root.GetComponentInChildren<NativeEncounterSession>(true)), "actual office producer exists");
            Assert(ReferenceEquals(ticket, HandoffRun.Current), "ticket survives opening office scene");
            var scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Single);
            NativeCaseSession session = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var found = root.GetComponentInChildren<NativeCaseSession>(true); if (found) session = found;
                root.SetActive(false);
            }
            Assert(session && session.presentation && session.voice && session.realtime, "actual OR consumer and bindings exist");
            Assert(ReferenceEquals(ticket, HandoffRun.Current), "same ticket survives office to OR scene load");
            session.realtime.autoConnect = false;
            Assert(session.SelectedPatientId == ticket.patientId && session.SelectedPatientId != NativeCaseSession.PatientId && session.SelectedProcedureId == ticket.procedureId, "OR uses ticket patient instead of demo constant");
            session.presentation.passthrough = false;
            var request = JsonUtility.FromJson<NativeCaseSession.CreateRequest>(JsonUtility.ToJson(session.CoachRequest()));
            Assert(request.patientId == ticket.patientId && request.encounterId == ticket.encounterId && request.mode == "virtual", "actual coach request JSON carries patient, encounter and chosen mode");

            HandoffRun.Preflight.volunteerConsented = HandoffRun.Preflight.cameraGranted = HandoffRun.Preflight.sceneGranted = HandoffRun.Preflight.poseServiceOk = HandoffRun.Preflight.coachServiceOk = true;
            ticket.attemptId = "attempt-before-practice"; ticket.sharedSessionId = "shared-fixture";
            Call(session.workbench, "Awake"); // Cache authored equipment reset poses without XR Update.
            Property(session, "Phase", "Confirmed"); // Case verified, before Time-Out/practice.
            var score = ticket.scorecard; string run = ticket.runId, encounter = ticket.encounterId;
            Assert(!HandoffRun.SwitchNeedsNewAttempt(ticket), "pre-practice switch retains attempt policy");
            int voiceGeneration = Get<int>(session.voice, "generation");
            var staleTool = new QuestJarvisVoice.ToolRequest { ToolCallId = "stale-fixture", ConnectionGeneration = voiceGeneration };
            Get<HashSet<string>>(session.voice, "pendingTools").Add(staleTool.ToolCallId);
            Assert(session.voice.OwnsClientTool(staleTool), "positive control: current voice tool is owned");
            Assert(session.TryChangePresentation(true) && ticket.attemptId == "attempt-before-practice" && ticket.presentationMode == "mixed_reality", "actual AR switch before Time-Out keeps attempt");
            Assert(Get<int>(session.voice, "generation") > voiceGeneration && !session.voice.OwnsClientTool(staleTool), "switch disconnect invalidates pending prior voice responses");
            HandoffRun.Preflight.poseServiceOk = false;
            Assert(session.TryChangePresentation(false) && ticket.attemptId == "attempt-before-practice", "AR failure fallback keeps attempt");
            Assert(!session.TryChangePresentation(true) && !session.presentation.passthrough, "actual red preflight blocks AR");
            HandoffRun.Preflight.poseServiceOk = true;
            StickToggle(session, ticket);
            Set(session, "candidate", candidate); Set(session, "previewScale", session.preview.transform.parent.localScale);
            Set(session, "sharedAttemptReady", true);
            ticket.patientConfirmed = ticket.procedureConfirmed = ticket.siteConfirmed = ticket.risksConfirmed = ticket.antibioticsReviewed = ticket.imagingReviewed = true;
            Assert(ticket.AllConfirmed, "six Time-Out confirmations required");
            Assert(session.TryChangePresentation(true) && session.presentation.passthrough && ticket.attemptId == "attempt-before-practice", "learner AR choice before practice keeps attempt");
            ticket.practiceStarted = true; Property(session, "Phase", "Practicing");
            Assert(HandoffRun.SwitchNeedsNewAttempt(ticket), "post-practice switch needs new attempt");
            Assert(!session.TryChangePresentation(false) && session.presentation.passthrough && ticket.practiceStarted && ticket.attemptId == "attempt-before-practice",
                "OR core refuses a mid-practice mode change without an explicit new attempt");
            PausedSwitchToVirtual(session, ticket);
            Assert(Get<bool>(session, "reviewed"), "handoff retry preserves review acknowledgement required by appendectomy cases");
            Call(session, "AttemptStarted", "attempt-after-practice");
            Assert(ticket.attemptId == "attempt-after-practice" && ticket.encounterId == encounter && ticket.runId == run && ReferenceEquals(ticket.scorecard, score), "new attempt acknowledgement preserves diagnosis and run");
            HandoffRecoveryValidation.Verify(session);
        }

        // A stray right-stick click must never change a theatre ticket's mode; standalone keeps the session's mode rules.
        static void StickToggle(NativeCaseSession session, HandoffTicket ticket)
        {
            var view = session.presentation;
            Action click = () => { Call(view, "StickToggle", false); Call(view, "StickToggle", true); };
            Assert(!view.passthrough && session.Phase == "Confirmed", "stick fixture starts in the pre-Time-Out virtual OR");
            click();
            Assert(!view.passthrough && ticket.presentationMode == "virtual" && ticket.attemptId == "attempt-before-practice", "right-stick click cannot change a theatre ticket's mode");
            typeof(HandoffRun).GetProperty(nameof(HandoffRun.Current)).SetValue(null, null);
            try
            {
                Property(session, "Phase", "Selecting"); click();
                Assert(view.passthrough, "positive control: standalone stick still selects AR during case selection");
                Property(session, "Phase", "Practicing"); click();
                Assert(view.passthrough, "standalone stick obeys the no-mode-change-during-practice rule");
                Property(session, "Phase", "Selecting"); click();
                Assert(!view.passthrough, "standalone stick returns to VR during selection");
            }
            finally { typeof(HandoffRun).GetProperty(nameof(HandoffRun.Current)).SetValue(null, ticket); Property(session, "Phase", "Confirmed"); }
        }

        // The paused/stopped/fit-loss cards' "Virtual OR (new attempt)" must actually start a new attempt.
        static void PausedSwitchToVirtual(NativeCaseSession session, HandoffTicket ticket)
        {
            var host = new GameObject("HandoffPausedSwitchFixture"); host.SetActive(false);
            try
            {
                var flow = host.AddComponent<HandoffFlow>();
                Set(flow, "card", UnityEngine.Object.Instantiate(Resources.Load<HandoffCard>("HandoffCard"), host.transform));
                Set(flow, "surgery", session); Set(flow, "phase", "paused");
                Call(flow, "SwitchToVirtual");
                Assert(!session.presentation.passthrough && ticket.presentationMode == "virtual" && ticket.modeChosenBy == "fallback",
                    "paused-practice Virtual OR switches mode");
                Assert(!Get<bool>(session, "sharedAttemptReady") && !ticket.practiceStarted && !ticket.AllConfirmed && ticket.attemptId == "" && Get<string>(flow, "phase") == "timeout",
                    "paused-practice switch requests a fresh attempt and a new Time-Out");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        // The real Theatre card: brand type, level placement, readable sizes, and both controllers' aim rays pressing options.
        static void TheatreCardPointer()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var origin = new GameObject("CardTrackingOrigin").transform; origin.SetPositionAndRotation(new Vector3(.4f, 0, -1), Quaternion.Euler(0, 25, 0));
            var camera = new GameObject("CardHead").AddComponent<Camera>(); camera.transform.SetParent(origin, false);
            camera.transform.SetLocalPositionAndRotation(new Vector3(0, 1.6f, 0), Quaternion.Euler(20, 10, 15));
            var card = UnityEngine.Object.Instantiate(Resources.Load<HandoffCard>("HandoffCard")); card.viewer = camera;
            try
            {
                int chosen = -1;
                card.Show("To theatre", "Priya Ramaswamy, 40\nLaparoscopic appendectomy · Urgent\nVolunteer patient: virtual organs on a real person.\nVirtual OR: a virtual patient in the operating room.",
                    new[] { "Volunteer patient (AR) · Recommended", "Virtual OR (VR)" }, index => chosen = index);
                Assert(ScalpalPlacement.IsLevel(card.transform), "Theatre card spawns level with the horizon even when the head is pitched and rolled");
                var brand = ScalpalBrand.Active;
                var texts = card.GetComponentsInChildren<TMPro.TextMeshPro>();
                Assert(texts.Single(text => text.name == "Title").font == brand.display && texts.Where(text => text.name != "Title").All(text => text.font == brand.body || text.font == brand.label), "card title is Instrument Serif; copy and actions are Geist Mono");
                Assert(!card.GetComponentsInChildren<TextMesh>(true).Any(), "card renders no legacy TextMesh");
                foreach (var item in ScalpalBrandLayout.Measure(card.transform, camera.transform.position))
                    Assert(item.Passes, "card text meets its readability floor: '" + item.fit.Text.text + "' " + item.mmAt1m.ToString("F1") + " mm/m");
                var targets = card.GetComponentsInChildren<HandoffCardTarget>();
                Assert(targets.Length == 2 && targets.All(target => target.GetComponent<BoxCollider>()), "each Theatre option is a ray collider");
                for (int hand = 0; hand < 2; hand++)
                {
                    chosen = -1;
                    var target = targets.Single(item => item.index == 1 - hand);
                    var result = ScalpalPointerProbe.Press(hand, origin, target.GetComponent<Collider>(), () => card.Pointer(hand), card.StepPointers);
                    Assert(result.RayMatchesAim && result.rayVisible && (result.lineStart - result.expectedOrigin).magnitude < 1e-4f, "card ray starts at the " + (hand == 0 ? "left" : "right") + " controller aim pose");
                    Assert(result.hovered && result.accentOnHover && chosen == 1 - hand, "aim ray + trigger chooses Theatre option " + (1 - hand) + " from the " + (hand == 0 ? "left" : "right") + " controller");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(card.gameObject); UnityEngine.Object.DestroyImmediate(origin.gameObject); }
        }

        static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, Private).GetValue(instance);
        static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Private).SetValue(instance, value);
        static void Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Private).Invoke(instance, args);
        static void Property(object instance, string name, object value) => instance.GetType().GetProperty(name).GetSetMethod(true).Invoke(instance, new[] { value });
        static void AssertThrows(Action action, string message) { try { action(); } catch (ArgumentException) { Assert(true, message); return; } throw new InvalidOperationException("Handoff validation failed: " + message); }
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Handoff validation failed: " + message); checks++; }
    }
}
