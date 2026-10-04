using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Quest;
using Scalpal.Voice;
using UnityEngine;

namespace Scalpal.Handoff.Editor
{
    // Steps the real recovery coroutine; HTTP response callbacks and relay receipts are synthetic.
    // Inactive scene components prevent provider, microphone, socket and editor-coroutine activity.
    public static class HandoffRecoveryValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;

        public static void Verify(NativeCaseSession session)
        {
            checks = 0;
            Assert(session && !session.isActiveAndEnabled && HandoffRun.Current != null, "inactive real consumer with handoff required");
            string encounter = HandoffRun.Current.encounterId;
            var candidate = new SurgicalCase { patientId = HandoffRun.Current.patientId, procedureId = HandoffRun.Current.procedureId,
                caseId = "recovery-fixture-case", procedure = new Procedure { firstStep = "umbilical", steps = Array.Empty<ProcedureStep>() } };
            Set(session, "candidate", candidate);
            session.presentation.passthrough = false;
            Property(session.workbench, "IsReady", true);
            Property(session.realtime, "Paired", true); Property(session.realtime, "SessionId", "recovery-shared"); Property(session.realtime, "AttemptId", "recovery-attempt");
            Set(session, "boundSharedSession", "recovery-shared"); Set(session, "boundSharedAttempt", "recovery-attempt"); Set(session, "sharedAttemptReady", true);
            Property(session, "Phase", "Practicing"); Set(session, "practicePaused", false);
            session.SetHandoffVoiceAllowed(false);

            Call(session, "ResetHandoffRecovery");
            var journal = Get<IList>(session, "localCoachEvents");
            Call(session, "RecordLocalCoachEvent", CaseEvent.PlacePort("umbilical"), "umbilical");
            Assert(journal.Count == 0, "normal live attempt does not journal or duplicate coach events");
            Set(session, "localCaptionAttempt", true);
            Call(session, "RecordLocalCoachEvent", CaseEvent.PlacePort("umbilical"), "umbilical");
            Call(session, "RecordLocalCoachEvent", CaseEvent.PlacePort("left_lower"), "working_ports");
            Assert(journal.Count == 2, "offline journal keeps every authored event in order");
            Assert((string)journal[0].GetType().GetField("Item2").GetValue(journal[0]) == "umbilical"
                && (string)journal[1].GetType().GetField("Item2").GetValue(journal[1]) == "working_ports", "journal retains the event's original validating step");

            Ready(session);
            var old = Start(session);
            Inject(old.Current, Created(candidate));
            Set(session, "generation", Get<int>(session, "generation") + 1);
            Assert(!old.MoveNext() && !Get<bool>(session, "recoveringLocalCoach") && !session.CoachPrepared, "late create response from old attempt cannot adopt a coach");

            Ready(session);
            var mismatch = Start(session);
            Inject(mismatch.Current, Created(candidate, "other-patient"));
            Assert(!mismatch.MoveNext() && !Get<bool>(session, "recoveringLocalCoach") && !session.CoachPrepared, "wrong-patient create response cannot adopt a coach");

            Ready(session);
            var paused = AtSynchronization(session, candidate);
            Set(session, "practicePaused", true);
            Property(session.coach, "IsSynchronized", true);
            Assert(!paused.MoveNext() && !Get<bool>(session, "recoveringLocalCoach") && !Get<bool>(session, "localCoachReady"), "pause while awaiting relay synchronization abandons recovery");

            Ready(session);
            var contextWait = AtContext(session, candidate);
            Inject(contextWait.Current, Context(candidate));
            Set(session, "practicePaused", true);
            Assert(!contextWait.MoveNext() && !session.CoachPrepared && !Get<bool>(session, "recoveringLocalCoach"), "pause while awaiting context cannot restore coach voice");

            Ready(session);
            var changedIdentity = AtContext(session, candidate);
            Inject(changedIdentity.Current, Context(candidate, "other-patient"));
            Assert(!changedIdentity.MoveNext() && !session.CoachPrepared, "context response must preserve patient identity");

            Ready(session);
            var changedAttempt = AtContext(session, candidate);
            Inject(changedAttempt.Current, Context(candidate));
            Property(session.realtime, "AttemptId", "other-attempt");
            Assert(!changedAttempt.MoveNext() && !session.CoachPrepared && !Get<bool>(session, "recoveringLocalCoach"), "shared attempt changed during context wait abandons recovery");
            Property(session.realtime, "AttemptId", "recovery-attempt");

            Ready(session);
            session.SetHandoffVoiceAllowed(true);
            int voiceEpoch = Get<int>(session.voice, "generation");
            var stale = new QuestJarvisVoice.ToolRequest { ToolCallId = "recovery-stale-tool", ConnectionGeneration = voiceEpoch };
            Get<HashSet<string>>(session.voice, "pendingTools").Add(stale.ToolCallId);
            Assert(session.voice.OwnsClientTool(stale), "positive control: pending tool belongs to current voice epoch");
            session.SetHandoffVoiceAllowed(false);
            Assert(Get<int>(session.voice, "generation") > voiceEpoch && !session.voice.OwnsClientTool(stale), "silent phase gate disconnect invalidates prior voice tool responses");
            int silentEpoch = Get<int>(session.voice, "generation");
            var complete = AtContext(session, candidate);
            Inject(complete.Current, Context(candidate));
            Assert(!complete.MoveNext() && Get<bool>(session, "localCoachReady") && !Get<bool>(session, "recoveringLocalCoach") && session.CoachPrepared,
                "matching recovery can cache a coach without changing the local attempt");
            Assert(Get<int>(session.voice, "generation") == silentEpoch && !session.voice.Connected && session.voice.Status != "connecting",
                "cached recovery obeys silent handoff phase and never starts voice transport");
            Assert(HandoffRun.Current.encounterId == encounter, "recovery preserves diagnosis encounter identity");
            Call(session, "ResetHandoffRecovery");
            Assert(journal.Count == 0 && !Get<bool>(session, "localCoachReady") && !Get<bool>(session, "recoveringLocalCoach") && !Get<bool>(session, "localCaptionAttempt"),
                "new attempt clears recovery journal and all recovery latches");
            Debug.Log("SCALPAL_HANDOFF_RECOVERY_VALIDATION_OK checks=" + checks + " actual recovery coroutine; synthetic HTTP callbacks/relay receipts; no provider or network evidence");
        }

