using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.EncounterOffice
{
    // Uses the native workbench's proven floor/head/controller pose contract and existing Android OpenXR settings.
    [DefaultExecutionOrder(-200)]
    public sealed class EncounterOfficeRig : MonoBehaviour
    {
        public Transform origin;
        public Camera head;
        public Transform left, right;
        public LineRenderer leftRay, rightRay;
        public Vector3 initialHeadFloorPosition = new Vector3(0, 0, 1.75f);
        public bool Ready { get; private set; }
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        bool aligned, tracked, focused = true, paused;
        bool leftDown, rightDown, leftArmed, rightArmed;
        void OnEnable() => Application.onBeforeRender += RefreshPose;
        void OnDisable() { Application.onBeforeRender -= RefreshPose; Ready = false; ClearRays(); }
        void Update()
        {
            SubsystemManager.GetSubsystems(displays); SubsystemManager.GetSubsystems(inputs);
            bool floor = false;
            foreach (var input in inputs)
            {
                if (!input.running) continue;
                if (input.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                floor |= input.GetTrackingOriginMode() == TrackingOriginModeFlags.Floor;
            }
            RefreshPose();
            if (!aligned && tracked && floor && displays.Exists(display => display.running))
            {
                var yaw = Quaternion.Euler(0, 180 - head.transform.localEulerAngles.y, 0);
                origin.rotation = yaw;
                var local = head.transform.localPosition;
                origin.position = initialHeadFloorPosition - yaw * new Vector3(local.x, 0, local.z);
                aligned = true;
            }
            Ready = aligned && tracked && floor && displays.Exists(display => display.running) && focused && !paused;
            Point(XRNode.LeftHand, left, leftRay, ref leftDown, ref leftArmed);
            Point(XRNode.RightHand, right, rightRay, ref rightDown, ref rightArmed);
#if UNITY_EDITOR
            // Desktop component preview fallback, independent of physical XR evidence.
            if (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame && Camera.main)
            {
                var ray = Camera.main.ScreenPointToRay(UnityEngine.InputSystem.Mouse.current.position.ReadValue());
                if (Physics.Raycast(ray, out var hit, 8)) hit.collider.GetComponent<EncounterOfficeButton>()?.Press();
            }
#endif
        }
        [BeforeRenderOrder(-200)]
        void RefreshPose()
        {
            tracked = Pose(XRNode.Head, head ? head.transform : null);
            Pose(XRNode.LeftHand, left); Pose(XRNode.RightHand, right);
        }
        static bool Pose(XRNode node, Transform target)
        {
            if (!target) return false;
            var device = InputDevices.GetDeviceAtXRNode(node);
            bool valid = device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out var position) && device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation);
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked)) valid &= isTracked;
            if (!valid) return false;
            device.TryGetFeatureValue(CommonUsages.devicePosition, out position); device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            target.SetLocalPositionAndRotation(position, rotation); return true;
        }
        void Point(XRNode node, Transform hand, LineRenderer line, ref bool previous, ref bool armed)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            bool handTracked = device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) && isTracked;
            device.TryGetFeatureValue(CommonUsages.triggerButton, out bool pressed);
            if (!Ready || !handTracked) { armed = false; previous = pressed; if (line) line.enabled = false; return; }
            if (!pressed) armed = true;
            var direction = hand.forward;
            // Touch grip poses point their local forward axis along the controller ray.
            bool hit = Physics.Raycast(hand.position, direction, out var target, 6);
            if (line) { line.enabled = true; line.SetPosition(0, hand.position); line.SetPosition(1, hit ? target.point : hand.position + direction * 3); }
            var button = hit ? target.collider.GetComponent<EncounterOfficeButton>() : null;
            if (button) button.Highlight();
            if (armed && pressed && !previous) button?.Press();
            previous = pressed;
        }
        void ClearRays() { if (leftRay) leftRay.enabled = false; if (rightRay) rightRay.enabled = false; leftArmed = rightArmed = false; }
        void OnApplicationFocus(bool value) { focused = value; if (!value) { Ready = false; ClearRays(); } }
        void OnApplicationPause(bool value) { paused = value; if (value) { Ready = false; ClearRays(); } }
    }
}
