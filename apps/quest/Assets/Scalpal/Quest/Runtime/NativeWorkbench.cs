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
        public NativePresentation presentation;
        // Full-VR locomotion: left stick walks along the gaze, right stick snap-turns. Off in passthrough,
        // where moving the origin would slide the anatomy off the registered patient.
        public float moveSpeed = 1.2f, snapDegrees = 30, roamRadius = 3f, riseSpeed = .6f, maximumRise = .6f;
        public bool IsReady { get; private set; }
        public event Action ToolsReset;
        public event Action RetryRequested;

        Vector3[] toolPositions;
        Quaternion[] toolRotations;
        Transform[] toolParents;
        Vector3[] targetPositions;
        Quaternion[] targetRotations;
        Transform[] targetParents;
        bool aligned, headTracked, paused, resetPressed, retryPressed;
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
                tools[i].CaptureRestPose();
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
            Gate(false);
            sampleStart = Time.unscaledTime;
            Debug.Log("SCALPAL_NATIVE_BOOT version=" + Application.version + " view=" + (presentation && presentation.passthrough ? "passthrough" : "full_vr") + " tools=" + tools.Length);
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
            bool running = XRInput.Source.DisplayRunning;
            bool floor = XRInput.Source.FloorTracking;
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
            bool focused = XRInput.Source.HasFocus;
            bool valid = running && floor && aligned && headTracked && focused && !paused
                && (!presentation || presentation.Ready);
            Gate(valid);
            if (valid && !(presentation && presentation.passthrough)) Locomote(Time.deltaTime);
            bool reset = XRInput.Button(XRNode.RightHand, XRInputButton.Primary);
            bool retry = XRInput.Button(XRNode.LeftHand, XRInputButton.Menu);
            HandleSessionButtons(reset, retry);
            frames++;
            if (Time.unscaledTime >= nextStatus)
            {
                float seconds = Mathf.Max(0.001f, Time.unscaledTime - sampleStart);
                string state = $"xr={running} head={headTracked} floor={floor} focus={focused && !paused} aligned={aligned} left={Hand(0)} right={Hand(1)} effects={effects} updateHz={frames / seconds:F1}";
                Debug.Log("SCALPAL_NATIVE_STATUS " + state);
                if (status && !externalSessionControls) status.text = "Scalpal | Native tool test\nGrip: pick up / release   Trigger: use tool\nA: reset tools and practice patch\n" + (valid ? "Tracking ready" : "Paused: waiting for valid XR tracking") + "   Effects: " + effects;
                frames = 0; sampleStart = Time.unscaledTime; nextStatus = Time.unscaledTime + 2;
            }
        }

        bool turnArmed = true;
        public void Locomote(float deltaTime)
        {
            var move = XRInput.Stick(XRNode.LeftHand);
            if (move.sqrMagnitude > .04f)
            {
                var forward = Vector3.ProjectOnPlane(headCamera.transform.forward, Vector3.up).normalized;
                var right = Vector3.Cross(Vector3.up, forward);
                var next = trackingOrigin.position + (forward * move.y + right * move.x) * (moveSpeed * deltaTime);
                // Keep the head inside the room around the start pose (never walk through the walls).
                var head = headCamera.transform.position - trackingOrigin.position + next;
                var offset = Vector3.ProjectOnPlane(head - initialHeadFloorPosition, Vector3.up);
                if (offset.magnitude > roamRadius) next -= offset - offset.normalized * roamRadius;
                trackingOrigin.position = next;
            }
            // Right stick up/down raises or lowers the viewpoint (e.g. to look down into the wound), within ±maximumRise
            // of the aligned floor height.
            float rise = XRInput.Stick(XRNode.RightHand).y;
            if (Mathf.Abs(rise) > .2f)
            {
                var position = trackingOrigin.position;
                position.y = Mathf.Clamp(position.y + rise * riseSpeed * deltaTime, initialHeadFloorPosition.y - maximumRise, initialHeadFloorPosition.y + maximumRise);
                trackingOrigin.position = position;
            }
            float turn = XRInput.Stick(XRNode.RightHand).x;
            if (Mathf.Abs(turn) < .3f) turnArmed = true;
            else if (turnArmed && Mathf.Abs(turn) > .7f)
            {
                turnArmed = false;
                trackingOrigin.RotateAround(headCamera.transform.position, Vector3.up, Mathf.Sign(turn) * snapDegrees);
            }
        }

        // Hardware and frame-driven validation use this same gated rising-edge route.
        public void HandleSessionButtons(bool resetTools, bool retryAttempt)
        {
            if (IsReady && resetTools && !resetPressed)
            {
                if (externalSessionControls) ResetTools();
                else ResetWorkbench();
            }
            if (IsReady && externalSessionControls && retryAttempt && !retryPressed) RetryRequested?.Invoke();
            resetPressed = resetTools; retryPressed = retryAttempt;
        }

        [BeforeRenderOrder(-200)]
        void UpdateHeadPose()
        {
            if (!headCamera) return;
            headTracked = XRInput.TryPose(XRNode.Head, out var pose);
            if (headTracked) headCamera.transform.SetLocalPositionAndRotation(pose.position, pose.rotation);
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

        // Additive case kits preserve the reset poses of existing tools. Call after Awake.
        public void RegisterAdditionalTools(InstrumentBehaviour[] additions)
        {
            var all = new List<InstrumentBehaviour>(tools ?? Array.Empty<InstrumentBehaviour>());
            var positions = new List<Vector3>(toolPositions ?? Array.Empty<Vector3>());
            var rotations = new List<Quaternion>(toolRotations ?? Array.Empty<Quaternion>());
            var parents = new List<Transform>(toolParents ?? Array.Empty<Transform>());
            foreach (var item in additions ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!item || all.Contains(item)) continue;
                all.Add(item); positions.Add(item.transform.position); rotations.Add(item.transform.rotation); parents.Add(item.transform.parent);
                item.CaptureRestPose(); item.ActionApplied += Applied;
            }
            tools = all.ToArray(); toolPositions = positions.ToArray(); toolRotations = rotations.ToArray(); toolParents = parents.ToArray();
        }

        // A case layout moves a registered tool to a new resting place; equipment reset (A) returns it there.
        public void SetRestPose(InstrumentBehaviour tool, Vector3 position, Quaternion rotation)
        {
            int index = Array.IndexOf(tools ?? Array.Empty<InstrumentBehaviour>(), tool);
            if (index < 0 || toolPositions == null || index >= toolPositions.Length) return;
            tool.transform.SetPositionAndRotation(position, rotation);
            toolPositions[index] = position; toolRotations[index] = rotation;
            tool.RecaptureRestPose();
            var body = tool.GetComponent<Rigidbody>();
            if (!body) return;
            body.position = position; body.rotation = rotation;
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        }

        public void ResetWorkbench()
        {
            ResetTools();
            for (int i = 0; i < targets.Length; i++)
            {
                targets[i].ResetTeachingTarget();
                targets[i].transform.SetParent(targetParents[i], true);
                targets[i].transform.SetPositionAndRotation(targetPositions[i], targetRotations[i]);
            }
            effects = 0;
            Debug.Log("SCALPAL_NATIVE_RESET");
        }

        // A in a session resets only equipment: no case, attempt, target or coach reset.
        public void ResetTools()
        {
            ToolsReset?.Invoke();
            foreach (var input in inputs ?? Array.Empty<XRInstrumentInput>())
                if (input && input.TryGetComponent<InstrumentInteractor>(out var interactor)) interactor.ReturnHeldToRest();
            for (int i = 0; i < tools.Length; i++)
            {
                var tool = tools[i];
                tool.transform.SetParent(toolParents[i], true);
                tool.transform.SetPositionAndRotation(toolPositions[i], toolRotations[i]);
                var body = tool.GetComponent<Rigidbody>();
                if (body && !body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            }
            Debug.Log("SCALPAL_NATIVE_TOOLS_RESET");
        }

        void OnApplicationFocus(bool value) { if (!value) Gate(false); }
        void OnApplicationPause(bool value) { paused = value; if (value) Gate(false); }
    }
}
