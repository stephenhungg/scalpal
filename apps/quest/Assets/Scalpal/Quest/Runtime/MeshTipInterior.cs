using System;
using Scalpal.Anatomy.Tissue;
using UnityEngine;

namespace Scalpal.Quest
{
    // Owned anatomy contact only. Open or large teaching meshes use the nearest outward
    // surface normal, an approximation near holes/concavities, not watertight-volume proof.
    // Closed bounded shells retain winding/foldover rejection. No extra scoring producer.
    public sealed class MeshTipInterior : IDisposable
    {
        ClosedMeshInterior closed = new ClosedMeshInterior();
        TissueSurfaceBvh surface;
        Mesh source;
        int revision = -1, frame = -1;
        bool fallbackOnly, cached, answer;
        Vector3 point;
        Vector3 origin;
        float scale;
        Vector3[] normalized;
        Matrix4x4 matrix;
        readonly Vector3[] probe = new Vector3[1];
        public int GeometryQueries { get; private set; }
        public int LastTriangleTests => surface?.LastTriangleTests ?? 0;
        public bool UsesSurfaceApproximation => fallbackOnly;

        // frameIndex < 0 disables result caching for Editor geometry fixtures.
        public bool Contains(MeshCollider collider, Vector3 worldPoint, int frameIndex = -1)
        {
            if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.convex ||
                !collider.sharedMesh || !collider.sharedMesh.isReadable || !TissueCage.Finite(worldPoint)) return false;
            var mesh = collider.sharedMesh;
            var tissue = collider.GetComponent<DeformableTissue>();
            int currentRevision = tissue ? tissue.SurfaceRevision : 0;
            if (source != mesh)
            {
                Dispose(); source = mesh; closed = new ClosedMeshInterior(); fallbackOnly = false;
            }
            if (revision != currentRevision)
            {
                cached = false; revision = currentRevision;
                if (surface != null)
                {
                    Normalize(tissue ? tissue.SurfaceVertices : mesh.vertices);
                    surface.Refit(normalized);
                }
            }
            var currentMatrix = collider.transform.localToWorldMatrix;
            if (cached && frameIndex >= 0 && frame == frameIndex && point.Equals(worldPoint) && matrix.Equals(currentMatrix)) return answer;
            frame = frameIndex; point = worldPoint; matrix = currentMatrix; cached = true; GeometryQueries++;
            answer = false;
            var local = collider.transform.InverseTransformPoint(worldPoint);
            if (!TissueCage.Finite(local) || !mesh.bounds.Contains(local)) return false;
            if (!fallbackOnly)
            {
                answer = closed.Contains(collider, worldPoint);
                if (answer || !closed.SurfaceFallbackEligible) return answer;
                // This route is restricted to the selected AnatomyPart by NativeProcedureInput.
                // Import holes and the large omentum must not disable all buried-tip contacts.
                var vertices = tissue && tissue.SurfaceVertices != null ? tissue.SurfaceVertices : mesh.vertices;
                var triangles = mesh.triangles;
                for (int i = 0; i < vertices.Length; i++) if (!TissueCage.Finite(vertices[i])) return false;
                for (int i = 0; i < triangles.Length; i++) if (triangles[i] < 0 || triangles[i] >= vertices.Length) return false;
                if (triangles.Length == 0 || triangles.Length % 3 != 0) return false;
                // FBX mesh coordinates can be 1/100 of body meters. Normalize before
                // nearest-query epsilon/degeneracy tests, otherwise tiny artery faces
                // are discarded and unrelated faces compare as equal-distance ties.
                origin = mesh.bounds.center;
                scale = Mathf.Max(mesh.bounds.size.x, Mathf.Max(mesh.bounds.size.y, mesh.bounds.size.z));
                if (!(scale > 1e-12f) || float.IsInfinity(scale)) return false;
                normalized = new Vector3[vertices.Length]; Normalize(vertices);
                surface = new TissueSurfaceBvh(normalized, triangles, 1); fallbackOnly = true;
            }
            probe[0] = (local - origin) / scale; surface.Query(probe, 1);
            var hit = surface.Result(0);
            answer = hit.triangle >= 0 && hit.penetration > 0 && !float.IsInfinity(hit.penetration) && !float.IsNaN(hit.penetration);
            return answer;
        }

        void Normalize(Vector3[] vertices)
        {
            for (int i = 0; i < vertices.Length; i++) normalized[i] = (vertices[i] - origin) / scale;
        }

        public void Dispose()
        {
            surface?.Dispose(); surface = null; source = null; normalized = null; revision = -1; cached = false;
        }
    }
}
