using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy.Tissue;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Geometry/input component regressions. Actual Play Mode startup/trigger delivery is a separate gate.
    public static class NativeInteriorContactValidation
    {
        static int checks;
        [MenuItem("Scalpal/Quest/Validate Interior Tip Contact")]
        public static void Run()
        {
            checks = 0;
            var root = new GameObject("InteriorContactRegression");
            var inactive = new GameObject("InactiveHardwareFixture"); inactive.SetActive(false);
            Mesh mesh = null;
            try
            {
                var controller = root.AddComponent<AnatomyController>(); controller.initiallyHiddenSystems = Array.Empty<string>();
                var body = GameObject.CreatePrimitive(PrimitiveType.Cube); body.name = "anat_appendix";
                body.transform.SetParent(root.transform, false);
                UnityEngine.Object.DestroyImmediate(body.GetComponent<BoxCollider>());
                mesh = UnityEngine.Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh);
                body.GetComponent<MeshFilter>().sharedMesh = mesh;
                var shell = body.AddComponent<MeshCollider>(); shell.sharedMesh = mesh; shell.convex = false;
                var part = body.AddComponent<AnatomyPart>(); part.stableId = "appendix";
                var helper = new ClosedMeshInterior(); Physics.SyncTransforms();
                var point = new Vector3(.11f, .13f, -.07f);
                Assert(helper.Contains(shell, point), "closed cube interior is accepted");
                Assert(!helper.Contains(shell, new Vector3(2,0,0)), "exterior rejected");
                root.transform.SetPositionAndRotation(new Vector3(3,-2,4), Quaternion.Euler(35,71,19));
                root.transform.localScale = new Vector3(.23f,.37f,.19f); Physics.SyncTransforms();
                Assert(helper.Contains(shell, shell.transform.TransformPoint(point)), "rotated translated nonuniform frame");
                Assert(!helper.Contains(shell, point), "old world point is outside moved body");
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); root.transform.localScale = Vector3.one;
                var originalVertices = mesh.vertices; var originalTriangles = mesh.triangles;
                var moved = (Vector3[])originalVertices.Clone();
                for (int i=0;i<moved.Length;i++) moved[i] = moved[i] * .12f + Vector3.right * .3f;
                mesh.vertices = moved; mesh.RecalculateBounds(); shell.sharedMesh = null; shell.sharedMesh = mesh; Physics.SyncTransforms();
                Assert(!helper.Contains(shell, point), "same-mesh deformed old interior rejected");
                Assert(helper.Contains(shell, Vector3.right * .3f), "same-mesh current deformed interior accepted");
                mesh.vertices = originalVertices;
                var open = new int[originalTriangles.Length-3]; Array.Copy(originalTriangles, open, open.Length);
                mesh.triangles = open; mesh.RecalculateBounds();
                Assert(!helper.Contains(shell, point), "open shell fails closed after same-mesh topology edit");
                var reversed = (int[])originalTriangles.Clone();
                for(int i=0;i<reversed.Length;i+=3) { int a=reversed[i]; reversed[i]=reversed[i+1]; reversed[i+1]=a; }
                mesh.triangles = reversed;
                Assert(!helper.Contains(shell, point), "inward-wound shell rejected");
                var nonmanifold = new int[originalTriangles.Length+3]; Array.Copy(originalTriangles,nonmanifold,originalTriangles.Length);
                Array.Copy(originalTriangles,0,nonmanifold,originalTriangles.Length,3); mesh.triangles=nonmanifold;
                Assert(!helper.Contains(shell,point),"non-manifold duplicate face rejected");
                mesh.triangles = originalTriangles; mesh.RecalculateBounds(); shell.sharedMesh = null; shell.sharedMesh = mesh;
                Assert(helper.Contains(shell, point), "valid topology restoration recovers");
                var inverted = (Vector3[])originalVertices.Clone();
                for(int i=0;i<inverted.Length;i++) inverted[i].x = -inverted[i].x;
                mesh.vertices=inverted;
                Assert(!helper.Contains(shell,point),"same-topology face inversion rejected");
                mesh.vertices=originalVertices; mesh.RecalculateBounds();
                for(int i=0;i<8;i++) helper.Contains(shell, point);
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                int acceptedWarm = 0;
                for(int i=0;i<256;i++) if(helper.Contains(shell, point)) acceptedWarm++;
                long warmBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                Assert(acceptedWarm==256 && warmBytes==0,"warm current-mesh queries allocate zero managed bytes; observed="+warmBytes);
                shell.enabled = false; Assert(!helper.Contains(shell, point), "disabled shell rejected"); shell.enabled = true;

                var toolObject = new GameObject("scalpel"); toolObject.transform.SetParent(root.transform, false);
                var tool = toolObject.AddComponent<InstrumentBehaviour>(); tool.instrumentId = "scalpel";
                var tipObject = new GameObject("Tip"); tipObject.transform.SetParent(toolObject.transform, false);
                var tipCollider = tipObject.AddComponent<SphereCollider>(); tipCollider.radius = .012f; tipCollider.isTrigger = true;
                var contact = tipObject.AddComponent<InstrumentTipContact>();
                tool.actionPoint = tipObject.transform; tool.contactRadius = .012f;
                tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1);
                toolObject.transform.position = point;
                Physics.SyncTransforms();
                Assert(!Physics.ComputePenetration(tipCollider,tipObject.transform.position,tipObject.transform.rotation,
                    shell,shell.transform.position,shell.transform.rotation,out _,out _), "fixture truly misses hollow-shell PhysX contact");
                var workbench = inactive.AddComponent<NativeWorkbench>(); workbench.tools = new[] { tool };
                typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(workbench,new object[]{true});
                var exercise = root.AddComponent<AnatomyExerciseBinding>(); exercise.anatomy = controller;
                var candidate = new SurgicalCase { caseId="interior-fixture",patientId="synthetic",procedureId="interior",status="ready",
                    instruments=new[]{new Instrument{id="scalpel"}}, procedure=new Procedure {id="interior",firstStep="apply",structures=new[]{"appendix"},
                        steps=new[]{new ProcedureStep{id="apply",instrumentId="scalpel",targets=new[]{"appendix"},
                            check=new SuccessCheck{type="apply_count",targets=new[]{"appendix"},count=8}}}}};
                controller.RebuildIndex();
                Assert(exercise.SelectCase(new ScalpalBundle{cases=new[]{candidate}},candidate.caseId,false,out var reason), "fixture selection: "+reason);
                controller.SetRegistrationValid(true); bool practice=true;
                var input = root.AddComponent<NativeProcedureInput>(); input.Initialize(exercise,workbench,()=>practice);
                int events=0; exercise.EventHandled += (_,__)=>events++;
                practice=false; input.ProbeInteriorContacts(); Assert(events==0,"phase blocks interior score"); practice=true;
                controller.SetRegistrationValid(false); input.ProbeInteriorContacts(); Assert(events==0,"registration blocks interior score"); controller.SetRegistrationValid(true);
                tool.SetTrackingValid(false); input.ProbeInteriorContacts(); Assert(events==0,"tool tracking blocks interior score"); tool.SetTrackingValid(true); tool.SetActivation(1);
                part.SetVisible(false); input.ProbeInteriorContacts(); Assert(events==0,"hidden geometry blocks interior score"); part.SetVisible(true);
                toolObject.transform.position = Vector3.right*2; Physics.SyncTransforms();
                Assert(!contact.TryReportContact(shell), "rejected exterior callback not acknowledged");
                toolObject.transform.position=point; Physics.SyncTransforms();
                input.ProbeInteriorContacts(); Assert(events==1,"interior activation scores through existing adapter after rejected contact without rearming");
                input.ProbeInteriorContacts(); Assert(events==1,"continuous interior contact counts once");
                contact.ResetContactCycle(); Assert(!contact.TryReportContact(shell)&&events==1,"adapter also deduplicates accepted cycle");
                tool.SetActivation(0); Tick(input); tool.SetActivation(1);
                toolObject.transform.position=Vector3.right*2; Physics.SyncTransforms(); input.ProbeInteriorContacts(); Assert(events==1,"outside current mesh cannot score");
                toolObject.transform.position=point; Physics.SyncTransforms(); input.ProbeInteriorContacts(); Assert(events==2,"fresh interior activation scores once");
                tool.SetActivation(0); Tick(input); tool.SetActivation(1);
                var foreign = GameObject.CreatePrimitive(PrimitiveType.Cube); foreign.name="anat_appendix"; foreign.transform.SetParent(root.transform,false);
                Assert(!contact.TryReportContact(foreign.GetComponent<Collider>())&&events==2,"same-name foreign collider cannot score");
                ValidateImportedAnatomy();
                Debug.Log("SCALPAL_NATIVE_INTERIOR_VALIDATION_OK checks="+checks+" geometry/input fixtures; not headset or Play Mode startup evidence");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(inactive); if(mesh) UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static readonly string[] importedIds = {
            "appendicular_artery", "appendix", "cecum", "greater_omentum", "mesoappendix",
            "right_ureter", "small_bowel", "terminal_ileum", "urinary_bladder"
        };
        // Per-ID origins/counts from the committed anatomy-atlas catalog. Most practice
        // structures retain their original visceral/cardiovascular subassets.
        static readonly string[] importedOrigins = {
            "cardiovascular", "visceral", "exercise-targets", "visceral", "visceral",
            "visceral", "exercise-targets", "exercise-targets", "visceral"
        };
        static readonly int[] importedTriangles = { 2006, 442, 1216, 77968, 468, 3168, 4599, 496, 3036 };
        static readonly HashSet<string> approximationIds = new HashSet<string>(StringComparer.Ordinal) {
            "greater_omentum", "small_bowel", "right_ureter", "appendicular_artery"
        };

        static void ValidateImportedAnatomy()
        {
            var scene = SceneManager.GetSceneByPath(NativeSessionBuild.ScenePath);
            bool closeScene = !scene.IsValid() || !scene.isLoaded;
            if (closeScene) scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Additive);
            var fixture = new GameObject("ImportedInteriorContactRegression");
            try
            {
                var session = scene.GetRootGameObjects().SelectMany(item => item.GetComponentsInChildren<NativeCaseSession>(true)).Single();
                Assert(session.anatomy, "actual NativeSession has the practice anatomy binding");
                var parts = session.anatomy.GetComponentsInChildren<AnatomyPart>(true);
                Assert(parts.Length == importedIds.Length && parts.Select(item => item.stableId).OrderBy(id => id)
                    .SequenceEqual(importedIds.OrderBy(id => id)), "actual nine practice structures, not preview/cube substitutes");
                foreach (var id in importedIds)
                {
                    var source = parts.Single(item => item.stableId == id).GetComponentsInChildren<MeshCollider>(true).Single();
                    var tissueSource = source.GetComponent<DeformableTissue>();
                    var imported = tissueSource && tissueSource.SourceMesh ? tissueSource.SourceMesh : source.sharedMesh;
                    int assetIndex = Array.IndexOf(importedIds, id);
                    Assert(imported && imported.isReadable && AssetDatabase.GetAssetPath(imported)
                        == "Assets/Scalpal/Anatomy/Models/" + importedOrigins[assetIndex] + ".fbx",
                        id + " retains the catalog's committed readable imported source");
                    Assert(imported.triangles.Length / 3 == importedTriangles[assetIndex], id + " retains audited real-mesh triangle count");
                    // Copy only contact geometry/frame. Source visibility, registration, components and mesh assets remain untouched.
                    var clone = new GameObject("Interior_" + id); clone.transform.SetParent(fixture.transform, false);
                    clone.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                    clone.transform.localScale = source.transform.lossyScale;
                    clone.AddComponent<MeshFilter>().sharedMesh = imported;
                    var shell = clone.AddComponent<MeshCollider>(); shell.sharedMesh = imported; shell.convex = false;
                    Physics.SyncTransforms();
                    var vertices = imported.vertices; var triangles = imported.triangles;
                    Vector3 inside, outside;
                    try { FindIndependentPair(vertices, triangles, imported.bounds, out inside, out outside); }
                    catch (Exception error) { throw new InvalidOperationException(id + " independent fixture failed", error); }
                    using (var helper = new MeshTipInterior())
                    {
                        Vector3 worldInside = shell.transform.TransformPoint(inside), worldOutside = shell.transform.TransformPoint(outside);
                        bool accepted = helper.Contains(shell, worldInside, 10);
                        var strictDiagnostic = new ClosedMeshInterior(); strictDiagnostic.Contains(shell, worldInside);
                        Assert(accepted, id + " independent real-mesh interior accepted: local=" + inside.ToString("G9") +
                            " fallback=" + helper.UsesSurfaceApproximation + " eligible=" + strictDiagnostic.SurfaceFallbackEligible +
                            " queries=" + helper.LastTriangleTests + " bounds=" + imported.bounds);
                        Assert(!helper.Contains(shell, worldOutside, 10), id + " independent near-surface exterior rejected");
                        Assert(!helper.Contains(shell, shell.transform.TransformPoint(imported.bounds.max + imported.bounds.size), 10),
                            id + " bounds-exterior rejected");
                        Assert(!approximationIds.Contains(id) || helper.UsesSurfaceApproximation, id + " audited unsupported geometry uses fallback");
                        if (approximationIds.Contains(id))
                        {
                            Assert(!new ClosedMeshInterior().Contains(shell, worldInside), id + " strict helper reproduces audited no-contact failure");
                            Assert(helper.Contains(shell, worldInside, 11), id + " approximation warm query accepted");
                            Assert(helper.LastTriangleTests > 0 && helper.LastTriangleTests < triangles.Length / 3 / 2,
                                id + " BVH query tests fewer than half the triangles; observed=" + helper.LastTriangleTests);
                        }
                        int queries = helper.GeometryQueries;
                        Assert(helper.Contains(shell, worldInside, 12), id + " new-frame contact accepted");
                        Assert(helper.GeometryQueries == queries + 1, id + " new frame recomputes geometry");
                        queries = helper.GeometryQueries;
                        for (int repeat = 0; repeat < 16; repeat++) Assert(helper.Contains(shell, worldInside, 12), id + " cached repeated contact");
                        Assert(helper.GeometryQueries == queries, id + " repeated same-frame contact does not repeat geometry work");
                        Assert(!helper.Contains(shell, worldOutside, 12) && helper.GeometryQueries == queries + 1,
                            id + " changed point invalidates same-frame result");
                        shell.enabled = false;
                        Assert(!helper.Contains(shell, worldInside, 12), id + " disabled imported collider rejected"); shell.enabled = true;
                        Assert(helper.Contains(shell, worldInside, 13), id + " pre-transform contact accepted");
                        queries = helper.GeometryQueries;
                        fixture.transform.position = Vector3.one * 50; Physics.SyncTransforms();
                        Assert(!helper.Contains(shell, worldInside, 13) && helper.GeometryQueries == queries + 1,
                            id + " same point and frame invalidates on matrix change");
                        fixture.transform.SetPositionAndRotation(new Vector3(-3, 2, 4), Quaternion.Euler(31, 67, 12));
                        fixture.transform.localScale = new Vector3(.71f, 1.27f, .89f); Physics.SyncTransforms();
                        queries = helper.GeometryQueries;
                        Assert(helper.Contains(shell, shell.transform.TransformPoint(inside), 12), id + " transformed real-mesh interior accepted");
                        Assert(helper.GeometryQueries == queries + 1, id + " same-frame transform invalidates result");
                        Assert(!helper.Contains(shell, shell.transform.TransformPoint(outside), 12), id + " transformed real-mesh exterior rejected");
                        fixture.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity); fixture.transform.localScale = Vector3.one;
                        if (id == "appendicular_artery") ValidateRevision(shell, helper, inside);
                    }
                    UnityEngine.Object.DestroyImmediate(clone);
                }
                Debug.Log("SCALPAL_IMPORTED_INTERIOR_VALIDATION_OK structures=9 auditedFallbacks=4 independentRayFixtures=true sourceAssetsUntouched=true");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
                if (closeScene) EditorSceneManager.CloseScene(scene, true);
            }
        }

        static void ValidateRevision(MeshCollider shell, MeshTipInterior helper, Vector3 inside)
        {
            var tissue = shell.gameObject.AddComponent<DeformableTissue>();
            try
            {
                float units = Mathf.Min(shell.transform.lossyScale.x, Mathf.Min(shell.transform.lossyScale.y, shell.transform.lossyScale.z));
                Assert(tissue.Initialize(TissuePreset.Artery, units), "real artery deformation initializes");
                var world = shell.transform.TransformPoint(inside);
                Assert(helper.Contains(shell, world, 20), "dynamic artery uses matching displayed surface");
                int revision = tissue.SurfaceRevision, queries = helper.GeometryQueries;
                Assert(tissue.Cage.BeginHandle(tissue.ToMeters(inside)), "real artery handle available");
                tissue.Step(1f / 90f, tissue.Cage.HandlePosition + Vector3.right * .001f); tissue.CommitSurface();
                Assert(tissue.SurfaceRevision > revision && tissue.Cage.MaxDisplacement > 0, "real deformation commits a new surface revision");
                helper.Contains(shell, world, 20);
                Assert(helper.GeometryQueries == queries + 1, "same-frame deform revision invalidates cached geometry");
                FindIndependentPair(shell.sharedMesh.vertices, shell.sharedMesh.triangles, shell.sharedMesh.bounds, out var deformedInside, out var deformedOutside);
                Assert(helper.Contains(shell, shell.transform.TransformPoint(deformedInside), 21), "deformed artery independent interior accepted");
                Assert(!helper.Contains(shell, shell.transform.TransformPoint(deformedOutside), 21), "deformed artery independent exterior rejected");
            }
            finally { tissue.RestoreSource(); UnityEngine.Object.DestroyImmediate(tissue); }
        }

        // Fixture selection is independent of MeshTipInterior, ClosedMeshInterior and TissueSurfaceBvh.
        // Surface winding proposes a pair, then three brute-force ray parities must independently
        // agree on both sides. No passing point is searched using the implementation under test.
        static void FindIndependentPair(Vector3[] vertices, int[] triangles, Bounds bounds, out Vector3 inside, out Vector3 outside)
        {
            float scale = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            var normalized = vertices.Select(vertex => (vertex - bounds.center) / scale).ToArray();
            for (int sample = 0; sample < 128; sample++)
            {
                int index = (int)((sample * 15485863L) % (triangles.Length / 3)) * 3;
                var a = normalized[triangles[index]]; var b = normalized[triangles[index + 1]]; var c = normalized[triangles[index + 2]];
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-16f) continue;
                float offset = Mathf.Clamp(Mathf.Min((b-a).magnitude, Mathf.Min((c-a).magnitude, (c-b).magnitude)) * .025f, .00001f, .001f);
                var center = (a+b+c)/3; normal.Normalize();
                var proposedInside = center - normal * offset; var proposedOutside = center + normal * offset;
                if (!IndependentInside(normalized, triangles, proposedInside) || IndependentInside(normalized, triangles, proposedOutside)) continue;
                inside = proposedInside * scale + bounds.center; outside = proposedOutside * scale + bounds.center; return;
            }
            throw new InvalidOperationException("No independently verified inward/outward pair for imported mesh; author explicit golden points instead of weakening the oracle.");
        }
        static bool IndependentInside(Vector3[] vertices, int[] triangles, Vector3 point)
        {
            var directions = new[] { new Vector3(.733f, .239f, .637f).normalized, new Vector3(-.313f, .891f, .327f).normalized,
                new Vector3(.173f, -.457f, .873f).normalized };
            int votes = 0;
            foreach (var direction in directions)
            {
                var distances = new List<float>();
                for (int index = 0; index < triangles.Length; index += 3)
                {
                    var a = vertices[triangles[index]]; var ab = vertices[triangles[index+1]] - a; var ac = vertices[triangles[index+2]] - a;
                    var cross = Vector3.Cross(direction, ac); float determinant = Vector3.Dot(ab, cross);
                    if (Mathf.Abs(determinant) < 1e-12f) continue;
                    var from = point - a; float u = Vector3.Dot(from, cross) / determinant;
                    if (u < 0 || u > 1) continue;
                    var q = Vector3.Cross(from, ab); float v = Vector3.Dot(direction, q) / determinant;
                    if (v < 0 || u+v > 1) continue;
                    float distance = Vector3.Dot(ac, q) / determinant;
                    if (distance > 1e-7f) distances.Add(distance);
                }
                distances.Sort(); int intersections = 0; float previous = float.NegativeInfinity;
                foreach (float distance in distances) if (distance - previous > 1e-6f) { intersections++; previous = distance; }
                if ((intersections & 1) != 0) votes++;
            }
            return votes >= 2;
        }

        static void Tick(NativeProcedureInput input) => typeof(NativeProcedureInput).GetMethod("Update",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(input,null);
        static void Assert(bool value,string message) { if(!value)throw new InvalidOperationException("Interior contact regression: "+message); checks++; }
    }
}
