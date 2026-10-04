using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;

namespace Scalpal.Recap
{
    // Independent scene input. The shell can disable this rig and retain its shared XR origin.
    public sealed class RecapInput : MonoBehaviour
    {
        public Transform origin, head, left, right;
        public LineRenderer leftRay, rightRay;
        public RecapController controller;
        readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
        bool leftPressed, rightPressed, leftArmed, rightArmed, focused = true, paused;
        void OnEnable() => Application.onBeforeRender += Pose;
        void OnDisable() { Application.onBeforeRender -= Pose; controller.SetTalkHeld(false); }
        void Pose() { Track(XRNode.Head, head); Track(XRNode.LeftHand, left); Track(XRNode.RightHand, right); }
        static bool Track(XRNode node, Transform target)
        {
            var d = InputDevices.GetDeviceAtXRNode(node);
            if (!d.TryGetFeatureValue(CommonUsages.isTracked, out var tracked) || !tracked || !d.TryGetFeatureValue(CommonUsages.devicePosition, out var p) || !d.TryGetFeatureValue(CommonUsages.deviceRotation, out var q)) return false;
            target.SetLocalPositionAndRotation(p,q); return true;
        }
        void Update()
        {
            SubsystemManager.GetSubsystems(inputs);
            foreach (var input in inputs) if (input.running && input.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor) input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            Pose();
            Point(XRNode.LeftHand,left,leftRay,ref leftPressed,ref leftArmed); Point(XRNode.RightHand,right,rightRay,ref rightPressed,ref rightArmed);
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
        bool Grip(XRNode node) { var d=InputDevices.GetDeviceAtXRNode(node); return d.TryGetFeatureValue(CommonUsages.isTracked,out var tracked)&&tracked&&d.TryGetFeatureValue(CommonUsages.gripButton,out var grip)&&grip; }
        void Point(XRNode node,Transform hand,LineRenderer line,ref bool previous,ref bool armed)
        {
            var d=InputDevices.GetDeviceAtXRNode(node); d.TryGetFeatureValue(CommonUsages.triggerButton,out var pressed);
            bool valid=focused&&!paused&&Track(node,hand);
            if(!valid){line.enabled=false;armed=false;previous=pressed;return;}
            if(!pressed)armed=true;
            bool hit=Physics.Raycast(hand.position,hand.forward,out var target,8);
            line.enabled=true;line.SetPosition(0,hand.position);line.SetPosition(1,hit?target.point:hand.position+hand.forward*3);
            var button=hit?target.collider.GetComponent<RecapButton>():null;
            if(button)button.Highlight(target.point);if(armed&&pressed&&(!previous||button?.action=="scrub"))button?.Press();previous=pressed;
        }
        void OnApplicationFocus(bool value){focused=value;if(!value)controller.SetTalkHeld(false);}
        void OnApplicationPause(bool value){paused=value;if(value)controller.SetTalkHeld(false);}
    }
}
