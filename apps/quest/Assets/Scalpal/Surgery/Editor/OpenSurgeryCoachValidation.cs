using System;
using System.Collections.Generic;
using System.Reflection;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEditor;
using UnityEngine;

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
                Debug.Log("SCALPAL_OPEN_SURGERY_COACH_VALIDATION_OK synthetic relay serialization, gating, stale captions, reset and reflex URL checks; no audio hardware test");
            }
            finally { UnityEngine.Object.DestroyImmediate(fixture); }
        }
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value });
        static void Assert(bool passed, string reason) { if (!passed) throw new InvalidOperationException("Open surgery coach validation: " + reason); }
    }
}
