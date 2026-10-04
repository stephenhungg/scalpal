using System;
using System.Linq;
using System.Reflection;
using Scalpal.Exercises.Engine;
using Scalpal.Handoff;
using Scalpal.Quest;
using Scalpal.Recap;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Robotics.EditorTools
{
    // The robot learns from the headset's own tracking. Why it matters: if the OR scene has no capture, a capture
    // in the wrong frame, or the mark_incision frames never reach the coach's robot-demo route in the schema the Mac
    // validates, the robot trains on synthetic demos only and the demo claim is false. Also proves the retired
    // passthrough hand-clip recorder can no longer start from the flow. Actual NativeSession scene; stub request
    // sink instead of HTTP; synthetic frame times. Not headset evidence.
    public static class RobotDemoValidation
    {
        static int checks;
        static void Check(bool pass, string message) { checks++; if (!pass) throw new InvalidOperationException("Robot demo validation: " + message); }
        [Serializable] sealed class DemoBody { public string stepId; public ControllerMotionFrame[] frames; }

        public static void Run()
        {
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var sink = ControllerMotionCapture.RequestSink;
            var savedPreflight = HandoffRun.Preflight.learnerCaptureConsented;
            try
            {
                var scene = EditorSceneManager.OpenScene(ControllerMotionSetup.NativeSessionScene, OpenSceneMode.Single);
                var captures = UnityEngine.Object.FindObjectsByType<ControllerMotionCapture>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                Check(captures.Length == 1, "the OR scene has exactly one ControllerMotionCapture (found " + captures.Length + ")");
                var capture = captures[0];
                var patient = scene.GetRootGameObjects().SingleOrDefault(root => root.name == "PatientRoot");
                Check(patient && capture.PoseRoot == patient.transform, "Pose Root is the scene's PatientRoot");
                Check(capture.gameObject.activeSelf && capture.enabled, "the capture runs in the OR");
                Check(!(bool)Field(capture, "streaming") && !capture.OperatorConfirmedConsent, "no UDP stream or on-device recording by default (USB + adb reverse is TCP only)");

                var session = UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
                Check(session, "the OR scene has its NativeCaseSession");
                Field(session, "coachSessionId", "coach-robotdemo");
                session.coachBaseUrl = "http://localhost:8787";
                string url = null, body = null; int posts = 0;
                ControllerMotionCapture.RequestSink = (u, b) => { url = u; body = b; posts++; };

                // The session stamps the coach step onto the capture only while practicing.
                SetProperty(session, "Phase", "Startup"); Invoke(session, "StampRobotStep");
                Check(capture.StepId == "", "no step is stamped outside practice");

                // One real frame through the capture's own Update: stamped and in the patient frame.
                capture.StepId = ControllerMotionCapture.DemoStep;
                Invoke(capture, "Update");
                var real = capture.LastFrame;
                Check(real != null && real.stepId == "mark_incision" && real.space == "patient" && real.controllers.Length == 2, "captured frames carry the coach step and patient space");
                capture.ResetDemo();
                Feed(capture, "incise_skin", 0, 40);
                Check(capture.DemoFrameCount == 0, "frames of other steps are not part of the mark_incision demo");
                Feed(capture, "mark_incision", 10, 300);
                int buffered = capture.DemoFrameCount;
                Check(buffered >= 140 && buffered <= 151, "mark_incision frames are buffered at about 30 Hz (" + buffered + " of 300 at 50 Hz)");

                // Without the learner's motion-recording consent nothing leaves the headset.
                HandoffRun.Preflight.learnerCaptureConsented = false;
                Check(!session.RobotDemoProgress(new CaseResult { advanced = true, stepId = "mark_incision" }) && posts == 0, "no consent, no post");
                Check(capture.DemoFrameCount == buffered, "a refused post keeps the buffer in memory");
                // Consent (the operator box when there is no theatre handoff): completing mark_incision posts once.
                Field(capture, "operatorConfirmedConsent", true);
                Check(!session.RobotDemoProgress(new CaseResult { advanced = true, stepId = "open_fascia" }) && posts == 0, "completing another step does not post the demo");
                Check(session.RobotDemoProgress(new CaseResult { advanced = true, stepId = "mark_incision" }) && posts == 1, "completing mark_incision posts the buffered frames");
                Check(url == "http://localhost:8787/coach/sessions/coach-robotdemo/robot-demo", "posted to the coach robot-demo route: " + url);
                var demo = JsonUtility.FromJson<DemoBody>(body);
                Check(demo.stepId == "mark_incision" && demo.frames.Length == buffered, "body is {stepId, frames} with every buffered frame");
                Check(demo.frames.All(f => f.schema == "scalpal.controller_motion.v1" && f.space == "patient" && f.stepId == "mark_incision"
                    && f.headPosition.Length == 3 && f.headRotation.Length == 4 && f.controllers.Length == 2
                    && f.controllers.All(c => (c.hand == "left" || c.hand == "right") && c.position.Length == 3 && c.rotation.Length == 4 && c.heldInstrument != null)),
                    "every frame matches the coach's scalpal.controller_motion.v1 validator");
                Check(demo.frames.Select(f => f.unityTime).SequenceEqual(demo.frames.Select(f => f.unityTime).OrderBy(t => t)), "frames stay in capture order");
                Check(body.Contains("\"heldInstrument\":\"skin_marker\"") && body.Length < ControllerMotionCapture.MaxDemoBytes, "held instrument kept; body under 3 MB");
                Check(!session.RobotDemoProgress(new CaseResult { advanced = true, completed = true, stepId = "close" }) && posts == 1, "practice end does not post the same demo twice");

                // A new attempt: practice that ends with mark_incision frames posts them at the end.
                capture.ResetDemo(); Feed(capture, "mark_incision", 100, 50);
                Check(session.RobotDemoProgress(new CaseResult { completed = true, stepId = "" }) && posts == 2, "practice ending with mark_incision frames posts them");
                // A long, slow marking step stays under the coach's 3 MB limit.
                capture.ResetDemo(); Feed(capture, "mark_incision", 200, 20000, 1f / 90);
                Check(capture.DemoBytes <= ControllerMotionCapture.DemoBudgetBytes && capture.DemoFrameCount > 1000, "the buffer halves its rate to fit (" + capture.DemoFrameCount + " frames, " + capture.DemoBytes + " bytes)");
                Check(session.RobotDemoProgress(new CaseResult { advanced = true, stepId = "mark_incision" }) && body.Length < ControllerMotionCapture.MaxDemoBytes, "a long demo still posts under 3 MB (" + body.Length + " bytes)");
                // With a theatre handoff the learner's setup-card consent is the gate.
                Field(capture, "operatorConfirmedConsent", false);
                SetStatic(typeof(HandoffRun), "Current", new HandoffTicket());
                HandoffRun.Preflight.learnerCaptureConsented = true;
                Check(session.RobotDemoConsented, "the theatre setup's motion-recording consent covers the robot demo");
                HandoffRun.Preflight.learnerCaptureConsented = false;
                Check(!session.RobotDemoConsented, "withdrawn consent stops the robot demo");
                SetStatic(typeof(HandoffRun), "Current", null);

                // The passthrough hand-clip recorder is retired: no type, nothing subscribed to the recap segment.
                Check(!AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).Any(t => t.Name == "HandCaptureRecorder" || t.Namespace == "Scalpal.Capture"),
                    "HandCaptureRecorder and the Scalpal.Capture module are gone from the build");
                Check(typeof(NativeCaseSession).GetMethod("BeginRecapRun", BindingFlags.NonPublic | BindingFlags.Instance) != null, "the recap run still begins from practice");
                var go = new GameObject("Robot demo recap context");
                try
                {
                    var context = go.AddComponent<RecapRunContext>();
                    Check(typeof(RecapRunContext).GetField("SegmentStarted", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(context) == null
                        && go.GetComponents<Component>().Length == 2, "nothing records on the recap segment");
                }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            }
            finally
            {
                ControllerMotionCapture.RequestSink = sink;
                HandoffRun.Preflight.learnerCaptureConsented = savedPreflight;
                if (previous.Any(s => s.isLoaded) && previous.Any(s => s.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            Debug.Log("SCALPAL_ROBOT_DEMO_OK checks=" + checks + " scene=NativeSession poseRoot=PatientRoot route=/coach/sessions/:id/robot-demo stubSink=true headset=false");
        }

        // Synthetic tracked marker frames at 50 Hz (or the given step), as the capture would produce them.
        static void Feed(ControllerMotionCapture capture, string step, double start, int count, double dt = .02)
        {
            for (int i = 0; i < count; i++)
            {
                var frame = new ControllerMotionFrame
                {
                    stepId = step, frameIndex = i, unityTime = start + i * dt, space = "patient", headTracked = true,
                    headPosition = new[] { 0f, .5f, -.4f }, headRotation = new[] { 0f, 0f, 0f, 1f },
                    controllers = new[]
                    {
                        new ControllerSample { hand = "left", tracked = true, position = new[] { -.2f, .1f, 0f }, rotation = new[] { 0f, 0f, 0f, 1f } },
                        new ControllerSample { hand = "right", tracked = true, position = new[] { .05f + i * 1e-4f, .02f, .01f }, rotation = new[] { 0f, 0f, 0f, 1f }, grip = 1, trigger = .8f, heldInstrument = "skin_marker" },
                    },
                };
                capture.Buffer(frame);
            }
        }
        static System.Collections.Generic.IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }
        static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        static void Field(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value });
        static void SetStatic(Type type, string name, object value) => type.GetProperty(name, BindingFlags.Public | BindingFlags.Static).GetSetMethod(true).Invoke(null, new[] { value });
        static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null);
    }
}
