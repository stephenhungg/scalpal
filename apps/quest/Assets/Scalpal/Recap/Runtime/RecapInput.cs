using System.Collections.Generic;
using Scalpal.Brand;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace Scalpal.Recap
{
    // Independent scene input. The shell can disable this rig and retain its shared XR origin.
    // Rays come from each controller's OpenXR aim pose (ScalpalAim); grip transforms are kept for the head/hands only.
    public sealed class RecapInput : MonoBehaviour
    {
        public Transform origin, head, left, right;
        public RecapController controller;
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        readonly ScalpalPointerHand[] pointers = { new ScalpalPointerHand(0, "RecapPointerLeft"), new ScalpalPointerHand(1, "RecapPointerRight") };
        public ScalpalPointerHand Pointer(int hand) => pointers[hand];
        bool focused = true, paused;
        void OnEnable() => Application.onBeforeRender += Pose;
        void OnDisable() { Application.onBeforeRender -= Pose; foreach (var pointer in pointers) pointer.Clear(); controller.SetTalkHeld(false); }
        void OnDestroy() { foreach (var pointer in pointers) pointer.Destroy(); }
        void Pose() { Track(XRNode.Head, head); Track(XRNode.LeftHand, left); Track(XRNode.RightHand, right); }
        static bool Track(XRNode node, Transform target)
        {
            var d = InputDevices.GetDeviceAtXRNode(node);
            if (!target || !d.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) || !tracked || !d.TryGetFeatureValue(CommonUsages.devicePosition, out var p) || !d.TryGetFeatureValue(CommonUsages.deviceRotation, out var q)) return false;
            target.SetLocalPositionAndRotation(p,q); return true;
        }
        void Update()
        {
            SubsystemManager.GetSubsystems(inputs);
            foreach (var input in inputs) if (input.running && input.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            Pose();
            StepPointers();
            controller.SetTalkHeld(focused && !paused && (Grip(XRNode.LeftHand) || Grip(XRNode.RightHand)));
#if UNITY_EDITOR
            if (Mouse.current?.leftButton.wasPressedThisFrame == true && Camera.main)
            {
                var ray=Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
                if(Physics.Raycast(ray,out var hit,8)) { var b=hit.collider.GetComponent<RecapButton>(); b?.Highlight(hit.point); b?.Press(); }
            }
            if (Keyboard.current?.spaceKey.isPressed == true) controller.SetTalkHeld(true);
#endif
        }
        /// <summary>Aim-pose rays; trigger/pinch press once, and the clip scrubber follows a held trigger.</summary>
        public void StepPointers()
        {
            bool ready = focused && !paused && origin;
            foreach (var pointer in pointers)
            {
                var pressed = pointer.Step(origin, ready, origin, collider => collider.GetComponent<RecapButton>());
                if (pressed == null && pointer.HeldOnTarget && pointer.Hovered is RecapButton held && held.action == "scrub") held.Press();
            }
        }
        bool Grip(XRNode node) { var d=InputDevices.GetDeviceAtXRNode(node); return d.TryGetFeatureValue(CommonUsages.isTracked,out var tracked)&&tracked&&d.TryGetFeatureValue(CommonUsages.gripButton,out var grip)&&grip; }
        void OnApplicationFocus(bool value){focused=value;if(!value){foreach(var pointer in pointers)pointer.Clear();controller.SetTalkHeld(false);}}
        void OnApplicationPause(bool value){paused=value;if(value){foreach(var pointer in pointers)pointer.Clear();controller.SetTalkHeld(false);}}
    }
}
