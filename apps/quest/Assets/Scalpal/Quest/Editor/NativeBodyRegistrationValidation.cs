using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic calibrated images/plane only; no headset, camera or participant observations.
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
            Assert(BodyRegistrationMath.TryPlane(points[0], points[1], points[2], new Vector3(0, 2, 0), out var plane), "reclining plane");
            Assert(!BodyRegistrationMath.TryPlane(Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, out _), "standing plane rejected");
            Assert(!BodyRegistrationMath.TryPlane(points[0], points[0], points[0], Vector3.up, out _), "degenerate plane rejected");
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
                && Vector3.Distance(hit, target) < .001f, "rotated tracking origin preserves calibrated ray lift");
            var eye = new Vector3(0, 2, 0);
            Assert(BodyRegistrationMath.ImageRay(new Ray(eye, new Vector3(-.2f, -1, -.6f)), new Ray(eye, new Vector3(1.8f, -1, -.6f)),
                new Ray(eye, new Vector3(-.2f, -1, 1.4f)), Vector3.down, new Vector2(.25f, .6f), out var calibrated)
                && BodyRegistrationMath.Intersect(calibrated, plane, Vector3.up, out var offCenter)
                && Vector3.Distance(offCenter, new Vector3(.3f, 1, .2f)) < .001f, "off-center principal point preserves pinhole ray rather than interpolating normalized directions");

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
                Set(body, "plane", plane); Set(body, "center", Vector3.up);
                Assert(!body.TryAccept(), "unobserved fit cannot be accepted");
                var reply = Reply(points);
                Action observe = () => body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup,
                    new Ray(new Vector3(0, 2, 0), new Vector3(-1, -1, -1)),
                    new Ray(new Vector3(0, 2, 0), new Vector3(1, -1, -1)),
                    new Ray(new Vector3(0, 2, 0), new Vector3(-1, -1, 1)), Vector3.down);
                observe(); Assert(body.CandidateValid && !body.Accepted, "one observation does not silently accept fit");
                Assert(!body.TryAccept(), "one sample is not stable"); observe(); observe();
                Assert(body.TryAccept(), "three stable observations plus explicit accept");
                Assert(Vector3.Distance(body.patientFrame.position, body.anatomyFit.TransformPoint(BodyRegistrationMath.SourceUmbilicus)) < .001f, "ports derive from fitted source umbilicus");
                reply.personCount = 2; observe(); Assert(!body.Accepted && !body.CandidateValid, "ambiguous person count closes gate"); reply.personCount = 1;
                observe(); observe(); observe(); Assert(body.TryAccept(), "recovery requires new acceptance");
                reply.landmarks[11].visibility = .2f; observe(); Assert(!body.Accepted, "occlusion closes gate"); reply.landmarks[11].visibility = .9f;
                observe(); observe(); observe(); Assert(body.TryAccept(), "second explicit acceptance");
                body.Process(reply, "synthetic-1", new Vector2Int(640, 480), Time.realtimeSinceStartup - 2, default, default, default, Vector3.down);
                Assert(!body.Accepted, "stale frame closes gate");
                reply.frameId = "foreign"; observe(); Assert(!body.CandidateValid, "wrong frame cannot lift into cached rays"); reply.frameId = "synthetic-1";
                reply.landmarks[12].index = 11; observe(); Assert(!body.CandidateValid, "duplicate landmarks rejected"); reply.landmarks[12].index = 12;
                reply.model.sha256 = "wrong"; observe(); Assert(!body.CandidateValid, "unknown model identity rejected");
                body.ResetFit(); Assert(!body.Accepted && !body.CandidateValid, "retry invalidates fit");
                presentation.passthrough = false; presentation.Apply();
                Assert(body.anatomyFit.position == Vector3.zero && body.patientFrame.position == Vector3.zero,
                    "full VR restores authored transforms after real-body fit");
                Set(body, "inFlight", true);
                typeof(NativeBodyRegistration).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(body, null);
                Assert(!(bool)typeof(NativeBodyRegistration).GetField("inFlight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(body),
                    "deactivation clears canceled observation before reactivation");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
            Debug.Log("SCALPAL_NATIVE_BODY_REGISTRATION_VALIDATION_OK checks=" + checks + " synthetic calibration/projection/gates; no physical alignment evidence");
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
