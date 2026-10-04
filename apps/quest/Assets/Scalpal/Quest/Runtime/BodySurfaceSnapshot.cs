using UnityEngine;

namespace Scalpal.Quest
{
    // Ephemeral acquisition-time native raycasts, indexed by top-left image coordinates.
    // No scene mesh, MediaPipe z, or response-time head pose supplies depth.
    public sealed class BodySurfaceSnapshot
    {
        public const int Width = 17, Height = 13;
        public readonly Vector3[] points = new Vector3[Width * Height];
        public readonly Vector3[] normals = new Vector3[Width * Height];
        public readonly bool[] valid = new bool[Width * Height];
        public Ray bottomLeft, bottomRight, topLeft;
        public Vector3 lensForward;

        const int ProbeCount = 8, MinimumConsensus = 6;
        const float PlaneTolerance = .065f;
        readonly Vector3[] torsoPoints = new Vector3[ProbeCount];
        readonly Vector3[] torsoNormals = new Vector3[ProbeCount];
        readonly bool[] torsoValid = new bool[ProbeCount];
        static readonly float[] Bands = { .12f, .28f, .72f, .88f };

        public bool Sample(Vector2 image, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            if (!BodyRegistrationMath.Finite(image.x) || !BodyRegistrationMath.Finite(image.y)
                || image.x < 0 || image.x > 1 || image.y < 0 || image.y > 1) return false;
            float gx = image.x * (Width - 1), gy = image.y * (Height - 1);
            int x = Mathf.Min((int)gx, Width - 2), y = Mathf.Min((int)gy, Height - 2);
            int a = y * Width + x, b = a + 1, c = a + Width, d = c + 1;
            if (!ValidHit(a) || !ValidHit(b) || !ValidHit(c) || !ValidHit(d)) return false;
            normal = (normals[a] + normals[b] + normals[c] + normals[d]).normalized;
            if (Vector3.Dot(normal, Vector3.up) < .7f) return false;
            Vector3 center = (points[a] + points[b] + points[c] + points[d]) * .25f;
            if (!SameCell(a, normal, center) || !SameCell(b, normal, center)
                || !SameCell(c, normal, center) || !SameCell(d, normal, center))
                return false; // Never blend across a body/table/background discontinuity.
            if (!BodyRegistrationMath.ImageRay(bottomLeft, bottomRight, topLeft, lensForward, image, out var ray)) return false;
            return BodyRegistrationMath.Intersect(ray, new Plane(normal, center), center, out point);
        }

        bool ValidHit(int i) => valid[i] && BodyRegistrationMath.Finite(points[i])
            && BodyRegistrationMath.Finite(normals[i]) && Mathf.Abs(normals[i].sqrMagnitude - 1) <= .01f;
        bool SameCell(int i, Vector3 normal, Vector3 center) => Vector3.Dot(normals[i], normal) >= .85f
            && Mathf.Abs(Vector3.Dot(points[i] - center, normal)) <= .05f;

        public bool TryTorsoPlane(Vector2[] landmarks, out Plane plane, out Vector3 center)
        {
            plane = default; center = default;
            if (landmarks == null || landmarks.Length != 4) return false;
            foreach (var landmark in landmarks)
                if (!BodyRegistrationMath.Finite(landmark.x) || !BodyRegistrationMath.Finite(landmark.y)
                    || landmark.x < 0 || landmark.x > 1 || landmark.y < 0 || landmark.y > 1) return false;

            // Labeled shoulders 0/1 and hips 2/3 bound the torso. Probe the interior
            // chest/hip flanks rather than the working abdomen, where tools/hands occlude
            // depth. These are measured surface rays, not MediaPipe metric joint depths.
            for (int band = 0; band < Bands.Length; band++)
            {
                var left = Vector2.Lerp(landmarks[0], landmarks[2], Bands[band]);
                var right = Vector2.Lerp(landmarks[1], landmarks[3], Bands[band]);
                for (int side = 0; side < 2; side++)
                {
                    int i = band * 2 + side;
                    torsoValid[i] = Sample(Vector2.Lerp(left, right, side == 0 ? .18f : .82f),
                        out torsoPoints[i], out torsoNormals[i]);
                }
            }

            int bestMask = 0, bestCount = 0; float bestResidual = float.PositiveInfinity;
            for (int seed = 0; seed < ProbeCount; seed++)
            {
                if (!torsoValid[seed]) continue;
                int mask = 0, count = 0; float residual = 0;
                for (int i = 0; i < ProbeCount; i++)
                {
                    if (!torsoValid[i] || Vector3.Dot(torsoNormals[seed], torsoNormals[i]) < .85f) continue;
                    float distance = Mathf.Abs(Vector3.Dot(torsoPoints[i] - torsoPoints[seed], torsoNormals[seed]));
                    if (distance > PlaneTolerance) continue;
                    mask |= 1 << i; count++; residual += distance;
                }
                // Coverage in all four torso quadrants prevents a single exposed patch
                // (or a coherent foreground hand covering one band) supplying the fit.
                if (count < MinimumConsensus || !SpansTorso(mask)) continue;
                if (count > bestCount || (count == bestCount && residual < bestResidual))
                { bestMask = mask; bestCount = count; bestResidual = residual; }
            }
            if (bestCount < MinimumConsensus) return false;
            Vector3 normal = default;
            for (int i = 0; i < ProbeCount; i++)
                if ((bestMask & (1 << i)) != 0) { center += torsoPoints[i] / bestCount; normal += torsoNormals[i]; }
            normal.Normalize();
            if (Vector3.Dot(normal, Vector3.up) < .7f || Vector3.Dot(normal, bottomLeft.origin - center) <= .1f) return false;
            for (int i = 0; i < ProbeCount; i++)
                if ((bestMask & (1 << i)) != 0 && (Vector3.Dot(normal, torsoNormals[i]) < .85f
                    || Mathf.Abs(Vector3.Dot(torsoPoints[i] - center, normal)) > PlaneTolerance)) return false;
            plane = new Plane(normal, center);
            return true;
        }

        static bool SpansTorso(int mask) => (mask & 0x05) != 0 && (mask & 0x0A) != 0
            && (mask & 0x50) != 0 && (mask & 0xA0) != 0;
    }
}
