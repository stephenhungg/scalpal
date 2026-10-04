using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Scene/case-authored description of organs that are mobilized together (for example a cecum
    // with its appendix and mesentery). The mechanic below never names an organ itself.
    [Serializable]
    public sealed class MobileOrganGroup
    {
        public string[] partIds = Array.Empty<string>();
        [Tooltip("Part whose rendered centre must lie above the wound plane for the group to stay out after release.")]
        public string deliveryPartId = "";
        [Tooltip("Maximum rigid travel of the group from its rest pose (the mesenteric tether).")]
        public float maxTravelMm = 200;
        public float maxSpeedMps = .25f;
        public float returnSpeedMps = .1f;
        public float maxTiltDegrees = 15;
        public float maxTiltDegreesPerSecond = 45;
        [Tooltip("Optional held-wrist rotation from the authored pose. Zero retains positional mobilization only.")]
        public float maxGripRotationDegrees;
    }

    // Rigid mobilization of a group of atlas parts, on top of each part's local cage deformation.
    // The group transform is kept in the anatomy root's unit-scale frame, so a registration or
    // mannequin fit moves the group with the body (AR and VR use the same semantics). Rest poses
    // are the parts' authored local poses and are restored exactly.
    public sealed class OrganMobilization
    {
        readonly MobileOrganGroup definition;
        readonly Transform reference;
        readonly Transform[] parts;
        readonly Vector3[] restPositions;
        readonly Quaternion[] restRotations;
        readonly Transform deliveryPart;
        readonly MeshFilter deliveryFilter;
        Vector3 pivot, offset, grip;
        Quaternion tilt = Quaternion.identity;
        Quaternion gripToolRotation, gripStartTilt;
        bool followsWrist;
        public bool Held { get; private set; }
        public MobileOrganGroup Definition => definition;
        public Vector3 OffsetMeters => offset;
        public Quaternion Tilt => tilt;
        public Vector3 GripWorldPosition => FromReference(pivot + offset + tilt * (grip - pivot));
        public bool AtRest => offset == Vector3.zero && tilt == Quaternion.identity;
        public IReadOnlyList<Transform> Parts => parts;

        OrganMobilization(MobileOrganGroup group, Transform frame, List<Transform> members, Transform delivery)
        {
            definition = group; reference = frame; parts = members.ToArray(); deliveryPart = delivery;
            restPositions = new Vector3[parts.Length]; restRotations = new Quaternion[parts.Length];
            for (int i = 0; i < parts.Length; i++) { restPositions[i] = parts[i].localPosition; restRotations[i] = parts[i].localRotation; }
            deliveryFilter = delivery ? delivery.GetComponentInChildren<MeshFilter>(true) : null;
            Vector3 sum = Vector3.zero; int count = 0;
            foreach (var part in parts)
            {
                var filter = part.GetComponent<MeshFilter>();
                sum += filter && filter.sharedMesh ? part.TransformPoint(filter.sharedMesh.bounds.center) : part.position; count++;
            }
            pivot = ToReference(sum / count);
        }

        // Fails closed: every listed part must resolve to exactly one atlas part. Descendants of
        // another listed member are skipped because they already move with their ancestor.
        public static OrganMobilization Create(AnatomyController anatomy, MobileOrganGroup group, out string reason)
        {
            reason = "";
            if (!anatomy || group == null || group.partIds == null || group.partIds.Length == 0) { reason = "empty mobile group"; return null; }
            if (!Positive(group.maxTravelMm) || !Positive(group.maxSpeedMps) || !Positive(group.returnSpeedMps)
                || !float.IsFinite(group.maxTiltDegrees) || !(group.maxTiltDegrees >= 0)
                || !float.IsFinite(group.maxTiltDegreesPerSecond) || !(group.maxTiltDegreesPerSecond >= 0)
                || !float.IsFinite(group.maxGripRotationDegrees) || group.maxGripRotationDegrees < 0 || group.maxGripRotationDegrees > 180) { reason = "invalid mobile group limits"; return null; }
            var members = new List<Transform>();
            foreach (var id in group.partIds)
            {
                if (!anatomy.TryGetPart(id, out var part) || !part) { reason = "mobile group part missing: " + id; return null; }
                if (!part.transform.IsChildOf(anatomy.transform) || part.transform == anatomy.transform) { reason = "mobile group part outside anatomy: " + id; return null; }
                if (!members.Contains(part.transform)) members.Add(part.transform);
            }
            members.RemoveAll(member => members.Exists(other => other != member && member.IsChildOf(other)));
            Transform delivery = null;
            if (!string.IsNullOrEmpty(group.deliveryPartId))
            {
                if (!anatomy.TryGetPart(group.deliveryPartId, out var part) || !members.Exists(m => part.transform.IsChildOf(m)))
                { reason = "delivery part is not a mobile group member: " + group.deliveryPartId; return null; }
                delivery = part.transform;
            }
            return new OrganMobilization(group, anatomy.transform, members, delivery);
        }
        static bool Positive(float value) => float.IsFinite(value) && value > 0;

        public bool Contains(Transform transform)
        {
            if (!transform) return false;
            foreach (var part in parts) if (part && transform.IsChildOf(part)) return true;
            return false;
        }
        // Unit-scale reference frame: travel limits stay metric even if the atlas root is scaled.
        Vector3 ToReference(Vector3 world) => Quaternion.Inverse(reference.rotation) * (world - reference.position);
        Vector3 FromReference(Vector3 local) => reference.position + reference.rotation * local;

        public bool BeginHold(Vector3 worldGrip)
        {
            if (Held || !reference || !OpenSurgeryStroke.Finite(worldGrip)) return false;
            // Store the grip in rest coordinates so the held point stays fixed on the tissue.
            grip = pivot + Quaternion.Inverse(tilt) * (ToReference(worldGrip) - pivot - offset);
            followsWrist = false; Held = true; return true;
        }
        public bool BeginHold(Vector3 worldGrip, Quaternion worldToolRotation)
        {
            if (!FiniteRotation(worldToolRotation) || !BeginHold(worldGrip)) return false;
            gripToolRotation = Quaternion.Inverse(reference.rotation) * worldToolRotation.normalized;
            gripStartTilt = tilt;
            followsWrist = definition.maxGripRotationDegrees > 0;
            return true;
        }
        public void EndHold() { Held = false; followsWrist = false; }
        static bool FiniteRotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w)
            && float.IsFinite(Quaternion.Dot(value, value)) && Quaternion.Dot(value, value) > .000001f;

        // Speed-limited follow of the held point toward the tool, with a slight bounded tilt
        // and the tether travel bound. Returns false when nothing moved.
        public bool Follow(Vector3 worldTool, float seconds)
        {
            if (!Held || !reference || !OpenSurgeryStroke.Finite(worldTool) || !float.IsFinite(seconds) || !(seconds > 0)) return false;
            Vector3 target = ToReference(worldTool), arm = grip - pivot, reach = target - pivot - offset;
            if (definition.maxTiltDegrees > 0 && arm.sqrMagnitude > .005f * .005f && reach.sqrMagnitude > .005f * .005f)
            {
                var desired = Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(arm, reach), definition.maxTiltDegrees);
                tilt = Quaternion.RotateTowards(tilt, desired, definition.maxTiltDegreesPerSecond * seconds);
            }
            FollowPoint(target, arm, seconds); return true;
        }

        // A grasped assembly rotates with the learner's wrist. Appearance, source colliders,
        // deformation frames and base references all share this same bounded rigid transform.
        // Re-grasping captures the current pose; it never snaps the organ back to rest.
        public bool Follow(Vector3 worldTool, Quaternion worldToolRotation, float seconds)
        {
            if (!followsWrist) return Follow(worldTool, seconds);
            if (!Held || !reference || !OpenSurgeryStroke.Finite(worldTool) || !FiniteRotation(worldToolRotation)
                || !float.IsFinite(seconds) || seconds <= 0) return false;
            var current = Quaternion.Inverse(reference.rotation) * worldToolRotation.normalized;
            var delta = current * Quaternion.Inverse(gripToolRotation);
            var desired = Quaternion.RotateTowards(Quaternion.identity, delta * gripStartTilt, definition.maxGripRotationDegrees);
            tilt = Quaternion.RotateTowards(tilt, desired, definition.maxTiltDegreesPerSecond * seconds);
            FollowPoint(ToReference(worldTool), grip - pivot, seconds);
            return true;
        }
        void FollowPoint(Vector3 target, Vector3 arm, float seconds)
        {
            Vector3 step = Vector3.ClampMagnitude(target - pivot - tilt * arm - offset, definition.maxSpeedMps * seconds);
            offset = Vector3.ClampMagnitude(offset + step, definition.maxTravelMm * .001f);
            Apply();
        }

        // Unheld: a delivered group rests where it was left; otherwise it eases back to rest
        // at a bounded speed and lands on the exact authored pose.
        public bool Settle(float seconds, Transform wound)
        {
            if (Held || AtRest || !float.IsFinite(seconds) || !(seconds > 0)) return false;
            if (Delivered(wound)) return false;
            offset = Vector3.MoveTowards(offset, Vector3.zero, definition.returnSpeedMps * seconds);
            tilt = Quaternion.RotateTowards(tilt, Quaternion.identity, Mathf.Max(definition.maxTiltDegreesPerSecond, 1) * seconds);
            if (offset.sqrMagnitude < 1e-12f && Quaternion.Angle(tilt, Quaternion.identity) < .001f) { RestoreRest(); return true; }
            Apply(); return true;
        }

        // Above the registered wound plane (+Z points inward) at the delivery part's rendered centre.
        public bool Delivered(Transform wound)
        {
            if (!wound || !deliveryPart) return false;
            // Read the live mesh: a deformable part swaps in its runtime surface after binding.
            Vector3 center = deliveryFilter && deliveryFilter.sharedMesh ? deliveryFilter.transform.TransformPoint(deliveryFilter.sharedMesh.bounds.center) : deliveryPart.position;
            return OpenSurgeryStroke.Finite(center) && wound.InverseTransformPoint(center).z < 0;
        }

        public void RestoreRest()
        {
            Held = false; followsWrist = false; offset = Vector3.zero; tilt = Quaternion.identity;
            for (int i = 0; i < parts.Length; i++)
                if (parts[i]) { parts[i].localPosition = restPositions[i]; parts[i].localRotation = restRotations[i]; }
            Physics.SyncTransforms();
        }

        void Apply()
        {
            Quaternion frame = reference.rotation, worldTilt = frame * tilt * Quaternion.Inverse(frame);
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i]; if (!part) continue;
                var parent = part.parent;
                Vector3 restWorld = parent ? parent.TransformPoint(restPositions[i]) : restPositions[i];
                Quaternion restRotation = parent ? parent.rotation * restRotations[i] : restRotations[i];
                Vector3 local = ToReference(restWorld);
                part.SetPositionAndRotation(FromReference(pivot + offset + tilt * (local - pivot)), worldTilt * restRotation);
            }
            // Contact queries (Collider.Raycast/ClosestPoint) read synced physics poses.
            Physics.SyncTransforms();
        }
    }
}
