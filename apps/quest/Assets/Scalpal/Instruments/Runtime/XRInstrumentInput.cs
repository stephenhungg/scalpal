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
            bool valid = UpdatePose();
            interactor.SetGrip(valid ? XRInput.Grip(controller) : 0);
            interactor.SetActivation(valid ? XRInput.Trigger(controller) : 0);
        }

        [BeforeRenderOrder(-100)]
        void RefreshRenderPose()
        {
            // Match the head's late pose refresh so a held tool does not remain
            // at its earlier Update pose while the camera follows a newer pose.
            // Buttons and simulation effects run only in Update.
            UpdatePose();
        }

        bool UpdatePose()
        {
            if (interactor == null) interactor = GetComponent<InstrumentInteractor>();
            bool valid = XRInput.TryPose(controller, out var pose);
            Vector3 worldPosition = trackingOrigin != null ? trackingOrigin.TransformPoint(pose.position) : pose.position;
            Quaternion worldRotation = trackingOrigin != null ? trackingOrigin.rotation * pose.rotation : pose.rotation;
            interactor.SetTrackedPose(worldPosition, worldRotation, valid);
            return valid;
        }
    }
}
