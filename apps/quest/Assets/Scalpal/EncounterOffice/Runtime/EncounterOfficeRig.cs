using System.Collections.Generic;
using Scalpal.Brand;
using Scalpal.Shell;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.EncounterOffice
{
    // Uses the native workbench's floor/head pose contract and existing Android OpenXR settings.
    // UI rays come from each controller's OpenXR aim pose (ScalpalAim), not the grip pose.
    [DefaultExecutionOrder(-200)]
    public sealed class EncounterOfficeRig : MonoBehaviour
    {
        public Transform origin;
        public Camera head;
        public Transform left, right;
        public NativeEncounterSession session;
        public TMPro.TextMeshPro talkHint;
        public Vector3 initialHeadFloorPosition = new Vector3(0, 0, 1.75f);
        public bool Ready { get; private set; }
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        bool aligned, tracked, focused = true, paused;
        readonly ScalpalPointerHand[] pointers = { new ScalpalPointerHand(0, "OfficePointerLeft"), new ScalpalPointerHand(1, "OfficePointerRight") };
        public ScalpalPointerHand Pointer(int hand) => pointers[hand];
        bool rightAnswerArmed, previousAnswerHold;
        XRNode talkHand;
        void OnEnable() => Application.onBeforeRender += RefreshPose;
        void OnDisable() { Application.onBeforeRender -= RefreshPose; Ready = false; ClearRays(); ClearTalk(); }
        void OnDestroy() { foreach (var pointer in pointers) pointer.Destroy(); }
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
            StepPointers();
            // Hold B (right controller) to say an answer. Its hint is the dialogue box's last line, not a controller label.
            ApplyTalkInput(Hold(XRNode.RightHand, ref rightAnswerArmed), XRNode.RightHand);
            if (talkHint) talkHint.gameObject.SetActive(false);
#if UNITY_EDITOR
            // Desktop component preview fallback, independent of physical XR evidence.
            if (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.wasPressedThisFrame && Camera.main)
            {
                var ray = Camera.main.ScreenPointToRay(UnityEngine.InputSystem.Mouse.current.position.ReadValue());
                if (Physics.Raycast(ray, out var hit, 8)) Target(hit.collider)?.Press();
            }
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && session)
            {
                if (keyboard.spaceKey.wasPressedThisFrame) session.SetTalkHeld(true);
                if (keyboard.spaceKey.wasReleasedThisFrame) session.SetTalkHeld(false);
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
        /// <summary>Aim-pose rays from both hands; trigger (or pinch) presses office buttons. Simulated aim stands in for readiness in validation.</summary>
        public void StepPointers()
        {
            bool ready = (Ready || ScalpalAim.Simulated) && origin;
            foreach (var pointer in pointers) pointer.Step(origin, ready, origin, Target);
        }
        // Ray targets: the picker's buttons and the dialogue box's interview choices.
        public static IScalpalPressable Target(Collider collider)
        {
            var button = collider.GetComponent<EncounterOfficeButton>(); if (button) return button;
            var choice = collider.GetComponent<DialogueChoice>(); return choice ? choice : null;
        }
        void ClearRays() { foreach (var pointer in pointers) pointer.Clear(); }
        bool Hold(XRNode node, ref bool armed)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            bool valid = device.isValid && device.TryGetFeatureValue(CommonUsages.isTracked, out bool trackedHand) && trackedHand;
            device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool pressed);
            if (!Ready || !valid) { armed = false; return false; }
            if (!pressed) armed = true;
            return armed && pressed;
        }
        void ApplyTalkInput(bool held, XRNode hand)
        {
            if (held == previousAnswerHold) return;
            previousAnswerHold = held;
            if (held) talkHand = hand;
            if (session) session.SetTalkHeld(held);
            var device = InputDevices.GetDeviceAtXRNode(talkHand);
            if (Ready && device.isValid && device.TryGetHapticCapabilities(out var capabilities) && capabilities.supportsImpulse)
                device.SendHapticImpulse(0, .18f, .04f);
        }
        void ClearTalk() { rightAnswerArmed = previousAnswerHold = false; if (session) session.SetTalkHeld(false); if (talkHint) talkHint.gameObject.SetActive(false); }
        void OnApplicationFocus(bool value) { focused = value; if (!value) { Ready = false; ClearRays(); ClearTalk(); } }
        void OnApplicationPause(bool value) { paused = value; if (value) { Ready = false; ClearRays(); ClearTalk(); } }
    }
}
