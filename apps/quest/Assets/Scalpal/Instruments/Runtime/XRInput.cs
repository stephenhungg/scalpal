using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Instruments
{
    public enum XRInputButton { Primary, Secondary, Menu, StickClick }

    public interface IXRInputSource
    {
        bool DisplayRunning { get; }
        bool FloorTracking { get; }
        bool HasFocus { get; }
        bool TryPose(XRNode node, out Pose pose);
        float Grip(XRNode node);
        float Trigger(XRNode node);
        bool Button(XRNode node, XRInputButton button);
    }

    // Production uses Unity XR. A scoped Play Mode fixture may replace the provider and
    // drive the same Update, alignment, tracking, button and physics routes.
    public static class XRInput
    {
        static readonly IXRInputSource unity = new UnityXRInputSource();
        public static IXRInputSource Source { get; set; } = unity;
        public static void RestoreUnitySource() => Source = unity;
        public static bool TryPose(XRNode node, out Pose pose) => Source.TryPose(node, out pose);
        public static float Grip(XRNode node) => Source.Grip(node);
        public static float Trigger(XRNode node) => Source.Trigger(node);
        public static bool Button(XRNode node, XRInputButton button) => Source.Button(node, button);

        sealed class UnityXRInputSource : IXRInputSource
        {
            public bool HasFocus => Application.isFocused;
            readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
            readonly List<XRInputSubsystem> inputs = new List<XRInputSubsystem>();
            public bool DisplayRunning
            {
                get
                {
                    SubsystemManager.GetSubsystems(displays);
                    foreach (var display in displays) if (display.running) return true;
                    return false;
                }
            }
            public bool FloorTracking
            {
                get
                {
                    SubsystemManager.GetSubsystems(inputs);
                    bool floor = false;
                    foreach (var input in inputs)
                    {
                        if (!input.running) continue;
                        if (input.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor)
                            input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                        floor |= input.GetTrackingOriginMode() == TrackingOriginModeFlags.Floor;
                    }
                    return floor;
                }
            }
            public bool TryPose(XRNode node, out Pose pose)
            {
                var device = InputDevices.GetDeviceAtXRNode(node);
                pose = Pose.identity;
                Vector3 position = Vector3.zero;
                Quaternion rotation = Quaternion.identity;
                bool valid = device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                    && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
                if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) valid &= tracked;
                if (!valid) return false;
                pose = new Pose(position, rotation);
                return true;
            }
            public float Grip(XRNode node)
            {
                InputDevices.GetDeviceAtXRNode(node).TryGetFeatureValue(CommonUsages.grip, out float value);
                return value;
            }
            public float Trigger(XRNode node)
            {
                InputDevices.GetDeviceAtXRNode(node).TryGetFeatureValue(CommonUsages.trigger, out float value);
                return value;
            }
            public bool Button(XRNode node, XRInputButton button)
            {
                var feature = button == XRInputButton.Primary ? CommonUsages.primaryButton
                    : button == XRInputButton.Secondary ? CommonUsages.secondaryButton
                    : button == XRInputButton.Menu ? CommonUsages.menuButton : CommonUsages.primary2DAxisClick;
                InputDevices.GetDeviceAtXRNode(node).TryGetFeatureValue(feature, out bool value);
                return value;
            }
        }
    }
}
