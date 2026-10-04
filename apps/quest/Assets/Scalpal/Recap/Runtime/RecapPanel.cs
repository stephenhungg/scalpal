using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.Recap
{
    // Only the essentials: diagnosis and surgery scores, the robot replay with one result line (plus one
    // provenance line when the robot learned from synthetic demos), and the two ways out.
    public sealed class RecapPanel : MonoBehaviour
    {
        public RecapController controller;
        public TextMeshPro diagnosisLabel, diagnosisValue, surgeryLabel, surgeryValue, robotLine, robotNote, emptyLine;
        public GameObject scoreRoot, robotRoot, videoSurface;
        public RecapButton exploreButton, retryButton;
        public const string Pending = "Robot learning…", Unavailable = "Robot replay unavailable", Empty = "No result for this run";

        public void Refresh()
        {
            if (!controller) return;
            var r = controller.Result;
            scoreRoot.SetActive(r != null); robotRoot.SetActive(r != null); emptyLine.gameObject.SetActive(r == null);
            retryButton.gameObject.SetActive(r != null); exploreButton.gameObject.SetActive(true);
            if (r != null)
            {
                diagnosisLabel.text = "Diagnosis"; diagnosisValue.text = Clinical(r);
                surgeryLabel.text = r.surgery?.demoAssisted == true ? "Surgery · assisted" : "Surgery"; surgeryValue.text = Procedural(r);
                robotLine.text = RobotLine(controller.Robot, controller.RobotReply, controller.RobotPathErrorKnown);
                robotNote.text = controller.Robot == RecapController.RobotState.Ready ? RobotNote(controller.RobotReply) : "";
                robotNote.gameObject.SetActive(robotNote.text.Length > 0);
                videoSurface.SetActive(controller.Robot == RecapController.RobotState.Ready && controller.replay.Showing);
            }
            foreach (var fit in GetComponentsInChildren<ScalpalTextFit>(true)) fit.Fit();
        }
        public static string Clinical(RunResult r)
        {
            if (r.diagnosisSkipped) return "Skipped";
            if (!r.diagnosisAvailable || r.diagnosis == null) return "Unavailable";
            return r.diagnosis.total + " / " + r.diagnosis.max;
        }
        public static string Procedural(RunResult r)
        {
            var s = r.surgery;
            if (s?.available != true) return "Unavailable";
            return s.total.ToString("0.#") + " / " + s.max.ToString("0.#");
        }
        public static string RobotLine(RecapController.RobotState state, RecapController.RobotResult reply, bool pathErrorKnown)
        {
            if (state == RecapController.RobotState.Pending) return Pending;
            if (state != RecapController.RobotState.Ready || reply == null) return Unavailable;
            string step = string.IsNullOrWhiteSpace(reply.stepTitle) ? reply.stepId.Replace('_', ' ') : reply.stepTitle.Trim();
            if (step.Length > 0) step = char.ToUpperInvariant(step[0]) + step.Substring(1);
            return "Robot · " + step + " · " + (reply.success ? "Success" : "Missed") + (pathErrorKnown ? " · " + reply.pathErrorMm.ToString("0") + " mm" : "");
        }
        // Provenance only when synthetic data trained the policy; never claims headset demos it did not use.
        public static string RobotNote(RecapController.RobotResult reply)
        {
            if (reply == null || !reply.synthetic) return "";
            int human = reply.demos?.human ?? 0, synthetic = reply.demos?.synthetic ?? 0;
            if (human + synthetic <= 0) return "";
            return human > 0 ? "Learned from " + human + " headset + " + synthetic + " synthetic demos" : "Learned from " + synthetic + " demos (synthetic)";
        }
    }
}
