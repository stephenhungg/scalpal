using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Real input adapter and score binding with synthetic tracked poses. This is neither
    // headset evidence nor a claim that the authored wound is mechanically validated.
    public static class OpenBodyInteractionValidation
    {
        static int checks;
        static void Require(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Open body interaction validation: " + message);
        }
        [MenuItem("Scalpal/Surgery/Validate Measured Interaction")]
        public static void Run()
        {
            checks = 0;
            var source = Resources.Load<TextAsset>("scalpal_bundle");
            Require(source, "offline bundle exists");
            var bundle = JsonUtility.FromJson<ScalpalBundle>(source.text);
            var procedure = Array.Find(bundle.procedures, p => p.id == "open_appendectomy");
            Require(procedure?.openBody != null, "open-body procedure exists");
            VerifyStroke(bundle, procedure);
            VerifyTransformJump(bundle, procedure);
            VerifyTentRelease(bundle, procedure);
            VerifyDeformedReferences();
            VerifyOffPath(bundle, procedure);
            VerifyRetractionAndChoice(bundle, procedure);
            VerifyRoughHandling(bundle, procedure);
            VerifyFluid(procedure);
            int fixtureChecks = checks;
            // Actual native-scene atlas: mobilize, deliver, then measure the base on the moved anatomy.
            checks += OpenBodyDeliveryValidation.Run();
            Debug.Log("SCALPAL_OPEN_BODY_INTERACTION_VALIDATION_OK: " + checks + " checks (" + fixtureChecks + " synthetic actual-adapter and real-vessel snapshot, "
                + (checks - fixtureChecks) + " actual-scene delivery); no headset test");
        }
        sealed class Fixture : IDisposable
        {
            public readonly GameObject root;
            public readonly Transform wound;
            public readonly AnatomyController anatomy;
            public readonly AnatomyExerciseBinding binding;
            public readonly OpenBodyInteraction input;
            public readonly InstrumentBehaviour[] tools;
            public readonly List<BodyRecord> submitted = new List<BodyRecord>();
            public bool gate = true;
            public Fixture(ScalpalBundle bundle, Procedure procedure, params string[] ids)
            {
                root = new GameObject("SyntheticOpenBodyAdapterFixture");
                root.transform.SetPositionAndRotation(new Vector3(3, 2, -1), Quaternion.Euler(0, 21, 0));
                var anatomyRoot = new GameObject("Anatomy"); anatomyRoot.transform.SetParent(root.transform, false);
                anatomy = anatomyRoot.AddComponent<AnatomyController>();
                var woundRoot = new GameObject("AuthoredWoundFrame"); woundRoot.transform.SetParent(root.transform, false);
                woundRoot.transform.localPosition = new Vector3(.1f, .2f, .3f); wound = woundRoot.transform;
                // One real collider target permits off-wall bowel contact through SurgeryTissueTarget.
                var bowel = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                bowel.name = "SyntheticBowel"; bowel.transform.SetParent(anatomyRoot.transform, false);
                bowel.transform.position = wound.TransformPoint(new Vector3(.1f, 0, .05f));
                bowel.transform.localScale = Vector3.one * .02f;
                bowel.AddComponent<AnatomyPart>().stableId = "terminal_ileum";
                var target = bowel.AddComponent<SurgeryTissueTarget>(); target.tissueId = "terminal_ileum";
                var basePoint = new GameObject("Base").transform; basePoint.SetParent(root.transform, false);
                basePoint.position = wound.TransformPoint(new Vector3(.09f, 0, .05f));
                var endPoint = new GameObject("End").transform; endPoint.SetParent(root.transform, false);
                endPoint.position = wound.TransformPoint(new Vector3(.11f, 0, .05f));
                target.basePoint = basePoint; target.endPoint = endPoint;
                binding = root.AddComponent<AnatomyExerciseBinding>(); binding.anatomy = anatomy;
                var kase = new SurgicalCase { caseId = "synthetic-open-adapter", patientId = "synthetic-patient", status = "ready",
                    procedureId = procedure.id, procedure = procedure, instruments = bundle.instruments };
                Require(binding.SelectCase(new ScalpalBundle { cases = new[]{ kase } }, kase.caseId, true, out var reason), "real binding selects fixture: " + reason);
                anatomy.SetRegistrationValid(true);
                tools = ids.Select(CreateTool).ToArray();
                input = root.AddComponent<OpenBodyInteraction>();
                input.Initialize(binding, tools, root.transform, wound, () => gate);
                input.Submitted += (record, _) => submitted.Add(record);
                Physics.SyncTransforms();
                Require(input.Ready, "adapter ready only after valid selected registration");
            }
            InstrumentBehaviour CreateTool(string id)
            {
                var go = new GameObject(id); go.transform.SetParent(root.transform, false);
                go.transform.position = wound.TransformPoint(new Vector3(.2f, .2f, -.2f));
                var tool = go.AddComponent<InstrumentBehaviour>(); tool.instrumentId = id;
                var tip = new GameObject("Tip").transform; tip.SetParent(go.transform, false);
                tool.actionPoint = tip; tool.gripAnchor = go.transform; tool.contactRadius = .004f;
                if (id == "scalpel")
                {
                    var first = new GameObject("CutStart").transform; first.SetParent(go.transform, false); first.localPosition = Vector3.left * .002f;
                    var last = new GameObject("CutEnd").transform; last.SetParent(go.transform, false); last.localPosition = Vector3.right * .002f;
                }
                tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1);
                return tool;
            }
            public void Move(int index, Vector3 local)
            {
                tools[index].transform.rotation = wound.rotation;
                tools[index].transform.position += wound.TransformPoint(local) - tools[index].actionPoint.position;
                Physics.SyncTransforms();
            }
            public void Expose(Procedure procedure, int steps)
            {
                foreach (var step in procedure.steps.Take(steps)) foreach (var e in CaseRunner.PerfectEvents(step))
                    Require(binding.Submit(e, out _, out var reason), "precondition goes through real score binding: " + reason);
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); }
        }
        static void VerifyStroke(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                Require(f.input.SetLandmarks(f.wound.TransformPoint(new Vector3(-.06f,0,0)), f.wound.TransformPoint(new Vector3(.12f,0,0)), f.wound.right), "registered landmark reference accepted");
                f.Move(0, new Vector3(-.03f,0,0));
                for (int i=0; i<8; i++) f.input.Simulate(.02f);
                Require(f.binding.Body.Get("skin", "opened") == 0 && !f.submitted.Any(r => r.action.verb == "cut"), "stationary active blade contact does not cut");
                for (int i=1; i<=12; i++) { f.Move(0, new Vector3(-.03f + i*.005f,0,0)); f.input.Simulate(.02f); }
                var cut = f.submitted.Last(r => r.action.verb == "cut");
                Require(f.binding.Body.Get("skin", "opened") == 1 && cut.action.lengthMm > 55, "finite measured stroke opens skin");
                Require(cut.action.distanceMm < .1f && cut.action.angleDegrees < .1f, "on-line stroke reports measured position and angle");
                var expected = f.root.transform.InverseTransformPoint(f.wound.TransformPoint(new Vector3(.03f,0,0)));
                Require(Vector3.Distance(new Vector3(cut.action.position.x,cut.action.position.y,cut.action.position.z), expected) < .0001f &&
                    cut.action.coordinateFrame == "registered_torso_m", "world poses are transformed into registered torso coordinates");
                double clock = f.input.ActiveSeconds; int events = f.binding.Body.Log.Count;
                f.anatomy.SetRegistrationValid(false);
                f.Move(0,new Vector3(.02f,0,0)); f.input.Simulate(.02f);
                Require(f.input.ActiveSeconds == clock && f.binding.Body.Log.Count == events, "invalid registration freezes active clock and scoring");
                f.anatomy.SetRegistrationValid(true);
                f.input.Simulate(float.NaN); f.input.Simulate(.2f); f.input.Simulate(0);
                Require(f.input.ActiveSeconds == clock && f.binding.Body.Log.Count == events, "invalid and stalled deltas create no clock catch-up or effects");
                f.gate = false; f.input.Simulate(.02f);
                Require(f.input.ActiveSeconds == clock && f.input.CreateMeasurement("assistant","tick","skin",f.wound.position) == null, "external practice gate blocks measurements");
                f.gate = true; f.input.Simulate(.02f);
                var future = f.input.CreateMeasurement("assistant","tick","skin",f.wound.position); future.timeMs += 100;
                Require(!f.input.SubmitMeasured(future), "future-dated external sample rejected");
                f.tools[0].SetTrackingValid(false); int cuts = f.submitted.Count(r=>r.action.verb=="cut");
                f.Move(0,new Vector3(-.02f,0,0)); f.input.Simulate(.02f);
                Require(f.submitted.Count(r=>r.action.verb=="cut") == cuts, "untracked tool cannot add a stroke");
            }
        }
        static void VerifyTransformJump(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                f.Move(0, new Vector3(-.025f, 0, 0)); f.input.Simulate(.02f);
                f.Move(0, new Vector3(-.020f, 0, 0)); f.input.Simulate(.02f);
                int cuts = f.submitted.Count(r => r.action.verb == "cut");
                Require(cuts > 0, "transform regression begins with a real scored stroke");
                Vector3 stationaryController = f.tools[0].actionPoint.position;
                f.wound.position += f.wound.right * .012f; Physics.SyncTransforms();
                f.input.Simulate(.02f); f.input.Simulate(.02f);
                Require(f.tools[0].actionPoint.position == stationaryController && f.submitted.Count(r => r.action.verb == "cut") == cuts,
                    "wound fit correction cannot turn a stationary controller into a cut");
                f.tools[0].transform.SetParent(null, true);
                try
                {
                    f.tools[0].transform.position += f.wound.right * .004f; Physics.SyncTransforms(); f.input.Simulate(.02f);
                    cuts = f.submitted.Count(r => r.action.verb == "cut"); stationaryController = f.tools[0].actionPoint.position;
                    Vector3 fixedWound = f.wound.position;
                    f.root.transform.position += f.root.transform.right * .008f;
                    f.wound.position = fixedWound; Physics.SyncTransforms();
                    f.input.Simulate(.02f); f.input.Simulate(.02f);
                    Require(f.tools[0].actionPoint.position == stationaryController && f.submitted.Count(r => r.action.verb == "cut") == cuts,
                        "torso-only correction does not score stationary controllers");
                    f.tools[0].transform.position += f.wound.right * .005f; Physics.SyncTransforms(); f.input.Simulate(.02f);
                    var resumed = f.submitted.Last(r => r.action.verb == "cut");
                    Require(resumed.action.lengthMm > 4 && resumed.action.lengthMm < 6,
                        "new motion after torso correction starts a fresh measured stroke");
                    double clock = f.input.ActiveSeconds; var body = f.binding.Body; int records = body.Log.Count;
                    f.input.ClearPlacements();
                    Require(f.input.ActiveSeconds == clock && f.binding.Body == body && body.Log.Count == records,
                        "equipment placement reset preserves clock, body, and event history");
                }
                finally { f.tools[0].transform.SetParent(f.root.transform, true); }
            }
            using (var f = new Fixture(bundle, procedure, "hemostat"))
            {
                f.Expose(procedure, 5);
                f.Move(0, new Vector3(.10f, 0, .05f)); f.input.Simulate(.02f);
                var latch = f.tools[0].GetComponent<SurgeryInstrumentLatch>();
                Require(latch && latch.Attached, "accepted clamp creates an equipment latch");
                double clock = f.input.ActiveSeconds; int records = f.binding.Body.Log.Count;
                f.input.ClearPlacements();
                Require(!latch.Attached && f.input.ActiveSeconds == clock && f.binding.Body.Log.Count == records,
                    "clear placements removes retained tools without retrying or rewriting scoring");
            }
        }
        static void VerifyTentRelease(ScalpalBundle bundle, Procedure procedure)
        {
            void Lift(Fixture f, int index)
            {
                f.Move(index, new Vector3(.02f, 0, .027f));
                for (int i = 0; i < 6; i++) f.input.Simulate(.02f);
                f.Move(index, new Vector3(.02f, 0, .017f));
                for (int i = 0; i < 6; i++) f.input.Simulate(.02f);
                Require(f.binding.Body.Get("peritoneum", "tented") == 1, "measured forceps lift tents exposed peritoneum");
            }
            using (var f = new Fixture(bundle, procedure, "scalpel", "toothed_forceps"))
            {
                f.Expose(procedure, 4); Lift(f, 1);
                var mistakes = new List<string>(); f.binding.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                f.Move(0, new Vector3(-.01f, 0, .027f)); f.input.Simulate(.02f);
                // Arrival from the parked tool exceeds the jump bound; the next stable pose
                // establishes contact before we test a real stroke in the release frame.
                f.input.Simulate(.02f);
                f.tools[1].SetHeld(false);
                f.Move(0, new Vector3(-.005f, 0, .027f)); f.input.Simulate(.02f);
                var release = f.submitted.FindLastIndex(r => r.action.choice == "release");
                var cut = f.submitted.FindLastIndex(r => r.action.verb == "cut");
                Require(release >= 0 && cut > release && f.submitted[release].action.depthMm == 0,
                    "last forceps release scores zero lift before a blade earlier in tool order");
                Require(f.binding.Body.Get("peritoneum", "tentedBeforeCut") == 0 && mistakes.Contains("lift_first"),
                    "cut after release records untented guardrail instead of stale tent success");
            }
            using (var f = new Fixture(bundle, procedure, "toothed_forceps"))
            {
                f.Expose(procedure, 4); Lift(f, 0);
                int records = f.binding.Body.Log.Count; double clock = f.input.ActiveSeconds;
                f.anatomy.SetRegistrationValid(false); f.input.Simulate(.02f);
                f.tools[0].SetHeld(false); f.input.ClearPlacements();
                Require(f.binding.Body.Log.Count == records && f.input.ActiveSeconds == clock && f.binding.Body.Get("peritoneum", "tented") == 1,
                    "registration loss queues release without mutating frozen body or clock");
                f.anatomy.SetRegistrationValid(true); f.input.Simulate(.02f);
                Require(f.binding.Body.Get("peritoneum", "tented") == 0 && f.submitted.Any(r => r.action.choice == "release"),
                    "first valid sample drains queued release through the scored path");
                f.tools[0].SetHeld(true); f.tools[0].SetActivation(1); Lift(f, 0);
                f.input.ClearPlacements();
                var selected = f.binding.SelectedCase;
                Require(f.binding.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out _), "retry selects fresh attempt");
                f.anatomy.SetRegistrationValid(true); f.tools[0].SetHeld(false);
                int releases = f.submitted.Count(r => r.action.choice == "release");
                f.input.Simulate(.02f);
                Require(f.submitted.Count(r => r.action.choice == "release") == releases && f.binding.Body.Log.Count == 0,
                    "retry discards queued previous-attempt releases");
            }
            using (var f = new Fixture(bundle, procedure, "toothed_forceps", "toothed_forceps"))
            {
                f.Expose(procedure, 4);
                f.Move(0, new Vector3(-.02f, 0, .027f)); Lift(f, 1);
                Require(f.binding.Body.Get("peritoneum", "tented") == 1, "lower second grasp does not erase another forceps' current measured lift");
                f.tools[0].SetHeld(false); f.input.Simulate(.02f);
                Require(f.binding.Body.Get("peritoneum", "tented") == 1 && f.submitted.Last(r => r.action.choice == "release").action.depthMm >= 8,
                    "one release retains the other forceps' measured lift");
                f.tools[1].SetTrackingValid(false); f.input.Simulate(.02f);
                Require(f.binding.Body.Get("peritoneum", "tented") == 0,
                    "last forceps tracking loss releases tent through a scored event");
            }
        }
        static void VerifyDeformedReferences()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SyntheticDeformedLongitudinalReferences";
            try
            {
                UnityEngine.Object.DestroyImmediate(go.GetComponent<BoxCollider>());
                go.AddComponent<MeshCollider>().sharedMesh = go.GetComponent<MeshFilter>().sharedMesh;
                go.transform.SetPositionAndRotation(new Vector3(2, 1, -3), Quaternion.Euler(17, 39, -23));
                go.transform.localScale = Vector3.one * .01f;
                var deformable = go.AddComponent<DeformableTissue>();
                Require(deformable.Initialize(TissuePreset.Bowel, .01f), "scaled raw-source fixture initializes actual deformable mesh");
                var target = go.AddComponent<SurgeryTissueTarget>();
                target.basePoint = new GameObject("RestBase").transform; target.basePoint.SetParent(go.transform, false);
                target.endPoint = new GameObject("RestEnd").transform; target.endPoint.SetParent(go.transform, false);
                Vector3 rawBase = new Vector3(-.5f, -.5f, -.5f), rawEnd = new Vector3(.5f, -.5f, -.5f);
                target.basePoint.localPosition = rawBase; target.endPoint.localPosition = rawEnd; target.Refresh();
                Vector3 restAxis = target.LongitudinalWorld;
                Require(deformable.ApplyContact(deformable.ToMeters(rawBase), new Vector3(.002f, .001f, 0)),
                    "actual cage contact displaces the base without moving rest references");
                deformable.CommitSurface();
                void CheckReferences()
                {
                    Vector3 start = go.transform.TransformPoint(deformable.DeformSurfacePoint(rawBase));
                    Vector3 end = go.transform.TransformPoint(deformable.DeformSurfacePoint(rawEnd));
                    Vector3 axis = (end - start).normalized;
                    Require(target.HasBase && Vector3.Angle(target.LongitudinalWorld, axis) < .1f,
                        "longitudinal axis follows deformed mesh endpoints");
                    Require(target.DistanceFromBase(start + axis * .003f, out var distance) && Mathf.Abs(distance - 3) < .02f,
                        "longitudinal distance uses deformed base in world meters");
                    Require(go.GetComponent<MeshFilter>().sharedMesh.vertices.Any(v => Vector3.Distance(go.transform.TransformPoint(v), start) < .00001f),
                        "deformed base reference agrees with committed rendered surface");
                }
                CheckReferences();
                Require(Vector3.Angle(restAxis, target.LongitudinalWorld) > 1,
                    "synthetic deformation changes the longitudinal axis rather than only the object transform");
                go.transform.SetPositionAndRotation(new Vector3(-1, 2, 3), Quaternion.Euler(-31, 8, 41));
                CheckReferences();
                Require(target.basePoint.localPosition == rawBase && target.endPoint.localPosition == rawEnd,
                    "measurement preserves authored rest coordinates after deformation and rigid movement");
                deformable.ResetTissue();
                Require(target.DistanceFromBase(go.transform.TransformPoint(rawBase) + go.transform.right * .003f, out var resetDistance)
                    && Mathf.Abs(resetDistance - 3) < .02f, "tissue reset restores rest-reference distances without rebinding");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
        static void VerifyOffPath(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                for (int i=0; i<=12; i++) { f.Move(0,new Vector3(-.03f+i*.005f,.02f,0)); f.input.Simulate(.02f); }
                Require(f.binding.Body.Get("skin","opened") == 1 && f.binding.Body.Get("skin","cutErrorMm") > 19,
                    "off-line cut remains a real consequence with measured error");
            }
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                f.Expose(procedure,5);
                for (int i=0; i<=6; i++) { f.Move(0,new Vector3(.094f+i*.002f,0,.05f)); f.input.Simulate(.02f); }
                Require(f.binding.Body.Get("","contamination") == 1, "actual off-wall collider stroke injures bowel");
                Require(f.submitted.Any(r=>r.action.tissueId=="terminal_ileum" && r.outcomes.Contains("hollow_leak")), "adapter emits structured off-path bowel consequence");
            }
        }
        static void VerifyRetractionAndChoice(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "retractor", "retractor"))
            {
                f.Expose(procedure,3);
                f.Move(0,new Vector3(0,-.003f,.019f)); f.Move(1,new Vector3(0,.003f,.019f));
                for (int i=0; i<6; i++) f.input.Simulate(.02f);
                Require(f.binding.Body.Get("muscle","opened") == 0, "two stationary retractors do not invent a split");
                for (int i=1; i<=12; i++)
                {
                    f.Move(0,new Vector3(0,-.003f-i*.001f,.019f));
                    f.Move(1,new Vector3(0,.003f+i*.001f,.019f)); f.input.Simulate(.02f);
                }
                Require(f.binding.Body.Get("muscle","splitWidthMm") >= 15 && f.binding.Body.Get("muscle","opened") == 1,
                    "two held retractors produce measured muscle separation");
                Require(f.submitted.Any(r=>r.action.tissueId=="muscle" && r.action.separationMm>=15 &&
                    !string.IsNullOrEmpty(r.action.secondaryInstanceId) && r.action.secondaryInstanceId!=r.action.instrumentInstanceId), "split evidence contains distinct measured tool instances");
                Require(!f.input.Choose("appendix","invented_answer"), "unknown decision choice rejected");
                Require(f.input.Choose("appendix","true_base") && f.binding.Body.Get("appendix","decision_true_base") == 1,
                    "authored choice follows the existing scored event path");
                f.anatomy.SetRegistrationValid(false);
                Require(!f.input.Choose("appendix","appendix_tip"), "invalid registration blocks decision submission");
            }
        }
        static void VerifyRoughHandling(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "toothed_forceps"))
            {
                f.Expose(procedure, 4);
                var mistakes = new List<string>(); f.binding.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                f.Move(0, new Vector3(.02f, 0, .027f)); for (int i = 0; i < 3; i++) f.input.Simulate(.02f);
                // Slow 10 mm/s lift with +/-1.5 mm frame-to-frame jitter: about 0.15 m/s instantaneous.
                for (int i = 1; i <= 50; i++) { f.Move(0, new Vector3(.02f + (i % 2 == 0 ? .0015f : -.0015f), 0, .027f - i * .0002f)); f.input.Simulate(.02f); }
                int lifts = f.submitted.Count(r => r.action.verb == "grasp");
                Require(lifts >= 8 && !f.submitted.Any(r => r.outcomes.Contains("rough_handling")), "slow but noisy lift is not rough handling");
                // Sustained 150 mm/s lift reported every 100 ms is one rough-handling mistake, not one per report.
                for (int i = 1; i <= 30; i++) { f.Move(0, new Vector3(.02f, 0, .017f - i * .003f)); f.input.Simulate(.02f); }
                Require(f.submitted.Count(r => r.action.verb == "grasp") >= lifts + 5 &&
                    f.submitted.Count(r => r.outcomes.Contains("rough_handling")) == 1 &&
                    mistakes.Count(id => id == "rough_handling") == 1, "one fast lift yields exactly one rough-handling mistake");
            }
        }
        static void VerifyFluid(Procedure procedure)
        {
            var runner = new CaseRunner(procedure);
            foreach (var step in procedure.steps.Take(5)) foreach (var e in CaseRunner.PerfectEvents(step)) runner.Handle(e);
            var vessel = new VesselBleeding(); int sequence = 0;
            BodyAction Event(string verb, string instrument, double time) => new BodyAction { actionId="fluid-test-"+(++sequence),
                verb=verb, instrumentId=instrument, instrumentInstanceId="fluid-source", tissueId="mesoappendix", layer="mesoappendix", registered=true,
                timeMs=time, lengthMm=4 };
            void Snapshot(double time)
            {
                var action=Event("fluid","assistant",time);
                action.bloodLostMl=(float)vessel.CumulativeLossMilliliters; action.poolMl=(float)vessel.PooledMilliliters;
                action.flowMlPerSecond=(float)vessel.FlowMillilitersPerSecond;
                runner.Handle(CaseEvent.Surgery(action));
            }
            Snapshot(0); // Bind the measured source before injury so fallback flow never runs.
            runner.Handle(CaseEvent.Surgery(Event("cut","scalpel",0)));
            Require(vessel.OpenInjury(.000001), "real vessel injury created");
            for(int i=1;i<=5;i++) { Require(vessel.Step(.02),"bounded real vessel step"); Snapshot(i*20); }
            double lost=vessel.CumulativeLossMilliliters;
            Require(lost>0 && Math.Abs(runner.Body.Get("","bloodLostMl")-lost)<.00001, "scorer accumulates measured vessel loss without fallback double counting");
            runner.Handle(CaseEvent.Surgery(Event("tick","assistant",200)));
            Require(Math.Abs(runner.Body.Get("","bloodLostMl")-lost)<.00001, "clock tick never reintegrates a measured vessel source");
            runner.Handle(CaseEvent.Surgery(Event("clamp","hemostat",200))); vessel.SetOccluded(true); Snapshot(200);
            Require(runner.Body.Get("","activeBleeds")==0 && vessel.FlowMillilitersPerSecond==0, "clamp control and real vessel snapshot agree on hemostasis");
            Require(vessel.Step(.02) && vessel.CumulativeLossMilliliters==lost, "occluded real vessel loses no more blood");
            vessel.SetOccluded(false); Require(vessel.Step(.02),"reopened real vessel advances"); Snapshot(220);
            runner.Handle(CaseEvent.Surgery(Event("tie","suture_tie",220))); vessel.SetOccluded(true); Snapshot(220);
            Require(runner.Body.Get("","activeBleeds")==0, "tie control and measured source stop resumed bleed");
            double beforePool=runner.Body.Get("","poolMl");
            var suction=Event("suction","suction_irrigator",220); suction.durationMs=1000;
            runner.Handle(CaseEvent.Surgery(suction));
            Require(runner.Body.Get("","poolMl")==beforePool, "semantic suction does not also drain a measured pool");
            vessel.RemovePool(vessel.PooledMilliliters); Snapshot(220);
            Require(runner.Body.Get("","poolMl")<.00001 && Math.Abs(runner.Body.Get("","bloodLostMl")-vessel.CumulativeLossMilliliters)<.00001,
                "real pool removal reaches scorer while preserving cumulative loss");
            int count=runner.Body.Log.Count;
            var sample=Event("fluid","assistant",220); sample.bloodLostMl=(float)vessel.CumulativeLossMilliliters;
            runner.Handle(CaseEvent.Surgery(sample)); runner.Handle(CaseEvent.Surgery(sample));
            Require(runner.Body.Log.Count==count+1 && Math.Abs(runner.Body.Get("","bloodLostMl")-vessel.CumulativeLossMilliliters)<.00001,
                "duplicate physical snapshot cannot count fluid twice");
        }
    }
}
