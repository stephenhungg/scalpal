using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Quest
{
    // The CC0 mannequin exports separate corners with face-aligned custom normals. This
    // changes only render normals/tangents; it does not round the body, subdivide topology,
    // move the skin, or change the independently authored patient collider.
    [DisallowMultipleComponent]
    public sealed class NativePatientSurfaceShading : MonoBehaviour
    {
        MeshFilter filter;
        Mesh source, shown;
        public Mesh SourceMesh => source;
        public Mesh RenderMesh => shown;
        public int ChangedNormals { get; private set; }
        public const float SmoothingAngleDegrees = 60;

        public void Initialize(NativePresentation presentation)
        {
            Dispose();
            if (!presentation || !presentation.virtualMannequin) return;
            filter = presentation.virtualMannequin.GetComponent<MeshFilter>();
            if (!filter || !filter.sharedMesh || !filter.sharedMesh.isReadable) { filter = null; return; }
            source = filter.sharedMesh;
            float metricScale = Mathf.Max(filter.transform.TransformVector(Vector3.right).magnitude,
                Mathf.Max(filter.transform.TransformVector(Vector3.up).magnitude, filter.transform.TransformVector(Vector3.forward).magnitude));
            var result = SmoothNormals(source, metricScale, out int changed);
            if (result == null) { filter = null; source = null; return; }
            ChangedNormals = changed;
            shown = Instantiate(source); shown.name = source.name + "_AngleLimitedRenderNormals";
            shown.normals = result;
            if (shown.uv.Length == shown.vertexCount) shown.RecalculateTangents();
            filter.sharedMesh = shown;
        }

        // Pure source calculation shared with the unit-scale regression. It allocates once
        // during setup, never changes the argument mesh, and has no scene/scoring dependencies.
        public static Vector3[] SmoothNormals(Mesh source, float metricScale, out int changed)
        {
            changed = 0;
            if (!source || !source.isReadable) return null;
            var vertices = source.vertices; var normals = source.normals; var triangles = source.triangles;
            if (vertices.Length == 0 || normals.Length != vertices.Length || triangles.Length == 0) return null;
            float extent = Mathf.Max(source.bounds.size.x, Mathf.Max(source.bounds.size.y, source.bounds.size.z));
            if (!float.IsFinite(extent) || extent <= 0) return null;
            var weighted = new Vector3[vertices.Length];
            // Imported visual normals retain the authored orientation, which can oppose the
            // raw cross-product winding after FBX handedness conversion. Use triangle area
            // only as the weight; never replace that authored direction with a winding normal.
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                // FBX raw positions can be tiny behind a large node transform. Dividing
                // edges by one common extent preserves area ratios at every source unit.
                float area = Vector3.Cross((vertices[b] - vertices[a]) / extent, (vertices[c] - vertices[a]) / extent).magnitude;
                weighted[a] += normals[a].normalized * area;
                weighted[b] += normals[b].normalized * area;
                weighted[c] += normals[c].normalized * area;
            }
            // A micrometre weld tolerance in the current metric fit, without merging vertices.
            float tolerance = Mathf.Max(.000001f / Mathf.Max(metricScale, .000001f), 1e-9f);
            var groups = new Dictionary<Vector3Int, List<int>>();
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i] / tolerance;
                var key = new Vector3Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z));
                if (!groups.TryGetValue(key, out var group)) { group = new List<int>(); groups.Add(key, group); }
                group.Add(i);
            }
            float cosine = Mathf.Cos(SmoothingAngleDegrees * Mathf.Deg2Rad);
            var result = (Vector3[])normals.Clone();
            foreach (var group in groups.Values)
                foreach (int current in group)
                {
                    Vector3 reference = normals[current].normalized, sum = Vector3.zero;
                    if (reference.sqrMagnitude < .5f) continue;
                    foreach (int neighbor in group)
                        if (Vector3.Dot(reference, normals[neighbor].normalized) >= cosine)
                            sum += weighted[neighbor];
                    if (sum.sqrMagnitude > 1e-20f)
                    {
                        // Vector3.normalized deliberately returns zero below a fixed length
                        // cutoff, which is inappropriate for weighted sums of small faces.
                        Vector3 normal = sum / Mathf.Sqrt(sum.sqrMagnitude);
                        // Degenerate/counter-wound source faces must not reverse a normal.
                        if (Vector3.Dot(normal, reference) >= cosine - .0001f) result[current] = normal;
                    }
                    if (Vector3.Dot(result[current].normalized, reference) < .9999f) changed++;
                }
            return result;
        }
        void OnDestroy() => Dispose();
        public void Dispose()
        {
            // Another appearance owner may replace the renderer later. Restore only our
            // own binding; never undo that owner's newer mesh selection.
            if (filter && filter.sharedMesh == shown) filter.sharedMesh = source;
            if (shown) { if (Application.isPlaying) Destroy(shown); else DestroyImmediate(shown); }
            filter = null; shown = source = null; ChangedNormals = 0;
        }
    }
}
