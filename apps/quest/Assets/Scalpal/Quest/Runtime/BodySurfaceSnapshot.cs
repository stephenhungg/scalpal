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

        public bool Sample(Vector2 image, out Vector3 point, out Vector3 normal)
        {
            point = normal = default;
            if (!BodyRegistrationMath.Finite(image.x) || !BodyRegistrationMath.Finite(image.y)
                || image.x < 0 || image.x > 1 || image.y < 0 || image.y > 1) return false;
            float gx = image.x * (Width - 1), gy = image.y * (Height - 1);
            int x = Mathf.Min((int)gx, Width - 2), y = Mathf.Min((int)gy, Height - 2);
            int a = y * Width + x, b = a + 1, c = a + Width, d = c + 1;
            int[] indices = { a, b, c, d };
            foreach (int i in indices)
                if (!valid[i] || !BodyRegistrationMath.Finite(points[i]) || !BodyRegistrationMath.Finite(normals[i])
                    || Mathf.Abs(normals[i].sqrMagnitude - 1) > .01f) return false;
            normal = (normals[a] + normals[b] + normals[c] + normals[d]).normalized;
            if (Vector3.Dot(normal, Vector3.up) < .7f) return false;
            Vector3 center = (points[a] + points[b] + points[c] + points[d]) * .25f;
            foreach (int i in indices)
                if (Vector3.Dot(normals[i], normal) < .85f || Mathf.Abs(Vector3.Dot(points[i] - center, normal)) > .05f)
                    return false; // Never blend across a body/table/background discontinuity.
            if (!BodyRegistrationMath.ImageRay(bottomLeft, bottomRight, topLeft, lensForward, image, out var ray)) return false;
            return BodyRegistrationMath.Intersect(ray, new Plane(normal, center), center, out point);
        }

        public bool TryTorsoPlane(Vector2[] landmarks, out Plane plane, out Vector3 center)
        {
            plane = default; center = default;
            if (landmarks == null || landmarks.Length != 4) return false;
            Vector2 middle = (landmarks[0] + landmarks[1] + landmarks[2] + landmarks[3]) * .25f;
            var samples = new Vector3[5]; var directions = new Vector3[5];
            // Move inward from silhouette joints so a neighboring grid cell does not use the table.
            for (int i = 0; i < 5; i++)
            {
                Vector2 image = i == 4 ? middle : Vector2.Lerp(landmarks[i], middle, .3f);
                if (!Sample(image, out samples[i], out directions[i])) return false;
                center += samples[i] * .2f;
            }
            Vector3 normal = (directions[0] + directions[1] + directions[2] + directions[3] + directions[4]).normalized;
            if (Vector3.Dot(normal, Vector3.up) < .7f || Vector3.Dot(normal, bottomLeft.origin - center) <= .1f) return false;
            for (int i = 0; i < 5; i++)
                if (Vector3.Dot(normal, directions[i]) < .85f || Mathf.Abs(Vector3.Dot(samples[i] - center, normal)) > .065f)
                    return false;
            plane = new Plane(normal, center);
            return true;
        }
    }
}
