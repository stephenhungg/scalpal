using System;
using UnityEngine;

namespace Scalpal.Quest
{
    // All image coordinates are normalized top-left. MediaPipe z is never world depth.
    public static class BodyRegistrationMath
    {
        public const float SourceHipHeight = 0.860876f;
        public const float SourceShoulderHeight = 1.382441f;
        public const float SourceFront = -0.115287f;
        public static readonly Vector3 SourceUmbilicus = new Vector3(0, 1.017768f, SourceFront);
        public struct Fit { public Vector3 position; public Quaternion rotation; public float scale; public Vector3[] points; }
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        public static bool ValidPose(Pose pose)
        {
            var q = pose.rotation;
            float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return Finite(pose.position) && Finite(norm) && Mathf.Abs(norm - 1) < .01f;
        }
        public static bool ValidFloorLensPose(Pose lens, Pose lensOffset)
        {
            if (!ValidPose(lens) || !ValidPose(lensOffset)) return false;
            var headRotation = lens.rotation * Quaternion.Inverse(lensOffset.rotation);
            var headPosition = lens.position - headRotation * lensOffset.position;
            // OVR GetNodePoseStateAtTime returns an identity head pose on native failure.
            // It still yields a unit lens rotation after extrinsics are applied. In this
            // floor-space reclining demo the observer's head cannot be at floor origin.
            return Finite(headPosition) && headPosition.y > .25f;
        }

        public static bool TryPlane(Vector3 a, Vector3 b, Vector3 c, Vector3 observer, out Plane plane)
        {
            plane = default;
            if (!Finite(a) || !Finite(b) || !Finite(c)) return false;
            var cross = Vector3.Cross(b - a, c - a);
            if (cross.magnitude < 0.025f) return false;
            var normal = cross.normalized;
            if (Vector3.Dot(normal, observer - a) < 0) normal = -normal;
            // This first slice is a reclining torso, not arbitrary standing-body reconstruction.
            if (Vector3.Dot(normal, Vector3.up) < 0.7f) return false;
            plane = new Plane(normal, a); return true;
        }

        public static bool Intersect(Ray ray, Plane plane, Vector3 calibratedCenter, out Vector3 point)
        {
            point = default;
            if (!Finite(ray.origin) || !Finite(ray.direction) || Mathf.Abs(Vector3.Dot(ray.direction.normalized, plane.normal)) < 0.12f
                || !plane.Raycast(ray, out float distance) || distance < 0.2f || distance > 3f) return false;
            point = ray.GetPoint(distance);
            return Finite(point) && Vector3.Distance(point, calibratedCenter) < 1.2f;
        }

        public static bool ImageRay(Ray bottomLeft, Ray bottomRight, Ray topLeft, Vector3 lensForward, Vector2 imagePoint, out Ray ray)
        {
            ray = default;
            // Unity Ray normalizes direction. Reconstruct the common pinhole z=1 plane
            // before interpolation; off-center intrinsics make corner ray lengths unequal.
            float a = Vector3.Dot(bottomLeft.direction, lensForward), b = Vector3.Dot(bottomRight.direction, lensForward), c = Vector3.Dot(topLeft.direction, lensForward);
            if (!Finite(lensForward) || Mathf.Abs(lensForward.sqrMagnitude - 1) > .01f || !Finite(a) || !Finite(b) || !Finite(c) || a < .05f || b < .05f || c < .05f
                || Vector3.Distance(bottomLeft.origin, bottomRight.origin) > .001f || Vector3.Distance(bottomLeft.origin, topLeft.origin) > .001f) return false;
            var lower = bottomLeft.direction / a;
            var direction = lower + imagePoint.x * (bottomRight.direction / b - lower) + (1 - imagePoint.y) * (topLeft.direction / c - lower);
            if (!Finite(direction)) return false;
            ray = new Ray(bottomLeft.origin, direction); return true;
        }

        // Landmarks 11,12,23,24: left/right shoulders and left/right hips, in that order.
        // Operator sets an anterior surface; projected joints are a planar teaching approximation.
        public static bool TryFit(Vector3[] points, Vector3 anterior, out Fit fit)
        {
            fit = default;
            if (points == null || points.Length != 4 || Array.Exists(points, p => !Finite(p))) return false;
            var shoulders = (points[0] + points[1]) * 0.5f;
            var hips = (points[2] + points[3]) * 0.5f;
            float length = Vector3.Distance(shoulders, hips);
            float shoulderWidth = Vector3.Distance(points[0], points[1]);
            float hipWidth = Vector3.Distance(points[2], points[3]);
            if (length < 0.32f || length > 0.75f || shoulderWidth < 0.2f || shoulderWidth > 0.65f || hipWidth < 0.1f || hipWidth > 0.5f) return false;
            var cranial = (shoulders - hips).normalized;
            var left = Vector3.ProjectOnPlane(points[0] - points[1], cranial).normalized;
            // Labeled sides must agree with the independently calibrated anterior normal.
            if (Vector3.Dot(Vector3.Cross(cranial, left), anterior) < 0.85f
                || Vector3.Dot((points[2] - points[3]).normalized, left) < 0.85f) return false;
            fit.rotation = Quaternion.LookRotation(-anterior, cranial);
            fit.scale = length / (SourceShoulderHeight - SourceHipHeight);
            fit.position = hips - fit.rotation * new Vector3(0, SourceHipHeight, SourceFront) * fit.scale;
            fit.points = (Vector3[])points.Clone();
            // Preserve uniform source proportions. Never stretch each organ to arbitrary points.
            var source = new[] { new Vector3(0.167503f, SourceShoulderHeight, SourceFront), new Vector3(-0.167503f, SourceShoulderHeight, SourceFront),
                new Vector3(0.084307f, SourceHipHeight, SourceFront), new Vector3(-0.084307f, SourceHipHeight, SourceFront) };
            for (int i = 0; i < 4; i++)
                if (Vector3.Distance(fit.position + fit.rotation * source[i] * fit.scale, points[i]) > 0.09f) return false;
            return true;
        }

        public static bool Near(Fit a, Fit b, float translation = 0.035f, float degrees = 5f, float scale = 0.05f)
        {
            if (a.points == null || b.points == null || Quaternion.Angle(a.rotation, b.rotation) > degrees || Mathf.Abs(a.scale - b.scale) > scale) return false;
            for (int i = 0; i < 4; i++) if (Vector3.Distance(a.points[i], b.points[i]) > translation) return false;
            return true;
        }
    }
}
