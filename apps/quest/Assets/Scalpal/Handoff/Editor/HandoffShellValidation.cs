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
using Scalpal.Shell;
using Scalpal.Voice;
using UnityEngine;

namespace Scalpal.Handoff.Editor
{
    // Actual handoff coroutine/update methods with synthetic HTTP callbacks and shell state.
    // No scene load, XR input, network connection, or microphone is started by this fixture.
    public static class HandoffShellValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        public static void Verify()
        {
            checks = 0;
            var oldTicket = HandoffRun.Current;
            var oldPause = ShellPause.Instance;
            bool oldBusy = ShellTransition.Busy;
            var fixture = new GameObject("HandoffShellBoundaryFixture"); fixture.SetActive(false);
            try
            {
                var flow = fixture.AddComponent<HandoffFlow>();
                var card = fixture.AddComponent<HandoffCard>(); Set(flow, "card", card);
                var native = fixture.AddComponent<NativeCaseSession>();
                native.anatomy = fixture.AddComponent<AnatomyController>();
                native.coach = fixture.AddComponent<CoachRelay>();
                native.voice = fixture.AddComponent<QuestJarvisVoice>();
                var other = new GameObject("ReplacementConsumer"); other.SetActive(false); other.transform.SetParent(fixture.transform);
                var replacement = other.AddComponent<NativeCaseSession>();
                var pause = fixture.AddComponent<ShellPause>();
                StaticProperty(typeof(ShellPause), "Instance", pause);
                StaticProperty(typeof(ShellTransition), "Busy", false);

                var accepted = Begin(flow, native, pause);
                var acceptedRoutine = Request(flow);
                Respond(acceptedRoutine.Current, accepted);
                Assert(!acceptedRoutine.MoveNext() && accepted.preopResult?.patientId == accepted.patientId,
                    "positive control: current Time-Out accepts matching structured review response");
                Assert(!Get<bool>(flow, "loading"), "completed current Time-Out releases loading latch");

                Rejected(flow, native, pause, "shell pause", () => Property(pause, "IsPaused", true));
                Rejected(flow, native, pause, "focus loss", () => Set(flow, "focused", false));
                Rejected(flow, native, pause, "phase change", () => Set(flow, "phase", "paused"));
                Rejected(flow, native, pause, "global scene transition", () => StaticProperty(typeof(ShellTransition), "Busy", true));
                Rejected(flow, native, pause, "changed attempt", () => HandoffRun.Current.attemptId = "replacement-attempt");
                Rejected(flow, native, pause, "replacement native consumer", () => Set(flow, "surgery", replacement));
                Rejected(flow, native, pause, "cleared ticket", HandoffRun.Clear);
                Rejected(flow, native, pause, "replacement ticket", () => Ticket());

                var mismatched = Begin(flow, native, pause);
                var mismatchRoutine = Request(flow);
                Inject(mismatchRoutine.Current, "{\"patientId\":\"other-patient\",\"caseId\":\"shell-case\"}");
                Assert(!mismatchRoutine.MoveNext() && mismatched.preopResult == null && !mismatched.timeOutConfirmed,
                    "wrong-patient review response cannot unlock Time-Out");
                Assert(Get<string>(flow, "failure").Contains("identity mismatch"), "review identity refusal is visible");

                var pausedTicket = Begin(flow, native, pause);
                native.SetHandoffVoiceAllowed(true);
                native.anatomy.SetRegistrationValid(true);
                int epoch = Get<int>(native.voice, "generation");
                var tool = new QuestJarvisVoice.ToolRequest { ToolCallId = "shell-pause-tool", ConnectionGeneration = epoch };
                Get<HashSet<string>>(native.voice, "pendingTools").Add(tool.ToolCallId);
                Assert(native.voice.OwnsClientTool(tool), "positive control: pending voice tool belongs to current generation");
                Set(flow, "loading", true); Set(flow, "focused", false); Set(flow, "shellPaused", false);
                Property(pause, "IsPaused", true);
                Call(flow, "Update");
                Assert(Get<bool>(native, "practicePaused") && !Get<bool>(native, "handoffVoiceAllowed") && !native.anatomy.RegistrationValid,
                    "global shell pause gates practice and voice even during loading/focus loss");
                Assert(!native.voice.OwnsClientTool(tool) && Get<int>(native.voice, "generation") > epoch,
                    "global pause invalidates old voice tool ownership");
                Assert(ReferenceEquals(HandoffRun.Current, pausedTicket), "pause retains the encounter ticket");

                Property(native, "Phase", "Recap");
                int generation = Get<int>(native, "generation");
                pausedTicket.patientConfirmed = true;
                Call(native, "WorkbenchRetry");
                Assert(Get<int>(native, "generation") == generation && pausedTicket.patientConfirmed,
                    "native menu retry cannot bypass handoff recap ownership");
                native.presentation = fixture.AddComponent<NativePresentation>(); native.presentation.passthrough = false;
                pausedTicket.presentationMode = "virtual"; pausedTicket.practiceStarted = true;
                Set(flow, "phase", "paused"); Set(flow, "surgery", native);
                Call(flow, "SwitchToVirtual");
                Assert(Get<string>(flow, "phase") == "paused" && pausedTicket.practiceStarted,
                    "redundant virtual-mode choice cannot reopen Time-Out or reset practice on the same attempt");
                Debug.Log("SCALPAL_HANDOFF_SHELL_VALIDATION_OK checks=" + checks
                    + " actual handoff coroutines/update; synthetic shell state and HTTP callbacks; no headset or network evidence");
            }
            finally
            {
                StaticProperty(typeof(HandoffRun), "Current", oldTicket);
                StaticProperty(typeof(ShellPause), "Instance", oldPause);
                StaticProperty(typeof(ShellTransition), "Busy", oldBusy);
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }

