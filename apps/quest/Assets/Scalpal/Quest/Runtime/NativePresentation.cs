using UnityEngine;
using UnityEngine.XR;
using Scalpal.Instruments;
using UnityEngine.XR.ARFoundation;

namespace Scalpal.Quest
{
    // Passthrough is a compositor background. It does not register a person,
    // expose camera pixels, or convert the authored model into patient anatomy.
    [DefaultExecutionOrder(-250)]
    public sealed class NativePresentation : MonoBehaviour
    {
        public Camera headCamera;
        public ARCameraManager cameraManager;
        public GameObject virtualRoom;
        public Renderer virtualMannequin;
        public Transform anatomyFit, patientFrame;
        public NativeCaseSession session;
        public bool passthrough = true;
        bool previousClick;
        Pose authoredFit, authoredFrame;
        Vector3 authoredScale, authoredFrameScale;
        bool initialized;
        public string CoachMode => passthrough ? "mixed_reality" : "virtual";
#if UNITY_EDITOR
        // The native compositor cannot run in Editor. Only the explicitly synthetic
        // frame-driven harness opts in; this seam is absent from headset builds.
        public bool SyntheticCompositorReady { get; set; }
#endif
        public bool Ready => !passthrough || (cameraManager && cameraManager.enabled
            && (cameraManager.subsystem != null && cameraManager.subsystem.running
#if UNITY_EDITOR
                || SyntheticCompositorReady
#endif
            ));

        void Awake()
        {
            if (anatomyFit && patientFrame)
            {
                authoredFit = new Pose(anatomyFit.position, anatomyFit.rotation); authoredScale = anatomyFit.localScale;
                authoredFrame = new Pose(patientFrame.position, patientFrame.rotation); authoredFrameScale = patientFrame.localScale;
                initialized = true;
            }
            if (Scalpal.Handoff.HandoffRun.Current != null) passthrough = Scalpal.Handoff.HandoffRun.Current.presentationMode == "mixed_reality";
            Apply();
        }

        void Update() => StickToggle(XRInput.Button(XRNode.RightHand, XRInputButton.StickClick));

        // Standalone convenience only. With a theatre ticket the handoff cards own AR/VR choice, so a stray
        // stick press can never change mode or abandon an attempt. Otherwise the session's mode rules apply
        // (pre-practice phases only; AR needs green operator preflight for office routes).
        void StickToggle(bool click)
        {
            if (click && !previousClick && session && Scalpal.Handoff.HandoffRun.Current == null) session.TryChangePresentation(!passthrough);
            previousClick = click;
        }

        public void Apply()
        {
            if (virtualRoom) virtualRoom.SetActive(!passthrough);
            if (virtualMannequin) virtualMannequin.enabled = !passthrough;
            if (!passthrough && initialized)
            {
                anatomyFit.SetPositionAndRotation(authoredFit.position, authoredFit.rotation); anatomyFit.localScale = authoredScale;
                patientFrame.SetPositionAndRotation(authoredFrame.position, authoredFrame.rotation); patientFrame.localScale = authoredFrameScale;
            }
            if (headCamera)
            {
                headCamera.clearFlags = CameraClearFlags.SolidColor;
                headCamera.backgroundColor = passthrough ? Color.clear : new Color(0.12f, 0.15f, 0.19f, 1f);
            }
            if (cameraManager) cameraManager.enabled = passthrough;
        }
    }
}
