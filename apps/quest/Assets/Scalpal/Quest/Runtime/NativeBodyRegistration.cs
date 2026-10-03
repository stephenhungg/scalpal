using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Scalpal.Anatomy;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // Local, ephemeral camera inference. No participant images, landmarks or audio are persisted.
    [DefaultExecutionOrder(50)]
    public sealed class NativeBodyRegistration : MonoBehaviour
    {
        public NativeWorkbench workbench;
        public NativePresentation presentation;
        public PassthroughCameraAccess cameraAccess;
        public Transform anatomyFit, patientFrame;
        public AnatomyController bodyOverview;
        public string endpoint = "http://localhost:8790";
        public bool Accepted { get; private set; }
        public bool CandidateValid => candidateValid && Time.realtimeSinceStartup - observationTime < 0.75f;
        public string Status { get; private set; } = "Participant agreed? Left stick: enable local body detection";
        public bool EnabledByOperator { get; private set; }
        bool previousClick, candidateValid, inFlight;
        int calibrationCount, epoch, stableFrames;
        readonly Vector3[] calibration = new Vector3[3];
        Plane plane;
        Vector3 center;
        BodyRegistrationMath.Fit candidate, accepted;
        float observationTime, nextFrame;
        Texture2D readback;
        UnityWebRequest activeRequest;
        DateTime lastCameraTimestamp;
        readonly List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        readonly HashSet<XRInputSubsystem> subscribed = new HashSet<XRInputSubsystem>();
        float nextSubscriptions;
        readonly GameObject[] markers = new GameObject[4];
        [Serializable] public class Landmark { public int index; public float x, y, z, visibility, presence; }
        [Serializable] public class Reply
        {
            public string schema, frameId, coordinateConvention, reason;
            public int imageWidth, imageHeight, personCount;
            public bool valid;
            public Landmark[] landmarks;
            public Model model;
        }
        [Serializable] public class Model { public string name, version, sha256, mediapipeVersion; }

        void Start()
        {
            if (cameraAccess) cameraAccess.enabled = false;
            for (int i = 0; i < markers.Length; i++)
            {
                markers[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                markers[i].name = "BodyFitLandmark_" + i;
                Destroy(markers[i].GetComponent<Collider>());
                markers[i].transform.localScale = Vector3.one * 0.025f;
                markers[i].GetComponent<Renderer>().material.color = Color.cyan;
                markers[i].SetActive(false);
            }
            Hide();
        }

        void Update()
        {
            if (Time.unscaledTime >= nextSubscriptions)
            {
                nextSubscriptions = Time.unscaledTime + 1;
                SubsystemManager.GetSubsystems(subsystems);
                foreach (var subsystem in subsystems)
                    if (subscribed.Add(subsystem)) subsystem.trackingOriginUpdated += OriginChanged;
            }
            if (!presentation || !presentation.passthrough) { StopTracking(); return; }
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            left.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click);
            if (click && !previousClick && workbench.IsReady) Calibrate();
            previousClick = click;
            if (EnabledByOperator && (!workbench.IsReady || (candidateValid && !CandidateValid))) Invalidate("Body tracking stale or XR paused; confirm a new fit");
            if (EnabledByOperator && cameraAccess && !cameraAccess.IsPlaying && calibrationCount == 3)
                Status = "Waiting for camera permission/frames; body fit remains invalid";
            if (EnabledByOperator && workbench.IsReady && cameraAccess && cameraAccess.IsPlaying && calibrationCount == 3 && !inFlight && Time.realtimeSinceStartup >= nextFrame)
                StartCoroutine(Observe(epoch));
            bool show = presentation.passthrough && CandidateValid;
            foreach (var marker in markers) if (marker) marker.SetActive(show);
            if (bodyOverview)
            {
                bodyOverview.gameObject.SetActive(show && presentation.session && !presentation.session.Practicing);
                if (show) Apply(candidate, bodyOverview.transform);
            }
        }

        void Calibrate()
        {
            if (!EnabledByOperator)
            {
                EnabledByOperator = true;
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
                    Permission.RequestUserPermission("horizonos.permission.HEADSET_CAMERA");
#endif
                cameraAccess.enabled = true;
                Status = "Hold right controller just above torso front near a shoulder. Left stick: first plane point";
                return;
            }
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!right.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || !tracked
                || !right.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position)) { Status = "Right controller is not tracked"; return; }
            if (calibrationCount == 3) { ResetFit(); calibrationCount = 0; }
            calibration[calibrationCount++] = workbench.trackingOrigin.TransformPoint(position);
            if (calibrationCount < 3)
            { Status = calibrationCount == 1 ? "Second plane point: just above other shoulder; left stick" : "Third plane point: just above hip at same front surface; left stick"; return; }
            if (!BodyRegistrationMath.TryPlane(calibration[0], calibration[1], calibration[2], workbench.headCamera.transform.position, out plane))
            { calibrationCount = 0; Status = "Plane is too small or not reclining. Set three spaced torso surface points again"; return; }
            center = (calibration[0] + calibration[1] + calibration[2]) / 3;
            Status = "Torso plane set. Look at both shoulders and hips; waiting for one person";
        }

        IEnumerator Observe(int generation)
        {
            inFlight = true; nextFrame = Time.realtimeSinceStartup + 0.2f;
            yield return new WaitForEndOfFrame();
            if (generation != epoch || !cameraAccess || !cameraAccess.IsPlaying) { inFlight = false; yield break; }
            float captured = Time.realtimeSinceStartup;
            // PCA returns lens pose in tracking space. Apply exactly the rig's existing floor transform.
            Pose lens = cameraAccess.GetCameraPose();
            Pose world = new Pose(workbench.trackingOrigin.TransformPoint(lens.position), workbench.trackingOrigin.rotation * lens.rotation);
            Texture texture = cameraAccess.GetTexture();
            if (!texture || cameraAccess.Timestamp == default || !BodyRegistrationMath.ValidFloorLensPose(lens, cameraAccess.Intrinsics.LensOffset)
                || !TrackingSpacesAgree()) { Invalidate("Camera pose/reference space unavailable"); inFlight = false; yield break; }
            if (cameraAccess.Timestamp == lastCameraTimestamp) { inFlight = false; yield break; }
            double cameraAge = (DateTime.UtcNow - cameraAccess.Timestamp).TotalSeconds;
            if (cameraAge < -0.1 || cameraAge > 0.5) { Invalidate("Camera frame timestamp is stale or incompatible"); inFlight = false; yield break; }
            lastCameraTimestamp = cameraAccess.Timestamp;
            captured -= (float)Math.Max(0, cameraAge);
            Vector2Int resolution = cameraAccess.CurrentResolution;
            // Cache rays at acquisition, before asynchronous inference and subsequent head motion.
            Ray bottomLeft = cameraAccess.ViewportPointToRay(Vector2.zero, world);
            Ray bottomRight = cameraAccess.ViewportPointToRay(Vector2.right, world);
            Ray topLeft = cameraAccess.ViewportPointToRay(Vector2.up, world);
            // Meta explicitly warns that blocking Blit/GetTexture readback can return the
            // preceding image. Queue asynchronous GPU readback with this frame's metadata.
            if (!SystemInfo.supportsAsyncGPUReadback) { Invalidate("Calibrated camera readback unsupported"); inFlight = false; yield break; }
            var pixels = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32);
            while (!pixels.done) yield return null;
            if (generation != epoch || pixels.hasError) { if (generation == epoch) Invalidate("Camera readback failed"); inFlight = false; yield break; }
            if (!readback || readback.width != resolution.x || readback.height != resolution.y)
            { if (readback) Destroy(readback); readback = new Texture2D(resolution.x, resolution.y, TextureFormat.RGBA32, false); }
            readback.LoadRawTextureData(pixels.GetData<byte>()); readback.Apply(false);
            byte[] jpeg = readback.EncodeToJPG(75);
            string id = generation + "-" + Time.frameCount;
            using (var request = new UnityWebRequest(endpoint.TrimEnd('/') + "/pose", "POST"))
            {
                activeRequest = request;
                request.uploadHandler = new UploadHandlerRaw(jpeg); request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 2;
                request.SetRequestHeader("Content-Type", "image/jpeg"); request.SetRequestHeader("X-Frame-Id", id);
                yield return request.SendWebRequest();
                if (generation == epoch)
                {
                    Reply reply = null;
                    if (request.result == UnityWebRequest.Result.Success)
                        try { reply = JsonUtility.FromJson<Reply>(request.downloadHandler.text); } catch (ArgumentException) { }
                    Process(reply, id, resolution, captured, bottomLeft, bottomRight, topLeft, world.rotation * Vector3.forward);
                }
                activeRequest = null;
            }
            inFlight = false;
        }

        bool TrackingSpacesAgree()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) || !head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation)) return false;
            var ovr = OVRPlugin.GetNodePose(OVRPlugin.Node.Head, OVRPlugin.Step.Render).ToOVRPose();
            return Vector3.Distance(position, ovr.position) < 0.05f && Quaternion.Angle(rotation, ovr.orientation) < 8f;
        }

        public void Process(Reply reply, string id, Vector2Int resolution, float captured, Ray bottomLeft, Ray bottomRight, Ray topLeft, Vector3 lensForward)
        {
            if (reply == null || reply.schema != "scalpal.body_pose.v1" || reply.frameId != id || !reply.valid || reply.personCount != 1
                || reply.coordinateConvention != "normalized_image_top_left" || reply.imageWidth != resolution.x || reply.imageHeight != resolution.y
                || reply.landmarks == null || reply.landmarks.Length != 33 || Time.realtimeSinceStartup - captured > 0.75f || captured > Time.realtimeSinceStartup
                || reply.model == null || reply.model.sha256 != "59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a")
            { Invalidate("No fresh single-person pose; scoring paused"); return; }
            var indices = new bool[33];
            foreach (var point in reply.landmarks)
            {
                if (point == null || point.index < 0 || point.index > 32 || indices[point.index]
                    || !BodyRegistrationMath.Finite(point.x) || !BodyRegistrationMath.Finite(point.y) || !BodyRegistrationMath.Finite(point.z)
                    || !BodyRegistrationMath.Finite(point.visibility) || !BodyRegistrationMath.Finite(point.presence)
                    || point.visibility < 0 || point.visibility > 1 || point.presence < 0 || point.presence > 1)
                { Invalidate("Malformed body landmark result"); return; }
                indices[point.index] = true;
            }
            int[] ids = { 11, 12, 23, 24 }; var points = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                var item = Array.Find(reply.landmarks, p => p != null && p.index == ids[i]);
                if (item == null || !BodyRegistrationMath.Finite(item.x) || !BodyRegistrationMath.Finite(item.y)
                    || !BodyRegistrationMath.Finite(item.visibility) || !BodyRegistrationMath.Finite(item.presence)
                    || item.x < 0 || item.x > 1 || item.y < 0 || item.y > 1 || item.visibility < 0.65f || item.presence < 0.65f)
                { Invalidate("Shoulders/hips occluded or uncertain; scoring paused"); return; }
                if (!BodyRegistrationMath.ImageRay(bottomLeft, bottomRight, topLeft, lensForward, new Vector2(item.x, item.y), out var ray)
                    || !BodyRegistrationMath.Intersect(ray, plane, center, out points[i]))
                { Invalidate("Landmark ray misses the accepted torso plane"); return; }
            }
            if (!BodyRegistrationMath.TryFit(points, plane.normal, out var proposed)) { Invalidate("Body fit proportions/orientation uncertain"); return; }
            if (Accepted && !BodyRegistrationMath.Near(proposed, accepted)) Invalidate("Participant moved; visually accept a fresh fit");
            stableFrames = candidateValid && BodyRegistrationMath.Near(candidate, proposed) ? stableFrames + 1 : 1;
            candidate = proposed; candidateValid = true; observationTime = captured;
            for (int i = 0; i < 4; i++) if (markers[i]) markers[i].transform.position = points[i];
            Status = Accepted ? "Accepted generic planar body fit; tracking live" : stableFrames >= 3 ? "Check shoulder/hip markers in both eyes. B: accept generic fit" : "Waiting for a stable body fit";
        }

        public bool TryAccept()
        {
            if (!CandidateValid || stableFrames < 3 || !workbench.IsReady) return false;
            accepted = candidate; Accepted = true; Apply(accepted, anatomyFit);
            patientFrame.SetPositionAndRotation(anatomyFit.TransformPoint(BodyRegistrationMath.SourceUmbilicus),
                Quaternion.LookRotation(accepted.rotation * Vector3.up, plane.normal));
            patientFrame.localScale = Vector3.one * accepted.scale;
            Status = "Generic body fit accepted"; return true;
        }

        static void Apply(BodyRegistrationMath.Fit fit, Transform root)
        { root.SetPositionAndRotation(fit.position, fit.rotation); root.localScale = Vector3.one * fit.scale; }
        void Hide() { foreach (var marker in markers) if (marker) marker.SetActive(false); if (bodyOverview) bodyOverview.gameObject.SetActive(false); }
        void Invalidate(string reason) { Accepted = candidateValid = false; stableFrames = 0; Status = reason; Hide(); }
        public void ResetFit() { epoch++; Accepted = candidateValid = false; stableFrames = 0; Hide(); }
        public void StopTracking()
        {
            if (EnabledByOperator) { ResetFit(); EnabledByOperator = false; calibrationCount = 0; if (cameraAccess) cameraAccess.enabled = false; }
            Status = "Participant agreed? Left stick: enable local body detection";
        }
        void OriginChanged(XRInputSubsystem subsystem)
        {
            if (!EnabledByOperator) return;
            ResetFit(); calibrationCount = 0;
            Status = "XR origin changed; set three new torso-plane points";
        }
        void OnApplicationPause(bool paused) { if (paused) StopTracking(); }
        void OnDisable()
        {
            // Deactivation stops coroutines without reaching their normal inFlight cleanup.
            // Cancel this component's observation before allowing any reactivation/retry.
            StopAllCoroutines();
            if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
            inFlight = false;
            StopTracking(); Hide();
        }
        void OnDestroy()
        {
            foreach (var subsystem in subscribed) subsystem.trackingOriginUpdated -= OriginChanged;
            if (readback) Destroy(readback); foreach (var marker in markers) if (marker) Destroy(marker);
        }
    }
}
