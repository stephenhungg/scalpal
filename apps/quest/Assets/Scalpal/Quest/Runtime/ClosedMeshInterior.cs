using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Quest
{
    // Conservative bounded fallback for hollow PhysX mesh shells, not a fluid or surgical SDF.
    // Validates oriented watertight topology (including exact-position import seams), then checks
    // CURRENT collider geometry in its own local frame. Open/non-manifold/inverted shells fail closed.
    public sealed class ClosedMeshInterior
    {
        public const int MaximumTriangles = 32768;
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<int> indices = new List<int>();
        readonly List<int> submesh = new List<int>();
        Mesh source;
        int[] welded, parents;
        Vector3[] referenceNormals;
        double[] volumes;
        ulong topologyHash;
        bool closed;
        static readonly Vector3[] directions = {
            new Vector3(1, .371f, .129f).normalized,
            new Vector3(.217f, 1, .513f).normalized,
            new Vector3(.619f, .283f, 1).normalized
        };

        public bool Contains(MeshCollider collider, Vector3 worldPoint)
        {
            if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.convex ||
                !collider.sharedMesh || !collider.sharedMesh.isReadable || !Finite(worldPoint)) return false;
            var mesh = collider.sharedMesh;
            if (mesh.vertexCount > MaximumTriangles * 3 || mesh.subMeshCount > 16) return false;
            mesh.GetVertices(vertices); indices.Clear();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles || mesh.GetIndexCount(i) > MaximumTriangles * 3) return false;
                mesh.GetTriangles(submesh, i); indices.AddRange(submesh);
                if (indices.Count > MaximumTriangles * 3) return false;
            }
            if (indices.Count < 12 || indices.Count % 3 != 0 || vertices.Count < 4) return false;
            var hash = Hash(indices);
            if (source != mesh || welded == null || welded.Length != vertices.Count || hash != topologyHash)
            {
                source = mesh; topologyHash = hash;
                closed = BuildTopology();
            }
            if (!closed) return false;
            var point = collider.transform.InverseTransformPoint(worldPoint);
            if (!Finite(point)) return false;
            Vector3 minimum = vertices[0], maximum = minimum;
            for (int i = 0; i < vertices.Count; i++)
            {
                if (!Finite(vertices[i]) || (vertices[i] - vertices[welded[i]]).sqrMagnitude > 1e-14f) return false;
                minimum = Vector3.Min(minimum, vertices[i]); maximum = Vector3.Max(maximum, vertices[i]);
            }
            var bounds = new Bounds((minimum + maximum) * .5f, maximum - minimum);
            if (!bounds.Contains(point)) return false;
            float scale = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (!(scale > 1e-8f)) return false;
            Array.Clear(volumes, 0, volumes.Length);
            for (int i = 0; i < indices.Count; i += 3)
            {
                var a = vertices[indices[i]]; var b = vertices[indices[i+1]]; var c = vertices[indices[i+2]];
                var normal = Vector3.Cross(b-a, c-a);
                // Reject a face foldover relative to the accepted initial shell. Large rigid moves
                // must use Transform; locally rewriting a shell through >90 degrees is conservative rejection.
                if (!Finite(normal) || Vector3.Dot(normal, referenceNormals[i / 3]) <= 0) return false;
                a = (a - bounds.center) / scale; b = (b - bounds.center) / scale; c = (c - bounds.center) / scale;
                volumes[Root(welded[indices[i]])] += Dot(a, Vector3.Cross(b, c)) / 6.0;
            }
            for (int i = 0; i < parents.Length; i++)
                if (welded[i] == i && Root(i) == i && !(volumes[i] > 1e-12)) return false;
            int accepted = 0;
            foreach (var direction in directions)
            {
                if (!Parity(point, direction, scale, out bool inside)) continue; // grazing edges are ambiguous
                if (!inside) return false;
                accepted++;
            }
            return accepted >= 2;
        }

        bool BuildTopology()
        {
            welded = new int[vertices.Count]; parents = new int[vertices.Count]; volumes = new double[vertices.Count];
            referenceNormals = new Vector3[indices.Count / 3];
            var positions = new Dictionary<Vector3, int>();
            var edges = new Dictionary<ulong, Edge>();
            for (int i = 0; i < vertices.Count; i++)
            {
                if (!Finite(vertices[i])) return false;
                if (!positions.TryGetValue(vertices[i], out int id)) { id = i; positions.Add(vertices[i], id); }
                welded[i] = id; parents[i] = i;
            }
            for (int i = 0; i < indices.Count; i += 3)
            {
                int a = indices[i], b = indices[i+1], c = indices[i+2];
                if (a < 0 || b < 0 || c < 0 || a >= welded.Length || b >= welded.Length || c >= welded.Length) return false;
                var normal = Vector3.Cross(vertices[b]-vertices[a], vertices[c]-vertices[a]);
                if (!Finite(normal) || !(normal.sqrMagnitude > 1e-24f)) return false;
                referenceNormals[i / 3] = normal;
                a = welded[a]; b = welded[b]; c = welded[c];
                if (a == b || b == c || a == c) return false;
                AddEdge(edges, a, b); AddEdge(edges, b, c); AddEdge(edges, c, a);
                parents[Root(b)] = Root(a); parents[Root(c)] = Root(a);
            }
            foreach (var edge in edges.Values) if (edge.count != 2 || edge.orientation != 0) return false;
            for (int i = 0; i < parents.Length; i++) parents[i] = Root(i);
            return true;
        }
        struct Edge { public int count, orientation; }
        static void AddEdge(Dictionary<ulong, Edge> edges, int a, int b)
        {
            var key = ((ulong)(uint)Math.Min(a,b) << 32) | (uint)Math.Max(a,b);
            edges.TryGetValue(key, out var edge); edge.count++; edge.orientation += a < b ? 1 : -1; edges[key] = edge;
        }
        int Root(int i)
        {
            int root = i;
            while (parents[root] != root) root = parents[root];
            while (parents[i] != i) { int next = parents[i]; parents[i] = root; i = next; }
            return root;
        }
        bool Parity(Vector3 point, Vector3 direction, float scale, out bool inside)
        {
            int crossings = 0; inside = false;
            for (int i = 0; i < indices.Count; i += 3)
            {
                // Normalize units for centimeter FBX imports and meter-authored fixtures alike.
                var a = (vertices[indices[i]] - point) / scale;
                var e1 = (vertices[indices[i+1]] - vertices[indices[i]]) / scale;
                var e2 = (vertices[indices[i+2]] - vertices[indices[i]]) / scale;
                var p = Vector3.Cross(direction, e2); double det = Dot(e1,p);
                if (Math.Abs(det) < 1e-12) continue;
                var t = -a; double u = Dot(t,p) / det;
                if (u < -1e-7 || u > 1+1e-7) continue;
                var q = Vector3.Cross(t,e1); double v = Dot(direction,q) / det;
                if (v < -1e-7 || u+v > 1+1e-7) continue;
                double distance = Dot(e2,q) / det;
                if (distance < -1e-7) continue;
                if (distance <= 1e-7 || u < 1e-7 || v < 1e-7 || 1-u-v < 1e-7) return false;
                crossings++;
            }
            inside = (crossings & 1) != 0; return true;
        }
        static double Dot(Vector3 a, Vector3 b) => (double)a.x*b.x + (double)a.y*b.y + (double)a.z*b.z;
        static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);
        static ulong Hash(List<int> values) { unchecked { ulong h = 1469598103934665603UL; foreach (int v in values) h = (h ^ (uint)v) * 1099511628211UL; return h; } }
    }
}
