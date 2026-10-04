using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Scalpal.Instruments;
using Scalpal.Quest;
using Scalpal.Realtime;
using Scalpal.Voice;
using SpacetimeDB.Types;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Surgery.Editor
{
    // SpacetimeDB as the operating room's shared command path: handInstrument / highlightInstrument rows from any actor
    // (browser scrub nurse, coach, Jarvis) go through the same hand-over and highlight code as the voice tools, resolve
    // exactly once, and Jarvis's own tool calls become requestCommand rows when paired, falling back to the local
    // hand-over (the demo-fallback build's behavior) when unpaired, refused or silent. Stub bridge, no network, no headset.
    public static class SharedInstrumentCommandValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] sealed class ToolResult { public string type, tool_call_id, result; public bool is_error; }

        [MenuItem("Scalpal/Surgery/Validate Shared Instrument Commands")]
        public static void Run()
        {
            var fixture = new GameObject("SharedInstrumentCommandValidation");
            try
            {
                var stand = new GameObject("Stand").transform; stand.SetParent(fixture.transform, false); stand.position = new Vector3(.6f, .9f, .2f);
                InstrumentBehaviour Tool(string id, float x)
                {
                    var go = new GameObject(id); go.transform.SetParent(stand, false); go.transform.localPosition = new Vector3(x, 0, 0);
                    var mesh = GameObject.CreatePrimitive(PrimitiveType.Cube); mesh.transform.SetParent(go.transform, false); mesh.transform.localScale = new Vector3(.02f, .01f, .15f);
                    var tool = go.AddComponent<InstrumentBehaviour>(); tool.instrumentId = id; tool.CaptureRestPose();
                    return tool;
                }
                XRInstrumentInput Hand(XRNode node) { var go = new GameObject(node.ToString()); go.transform.SetParent(fixture.transform, false); var input = go.AddComponent<XRInstrumentInput>(); input.controller = node; return input; }
                var scalpel = Tool("scalpel", 0); var hemostat = Tool("hemostat", .06f); var retractor = Tool("retractor", .12f); var babcock = Tool("babcock", .18f);
                var left = Hand(XRNode.LeftHand); var right = Hand(XRNode.RightHand);
                var leftGrip = left.GetComponent<InstrumentInteractor>(); var rightGrip = right.GetComponent<InstrumentInteractor>();
                leftGrip.SetTrackedPose(new Vector3(-.2f, 1, 0), Quaternion.identity, true, 0);
                rightGrip.SetTrackedPose(new Vector3(.2f, 1.1f, -.1f), Quaternion.identity, true, 0);

                var bridge = fixture.AddComponent<QuestSessionBridge>(); bridge.autoConnect = false;
                var requests = new List<Reducer.RequestCommand>();
                Set(bridge, "requestSender", (Action<Reducer.RequestCommand>)requests.Add);
                SetProperty(bridge, "Paired", true); SetProperty(bridge, "SessionId", "session-or"); SetProperty(bridge, "AttemptId", "attempt-or");
                SetProperty(bridge, "ObservedState", new ExerciseState { SessionId = "session-or", AttemptId = "attempt-or", StepVersion = 3 });
                bridge.PublishSnapshot("Practicing", "incise", 1, 5, "", "", false, false, true, "Authored virtual mannequin fit");

                var voice = fixture.AddComponent<QuestJarvisVoice>();
                var native = fixture.AddComponent<NativeCaseSession>(); native.voice = voice; native.realtime = bridge;
                native.workbench = fixture.AddComponent<NativeWorkbench>(); native.workbench.inputs = new[] { left, right };
                native.workbench.tools = new[] { scalpel, hemostat, retractor, babcock };
                var hint = fixture.AddComponent<SurgeryTriggerHint>();
                SetProperty(native, "Phase", "Practicing");
                Set(native, "sharedAttemptReady", true); Set(native, "boundSharedSession", "session-or"); Set(native, "boundSharedAttempt", "attempt-or");

                // Voice connection that captures client_tool_result messages.
                Set(voice, "generation", 7); SetProperty(voice, "Status", "connected");
                var connectionType = typeof(QuestJarvisVoice).GetNestedType("Connection", BindingFlags.NonPublic);
                object connection = Activator.CreateInstance(connectionType, new object[] { 7 });
                connectionType.GetField("Open").SetValue(connection, true); Set(voice, "active", connection);
                var outgoing = (ConcurrentQueue<string>)connectionType.GetField("Outgoing").GetValue(connection);
                List<ToolResult> Results() { var list = new List<ToolResult>(); while (outgoing.TryDequeue(out string m)) list.Add(JsonUtility.FromJson<ToolResult>(m)); return list; }
                int toolCalls = 0;
                QuestJarvisVoice.ToolRequest Jarvis(string tool, string json)
                {
                    var request = new QuestJarvisVoice.ToolRequest { ToolCallId = "jarvis-call-" + (++toolCalls), ToolName = tool, ParametersJson = json, ConnectionGeneration = 7 };
                    Get<HashSet<string>>(voice, "pendingTools").Add(request.ToolCallId);
                    Invoke(native, "VoiceTool", request);
                    return request;
                }
                ToolResult Resolved(QuestJarvisVoice.ToolRequest request, string because)
                {
                    var results = Results();
                    Assert(results.Count == 1 && results[0].tool_call_id == request.ToolCallId, because + ": the tool call resolves exactly once (" + results.Count + ")");
                    voice.ResolveClientTool(request, "late duplicate");
                    Assert(outgoing.IsEmpty, because + ": the tool call cannot be resolved twice");
                    return results[0];
                }
                void Pump(float now) => Invoke(native, "PumpSharedTools", now);

                // The bridge's command delivery: one pending row dispatched to the scene adapter, one acknowledgement.
                long Generation() => Get<long>(bridge, "generation");
                Command Row(string id, string action, string target, double? side, string role) => new Command
                {
                    CommandId = id, SessionId = "session-or", AttemptId = "attempt-or", Action = action, TargetId = target, ArgNumber = side,
                    ExpectedStepVersion = 3, RequestedRole = role, Status = "pending"
                };
                (string outcome, string reason, long acknowledgements) Deliver(Command command)
                {
                    Set(bridge, "dispatchedCommand", command); Set(bridge, "resolution", null); Set(bridge, "resolutionReason", null);
                    long before = Generation();
                    Invoke(native, "SharedCommand", command);
                    return (Get<string>(bridge, "resolution"), Get<string>(bridge, "resolutionReason"), Generation() - before);
                }

                // 1. A browser scrub nurse's handInstrument row: hemostat to the right hand, applied once, attributed at the hand.
                var nurse = Deliver(Row("nurse-hand-0001", "handInstrument", "hemostat", 1, "viewer"));
                Assert(nurse.outcome == "applied" && nurse.reason == null && nurse.acknowledgements == 1, "handInstrument resolves applied exactly once: " + nurse.outcome + "/" + nurse.acknowledgements);
                Assert(rightGrip.HeldInstrument == hemostat && hemostat.Held && hemostat.transform.parent == right.transform, "the hemostat is in the right hand");
                Assert(hint.Text == "Hemostat · from nurse", "a tiny attribution cue at the hand: " + hint.Text);
                Assert(Results().Count == 0, "a nurse's command answers no Jarvis tool call");

                // 2. Rejections carry the voice tool's reasons back to the shared row.
                var already = Deliver(Row("nurse-hand-0002", "handInstrument", "hemostat", 1, "operator"));
                Assert(already.outcome == "rejected" && already.reason == "hemostat already in your right hand" && already.acknowledgements == 0, "already in hand is rejected with a reason: " + already.reason);
                var missing = Deliver(Row("nurse-hand-0003", "handInstrument", "bone_saw", null, "viewer"));
                Assert(missing.outcome == "rejected" && missing.reason == "bone saw is not in this case", "an instrument off this case is rejected: " + missing.reason);
                Set(native, "practicePaused", true);
                var paused = Deliver(Row("nurse-hand-0004", "handInstrument", "scalpel", 0, "viewer"));
                Assert(paused.outcome == "rejected" && paused.reason == "practice is paused" && !leftGrip.HeldInstrument, "a paused practice rejects the hand-over: " + paused.reason);
                Set(native, "practicePaused", false);
                var light = Deliver(Row("nurse-light-0001", "highlightInstrument", "retractor", null, "viewer"));
                Assert(light.outcome == "applied" && native.InstrumentCallout && native.InstrumentCallout.Target == retractor, "highlightInstrument outlines the retractor");

                // 3. Jarvis acts through the same reducer: swap_instrument becomes a requestCommand row, nothing moves until
                // that row comes back through the subscription, and the tool call answers with the command's outcome.
                var swap = Jarvis("swap_instrument", "{\"instrument\":\"scalpel\",\"hand\":\"left\"}");
                Assert(requests.Count == 1, "a paired Jarvis swap writes one requestCommand");
                var sent = requests[0];
                Assert(sent.CommandId.StartsWith("jarvis-", StringComparison.Ordinal) && sent.SessionId == "session-or" && sent.Action == "handInstrument"
                    && sent.TargetId == "scalpel" && sent.ArgNumber == 0 && sent.ArgBool == null && sent.ExpectedStepVersion == 3, "requestCommand carries handInstrument scalpel left at the observed step version");
                Assert(!leftGrip.HeldInstrument && outgoing.IsEmpty, "nothing is applied or answered before the shared row arrives");
                Invoke(bridge, "RequestFinished", sent.CommandId, null);
                Pump(Time.realtimeSinceStartup);
                Assert(outgoing.IsEmpty, "a committed, still-pending command keeps the tool call waiting");
                var own = Deliver(Row(sent.CommandId, "handInstrument", "scalpel", 0, "headset"));
                Assert(own.outcome == "applied" && own.acknowledgements == 1 && leftGrip.HeldInstrument == scalpel, "Jarvis's row applies through the shared path");
                var answer = Resolved(swap, "swap_instrument via SpacetimeDB");
                Assert(!answer.is_error && answer.result == "ok: scalpel in left hand", "the tool call answers with the applied command: " + answer.result);
                Assert(hint.Text == "Scalpel · from Scalpal", "Jarvis's hand-over is attributed to Scalpal: " + hint.Text);

                // A rejected Jarvis command answers the tool call with the rejection.
                var twice = Jarvis("swap_instrument", "{\"instrument\":\"scalpel\",\"hand\":\"left\"}");
                Invoke(bridge, "RequestFinished", requests[1].CommandId, null);
                var rejected = Deliver(Row(requests[1].CommandId, "handInstrument", "scalpel", 0, "headset"));
                var rejectedAnswer = Resolved(twice, "rejected swap via SpacetimeDB");
                Assert(rejected.outcome == "rejected" && rejectedAnswer.is_error && rejectedAnswer.result.Contains("already in your left hand"), "the rejection reaches the tool call: " + rejectedAnswer.result);

                // 4. Fallbacks, so the demo-fallback flow still works: refused by the module (role or unknown action) ...
                var refused = Jarvis("highlight_instrument", "{\"instrument\":\"babcock\"}");
                Assert(requests.Count == 3 && requests[2].Action == "highlightInstrument" && requests[2].TargetId == "babcock" && requests[2].ArgNumber == null, "highlight_instrument requests highlightInstrument babcock");
                Invoke(bridge, "RequestFinished", requests[2].CommandId, "role headset may not request commands");
                Pump(Time.realtimeSinceStartup);
                var refusedAnswer = Resolved(refused, "refused request");
                Assert(!refusedAnswer.is_error && refusedAnswer.result == "ok: babcock highlighted" && native.InstrumentCallout.Target == babcock, "a refused request is applied locally: " + refusedAnswer.result);
                // ... no reducer outcome within 4 s: applied locally, and a late row is rejected instead of applied twice ...
                var silent = Jarvis("swap_instrument", "{\"instrument\":\"retractor\",\"hand\":\"right\"}");
                Pump(Time.realtimeSinceStartup + NativeCaseSession.SharedReducerSeconds + .1f);
                var silentAnswer = Resolved(silent, "silent request");
                Assert(!silentAnswer.is_error && rightGrip.HeldInstrument == retractor, "a silent request falls back to the local hand-over: " + silentAnswer.result);
                var late = Deliver(Row(requests[3].CommandId, "handInstrument", "retractor", 1, "headset"));
                Assert(late.outcome == "rejected" && late.reason.Contains("local fallback"), "a late row after the fallback is rejected, not applied twice");
                // ... a committed command never delivered: the tool call still resolves (with an error) before the voice's 10 s.
                var stuck = Jarvis("highlight_instrument", "{\"instrument\":\"hemostat\"}");
                Invoke(bridge, "RequestFinished", requests[4].CommandId, null);
                Pump(Time.realtimeSinceStartup + NativeCaseSession.SharedToolSeconds + .1f);
                Assert(Resolved(stuck, "undelivered command").is_error, "an undelivered command answers the tool call with an error");
                // ... and unpaired: no request at all, the local hand-over exactly as the fallback build.
                SetProperty(bridge, "Paired", false);
                var unpaired = Jarvis("swap_instrument", "{\"instrument\":\"babcock\",\"hand\":\"right\"}");
                var unpairedAnswer = Resolved(unpaired, "unpaired");
                Assert(requests.Count == 5 && !unpairedAnswer.is_error && unpairedAnswer.result == "ok: babcock in right hand" && rightGrip.HeldInstrument == babcock,
                    "unpaired, Jarvis applies locally without a shared request: " + unpairedAnswer.result);

                Debug.Log("SCALPAL_SHARED_INSTRUMENT_COMMAND_VALIDATION_OK handInstrument/highlightInstrument rows applied or rejected once with voice reasons and hand attribution; Jarvis tools via requestCommand resolve from the command; refused/silent/undelivered/unpaired fall back; stub bridge, no headset");
            }
            finally { UnityEngine.Object.DestroyImmediate(fixture); }
        }
        static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Private).Invoke(target, args);
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetSetMethod(true).Invoke(target, new[] { value });
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        static void Assert(bool passed, string reason) { if (!passed) throw new InvalidOperationException("Shared instrument command validation: " + reason); }
    }
}
