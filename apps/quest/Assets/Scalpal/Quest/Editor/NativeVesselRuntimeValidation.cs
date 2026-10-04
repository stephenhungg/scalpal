using System;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic native-component fixtures with actual finite blade events and PhysX
    // penetration queries. No clinical geometry fit, live XR or headset evidence.
    public static class NativeVesselRuntimeValidation
    {
        const float Step = 1f / 60f;
        static int checks;
        [MenuItem("Scalpal/Quest/Validate Native Vessel Runtime")]
        public static void Run()
        {
            checks = 0;
            SourceBindings();
            using (var fixture = new Fixture())
            {
                BladeGates(fixture);
                PauseAndRetry(fixture);
                ContactControls(fixture);
                Lifecycle(fixture);
                IndicatorAndCaches(fixture);
            }
            using (var importedUnits = new Fixture(100))
            {
                Assert(Near(importedUnits.UnitScale, .01), "100x mesh units are converted to source meters");
                BladeGates(importedUnits);
                ContactControls(importedUnits);
                IndicatorAndCaches(importedUnits);
            }
            Debug.Log("SCALPAL_NATIVE_VESSEL_RUNTIME_VALIDATION_OK checks=" + checks + " synthetic native geometry/contact/lifecycle fixtures; no clinical/headset validation");
        }

        static void BladeGates(Fixture f)
        {
            Assert(f.Artery.stableId == "appendicular_artery" && f.SourceMesh == f.Contact.sharedMesh, "fixture has stable vessel ID and shared mesh contact");
            Assert(f.Volume.Wall && f.Volume.Wall.transform.parent == f.SourceFrame, "producer wall uses shared source frame");
            Assert(f.Pool && !f.Pool.activeSelf && f.Pool.GetComponents<Collider>().Length == 0, "pool starts hidden and adds no scoring/contact collider");

            f.Reset(); f.MoveKnife(new Vector3(.2f, 0, -.009f)); f.Tick(); f.MoveKnife(new Vector3(.2f, 0, .009f)); f.Tick();
            Assert(f.Sweeps > 0, "remote motion emits a finite producer triangle"); NoInjury(f, "remote finite triangle does not become an infinite cutting plane");

            f.Reset(); f.MoveKnife(Vector3.zero); f.Tick(); f.Tick();
            Assert(f.Sweeps == 0, "stationary blade emits no degenerate sweep"); NoInjury(f, "stationary blade resting inside vessel cannot open injury");

            f.Reset(); f.MoveKnife(Vector3.back * .05f); f.Tick(); f.MoveKnife(Vector3.forward * .05f); f.Tick();
            Assert(f.Sweeps == 0, "over-4cm pose gap emits no bridged blade triangle"); NoInjury(f, "pose jump across artery does not create injury");

            f.Reset(); f.MoveKnife(Vector3.back * .009f); f.Tick(); f.Knife.SetTrackingValid(false);
            f.MoveKnife(Vector3.forward * .009f); f.Tick(); NoInjury(f, "untracked blade rejects injury");
            Enable(f.Knife); f.Tick(); NoInjury(f, "tracking recovery first pose does not bridge missing samples");

            f.Reset(); f.Knife.SetHeld(false); f.Sweep(); NoInjury(f, "unheld blade rejects injury");
            f.Reset(); f.Knife.SetActivation(.69f); f.Sweep(); NoInjury(f, "blade activation threshold rejects injury");
            f.Reset(); f.Knife.action = InstrumentAction.Grasp; f.Sweep(); NoInjury(f, "wrong blade action rejects injury"); f.Knife.action = InstrumentAction.Cut;
            f.Reset(); f.Ready = false; f.Sweep(); NoInjury(f, "session readiness rejects injury");
            f.Reset(); f.GlobalTrackingReady = false; f.Sweep(); NoInjury(f, "shared tracking readiness rejects injury");
            f.Reset(); f.Anatomy.SetRegistrationValid(false); f.Sweep(); NoInjury(f, "invalid registration rejects injury");
            f.Reset(); f.Artery.SetVisible(false); f.Sweep(); NoInjury(f, "hidden artery rejects injury");
            f.Reset(); f.ArteryRenderer.enabled = false; f.Sweep(); NoInjury(f, "hidden artery geometry rejects injury");
            f.Reset(); f.Contact.enabled = false; f.Sweep(); NoInjury(f, "disabled artery collider rejects injury");

            f.Reset(); f.MoveKnife(Vector3.back * .009f); f.Tick(); f.Ready = false; f.Tick();
            f.MoveKnife(Vector3.forward * .009f); f.Ready = true; f.Tick();
            Assert(f.Sweeps == 0, "readiness gap discards producer history"); NoInjury(f, "readiness recovery does not fabricate a missing sweep");

            f.Reset(); f.MoveKnife(Vector3.back * .009f); f.Tick();
            f.SourceFrame.SetPositionAndRotation(f.SourceFrame.position + Vector3.right * .01f, Quaternion.Euler(12, 42, 7));
            f.MoveKnife(Vector3.forward * .009f); f.Tick();
            Assert(f.Sweeps == 0, "source-frame change clears blade history"); NoInjury(f, "registration frame motion does not injure artery");

            f.Reset(); f.Injure();
            Assert(f.Sweeps > 0 && f.Vessel.Fluid.InjuryAreaSquareMeters > 0, "actual source-frame finite sweep intersects artery and opens an orifice");
            Assert(f.Vessel.Fluid.CumulativeLossMilliliters > 0 && f.Pool.activeSelf, "open orifice discharges fluid and exposes pool");
            Assert(finite(f.Vessel.Fluid.CumulativeLossMilliliters), "geometry-driven discharge remains finite");
            double area = f.Vessel.Fluid.InjuryAreaSquareMeters;
            f.MoveKnife(Vector3.back * .009f); f.Tick();
            Assert(f.Vessel.Fluid.InjuryAreaSquareMeters == area, "repeat actual sweep does not double one effective injury");
            AssertVertices(f.OriginalVertices, f.SourceMesh.vertices, "blade events do not mutate shared artery mesh");
            Assert(f.Root.GetComponentsInChildren<TrainingTarget>(true).Length == 0, "vessel and generated wall add no duplicate TrainingTarget");
            Ledger(f.Vessel.Fluid);
            Vector3 scale = f.Pool.transform.localScale * f.UnitScale;
            double visualVolume = 4 * Math.PI / 3 * scale.x * .5 * scale.y * .5 * scale.z * .5;
            Assert(Near(visualVolume * 1000000, f.Vessel.Fluid.PooledMilliliters), "small uncapped indicator matches ledger before its explicitly bounded display cap");
        }

        static void PauseAndRetry(Fixture f)
        {
            f.Reset(); f.Injure(); double loss = f.Vessel.Fluid.CumulativeLossMilliliters;
            double pool = f.Vessel.Fluid.PooledMilliliters;
            f.Ready = false; f.Tick(); Frozen(f, loss, pool, "session pause");
            f.Ready = true; f.Tick(); Assert(f.Vessel.Fluid.CumulativeLossMilliliters > loss, "readiness recovery resumes original injury");
            loss = f.Vessel.Fluid.CumulativeLossMilliliters; pool = f.Vessel.Fluid.PooledMilliliters;
            f.GlobalTrackingReady = false; f.Tick(); Frozen(f, loss, pool, "shared head/session tracking loss");
            f.GlobalTrackingReady = true; f.Tick();
            loss = f.Vessel.Fluid.CumulativeLossMilliliters; pool = f.Vessel.Fluid.PooledMilliliters;
            f.Anatomy.SetRegistrationValid(false); f.Tick(); Frozen(f, loss, pool, "registration loss");
            f.Anatomy.SetRegistrationValid(true); f.Tick();
            loss = f.Vessel.Fluid.CumulativeLossMilliliters; pool = f.Vessel.Fluid.PooledMilliliters;
            f.Artery.SetVisible(false); f.Tick(); Frozen(f, loss, pool, "hidden artery");
            f.Artery.SetVisible(true); f.Tick();
            loss = f.Vessel.Fluid.CumulativeLossMilliliters; pool = f.Vessel.Fluid.PooledMilliliters;
            f.Vessel.enabled = false; f.Vessel.Simulate(Step); Frozen(f, loss, pool, "disabled vessel component");
            f.Vessel.enabled = true;
            foreach (float dt in new[] { float.NaN, float.PositiveInfinity, -Step, 0 })
            {
                f.Vessel.Simulate(dt); Frozen(f, loss, pool, "invalid runtime time step");
            }
            f.Knife.SetHeld(false); f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.CumulativeLossMilliliters > loss, "putting down a tool does not pause circulation while shared readiness stays valid");
            f.Vessel.ResetTissues(); f.Volume.ResetTissues();
            Assert(f.Vessel.Fluid.InjuryAreaSquareMeters == 0 && f.Vessel.Fluid.CumulativeLossMilliliters == 0 && f.Vessel.Fluid.PooledMilliliters == 0,
                "retry clears injury and lost/pool volumes");
            Assert(!f.Pool.activeSelf && !f.Vessel.Fluid.IsOccluded && f.Vessel.Fluid.RemovedMilliliters == 0,
                "retry clears pool presentation, suction and occlusion");
            f.Reset(); f.Injure(); Assert(f.Vessel.Fluid.CumulativeLossMilliliters > 0, "retry can open a new geometric injury");
            Ledger(f.Vessel.Fluid);
        }

        static void ContactControls(Fixture f)
        {
            foreach (var tool in new[] { f.Seal, f.Clip })
            {
                f.Reset(); f.Injure(); f.AtInjury(tool); Enable(tool);
                var shape = tool.GetComponentInChildren<SphereCollider>(); var tip = tool.GetComponentInChildren<InstrumentTipContact>();
                Physics.SyncTransforms();
                Assert(Physics.ComputePenetration(shape, shape.transform.position, shape.transform.rotation,
                    f.Contact, f.Contact.transform.position, f.Contact.transform.rotation, out _, out _), "fixture has real owned tip/vessel penetration for " + tool.action);

                shape.enabled = false; f.Vessel.Simulate(Step); NotOccluded(f, "disabled tip collider cannot stop flow"); shape.enabled = true;
                shape.isTrigger = false; f.Vessel.Simulate(Step); NotOccluded(f, "non-trigger contact cannot stop flow"); shape.isTrigger = true;
                tip.enabled = false; f.Vessel.Simulate(Step); NotOccluded(f, "disabled InstrumentTipContact cannot stop flow"); tip.enabled = true;
                tool.SetHeld(false); f.Vessel.Simulate(Step); NotOccluded(f, "unheld seal/clip cannot stop flow"); Enable(tool);
                tool.SetTrackingValid(false); f.Vessel.Simulate(Step); NotOccluded(f, "untracked seal/clip cannot stop flow"); Enable(tool);
                tool.SetActivation(.69f); f.Vessel.Simulate(Step); NotOccluded(f, "inactive seal/clip cannot stop flow"); Enable(tool);
                tool.SetActivation(float.NaN); f.Vessel.Simulate(Step); NotOccluded(f, "nonfinite seal/clip activation cannot stop flow"); Enable(tool);

                // Action point is at the injury, but its real tip is remote.
                shape.transform.localPosition = Vector3.one * .1f; Physics.SyncTransforms();
                f.Vessel.Simulate(Step); NotOccluded(f, "action point proximity without real collision cannot stop flow");
                shape.transform.localPosition = Vector3.zero; Physics.SyncTransforms();
                // Real tip still intersects the vessel, but the declared action point is distant.
                Transform originalPoint = tool.actionPoint;
                var distant = new GameObject("SyntheticRemoteActionPoint"); distant.transform.SetParent(tool.transform, false); distant.transform.localPosition = Vector3.one * .1f;
                tool.actionPoint = distant.transform; f.Vessel.Simulate(Step); NotOccluded(f, "vessel collision away from action point cannot stop flow");
                tool.actionPoint = originalPoint; UnityEngine.Object.DestroyImmediate(distant);

                double before = f.Vessel.Fluid.CumulativeLossMilliliters;
                f.Vessel.Simulate(Step);
                Assert(f.Vessel.Fluid.IsOccluded && f.Vessel.Fluid.CumulativeLossMilliliters == before, "valid tracked " + tool.action + " at actual injury stops new discharge");
                Assert(f.Vessel.Fluid.PooledMilliliters > 0 && f.Pool.activeSelf, "seal/clip retains blood already pooled");
                Ledger(f.Vessel.Fluid);
            }

            f.Reset(); f.Injure(); f.AtInjury(f.Seal); Enable(f.Seal); f.Vessel.Simulate(Step); f.Seal.SetHeld(false);
            Assert(f.Vessel.Fluid.IsOccluded && f.Pool.activeSelf, "suction fixture first occludes real injured vessel");
            f.Suction.transform.position = f.Pool.transform.position + Vector3.one; Enable(f.Suction);
            double pooled = f.Vessel.Fluid.PooledMilliliters, lost = f.Vessel.Fluid.CumulativeLossMilliliters;
            f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters == pooled && f.Vessel.Fluid.RemovedMilliliters == 0, "remote suction does not remove fluid");
            f.Suction.contactRadius = float.PositiveInfinity; f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters == pooled && f.Vessel.Fluid.RemovedMilliliters == 0, "nonfinite suction radius cannot reach a remote pool");
            f.Suction.contactRadius = .003f; f.Suction.transform.position = f.Pool.transform.position;
            f.Suction.SetTrackingValid(false); f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters == pooled, "untracked suction cannot remove fluid"); Enable(f.Suction);
            f.Suction.SetActivation(.69f); f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters == pooled, "inactive suction cannot remove fluid"); Enable(f.Suction);
            f.Suction.SetActivation(float.NaN); f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters == pooled, "nonfinite suction activation cannot remove fluid"); Enable(f.Suction);
            f.Suction.SetHeld(false); f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters == pooled, "unheld suction cannot remove fluid"); Enable(f.Suction);
            f.Vessel.Simulate(Step);
            Assert(f.Vessel.Fluid.PooledMilliliters < pooled && f.Vessel.Fluid.RemovedMilliliters > 0, "valid suction near rendered pool removes bounded volume");
            Assert(f.Vessel.Fluid.CumulativeLossMilliliters == lost, "suction does not roll back cumulative blood loss");
            Ledger(f.Vessel.Fluid);
        }

        static void IndicatorAndCaches(Fixture f)
        {
            var template = Resources.Load<Material>("TissueOpaque");
            Assert(template && AssetDatabase.Contains(template) && template.shader && template.shader.name == "Standard" && template.GetFloat("_Mode") == 0,
                "committed resource explicitly retains opaque Standard shader variant");
            Assert(f.Pool.GetComponent<Renderer>().sharedMaterial.shader == template.shader &&
                f.Volume.Wall.GetComponent<Renderer>().sharedMaterials.All(m => m.shader == template.shader),
                "blood and every wall layer instantiate the authored shader reference");
            f.Reset(); f.Injure();
            for (int i = 0; i < 7000; i++) f.Vessel.Fluid.Step(.1);
            f.Knife.SetHeld(false); f.Vessel.Simulate(Step);
            Assert(Near(f.Vessel.Fluid.PooledMilliliters, f.Vessel.Fluid.InitialSourceMilliliters),
                "indicator-cap fixture actually exhausts the 500 mL reservoir");
            Vector3 scale = f.Pool.transform.localScale * f.UnitScale;
            Assert(Near(scale.x * .5, NativeVesselSimulation.MaximumPoolIndicatorRadius) && Near(scale.y * .5, NativeVesselSimulation.MaximumPoolIndicatorRadius),
                "large-volume display remains bounded at 4 cm radius rather than 28 cm");
            Assert(Vector3.Dot(f.Pool.transform.forward, -Physics.gravity.normalized) > .9999f,
                "indicator thin axis follows world gravity despite rotated anatomy and artery transforms");
            var poolQuery = (Func<Vector3, float, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, float, bool>), f.Vessel,
                typeof(NativeVesselSimulation).GetMethod("TouchesPool", BindingFlags.Instance | BindingFlags.NonPublic));
            Assert(poolQuery(f.Pool.transform.position + f.Pool.transform.right * .03f, .001f), "suction reaches rotated in-plane indicator edge");
            Assert(!poolQuery(f.Pool.transform.position + f.Pool.transform.forward * .01f, .001f), "suction rejects point above thin indicator along gravity");
            Assert(!poolQuery(f.Pool.transform.position + f.Pool.transform.right * .06f, .001f), "suction rejects beyond capped visual indicator");
            Ledger(f.Vessel.Fluid);

            f.Reset(); f.Injure(); f.AtInjury(f.Seal); Enable(f.Seal); f.Vessel.Simulate(Step);
            // Warm caches/JIT before measuring managed allocation on the actual hot paths.
            var sweep = (Action<Vector3, Vector3, Vector3>)Delegate.CreateDelegate(typeof(Action<Vector3, Vector3, Vector3>), f.Vessel,
                typeof(NativeVesselSimulation).GetMethod("BladeSweep", BindingFlags.Instance | BindingFlags.NonPublic));
            Vector3 a = f.Volume.Wall.transform.InverseTransformPoint(f.Artery.transform.TransformPoint(new Vector3(-.01f, 0, -.005f) / f.UnitScale));
            Vector3 b = f.Volume.Wall.transform.InverseTransformPoint(f.Artery.transform.TransformPoint(new Vector3(.01f, 0, -.005f) / f.UnitScale));
            Vector3 c = f.Volume.Wall.transform.InverseTransformPoint(f.Artery.transform.TransformPoint(new Vector3(0, 0, .005f) / f.UnitScale));
            for (int i = 0; i < 10; i++) { sweep(a, b, c); f.Vessel.Simulate(Step); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) sweep(a, b, c);
            long bladeAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) f.Vessel.Simulate(Step);
            long simulationAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(bladeAllocated < 4096, "1000 overlapping blade queries reuse mesh buffers, allocated=" + bladeAllocated);
            Assert(simulationAllocated < 4096, "1000 active seal/clip steps reuse owned tip cache, allocated=" + simulationAllocated);
            Assert(f.Vessel.Fluid.IsOccluded, "allocation fixture exercised an active intersecting seal tip");
        }

        static void Lifecycle(Fixture f)
        {
            f.Reset(); f.Injure(); var oldPool = f.Pool; var oldMaterial = oldPool.GetComponent<Renderer>().sharedMaterial; var oldFluid = f.Vessel.Fluid;
            f.Reinitialize();
            Assert(!oldPool && !oldMaterial, "explicit Editor reinitialize disposes prior generated pool/material");
            Assert(f.Pool && !f.Pool.activeSelf && !ReferenceEquals(oldFluid, f.Vessel.Fluid) && f.Vessel.Fluid.CumulativeLossMilliliters == 0,
                "reinitialize creates a fresh hidden pool and fluid ledger");
            Assert(ConsumerSubscriptions(f.Volume, f.Vessel) == 1, "reinitialize owns exactly one blade-event subscription");
            f.Artery.stableId = "synthetic_unknown_vessel"; f.Anatomy.RebuildIndex(); f.Reinitialize();
            Assert(!f.Pool && ConsumerSubscriptions(f.Volume, f.Vessel) == 0, "missing stable artery ID fails closed and removes previous binding");
            f.Sweep(); Assert(f.Vessel.Fluid.CumulativeLossMilliliters == 0, "unbound artery cannot discharge");
            f.Artery.stableId = "appendicular_artery"; f.Anatomy.RebuildIndex(); f.Anatomy.SetRegistrationValid(true); f.Reinitialize();
            Assert(f.Pool && ConsumerSubscriptions(f.Volume, f.Vessel) == 1, "correct ID rebind restores one consumer");
            f.Vessel.Initialize(f.Anatomy, null, f.Volume, () => true);
            f.Vessel.Simulate(Step);
            Assert(!f.Pool && ConsumerSubscriptions(f.Volume, f.Vessel) == 0 && f.Vessel.Fluid.CumulativeLossMilliliters == 0,
                "missing workbench fails closed without a null dereference or event binding");
            f.Reinitialize();
            AssertVertices(f.OriginalVertices, f.SourceMesh.vertices, "lifecycle never edits shared geometry");
            Assert(f.Artery.GetComponent<MeshFilter>().sharedMesh == f.SourceMesh && f.Contact.sharedMesh == f.SourceMesh,
                "runtime binding preserves source render/contact references");
            Assert(f.Root.GetComponentsInChildren<TrainingTarget>(true).Length == 0, "reinitialize does not create a scored target");
        }

        static void SourceBindings()
        {
            var session = UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Assert(session && session.anatomy && session.workbench, "actual native scene exposes shared anatomy/workbench bindings");
            var artery = session.anatomy.GetComponentsInChildren<AnatomyPart>(true).SingleOrDefault(p => p.stableId == "appendicular_artery");
            Assert(artery, "actual scene has exactly one stable appendicular artery");
            var render = artery.GetComponent<MeshFilter>(); var contact = artery.GetComponent<MeshCollider>();
            Assert(render && contact && render.sharedMesh && render.sharedMesh == contact.sharedMesh && render.sharedMesh.isReadable,
                "actual artery has readable matching source render/contact mesh");
            Assert(AssetDatabase.Contains(render.sharedMesh) && render.sharedMesh.triangles.Length / 3 == 2006, "actual imported artery inventory has 2006 triangles");
            var cutters = (session.workbench.tools ?? Array.Empty<InstrumentBehaviour>()).Where(t => t && t.action == InstrumentAction.Cut).ToArray();
            Assert(cutters.Length > 0, "actual source workbench contains a blade producer");
            foreach (var tool in cutters)
            {
                var anchors = tool.GetComponentsInChildren<Transform>(true).Where(t => t.GetComponentInParent<InstrumentBehaviour>() == tool).ToArray();
                Assert(anchors.Count(t => t.name == "CutStart") == 1 && anchors.Count(t => t.name == "CutEnd") == 1,
                    "actual cut tool has unique owned finite-blade anchors: " + tool.instrumentId);
                Assert(anchors.Where(t => t.name == "CutStart" || t.name == "CutEnd").All(t => TissueCage.Finite(t.position)),
                    "actual cut anchor world positions are finite: " + tool.instrumentId);
            }
        }

        sealed class Fixture : IDisposable
        {
            public readonly GameObject Root, RigObject;
            public readonly Transform SourceFrame;
            public readonly AnatomyController Anatomy;
            public readonly AnatomyPart Artery;
            public readonly Mesh SourceMesh;
            public readonly Vector3[] OriginalVertices;
            public readonly MeshCollider Contact;
            public readonly MeshRenderer ArteryRenderer;
            public readonly NativeWorkbench Rig;
            public readonly NativeVolumeSimulation Volume;
            public readonly NativeVesselSimulation Vessel;
            public readonly InstrumentBehaviour Knife, Seal, Clip, Suction;
            public bool Ready = true, GlobalTrackingReady = true;
            public int Sweeps;
            public GameObject Pool => Field<GameObject>(Vessel, "pool");
            public float UnitScale => Field<float>(Vessel, "sourceUnitScale");
            public Fixture(float meshImportScale = 1)
            {
                Root = new GameObject("SyntheticNativeVesselRuntime"); RigObject = new GameObject("SyntheticInactiveVesselWorkbench"); RigObject.SetActive(false);
                Rig = RigObject.AddComponent<NativeWorkbench>();
                SourceFrame = new GameObject("SyntheticAtlasFrame").transform; SourceFrame.SetParent(Root.transform, false);
                SourceFrame.SetPositionAndRotation(new Vector3(1, .4f, -2), Quaternion.Euler(9, 35, 6));
                var anatomyObject = new GameObject("SyntheticOwnedAnatomy"); anatomyObject.transform.SetParent(SourceFrame, false);
                var arteryObject = new GameObject("anat_appendicular_artery"); arteryObject.transform.SetParent(anatomyObject.transform, false);
                arteryObject.transform.SetLocalPositionAndRotation(new Vector3(.03f, 1.018f, -.09f), Quaternion.Euler(0, 0, 27));
                arteryObject.transform.localScale = Vector3.one / meshImportScale;
                SourceMesh = BoxMesh(meshImportScale); OriginalVertices = SourceMesh.vertices;
                arteryObject.AddComponent<MeshFilter>().sharedMesh = SourceMesh;
                ArteryRenderer = arteryObject.AddComponent<MeshRenderer>(); Contact = arteryObject.AddComponent<MeshCollider>(); Contact.sharedMesh = SourceMesh;
                Artery = arteryObject.AddComponent<AnatomyPart>(); Artery.stableId = "appendicular_artery";
                Anatomy = anatomyObject.AddComponent<AnatomyController>(); Anatomy.initiallyHiddenSystems = Array.Empty<string>(); Anatomy.RebuildIndex(); Anatomy.SetRegistrationValid(true);
                Knife = MakeTool("synthetic_blade", InstrumentAction.Cut); Seal = MakeTool("synthetic_seal", InstrumentAction.Seal);
                Clip = MakeTool("synthetic_clip", InstrumentAction.Clip); Suction = MakeTool("synthetic_suction", InstrumentAction.Suction);
                var start = new GameObject("CutStart").transform; start.SetParent(Knife.transform, false); start.localPosition = Vector3.left * .015f;
                var end = new GameObject("CutEnd").transform; end.SetParent(Knife.transform, false); end.localPosition = Vector3.right * .015f;
                Rig.tools = new[] { Knife, Seal, Clip, Suction };
                Volume = Root.AddComponent<NativeVolumeSimulation>(); Volume.Initialize(SourceFrame, Rig, () => Ready && GlobalTrackingReady && Anatomy.RegistrationValid);
                Vessel = Root.AddComponent<NativeVesselSimulation>(); Reinitialize();
                Volume.BladeSwept += CountSweep;
                Reset();
            }
            InstrumentBehaviour MakeTool(string id, InstrumentAction action)
            {
                var obj = new GameObject(id); obj.transform.SetParent(Root.transform, false);
                var tool = obj.AddComponent<InstrumentBehaviour>(); tool.instrumentId = id; tool.action = action; tool.contactRadius = .003f;
                var tip = new GameObject("OwnedInstrumentTip"); tip.transform.SetParent(obj.transform, false);
                var shape = tip.AddComponent<SphereCollider>(); shape.radius = .003f; shape.isTrigger = true; tip.AddComponent<InstrumentTipContact>();
                // Separate declared action point and physical tip so rejection fixtures
                // can vary proximity and collider penetration independently.
                tool.actionPoint = obj.transform;
                return tool;
            }
            void CountSweep(Vector3 a, Vector3 b, Vector3 c)
            {
                Assert(TissueCage.Finite(a) && TissueCage.Finite(b) && TissueCage.Finite(c) && Vector3.Cross(b - a, c - a).sqrMagnitude >= 1e-12f,
                    "producer emits only finite nondegenerate source-frame triangles"); Sweeps++;
            }
            public void Reinitialize() => Vessel.Initialize(Anatomy, Rig, Volume, () => Ready && GlobalTrackingReady);
            public void Reset()
            {
                Ready = GlobalTrackingReady = true; Vessel.enabled = Volume.enabled = true;
                ArteryRenderer.enabled = true; Contact.enabled = true; Anatomy.SetRegistrationValid(true); Artery.SetVisible(true);
                Volume.ResetTissues(); Vessel.ResetTissues(); Sweeps = 0;
                Knife.action = InstrumentAction.Cut;
                foreach (var tool in Rig.tools)
                {
                    tool.SetHeld(false); tool.SetTrackingValid(false); tool.transform.position = Vector3.one * 5;
                    tool.enabled = true; tool.contactRadius = .003f;
                    var shape = tool.GetComponentInChildren<SphereCollider>(); shape.enabled = true; shape.isTrigger = true; shape.transform.localPosition = Vector3.zero;
                    tool.GetComponentInChildren<InstrumentTipContact>().enabled = true;
                }
                Enable(Knife); Physics.SyncTransforms();
            }
            public void MoveKnife(Vector3 arteryMeters) => Knife.transform.SetPositionAndRotation(Artery.transform.TransformPoint(arteryMeters / UnitScale), Artery.transform.rotation);
            public void Tick() { Physics.SyncTransforms(); Volume.Simulate(Step); Vessel.Simulate(Step); }
            public void Sweep() { MoveKnife(Vector3.back * .009f); Tick(); MoveKnife(Vector3.forward * .009f); Tick(); }
            public void Injure() { Sweep(); Assert(Vessel.Fluid.InjuryAreaSquareMeters > 0, "fixture injury requires actual finite producer sweep"); }
            public void AtInjury(InstrumentBehaviour tool)
            { tool.transform.SetPositionAndRotation(Artery.transform.TransformPoint(Field<Vector3>(Vessel, "injuryPoint") / UnitScale), Artery.transform.rotation); Physics.SyncTransforms(); }
            public void Dispose()
            {
                // Explicit Editor resource disposal; this is not native OnDestroy timing proof.
                Volume.BladeSwept -= CountSweep;
                var generatedMesh = Volume.Wall ? Volume.Wall.Surface : null;
                var generatedMaterials = Volume.Wall ? Volume.Wall.GetComponent<Renderer>().sharedMaterials : Array.Empty<Material>();
                var poolMaterial = Pool ? Pool.GetComponent<Renderer>().sharedMaterial : null;
                UnityEngine.Object.DestroyImmediate(Root); UnityEngine.Object.DestroyImmediate(RigObject);
                if (generatedMesh) UnityEngine.Object.DestroyImmediate(generatedMesh);
                foreach (var material in generatedMaterials) if (material) UnityEngine.Object.DestroyImmediate(material);
                if (poolMaterial) UnityEngine.Object.DestroyImmediate(poolMaterial);
                if (SourceMesh) UnityEngine.Object.DestroyImmediate(SourceMesh);
            }
        }

        static void NoInjury(Fixture f, string reason)
        { Assert(f.Vessel.Fluid.InjuryAreaSquareMeters == 0 && f.Vessel.Fluid.CumulativeLossMilliliters == 0 && !f.Pool.activeSelf, reason); }
        static void Frozen(Fixture f, double lost, double pool, string reason)
        { Assert(f.Vessel.Fluid.CumulativeLossMilliliters == lost && f.Vessel.Fluid.PooledMilliliters == pool && !f.Pool.activeSelf, reason + " freezes ledger without resetting or showing pool"); }
        static void NotOccluded(Fixture f, string reason)
        { Assert(!f.Vessel.Fluid.IsOccluded && f.Vessel.Fluid.FlowMillilitersPerSecond > 0, reason); }
        static void Enable(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
        static void Ledger(VesselBleeding fluid)
        {
            Assert(Near(fluid.InitialSourceMilliliters, fluid.RemainingSourceMilliliters + fluid.CumulativeLossMilliliters), "runtime source/loss conservation");
            Assert(Near(fluid.CumulativeLossMilliliters, fluid.PooledMilliliters + fluid.RemovedMilliliters), "runtime pool/removal conservation");
        }
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
        static int ConsumerSubscriptions(NativeVolumeSimulation volume, NativeVesselSimulation vessel)
        {
            var callbacks = Field<Action<Vector3, Vector3, Vector3>>(volume, "BladeSwept");
            return callbacks == null ? 0 : callbacks.GetInvocationList().Count(callback => ReferenceEquals(callback.Target, vessel));
        }
        static Mesh BoxMesh(float importScale)
        {
            var mesh = new Mesh { name = "SyntheticReadableArteryBox" }; var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) == 0 ? -.004f : .004f,
                (i & 2) == 0 ? -.01f : .01f, (i & 4) == 0 ? -.004f : .004f) * importScale;
            mesh.vertices = vertices; mesh.triangles = new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4, 2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        static void AssertVertices(Vector3[] expected, Vector3[] actual, string reason)
        {
            Assert(expected.Length == actual.Length, reason + " vertex count");
            for (int i = 0; i < expected.Length; i++) Assert((expected[i] - actual[i]).sqrMagnitude < 1e-14f, reason + " vertex " + i);
        }
        static bool finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-8 + 1e-5 * Math.Max(Math.Abs(a), Math.Abs(b));
        static void Assert(bool condition, string reason)
        { checks++; if (!condition) throw new InvalidOperationException("Native vessel runtime: " + reason); }
    }
}
