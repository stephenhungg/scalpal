using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
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
            VerifyStrokeGuardrails(bundle, procedure);
            VerifyClampRelease(bundle, procedure);
            VerifyTelemetryRate(bundle, procedure);
            VerifyFluid(procedure);
            VerifyRegionAtlas();
            VerifyTrackerEvents(bundle, procedure);
            int fixtureChecks = checks;
            // Actual native-scene atlas: mobilize, deliver, then measure the base on the moved anatomy.
            int delivery = OpenBodyDeliveryValidation.Run();
            // Actual native-scene open wall: cuts, paired muscle split and peritoneal tenting from the volume.
            int wall = OpenWallCouplingValidation.Run();
            checks += delivery + wall;
            Debug.Log("SCALPAL_OPEN_BODY_INTERACTION_VALIDATION_OK: " + checks + " checks (" + fixtureChecks + " synthetic actual-adapter and real-vessel snapshot, "
                + delivery + " actual-scene delivery, " + wall + " actual-scene wall coupling); no headset test");
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
            public readonly NativeVolumeSimulation volume;
            GameObject rigObject;
            public bool gate = true;
            public Fixture(ScalpalBundle bundle, Procedure procedure, params string[] ids) : this(bundle, procedure, false, ids) { }
            // withWall: the actual five-layer open-wall volume in the wound frame, stepped after the adapter
            // as at runtime (Update then LateUpdate); the fixture's blades are its registered cutting tools.
            public Fixture(ScalpalBundle bundle, Procedure procedure, bool withWall, params string[] ids)
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
                if (withWall)
                {
                    rigObject = new GameObject("InactiveWallFixtureRig"); rigObject.SetActive(false);
                    var rig = rigObject.AddComponent<NativeWorkbench>(); rig.tools = tools;
                    volume = root.AddComponent<NativeVolumeSimulation>();
                    volume.Initialize(wound, rig, () => gate && anatomy.RegistrationValid, true);
                    input.BindWall(volume);
                }
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
                    tool.action = InstrumentAction.Cut; // A wall volume sweeps only cutting tools.
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
            // Blade edge along the wound's inward axis, its deep end at local.
            public void MoveBlade(int index, Vector3 local)
            {
                var end = tools[index].GetComponentsInChildren<Transform>(true).First(t => t.name == "CutEnd");
                tools[index].transform.rotation = wound.rotation * Quaternion.FromToRotation(Vector3.right, Vector3.forward);
                tools[index].transform.position += wound.TransformPoint(local) - end.position;
                Physics.SyncTransforms();
            }
            // Runtime order: scored adapter (Update), then the wall solver/blade sweep (LateUpdate).
            public void Step(float seconds = .02f) { input.Simulate(seconds); if (volume) volume.Simulate(seconds); }
            public void Expose(Procedure procedure, int steps)
            {
                foreach (var step in procedure.steps.Take(steps)) foreach (var e in CaseRunner.PerfectEvents(step))
                    Require(binding.Submit(e, out _, out var reason), "precondition goes through real score binding: " + reason);
            }
            // The trigger is released for one frame: the blade leaves the tissue and the stroke ends.
            public void EndStroke(int index)
            {
                tools[index].SetActivation(0); Step(); tools[index].SetActivation(1);
            }
            public void Dispose() { UnityEngine.Object.DestroyImmediate(root); if (rigObject) UnityEngine.Object.DestroyImmediate(rigObject); }
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
                Require(!f.submitted.Any(r => r.action.verb == "cut"), "a stroke in progress is not committed millimetre by millimetre");
                f.EndStroke(0);
                Require(f.submitted.Count(r => r.action.verb == "cut") == 1, "one continuous stroke commits exactly one cut");
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
                Require(!f.submitted.Any(r => r.action.verb == "cut"), "transform regression begins with a real stroke in progress");
                Vector3 stationaryController = f.tools[0].actionPoint.position;
                f.wound.position += f.wound.right * .012f; Physics.SyncTransforms();
                f.input.Simulate(.02f); f.input.Simulate(.02f);
                int cuts = f.submitted.Count(r => r.action.verb == "cut");
                Require(f.tools[0].actionPoint.position == stationaryController && cuts == 1 && f.submitted.Last(r => r.action.verb == "cut").action.lengthMm == 5,
                    "wound fit correction commits the stroke measured before it once and cannot turn a stationary controller into a cut");
                f.tools[0].transform.SetParent(null, true);
                try
                {
                    f.tools[0].transform.position += f.wound.right * .004f; Physics.SyncTransforms(); f.input.Simulate(.02f);
                    cuts = f.submitted.Count(r => r.action.verb == "cut"); stationaryController = f.tools[0].actionPoint.position;
                    Vector3 fixedWound = f.wound.position;
                    f.root.transform.position += f.root.transform.right * .008f;
                    f.wound.position = fixedWound; Physics.SyncTransforms();
                    f.input.Simulate(.02f);
                    // The 4 mm already travelled before the correction is committed once; nothing after it.
                    Require(f.submitted.Count(r => r.action.verb == "cut") == cuts + 1 && f.submitted.Last(r => r.action.verb == "cut").action.lengthMm == 4,
                        "torso-only correction commits the stroke measured before it");
                    cuts++; f.input.Simulate(.02f); f.input.Simulate(.02f);
                    Require(f.tools[0].actionPoint.position == stationaryController && f.submitted.Count(r => r.action.verb == "cut") == cuts,
                        "torso-only correction does not score stationary controllers");
                    // The committed strokes opened the centre gap, so resume on the intact skin lip beside it.
                    f.tools[0].transform.position += f.wound.up * .01f; Physics.SyncTransforms(); f.input.Simulate(.02f);
                    f.tools[0].transform.position += f.wound.right * .005f; Physics.SyncTransforms(); f.input.Simulate(.02f);
                    f.EndStroke(0);
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
            // The actual membrane is gripped and lifted by the wall solver; its accepted lift tents it.
            void Lift(Fixture f, int index, float x = .02f)
            {
                f.Move(index, new Vector3(x, 0, .027f));
                for (int i = 0; i < 6; i++) f.Step();
                for (int i = 1; i <= 15; i++) { f.Move(index, new Vector3(x, 0, .027f - i * .001f)); f.Step(); }
                for (int i = 0; i < 10; i++) f.Step();
                Require(f.binding.Body.Get("peritoneum", "tented") == 1 && f.binding.Body.Get("peritoneum", "liftMm") >= 8,
                    "accepted membrane lift tents exposed peritoneum (tool " + index + " lift " + f.binding.Body.Get("peritoneum", "liftMm") + " mm, grasps "
                    + f.submitted.Count(r => r.action.verb == "grasp") + ", " + f.input.LastRejection + ")");
            }
            using (var f = new Fixture(bundle, procedure, true, "scalpel", "toothed_forceps"))
            {
                f.Expose(procedure, 4); Lift(f, 1);
                var mistakes = new List<string>(); f.binding.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                // Far from the held membrane, so the blade crosses the resting peritoneum.
                f.MoveBlade(0, new Vector3(-.045f, 0, .0295f)); f.Step();
                // Arrival from the parked tool exceeds the jump bound; the next stable pose
                // establishes contact before we test a real stroke in the release frame.
                f.Step();
                f.tools[1].SetHeld(false);
                f.MoveBlade(0, new Vector3(-.04f, 0, .0295f)); f.Step(); f.EndStroke(0);
                var release = f.submitted.FindLastIndex(r => r.action.choice == "release");
                var cut = f.submitted.FindLastIndex(r => r.action.verb == "cut" && r.action.tissueId == "peritoneum");
                Require(release >= 0 && cut > release && f.submitted[release].action.depthMm == 0,
                    "last forceps release scores zero lift before a blade earlier in tool order: " + string.Join(",", f.submitted.Skip(4).Select(r => r.action.verb + ":" + r.action.tissueId + ":" + r.action.choice + ":" + r.action.depthMm)) + " rejection=" + f.input.LastRejection);
                Require(f.binding.Body.Get("peritoneum", "tentedBeforeCut") == 0 && mistakes.Contains("lift_first"),
                    "cut after release records untented guardrail instead of stale tent success");
            }
            using (var f = new Fixture(bundle, procedure, true, "toothed_forceps"))
            {
                f.Expose(procedure, 4); Lift(f, 0);
                int records = f.binding.Body.Log.Count; double clock = f.input.ActiveSeconds;
                f.anatomy.SetRegistrationValid(false); f.Step();
                f.tools[0].SetHeld(false); f.input.ClearPlacements();
                Require(f.binding.Body.Log.Count == records && f.input.ActiveSeconds == clock && f.binding.Body.Get("peritoneum", "tented") == 1,
                    "registration loss queues release without mutating frozen body or clock");
                f.anatomy.SetRegistrationValid(true); f.Step();
                Require(f.binding.Body.Get("peritoneum", "tented") == 0 && f.submitted.Any(r => r.action.choice == "release"),
                    "first valid sample drains queued release through the scored path");
                for (int i = 0; i < 90; i++) f.Step(); // The released membrane relaxes before the next grasp.
                f.tools[0].SetHeld(true); f.tools[0].SetActivation(1); Lift(f, 0, -.03f);
                f.input.ClearPlacements();
                var selected = f.binding.SelectedCase;
                Require(f.binding.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out _), "retry selects fresh attempt");
                f.anatomy.SetRegistrationValid(true); f.tools[0].SetHeld(false);
                int releases = f.submitted.Count(r => r.action.choice == "release");
                f.Step();
                Require(f.submitted.Count(r => r.action.choice == "release") == releases && f.binding.Body.Log.Count == 0,
                    "retry discards queued previous-attempt releases");
            }
            using (var f = new Fixture(bundle, procedure, true, "toothed_forceps", "toothed_forceps"))
            {
                f.Expose(procedure, 4);
                f.Move(0, new Vector3(-.05f, 0, .027f)); Lift(f, 1);
                Require(f.binding.Body.Get("peritoneum", "tented") == 1, "an unlifted second grip does not erase another forceps' accepted lift");
                f.tools[0].SetHeld(false); f.Step();
                Require(f.binding.Body.Get("peritoneum", "tented") == 1 && f.submitted.Last(r => r.action.choice == "release").action.depthMm >= 8,
                    "one release retains the other forceps' accepted lift");
                f.tools[1].SetTrackingValid(false); f.Step();
                Require(f.binding.Body.Get("peritoneum", "tented") == 0,
                    "last forceps tracking loss releases tent through a scored event");
            }
            using (var f = new Fixture(bundle, procedure, "toothed_forceps"))
            {
                // No wall volume: lifting the controller is not tissue motion, so nothing is tented.
                f.Expose(procedure, 4);
                f.Move(0, new Vector3(.02f, 0, .027f)); for (int i = 0; i < 6; i++) f.Step();
                for (int i = 1; i <= 15; i++) { f.Move(0, new Vector3(.02f, 0, .027f - i * .001f)); f.Step(); }
                Require(f.submitted.Any(r => r.action.verb == "grasp" && r.action.tissueId == "peritoneum") &&
                    f.binding.Body.Get("peritoneum", "tented") == 0 && f.submitted.All(r => r.action.depthMm == 0),
                    "controller travel without a gripped membrane never tents");
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
                f.EndStroke(0);
                Require(f.binding.Body.Get("skin","opened") == 1 && f.binding.Body.Get("skin","cutErrorMm") > 19,
                    "off-line cut remains a real consequence with measured error");
            }
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                f.Expose(procedure,5);
                for (int i=0; i<=6; i++) { f.Move(0,new Vector3(.094f+i*.002f,0,.05f)); f.input.Simulate(.02f); }
                f.EndStroke(0);
                Require(f.submitted.Count(r=>r.action.tissueId=="terminal_ileum" && r.outcomes.Contains("hollow_leak")) == 1, "one stroke through bowel is one injury, not one per millimetre");
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
                // Paired physical splitting is verified on the actual wall in OpenWallCouplingValidation.
                Require(f.submitted.Any(r=>r.action.tissueId=="muscle" && r.action.verb=="retract") && f.binding.Body.Get("muscle","opened") == 0 &&
                    f.submitted.All(r=>r.action.separationMm == 0), "without a wall volume, retractor travel alone cannot split the muscle");
                Require(!f.input.Choose("appendix","invented_answer"), "unknown decision choice rejected");
                Require(f.input.Choose("appendix","true_base") && f.binding.Body.Get("appendix","decision_true_base") == 1,
                    "authored choice follows the existing scored event path");
                f.anatomy.SetRegistrationValid(false);
                Require(!f.input.Choose("appendix","appendix_tip"), "invalid registration blocks decision submission");
            }
        }
        static void VerifyStrokeGuardrails(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                Require(f.input.SetLandmarks(f.wound.TransformPoint(new Vector3(-.06f,0,0)), f.wound.TransformPoint(new Vector3(.12f,0,0)), f.wound.right), "landmarks accepted");
                var mistakes = new List<string>(); f.binding.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                for (int i = 0; i <= 12; i++) { f.Move(0, new Vector3(-.03f + i * .005f, .006f, 0)); f.input.Simulate(.02f); }
                f.EndStroke(0);
                Require(f.submitted.Count(r => r.action.verb == "cut") == 1 && mistakes.Count(id => id == "off_mark") == 1,
                    "a 60 mm stroke 6 mm off the line is one cut and one off_mark");
            }
            using (var f = new Fixture(bundle, procedure, "skin_marker"))
            {
                Require(f.input.SetLandmarks(f.wound.TransformPoint(new Vector3(-.06f,0,0)), f.wound.TransformPoint(new Vector3(.12f,0,0)), f.wound.right), "landmarks accepted");
                var mistakes = new List<string>(); f.binding.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                for (int i = 0; i <= 12; i++) { f.Move(0, new Vector3(-.03f + i * .005f, 0, 0)); f.input.Simulate(.02f); }
                f.EndStroke(0);
                Require(f.submitted.Count(r => r.action.verb == "mark") == 1 && mistakes.Count == 0 && f.binding.Body.Get("skin", "markErrorMm") == 0,
                    "a perfect mark is one mark action with no mark_far");
            }
        }
        static void VerifyClampRelease(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "hemostat"))
            {
                f.Expose(procedure, 5);
                f.Move(0, new Vector3(0, .01f, .019f)); f.input.Simulate(.02f); f.input.Simulate(.02f);
                Require(f.binding.Body.Get("muscle", "clampCount") == 1, "hemostat clamps the exposed muscle lip");
                f.Move(0, new Vector3(.10f, 0, .05f)); for (int i = 0; i < 3; i++) f.input.Simulate(.02f);
                Require(f.binding.Body.Get("terminal_ileum", "clampCount") == 1 && f.binding.Body.Get("muscle", "clampCount") == 0 &&
                    f.submitted.Any(r => r.action.verb == "release" && r.action.tissueId == "muscle"), "applying one physical clamp elsewhere first releases its previous tissue");
                f.tools[0].SetHeld(false); f.input.ClearPlacements(); f.input.Simulate(.02f);
                Require(f.binding.Body.Get("terminal_ileum", "clampCount") == 0 && f.submitted.Last().action.verb == "release",
                    "putting a placed clamp away releases it in the body");
            }
        }
        // The coarse region map must agree with the imported surface atlas, in the registered torso frame
        // (umbilicus origin, +X left, +Y anterior, +Z cranial), or a neck cut is reported as the chest.
        static void VerifyRegionAtlas()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Scalpal/Anatomy/Models/surface.fbx");
            Require(model, "imported surface atlas exists");
            var expected = new Dictionary<string, string> {
                ["hairs_of_head"] = "head", ["frontal_region_l"] = "head", ["occipital_region_r"] = "head",
                ["lateral_region_of_neck_l"] = "neck", ["lateral_region_of_neck_r"] = "neck", ["posterior_region_of_neck_l"] = "neck",
                ["presternal_region_l"] = "chest", ["pectoral_region_r"] = "chest", ["mammary_region_l"] = "chest", ["infraclavicular_fossa_r"] = "chest",
                ["deltoid_region_l"] = "left_arm", ["anterior_region_of_arm_r"] = "right_arm", ["anterior_region_of_forearm_l"] = "left_arm", ["dorsum_of_hand_r"] = "right_arm",
                ["anterior_region_of_thigh_l"] = "left_leg", ["anterior_region_of_knee_r"] = "right_leg", ["dorsum_of_foot_l"] = "left_leg",
                ["umbilical_region_r"] = "", ["inguinal_region_r"] = "", ["lateral_region_of_abdomen_r"] = "", ["epigastric_region_l"] = "" };
            var filters = model.GetComponentsInChildren<MeshFilter>(true).ToDictionary(f => f.name);
            foreach (var pair in expected)
            {
                Require(filters.TryGetValue("atlas_surface__" + pair.Key, out var filter), "atlas part exists: " + pair.Key);
                Vector3 source = filter.transform.TransformPoint(filter.sharedMesh.bounds.center);
                var torso = new Vector3(source.x, BodyRegistrationMath.SourceFront - source.z, source.y - BodyRegistrationMath.SourceUmbilicus.y);
                string region = OpenBodyInteraction.BodyRegion(torso);
                Require(region == pair.Value, pair.Key + " at torso " + torso.ToString("F3") + " maps to '" + pair.Value + "', not '" + region + "'");
            }
            Require(OpenBodyInteraction.BodyRegion(new Vector3(0, .12f, .3f)) == "" && OpenBodyInteraction.BodyRegion(new Vector3(0, -.3f, .3f)) == ""
                && OpenBodyInteraction.BodyRegion(new Vector3(float.NaN, 0, .3f)) == "", "a blade above the skin, under the back or untracked is not on the body");
        }
        // Jarvis's state tracker: a cut outside the field is one injury per region until a hemostatic tool controls it,
        // and a tip contact is one event per touch, never one per frame.
        static void VerifyTrackerEvents(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "scalpel", "hemostat"))
            {
                var injuries = new List<string>(); var contacts = new List<string>();
                f.input.RegionInjured += (region, tool, controlled) => injuries.Add(region + ":" + tool.instrumentId + ":" + controlled);
                f.input.Contacted += (tool, tissue) => contacts.Add(tool.instrumentId + ":" + tissue);
                void At(int index, Vector3 torso, int frames = 3)
                {
                    f.tools[index].transform.position += f.root.transform.TransformPoint(torso) - f.tools[index].actionPoint.position;
                    Physics.SyncTransforms(); for (int i = 0; i < frames; i++) f.Step();
                }
                At(1, new Vector3(.3f, .4f, .1f), 1);
                At(0, new Vector3(0, -.06f, .46f));
                Require(string.Join(",", injuries) == "neck:scalpel:False", "a blade in the neck reports one neck injury: " + string.Join(",", injuries));
                At(0, new Vector3(.03f, -.07f, .47f), 10);
                Require(injuries.Count == 1, "dragging the blade within the injured neck is not a new injury every frame");
                At(0, new Vector3(0, .1f, .3f));
                Require(injuries.Count == 1, "a blade above the chest skin cuts nothing");
                At(0, new Vector3(.05f, -.05f, .3f));
                At(0, new Vector3(.1f, -.05f, .05f));
                Require(string.Join(",", injuries) == "neck:scalpel:False,chest:scalpel:False", "the chest is its own injury; the abdomen beside the field is not a region: " + string.Join(",", injuries));
                At(1, new Vector3(.21f, -.12f, .2f));
                Require(injuries.Count == 2, "a clamp on an uninjured arm controls nothing");
                At(1, new Vector3(0, -.06f, .46f), 6);
                Require(injuries.Count == 3 && injuries[2] == "neck:hemostat:True", "a hemostat in the injured neck controls it once: " + string.Join(",", injuries));
                At(1, new Vector3(.3f, .4f, .1f), 1);
                At(0, new Vector3(0, -.06f, .46f));
                Require(injuries.Count == 4 && injuries[3] == "neck:scalpel:False", "cutting the controlled neck again is a new injury");
                Require(contacts.Count == 0, "region cuts outside the field are not tissue contacts");
                f.Move(0, new Vector3(-.03f, 0, 0)); for (int i = 0; i < 8; i++) f.Step();
                Require(string.Join(",", contacts) == "scalpel:skin" && injuries.Count == 4, "a tip resting on the skin is one contact and no injury: " + string.Join(",", contacts));
                At(0, new Vector3(.3f, .4f, .1f));
                f.Move(0, new Vector3(-.03f, 0, 0)); for (int i = 0; i < 3; i++) f.Step();
                Require(string.Join(",", contacts) == "scalpel:skin,scalpel:skin", "lifting off and touching again is a second contact");
            }
        }
        static void VerifyTelemetryRate(ScalpalBundle bundle, Procedure procedure)
        {
            using (var f = new Fixture(bundle, procedure, "scalpel"))
            {
                f.Expose(procedure, 5); // Synthetic exposure is stamped at time 0, so it precedes the clocked samples.
                var vessels = f.root.AddComponent<OpenBodyBleeding>(); vessels.Initialize(f.input, f.binding, f.wound);
                int perfused = f.binding.Body.Tissues.Count(t => t.perfused);
                int start = f.binding.Body.Log.Count;
                for (int i = 0; i < 100; i++) { f.input.Simulate(.02f); vessels.Simulate(.02f); }
                Require(f.binding.Body.Log.Count - start == perfused && f.binding.Body.Log.Skip(start).All(r => r.action.verb == "fluid"),
                    "while nothing bleeds: one zero fluid snapshot per vessel, then no ticks and no unchanged snapshots");
                var cut = f.input.CreateMeasurement("scalpel", "cut", "mesoappendix", f.wound.position); cut.lengthMm = 4; cut.distanceMm = 10;
                Require(f.input.SubmitMeasured(cut) && f.binding.Body.Get("", "activeBleeds") == 1, "unsecured cut starts a bleed");
                start = f.binding.Body.Log.Count;
                for (int i = 0; i < 105; i++) { f.input.Simulate(.02f); vessels.Simulate(.02f); } // 2.1 s
                var bleeding = f.binding.Body.Log.Skip(start).ToArray();
                Require(bleeding.Count(r => r.action.verb == "tick") == 2 && bleeding.Count(r => r.action.verb == "fluid") >= 2 &&
                    bleeding.Where(r => r.action.verb == "fluid").Zip(bleeding.Where(r => r.action.verb == "fluid").Skip(1), (a, b) => b.action.timeMs - a.action.timeMs).All(gap => gap >= 990) &&
                    bleeding.All(r => r.action.verb == "tick" || (r.action.verb == "fluid" && r.action.tissueId == "mesoappendix")),
                    "a bleed ticks at 1 Hz and only the changing vessel publishes, at 1 Hz: " + string.Join(",", bleeding.Select(r => r.action.verb + ":" + r.action.tissueId + "@" + r.action.timeMs)));
                UnityEngine.Object.DestroyImmediate(vessels);
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
