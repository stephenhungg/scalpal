using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic acquisition-time images/depth only; no headset, camera or participant observations.
    public static class NativeBodyRegistrationValidation
    {
        static int checks;
        static void Assert(bool condition, string reason) { checks++; if (!condition) throw new InvalidOperationException("Body registration: " + reason); }
        static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        [MenuItem("Scalpal/Quest/Validate Body Registration Boundaries")]
        public static void Run()
        {
            checks = 0;
            Assert(!BodyRegistrationMath.ValidPose(default), "PCA default pose cannot masquerade as a calibrated lens");
            Assert(BodyRegistrationMath.ValidPose(new Pose(Vector3.one, Quaternion.Euler(12, 30, 2))), "finite unit lens pose accepted");
            var offset = new Pose(new Vector3(.03f, .02f, .05f), Quaternion.Euler(180, 0, 0));
            Assert(!BodyRegistrationMath.ValidFloorLensPose(offset, offset), "SDK identity historical-head fallback rejected even with unit extrinsics");
            Assert(BodyRegistrationMath.ValidFloorLensPose(new Pose(offset.position + Vector3.up * 1.6f, offset.rotation), offset), "valid historical floor-head lens pose accepted");
            var points = new[] { new Vector3(.167503f, 1, .521565f), new Vector3(-.167503f, 1, .521565f),
                new Vector3(.084307f, 1, 0), new Vector3(-.084307f, 1, 0) };
            var surface = Surface();
            var images = Images(points);
            Assert(surface.TryTorsoPlane(images, out var plane, out var center), "cached native surface supplies reclining torso plane");
            Assert(Vector3.Dot(plane.normal, Vector3.up) > .999f && Mathf.Abs(center.y - 1) < .001f, "surface depth and normals retained");
            Assert(surface.Sample(images[0], out var sampled, out _) && Vector3.Distance(sampled, points[0]) < .001f,
                "sub-grid landmark uses calibrated pinhole ray rather than nearest grid point");
            Assert(surface.Sample(Vector2.zero, out _, out _) && surface.Sample(Vector2.one, out _, out _), "image boundary samples stay within grid");
            Assert(!surface.Sample(new Vector2(-.01f, .5f), out _, out _) && !surface.Sample(new Vector2(float.NaN, .5f), out _, out _), "invalid image coordinates rejected");
            var missing = Surface(); Array.Clear(missing.valid, 0, missing.valid.Length);
            Assert(!missing.TryTorsoPlane(images, out _, out _), "unavailable or low-confidence depth cannot calibrate");
            var hole = Surface(); hole.valid[6 * BodySurfaceSnapshot.Width + 8] = false;
            Assert(!hole.TryTorsoPlane(images, out _, out _), "partial torso coverage cannot silently interpolate a missing hit");
            var invalidDepth = Surface(); invalidDepth.points[6 * BodySurfaceSnapshot.Width + 8] = new Vector3(float.NaN, 1, 0);
            Assert(!invalidDepth.TryTorsoPlane(images, out _, out _), "nonfinite depth rejected");
            var badNormal = Surface(); badNormal.normals[6 * BodySurfaceSnapshot.Width + 8] = Vector3.up * 2;
            Assert(!badNormal.TryTorsoPlane(images, out _, out _), "non-unit native normals rejected");
            var standing = Surface(); for (int i = 0; i < standing.normals.Length; i++) standing.normals[i] = Vector3.forward;
            Assert(!standing.TryTorsoPlane(images, out _, out _), "standing surface not mistaken for reclining torso");
            var discontinuity = Surface(); discontinuity.points[6 * BodySurfaceSnapshot.Width + 8].y -= .2f;
            Assert(!discontinuity.TryTorsoPlane(images, out _, out _), "body/table boundary cannot blend into phantom surface");
            var opposed = Surface(); opposed.normals[6 * BodySurfaceSnapshot.Width + 8] = Vector3.down;
            Assert(!opposed.TryTorsoPlane(images, out _, out _), "inconsistent neighborhood normals rejected");
            var inconsistent = Surface();
            for (int y = 0; y < 5; y++) for (int x = 0; x < BodySurfaceSnapshot.Width; x++)
                inconsistent.points[y * BodySurfaceSnapshot.Width + x].y += .3f;
            var separated = new[] { new Vector2(.25f, .25f), new Vector2(.75f, .25f), new Vector2(.25f, .75f), new Vector2(.75f, .75f) };
            Assert(!inconsistent.TryTorsoPlane(separated, out _, out _), "individually smooth but incompatible torso depth samples rejected");
            Assert(!surface.TryTorsoPlane(null, out _, out _) && !surface.TryTorsoPlane(new Vector2[3], out _, out _), "incomplete torso landmark set rejected");
            Assert(BodyRegistrationMath.TryFit(points, plane.normal, out var fit), "correct labeled torso fits");
            Assert(Mathf.Abs(fit.scale - 1) < .0001f, "uniform metric scale");
            Assert(Vector3.Distance(fit.position + fit.rotation * new Vector3(0, BodyRegistrationMath.SourceHipHeight, BodyRegistrationMath.SourceFront), Vector3.up) < .001f, "source front hip projects to accepted surface");
            Assert(Vector3.Dot(fit.rotation * -Vector3.forward, plane.normal) > .999f, "anterior points away from torso");
            var mirrored = (Vector3[])points.Clone(); mirrored[0] = points[1]; mirrored[1] = points[0];
            Assert(!BodyRegistrationMath.TryFit(mirrored, plane.normal, out _), "mirrored sides rejected");
            var invalid = (Vector3[])points.Clone(); invalid[0].x = float.NaN;
            Assert(!BodyRegistrationMath.TryFit(invalid, plane.normal, out _), "nonfinite fit rejected");
            Assert(!BodyRegistrationMath.Intersect(new Ray(Vector3.up * 2, Vector3.right), plane, Vector3.up, out _), "parallel depth ray rejected");
            Assert(!BodyRegistrationMath.Intersect(new Ray(Vector3.up * 2, Vector3.up), plane, Vector3.up, out _), "behind-camera plane rejected");
            var moved = fit; moved.points = (Vector3[])points.Clone(); moved.points[0] += Vector3.right * .1f;
            Assert(!BodyRegistrationMath.Near(fit, moved), "participant motion invalidates frozen accepted fit");
            // Rays were cached at image acquisition. An arbitrary subsequent head pose is irrelevant.
            var rotation = Quaternion.Euler(0, 37, 0); var translation = new Vector3(2, .1f, -1);
            var transformedPlane = new Plane(rotation * plane.normal, rotation * Vector3.up + translation);
            var target = rotation * points[0] + translation;
            var ray = new Ray(rotation * new Vector3(0, 2, 0) + translation, rotation * (points[0] - new Vector3(0, 2, 0)));
            Assert(BodyRegistrationMath.Intersect(ray, transformedPlane, rotation * Vector3.up + translation, out var hit)
                && Vector3.Distance(hit, target) < .001f, "rotated tracking origin preserves acquisition-time ray lift");
            var eye = new Vector3(0, 2, 0);
            Assert(BodyRegistrationMath.ImageRay(new Ray(eye, new Vector3(-.2f, -1, -.6f)), new Ray(eye, new Vector3(1.8f, -1, -.6f)),
                new Ray(eye, new Vector3(-.2f, -1, 1.4f)), Vector3.down, new Vector2(.25f, .6f), out var calibrated)
                && BodyRegistrationMath.Intersect(calibrated, plane, Vector3.up, out var offCenter)
                && Vector3.Distance(offCenter, new Vector3(.3f, 1, .2f)) < .001f, "off-center principal point preserves pinhole ray rather than interpolating normalized directions");

            var transformedSurface = Surface(rotation, translation);
            Assert(transformedSurface.TryTorsoPlane(images, out var liftedPlane, out _) && transformedSurface.Sample(images[0], out var lifted, out _)
                && Vector3.Distance(lifted, target) < .001f && Vector3.Dot(liftedPlane.normal, rotation * Vector3.up) > .999f,
                "cached depth remains in tracking world after response-time head movement");
            var tilted = Surface(Quaternion.Euler(0, 0, 12), Vector3.zero);
            Assert(tilted.TryTorsoPlane(images, out _, out _), "modestly tilted reclining surface supported");

            var root = new GameObject("SyntheticBodyRegistrationBoundary"); root.SetActive(false);
            try
            {
                var workbench = root.AddComponent<NativeWorkbench>();
                typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(workbench, new object[] { true });
                var presentation = root.AddComponent<NativePresentation>(); presentation.passthrough = true;
                var body = root.AddComponent<NativeBodyRegistration>(); body.workbench = workbench; body.presentation = presentation;
                body.anatomyFit = new GameObject("SyntheticFit").transform; body.anatomyFit.SetParent(root.transform);
                body.patientFrame = new GameObject("SyntheticPorts").transform; body.patientFrame.SetParent(root.transform);
                presentation.anatomyFit = body.anatomyFit; presentation.patientFrame = body.patientFrame;
                typeof(NativePresentation).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(presentation, null);
                Assert(!body.TryAccept(), "unobserved fit cannot be accepted");
                var reply = Reply(points);
                Action observe = () => body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup, surface);
                observe(); Assert(body.CandidateValid && !body.Accepted, "one observation cannot auto-calibrate");
                Assert(!body.TryAccept(), "one sample is not stable");
                observe(); Assert(body.CandidateValid && !body.Accepted, "two observations cannot auto-calibrate");
                observe(); Assert(body.Accepted, "third stable image/depth pair automatically calibrates without controller points");
                Assert(Vector3.Distance(body.patientFrame.position, body.anatomyFit.TransformPoint(BodyRegistrationMath.SourceUmbilicus)) < .001f, "ports derive from fitted source umbilicus");
                body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup, null);
                Assert(!body.Accepted && !body.CandidateValid, "missing acquisition-time depth closes scoring gate");
                observe(); observe(); observe(); Assert(body.Accepted, "fresh valid depth automatically recovers registration");
                body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup, missing);
                Assert(!body.Accepted && !body.CandidateValid, "lost depth confidence invalidates accepted fit");
                observe(); observe(); observe(); Assert(body.Accepted, "depth recovery requires three fresh observations");
                body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup, Surface(Quaternion.identity, Vector3.up * .06f));
                Assert(!body.Accepted && body.CandidateValid, "participant surface motion invalidates previously accepted fit");
                observe(); observe(); observe(); Assert(body.Accepted, "stable participant position automatically reacquires");
                reply.personCount = 2; observe(); Assert(!body.Accepted && !body.CandidateValid, "ambiguous person count closes gate"); reply.personCount = 1;
                observe(); observe(); observe(); Assert(body.Accepted, "single-person recovery automatically reacquires");
                reply.landmarks[11].visibility = .2f; observe(); Assert(!body.Accepted, "occlusion closes gate"); reply.landmarks[11].visibility = .9f;
                observe(); observe(); observe(); Assert(body.Accepted, "visible landmarks automatically reacquire");
                body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup - 2, surface);
                Assert(!body.Accepted && !body.CandidateValid, "stale image cannot calibrate against cached depth");
                body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup + 2, surface);
                Assert(!body.CandidateValid, "future acquisition timestamp rejected");
                reply.frameId = "foreign"; observe(); Assert(!body.CandidateValid, "wrong image identity cannot use cached depth"); reply.frameId = "synthetic-1";
                reply.imageWidth = 1280; observe(); Assert(!body.CandidateValid, "image dimensions must match depth sampling rays"); reply.imageWidth = 640;
                reply.landmarks[12].index = 11; observe(); Assert(!body.CandidateValid, "duplicate landmarks rejected"); reply.landmarks[12].index = 12;
                reply.landmarks[11].x = float.NaN; observe(); Assert(!body.CandidateValid, "nonfinite model landmarks rejected"); reply = Reply(points);
                reply.model.sha256 = "wrong"; observe(); Assert(!body.CandidateValid, "unknown model identity rejected"); reply = Reply(points);
                // MediaPipe z does not provide metric depth, even when it is finite and extreme.
                foreach (var landmark in reply.landmarks) landmark.z = 500;
                observe(); observe(); observe(); Assert(body.Accepted && Mathf.Abs(body.patientFrame.position.y - 1) < .001f,
                    "MediaPipe relative z never substitutes for native surface depth");
                typeof(NativeBodyRegistration).GetProperty("EnabledByOperator").GetSetMethod(true).Invoke(body, new object[] { true });
                typeof(NativeBodyRegistration).GetMethod("OriginChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(body, new object[] { null });
                Assert(!body.Accepted && !body.CandidateValid, "XR origin change invalidates cached registration");
                observe(); observe(); observe(); Assert(body.Accepted, "fresh post-origin observations automatically reacquire");
                body.ResetFit(); Assert(!body.Accepted && !body.CandidateValid, "retry invalidates automatic fit");
                ValidatePermissionLifecycle(body, workbench, root);
                ValidateObservationLifetime(body);
                presentation.passthrough = false; presentation.Apply();
                Assert(body.anatomyFit.position == Vector3.zero && body.patientFrame.position == Vector3.zero,
                    "full VR restores authored transforms after real-body fit");
                Set(body, "inFlight", true);
                typeof(NativeBodyRegistration).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(body, null);
                Assert(!(bool)typeof(NativeBodyRegistration).GetField("inFlight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(body),
                    "deactivation clears canceled observation before reactivation");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            Debug.Log("SCALPAL_NATIVE_BODY_REGISTRATION_VALIDATION_OK checks=" + checks + " synthetic native depth/projection/automatic gates; no physical alignment evidence");
        }

        sealed class FailedFrame : IEnumerator, IDisposable
        {
            readonly bool first, current; int advances;
            public bool Disposed { get; private set; }
            public FailedFrame(bool first = false, bool current = false) { this.first = first; this.current = current; }
            public object Current => current ? throw new InvalidOperationException("Synthetic yielded-value failure") : null;
            public bool MoveNext() { if (first || ++advances > 1) throw new InvalidOperationException("Synthetic readback/encode failure"); return true; }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
        }
        static IEnumerator CompletedFrame() { yield return null; }
        static IEnumerator Safe(NativeBodyRegistration body, IEnumerator frame) => (IEnumerator)typeof(NativeBodyRegistration)
            .GetMethod("ObserveSafely", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(body, new object[] { frame, Field<int>(body, "epoch") });
        static void ValidateObservationLifetime(NativeBodyRegistration body)
        {
            var frame = new FailedFrame(); var observed = Safe(body, frame);
            Assert(observed.MoveNext() && Field<bool>(body, "inFlight"), "yielded acquisition holds one in-flight lease");
            Assert(!observed.MoveNext() && frame.Disposed && !Field<bool>(body, "inFlight"),
                "exception after yielded readback clears lease and disposes frame");
            Assert(body.Status.Contains("retrying"), "failed acquisition reports retry instead of silent permanent wedge");
            foreach (var failing in new[] { new FailedFrame(first: true), new FailedFrame(current: true) })
            {
                var retry = Safe(body, failing);
                Assert(!retry.MoveNext() && failing.Disposed && !Field<bool>(body, "inFlight"),
                    "initial or yielded-value exception cannot retain acquisition lease");
            }
            var success = Safe(body, CompletedFrame());
            Assert(success.MoveNext() && !success.MoveNext() && !Field<bool>(body, "inFlight"),
                "fresh acquisition can finish after prior failure");
            var canceledFrame = new FailedFrame(); var canceled = Safe(body, canceledFrame);
            Assert(canceled.MoveNext() && Field<bool>(body, "inFlight"), "cancellation fixture is actually in flight");
            ((IDisposable)canceled).Dispose();
            Assert(canceledFrame.Disposed && !Field<bool>(body, "inFlight"), "iterator cancellation clears lease and inner resources");
            var texture = new Texture2D(9, 7, TextureFormat.RGBA32, false);
            try
            {
                Invoke(body, "EnsureReadback", texture); var buffer = Field<Texture2D>(body, "readback");
                Assert(buffer.width == 9 && buffer.height == 7, "readback uses actual texture dimensions, independent of negotiated metadata");
                buffer.LoadRawTextureData(new byte[9 * 7 * 4]);
                Assert(buffer.GetRawTextureData<byte>().Length == 9 * 7 * 4, "actual-size RGBA readback accepts exactly the acquired payload");
                Invoke(body, "EnsureReadback", texture);
                Assert(ReferenceEquals(buffer, Field<Texture2D>(body, "readback")), "same acquired dimensions reuse buffer");
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            var resized = new Texture2D(3, 5, TextureFormat.RGBA32, false);
            try
            {
                Invoke(body, "EnsureReadback", resized); var buffer = Field<Texture2D>(body, "readback");
                Assert(buffer.width == 3 && buffer.height == 5, "changed texture dimensions replace old readback buffer");
            }
            finally { UnityEngine.Object.DestroyImmediate(resized); }
        }

        static void Invoke(NativeBodyRegistration body, string method, params object[] arguments)
            => typeof(NativeBodyRegistration).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(body, arguments);
        static T Field<T>(NativeBodyRegistration body, string name)
            => (T)typeof(NativeBodyRegistration).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(body);
        static bool SourcesOff(NativeBodyRegistration body) => !body.cameraAccess.enabled && !body.surfaceAccess.enabled;

        static void ValidatePermissionLifecycle(NativeBodyRegistration body, NativeWorkbench workbench, GameObject root)
        {
            // Keep the fixture inactive: enabled flags are tested without starting hardware or OS permission prompts.
            workbench.trackingOrigin = root.transform;
            body.cameraAccess = root.AddComponent<Meta.XR.PassthroughCameraAccess>(); body.cameraAccess.enabled = false;
            body.surfaceAccess = root.AddComponent<Meta.XR.EnvironmentRaycastManager>(); body.surfaceAccess.enabled = false;
            body.StopTracking();
            Invoke(body, "WaitForPermissions");
            Assert(Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "permission request keeps camera and depth off until permission and XR readiness");
            Invoke(body, "OnApplicationPause", true);
            Assert(Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "Android permission-dialog pause preserves pending opt-in without starting capture");
            Invoke(body, "CompletePermissionWait", true, false);
            Assert(Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "permission grant while XR loses focus waits for tracking readiness");
            Invoke(body, "CompletePermissionWait", true, true);
            Assert(!Field<bool>(body, "awaitingPermissions") && body.EnabledByOperator && body.cameraAccess.enabled && body.surfaceAccess.enabled,
                "granted permission after XR resumes activates detection from original opt-in");
            Assert(body.surfaceAccess.CustomTrackingSpace == workbench.trackingOrigin, "resumed depth source uses existing tracking origin");
            Invoke(body, "OnApplicationPause", true);
            Assert(!Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "normal active-app pause stops participant detection and requires fresh opt-in");
            Invoke(body, "CompletePermissionWait", true, true);
            Assert(!body.EnabledByOperator && SourcesOff(body), "late grant after active pause cannot restart capture");

            Invoke(body, "WaitForPermissions");
            Invoke(body, "CompletePermissionWait", false, true);
            Assert(!body.EnabledByOperator && SourcesOff(body), "denied or missing permission never enables a source");
            int deniedGeneration = Field<int>(body, "epoch");
            Invoke(body, "CancelPermissionRequest", deniedGeneration);
            Assert(!Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "current denial cancels pending request");
            Assert(body.Status.ToLowerInvariant().Contains("denied"), "denial is visible instead of indefinite waiting");

            Invoke(body, "WaitForPermissions");
            int canceledGeneration = Field<int>(body, "epoch");
            body.StopTracking();
            Invoke(body, "CompletePermissionWait", true, true);
            Assert(!Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "operator stop cancels pending request and late grant cannot capture");
            Invoke(body, "WaitForPermissions");
            Invoke(body, "CancelPermissionRequest", canceledGeneration);
            Assert(Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "stale denial from canceled request cannot cancel newer opt-in");
            Invoke(body, "CompletePermissionWait", true, true);
            Assert(body.EnabledByOperator && !Field<bool>(body, "awaitingPermissions"), "new opt-in survives stale permission callback");
            string activeStatus = body.Status;
            Invoke(body, "CancelPermissionRequest", Field<int>(body, "epoch"));
            Assert(body.EnabledByOperator && body.Status == activeStatus, "denial callback cannot undo already completed permission request");

            Invoke(body, "WaitForPermissions");
            body.presentation.passthrough = false;
            Invoke(body, "Update");
            Invoke(body, "CompletePermissionWait", true, true);
            Assert(!Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "switching to full VR cancels pending participant-camera opt-in");
            body.presentation.passthrough = true;
            Invoke(body, "WaitForPermissions");
            Invoke(body, "OnDisable");
            Invoke(body, "CompletePermissionWait", true, true);
            Assert(!Field<bool>(body, "awaitingPermissions") && !body.EnabledByOperator && SourcesOff(body),
                "component disable cancels pending request and late grant cannot activate disabled detector");
        }

        static BodySurfaceSnapshot Surface() => Surface(Quaternion.identity, Vector3.zero);
        static BodySurfaceSnapshot Surface(Quaternion rotation, Vector3 translation)
        {
            var eye = new Vector3(0, 2, 0);
            var snapshot = new BodySurfaceSnapshot
            {
                bottomLeft = new Ray(rotation * eye + translation, rotation * new Vector3(-1, -1, -1)),
                bottomRight = new Ray(rotation * eye + translation, rotation * new Vector3(1, -1, -1)),
                topLeft = new Ray(rotation * eye + translation, rotation * new Vector3(-1, -1, 1)),
                lensForward = rotation * Vector3.down
            };
            for (int y = 0; y < BodySurfaceSnapshot.Height; y++) for (int x = 0; x < BodySurfaceSnapshot.Width; x++)
            {
                int index = y * BodySurfaceSnapshot.Width + x;
                snapshot.points[index] = rotation * new Vector3(2f * x / (BodySurfaceSnapshot.Width - 1) - 1, 1, 1 - 2f * y / (BodySurfaceSnapshot.Height - 1)) + translation;
                snapshot.normals[index] = rotation * Vector3.up; snapshot.valid[index] = true;
            }
            return snapshot;
        }

        static Vector2[] Images(Vector3[] points)
        {
            var images = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++) images[i] = new Vector2((points[i].x + 1) / 2, (1 - points[i].z) / 2);
            return images;
        }

        static NativeBodyRegistration.Reply Reply(Vector3[] points)
        {
            var landmarks = new NativeBodyRegistration.Landmark[33];
            for (int i = 0; i < 33; i++) landmarks[i] = new NativeBodyRegistration.Landmark { index = i, x = .5f, y = .5f, visibility = .9f, presence = .9f };
            int[] ids = { 11, 12, 23, 24 };
            for (int i = 0; i < 4; i++) { landmarks[ids[i]].x = (points[i].x + 1) / 2; landmarks[ids[i]].y = (1 - points[i].z) / 2; }
            return new NativeBodyRegistration.Reply { schema = "scalpal.body_pose.v1", frameId = "synthetic-1", imageWidth = 640, imageHeight = 480,
                coordinateConvention = "normalized_image_top_left", valid = true, personCount = 1, landmarks = landmarks,
                model = new NativeBodyRegistration.Model { sha256 = "59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a" } };
        }
    }
}
