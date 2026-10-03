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

        void Update()
        {
            if (interactor == null) interactor = GetComponent<InstrumentInteractor>();
            var device = InputDevices.GetDeviceAtXRNode(controller);
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            bool valid = device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) valid &= tracked;
            Vector3 worldPosition = trackingOrigin != null ? trackingOrigin.TransformPoint(position) : position;
            Quaternion worldRotation = trackingOrigin != null ? trackingOrigin.rotation * rotation : rotation;
            interactor.SetTrackedPose(worldPosition, worldRotation, valid);
            device.TryGetFeatureValue(CommonUsages.grip, out float grip);
            device.TryGetFeatureValue(CommonUsages.trigger, out float trigger);
            interactor.SetGrip(valid ? grip : 0);
            interactor.SetActivation(valid ? trigger : 0);
        }
    }
}
