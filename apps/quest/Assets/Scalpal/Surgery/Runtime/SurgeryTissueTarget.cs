using System;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Authored anatomy identity and a metric base-to-tip reference. No procedure or milestone IDs.
    [DisallowMultipleComponent]
    public sealed class SurgeryTissueTarget : MonoBehaviour
    {
        public string tissueId;
        public Transform basePoint, endPoint;
        public Vector3 localFiberDirection = Vector3.right;
        [Tooltip("Enable only when the existing tissue simulation is not driving this cage.")]
        public bool driveGrasp;
        Collider[] shapes = Array.Empty<Collider>();
        AnatomyPart part;
        public DeformableTissue Deformable { get; private set; }
        public bool HasBase => LongitudinalReferences(out _, out _);
        public Vector3 FiberWorld => transform.TransformDirection(localFiberDirection).normalized;
        public Vector3 LongitudinalWorld => LongitudinalReferences(out var start, out var end) ? (end - start).normalized : FiberWorld;
        public bool Available => isActiveAndEnabled && (!part || (part.IsVisible && part.HasVisibleGeometry));

        public void Refresh()
        {
            part = GetComponent<AnatomyPart>();
            Deformable = GetComponent<DeformableTissue>();
            var candidates = GetComponentsInChildren<Collider>(true);
            var owned = new System.Collections.Generic.List<Collider>();
            foreach (var collider in candidates)
                if (collider.GetComponentInParent<SurgeryTissueTarget>(true) == this &&
                    (!part || collider.GetComponentInParent<AnatomyPart>(true) == part)) owned.Add(collider);
            shapes = owned.ToArray();
        }
        void Awake() => Refresh();

        public bool DistanceFromBase(Vector3 world, out float mm)
        {
            mm = 0;
            if (!OpenSurgeryStroke.Finite(world) || !LongitudinalReferences(out var start, out var end)) return false;
            float along = Vector3.Dot(world - start, (end - start).normalized);
            if (!float.IsFinite(along) || along < 0) return false;
            mm = along * 1000; return true;
        }
        bool LongitudinalReferences(out Vector3 start, out Vector3 end)
        {
            start = end = default;
            if (!basePoint || !endPoint) return false;
            start = ReferenceWorld(basePoint); end = ReferenceWorld(endPoint);
            return OpenSurgeryStroke.Finite(start) && OpenSurgeryStroke.Finite(end) && Vector3.Distance(start, end) > .001f;
        }
        Vector3 ReferenceWorld(Transform reference)
        {
            if (!Deformable || Deformable.Cage == null) return reference.position;
            // References stay in the authored rest surface. Embed them through the same
            // raw-source-to-meter cage mapping as rendered vertices, without moving the authoring data.
            Vector3 raw = Deformable.transform.InverseTransformPoint(reference.position);
            return Deformable.transform.TransformPoint(Deformable.DeformSurfacePoint(raw));
        }

        // Collider.Raycast reads the current cooked deforming mesh. Nonconvex MeshCollider does
        // not support ClosestPoint; use six bounded surface probes rather than stale source vertices.
        // This is sampled contact, not a continuous collision guarantee.
        public bool TryContact(Vector3 world, float radius, out Vector3 contact)
        {
            contact = default;
            if (!Available || !OpenSurgeryStroke.Finite(world) || radius <= 0) return false;
            float closest = radius * radius; bool found = false;
            foreach (var shape in shapes)
            {
                if (!shape || !shape.enabled || !shape.gameObject.activeInHierarchy || shape.bounds.SqrDistance(world) > closest) continue;
                if (!(shape is MeshCollider mesh) || mesh.convex)
                {
                    Vector3 p = shape.ClosestPoint(world); float distance = (world - p).sqrMagnitude;
                    if (distance <= closest) { closest = distance; contact = p; found = true; }
                    continue;
                }
                for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 direction = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                    direction *= sign;
                    if (!shape.Raycast(new Ray(world - direction * radius, direction), out var hit, radius * 2)) continue;
                    float distance = (world - hit.point).sqrMagnitude;
                    if (distance <= closest) { closest = distance; contact = hit.point; found = true; }
                }
            }
            return found;
        }
        public bool BladeContact(Vector3 start, Vector3 end, out Vector3 contact)
        {
            contact = default;
            Vector3 delta = end - start; float length = delta.magnitude;
            if (!Available || length < .00001f || length > .15f) return false;
            foreach (var shape in shapes)
            {
                if (!shape || !shape.enabled || !shape.gameObject.activeInHierarchy) continue;
                if (shape.Raycast(new Ray(start, delta / length), out var hit, length) ||
                    shape.Raycast(new Ray(end, -delta / length), out hit, length)) { contact = hit.point; return true; }
            }
            return false;
        }
    }
}
