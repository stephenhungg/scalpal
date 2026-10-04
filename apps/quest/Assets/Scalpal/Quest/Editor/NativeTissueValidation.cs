using System;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic Editor mechanics/contact fixtures; no participant fit, tissue calibration,
    // surgical fidelity, Android frame-time or physical headset evidence is asserted here.
    public static class NativeTissueValidation
    {
        static int checks;
        const float Step = 1f / 90f;
        [MenuItem("Scalpal/Quest/Validate Native Tissue")]
        public static void Run()
        {
            checks = 0;
            CageChecks();
            SurfaceAndRoutingChecks();
            NativeSourceAssetChecks();
            Debug.Log("SCALPAL_NATIVE_TISSUE_VALIDATION_OK checks=" + checks + " synthetic Editor fixtures; no clinical or headset validation");
        }

        static void CageChecks()
        {
            var bounds = new Bounds(Vector3.zero, new Vector3(.06f, .05f, .04f));
            foreach (var preset in new[] { TissuePreset.Bowel, TissuePreset.Mesentery, TissuePreset.Artery })
            {
                var cage = new TissueCage(bounds, preset);
                for (int i = 0; i < 8; i++) Assert(Near(cage.Positions[i], cage.Rest[i]), "cage rests at original corners");
                Assert(Near(cage.Deform(new Vector3(.006f, -.007f, -.009f)), new Vector3(.006f, -.007f, -.009f)), "interior embedding preserves rest geometry");
                Assert(Mathf.Abs(cage.VolumeRatio - 1) < .00001f, "rest volume");
                Assert(!cage.BeginHandle(new Vector3(float.NaN, 0, 0)) && cage.Handle < 0, "nonfinite grasp rejected");
                Assert(cage.BeginHandle(cage.Rest[0]) && cage.Handle < 4, "handle uses movable anterior face");
                for (int frame = 0; frame < 90; frame++) cage.Step(Step, cage.Rest[0] + Vector3.left * .006f);
                float pulled = cage.MaxDisplacement;
                Assert(pulled > .0001f, "valid pull deforms cage");
                CheckBounded(cage, preset.maxDisplacement);
                cage.ReleaseHandle();
                Assert(cage.Handle < 0, "release clears handle");
                for (int frame = 0; frame < 900; frame++) cage.Step(Step, Vector3.zero);
                Assert(cage.MaxDisplacement < pulled && cage.MaxDisplacement < .001f, "released tissue settles toward attachments");
                Assert(cage.BeginHandle(cage.Rest[0]), "reacquire after release");
                cage.Step(10, Vector3.one * 10000);
                CheckBounded(cage, preset.maxDisplacement);
                cage.Step(Step, new Vector3(float.PositiveInfinity, 0, 0));
                Assert(cage.Handle < 0, "invalid target cancels handle");
                cage.Positions[0] = new Vector3(float.NaN, 0, 0);
                cage.Step(Step, Vector3.zero);
                Assert(cage.MaxDisplacement == 0 && cage.Handle < 0, "nonfinite solver state resets");
                CheckRest(cage);
                cage.Reset(); CheckRest(cage);
            }
            // An extreme authored pull may be projected into a valid shape or reset.
            // The required outcome is preserved orientation, not a particular fallback.
            var inverted = new TissueCage(bounds, new TissuePreset {
                stretchCompliance = 1000, dampingPerSecond = 0, returnPerSecond = 0, maxDisplacement = .5f
            });
            inverted.BeginHandle(inverted.Rest[0]);
            inverted.Step(Step, inverted.Rest[0] + Vector3.forward * .3f);
            CheckBounded(inverted, .5f);
            bool rejected = false;
            try { new TissueCage(new Bounds(Vector3.zero, new Vector3(1, 1, 0)), TissuePreset.Bowel); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "flat volume rejected");
        }

        static void CheckRest(TissueCage cage)
        {
            for (int i = 0; i < 8; i++) Assert(Near(cage.Positions[i], cage.Rest[i]), "reset restores every corner");
            Assert(Mathf.Abs(cage.VolumeRatio - 1) < .00001f, "reset restores volume");
        }
        static void CheckBounded(TissueCage cage, float limit)
        {
            Assert(TissueCage.Finite(new Vector3(cage.VolumeRatio, cage.MaxDisplacement, 0)) && cage.VolumeRatio > 0, "finite positive cage volume");
            Assert(cage.MaxDisplacement <= limit + .00001f, "displacement cap");
            for (int i = 0; i < 8; i++)
            {
                Assert(TissueCage.Finite(cage.Positions[i]), "finite solved corner");
                if ((i & 4) != 0) Assert(Near(cage.Positions[i], cage.Rest[i]), "posterior attachment remains pinned");
            }
            int[,] tets = { {0,1,2,4}, {1,2,3,7}, {1,4,5,7}, {2,4,6,7}, {1,2,4,7} };
            for (int t = 0; t < 5; t++)
                Assert(Volume(cage.Positions, tets, t) * Volume(cage.Rest, tets, t) > 0, "cell preserves signed orientation");
        }
        static float Volume(Vector3[] p, int[,] t, int row) => Vector3.Dot(p[t[row,1]] - p[t[row,0]],
            Vector3.Cross(p[t[row,2]] - p[t[row,0]], p[t[row,3]] - p[t[row,0]])) / 6;

        static void SurfaceAndRoutingChecks()
        {
            var root = new GameObject("SyntheticTissueValidation");
            var rigObject = new GameObject("SyntheticInactiveTissueWorkbench");
            rigObject.SetActive(false); // Prevent XR initialization; readiness is injected explicitly.
            Mesh source = null;
            try
            {
                var rig = rigObject.AddComponent<NativeWorkbench>();
                var obj = new GameObject("SyntheticAppendix"); obj.transform.SetParent(root.transform, false);
                source = BoxMesh(); var original = source.vertices;
                var filter = obj.AddComponent<MeshFilter>(); filter.sharedMesh = source;
                var renderer = obj.AddComponent<MeshRenderer>();
                var collider = obj.AddComponent<MeshCollider>(); collider.sharedMesh = source;
                var part = obj.AddComponent<AnatomyPart>(); part.stableId = "appendix";
                var anatomy = root.AddComponent<AnatomyController>(); anatomy.initiallyHiddenSystems = Array.Empty<string>();
                anatomy.RebuildIndex(); anatomy.SetRegistrationValid(true);
                var instrumentObject = new GameObject("SyntheticForceps"); instrumentObject.transform.SetParent(root.transform, false);
                var instrument = instrumentObject.AddComponent<InstrumentBehaviour>();
                instrument.instrumentId = "synthetic_forceps"; instrument.action = InstrumentAction.Grasp; instrument.contactRadius = .012f;
                var tipObject = new GameObject("SyntheticTip"); tipObject.transform.SetParent(instrumentObject.transform, false);
                var sphere = tipObject.AddComponent<SphereCollider>(); sphere.radius = .01f; sphere.isTrigger = true;
                var tip = tipObject.AddComponent<InstrumentTipContact>(); instrument.actionPoint = tipObject.transform;
                instrumentObject.transform.position = new Vector3(0, 0, -.02f);
                rig.tools = new[] { instrument };
                var simulation = root.AddComponent<NativeTissueSimulation>();
                bool ready = true;
                simulation.Initialize(anatomy, rig, () => ready);
                var tissue = obj.GetComponent<DeformableTissue>();
                Assert(simulation.TissueCount == 1 && tissue && tissue.Cage != null, "bind only available authored tissue ID");
                Assert(filter.sharedMesh != source && filter.sharedMesh == collider.sharedMesh, "render and contact share private runtime mesh");
                AssertVertices(original, filter.sharedMesh.vertices, "rest source geometry preserved");
                EnableInstrument(instrument); Physics.SyncTransforms();
                Assert(Physics.ComputePenetration(sphere, sphere.transform.position, sphere.transform.rotation,
                    collider, collider.transform.position, collider.transform.rotation, out _, out _), "fixture has actual tip penetration");

                ready = false; AssertNoGrasp(simulation, tissue, "session readiness gate"); ready = true;
                anatomy.SetRegistrationValid(false); AssertNoGrasp(simulation, tissue, "registration gate"); anatomy.SetRegistrationValid(true);
                instrument.SetHeld(false); AssertNoGrasp(simulation, tissue, "held gate"); EnableInstrument(instrument);
                instrument.SetTrackingValid(false); AssertNoGrasp(simulation, tissue, "tracking gate"); EnableInstrument(instrument);
                instrument.SetActivation(.69f); AssertNoGrasp(simulation, tissue, "activation threshold gate"); EnableInstrument(instrument);
                instrument.action = InstrumentAction.Cut; AssertNoGrasp(simulation, tissue, "cut does not masquerade as grasp"); instrument.action = InstrumentAction.Grasp;
                sphere.isTrigger = false; AssertNoGrasp(simulation, tissue, "tip must be trigger"); sphere.isTrigger = true;
                sphere.enabled = false; AssertNoGrasp(simulation, tissue, "disabled tip collider rejected"); sphere.enabled = true;
                tip.enabled = false; AssertNoGrasp(simulation, tissue, "disabled contact component rejected"); tip.enabled = true;
                renderer.enabled = false; AssertNoGrasp(simulation, tissue, "hidden geometry rejected"); renderer.enabled = true;
                collider.enabled = false; AssertNoGrasp(simulation, tissue, "disabled tissue contact rejected"); collider.enabled = true;
                instrument.actionPoint = instrumentObject.transform;
                tipObject.transform.localPosition = Vector3.right * .025f;
                AssertNoGrasp(simulation, tissue, "tip outside declared action radius rejected");
                tipObject.transform.localPosition = Vector3.zero; instrument.actionPoint = tipObject.transform;
                instrumentObject.transform.position = new Vector3(1, 0, 0); AssertNoGrasp(simulation, tissue, "no penetration means no grasp");
                instrumentObject.transform.position = new Vector3(0, 0, -.02f); Physics.SyncTransforms();
                simulation.Simulate(Step); Assert(tissue.Cage.Handle >= 0, "valid real instrument overlap acquires handle");
                Assert(tissue.Cage.MaxDisplacement < .00001f, "acquisition does not snap tissue to cage corner");
                instrumentObject.transform.position += Vector3.left * .005f; Physics.SyncTransforms();
                for (int i = 0; i < 12; i++) simulation.Simulate(Step);
                Assert(tissue.Cage.MaxDisplacement > .0001f, "held tracked tool drives deformation");
                Assert(filter.sharedMesh == collider.sharedMesh, "deformed renderer and scoring collider agree");
                bool changed = false; var displaced = filter.sharedMesh.vertices;
                for (int i = 0; i < original.Length; i++) changed |= !Near(original[i], displaced[i]);
                Assert(changed, "surface commits actual changed vertices");
                AssertVertices(original, source.vertices, "shared source mesh never mutated");
                instrument.SetTrackingValid(false); simulation.Simulate(Step);
                Assert(tissue.Cage.Handle < 0, "tracking loss releases current grasp");
                EnableInstrument(instrument); simulation.ResetTissues();
                AssertVertices(original, filter.sharedMesh.vertices, "retry reset restores rendered surface");
                Assert(filter.sharedMesh == collider.sharedMesh && tissue.Cage.Handle < 0, "retry reset restores contact and handle");
                instrumentObject.transform.position = new Vector3(0, 0, -.02f); Physics.SyncTransforms();
                simulation.Simulate(Step); instrumentObject.transform.position += Vector3.left * .005f;
                for (int i = 0; i < 12; i++) simulation.Simulate(Step);
                ready = false; simulation.Simulate(Step);
                Assert(tissue.Cage.Handle < 0 && tissue.Cage.MaxDisplacement == 0, "readiness loss resets active deformation");
                AssertVertices(original, filter.sharedMesh.vertices, "readiness loss commits original surface");
                ready = true; simulation.enabled = false;
                AssertNoGrasp(simulation, tissue, "disabled simulation gate");
                // Explicit Editor disposal checks cleanup, not native OnDestroy scheduling.
                tissue.RestoreSource();
                Assert(filter.sharedMesh == source && collider.sharedMesh == source, "explicit Editor disposal restores shared source references");
                tissue.RestoreSource();
                Assert(filter.sharedMesh == source && collider.sharedMesh == source, "explicit Editor disposal is idempotent");
                UnityEngine.Object.DestroyImmediate(tissue);
                Assert(filter.sharedMesh == source && collider.sharedMesh == source, "Editor component removal retains restored source references");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(rigObject);
                if (source) UnityEngine.Object.DestroyImmediate(source);
            }
        }


        static void NativeSourceAssetChecks()
        {
            var session = UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Assert(session && session.anatomy && session.preview, "actual native scene has practice and preview bindings");
            var root = new GameObject("SyntheticNativeAssetTissueValidation");
            GameObject previewClone = null;
            try
            {
                int triangles = 0;
                var ids = new[] { "appendix", "mesoappendix", "appendicular_artery" };
                var expectedTriangles = new[] { 442, 468, 2006 };
                var presets = new[] { TissuePreset.Bowel, TissuePreset.Mesentery, TissuePreset.Artery };
                for (int item = 0; item < ids.Length; item++)
                {
                    var id = ids[item];
                    var authored = session.anatomy.GetComponentsInChildren<AnatomyPart>(true).SingleOrDefault(part => part.stableId == id);
                    Assert(authored, "native scene source target exists: " + id);
                    var sourceFilter = authored.GetComponent<MeshFilter>();
                    var sourceCollider = authored.GetComponent<MeshCollider>();
                    Assert(sourceFilter && sourceCollider && sourceFilter.sharedMesh == sourceCollider.sharedMesh,
                        "native source mesh and collider correspond: " + id);
                    var source = sourceFilter.sharedMesh;
                    Assert(source && source.isReadable, "native tissue source is CPU-readable: " + id);
                    Assert(AssetDatabase.Contains(source), "native tissue uses imported source asset: " + id);
                    Assert(source.triangles.Length / 3 == expectedTriangles[item], "native target triangle inventory: " + id);
                    triangles += source.triangles.Length / 3;
                    var bounds = source.bounds;
                    Assert(TissueCage.Finite(bounds.center) && TissueCage.Finite(bounds.size)
                        && bounds.size.x > 0 && bounds.size.y > 0 && bounds.size.z > 0,
                        "native tissue has finite three-dimensional bounds: " + id);
                    var original = source.vertices;
                    var clone = UnityEngine.Object.Instantiate(authored.gameObject, root.transform);
                    var filter = clone.GetComponent<MeshFilter>(); var collider = clone.GetComponent<MeshCollider>();
                    var tissue = clone.GetComponent<DeformableTissue>() ?? clone.AddComponent<DeformableTissue>();
                    Assert(tissue.Initialize(presets[item]), "actual imported target accepts cage: " + id);
                    Assert(filter.sharedMesh != source && filter.sharedMesh == collider.sharedMesh,
                        "actual target gets one private render/contact mesh: " + id);
                    AssertVertices(original, filter.sharedMesh.vertices, "actual target preserves rest vertices: " + id);
                    Assert(tissue.Cage.BeginHandle(tissue.Cage.Rest[0]), "actual target permits anterior handle: " + id);
                    Vector3 target = tissue.Cage.Rest[0] + Vector3.left * Mathf.Min(.0005f, presets[item].maxDisplacement * .1f);
                    for (int frame = 0; frame < 30; frame++) tissue.Step(Step, target);
                    tissue.CommitSurface();
                    CheckBounded(tissue.Cage, presets[item].maxDisplacement);
                    Assert(tissue.Cage.MaxDisplacement > .00001f, "gentle pull deforms actual target: " + id);
                    Assert(filter.sharedMesh == collider.sharedMesh, "actual deformed contact matches render: " + id);
                    AssertVertices(original, source.vertices, "actual imported source remains immutable: " + id);
                    Assert(sourceFilter.sharedMesh == source && sourceCollider.sharedMesh == source,
                        "native scene source references remain unchanged: " + id);
                    tissue.ResetTissue();
                    AssertVertices(original, filter.sharedMesh.vertices, "actual target reset restores geometry: " + id);
                    // This calls cleanup explicitly; Editor removal is not a player lifecycle test.
                    tissue.RestoreSource();
                    Assert(filter.sharedMesh == source && collider.sharedMesh == source,
                        "actual target explicit Editor disposal restores source: " + id);
                    tissue.RestoreSource();
                    UnityEngine.Object.DestroyImmediate(tissue);
                    Assert(filter.sharedMesh == source && collider.sharedMesh == source,
                        "actual target Editor removal retains restored source: " + id);
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                Assert(triangles == 2916, "three real deformation targets total 2,916 triangles");

                previewClone = UnityEngine.Object.Instantiate(session.preview.gameObject, root.transform);
                var preview = previewClone.GetComponent<AnatomyController>();
                var layers = previewClone.GetComponent<AnatomyLayerView>();
                Assert(layers && preview, "native preview binds layered atlas presentation");
                Assert(previewClone.GetComponentsInChildren<Collider>(true).Length == 0,
                    "layer preview has no scored or physical contact colliders");
                preview.RebuildIndex(); preview.SetPreviewMode(true); preview.SetRegistrationValid(false);
                foreach (AnatomyLayerView.Layer layer in Enum.GetValues(typeof(AnatomyLayerView.Layer)))
                {
                    layers.Show(layer); int visibleCount = 0;
                    foreach (var part in preview.Parts)
                    {
                        bool expected = layer == AnatomyLayerView.Layer.Organs ? part.system != "surface" && part.system != "muscular" && part.system != "skeletal" :
                            layer == AnatomyLayerView.Layer.Surface ? part.system == "surface" :
                            layer == AnatomyLayerView.Layer.AbdominalWall ? part.system == "muscular" :
                            layer == AnatomyLayerView.Layer.Skeleton ? part.system == "skeletal" : part.system == "cardiovascular";
                        Assert(part.IsVisible == expected && part.HasVisibleGeometry == expected,
                            "actual preview layer visibility: " + layer + "/" + part.stableId);
                        if (expected) visibleCount++;
                    }
                    Assert(visibleCount > 0, "each native atlas layer contains geometry: " + layer);
                }
                preview.SetPreviewMode(false); preview.SetRegistrationValid(false);
                layers.Show(AnatomyLayerView.Layer.Surface);
                foreach (var part in preview.Parts)
                    Assert(!part.IsVisible && !part.HasVisibleGeometry, "layer switching cannot bypass invalid practice registration");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        static void EnableInstrument(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
        static void AssertNoGrasp(NativeTissueSimulation simulation, DeformableTissue tissue, string message)
        {
            simulation.ResetTissues(); Physics.SyncTransforms(); simulation.Simulate(Step);
            Assert(tissue.Cage.Handle < 0 && tissue.Cage.MaxDisplacement < .00001f, message);
        }
        static Mesh BoxMesh()
        {
            var mesh = new Mesh { name = "SyntheticClosedTissueBox" };
            var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) == 0 ? -.03f : .03f,
                (i & 2) == 0 ? -.025f : .025f, (i & 4) == 0 ? -.02f : .02f);
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0,2,1, 1,2,3, 4,5,6, 5,7,6, 0,1,4, 1,5,4,
                2,6,3, 3,6,7, 0,4,2, 2,4,6, 1,3,5, 3,7,5 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
        static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-12f;
        static void AssertVertices(Vector3[] expected, Vector3[] actual, string message)
        {
            Assert(expected.Length == actual.Length, message + " vertex count");
            for (int i = 0; i < expected.Length; i++) Assert(Near(expected[i], actual[i]), message + " vertex " + i);
        }
        static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Native tissue validation failed: " + message);
            checks++;
        }
    }
}
