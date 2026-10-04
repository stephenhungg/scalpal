using System;
using System.Collections.Generic;
using Scalpal.Instruments;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // Local full-VR hardware slice. Its authored patch is not a participant registration result.
    [DefaultExecutionOrder(-200)]
    public sealed class NativeWorkbench : MonoBehaviour
    {
        public Transform trackingOrigin;
        public Camera headCamera;
        public XRInstrumentInput[] inputs;
        public InstrumentBehaviour[] tools;
        public TrainingTarget[] targets;
        public TextMesh status;
        public Vector3 initialHeadFloorPosition = new Vector3(0, 0, -0.5f);
        public bool externalSessionControls;
        public bool IsReady { get; private set; }
        public event Action ResetRequested;

        readonly List<XRInputSubsystem> inputSystems = new List<XRInputSubsystem>();
        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        Vector3[] toolPositions;
        Quaternion[] toolRotations;
        Transform[] toolParents;
        Vector3[] targetPositions;
        Quaternion[] targetRotations;
        Transform[] targetParents;
        Renderer[] gripMarkers;
        bool aligned, headTracked, focused = true, paused, resetPressed;
        float nextStatus;
        int effects, frames;
        float sampleStart;

        void Awake()
        {
            if (!trackingOrigin || !headCamera || inputs == null || inputs.Length != 2 || tools == null || targets == null)
            {
                Debug.LogError("SCALPAL_NATIVE_BINDING_ERROR");
                enabled = false;
                return;
            }
            toolPositions = new Vector3[tools.Length];
            toolRotations = new Quaternion[tools.Length];
            toolParents = new Transform[tools.Length];
            for (int i = 0; i < tools.Length; i++)
            {
                toolPositions[i] = tools[i].transform.position;
                toolRotations[i] = tools[i].transform.rotation;
                toolParents[i] = tools[i].transform.parent;
                tools[i].ActionApplied += Applied;
            }
            targetPositions = new Vector3[targets.Length];
            targetRotations = new Quaternion[targets.Length];
            targetParents = new Transform[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                targetPositions[i] = targets[i].transform.position;
                targetRotations[i] = targets[i].transform.rotation;
                targetParents[i] = targets[i].transform.parent;
            }
            gripMarkers = new Renderer[inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
                gripMarkers[i] = inputs[i].transform.Find("ControllerGripMarker")?.GetComponent<Renderer>();
            Gate(false);
            sampleStart = Time.unscaledTime;
            Debug.Log("SCALPAL_NATIVE_BOOT version=" + Application.version + " mode=full_vr tools=" + tools.Length);
        }

        void OnEnable() => Application.onBeforeRender += UpdateHeadPose;
        void OnDisable()
        {
            Application.onBeforeRender -= UpdateHeadPose;
            Gate(false);
        }
        void OnDestroy()
        {
            if (tools != null) foreach (var tool in tools) if (tool) tool.ActionApplied -= Applied;
        }

        void Update()
        {
            SubsystemManager.GetSubsystems(inputSystems);
            SubsystemManager.GetSubsystems(displays);
            bool running = displays.Exists(display => display.running);
            bool floor = false;
            foreach (var system in inputSystems)
            {
                if (!system.running) continue;
                if (system.GetTrackingOriginMode() != TrackingOriginModeFlags.Floor)
                    system.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                floor |= system.GetTrackingOriginMode() == TrackingOriginModeFlags.Floor;
            }
            UpdateHeadPose();
            if (!aligned && running && floor && headTracked)
            {
                // Put the initial physical pose in front of the authored workbench. The same
                // rotation/translation transforms the head and both controllers; no independent offsets.
                var yaw = Quaternion.Euler(0, -headCamera.transform.localEulerAngles.y, 0);
                trackingOrigin.rotation = yaw;
                var local = headCamera.transform.localPosition;
                var horizontal = new Vector3(local.x, 0, local.z);
                trackingOrigin.position = initialHeadFloorPosition - yaw * horizontal;
                aligned = true;
                Debug.Log("SCALPAL_NATIVE_ALIGNED origin=floor");
            }
            bool valid = running && floor && aligned && headTracked && focused && !paused;
            Gate(valid);
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            right.TryGetFeatureValue(CommonUsages.primaryButton, out bool reset);
            if (valid && reset && !resetPressed)
            {
                if (externalSessionControls) ResetRequested?.Invoke();
                else ResetWorkbench();
            }
            resetPressed = reset;
            frames++;
            if (Time.unscaledTime >= nextStatus)
            {
                float seconds = Mathf.Max(0.001f, Time.unscaledTime - sampleStart);
                string state = $"xr={running} head={headTracked} floor={floor} focus={focused && !paused} aligned={aligned} left={Hand(0)} right={Hand(1)} effects={effects} updateHz={frames / seconds:F1}";
                Debug.Log("SCALPAL_NATIVE_STATUS " + state);
                if (status && !externalSessionControls) status.text = "SCALPAL | NATIVE TOOL TEST\nGrip: pick up / release   Trigger: use tool\nA: reset tools and practice patch\n" + (valid ? "Tracking ready" : "Paused: waiting for valid XR tracking") + "   Effects: " + effects;
                frames = 0; sampleStart = Time.unscaledTime; nextStatus = Time.unscaledTime + 2;
            }
        }

        [BeforeRenderOrder(-200)]
        void UpdateHeadPose()
        {
            if (!headCamera) return;
            var device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            headTracked = device.isValid && device.TryGetFeatureValue(CommonUsages.devicePosition, out position)
                && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked)) headTracked &= tracked;
            if (headTracked) headCamera.transform.SetLocalPositionAndRotation(position, rotation);
        }

        void LateUpdate()
        {
            if (gripMarkers == null) return;
            for (int i = 0; i < gripMarkers.Length; i++)
                if (gripMarkers[i]) gripMarkers[i].enabled = inputs[i].enabled && inputs[i].GetComponent<InstrumentInteractor>().TrackingValid;
        }

        void Gate(bool valid)
        {
            IsReady = valid;
            if (targets != null) foreach (var target in targets) if (target) target.SetRegistrationValid(valid);
            if (inputs == null) return;
            foreach (var input in inputs)
            {
                if (!input) continue;
                input.enabled = valid;
                if (!valid)
                {
                    var hand = input.GetComponent<InstrumentInteractor>();
                    if (hand) hand.SetTrackedPose(hand.transform.position, hand.transform.rotation, false);
                }
            }
        }

        string Hand(int i)
        {
            var hand = inputs[i].GetComponent<InstrumentInteractor>();
            return !hand || !hand.TrackingValid ? "untracked" : hand.HeldInstrument ? hand.HeldInstrument.instrumentId : "empty";
        }

        void Applied(InstrumentActionRecord record)
        {
            effects++;
            Debug.Log($"SCALPAL_NATIVE_EFFECT tool={record.instrumentId} target={record.targetId} action={record.action} outcome={record.outcome}");
        }

        public void ResetWorkbench()
        {
            // Invalidating held input also requires a physical grip release before another pickup.
            Gate(false);
            for (int i = 0; i < tools.Length; i++)
            {
                var tool = tools[i];
                tool.transform.SetParent(toolParents[i], true);
                tool.transform.SetPositionAndRotation(toolPositions[i], toolRotations[i]);
                var body = tool.GetComponent<Rigidbody>();
                if (body && !body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i].ResetTeachingTarget();
                targets[i].transform.SetParent(targetParents[i], true);
                targets[i].transform.SetPositionAndRotation(targetPositions[i], targetRotations[i]);
            }
            effects = 0;
            Debug.Log("SCALPAL_NATIVE_RESET");
        }

        void OnApplicationFocus(bool value) { focused = value; if (!value) Gate(false); }
        void OnApplicationPause(bool value) { paused = value; if (value) Gate(false); }
    }
}
