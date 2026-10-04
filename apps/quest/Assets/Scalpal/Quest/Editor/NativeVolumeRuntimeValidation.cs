using System;
using System.Linq;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Public runtime boundary fixtures. These do not establish headset or clinical fidelity.
    public static class NativeVolumeRuntimeValidation
    {
        const float Step = 1f / 90f;
        const float CenterY = 1.017768f;
        static int checks;
        static readonly Vector3 BladeStart = new Vector3(-.014f, CenterY, -.118f);
        static readonly Vector3 BladeEnd = new Vector3(-.014f, CenterY, -.095f);

        [MenuItem("Scalpal/Quest/Validate Native Volume Runtime")]
        public static void Run()
        {
            checks = 0;
            GatesAndRetry();
            InvalidTools();
            Discontinuities();
            AuthoredScalpel();
            Debug.Log("SCALPAL_NATIVE_VOLUME_RUNTIME_VALIDATION_OK checks=" + checks + " synthetic Editor interaction fixtures; no headset or clinical validation");
        }

        sealed class Fixture : IDisposable
        {
            public readonly GameObject root, rigObject;
            public readonly NativeWorkbench rig;
            public readonly NativeVolumeSimulation simulation;
            public readonly Transform frame;
            public readonly InstrumentBehaviour blade, grasper;
            public readonly Transform start, end;
            public bool ready;
            public int events;
            public VolumetricTissue Wall => simulation.Wall;
            public TissueVolume Volume => Wall.Volume;

            public Fixture(string anchors = "owned", GameObject prefab = null)
            {
                root = new GameObject("SyntheticVolumeRuntimeValidation");
                rigObject = new GameObject("SyntheticInactiveVolumeRig");
                rigObject.SetActive(false); // Never initialize XR; session readiness is explicit.
                rig = rigObject.AddComponent<NativeWorkbench>();
                frame = new GameObject("RegisteredSourceFrame").transform;
                frame.SetParent(root.transform, false);
                frame.SetPositionAndRotation(new Vector3(.3f, -.2f, .6f), Quaternion.Euler(9, 23, -7));
                frame.localScale = Vector3.one * .8f;
                var toolObject = prefab ? UnityEngine.Object.Instantiate(prefab, root.transform) : new GameObject("SyntheticBlade");
                toolObject.transform.SetParent(root.transform, false);
                blade = toolObject.GetComponent<InstrumentBehaviour>() ?? toolObject.AddComponent<InstrumentBehaviour>();
                blade.instrumentId = prefab ? blade.instrumentId : "synthetic_volume_blade";
                if (!prefab) blade.action = InstrumentAction.Cut;
                if (prefab)
                {
                    start = OwnedAnchor(blade, "CutStart");
                    end = OwnedAnchor(blade, "CutEnd");
                }
                else
                {
                    Transform parent = blade.transform;
                    if (anchors == "nested")
                    {
                        parent = new GameObject("OtherInstrumentOwner").transform;
                        parent.SetParent(blade.transform, false);
                        parent.gameObject.AddComponent<InstrumentBehaviour>();
                    }
                    start = Anchor(parent, anchors == "missing" ? "NotCutStart" : "CutStart");
                    end = Anchor(parent, "CutEnd");
                    if (anchors == "duplicate") Anchor(parent, "CutStart");
                }
                blade.actionPoint = end;
                blade.ActionApplied += _ => events++;
                var graspObject = new GameObject("SyntheticSurfaceGrasper");
                graspObject.transform.SetParent(root.transform, false);
                grasper = graspObject.AddComponent<InstrumentBehaviour>();
                grasper.action = InstrumentAction.Grasp;
                grasper.instrumentId = "synthetic_volume_grasper";
                grasper.actionPoint = grasper.transform;
                grasper.ActionApplied += _ => events++;
                rig.tools = new[] { blade, grasper };
                rig.targets = Array.Empty<TrainingTarget>();
                simulation = root.AddComponent<NativeVolumeSimulation>();
                simulation.Initialize(frame, rig, () => ready);
                if (!prefab) Pose(BladeStart, BladeEnd);
                Enable(blade);
            }

            static Transform Anchor(Transform parent, string name)
            {
                var anchor = new GameObject(name).transform;
                anchor.SetParent(parent, false);
                return anchor;
            }
            public void Pose(Vector3 a, Vector3 b)
            {
                start.position = frame.TransformPoint(a);
                end.position = frame.TransformPoint(b);
            }
            public void MoveBlade(float x) => Pose(BladeStart + Vector3.right * x, BladeEnd + Vector3.right * x);
            public void Tick(float seconds = Step) => simulation.Simulate(seconds);
            public void Cut()
            {
                ready = true;
                MoveBlade(0); Tick();
                MoveBlade(.028f); Tick();
                Wall.CommitSurface();
            }
            public void Dispose()
            {
                // Editor fixture disposal avoids invoking player-delayed destruction on live assets.
                if (Wall)
                {
                    var surface = Wall.Surface;
                    var materials = Wall.GetComponent<MeshRenderer>().sharedMaterials;
                    if (surface) UnityEngine.Object.DestroyImmediate(surface);
                    foreach (var material in materials) if (material) UnityEngine.Object.DestroyImmediate(material);
                    UnityEngine.Object.DestroyImmediate(Wall.gameObject);
                }
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }

        sealed class State
        {
            public readonly int nodes, cutFaces;
            public readonly float mass, referenceVolume;
            public readonly bool[] cuts;
            public readonly int[] binding;
            public readonly Vector3[] positions;
            public State(TissueVolume volume)
            {
                nodes = volume.NodeCount; cutFaces = volume.CutFaceCount;
                mass = volume.TotalMass; referenceVolume = volume.ReferenceVolume;
                cuts = volume.Faces.Select(face => face.cut).ToArray();
                binding = Enumerable.Range(0, volume.Cells.Length * 4).Select(i => volume.NodeFor(i / 4, i % 4)).ToArray();
                positions = (Vector3[])volume.Positions.Clone();
            }
            public void AssertTopology(TissueVolume volume, string reason)
            {
                Assert(volume.NodeCount == nodes && volume.CutFaceCount == cutFaces, reason + ": topology counts");
                Assert(Near(volume.TotalMass, mass) && Near(volume.ReferenceVolume, referenceVolume), reason + ": reference mass/volume");
                Assert(cuts.SequenceEqual(volume.Faces.Select(face => face.cut)), reason + ": fracture flags");
                Assert(binding.SequenceEqual(Enumerable.Range(0, binding.Length).Select(i => volume.NodeFor(i / 4, i % 4))), reason + ": cell bindings");
            }
        }

        static void GatesAndRetry()
        {
            using (var f = new Fixture())
            {
                var initial = new State(f.Volume);
                var vertices = f.Wall.Surface.vertices;
                var triangles = f.Wall.Surface.triangles;
                Assert(f.Wall.transform.parent == f.frame && f.Wall.transform.localPosition == Vector3.zero, "generated wall uses registered source frame");
                Visibility(f, false, "initial state");
                f.Tick(); Visibility(f, false, "invalid session gate"); initial.AssertTopology(f.Volume, "initial invalid gate");
                f.ready = true; f.Tick(); Visibility(f, true, "valid session gate");
                foreach (float seconds in new[] { 0f, -.1f, float.NaN, float.PositiveInfinity })
                {
                    f.Tick(seconds); Visibility(f, false, "invalid elapsed time"); initial.AssertTopology(f.Volume, "invalid elapsed time");
                }
                f.Cut();
                Assert(f.Volume.CutFaceCount > 0, "owned finite CutStart/CutEnd sweep fractures the wall");
                Assert(f.Wall.Surface.triangles.Length == triangles.Length + 6 * f.Volume.CutFaceCount, "each fracture exposes two generated interior triangles");
                Assert(f.Volume.NodeCount > initial.nodes, "incision creates independently bound cut-side nodes");
                Assert(Near(f.Volume.TotalMass, initial.mass) && Near(f.Volume.ReferenceVolume, initial.referenceVolume), "blade sweep conserves reference volume and mass");
                var firstCut = new State(f.Volume);
                f.Cut(); firstCut.AssertTopology(f.Volume, "repeated blade traversal cannot duplicate an existing incision");
                foreach (var face in f.Volume.Faces.Where(face => face.cut))
                {
                    var centroid = (f.Volume.Original[face.a] + f.Volume.Original[face.b] + f.Volume.Original[face.c]) / 3;
                    Assert(Mathf.Abs(centroid.x) < .035f && Mathf.Abs(centroid.y - CenterY) < .025f, "fracture remains near finite blade footprint");
                }
                f.blade.SetHeld(false);
                f.grasper.transform.position = f.frame.TransformPoint(new Vector3(.04f, CenterY, -.115287f));
                Enable(f.grasper); f.Tick();
                Assert(f.Volume.Handle >= 0, "owned active grasper acquires generated boundary surface");
                f.grasper.transform.position += f.frame.TransformVector(Vector3.left * .002f);
                for (int i = 0; i < 5; i++) f.Tick();
                var cut = new State(f.Volume);
                f.ready = false; f.Tick();
                Visibility(f, false, "gate loss after incision");
                Assert(f.Wall.Surface.triangles.Length == triangles.Length + 6 * f.Volume.CutFaceCount, "hidden wall retains generated interior faces");
                Assert(f.Volume.Handle < 0, "gate loss releases volume handle");
                cut.AssertTopology(f.Volume, "gate loss preserves incision");
                AssertPositions(cut.positions, f.Volume.Positions, "gate loss freezes current deformed tissue");
                f.grasper.SetHeld(false); f.ready = true; f.Tick();
                Visibility(f, true, "reacquired gate"); cut.AssertTopology(f.Volume, "gate reacquisition preserves incision");
                f.simulation.enabled = false; f.Tick();
                Visibility(f, false, "disabled simulation"); cut.AssertTopology(f.Volume, "disabled simulation retains incision");
                f.simulation.ResetTissues();
                initial.AssertTopology(f.Volume, "retry restores intact topology");
                AssertPositions(vertices, f.Wall.Surface.vertices, "retry restores rendered exterior");
                Assert(triangles.SequenceEqual(f.Wall.Surface.triangles), "retry restores exterior triangle inventory");
                Assert(f.Volume.Handle < 0, "retry clears handle");
                f.simulation.enabled = true; Enable(f.blade); f.Tick();
                Assert(f.Volume.CutFaceCount == 0, "first pose after retry cannot cut from stale blade history");
                AssertPositions(f.Volume.Rest, f.Volume.Positions, "retry removes residual velocity before the next solver step");
                AssertUnscored(f);
            }
        }

        static void InvalidTools()
        {
            foreach (var anchors in new[] { "missing", "duplicate", "nested" })
                using (var f = new Fixture(anchors))
                {
                    f.Cut(); Assert(f.Volume.CutFaceCount == 0, "reject " + anchors + " blade anchors"); AssertUnscored(f);
                }
            using (var f = new Fixture())
            {
                f.ready = true;
                Action<string, Action> reject = (reason, invalidate) =>
                {
                    f.simulation.ResetTissues(); Enable(f.blade); f.blade.enabled = true;
                    f.rig.tools = new[] { f.blade, f.grasper }; f.blade.action = InstrumentAction.Cut;
                    f.MoveBlade(0); f.Tick(); invalidate(); f.MoveBlade(.028f); f.Tick();
                    Assert(f.Volume.CutFaceCount == 0, reason);
                };
                reject("unheld blade cannot cut", () => f.blade.SetHeld(false));
                reject("untracked blade cannot cut", () => f.blade.SetTrackingValid(false));
                reject("subthreshold activation cannot cut", () => f.blade.SetActivation(.69f));
                reject("inactive blade component cannot cut", () => f.blade.enabled = false);
                reject("inactive blade object cannot cut", () => f.blade.gameObject.SetActive(false));
                f.blade.gameObject.SetActive(true);
                reject("removed workbench tool cannot cut", () => f.rig.tools = new[] { f.grasper });
                reject("changed non-cut action cannot cut", () => f.blade.action = InstrumentAction.None);
                f.simulation.ResetTissues(); Enable(f.blade); f.blade.action = InstrumentAction.Cut;
                f.rig.tools = new[] { f.blade, f.grasper }; f.MoveBlade(0); f.Tick();
                f.Pose(BladeStart + Vector3.right * .028f, BladeStart + Vector3.right * .028f); f.Tick();
                Assert(f.Volume.CutFaceCount == 0, "collapsed blade endpoints reject sweep");
                f.simulation.ResetTissues(); f.MoveBlade(0); f.Tick();
                f.Pose(BladeStart + Vector3.right * .028f, BladeStart + Vector3.right * .028f + Vector3.forward * .061f); f.Tick();
                Assert(f.Volume.CutFaceCount == 0, "overlong blade rejects sweep");
                f.simulation.ResetTissues(); f.MoveBlade(0); f.Tick(); f.MoveBlade(.08f); f.Tick();
                Assert(f.Volume.CutFaceCount == 0, "large pose discontinuity cannot bridge a cut");
                f.simulation.ResetTissues(); f.MoveBlade(.3f); f.Tick(); f.MoveBlade(.328f); f.Tick();
                Assert(f.Volume.CutFaceCount == 0, "distant finite blade sweep cannot fracture an infinite plane through the wall");
                AssertUnscored(f);
            }
        }

        static void Discontinuities()
        {
            foreach (string loss in new[] { "session", "tracking", "activation", "held", "origin" })
                using (var f = new Fixture())
                {
                    f.ready = true; f.MoveBlade(0); f.Tick();
                    if (loss == "session") f.ready = false;
                    if (loss == "tracking") f.blade.SetTrackingValid(false);
                    if (loss == "activation") f.blade.SetActivation(0);
                    if (loss == "held") f.blade.SetHeld(false);
                    if (loss != "origin") f.Tick();
                    else f.frame.position += Vector3.right * .006f;
                    f.MoveBlade(.028f); f.ready = true; Enable(f.blade); f.Tick();
                    Assert(f.Volume.CutFaceCount == 0, loss + " reacquisition never sweeps across lost history");
                    f.MoveBlade(.024f); f.Tick();
                    Assert(f.Volume.CutFaceCount > 0, loss + " permits a new finite sweep after reacquisition");
                    AssertUnscored(f);
                }
        }

        static void AuthoredScalpel()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Scalpal/Instruments/Prefabs/inst_scalpel.prefab");
            Assert(prefab, "actual scalpel prefab exists");
            using (var f = new Fixture(prefab: prefab))
            {
                Assert(f.blade.instrumentId == "scalpel" && f.blade.action == InstrumentAction.Cut, "actual scalpel action identity");
                float length = Vector3.Distance(f.start.position, f.end.position);
                Assert(length >= .002f && length <= .06f, "actual exported blade anchors have supported metric length");
                var localStart = f.blade.transform.InverseTransformPoint(f.start.position);
                var localEnd = f.blade.transform.InverseTransformPoint(f.end.position);
                f.blade.transform.rotation = f.frame.rotation * Quaternion.FromToRotation(localEnd - localStart, Vector3.forward);
                f.blade.transform.position = f.frame.TransformPoint(BladeStart) - f.blade.transform.TransformVector(localStart);
                f.ready = true; f.Tick();
                f.blade.transform.position += f.frame.TransformVector(Vector3.right * .028f); f.Tick();
                Assert(f.Volume.CutFaceCount > 0, "actual authored scalpel blade anchors drive runtime fracture");
                AssertUnscored(f);
            }
        }

        static Transform OwnedAnchor(InstrumentBehaviour tool, string name)
        {
            var anchors = tool.GetComponentsInChildren<Transform>(true).Where(anchor => anchor.name == name && anchor.GetComponentInParent<InstrumentBehaviour>() == tool).ToArray();
            Assert(anchors.Length == 1, "actual tool owns exactly one " + name);
            return anchors[0];
        }
        static void Visibility(Fixture f, bool visible, string reason)
        {
            Assert(f.Wall.Visible == visible, reason + ": shared visibility gate");
            Assert(f.Wall.GetComponentsInChildren<Renderer>(true).All(renderer => renderer.enabled == visible), reason + ": every generated renderer");
            Assert(f.Wall.GetComponentsInChildren<Collider>(true).All(collider => collider.enabled == visible), reason + ": every generated collider");
            Assert(f.Wall.GetComponent<MeshFilter>().sharedMesh == f.Wall.Surface && f.Wall.GetComponent<MeshCollider>().sharedMesh == f.Wall.Surface, reason + ": common render/contact surface");
        }
        static void AssertUnscored(Fixture f)
        {
            Assert(f.root.GetComponentsInChildren<TrainingTarget>(true).Length == 0 && f.rig.targets.Length == 0, "generated wall adds no TrainingTarget or scored target");
            Physics.SyncTransforms(); f.blade.SimulateStep(Step); f.grasper.SimulateStep(Step);
            Assert(f.events == 0, "volume mechanics and generic contact emit no duplicate scored action events");
        }
        static void Enable(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
        static bool Near(float a, float b) => Mathf.Abs(a - b) <= Mathf.Max(1e-9f, Mathf.Abs(b) * .0001f);
        static void AssertPositions(Vector3[] expected, Vector3[] actual, string reason)
        {
            Assert(expected.Length == actual.Length, reason + ": vertex count");
            for (int i = 0; i < expected.Length; i++) Assert((expected[i] - actual[i]).sqrMagnitude < 1e-12f, reason + ": vertex " + i);
        }
        static void Assert(bool valid, string reason)
        {
            checks++;
            if (!valid) throw new InvalidOperationException("Native volume runtime validation failed: " + reason);
        }
    }
}
