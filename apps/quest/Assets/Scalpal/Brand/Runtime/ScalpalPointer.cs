using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using InputDevice = UnityEngine.InputSystem.InputDevice;

namespace Scalpal.Brand
{
    /// <summary>Anything a pointer ray can focus and press: shell, office, handoff and recap buttons.</summary>
    public interface IScalpalPressable
    {
        bool Pressable { get; }
        void Hover(Vector3 point);
        void Press();
    }

    public enum ScalpalPointerKind { None, Controller, Hand }

    /// <summary>One hand's pointing pose in tracking space (the XR origin's local metres).</summary>
    public struct ScalpalPointerSample
    {
        public ScalpalPointerKind kind;
        public Vector3 position;
        public Quaternion rotation;
        public float select;
        public InputDevice device;
        public bool Valid => kind != ScalpalPointerKind.None;
    }

    /// <summary>
    /// Reads the OpenXR <b>aim</b> pose (pointerPosition/pointerRotation), never the grip pose.
    /// The grip pose sits in the palm with its forward axis down the handle, so a ray built from
    /// it starts below the view and points ~40 degrees low of where the controller is aimed.
    /// A tracked controller wins over hand interaction for the same hand.
    /// </summary>
    public static class ScalpalAim
    {
        /// <summary>Validation hook. When set, it replaces device sampling and stands in for XR readiness.</summary>
        public static Func<int, ScalpalPointerSample> Override;
        /// <summary>Validation clock; production uses unscaled time.</summary>
        public static Func<float> Clock;
        public static bool Simulated => Override != null;
        public static float Now => Clock != null ? Clock() : Time.unscaledTime;

        public static ScalpalPointerSample Read(int hand)
        {
            if (Override != null) return Override(hand);
            var usage = hand == 0 ? UnityEngine.InputSystem.CommonUsages.LeftHand : UnityEngine.InputSystem.CommonUsages.RightHand;
            ScalpalPointerSample handSample = default;
            var devices = InputSystem.devices;
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (!device.added || !device.enabled || !HasUsage(device, usage)) continue;
                if (device is HandInteractionProfile.HandInteraction handDevice)
                {
                    if (!handSample.Valid) handSample = ReadHand(handDevice);
                    continue;
                }
                if (!(device is XRController)) continue;
                var controller = ReadController(device);
                if (controller.Valid) return controller;
            }
            return handSample;
        }

        static bool HasUsage(InputDevice device, UnityEngine.InputSystem.Utilities.InternedString usage)
        {
            var usages = device.usages;
            for (int i = 0; i < usages.Count; i++) if (usages[i] == usage) return true;
            return false;
        }

        public static ScalpalPointerSample ReadController(InputDevice device)
        {
            var tracked = device.TryGetChildControl<ButtonControl>("isTracked");
            var position = device.TryGetChildControl<Vector3Control>("pointerPosition");
            var rotation = device.TryGetChildControl<QuaternionControl>("pointerRotation");
            var trigger = device.TryGetChildControl<AxisControl>("trigger");
            if (tracked == null || position == null || rotation == null || !tracked.isPressed) return default;
            if (!TryPose(position.ReadValue(), rotation.ReadValue(), out var p, out var q)) return default;
            return new ScalpalPointerSample { kind = ScalpalPointerKind.Controller, position = p, rotation = q, select = trigger != null ? trigger.ReadValue() : 0, device = device };
        }

        public static ScalpalPointerSample ReadHand(HandInteractionProfile.HandInteraction hand)
        {
            if (!hand.pointer.isTracked.isPressed || !hand.pinchReady.isPressed) return default;
            if (!TryPose(hand.pointerPosition.ReadValue(), hand.pointerRotation.ReadValue(), out var p, out var q)) return default;
            return new ScalpalPointerSample { kind = ScalpalPointerKind.Hand, position = p, rotation = q, select = hand.pinchValue.ReadValue(), device = hand };
        }

