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
        bool dirty;
        public bool Initialize(TissuePreset preset)
        {
            if (Cage != null) return true;
            filter = GetComponent<MeshFilter>(); contact = GetComponent<MeshCollider>();
            // Contact and rendered surface must agree. Never leave a static scoring surface behind.
            if (!filter || !contact || !filter.sharedMesh || !filter.sharedMesh.isReadable ||
                filter.sharedMesh.vertexCount > 8000 || filter.sharedMesh.triangles.Length > 9000) return false;
            source = filter.sharedMesh;
            try { Cage = new TissueCage(source.bounds, preset); }
            catch (ArgumentException) { return false; }
            rest = source.vertices; deformed = new Vector3[rest.Length];
            dynamicMesh = Instantiate(source); dynamicMesh.name = source.name + "_RuntimeTissue"; dynamicMesh.MarkDynamic();
            filter.sharedMesh = dynamicMesh; contact.sharedMesh = dynamicMesh;
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
            for (int i=0;i<rest.Length;i++) deformed[i] = Cage.Deform(rest[i]);
            dynamicMesh.vertices = deformed; dynamicMesh.RecalculateNormals(); dynamicMesh.RecalculateBounds();
            // Bounded low-triangle targets only. Rendering and contact are updated in one commit.
            contact.sharedMesh = null; contact.sharedMesh = dynamicMesh; dirty = false;
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
            dynamicMesh = null; Cage = null; rest = deformed = null; dirty = false;
        }
    }
}
