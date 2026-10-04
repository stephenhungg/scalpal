using System;
using System.Collections.Generic;
using Scalpal.Brand;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace Scalpal.Shell
{
    // Local XR meters -> origin world transform. Invalid tracking always requires a fresh release.
    // Rays come from each controller's OpenXR aim pose (or the hand-interaction pointer pose).
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
        readonly ScalpalPointerHand[] pointers = { new ScalpalPointerHand(0, "ShellPointerLeft"), new ScalpalPointerHand(1, "ShellPointerRight") };
        public ScalpalPointerHand Pointer(int hand) => pointers[hand];
        bool focused = true, suspended, placed;
        void OnEnable() { Application.onBeforeRender += RefreshHead; }
        void OnDisable()
        {
            Application.onBeforeRender -= RefreshHead;
            foreach (var system in subscribed) system.trackingOriginUpdated -= OriginUpdated;
            subscribed.Clear();
            Release(); Ready = false;
        }
        void OnDestroy() { foreach (var p in pointers) p.Destroy(); }
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
            StepPointers();
#if UNITY_EDITOR
            bool permitted = CanPoint(ShellTransition.Busy, ShellPause.Instance != null && ShellPause.Instance.IsPaused, allowedRoot);
            if (focused && !suspended && permitted)
            {
                if (Mouse.current != null)
                {
                    var ray = head.ScreenPointToRay(Mouse.current.position.ReadValue());
                    var button = Physics.Raycast(ray, out var hit, ScalpalPointerHand.MaximumDistance) ? Resolve(hit.collider) as ShellButton : null;
                    if (button) button.Highlight();
                    if (Mouse.current.leftButton.wasPressedThisFrame) button?.Press();
                }
                if (!allowedRoot && Keyboard.current != null && Keyboard.current.enterKey.wasPressedThisFrame) Confirm?.Invoke();
            }
#endif
        }
        /// <summary>Both hands: aim pose ray, hover, trigger/pinch press. Simulated aim stands in for XR readiness in validation.</summary>
        public void StepPointers()
        {
            bool permitted = CanPoint(ShellTransition.Busy, ShellPause.Instance != null && ShellPause.Instance.IsPaused, allowedRoot);
            bool ready = (Ready || ScalpalAim.Simulated) && permitted && origin;
            IsHands = false;
            foreach (var p in pointers)
            {
                p.Step(origin, ready, transform, Resolve);
                IsHands |= p.LastSample.kind == ScalpalPointerKind.Hand;
            }
        }
        IScalpalPressable Resolve(Collider collider)
        {
            var button = collider.GetComponent<ShellButton>();
            return button && (!allowedRoot || button.transform.IsChildOf(allowedRoot)) ? button : null;
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
        public static bool CanPoint(bool transitionBusy, bool paused, bool pauseRoot) => pauseRoot ? paused : !transitionBusy && !paused;
        public void Release() { foreach (var p in pointers) p.Clear(); }
        public void Recenter()
        {
            if (!head || !content) return;
            // Level with the horizon: yaw-only, whatever the head's pitch or roll at the moment of placement.
            if (!ScalpalPlacement.TryFlatForward(head.transform, out var forward)) return;
            content.SetPositionAndRotation(head.transform.position + Vector3.down * .08f, ScalpalPlacement.Level(forward));
            Release();
        }
        void OriginUpdated(XRInputSubsystem system) { if (placed && Ready) Recenter(); }
        void OnApplicationFocus(bool value) { focused = value; if (!value) Release(); }
        void OnApplicationPause(bool value) { suspended = value; if (value) Release(); }
    }
}