        static void Rejected(HandoffFlow flow, NativeCaseSession native, ShellPause pause, string reason, Action interrupt)
        {
            var ticket = Begin(flow, native, pause);
            var routine = Request(flow); Respond(routine.Current, ticket);
            interrupt();
            Assert(!routine.MoveNext() && ticket.preopResult == null && !ticket.timeOutConfirmed && !ticket.practiceStarted,
                "pending Time-Out cannot complete after " + reason);
            Assert(HandoffRun.Current == null || HandoffRun.Current.preopResult == null,
                "old response cannot attach a result to replacement state after " + reason);
        }
        static HandoffTicket Begin(HandoffFlow flow, NativeCaseSession native, ShellPause pause)
        {
            Set(flow, "surgery", native); Set(flow, "loading", false); Set(flow, "focused", true); Set(flow, "phase", "timeout"); Set(flow, "failure", "");
            Property(pause, "IsPaused", false); StaticProperty(typeof(ShellTransition), "Busy", false);
            return Ticket();
        }
        static HandoffTicket Ticket()
        {
            var state = new EncounterState { encounterId = "enc-shell-fixture", patientId = "patient-shell-fixture", phase = "scored" };
            var score = new EncounterScore { patientId = state.patientId, procedureId = "lap_appendectomy", carryoverItems = Array.Empty<EncounterCarryoverItem>() };
            var ticket = HandoffRun.Begin(state, score, "http://localhost:8787");
            ticket.attemptId = "shell-attempt";
            ticket.verifiedCase = new SurgicalCase { patientId = ticket.patientId, caseId = "shell-case" };
            ticket.patientConfirmed = ticket.procedureConfirmed = ticket.siteConfirmed = ticket.risksConfirmed = ticket.antibioticsReviewed = ticket.imagingReviewed = true;
            return ticket;
        }
        static IEnumerator Request(HandoffFlow flow)
        {
            var routine = (IEnumerator)Call(flow, "ConfirmTimeOut");
            Assert(routine.MoveNext() && routine.Current is IEnumerator, "Time-Out starts its real HTTP coroutine");
            return routine;
        }
        static void Respond(object request, HandoffTicket ticket) => Inject(request,
            JsonUtility.ToJson(new PreopCheckResult { patientId = ticket.patientId, caseId = ticket.verifiedCase.caseId, score = 1, total = 1, passed = true }));
        static void Inject(object request, string json)
        {
            var callback = request.GetType().GetFields(Private | BindingFlags.Public)
                .FirstOrDefault(field => field.FieldType == typeof(Action<string>) && field.GetValue(request) != null);
            if (callback == null) throw new InvalidOperationException("Actual Time-Out HTTP response callback was not found.");
            ((Action<string>)callback.GetValue(request))(json);
        }
        static object Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Private).Invoke(instance, args);
        static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, Private).GetValue(instance);
        static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Private).SetValue(instance, value);
        static void Property(object instance, string name, object value) => instance.GetType().GetProperty(name).GetSetMethod(true).Invoke(instance, new[] { value });
        static void StaticProperty(Type type, string name, object value) => type.GetProperty(name).GetSetMethod(true).Invoke(null, new[] { value });
        static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException("Handoff shell validation failed: " + reason); checks++; }
    }
}
