using System.Linq;
using Scalpal.Exercises.Engine;
using Scalpal.Handoff;
using Scalpal.Robotics;

namespace Scalpal.Quest
{
    public sealed partial class NativeCaseSession
    {
        // The headset's own tracking is the robot's demonstration: the scene's ControllerMotionCapture stamps each
        // frame with the coach step, buffers the mark_incision frames, and they are posted to the coach when that
        // step completes (or when practice ends with frames for it). No capture component: nothing happens.
        ControllerMotionCapture motionCapture;
        bool motionCaptureResolved;
        ControllerMotionCapture MotionCapture
        {
            get
            {
                if (!motionCaptureResolved) { motionCapture = FindFirstObjectByType<ControllerMotionCapture>(); motionCaptureResolved = true; }
                return motionCapture;
            }
        }
        // The theatre setup's learner recording consent covers controller motion; without a handoff the capture's
        // own operator consent box applies.
        public bool RobotDemoConsented => HasHandoff ? HandoffRun.Preflight?.learnerCaptureConsented == true
            : MotionCapture && MotionCapture.OperatorConfirmedConsent;

        void StampRobotStep()
        {
            if (MotionCapture) MotionCapture.StepId = Practicing && exercise ? exercise.Current?.id ?? "" : "";
        }
        void ResetRobotDemo() { if (MotionCapture) MotionCapture.ResetDemo(); }
        public bool RobotDemoProgress(CaseResult result)
        {
            if (!MotionCapture) return false;
            bool demoStepDone = result.advanced && (result.stepId == ControllerMotionCapture.DemoStep
                || (exercise && exercise.CompletedMilestones?.Contains(ControllerMotionCapture.DemoStep) == true));
            if (!demoStepDone && !result.completed) return false;
            return MotionCapture.SubmitDemo(coachBaseUrl, coachSessionId, RobotDemoConsented);
        }
    }
}
