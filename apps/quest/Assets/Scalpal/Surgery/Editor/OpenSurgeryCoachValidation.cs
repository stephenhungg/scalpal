using System;
using System.Collections.Generic;
using System.Reflection;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Surgery.Editor
{
    public static class OpenSurgeryCoachValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [MenuItem("Scalpal/Surgery/Validate Coach Delivery")]
        public static void Run()
        {
            var fixture = new GameObject("OpenSurgeryCoachValidation");
            fixture.SetActive(false);
            try
            {
                var relay = fixture.AddComponent<CoachRelay>();
                relay.UseSession("coach-test", "patient-test", "open_appendectomy", "mark", "case-test", "virtual");
                SetProperty(relay, "IsSynchronized", true);
                typeof(CoachRelay).GetField("sending", Private).SetValue(relay, true);
                var evidence = new BodyAction { actionId = "mark_line", verb = "mark", tissueId = "skin", layer = "skin",
                    instrumentId = "skin_marker", instrumentInstanceId = "marker-1", choice = "", registered = true,
                    coordinateFrame = "registered_torso_m", position = new Vec3(), timeMs = 1000,
                    distanceMm = 2, lengthMm = 45, angleDegrees = 3, depthMm = 0, durationMs = 1000, separationMm = 0 };
                relay.Forward(CaseEvent.Surgery(evidence), "mark");
                var pending = (Queue<CoachEventDto>)typeof(CoachRelay).GetField("pending", Private).GetValue(relay);
                Assert(pending.Count == 1, "one surgical event follows one scored path");
                var dto = pending.Peek(); string id = dto.eventId;
                evidence.lengthMm = 99;
                Assert(dto.type == "surgery" && dto.evidence.lengthMm == 45 && dto.stepId == "mark" && !string.IsNullOrEmpty(id), "queued evidence is an immutable sample with retry identity");
                var roundtrip = JsonUtility.FromJson<CoachEventBatch>(JsonUtility.ToJson(new CoachEventBatch { events = pending.ToArray() }));
                Assert(roundtrip.events[0].evidence.instrumentInstanceId == "marker-1" && roundtrip.events[0].evidence.durationMs == 1000, "nested evidence survives wire serialization");
                Assert(relay.TryReflexUrl("/jarvis/reflex/coach-test/mistake.bleeding", out _), "own session reflex route accepted");
                Assert(!relay.TryReflexUrl("https://elsewhere.test/audio", out _) && !relay.TryReflexUrl("/jarvis/reflex/other/x", out _)
                    && !relay.TryReflexUrl("/jarvis/reflex/coach-test/../x", out _) && !relay.TryReflexUrl("/jarvis/reflex/coach-test/a?token=x", out _), "foreign or escaping reflex routes rejected");
                SetProperty(relay, "IsSynchronized", false);
                relay.Forward(CaseEvent.Surgery(evidence), "mark");
                Assert(pending.Count == 1, "uncertain delivery gates subsequent scoring evidence");
                VerifyTrackerFacts(fixture, relay, pending);
                relay.UseSession("");
                Assert(pending.Count == 0 && relay.AlertCursor == 0 && relay.AlertSnapshot == null, "reset drops evidence and alert cursor");
                var hint = new CoachAlertDto { kind = "stuck", stepId = "mark", say = "Follow the line.", tier = "caution" };
                Assert(OpenSurgeryCoach.Relevant(hint, "mark", true, false), "current hint is delivered");
                Assert(!OpenSurgeryCoach.Relevant(hint, "incise", true, false) && !OpenSurgeryCoach.Relevant(hint, "mark", false, false)
                    && !OpenSurgeryCoach.Relevant(hint, "mark", true, true), "stale, paused and completed hints are discarded");
                hint.kind = "mistake";
                Assert(OpenSurgeryCoach.Relevant(hint, "incise", true, false), "guardrail warning survives a milestone change");
                var feed = new CoachAlertFeed { snapshot = new CoachSnapshotState { sessionId = "coach-test", version = 2 }, latestSeq = 2,
                    alerts = new[] { new CoachAlertDto { id = "a1", seq = 1, version = 1, say = "A" }, new CoachAlertDto { id = "a2", seq = 2, version = 2, say = "B" } } };
                Assert(CoachRelay.ValidAlertFeed(feed, "coach-test", 0), "ordered same-attempt feed accepted");
                Assert(!CoachRelay.ValidAlertFeed(feed, "other", 0), "old attempt alert feed rejected");
                feed.alerts[1].seq = 1;
                Assert(!CoachRelay.ValidAlertFeed(feed, "coach-test", 0), "out-of-order feed rejected without advancing cursor");
                feed.alerts[1].seq = 2; feed.alerts[1].version = 3;
                Assert(!CoachRelay.ValidAlertFeed(feed, "coach-test", 0), "future alert version rejected");
                hint.kind = "tracking_lost";
                Assert(OpenSurgeryCoach.Relevant(hint, "mark", false, false) && !OpenSurgeryCoach.Relevant(hint, "mark", true, false), "tracking warning only survives until recovery");
                var bleed = new CoachAlertDto { kind = "bleeding", tier = "warning", say = "Bleeding.", highlight = new[] { "mesoappendix" } };
                var bleeding = new HashSet<string> { "mesoappendix" };
                Assert(OpenSurgeryCoach.Relevant(bleed, "mark", true, false, bleeding.Contains), "bleeding alert plays while the body still bleeds there");
                bleeding.Clear();
                Assert(!OpenSurgeryCoach.Relevant(bleed, "mark", true, false, bleeding.Contains), "queued bleeding alert is dropped once that bleed is controlled");
                Assert(OpenSurgeryCoach.InterruptsAgent(bleed) && OpenSurgeryCoach.InterruptsAgent(new CoachAlertDto { kind = "mistake", tier = "warning" }),
                    "safety warnings may interrupt Jarvis");
                Assert(!OpenSurgeryCoach.InterruptsAgent(new CoachAlertDto { kind = "stuck", tier = "warning" }) &&
                    !OpenSurgeryCoach.InterruptsAgent(new CoachAlertDto { kind = "step_complete", tier = "caution" }), "non-safety alerts never interrupt Jarvis");
                // Jarvis must not talk constantly: a burst of the same caution plays once, and cautions are spaced out.
                var paced = new System.Collections.Generic.Queue<CoachAlertDto>();
                for (int i = 0; i < 6; i++) OpenSurgeryCoach.Enqueue(paced, new CoachAlertDto { kind = "stuck", say = "hint " + i, tier = "caution" });
                OpenSurgeryCoach.Enqueue(paced, new CoachAlertDto { kind = "wrong_tool", say = "tool", tier = "caution" });
                Assert(paced.Count == 2 && paced.Peek().say == "hint 5", "repeated cautions collapse to the newest of each kind");
                Assert(OpenSurgeryCoach.CautionGapSeconds >= 10, "cautions leave a quiet gap of at least 10 s between them");
                Debug.Log("SCALPAL_OPEN_SURGERY_COACH_VALIDATION_OK synthetic relay serialization, gating, stale captions, reset and reflex URL checks; no audio hardware test");
            }
            finally { UnityEngine.Object.DestroyImmediate(fixture); }
        }
        // Jarvis's state tracker: what each hand holds (on change only) and the wire shape of every tracker event.
        // These never score, so they are not gated on scoring synchronization.
        static void VerifyTrackerFacts(GameObject fixture, CoachRelay relay, Queue<CoachEventDto> pending)
        {
            pending.Clear();
            InstrumentBehaviour Tool(string id) { var go = new GameObject(id); go.transform.SetParent(fixture.transform, false); var tool = go.AddComponent<InstrumentBehaviour>(); tool.instrumentId = id; return tool; }
            XRInstrumentInput Hand(XRNode node) { var go = new GameObject(node.ToString()); go.transform.SetParent(fixture.transform, false); var input = go.AddComponent<XRInstrumentInput>(); input.controller = node; return input; }
            var scalpel = Tool("scalpel"); var hemostat = Tool("hemostat");
            var left = Hand(XRNode.LeftHand); var right = Hand(XRNode.RightHand);
            var native = fixture.AddComponent<NativeCaseSession>(); native.coach = relay;
            native.workbench = fixture.AddComponent<NativeWorkbench>(); native.workbench.inputs = new[] { left, right };
            var room = fixture.AddComponent<OpenSurgerySession>(); typeof(OpenSurgerySession).GetField("session", Private).SetValue(room, native);
            void Hold(XRInstrumentInput input, InstrumentBehaviour tool) => SetProperty(input.GetComponent<InstrumentInteractor>(), "HeldInstrument", tool);
            void Report() => typeof(OpenSurgerySession).GetMethod("ReportHands", Private).Invoke(room, null);
            string Sent() { var text = string.Join(",", Array.ConvertAll(pending.ToArray(), e => e.instrumentId + "/" + e.hand + "/" + e.held)); pending.Clear(); return text; }
            Hold(right, scalpel); Report(); Report();
            Assert(Sent() == "scalpel/right/True", "picking up the scalpel is one held event, not one per frame");
            Hold(right, null); Hold(left, scalpel); Report();
            Assert(Sent() == "scalpel/right/False,scalpel/left/True", "passing the scalpel to the other hand drops it from the right and picks it up in the left");
            Hold(left, hemostat); Report(); Report();
            Assert(Sent() == "scalpel/left/False,hemostat/left/True", "swapping tools in a hand puts the old one down first");
            relay.Instrument("scalpel", "right", true);
            relay.Contact("scalpel", "skin");
            relay.Injury("neck", "scalpel"); relay.Injury("neck", "hemostat", true);
            relay.Injury("abdomen", "scalpel"); relay.Instrument("scalpel", "both", true); relay.Contact("scalpel", "");
            Assert(pending.Count == 4 && Array.TrueForAll(pending.ToArray(), e => e.eventId.StartsWith("unity-")), "only coach regions, hands and structures are queued, each with retry identity");
            var wire = JsonUtility.ToJson(new CoachEventBatch { events = pending.ToArray() });
            var events = JsonUtility.FromJson<CoachEventBatch>(wire).events;
            Assert(events[0].type == "instrument" && events[0].hand == "right" && events[0].held && wire.Contains("\"hand\":\"right\",\"region\":\"\",\"held\":true"),
                "instrument event carries hand and held: " + wire);
            Assert(events[1].type == "contact" && events[1].instrumentId == "scalpel" && events[1].structureId == "skin", "contact names the tool and the touched structure");
            Assert(events[2].type == "injury" && events[2].region == "neck" && events[2].instrumentId == "scalpel" && !events[2].controlled
                && events[3].region == "neck" && events[3].instrumentId == "hemostat" && events[3].controlled, "injury then control of the same region");
            pending.Clear();
        }
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value });
        static void Assert(bool passed, string reason) { if (!passed) throw new InvalidOperationException("Open surgery coach validation: " + reason); }
    }
}
