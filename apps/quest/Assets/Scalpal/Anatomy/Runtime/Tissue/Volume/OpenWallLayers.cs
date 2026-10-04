using System;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Case-independent teaching geometry, in wound-local SI coordinates (+Z inward).
    // These depths and fiber axes are authored, not reconstructed patient anatomy.
    public static class OpenWallLayers
    {
        public const int Count = 5;
        public const string Provenance = "authored open-wall teaching geometry; uncalibrated, not participant-specific";
        public const string LandmarkProvenance = "authored one-third right-ASIS-to-umbilicus interpolation from explicit SI landmarks; no inferred landmarks";

        public readonly struct Layer
        {
            public readonly int index;
            public readonly string id;
            public readonly float startDepthMeters, endDepthMeters;
            public readonly Vector3 fiberDirection;
            public readonly bool hasFibers, cuttable, splittable, tentable;
            public string provenance => Provenance;
            public float ThicknessMeters => endDepthMeters - startDepthMeters;

            internal Layer(int index, string id, float start, float end, bool fibers, bool split, bool tent)
            {
                this.index = index; this.id = id;
                startDepthMeters = start; endDepthMeters = end;
                hasFibers = fibers; fiberDirection = fibers ? Vector3.right : Vector3.zero;
                // Cutting muscle is physically possible; the body-state owner grades that injury.
                cuttable = true; splittable = split; tentable = tent;
            }
        }

        // Fascia follows the existing +X incision reference. Muscle splitting opens across it (+Y).
        // One authored muscle layer stands for multiple muscles; this is not an anatomical fiber atlas.
        static readonly Layer[] layers = {
            new Layer(0, "skin",       0f,    .002f, false, false, false),
            new Layer(1, "fat",        .002f, .014f, false, false, false),
            new Layer(2, "fascia",     .014f, .019f, true,  false, false),
            new Layer(3, "muscle",     .019f, .027f, true,  true,  false),
            new Layer(4, "peritoneum", .027f, .028f, false, false, true)
        };

        public static Layer Get(int index)
        {
            if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            return layers[index];
        }

        // Exact stable IDs only: unknown or differently cased IDs cannot become a default layer.
        public static bool TryGet(string id, out Layer layer)
        {
            for (int i = 0; i < Count; i++)
                if (string.Equals(layers[i].id, id, StringComparison.Ordinal))
                { layer = layers[i]; return true; }
            layer = default; return false;
        }

        // Depth is undeformed authored wound depth, not a query against the current tissue mesh.
        // Interfaces belong to the deeper layer; only the last layer includes its inward boundary.
        public static bool TryAtDepth(float depthMeters, out Layer layer)
        {
            layer = default;
            if (!Finite(depthMeters)) return false;
            for (int i = 0; i < Count; i++)
            {
                var candidate = layers[i];
                if (depthMeters >= candidate.startDepthMeters &&
                    (depthMeters < candidate.endDepthMeters || i == Count - 1 && depthMeters == candidate.endDepthMeters))
                { layer = candidate; return true; }
            }
            return false;
        }

        // Unsigned in-plane axis angle: either direction along a fiber is equivalent (0..90 deg).
        // A normal-only, zero, nonfinite or un-fibered direction has no angle, rather than angle zero.
        // This is geometric evidence only; it does not apply a case threshold or alter body state.
        public static bool TryFiberAngle(string id, Vector3 localDirection, out float degrees)
        {
            degrees = float.NaN;
            if (!TryGet(id, out var layer) || !layer.hasFibers || !Finite(localDirection)) return false;
            float scale = Mathf.Max(Mathf.Abs(localDirection.x), Mathf.Abs(localDirection.y));
            if (scale <= 0) return false;
            // Scaling before normalization avoids overflow for otherwise finite coordinates.
            var planar = new Vector3(localDirection.x / scale, localDirection.y / scale, 0).normalized;
            float cosine = Mathf.Clamp01(Mathf.Abs(Vector3.Dot(planar, layer.fiberDirection)));
            degrees = Mathf.Acos(cosine) * Mathf.Rad2Deg;
            return Finite(degrees);
        }

        // Both inputs must already be metre positions in the same explicitly chosen frame.
        // Caller owns their source/registration quality. No camera/depth landmarks are fabricated.
        public static bool TryMcBurney(Vector3 rightAsisMeters, Vector3 umbilicusMeters, out Vector3 point)
        {
            point = default;
            if (!Finite(rightAsisMeters) || !Finite(umbilicusMeters)) return false;
            var difference = umbilicusMeters - rightAsisMeters;
            if (!Finite(difference) || difference.sqrMagnitude < 1e-12f) return false;
            var result = rightAsisMeters + difference / 3f;
            if (!Finite(result)) return false;
            point = result; return true;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