        // An unpopulated pose (all zero) is not a pose: it would draw a ray from the floor origin.
        static bool TryPose(Vector3 position, Quaternion rotation, out Vector3 p, out Quaternion q)
        {
            p = position; q = rotation;
            float magnitude = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            if (float.IsNaN(magnitude) || magnitude < .5f || float.IsNaN(position.x + position.y + position.z) || float.IsInfinity(position.x + position.y + position.z)) return false;
            if (position == Vector3.zero) return false;
            q = Quaternion.Normalize(rotation);
            return true;
        }
    }

    /// <summary>Analog select (trigger or pinch) to one deliberate press: release below .45, press above .75, 150 ms apart.</summary>
    public sealed class ScalpalPressGate
    {
        public const float PressThreshold = .75f, ReleaseThreshold = .45f, MinimumInterval = .15f;
        public bool Held { get; private set; } = true;
        bool armed;
        float lastPress = float.NegativeInfinity;
        public void Invalidate() { Held = true; armed = false; }
        public bool Sample(float value, bool valid, float now)
        {
            if (!valid || float.IsNaN(value) || float.IsInfinity(value)) { Invalidate(); return false; }
            if (value < ReleaseThreshold) { Held = false; armed = true; return false; }
            if (value <= PressThreshold || Held) return false;
            Held = true;
            bool press = armed && now - lastPress >= MinimumInterval;
            armed = false;
            if (press) lastPress = now;
            return press;
        }
    }

    /// <summary>A thin ray from the aim pose to the hit point, with a small reticle. Accent on hover.</summary>
    public sealed class ScalpalRayVisual
    {
        public const float StartWidth = .0022f, EndWidth = .0009f, MissLength = 2.5f;
        public readonly LineRenderer line;
        public readonly Transform reticle;
        readonly Renderer reticleRenderer;
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        public bool Visible => line && line.enabled;
        public bool Hovering { get; private set; }
        static Mesh ring;

        public ScalpalRayVisual(Transform parent, string name)
        {
            var brand = ScalpalBrand.Active;
            line = new GameObject(name).AddComponent<LineRenderer>();
            line.transform.SetParent(parent, false);
            line.sharedMaterial = brand.ray; line.useWorldSpace = true; line.positionCount = 2;
            line.startWidth = StartWidth; line.endWidth = EndWidth; line.numCapVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
            line.enabled = false;
            reticle = new GameObject(name + "Reticle", typeof(MeshFilter), typeof(MeshRenderer)).transform;
            reticle.SetParent(line.transform, false);
            reticle.GetComponent<MeshFilter>().sharedMesh = Ring();
            reticleRenderer = reticle.GetComponent<MeshRenderer>(); reticleRenderer.sharedMaterial = brand.ray;
            reticleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; reticleRenderer.receiveShadows = false;
            reticle.gameObject.SetActive(false);
            SetHover(false);
        }

        public void Show(Vector3 origin, Vector3 end, bool hit, Vector3 normal, bool hovering)
        {
            if (!line) return;
            line.enabled = true; line.SetPosition(0, origin); line.SetPosition(1, end);
            if (hovering != Hovering) SetHover(hovering);
            reticle.gameObject.SetActive(hit);
            if (!hit) return;
            var facing = normal.sqrMagnitude > .5f ? normal : (origin - end).normalized;
            if (Vector3.Dot(facing, origin - end) < 0) facing = -facing;
            reticle.SetPositionAndRotation(end + facing * .0015f, Quaternion.LookRotation(-facing));
            // Keep the ring about 0.45 degrees across at any distance (6 mm at 0.75 m).
            reticle.localScale = Vector3.one * Mathf.Clamp(Vector3.Distance(origin, end) * .008f, .004f, .02f);
        }

        public void Hide()
        {
            if (line) line.enabled = false;
            if (reticle) reticle.gameObject.SetActive(false);
        }

        void SetHover(bool hovering)
        {
            Hovering = hovering;
            var start = hovering ? ScalpalBrand.AccentOrange : new Color(1, 1, 1, .55f);
            var end = hovering ? ScalpalBrand.AccentGold : new Color(1, 1, 1, .12f);
            line.startColor = start; line.endColor = end;
            block.SetColor("_Color", hovering ? ScalpalBrand.AccentGold : new Color(1, 1, 1, .9f));
            reticleRenderer.SetPropertyBlock(block);
        }

        public void Destroy()
        {
            if (!line) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(line.gameObject); else UnityEngine.Object.DestroyImmediate(line.gameObject);
        }

        static Mesh Ring()
        {
            if (ring) return ring;
            const int segments = 24; const float outer = .5f, inner = .32f;
            var vertices = new Vector3[segments * 2]; var triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2 / segments;
                var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                vertices[i * 2] = direction * outer; vertices[i * 2 + 1] = direction * inner;
                int next = (i + 1) % segments, t = i * 6;
                triangles[t] = i * 2; triangles[t + 1] = next * 2; triangles[t + 2] = i * 2 + 1;
                triangles[t + 3] = i * 2 + 1; triangles[t + 4] = next * 2; triangles[t + 5] = next * 2 + 1;
            }
            ring = new Mesh { name = "ScalpalReticle", vertices = vertices, triangles = triangles, hideFlags = HideFlags.DontSave };
            ring.RecalculateBounds();
            return ring;
        }
    }

    /// <summary>
    /// One hand's UI pointer: aim pose to ray, physics hit to pressable, debounced press, haptics.
    /// Every panel rig (shell, office, handoff, recap) owns two of these.
    /// </summary>
    public sealed class ScalpalPointerHand
    {
        public const float MaximumDistance = 6, IdleSeconds = 10;
        public readonly int hand;
        public readonly ScalpalPressGate gate = new ScalpalPressGate();
        public ScalpalRayVisual Visual { get; private set; }
        public IScalpalPressable Hovered { get; private set; }
        public Ray LastRay { get; private set; }
        public ScalpalPointerSample LastSample { get; private set; }
        public bool Idle { get; private set; }
        /// <summary>The select input is held past the press threshold while hovering a target (continuous scrubbing).</summary>
        public bool HeldOnTarget { get; private set; }
        readonly string name;
        ScalpalPointerKind kind;
        Vector3 restPosition; Quaternion restRotation; float stillSince = float.NegativeInfinity;

        public ScalpalPointerHand(int hand, string name) { this.hand = hand; this.name = name; }

        /// <summary>Samples, raycasts and presses. Returns the pressed target, if any.</summary>
        public IScalpalPressable Step(Transform trackingSpace, bool permitted, Transform visualParent, Func<Collider, IScalpalPressable> resolve = null)
        {
            var sample = ScalpalAim.Read(hand);
            float now = ScalpalAim.Now;
            if (sample.kind != kind) { gate.Invalidate(); kind = sample.kind; stillSince = now; }
            if (!sample.Valid || !permitted || !trackingSpace) { Clear(); LastSample = sample; return null; }
            bool pressed = gate.Sample(sample.select, true, now);
            // Hide the ray of a controller that is set down: no motion and no input for IdleSeconds.
            if (Vector3.Distance(sample.position, restPosition) > .004f || Quaternion.Angle(sample.rotation, restRotation) > 1.5f || sample.select > .1f)
            { restPosition = sample.position; restRotation = sample.rotation; stillSince = now; }
            Idle = now - stillSince > IdleSeconds;
            LastSample = sample;
            var ray = new Ray(trackingSpace.TransformPoint(sample.position), trackingSpace.rotation * (sample.rotation * Vector3.forward));
            LastRay = ray;
            if (Idle) { HideVisual(); Hovered = null; HeldOnTarget = false; return null; }
            IScalpalPressable target = null;
            bool hit = Physics.Raycast(ray, out var info, MaximumDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hit) target = resolve != null ? resolve(info.collider) : info.collider.GetComponent<IScalpalPressable>();
            if (target != null && !target.Pressable) target = null;
            if (Visual == null || !Visual.line) Visual = new ScalpalRayVisual(visualParent, name);
            Visual.Show(ray.origin, hit ? info.point : ray.GetPoint(ScalpalRayVisual.MissLength), hit, hit ? info.normal : Vector3.zero, target != null);
            if (target != null) target.Hover(info.point);
            if (target != null && !ReferenceEquals(target, Hovered)) Haptic(sample, .15f, .025f);
            Hovered = target;
            HeldOnTarget = target != null && gate.Held && sample.select > ScalpalPressGate.PressThreshold;
            if (!pressed || target == null) return null;
            Haptic(sample, .3f, .04f);
            target.Press();
            return target;
        }

        public void Clear() { gate.Invalidate(); Hovered = null; HeldOnTarget = false; Idle = false; HideVisual(); }
        void HideVisual() { if (Visual != null) Visual.Hide(); }
        public void Destroy() { Visual?.Destroy(); Visual = null; }
        static void Haptic(ScalpalPointerSample sample, float amplitude, float duration)
        {
            if (Application.isPlaying && sample.kind == ScalpalPointerKind.Controller && sample.device is XRControllerWithRumble rumble) rumble.SendImpulse(amplitude, duration);
        }
    }

    /// <summary>Spawns panels level with the horizon: yaw-only facing the viewer, never pitched or rolled.</summary>
    public static class ScalpalPlacement
    {
        public static bool TryFlatForward(Transform head, out Vector3 forward)
        {
            forward = head ? Vector3.ProjectOnPlane(head.forward, Vector3.up) : Vector3.zero;
            // Looking straight up/down: derive yaw from the head's up vector instead of skipping.
            if (head && forward.sqrMagnitude < .01f) forward = Vector3.ProjectOnPlane(head.forward.y > 0 ? -head.up : head.up, Vector3.up);
            if (forward.sqrMagnitude < .0001f) return false;
            forward.Normalize();
            return true;
        }

        public static Quaternion Level(Vector3 forward) => Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, Vector3.up).normalized, Vector3.up);

        public static bool Place(Transform panel, Transform head, float distance, float drop)
        {
            if (!panel || !TryFlatForward(head, out var forward)) return false;
            panel.SetPositionAndRotation(head.position + forward * distance + Vector3.down * drop, Level(forward));
            return true;
        }

        public static bool IsLevel(Transform panel, float toleranceDegrees = .05f) => panel && Vector3.Angle(panel.up, Vector3.up) <= toleranceDegrees;
    }
}
