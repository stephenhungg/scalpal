using System;
using UnityEngine;

namespace Scalpal.Brand.Editor
{
    /// <summary>
    /// Drives the production pointer path (ScalpalAim -> ScalpalPointerHand -> physics -> IScalpalPressable)
    /// with a simulated controller aim pose. It replaces only the device read; raycasts, colliders,
    /// resolvers, press gating and the button's real action all run as on the headset.
    /// </summary>
    public static class ScalpalPointerProbe
    {
        // Monotonic across probes so one probe's press never debounces the next.
        static float clock = 1000;
        public struct Result
        {
            public bool hovered, rayVisible, accentOnHover;
            public Vector3 lineStart, lineEnd;
            public Ray ray;
            public Vector3 expectedOrigin, expectedDirection;
            public ScalpalPointerHand pointer;
            public bool RayMatchesAim => (ray.origin - expectedOrigin).magnitude < 1e-4f && Vector3.Angle(ray.direction, expectedDirection) < .01f;
        }

        /// <summary>
        /// Aims <paramref name="hand"/> from a controller-like pose 0.55 m in front of the collider (slightly low and to that hand's side),
        /// releases, pulls the trigger, and releases again. <paramref name="step"/> must run the rig's pointer update once.
        /// </summary>
        public static Result Press(int hand, Transform trackingSpace, Collider target, Func<ScalpalPointerHand> pointer, Action step, ScalpalPointerKind kind = ScalpalPointerKind.Controller)
        {
            if (!target) throw new InvalidOperationException("Pointer probe target has no collider.");
            Physics.SyncTransforms();
            var center = target.bounds.center;
            // The visible face of every Scalpal panel is its -Z side.
            var toward = -target.transform.forward;
            var tipWorld = center + toward * .55f + Vector3.down * .12f + target.transform.right * (hand == 0 ? -.10f : .10f);
            var tip = trackingSpace.InverseTransformPoint(tipWorld);
            var aim = Quaternion.Inverse(trackingSpace.rotation) * Quaternion.LookRotation(center - tipWorld, Vector3.up);
            float select = 0;
            var previousOverride = ScalpalAim.Override; var previousClock = ScalpalAim.Clock;
            ScalpalAim.Clock = () => clock;
            ScalpalAim.Override = h => h == hand ? new ScalpalPointerSample { kind = kind, position = tip, rotation = aim, select = select } : default;
            var result = new Result { expectedOrigin = tipWorld, expectedDirection = (center - tipWorld).normalized };
            try
            {
                select = 0; clock += .2f; step();
                result.pointer = pointer();
                result.ray = result.pointer.LastRay;
                result.hovered = result.pointer.Hovered != null;
                var visual = result.pointer.Visual;
                result.rayVisible = visual != null && visual.Visible;
                if (result.rayVisible) { result.lineStart = visual.line.GetPosition(0); result.lineEnd = visual.line.GetPosition(1); result.accentOnHover = visual.Hovering && visual.line.startColor == ScalpalBrand.AccentOrange; }
                select = 1; clock += .2f; step();
                select = 0; clock += .2f; step();
            }
            finally { ScalpalAim.Override = previousOverride; ScalpalAim.Clock = previousClock; }
            return result;
        }
    }
}
