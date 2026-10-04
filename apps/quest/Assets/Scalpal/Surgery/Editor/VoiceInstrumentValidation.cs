using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Scalpal.Exercises.Coach;
using Scalpal.Instruments;
using Scalpal.Quest;
using Scalpal.Voice;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Surgery.Editor
{
    // "Scalpal, give me the hemostat": the coach's swap_instrument and highlight_instrument client tools, run through the
    // production voice dispatch into NativeCaseSession, with real interactors, rest poses and coach hand events.
    public static class VoiceInstrumentValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [Serializable] sealed class ToolResult { public string type, tool_call_id, result; public bool is_error; }
        static int calls;

        [MenuItem("Scalpal/Surgery/Validate Voice Instrument Tools")]
        public static void Run()
        {
            var fixture = new GameObject("VoiceInstrumentValidation");
            try
            {
                var stand = new GameObject("Stand").transform; stand.SetParent(fixture.transform, false); stand.position = new Vector3(.6f, .9f, .2f);
                InstrumentBehaviour Tool(string id, float x, bool active = true)
                {
                    var go = new GameObject(id); go.transform.SetParent(stand, false); go.transform.localPosition = new Vector3(x, 0, 0);
                    var mesh = GameObject.CreatePrimitive(PrimitiveType.Cube); mesh.transform.SetParent(go.transform, false); mesh.transform.localScale = new Vector3(.02f, .01f, .15f);
                    var tool = go.AddComponent<InstrumentBehaviour>(); tool.instrumentId = id; tool.CaptureRestPose(); go.SetActive(active);
                    return tool;
                }
                XRInstrumentInput Hand(XRNode node) { var go = new GameObject(node.ToString()); go.transform.SetParent(fixture.transform, false); var input = go.AddComponent<XRInstrumentInput>(); input.controller = node; return input; }
                var scalpel = Tool("scalpel", 0); var hemostatA = Tool("hemostat", .06f); var hemostatB = Tool("hemostat", .12f);
                var retractor = Tool("retractor", .18f); var babcock = Tool("babcock", .24f); var suction = Tool("suction_irrigator", .3f);
                var stapler = Tool("endo_stapler", .36f, false); // off the open set: inactive like ApplyOpenToolSet leaves it
                var left = Hand(XRNode.LeftHand); var right = Hand(XRNode.RightHand);
                var leftGrip = left.GetComponent<InstrumentInteractor>(); var rightGrip = right.GetComponent<InstrumentInteractor>();

                var relay = fixture.AddComponent<CoachRelay>();
                relay.UseSession("coach-test", "patient-test", "open_appendectomy", "mark", "case-test", "virtual");
                typeof(CoachRelay).GetField("sending", Private).SetValue(relay, true); // queue only, no HTTP flush
                var pending = (Queue<CoachEventDto>)typeof(CoachRelay).GetField("pending", Private).GetValue(relay);
                var voice = fixture.AddComponent<QuestJarvisVoice>();
                var native = fixture.AddComponent<NativeCaseSession>(); native.coach = relay; native.voice = voice;
                native.workbench = fixture.AddComponent<NativeWorkbench>(); native.workbench.inputs = new[] { left, right };
                native.workbench.tools = new[] { scalpel, hemostatA, hemostatB, retractor, babcock, suction, stapler };
                var hint = fixture.AddComponent<SurgeryTriggerHint>();
                var room = fixture.AddComponent<OpenSurgerySession>(); typeof(OpenSurgerySession).GetField("session", Private).SetValue(room, native);
                void Report() => typeof(OpenSurgerySession).GetMethod("ReportHands", Private).Invoke(room, null);
                string Sent() { var text = string.Join(",", Array.ConvertAll(Array.FindAll(pending.ToArray(), e => e.type == "instrument"), e => e.instrumentId + "/" + e.hand + "/" + e.held)); pending.Clear(); return text; }
                SetProperty(native, "Phase", "Practicing");

                // The production voice dispatch: unknown names go to ClientToolRequested, which NativeCaseSession answers.
                Set(voice, "generation", 7); SetProperty(voice, "Status", "connected");
                var connectionType = typeof(QuestJarvisVoice).GetNestedType("Connection", BindingFlags.NonPublic);
                object connection = Activator.CreateInstance(connectionType, new object[] { 7 });
                connectionType.GetField("Open").SetValue(connection, true); Set(voice, "active", connection);
                var outgoing = (ConcurrentQueue<string>)connectionType.GetField("Outgoing").GetValue(connection);
                var handler = (Action<QuestJarvisVoice.ToolRequest>)Delegate.CreateDelegate(typeof(Action<QuestJarvisVoice.ToolRequest>), native,
                    typeof(NativeCaseSession).GetMethod("VoiceTool", Private));
                voice.ClientToolRequested += handler;
                ToolResult Call(string name, string json)
                {
                    string id = "voice-instrument-" + (++calls);
                    Get<HashSet<string>>(voice, "pendingTools").Add(id);
                    var type = typeof(QuestJarvisVoice).GetNestedType("ToolCall", BindingFlags.NonPublic);
                    object tool = Activator.CreateInstance(type, true);
                    type.GetField("tool_name").SetValue(tool, name); type.GetField("tool_call_id").SetValue(tool, id); type.GetField("expects_response").SetValue(tool, true);
                    var routine = (IEnumerator)typeof(QuestJarvisVoice).GetMethod("ExecuteTool", Private).Invoke(voice, new[] { tool, 7, json });
                    // Unity-style nested coroutine pump; a handler that resolves synchronously never yields a frame.
                    var stack = new Stack<IEnumerator>(); stack.Push(routine);
                    for (int steps = 0; stack.Count > 0; steps++)
                    {
                        Assert(steps < 8, name + " resolves synchronously instead of waiting for the 10 s timeout");
                        if (!stack.Peek().MoveNext()) { stack.Pop(); continue; }
                        if (stack.Peek().Current is IEnumerator nested) stack.Push(nested);
                    }
                    var results = new List<ToolResult>();
                    while (outgoing.TryDequeue(out string message)) results.Add(JsonUtility.FromJson<ToolResult>(message));
                    Assert(results.Count == 1 && results[0].tool_call_id == id && results[0].type == "client_tool_result", name + " resolves exactly once (" + results.Count + ")");
                    voice.ResolveClientTool(new QuestJarvisVoice.ToolRequest { ToolCallId = id, ConnectionGeneration = 7 }, "late duplicate");
                    Assert(outgoing.IsEmpty, name + " cannot be resolved a second time");
                    return results[0];
                }

                // Scalpel in the right hand, grip squeezed, hand moved off the stand.
                double clock = 0;
                leftGrip.SetTrackedPose(new Vector3(-.2f, 1, 0), Quaternion.identity, true, clock);
                rightGrip.SetTrackedPose(scalpel.transform.position, Quaternion.identity, true, clock);
                Assert(rightGrip.TryPickup(scalpel), "fixture: right hand picks up the scalpel");
                rightGrip.SetGrip(1);
                rightGrip.SetTrackedPose(new Vector3(.2f, 1.1f, -.1f), Quaternion.Euler(0, 30, 0), true, clock);
                Report();
                Assert(Sent() == "scalpel/right/True", "fixture: coach knows the scalpel is in the right hand");
                Vector3 scalpelRest = stand.TransformPoint(Vector3.zero);

                var swap = Call("swap_instrument", "{\"instrument\":\"hemostat\",\"hand\":\"either\"}");
                Assert(!swap.is_error && swap.result == "ok: hemostat in right hand", "swap succeeds with a short result: " + swap.result);
                Assert(rightGrip.HeldInstrument && rightGrip.HeldInstrument.instrumentId == "hemostat" && rightGrip.HeldInstrument.Held
                    && rightGrip.HeldInstrument.transform.parent == right.transform, "the hemostat is held in the right hand like a grip pickup");
                Assert(!scalpel.Held && scalpel.transform.parent == stand && (scalpel.transform.position - scalpelRest).sqrMagnitude < 1e-8f
                    && Quaternion.Angle(scalpel.transform.rotation, stand.rotation) < .01f, "the scalpel is back at its stand rest pose, not dropped");
                Report();
                Assert(Sent() == "scalpel/right/False,hemostat/right/True", "coach hears the scalpel put down and the hemostat picked up");
                Assert(hint.Text == "Hemostat · from Scalpal", "the hand-over shows a short attributed cue at the hand: " + hint.Text);
                var handed = rightGrip.HeldInstrument;
                for (int frame = 0; frame < 3; frame++) { rightGrip.SetGrip(1); rightGrip.SetTrackedPose(new Vector3(.2f, 1.1f, -.1f + frame * .01f), Quaternion.identity, true, clock); }
                Assert(rightGrip.HeldInstrument == handed, "the handed tool stays held while the learner keeps squeezing grip");

                var again = Call("swap_instrument", "{\"instrument\":\"hemostat\",\"hand\":\"right\"}");
                Assert(again.is_error && again.result.Contains("already in your right hand"), "asking for the tool already in that hand fails: " + again.result);
                var second = Call("swap_instrument", "{\"instrument\":\"hemostat\",\"hand\":\"left\"}");
                Assert(!second.is_error && leftGrip.HeldInstrument && leftGrip.HeldInstrument != handed && leftGrip.HeldInstrument.instrumentId == "hemostat",
                    "the second hemostat (the one not held) goes to the left hand");
                var unknown = Call("swap_instrument", "{\"instrument\":\"bone_saw\"}");
                Assert(unknown.is_error && unknown.result.Contains("not in this case"), "an unknown instrument fails with a reason: " + unknown.result);
                var offSet = Call("swap_instrument", "{\"instrument\":\"endo_stapler\"}");
                Assert(offSet.is_error && offSet.result.Contains("not in this case"), "an instrument off the open stand fails with a reason");

                rightGrip.SetGrip(0);
                Assert(!rightGrip.HeldInstrument && !handed.Held, "releasing grip releases the handed tool");
                Report(); pending.Clear();

                SetProperty(native, "Phase", "Confirmed");
                var timeOut = Call("swap_instrument", "{\"instrument\":\"scalpel\"}");
                Assert(timeOut.is_error && timeOut.result.Contains("not practicing yet") && !rightGrip.HeldInstrument, "before practice starts the swap fails with a reason: " + timeOut.result);
                SetProperty(native, "Phase", "Practicing");

                leftGrip.SetTrackedPose(Vector3.zero, Quaternion.identity, false, clock);
                rightGrip.SetTrackedPose(Vector3.zero, Quaternion.identity, false, clock);
                var untracked = Call("swap_instrument", "{\"instrument\":\"scalpel\"}");
                Assert(untracked.is_error && untracked.result.Contains("not tracked") && !rightGrip.HeldInstrument, "untracked hands fail with a reason: " + untracked.result);
                rightGrip.SetTrackedPose(new Vector3(.2f, 1.1f, -.1f), Quaternion.identity, true, clock);

                // highlight_instrument: one orange callout, replaced by a new call, off on pickup or after about 8 s.
                var lit = Call("highlight_instrument", "{\"instrument\":\"retractor\"}");
                var callout = native.InstrumentCallout;
                Assert(!lit.is_error && lit.result == "ok: retractor highlighted" && callout && callout.Visible && callout.Target == retractor, "the retractor is highlighted: " + lit.result);
                var line = callout.GetComponentInChildren<LineRenderer>();
                Assert(line.startColor.r > .6f && line.startColor.g < .5f * line.startColor.r + .1f && line.startColor.b < .2f, "the callout is orange, not the green identification box");
                var moved = Call("highlight_instrument", "{\"instrument\":\"babcock\"}");
                Assert(!moved.is_error && callout.Target == babcock && fixture.GetComponentsInChildren<NativeInstrumentCallout>().Length == 1
                    && fixture.GetComponentsInChildren<LineRenderer>().Length == 1, "a new call moves the single highlight to the babcock");
                double now = Time.unscaledTimeAsDouble;
                callout.Advance(now + 1);
                Assert(callout.Visible, "the highlight holds while the babcock is on the stand");
                Assert(rightGrip.TryHandOver(babcock), "fixture: the learner takes the babcock");
                callout.Advance(now + 1.1);
                Assert(!callout.Visible, "picking up the highlighted instrument clears the highlight");
                Call("highlight_instrument", "{\"instrument\":\"suction_irrigator\"}");
                now = Time.unscaledTimeAsDouble;
                callout.Advance(now + 7.5);
                Assert(callout.Visible && callout.Target == suction, "the highlight lasts about 8 s");
                callout.Advance(now + 8.1);
                Assert(!callout.Visible, "the highlight times out after about 8 s");
                var missing = Call("highlight_instrument", "{\"instrument\":\"bone_saw\"}");
                Assert(missing.is_error && missing.result.Contains("not in this case"), "an unknown instrument cannot be highlighted");

                voice.ClientToolRequested -= handler;
                Debug.Log("SCALPAL_VOICE_INSTRUMENT_VALIDATION_OK swap_instrument hand-over to rest pose with coach events, failures with reasons, single resolution; highlight_instrument replace/pickup/timeout; no headset");
            }
            finally { UnityEngine.Object.DestroyImmediate(fixture); }
        }
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetSetMethod(true).Invoke(target, new[] { value });
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        static void Assert(bool passed, string reason) { if (!passed) throw new InvalidOperationException("Voice instrument validation: " + reason); }
    }
}
