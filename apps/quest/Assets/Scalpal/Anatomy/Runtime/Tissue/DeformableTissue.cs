using System;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    [DisallowMultipleComponent]
    public sealed class DeformableTissue : MonoBehaviour
    {
        public TissueCage Cage { get; private set; }
        MeshFilter filter;
        MeshCollider contact;
        Mesh source, dynamicMesh;
        Vector3[] rest, deformed;
        int[] surfaceTriangles;
        bool dirty;
        // Read-only source asset reference; contact reads vertices without modifying it.
        public Mesh SourceMesh => source;
        public int SurfaceRevision { get; private set; }
        // Owned raw-coordinate buffers. Internal consumers must never mutate these arrays.
        internal Vector3[] SurfaceVertices => deformed;
        internal Vector3[] RestVertices => rest;
        internal int[] SurfaceTriangles => surfaceTriangles;
        public float SourceUnitScale { get; private set; } = 1;
        // FBX-local coordinates may have an import scale (e.g.100). Cage positions always use source-body meters.
        public Vector3 ToMeters(Vector3 rawPoint) => rawPoint * SourceUnitScale;
        public Vector3 FromMeters(Vector3 meterPoint) => meterPoint / SourceUnitScale;
        public Vector3 DeformSurfacePoint(Vector3 rawPoint) => Cage == null ? rawPoint : FromMeters(Cage.Deform(ToMeters(rawPoint)));
        public bool ApplyContact(Vector3 localPoint, Vector3 localCorrection)
        {
            if (Cage == null || !Cage.ApplyContact(localPoint, localCorrection)) return false;
            dirty = true; return true;
        }
        internal void CommitContact(Vector3[] candidate) { Cage.CommitContact(candidate); dirty = true; }
        public bool Initialize(TissuePreset preset, float sourceUnitsToMeters = 1, Vector3? posteriorAxis = null)
        {
            if (float.IsNaN(sourceUnitsToMeters) || float.IsInfinity(sourceUnitsToMeters) || sourceUnitsToMeters <= 0) return false;
            var axis=posteriorAxis??Vector3.forward;
            if(!TissueCage.Finite(axis)||axis.sqrMagnitude<1e-10f)return false;
            axis.Normalize();
            if (Cage != null) return Mathf.Approximately(SourceUnitScale, sourceUnitsToMeters)&&Vector3.Dot(Cage.PosteriorAxis,axis)>.99999f;
            filter = GetComponent<MeshFilter>(); contact = GetComponent<MeshCollider>();
            // Contact and rendered surface must agree. Never leave a static scoring surface behind.
            if (!filter || !contact || !filter.sharedMesh || !filter.sharedMesh.isReadable ||
                filter.sharedMesh.vertexCount > 8000 || filter.sharedMesh.triangles.Length > 9000) return false;
            source = filter.sharedMesh;
            try { Cage = new TissueCage(new Bounds(source.bounds.center * sourceUnitsToMeters, source.bounds.size * sourceUnitsToMeters), preset,axis); }
            catch (ArgumentException) { return false; }
            SourceUnitScale = sourceUnitsToMeters;
            rest = source.vertices; surfaceTriangles = source.triangles; deformed = (Vector3[])rest.Clone();
            dynamicMesh = Instantiate(source); dynamicMesh.name = source.name + "_RuntimeTissue"; dynamicMesh.MarkDynamic();
            filter.sharedMesh = dynamicMesh; contact.sharedMesh = dynamicMesh;
            SurfaceRevision++;
            return true;
        }
        public void Step(float seconds, Vector3 target)
        {
            if (Cage == null) return;
            float before = Cage.MaxDisplacement;
            Cage.Step(seconds, target);
            dirty |= before > .00001f || Cage.MaxDisplacement > .00001f;
        }
        public void CommitSurface()
        {
            if (!dirty || Cage == null) return;
            for (int i=0;i<rest.Length;i++) deformed[i] = DeformSurfacePoint(rest[i]);
            dynamicMesh.vertices = deformed; dynamicMesh.RecalculateNormals(); dynamicMesh.RecalculateBounds();
            // Bounded low-triangle targets only. Rendering and contact are updated in one commit.
            contact.sharedMesh = null; contact.sharedMesh = dynamicMesh; dirty = false;
            SurfaceRevision++;
        }
        public void ResetTissue()
        {
            if (Cage == null) return;
            Cage.Reset(); dirty = true; CommitSurface();
        }
        void OnDisable() => ResetTissue();
        void OnDestroy() => RestoreSource();
        // Explicit Editor-fixture disposal; player destruction uses the same idempotent cleanup.
        public void RestoreSource()
        {
            if (source == null) return;
            if (filter) filter.sharedMesh = source;
            if (contact) contact.sharedMesh = source;
            if (dynamicMesh) { if (Application.isPlaying) Destroy(dynamicMesh); else DestroyImmediate(dynamicMesh); }
            dynamicMesh = null; Cage = null; rest = deformed = null; surfaceTriangles = null; dirty = false; SourceUnitScale = 1;
            SurfaceRevision++;
        }
    }
}
