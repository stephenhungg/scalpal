using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Surgery;
using Scalpal.Surgery.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    public static class NativePatientSurfaceShadingValidation
    {
        [MenuItem("Scalpal/Quest/Validate Patient Surface Shading")]
        public static void Run()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup(); NativeTissueSimulation tissue = null;
            NativePatientSurfaceShading shading = null; Mesh otherOwner = null, scaleOracle = null; int checks = 0;
            void Require(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException("Patient shading: " + message); }
            try
            {
                OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                shading = session.GetComponent<NativePatientSurfaceShading>(); if (shading) shading.Dispose();
                var filter = session.presentation.virtualMannequin.GetComponent<MeshFilter>();
                Require(filter && filter.sharedMesh, "actual virtual patient owns a source MeshFilter");
                string sourceAsset = AssetDatabase.GetAssetPath(filter.sharedMesh);
                var importer = AssetImporter.GetAtPath(sourceAsset) as ModelImporter;
                Require(filter.sharedMesh.isReadable && importer && importer.isReadable,
                    "runtime normal smoothing requires ModelImporter Read/Write enabled on " + sourceAsset
                    + " (mesh readable=" + filter.sharedMesh.isReadable + ", importer readable=" + (importer && importer.isReadable) + ")");
                var source = filter.sharedMesh; var vertices = source.vertices; var normals = source.normals;
                var indices = source.triangles; var uv = source.uv; var tangents = source.tangents; var bounds = source.bounds;
                double sourceFaceDotSum = 0; int opposedFaces = 0, measuredFaces = 0;
                float extent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                for (int i = 0; i < indices.Length; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    Vector3 cross = Vector3.Cross((vertices[b] - vertices[a]) / extent, (vertices[c] - vertices[a]) / extent);
                    if (cross.sqrMagnitude <= 1e-20f) continue;
                    cross /= Mathf.Sqrt(cross.sqrMagnitude); measuredFaces++;
                    float dot = Vector3.Dot(cross, (normals[a] + normals[b] + normals[c]).normalized);
                    sourceFaceDotSum += dot; if (dot < 0) opposedFaces++;
                }
                var colliders = session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true);
                var collisionMeshes = colliders.Select(c => c.sharedMesh).ToArray();
                var collisionPositions = colliders.Select(c => c.sharedMesh.vertices).ToArray();
                Require(indices.Length / 3 == 1578 && vertices.Length > 4000 && colliders.Length > 0,
                    "fixture is the real split-corner 1578-triangle mannequin with its authored collider");
                int log = session.exercise.Body.Log.Count; var materials = session.presentation.virtualMannequin.sharedMaterials;
                float window = Shader.GetGlobalFloat("_ScalpalWoundWindow");
                if (!shading) shading = session.gameObject.AddComponent<NativePatientSurfaceShading>();
                shading.Initialize(session.presentation);
                var rendered = shading.RenderMesh;
                Require(rendered && rendered != source && filter.sharedMesh == rendered && shading.SourceMesh == source,
                    "only the actual renderer receives an owned clone");
                Require(shading.ChangedNormals > 300, "nonvacuous real-mesh correction changes hundreds of flat source normals; changed="
                    + shading.ChangedNormals + " sourceFaceDotSum=" + sourceFaceDotSum + " opposedFaces=" + opposedFaces + "/" + measuredFaces + " measured (" + (indices.Length / 3) + " total)");
                Require(rendered.vertexCount == vertices.Length && vertices.SequenceEqual(rendered.vertices) && indices.SequenceEqual(rendered.triangles)
                    && uv.SequenceEqual(rendered.uv) && rendered.bounds == bounds && rendered.subMeshCount == source.subMeshCount,
                    "positions/topology/UVs/bounds/submesh layout are exact, with no extra polygons");
                for (int sub = 0; sub < source.subMeshCount; sub++) Require(source.GetIndices(sub).SequenceEqual(rendered.GetIndices(sub)), "source submesh indices preserved");
                var result = rendered.normals;
                Require(result.All(n => float.IsFinite(n.x) && float.IsFinite(n.y) && float.IsFinite(n.z) && Mathf.Abs(n.magnitude - 1) < .0001f),
                    "all generated render normals are finite unit vectors");
                if (uv.Length == vertices.Length)
                    Require(rendered.tangents.Length == vertices.Length && rendered.tangents.All(t => float.IsFinite(t.x)
                        && float.IsFinite(t.y) && float.IsFinite(t.z) && float.IsFinite(t.w)),
                        "regenerated tangents remain finite on the actual tiny-coordinate source with UVs");
                float metricScale = Mathf.Max(filter.transform.TransformVector(Vector3.right).magnitude,
                    Mathf.Max(filter.transform.TransformVector(Vector3.up).magnitude, filter.transform.TransformVector(Vector3.forward).magnitude));
                scaleOracle = UnityEngine.Object.Instantiate(source); scaleOracle.vertices = vertices.Select(v => v * 1000f).ToArray(); scaleOracle.RecalculateBounds();
                var scaleResult = NativePatientSurfaceShading.SmoothNormals(scaleOracle, metricScale / 1000f, out int scaleChanged);
                Require(scaleResult != null && scaleChanged > 300 && scaleResult.Length == result.Length
                    && Enumerable.Range(0, result.Length).All(i => Vector3.Dot(result[i], scaleResult[i]) > .9999f),
                    "actual source scaled by 1000 yields the same smoothed normals, without absolute-area or Vector3 normalization cutoffs; changed=" + scaleChanged);
                float cosine = Mathf.Cos(NativePatientSurfaceShading.SmoothingAngleDegrees * Mathf.Deg2Rad);
                Require(Enumerable.Range(0, result.Length).All(i => Vector3.Dot(result[i], normals[i].normalized) >= cosine - .0001f),
                    "smoothing never turns a source corner by more than its explicit 60-degree limit");
                var coincident = new Dictionary<Vector3, List<int>>();
                for (int i = 0; i < vertices.Length; i++)
                { if (!coincident.TryGetValue(vertices[i], out var group)) { group = new List<int>(); coincident.Add(vertices[i], group); } group.Add(i); }
                int improved = 0; float strongest = 1; int sharpA = -1, sharpB = -1;
                foreach (var group in coincident.Values) for (int a = 0; a < group.Count; a++) for (int b = a + 1; b < group.Count; b++)
                {
                    int x = group[a], y = group[b]; float old = Vector3.Dot(normals[x].normalized, normals[y].normalized);
                    if (old > .5f && old < .98f && Vector3.Dot(result[x], result[y]) > old + .01f) improved++;
                    if (old < strongest) { strongest = old; sharpA = x; sharpB = y; }
                }
                Require(improved > 100, "hundreds of actual mild source seams become smoother, rather than only reallocating a mesh");
                Require(sharpA >= 0 && strongest < -.866f && Vector3.Dot(result[sharpA], result[sharpB]) < .87f,
                    "a real source seam over 150 degrees remains a distinct hard edge, not an indiscriminate weld");
                Require(normals.SequenceEqual(source.normals) && vertices.SequenceEqual(source.vertices) && indices.SequenceEqual(source.triangles)
                    && tangents.SequenceEqual(source.tangents), "imported source geometry/normals/tangents remain unchanged");
                for (int i = 0; i < colliders.Length; i++) Require(colliders[i].sharedMesh == collisionMeshes[i]
                    && collisionPositions[i].SequenceEqual(colliders[i].sharedMesh.vertices), "actual patient collider identity/positions remain unchanged");
                Require(materials.SequenceEqual(session.presentation.virtualMannequin.sharedMaterials) && session.exercise.Body.Log.Count == log
                    && Shader.GetGlobalFloat("_ScalpalWoundWindow") == window, "material/wound shader/scored state stays with its existing owners");
                shading.Initialize(session.presentation);
                Require(shading.RenderMesh && shading.RenderMesh != rendered && shading.SourceMesh == source
                    && shading.RenderMesh.triangles.SequenceEqual(indices), "retry/reinitialize replaces one owned clone without recursively cloning altered geometry");
                shading.Dispose(); Require(filter.sharedMesh == source && !shading.RenderMesh, "disposal restores original renderer binding");
                shading.Initialize(session.presentation); otherOwner = UnityEngine.Object.Instantiate(source); filter.sharedMesh = otherOwner;
                shading.Dispose(); Require(filter.sharedMesh == otherOwner, "disposal preserves a newer mesh selected by another owner");
                filter.sharedMesh = source; shading.Initialize(null); Require(!shading.RenderMesh, "missing presentation creates no fallback geometry");
                Debug.Log("SCALPAL_NATIVE_PATIENT_SHADING_OK: " + checks + " actual imported mannequin normal/source/collider checks; zero extra triangles; no silhouette or clinical shape claim");
            }
            finally { if (shading) UnityEngine.Object.DestroyImmediate(shading); if (otherOwner) UnityEngine.Object.DestroyImmediate(otherOwner); if (scaleOracle) UnityEngine.Object.DestroyImmediate(scaleOracle); if (tissue) tissue.Dispose(); OpenSurgeryBuild.RestoreScenes(previous); }
        }
    }
}