        static void Ready(NativeCaseSession session)
        {
            Call(session, "ResetHandoffRecovery"); Set(session, "localCaptionAttempt", true); Set(session, "practicePaused", false);
            Set(session, "coachSessionId", ""); Set(session, "generation", Get<int>(session, "generation") + 1);
            session.coach.UseSession(""); session.SetHandoffVoiceAllowed(false);
        }
        static IEnumerator Start(NativeCaseSession session)
        {
            var routine = (IEnumerator)Call(session, "RecoverLocalCoach", Get<int>(session, "generation"));
            Assert(routine.MoveNext() && routine.Current is IEnumerator, "recovery requests a new coach via actual request coroutine");
            return routine;
        }
        static IEnumerator AtSynchronization(NativeCaseSession session, SurgicalCase candidate)
        {
            var routine = Start(session); Inject(routine.Current, Created(candidate));
            Assert(routine.MoveNext() && routine.Current == null && Get<bool>(session, "recoveringLocalCoach"), "valid create enters relay synchronization and gates scoring");
            return routine;
        }
        static IEnumerator AtContext(NativeCaseSession session, SurgicalCase candidate)
        {
            var routine = AtSynchronization(session, candidate);
            Property(session.coach, "IsSynchronized", true);
            Assert(routine.MoveNext() && routine.Current is IEnumerator, "synchronized empty journal requests restored context");
            return routine;
        }
        static string Created(SurgicalCase c, string patient = null) => "{\"sessionId\":\"recovery-coach\",\"snapshot\":{" + Identity(c, patient) + "}}";
        static string Context(SurgicalCase c, string patient = null) => "{\"context\":\"Restored fixture\",\"snapshot\":{\"sessionId\":\"recovery-coach\"," + Identity(c, patient) + "}}";
        static string Identity(SurgicalCase c, string patient) => "\"patientId\":\"" + (patient ?? c.patientId) + "\",\"procedureId\":\"" + c.procedureId + "\",\"caseId\":\"" + c.caseId + "\",\"mode\":\"virtual\"";
        static void Inject(object requestRoutine, string response)
        {
            var callback = requestRoutine.GetType().GetFields(Private | BindingFlags.Public)
                .FirstOrDefault(field => field.FieldType == typeof(Action<string>) && field.GetValue(requestRoutine) != null);
            if (callback == null) throw new InvalidOperationException("Synthetic response callback missing on actual request coroutine.");
            ((Action<string>)callback.GetValue(requestRoutine))(response);
        }
        static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
        static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private).GetValue(target);
        static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        static void Property(object target, string name, object value) => target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value });
        static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Handoff recovery validation failed: " + message); checks++; }
    }
}
