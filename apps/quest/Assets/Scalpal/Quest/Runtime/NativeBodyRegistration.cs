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
        public EnvironmentRaycastManager surfaceAccess;
        public Transform anatomyFit, patientFrame;
        public AnatomyController bodyOverview;
        public string endpoint = "http://localhost:8790";
        public bool Accepted { get; private set; }
        public bool CandidateValid => candidateValid && Time.realtimeSinceStartup - observationTime < 0.75f;
        public string Status { get; private set; } = "Participant agreed? Left stick: enable local body detection";
        public bool EnabledByOperator { get; private set; }
        bool previousClick, candidateValid, inFlight, awaitingPermissions;
        int epoch, stableFrames;
        Plane plane;
        BodyRegistrationMath.Fit candidate, accepted;
        float observationTime, nextFrame, nextDepthDiagnostic;
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
            if (surfaceAccess) surfaceAccess.enabled = false;
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
            if (awaitingPermissions)
            {
                CompletePermissionWait(PermissionsGranted(), workbench.IsReady);
                if (awaitingPermissions) Status = "Waiting for camera/spatial permission and headset readiness";
            }
            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            left.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click);
            if (click && !previousClick && workbench.IsReady) EnableDetection();
            previousClick = click;
            if (EnabledByOperator && (!workbench.IsReady || (candidateValid && !CandidateValid))) Invalidate("Body tracking stale or XR paused; automatically reacquiring fit");
            if (EnabledByOperator && cameraAccess && !cameraAccess.IsPlaying)
                Status = "Waiting for camera permission/frames; automatic fit remains paused";
            if (EnabledByOperator && workbench.IsReady && cameraAccess && cameraAccess.IsPlaying && !inFlight && Time.realtimeSinceStartup >= nextFrame)
                StartCoroutine(Observe(epoch));
            bool show = presentation.passthrough && Accepted && CandidateValid;
            foreach (var marker in markers) if (marker) marker.SetActive(show);
            if (bodyOverview)
            {
                bodyOverview.gameObject.SetActive(show && presentation.session && !presentation.session.Practicing);
                if (show) Apply(accepted, bodyOverview.transform);
            }
        }

        void EnableDetection()
        {
            ResetFit();
            if (!cameraAccess || !surfaceAccess || !EnvironmentRaycastManager.IsSupported)
            { Status = "Automatic body depth unavailable on this runtime; alignment paused"; return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            var permissions = new List<string>();
            if (!Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA"))
                permissions.Add("horizonos.permission.HEADSET_CAMERA");
            if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
                permissions.Add(OVRPermissionsRequester.ScenePermission);
            if (permissions.Count > 0)
            {
                WaitForPermissions();
                int generation = epoch;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionDenied += _ => CancelPermissionRequest(generation);
                callbacks.PermissionDeniedAndDontAskAgain += _ => CancelPermissionRequest(generation);
                Permission.RequestUserPermissions(permissions.ToArray(), callbacks);
                return;
            }
#endif
            ActivateSources();
        }

        static bool PermissionsGranted()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA")
                && Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
#else
            return false; // Editor fixtures explicitly provide synthetic permission outcomes.
#endif
        }

        void WaitForPermissions()
        {
            EnabledByOperator = false; awaitingPermissions = true;
            if (cameraAccess) cameraAccess.enabled = false;
            if (surfaceAccess) surfaceAccess.enabled = false;
            Status = "Allow camera/spatial permissions to finish enabling detection";
        }

        void CompletePermissionWait(bool granted, bool ready)
        {
            if (!awaitingPermissions || !granted || !ready) return;
            awaitingPermissions = false;
            ActivateSources();
        }

        void CancelPermissionRequest(int generation)
        {
            if (generation != epoch || !awaitingPermissions) return;
            StopTracking();
            Status = "Camera/spatial permission denied; left stick to try enabling detection again";
        }

        void ActivateSources()
        {
            EnabledByOperator = true; awaitingPermissions = false;
            surfaceAccess.CustomTrackingSpace = workbench.trackingOrigin;
            surfaceAccess.enabled = true;
            cameraAccess.enabled = true;
            Status = "Look at both shoulders and hips; automatically measuring torso surface";
            Debug.Log("SCALPAL_NATIVE_BODY_DEPTH enabled=True supported=True");
        }

        BodySurfaceSnapshot CaptureSurface(Ray bottomLeft, Ray bottomRight, Ray topLeft, Vector3 forward, float cameraAge)
        {
            if (!surfaceAccess || !surfaceAccess.enabled || cameraAge > .1f) return null;
            var surface = new BodySurfaceSnapshot { bottomLeft = bottomLeft, bottomRight = bottomRight, topLeft = topLeft, lensForward = forward };
            float started = Time.realtimeSinceStartup;
            int count = 0;
            for (int y = 0; y < BodySurfaceSnapshot.Height; y++)
                for (int x = 0; x < BodySurfaceSnapshot.Width; x++)
                {
                    int i = y * BodySurfaceSnapshot.Width + x;
                    var image = new Vector2((float)x / (BodySurfaceSnapshot.Width - 1), (float)y / (BodySurfaceSnapshot.Height - 1));
                    if (BodyRegistrationMath.ImageRay(bottomLeft, bottomRight, topLeft, forward, image, out var ray)
                        && surfaceAccess.Raycast(ray, out var hit, 3f)
                        && hit.status == EnvironmentRaycastHitStatus.Hit && hit.normalConfidence >= .8f
                        && BodyRegistrationMath.Finite(hit.point) && BodyRegistrationMath.Finite(hit.normal)
                        && hit.normal.sqrMagnitude > .9f && Vector3.Distance(ray.origin, hit.point) >= .2f)
                    {
                        surface.valid[i] = true; surface.points[i] = hit.point; surface.normals[i] = hit.normal.normalized; count++;
                    }
                    // Native raycast has no sensor timestamp. Bound the acquisition window;
                    // never use depth first queried after the HTTP landmark response.
                    if (Time.realtimeSinceStartup - started > .008f)
                    { ReportDepth(started, count, false); return null; }
                }
            ReportDepth(started, count, true);
            return count > 0 ? surface : null;
        }

        void ReportDepth(float started, int count, bool complete)
        {
            if (Time.realtimeSinceStartup < nextDepthDiagnostic) return;
            nextDepthDiagnostic = Time.realtimeSinceStartup + 5;
            Debug.Log($"SCALPAL_NATIVE_BODY_DEPTH snapshotComplete={complete} hits={count} sampleMs={(Time.realtimeSinceStartup - started) * 1000:F2}");
        }

        IEnumerator Observe(int generation) => ObserveSafely(ObserveFrame(generation), generation);

        // Own the entire iterator lifetime, including exceptions after a yielded GPU/HTTP
        // operation and cancellation/disposal. A failed frame must not wedge acquisition.
        IEnumerator ObserveSafely(IEnumerator frame, int generation)
        {
            inFlight = true; nextFrame = Time.realtimeSinceStartup + 0.3f;
            try
            {
                while (true)
                {
                    bool next = false, failed = false; object current = null;
                    try { next = frame.MoveNext(); if (next) current = frame.Current; }
                    catch (Exception)
                    {
                        failed = true;
                        if (generation == epoch) Invalidate("Camera observation failed; automatically retrying");
                    }
                    if (failed || !next) yield break;
                    yield return current;
                }
            }
            finally
            {
                // Dispose the inner request even if Unity cancels this outer coroutine.
                try { (frame as IDisposable)?.Dispose(); }
                finally { activeRequest = null; inFlight = false; }
            }
        }

        void EnsureReadback(Texture texture)
        {
            if (!texture || texture.width < 1 || texture.height < 1) throw new ArgumentException("Missing camera texture dimensions");
            if (readback && readback.width == texture.width && readback.height == texture.height) return;
            if (readback) { if (Application.isPlaying) Destroy(readback); else DestroyImmediate(readback); }
            readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
        }

        IEnumerator ObserveFrame(int generation)
        {
            yield return new WaitForEndOfFrame();
            if (generation != epoch || !cameraAccess || !cameraAccess.IsPlaying) { yield break; }
            float captured = Time.realtimeSinceStartup;
            // PCA returns lens pose in tracking space. Apply exactly the rig's existing floor transform.
            Pose lens = cameraAccess.GetCameraPose();
            Pose world = new Pose(workbench.trackingOrigin.TransformPoint(lens.position), workbench.trackingOrigin.rotation * lens.rotation);
            Texture texture = cameraAccess.GetTexture();
            if (!texture || cameraAccess.Timestamp == default || !BodyRegistrationMath.ValidFloorLensPose(lens, cameraAccess.Intrinsics.LensOffset)
                || !TrackingSpacesAgree()) { Invalidate("Camera pose/reference space unavailable"); yield break; }
            if (cameraAccess.Timestamp == lastCameraTimestamp) { yield break; }
            double cameraAge = (DateTime.UtcNow - cameraAccess.Timestamp).TotalSeconds;
            if (cameraAge < -0.1 || cameraAge > 0.5) { Invalidate("Camera frame timestamp is stale or incompatible"); yield break; }
            lastCameraTimestamp = cameraAccess.Timestamp;
            captured -= (float)Math.Max(0, cameraAge);
            Vector2Int resolution = new Vector2Int(texture.width, texture.height);
            // Cache rays at acquisition, before asynchronous inference and subsequent head motion.
            Ray bottomLeft = cameraAccess.ViewportPointToRay(Vector2.zero, world);
            Ray bottomRight = cameraAccess.ViewportPointToRay(Vector2.right, world);
            Ray topLeft = cameraAccess.ViewportPointToRay(Vector2.up, world);
            var surface = CaptureSurface(bottomLeft, bottomRight, topLeft, world.rotation * Vector3.forward, (float)Math.Max(0, cameraAge));
            if (surface == null) { Invalidate("Waiting for live spatial depth/permission; automatic fit paused"); yield break; }
            // Meta explicitly warns that blocking Blit/GetTexture readback can return the
            // preceding image. Queue asynchronous GPU readback with this frame's metadata.
            if (!SystemInfo.supportsAsyncGPUReadback) { Invalidate("Calibrated camera readback unsupported"); yield break; }
            var pixels = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32);
            while (!pixels.done) yield return null;
            if (generation != epoch || pixels.hasError) { if (generation == epoch) Invalidate("Camera readback failed"); yield break; }
            EnsureReadback(texture);
            // Encoding consumes CPU pixel data; no redundant GPU upload via Apply.
            readback.LoadRawTextureData(pixels.GetData<byte>());
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
                    Process(reply, id, resolution, captured, surface);
                }
                activeRequest = null;
            }
        }

        bool TrackingSpacesAgree()
        {
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position) || !head.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation)) return false;
            var ovr = OVRPlugin.GetNodePose(OVRPlugin.Node.Head, OVRPlugin.Step.Render).ToOVRPose();
            return Vector3.Distance(position, ovr.position) < 0.05f && Quaternion.Angle(rotation, ovr.orientation) < 8f;
        }

        public void Process(Reply reply, string id, Vector2Int resolution, float captured, BodySurfaceSnapshot surface)
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
            int[] ids = { 11, 12, 23, 24 }; var points = new Vector3[4]; var imagePoints = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                var item = Array.Find(reply.landmarks, p => p != null && p.index == ids[i]);
                if (item == null || !BodyRegistrationMath.Finite(item.x) || !BodyRegistrationMath.Finite(item.y)
                    || !BodyRegistrationMath.Finite(item.visibility) || !BodyRegistrationMath.Finite(item.presence)
                    || item.x < 0 || item.x > 1 || item.y < 0 || item.y > 1 || item.visibility < 0.65f || item.presence < 0.65f)
                { Invalidate("Shoulders/hips occluded or uncertain; scoring paused"); return; }
                imagePoints[i] = new Vector2(item.x, item.y);
            }
            if (surface == null || !surface.TryTorsoPlane(imagePoints, out plane, out var center))
            { Invalidate("Torso depth missing or discontinuous; automatic fit paused"); return; }
            for (int i = 0; i < 4; i++)
                if (!BodyRegistrationMath.ImageRay(surface.bottomLeft, surface.bottomRight, surface.topLeft, surface.lensForward, imagePoints[i], out var ray)
                    || !BodyRegistrationMath.Intersect(ray, plane, center, out points[i]))
                { Invalidate("Body rays do not meet the measured torso surface"); return; }
            if (!BodyRegistrationMath.TryFit(points, plane.normal, out var proposed)) { Invalidate("Body fit proportions/orientation uncertain"); return; }
            if (Accepted && !BodyRegistrationMath.Near(proposed, accepted)) Invalidate("Participant moved; automatically reacquiring fit");
            stableFrames = candidateValid && BodyRegistrationMath.Near(candidate, proposed) ? stableFrames + 1 : 1;
            candidate = proposed; candidateValid = true; observationTime = captured;
            for (int i = 0; i < 4; i++) if (markers[i]) markers[i].transform.position = points[i];
            if (!Accepted && stableFrames >= 3) TryAccept();
            Status = Accepted ? "Automatically aligned generic anatomy; tracking live" : "Automatically acquiring a stable body fit";
        }

        public bool TryAccept()
        {
            if (!CandidateValid || stableFrames < 3 || !workbench.IsReady) return false;
            accepted = candidate; Accepted = true; Apply(accepted, anatomyFit);
            patientFrame.SetPositionAndRotation(anatomyFit.TransformPoint(BodyRegistrationMath.SourceUmbilicus),
                Quaternion.LookRotation(accepted.rotation * Vector3.up, plane.normal));
            patientFrame.localScale = Vector3.one * accepted.scale;
            Status = "Generic body fit automatically aligned"; return true;
        }

        static void Apply(BodyRegistrationMath.Fit fit, Transform root)
        { root.SetPositionAndRotation(fit.position, fit.rotation); root.localScale = Vector3.one * fit.scale; }
        void Hide() { foreach (var marker in markers) if (marker) marker.SetActive(false); if (bodyOverview) bodyOverview.gameObject.SetActive(false); }
        void Invalidate(string reason) { Accepted = candidateValid = false; stableFrames = 0; Status = reason; Hide(); }
        public void ResetFit() { epoch++; Accepted = candidateValid = false; stableFrames = 0; Hide(); }
        public void StopTracking()
        {
            if (EnabledByOperator || awaitingPermissions) ResetFit();
            EnabledByOperator = awaitingPermissions = false;
            if (cameraAccess) cameraAccess.enabled = false;
            if (surfaceAccess) surfaceAccess.enabled = false;
            Status = "Participant agreed? Left stick: enable local body detection";
        }
        void OriginChanged(XRInputSubsystem subsystem)
        {
            if (!EnabledByOperator) return;
            ResetFit();
            Status = "XR origin changed; automatically reacquiring torso depth";
        }
        void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            // The permission dialog may pause the player before any acquisition starts.
            // Preserve only this pending opt-in; capture stays off until both grants and XR
            // readiness return. A pause after acquisition begins still cancels acquisition.
            if (awaitingPermissions) { Hide(); return; }
            StopTracking();
        }
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
