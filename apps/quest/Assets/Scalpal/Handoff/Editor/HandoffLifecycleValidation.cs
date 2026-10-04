using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using Scalpal.Realtime;
using Scalpal.Shell;
using Scalpal.Voice;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Handoff.Editor
{
    // Drives the actual HandoffFlow lifecycle/update/card callbacks on inactive fixtures.
    // Each scenario names the learner-facing rule it protects; all scenarios run and every failure is reported.
    // No scene load, XR input, socket, provider or microphone is started.
    public static class HandoffLifecycleValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static int checks;
        static readonly List<GameObject> fixtures = new List<GameObject>();

        public static void Verify()
        {
            checks = 0;
            var oldTicket = HandoffRun.Current;
            var oldPreflight = HandoffRun.Preflight;
            var oldPause = ShellPause.Instance;
            bool oldBusy = ShellTransition.Busy;
            var failures = new List<string>();
            var scenarios = new (string name, Action run)[] {
                ("F1 headset resume keeps the paused card and the started attempt", ResumeKeepsPracticePaused),
                ("F3 another office patient clears the previous ticket", OfficePatientChangeClearsTicket),
                ("F4 participant recording permission ends with the run", ConsentEndsWithRun),
                ("F17 setup separates AR availability and optional recording", SetupSeparatesArAndRecording),
                ("F5 Time-Out sends only individually confirmed risks", RiskReviewSendsOnlyConfirmed),
                ("F6/F8 Time-Out clears stale errors and releases its loading latch", TimeOutClearsErrorAndLatch),
                ("F14 AR Time-Out captures the measured vitals baseline; VR keeps the chart", TimeOutBaselineOnlyInAr),
                ("F6/F7 a new office run starts without old errors or coach/fit-loss state", NewRunResetsState),
                ("F7 the fit-loss ladder restarts after leaving practice", FitLossLadderIsPerPractice),
                ("F9 legacy office route starts in the virtual OR", LegacyOfficeRouteStartsVirtual),
                ("F10 preflight checks do not flicker AR availability", PreflightDoesNotFlicker),
                ("F11 the wrong-plan challenge is shown once", ChallengeShownOnce),
                ("F12 waiting card has a way back and does not import every frame", WaitingCardReturnsAndThrottles),
                ("F13 voice gate follows the actual phase transitions", VoiceGateFollowsPhases),
                ("F13 unified scene order is checked against the product order", SceneOrderIsIndependent),
                ("F15 OR entry shows no connection error while the case loads or the session joins", EntryShowsNoConnectionErrorWhileLoading),
                ("F18 the instrument table and tools stay out of view until practice begins", ToolsHiddenUntilPractice),
                ("F19 End surgery from the pause menu resumes into the finish, not the Paused card", EndSurgerySkipsPausedCard),
            };
            try
            {
                foreach (var scenario in scenarios)
                {
                    int before = checks;
                    try { Reset(); scenario.run(); Debug.Log("SCALPAL_HANDOFF_LIFECYCLE_SCENARIO_OK " + scenario.name + " checks=" + (checks - before)); }
                    catch (Exception error)
                    {
                        var cause = error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
                        failures.Add(scenario.name + ": " + cause.Message);
                        Debug.LogError("SCALPAL_HANDOFF_LIFECYCLE_SCENARIO_FAILED " + scenario.name + ": " + cause);
                    }
                    finally { foreach (var item in fixtures) if (item) UnityEngine.Object.DestroyImmediate(item); fixtures.Clear(); }
                }
            }
            finally
            {
                StaticProperty(typeof(HandoffRun), "Current", oldTicket);
                StaticProperty(typeof(HandoffRun), "Preflight", oldPreflight);
                StaticProperty(typeof(ShellPause), "Instance", oldPause);
                StaticProperty(typeof(ShellTransition), "Busy", oldBusy);
                EncounterOfficeRoute.ClearSurgery();
            }
            if (failures.Count > 0)
                throw new InvalidOperationException("Handoff lifecycle validation failed (" + failures.Count + "/" + scenarios.Length + "):\n" + string.Join("\n", failures));
            Debug.Log("SCALPAL_HANDOFF_LIFECYCLE_VALIDATION_OK checks=" + checks + " scenarios=" + scenarios.Length
                + " actual HandoffFlow lifecycle/update/card callbacks; synthetic DTOs, no HTTP response, headset or provider evidence");
        }

        static void Reset()
        {
            StaticProperty(typeof(HandoffRun), "Current", null);
            StaticProperty(typeof(HandoffRun), "Preflight", new TheatrePreflight());
            StaticProperty(typeof(ShellPause), "Instance", null);
            StaticProperty(typeof(ShellTransition), "Busy", false);
            EncounterOfficeRoute.ClearSurgery();
        }

        // ---- Scenarios -------------------------------------------------------------------------------

        // A VR learner doffs and dons the headset mid-practice. Unity delivers Pause(false) then Focus(true).
        // The learner must land on the paused card with the same attempt, and the coach must stay down.
        static void ResumeKeepsPracticePaused()
        {
            var flow = Flow(out var card); var native = Native(flow.gameObject);
            var ticket = Ticket("virtual"); Confirm(ticket); ticket.practiceStarted = true; string attempt = ticket.attemptId;
            Set(flow, "surgery", native); Set(flow, "phase", "practice");
            Property(native, "HandoffVerified", true);
            native.SetHandoffVoiceAllowed(true);
            Call(flow, "OnApplicationFocus", false); Call(flow, "OnApplicationPause", true);
            Assert(Get<bool>(native, "practicePaused") && !Get<bool>(native, "handoffVoiceAllowed"), "suspend pauses practice and drops the coach");
            Call(flow, "OnApplicationPause", false); Call(flow, "OnApplicationFocus", true);
            Assert(Phase(flow) == "paused", "pause(false)+focus(true) resume lands on the paused card, not registration (was " + Phase(flow) + ")");
            Tick(flow);
            Assert(Heading(card) == "Paused" && Actions(card)[0] == "Resume", "paused card with Resume is rendered after resume");
            Assert(!Get<bool>(native, "handoffVoiceAllowed"), "coach voice stays disallowed while paused");

            Set(flow, "phase", "register"); Call(flow, "Registration");
            Assert(Phase(flow) == "paused", "VR registration re-entry mid-practice returns to paused, never Time-Out (was " + Phase(flow) + ")");
            Set(flow, "phase", "timeout"); Set(native, "coachSessionId", "coach-fixture"); Set(flow, "nextCoachRetry", 0f);
            Call(flow, "TimeOut");
            Assert(Phase(flow) == "paused" && !Get<bool>(native, "handoffVoiceAllowed"), "Time-Out never re-arms the coach for a started attempt");
            Set(flow, "phase", "timeout"); Set(flow, "loading", false); Set(flow, "focused", true);
            var routine = (IEnumerator)Call(flow, "ConfirmTimeOut");
            Assert(!routine.MoveNext() && !Get<bool>(flow, "loading"), "Begin practice cannot re-post or restart a started attempt");
            Assert(ticket.practiceStarted && ticket.attemptId == attempt && ticket.AllConfirmed, "attempt, practice progress and Time-Out survive the resume");
        }

        // The learner reaches the OR with every required service up. Loading the case (about a second over adb) and the OR's
        // own SpacetimeDB join are normal; the card must not offer "Retry connection" as if something failed.
        static void EntryShowsNoConnectionErrorWhileLoading()
        {
            var flow = Flow(out var card); var native = Native(flow.gameObject); Ticket("virtual");
            Set(flow, "surgery", native); Set(flow, "phase", "register");
            bool NoError() => Actions(card).All(label => label.IndexOf("retry", StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf("connection", StringComparison.OrdinalIgnoreCase) < 0);
            Set(native, "busy", true);
            Call(flow, "Registration");
            Assert(Heading(card) == "Preparing the operating room" && NoError(), "case loading shows a quiet preparing card without a connection retry (was " + Heading(card) + ")");
            Set(native, "busy", false);
            Call(flow, "Registration");
            Assert(Heading(card) == "Case unavailable" && Actions(card)[0] == "Try again", "an actual load failure offers one plain retry");
            ReadyForTimeOut(flow, native); Property(native.realtime, "Paired", false); Set(native.realtime, "connecting", true); Set(native.realtime, "connectStarted", Time.realtimeSinceStartup);
            Call(flow, "TimeOut");
            Assert(Heading(card) == "Preparing the operating room" && NoError(), "joining the shared session shows the preparing card, not a connection error (was " + Heading(card) + ")");
            Set(native.realtime, "connecting", false);
            Call(flow, "TimeOut");
            Assert(Heading(card) == "Shared headset session unavailable", "a session that is really unreachable still says so");
        }

        // The learner returns to the office picker and starts patient B; patient A's Theatre card must not survive.
        static void OfficePatientChangeClearsTicket()
        {
            var flow = Flow(out var card);
            var ticket = Ticket("virtual"); HandoffRun.Preflight.volunteerConsented = true;
            var office = Office(flow.gameObject, new EncounterState { encounterId = "int-patientbfixture", patientId = "patient-b-fixture", phase = "interview" }, null);
            Set(flow, "office", office); Set(flow, "phase", "theatre");
            Tick(flow);
            Assert(HandoffRun.Current == null && Phase(flow) == "office", "a different office encounter drops the previous patient's ticket");
            Assert(!HandoffRun.Preflight.volunteerConsented, "a new office run clears participant recording permission");
            Assert(!card.Visible || !Heading(card).StartsWith("To theatre", StringComparison.Ordinal), "patient A's Theatre card is not shown for patient B");
            Assert(ticket.encounterId != office.State.encounterId, "fixture compares two encounters");
        }

        static void SetupSeparatesArAndRecording()
        {
            var flow = Flow(out var card);
            var p = HandoffRun.Preflight;
            p.cameraGranted = p.sceneGranted = p.poseServiceOk = p.coachServiceOk = true;
            p.learnerCaptureConsented = p.volunteerConsented = false;
            var ticket = Ticket("mixed_reality"); ticket.consequenceSeen = true;
            Call(flow, "SetupCard");
            Assert(p.ArAvailable && Actions(card).Length == 2 && Actions(card)[0] == "Back to theatre", "AR-ready setup has no volunteer confirmation or permission-request control");
            Assert(!p.learnerCaptureConsented, "AR availability never turns on motion recording");
            Select(card, 1); Call(flow, "SetupCard");
            // Controller motion never captures the participant, so there is no participant permission to ask for.
            Assert(p.learnerCaptureConsented && !p.volunteerConsented && p.ArAvailable && Actions(card).Length == 2
                && Actions(card)[1] == "Withdraw motion-recording consent",
                "actual recording callback enables only learner motion recording");
            Select(card, 1); Call(flow, "SetupCard");
            Assert(!p.learnerCaptureConsented && !p.volunteerConsented && p.ArAvailable && Actions(card).Length == 2,
                "turning recording off clears both permissions while AR stays available");
            Select(card, 0);
            Assert(Phase(flow) == "theatre", "reindexed Back to theatre callback returns to the mode choice");
        }

        // Back to explore ends the run and clears optional participant recording permission.
        static void ConsentEndsWithRun()
        {
            var flow = Flow(out _);
            Ticket("virtual"); HandoffRun.Preflight.volunteerConsented = true; HandoffRun.Preflight.cameraGranted = true;
            Set(flow, "failure", "old failure"); Set(flow, "coachTried", true); Set(flow, "lossStarted", 3f); Set(flow, "realigns", 2);
            Call(flow, "BindScene");
            Assert(HandoffRun.Current == null && !HandoffRun.Preflight.volunteerConsented, "returning to explore clears the ticket and participant recording permission");
            Assert(HandoffRun.Preflight.cameraGranted, "measured permission state is not discarded with consent");
            Assert(Get<string>(flow, "failure") == "" && !Get<bool>(flow, "coachTried") && Get<float>(flow, "lossStarted") < 0 && Get<int>(flow, "realigns") == 0,
                "per-run error, coach-retry and fit-loss state do not leak into the next run");
        }

        // Two office risks; the learner plans for one and skips the other. Only the planned type may be sent.
        static void RiskReviewSendsOnlyConfirmed()
        {
            var flow = Flow(out var card); var native = Native(flow.gameObject);
            var ticket = Ticket("virtual");
            ticket.scorecard.carryoverItems = new[] {
                new EncounterCarryoverItem { flagId = "f1", type = "bleeding", label = "On apixaban", status = "found", detail = "Anticoagulant" },
                new EncounterCarryoverItem { flagId = "f2", type = "allergy", label = "Penicillin allergy", status = "missed", detail = "Prophylaxis choice" } };
            ReadyForTimeOut(flow, native);
            Call(flow, "TimeOut");
            // No Time-Out panel: the checklist completes itself, and only risks the learner asked about in the office count.
            Assert(ticket.AllConfirmed && !card.Visible, "Time-Out completes without a panel");
            Assert(ticket.confirmedRiskTypes.SequenceEqual(new[] { "bleeding" }), "only the risk the learner found in the office is planned for");
            var routine = (IEnumerator)Call(flow, "ConfirmTimeOut");
            Assert(routine.MoveNext() && routine.Current is IEnumerator, "Time-Out posts the review");
            string body = Field<string>(routine.Current, "body");
            Assert(body != null && body.Contains("\"bleeding\"") && !body.Contains("\"allergy\""), "preop-check receives only the confirmed risk: " + body);
            ticket.ResetTimeOut();
            Assert(!ticket.risksConfirmed && Get<int>(ticket, "riskReviewIndex") == 0 && Get<List<string>>(ticket, "confirmedRiskTypes").Count == 0, "Time-Out reset clears per-risk decisions");
        }

        static void TimeOutClearsErrorAndLatch()
        {
            var flow = Flow(out _); var native = Native(flow.gameObject);
            var ticket = Ticket("virtual"); Confirm(ticket);
            Set(flow, "surgery", native); Set(flow, "phase", "timeout"); Set(flow, "failure", "Finish the interview before entering the OR.");
            var routine = (IEnumerator)Call(flow, "ConfirmTimeOut");
            Assert(routine.MoveNext() && Get<string>(flow, "failure") == "", "a new Begin practice clears the previous error");
            HandoffRun.Clear();
            Assert(!routine.MoveNext() && !Get<bool>(flow, "loading"), "a response for a cleared ticket releases the loading latch");
        }

        // AR: the coach freezes the volunteer's Presage baseline at the Time-Out (POST with an empty body).
        // VR has no volunteer, so the charted vitals stay and nothing is posted.
        static void TimeOutBaselineOnlyInAr()
        {
            var flow = Flow(out _); var native = Native(flow.gameObject);
            Set(native, "coachSessionId", "coach-fixture");
            Assert(native.TimeOutBaseline() == null, "VR Time-Out posts no baseline");
            native.presentation.passthrough = true;
            var request = native.TimeOutBaseline();
            Assert(request != null && Field<string>(request, "method") == "POST" && Field<string>(request, "path") == "/coach/sessions/coach-fixture/vitals/baseline"
                && Field<string>(request, "body") == "{}", "AR Time-Out posts an empty body to the session's vitals baseline");
            Set(native, "coachSessionId", "");
            Assert(native.TimeOutBaseline() == null, "a captions-only Time-Out without a coach session posts nothing");
        }

        static void ToolsHiddenUntilPractice()
        {
            var flow = Flow(out _); var native = Native(flow.gameObject);
            native.workbench = native.gameObject.AddComponent<NativeWorkbench>();
            var tool = GameObject.CreatePrimitive(PrimitiveType.Cube).AddComponent<Scalpal.Instruments.InstrumentBehaviour>();
            var table = GameObject.CreatePrimitive(PrimitiveType.Cube); table.name = "Workbench";
            try
            {
                native.workbench.tools = new[] { tool };
                Set(flow, "surgery", native);
                bool Hidden(GameObject item) => item.GetComponentsInChildren<Renderer>(true).All(renderer => renderer.forceRenderingOff);
                foreach (var phase in new[] { "register", "briefing", "timeout" })
                {
                    Set(flow, "phase", phase); Call(flow, "ShowSurgeryTools", !flow.PreparingTheatre);
                    Assert(Hidden(tool.gameObject) && Hidden(table), "the instrument table and tools are not drawn during " + phase);
                }
                Assert(tool.GetComponent<Collider>().enabled && tool.gameObject.activeSelf, "hiding the tools keeps their colliders and active state");
                Set(flow, "phase", "practice"); Call(flow, "ShowSurgeryTools", !flow.PreparingTheatre);
                Assert(!Hidden(tool.gameObject) && tool.GetComponentsInChildren<Renderer>().All(renderer => !renderer.forceRenderingOff) && table.GetComponent<Renderer>().forceRenderingOff == false,
                    "the tools and table appear when practice begins");
            }
            finally { UnityEngine.Object.DestroyImmediate(tool.gameObject); UnityEngine.Object.DestroyImmediate(table); }
        }

        static void EndSurgerySkipsPausedCard()
        {
            var flow = Flow(out var card); var native = Native(flow.gameObject);
            var ticket = Ticket("virtual"); ReadyForTimeOut(flow, native);
            ticket.practiceStarted = true; Set(flow, "phase", "practice");
            Call(flow, "ResumeGate");
            Assert(Get<string>(flow, "phase") == "paused", "a plain resume after the pause menu shows the Paused card");
            Set(flow, "phase", "practice"); flow.RequestEndSurgery(); Call(flow, "ResumeGate");
            Assert(Get<string>(flow, "phase") == "practice" && !card.Visible, "End surgery resumes straight into practice so the finish can run");
        }

        static void NewRunResetsState()
        {
            var flow = Flow(out _);
            var office = Office(flow.gameObject, new EncounterState { encounterId = "int-officefixture1", patientId = "patient-office-fixture", phase = "scored",
                assessment = new EncounterAssessment { procedure = "colectomy" } }, Score("patient-office-fixture"));
            Set(flow, "failure", "Finish the interview before entering the OR."); Set(flow, "coachTried", true); Set(flow, "realigns", 2);
            Assert((bool)Call(flow, "ImportOffice", office) && HandoffRun.Current != null, "positive control: scored office imports a ticket");
            Assert(Get<string>(flow, "failure") == "" && !Get<bool>(flow, "coachTried") && Get<int>(flow, "realigns") == 0,
                "the new run does not inherit an old error or coach-retry state");
        }

        static void FitLossLadderIsPerPractice()
        {
            var flow = Flow(out _); var native = Native(flow.gameObject);
            Ticket("mixed_reality"); Set(flow, "surgery", native);
            Set(flow, "phase", "practice"); Set(flow, "lossStarted", 1f);
            Call(flow, "SetPhase", "stopped");
            Assert(Get<float>(flow, "lossStarted") < 0, "leaving practice resets the 15/45 s fit-loss timer");
        }

        static void LegacyOfficeRouteStartsVirtual()
        {
            var go = Fixture("LegacyOfficeRouteFixture");
            var native = go.AddComponent<NativeCaseSession>();
            native.presentation = go.AddComponent<NativePresentation>(); native.presentation.passthrough = true;
            var state = new EncounterState { encounterId = "int-legacyroute1", patientId = "patient-legacy-fixture", phase = "scored",
                assessment = new EncounterAssessment { procedure = "colectomy" } };
            Assert(EncounterOfficeRoute.PrepareSurgery(state, Score(state.patientId), "lap_appendectomy", "http://localhost:8787", out var reason, "legacy-session", "legacy-attempt"),
                "positive control: legacy producer prepared: " + reason);
            Call(native, "Awake");
            Assert(native.OfficeHandoff != null && !native.HasHandoff, "legacy no-ticket consumer adopted the office snapshot");
            Assert(!native.presentation.passthrough && native.PresentationMode == "virtual", "legacy route without preflight never starts in AR");
        }

        static void PreflightDoesNotFlicker()
        {
            var flow = Flow(out _); var native = Native(flow.gameObject);
            native.bodyRegistration = native.gameObject.AddComponent<NativeBodyRegistration>(); native.bodyRegistration.endpoint = "http://pose.fixture:9911";
            Set(flow, "surgery", native);
            HandoffRun.Preflight.poseServiceOk = HandoffRun.Preflight.coachServiceOk = true;
            var routine = (IEnumerator)Call(flow, "CheckPreflight");
            Assert(routine.MoveNext() && routine.Current is IEnumerator, "health check issues its first request");
            Assert(HandoffRun.Preflight.poseServiceOk && HandoffRun.Preflight.coachServiceOk, "AR stays available while a health check is in flight");
            Assert(Field<string>(routine.Current, "url") == "http://pose.fixture:9911/health", "pose health uses the configured registration endpoint");
        }

        static void ChallengeShownOnce()
        {
            var flow = Flow(out var card);
            var ticket = Ticket("virtual"); ticket.escalated = ticket.challengeSeen = ticket.consequenceSeen = true;
            Set(flow, "phase", "score"); Tick(flow);
            Assert(Heading(card).StartsWith("Interview score · 70/100", StringComparison.Ordinal) && Get<string>(card, "copy").Contains("Missed · What is the plan? Right answer: Laparoscopic appendectomy") && Actions(card).SequenceEqual(new[] { "To theatre" }), "compact interview scorecard: score, missed rounds with the right answer, one To theatre button");
            Select(card, 0);
            Assert(Phase(flow) == "theatre", "To theatre after a completed challenge goes straight to Theatre (was " + Phase(flow) + ")");
            ticket.consequenceSeen = false; Set(flow, "phase", "score"); Tick(flow); Select(card, 0);
            Assert(Phase(flow) == "consequence", "an unfinished consequence resumes without repeating the challenge");
            ticket.challengeSeen = false; Set(flow, "phase", "score"); Tick(flow); Select(card, 0);
            Assert(Phase(flow) == "challenge", "positive control: a first escalation still gets the one challenge");
        }

        static void WaitingCardReturnsAndThrottles()
        {
            var flow = Flow(out var card);
            var office = Office(flow.gameObject, new EncounterState { encounterId = "int-waitingfixture", patientId = "patient-office-fixture", phase = "scored",
                assessment = new EncounterAssessment { procedure = "colectomy" } }, Score("patient-office-fixture"));
            Set(office, "blocking", 1); // an interview request is still pending: import must refuse.
            Set(flow, "office", office); Set(flow, "phase", "office");
            Tick(flow);
            var actions = Actions(card);
            Assert(Heading(card) == "Waiting for scored office attempt" && actions.Length == 2 && actions[0] == "Refresh scorecard"
                && (actions[1] == "Back to explore" || actions[1] == "Back to patient picker"), "waiting card offers a return action");
            Set(flow, "failure", "sentinel"); Tick(flow);
            Assert(Get<string>(flow, "failure") == "sentinel", "import is throttled instead of retried every frame");
        }

        static void VoiceGateFollowsPhases()
        {
            var flow = Flow(out _); var native = Native(flow.gameObject);
            Ticket("virtual"); Set(flow, "surgery", native);
            foreach (var phase in new[] { "timeout", "practice" })
            {
                native.SetHandoffVoiceAllowed(false); Call(flow, "SetPhase", phase);
                Assert(Get<bool>(native, "handoffVoiceAllowed"), "coach voice is allowed only once Time-Out/practice is entered: " + phase);
            }
            foreach (var phase in new[] { "transition", "register", "paused", "stopped", "recap" })
            {
                native.SetHandoffVoiceAllowed(true);
                int generation = Get<int>(native.voice, "generation");
                var tool = new QuestJarvisVoice.ToolRequest { ToolCallId = "phase-" + phase, ConnectionGeneration = generation };
                Get<HashSet<string>>(native.voice, "pendingTools").Add(tool.ToolCallId);
                Call(flow, "SetPhase", phase);
                Assert(!Get<bool>(native, "handoffVoiceAllowed") && !native.voice.OwnsClientTool(tool), "entering " + phase + " drops coach voice and its pending tools");
                Set(native, "coachSessionId", "coach-fixture"); native.ReconnectTimeOutVoice();
                Assert(native.voice.Status != "connecting" && !native.voice.Connected, "no coach reconnect is possible in " + phase);
            }
        }

        static void SceneOrderIsIndependent()
        {
            var validate = typeof(ScalpalPlayerBuild).GetMethod("ValidateSceneOrder", new[] { typeof(EditorBuildSettingsScene[]) });
            Assert(validate != null, "scene order validator accepts an explicit scene list");
            Action<EditorBuildSettingsScene[]> check = scenes => validate.Invoke(null, new object[] { scenes });
            check(EditorBuildSettings.scenes); Assert(true, "committed build settings match Shell, office, OR, ending");
            var generated = ScalpalPlayerBuild.PlayerScenes();
            check(generated); Assert(true, "unified builder generates the product order");
            var mutations = new Func<EditorBuildSettingsScene[], EditorBuildSettingsScene[]>[] {
                s => new[] { s[0], s[2], s[1], s[3] },
                s => new[] { s[1], s[0], s[2], s[3] },
                s => s.Take(3).ToArray(),
                s => new[] { s[0], s[1], s[2], new EditorBuildSettingsScene(s[3].path, false) },
                s => new[] { new EditorBuildSettingsScene(s[0].path, false), s[1], s[2], s[3] },
                s => s.Concat(new[] { new EditorBuildSettingsScene("Assets/Scalpal/Quest/Scenes/NativeSession.unity", true) }).ToArray() };
            foreach (var mutate in mutations)
            {
                bool threw = false;
                try { check(mutate(generated)); } catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { threw = true; }
                Assert(threw, "a wrong unified scene order is rejected");
            }
        }

        // ---- Fixtures --------------------------------------------------------------------------------

        static GameObject Fixture(string name) { var go = new GameObject(name); go.SetActive(false); fixtures.Add(go); return go; }
        static HandoffFlow Flow(out HandoffCard card)
        {
            var go = Fixture("HandoffLifecycleFixture");
            var flow = go.AddComponent<HandoffFlow>();
            var prefab = Resources.Load<HandoffCard>("HandoffCard");
            if (!prefab) throw new InvalidOperationException("HandoffCard resource prefab missing.");
            card = UnityEngine.Object.Instantiate(prefab, go.transform);
            Set(flow, "card", card); Set(flow, "nextHealth", float.MaxValue); Set(flow, "focused", true);
            return flow;
        }
        static NativeCaseSession Native(GameObject go)
        {
            var native = go.AddComponent<NativeCaseSession>();
            native.anatomy = go.AddComponent<AnatomyController>();
            native.coach = go.AddComponent<CoachRelay>();
            native.voice = go.AddComponent<QuestJarvisVoice>();
            native.presentation = go.AddComponent<NativePresentation>(); native.presentation.passthrough = false;
            return native;
        }
        static void ReadyForTimeOut(HandoffFlow flow, NativeCaseSession native)
        {
            native.workbench = native.gameObject.AddComponent<NativeWorkbench>(); Property(native.workbench, "IsReady", true);
            native.realtime = native.gameObject.AddComponent<QuestSessionBridge>(); Property(native.realtime, "Paired", true);
            Property(native, "HandoffVerified", true); Set(native, "coachSessionId", "coach-fixture");
            Set(flow, "surgery", native); Set(flow, "phase", "timeout"); Set(flow, "fitConfirmed", true); Set(flow, "nextCoachRetry", float.MaxValue);
        }
        static NativeEncounterSession Office(GameObject go, EncounterState state, EncounterScore score)
        {
            var office = go.AddComponent<NativeEncounterSession>();
            Property(office, "State", state); Property(office, "Score", score); Property(office, "AuthoredProcedureId", "lap_appendectomy");
            Set(office, "patientId", state.patientId); Set(office, "encounterId", state.encounterId);
            Set(office, "sharedSessionId", "office-session"); Set(office, "sharedAttemptId", "office-attempt");
            return office;
        }
        static EncounterScore Score(string patient) => new EncounterScore { kind = "interview", patientId = patient, patientName = "Fixture", procedureId = "lap_appendectomy",
            procedureTitle = "Laparoscopic appendectomy", total = 70, max = 100, grade = "Solid", spoken = "", site = "Abdomen", urgency = "urgent",
            sections = new[] { new EncounterScoreSection { id = "plan", label = "Plan", score = 0, max = 15 } },
            rounds = new[] { new InterviewRoundResult { stage = "plan", prompt = "What is the plan?", max = 15,
                picked = new InterviewPickedChoice { key = "B", text = "Observe overnight", grade = "wrong" }, best = new InterviewPickedChoice { key = "A", text = "Laparoscopic appendectomy" } } },
            feedback = new[] { "The surgery this patient needs is a laparoscopic appendectomy." },
            carryoverItems = Array.Empty<EncounterCarryoverItem>() };
        static HandoffTicket Ticket(string mode)
        {
            var state = new EncounterState { encounterId = "int-lifecyclefixture", patientId = "patient-lifecycle-fixture", phase = "scored" };
            var ticket = HandoffRun.Begin(state, Score(state.patientId), "http://localhost:8787");
            ticket.presentationMode = mode; ticket.attemptId = "lifecycle-attempt"; ticket.sharedSessionId = "lifecycle-session";
            ticket.verifiedCase = new SurgicalCase { patientId = ticket.patientId, caseId = "lifecycle-case", urgency = "urgent" };
            return ticket;
        }
        static void Confirm(HandoffTicket ticket) => ticket.patientConfirmed = ticket.procedureConfirmed = ticket.siteConfirmed
            = ticket.risksConfirmed = ticket.antibioticsReviewed = ticket.imagingReviewed = true;

        static void Tick(HandoffFlow flow) { Set(flow, "nextRefresh", 0f); Call(flow, "Update"); }
        static string Phase(HandoffFlow flow) => Get<string>(flow, "phase");
        static string Heading(HandoffCard card) => Get<string>(card, "heading") ?? "";
        static string[] Actions(HandoffCard card) => Get<string[]>(card, "actions");
        static void Select(HandoffCard card, int index)
        {
            var callback = Get<Action<int>>(card, "selected");
            if (callback == null) throw new InvalidOperationException("Card has no production selection callback.");
            callback(index);
        }

        static T Field<T>(object instance, string name)
        {
            var field = instance.GetType().GetFields(Private | BindingFlags.Public).FirstOrDefault(item => item.Name == name && item.FieldType == typeof(T));
            if (field == null) throw new InvalidOperationException("Field " + name + " missing on " + instance.GetType().Name);
            return (T)field.GetValue(instance);
        }
        static object Call(object instance, string method, params object[] args)
        {
            var info = instance.GetType().GetMethod(method, Private | BindingFlags.Public);
            if (info == null) throw new InvalidOperationException("Method " + method + " missing on " + instance.GetType().Name);
            return info.Invoke(instance, args);
        }
        static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, Private | BindingFlags.Public).GetValue(instance);
        static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Private).SetValue(instance, value);
        static void Property(object instance, string name, object value) => instance.GetType().GetProperty(name).GetSetMethod(true).Invoke(instance, new[] { value });
        static void StaticProperty(Type type, string name, object value) => type.GetProperty(name, AnyStatic).GetSetMethod(true).Invoke(null, new[] { value });
        static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); checks++; }
    }
}
