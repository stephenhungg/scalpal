using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace Scalpal.Shell
{
    // Local XR meters -> origin world transform. Invalid tracking always requires a fresh release.
    [DefaultExecutionOrder(-210)]
    public sealed class ShellInput : MonoBehaviour
    {
        public Camera head;
        public Transform origin, content;
        public Transform allowedRoot;
        public bool driveHead = true;
        public Action Confirm;
        public bool IsHands { get; private set; }
        public bool Ready { get; private set; }
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        readonly List<XRInputSubsystem> subscribed = new List<XRInputSubsystem>();
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        readonly PointerState[] pointers = { new PointerState(), new PointerState() };
        bool focused = true, suspended, placed;
        Material rayMaterial;
        sealed class PointerState
        {
            public InputAction position, rotation, tracked, pinch, pinchReady;
            public bool down, armed, hands;
            public ShellButton hover;
            public LineRenderer line;
        }
        void OnEnable()
        {
            for (int i = 0; i < 2; i++)
            {
                var p = pointers[i]; string prefix = "<HandInteraction>{" + (i == 0 ? "LeftHand" : "RightHand") + "}/";
                p.position = Action(prefix + "pointerPosition"); p.rotation = Action(prefix + "pointerRotation");
                p.tracked = Action(prefix + "pointer/isTracked"); p.pinch = Action(prefix + "pinchValue"); p.pinchReady = Action(prefix + "pinchReady");
            }
            Application.onBeforeRender += RefreshHead;
        }
        static InputAction Action(string binding) { var action = new InputAction(binding: binding); action.Enable(); return action; }
        void OnDisable()
        {
            Application.onBeforeRender -= RefreshHead;
            foreach (var system in subscribed) system.trackingOriginUpdated -= OriginUpdated;
            subscribed.Clear();
            foreach (var p in pointers) { p.position?.Dispose(); p.rotation?.Dispose(); p.tracked?.Dispose(); p.pinch?.Dispose(); p.pinchReady?.Dispose(); }
            Release(); Ready = false;
        }
        void OnDestroy() { if (rayMaterial) Destroy(rayMaterial); }
        void Update()
        {
            if (!head) return;
            SubsystemManager.GetSubsystems(inputs); SubsystemManager.GetSubsystems(displays);
            bool floor = false;
            foreach (var system in inputs)
            {
                if (!subscribed.Contains(system)) { subscribed.Add(system); system.trackingOriginUpdated += OriginUpdated; }
                if (!system.running) continue;
                if (system.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor) system.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                floor |= system.GetTrackingOriginMode() == TrackingOriginModeFlags.Floor;
            }
            RefreshHead();
            var device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool tracked = device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked) && isTracked;
            Ready = focused && !suspended && tracked && floor && displays.Exists(d => d.running);
            if (Ready && !placed && content) { Recenter(); placed = true; }
            IsHands = false;
            bool permitted = (!ShellTransition.Busy || allowedRoot) && (ShellPause.Instance == null || !ShellPause.Instance.IsPaused || allowedRoot);
            Point(0, permitted); Point(1, permitted);
#if UNITY_EDITOR
            if (focused && !suspended && permitted)
            {
                if (Mouse.current != null)
                {
                    var ray = head.ScreenPointToRay(Mouse.current.position.ReadValue());
                    var button = Hit(ray, out _);
                    if (button) button.Highlight();
                    if (Mouse.current.leftButton.wasPressedThisFrame) button?.Press();
                }
                if (!allowedRoot && Keyboard.current != null && Keyboard.current.enterKey.wasPressedThisFrame) Confirm?.Invoke();
            }
#endif
        }
        [BeforeRenderOrder(-210)]
        void RefreshHead()
        {
            if (!driveHead || !head) return;
            var device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked &&
                device.TryGetFeatureValue(CommonUsages.devicePosition, out var position) && device.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation))
                head.transform.SetLocalPositionAndRotation(position, rotation);
        }
        void Point(int index, bool permitted)
        {
            var p = pointers[index]; var device = InputDevices.GetDeviceAtXRNode(index == 0 ? XRNode.LeftHand : XRNode.RightHand);
            bool hands = p.tracked.ReadValue<float>() > .5f && p.pinchReady.ReadValue<float>() > .5f;
            bool valid; bool down; Vector3 position = Vector3.zero; Quaternion rotation = Quaternion.identity;
            if (hands) { valid = true; position = p.position.ReadValue<Vector3>(); rotation = p.rotation.ReadValue<Quaternion>(); down = p.pinch.ReadValue<float>() > .75f; }
            else
            {
                valid = device.isValid && (device.characteristics & InputDeviceCharacteristics.Controller) != 0 && device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
                valid &= device.TryGetFeatureValue(CommonUsages.devicePosition, out position) && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
                device.TryGetFeatureValue(CommonUsages.triggerButton, out down);
            }
            IsHands |= hands;
            if (p.hands != hands) { p.armed = false; p.down = down; p.hands = hands; }
            if (!Ready || !valid || !permitted || !origin) { p.armed = false; p.down = down; p.hover = null; if (p.line) p.line.enabled = false; return; }
            if (!down) p.armed = true;
            var ray = new Ray(origin.TransformPoint(position), origin.rotation * rotation * Vector3.forward);
            var button = Hit(ray, out var point);
            if (!p.line) p.line = CreateRay(index);
            p.line.enabled = true; p.line.SetPosition(0, ray.origin); p.line.SetPosition(1, point);
            if (button) button.Highlight();
            if (button != p.hover && button && button.interactable && !hands) device.SendHapticImpulse(0, .15f, .025f);
            p.hover = button;
            if (p.armed && down && !p.down && button) { button.Press(); if (!hands) device.SendHapticImpulse(0, .3f, .04f); }
            p.down = down;
        }
        ShellButton Hit(Ray ray, out Vector3 point)
        {
            point = ray.GetPoint(3);
            if (!Physics.Raycast(ray, out var hit, 6)) return null;
            point = hit.point; var button = hit.collider.GetComponent<ShellButton>();
            return button && (!allowedRoot || button.transform.IsChildOf(allowedRoot)) ? button : null;
        }
        LineRenderer CreateRay(int index)
        {
            var line = new GameObject("ShellPointer" + index).AddComponent<LineRenderer>(); line.transform.SetParent(transform, false);
            if (!rayMaterial) rayMaterial = new Material(Shader.Find("Scalpal/Encounter Office/Glass")) { color = new Color(.8f,.73f,.98f,.8f) };
            line.sharedMaterial = rayMaterial; line.useWorldSpace = true; line.positionCount = 2;
            line.startWidth = .0015f; line.endWidth = .0007f; line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
            return line;
        }
        public void Release() { foreach (var p in pointers) { p.armed = false; p.down = true; p.hover = null; if (p.line) p.line.enabled = false; } }
        public void Recenter()
        {
            if (!head || !content) return;
            var forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .5f) return;
            content.SetPositionAndRotation(head.transform.position + Vector3.down * .08f, Quaternion.LookRotation(forward));
            Release();
        }
        void OriginUpdated(XRInputSubsystem system) { Recenter(); }
        void OnApplicationFocus(bool value) { focused = value; if (!value) Release(); }
        void OnApplicationPause(bool value) { suspended = value; if (value) Release(); }
    }
}
