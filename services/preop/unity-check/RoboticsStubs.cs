// Signature-only stand-ins for main's instrument input (Assets/Scalpal/Instruments, its own asmdef needing
// Unity XR) and the extra UnityEngine members the controller capture uses. Mirrors the real members:
// XRInput.TryPose/Grip/Trigger, XRInstrumentInput.controller/trackingOrigin, InstrumentInteractor.HeldInstrument.
using System;
using UnityEngine;

namespace UnityEngine.XR
{
    public enum XRNode { LeftEye, RightEye, CenterEye, Head, LeftHand, RightHand }
}

namespace UnityEngine
{
    public enum FindObjectsSortMode { None, InstanceID }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HeaderAttribute : Attribute
    {
        public HeaderAttribute(string header) { }
    }
}

namespace Scalpal.Instruments
{
    public static class XRInput
    {
        public static bool TryPose(UnityEngine.XR.XRNode node, out Pose pose)
        {
            pose = default;
            return false;
        }

        public static float Grip(UnityEngine.XR.XRNode node) => 0f;
        public static float Trigger(UnityEngine.XR.XRNode node) => 0f;
    }

    public sealed class XRInstrumentInput : MonoBehaviour
    {
        public UnityEngine.XR.XRNode controller = UnityEngine.XR.XRNode.RightHand;
        public Transform trackingOrigin;
    }

    public sealed class InstrumentInteractor : MonoBehaviour
    {
        public InstrumentBehaviour HeldInstrument { get; private set; }
    }
}
