using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Instruments
{
    // Works with Unity's XR device abstraction; requires a configured XR loader and tracking origin.
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(InstrumentInteractor))]
    public sealed class XRInstrumentInput : MonoBehaviour
    {
        public XRNode controller = XRNode.RightHand;
        public Transform trackingOrigin;
        InstrumentInteractor interactor;

        void Awake() => interactor = GetComponent<InstrumentInteractor>();
        void OnEnable() => Application.onBeforeRender += RefreshRenderPose;
        void OnDisable()
        {
            Application.onBeforeRender -= RefreshRenderPose;
            if (interactor != null) interactor.SetTrackedPose(transform.position, transform.rotation, false);
        }

        void Update()
        {
            var device = InputDevices.GetDeviceAtXRNode(controller);
            bool valid = UpdatePose(device);
            device.TryGetFeatureValue(CommonUsages.grip, out float grip);
            device.TryGetFeatureValue(CommonUsages.trigger, out float trigger);
            interactor.SetGrip(valid ? grip : 0);
            interactor.SetActivation(valid ? trigger : 0);
        }

        [BeforeRenderOrder(-100)]
        void RefreshRenderPose()
        {
            // Match the head's late pose refresh so a held tool does not remain
            // at its earlier Update pose while the camera follows a newer pose.
            // Buttons and simulation effects run only in Update.
            UpdatePose(InputDevices.GetDeviceAtXRNode(controller));
        }

        bool UpdatePose(InputDevice device)
        {
            if (interactor == null) interactor = GetComponent<InstrumentInteractor>();
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            bool valid = device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) valid &= tracked;
            Vector3 worldPosition = trackingOrigin != null ? trackingOrigin.TransformPoint(position) : position;
            Quaternion worldRotation = trackingOrigin != null ? trackingOrigin.rotation * rotation : rotation;
            interactor.SetTrackedPose(worldPosition, worldRotation, valid);
            return valid;
        }
    }
}
