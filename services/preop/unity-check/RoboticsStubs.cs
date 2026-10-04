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

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion Inverse(Quaternion q) => new Quaternion { x = -q.x, y = -q.y, z = -q.z, w = q.w };
        public static Quaternion operator *(Quaternion a, Quaternion b) => a;
    }

    public struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    public static class SystemInfo
    {
        public static string deviceModel => "stub";
    }

    public static class Application
    {
        public static string persistentDataPath => "";
        public static string version => "0";
    }

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

namespace UnityEditor
{
    public static class Undo
    {
        public static void RegisterCreatedObjectUndo(UnityEngine.Object obj, string name) { }
        public static void RecordObject(UnityEngine.Object obj, string name) { }
        public static T AddComponent<T>(UnityEngine.GameObject go) where T : UnityEngine.Component => default;
    }

    public static class PlayerSettings
    {
        public static class Android
        {
            public static bool forceInternetPermission { get; set; }
        }
    }
}

namespace UnityEditor.SceneManagement
{
    public static class EditorSceneManager
    {
        public static bool MarkSceneDirty(UnityEngine.SceneManagement.Scene scene) => true;
        public static UnityEngine.SceneManagement.Scene OpenScene(string path, OpenSceneMode mode) => default;
        public static bool SaveScene(UnityEngine.SceneManagement.Scene scene) => true;
    }

    public enum OpenSceneMode { Single, Additive }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { }
}
