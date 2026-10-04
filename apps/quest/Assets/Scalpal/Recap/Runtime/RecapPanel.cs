using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Scalpal.Recap
{
    public sealed class RecapPanel : MonoBehaviour
    {
        public RecapController controller;
        public TextMesh title, subtitle, status, leftCard, rightCard, reflection, feedback, timeline, sourceLabel, robotLabel, demoLabel;
        public GameObject videoRoot, scoreRoot, reflectionRoot, sourcePlaceholder;
        public RecapButton[] errors;
        public RecapButton continueButton, exploreButton, retryButton, errorsNext;
        int errorOffset;
        public void NextErrors() { errorOffset += errors.Length; Refresh(); }
        public void Refresh()
        {
            if (!controller) return;
            var r = controller.Result;
            if (r == null) { if (status) status.text = controller.Notice; return; }
            bool replay = controller.Phase == "replay", scores = controller.Phase == "scores";
            videoRoot.SetActive(replay); scoreRoot.SetActive(scores); reflectionRoot.SetActive(!replay && !scores);
            title.text = replay ? "Your hands, reimagined" : scores ? "A moment to grow" : "Take a breath";
            subtitle.text = (r.isSample ? "SYNTHETIC PREVIEW  /  " : "") + "Explore  ·  Office  ·  OR  ·  Replay  ·  Recap";
            status.text = replay ? "Motion: " + r.replay.status + (r.replay.status == "failed" ? " — " + r.replay.failureReason : "") : controller.Notice;
            if (replay && !string.IsNullOrEmpty(controller.Notice)) status.text += "\n" + controller.Notice;
            if (replay && !string.IsNullOrEmpty(controller.replay.PlaybackError)) status.text += "\n" + controller.replay.PlaybackError;
            sourcePlaceholder.SetActive(controller.replay.Fallback || string.IsNullOrEmpty(r.replay.sourceVideoUrl));
            sourceLabel.text = controller.replay.Fallback ? "Your recording · unavailable in sample mode" : string.IsNullOrEmpty(r.replay.sourceVideoUrl) ? "Source recording unavailable" : r.replay.source == "learner" ? "Your recorded segment" : r.replay.source == "rehearsal" ? "Rehearsal recording · not this attempt" : "Synthetic source · not your recording";
            robotLabel.text = controller.replay.Fallback ? "Shadow hand · SYNTHETIC SAMPLE" : "Shadow hand · " + r.replay.source.ToUpperInvariant();
            reflection.text = controller.Phase == "reaction" ? "Jarvis\n\n" + controller.ReactionQuestion + "\n\nHold grip to respond, or reflect silently.\nYour reaction is not scored." : "Your reflection\n\n" + controller.SelfAssessmentQuestion + "\n\nChoose one moment to carry into your next run.\nYour answer is not scored.";
            leftCard.text = Clinical(r); rightCard.text = Procedural(r);
            var facts = RunResultContract.Feedback(r);
            feedback.text = "STRENGTHS\n" + (facts.strengths.Count == 0 ? "No logged evidence available." : string.Join("\n", facts.strengths)) + "\n\nNEXT TIME\n" + (facts.improvements.Count == 0 ? "No logged improvement facts available." : string.Join("\n", facts.improvements)) + "\n\n" + facts.takeAway;
            demoLabel.text = "Demo mode: " + (controller.DemoEnabled ? "ON · 20 s highlight" : "OFF");
            continueButton.gameObject.SetActive(!scores); exploreButton.gameObject.SetActive(scores); retryButton.gameObject.SetActive(scores);
            continueButton.label.text = replay ? "Reflect on this run" : controller.Phase == "reaction" ? "Self-assessment" : "Reveal scorecards";
            var items = r.surgery?.guardrailViolations ?? Array.Empty<TimedFact>();
            if (errorOffset >= items.Length) errorOffset = 0;
            errorsNext.gameObject.SetActive(replay && items.Length > errors.Length);
            for (int i = 0; i < errors.Length; i++)
            {
                int index = i + errorOffset; errors[i].index = index;
                bool visible = replay && r.surgery?.available == true && index < items.Length;
                errors[i].gameObject.SetActive(visible);
                if (!visible) continue;
                bool aligned = !controller.replay.Fallback && RunResultContract.TryClipTime(r, items[index], out var seconds) && seconds <= controller.replay.Duration;
                errors[i].label.text = items[index].atSeconds.ToString("0.0") + " s · " + items[index].label + (aligned ? " · seek" : " · unaligned");
            }
            foreach (var fit in GetComponentsInChildren<Scalpal.EncounterOffice.EncounterOfficeText>(true)) fit.Fit();
        }
        public static string Clinical(RunResult r)
        {
            var s = r.diagnosis;
            if (s == null) return "CLINICAL REASONING\n\nNot available\nThe encounter has not supplied its scorecard.";
            var b = new StringBuilder("CLINICAL REASONING\n" + s.total + " / " + s.max + "  ·  " + s.grade + "\n");
            foreach (var section in s.sections ?? Array.Empty<ScoreSection>()) b.AppendLine(section.label + "  " + section.score + "/" + section.max);
            b.AppendLine("Diagnosis: " + s.diagnosisResult);
            b.AppendLine("Critical found: " + string.Join(", ", (s.criticalFound ?? Array.Empty<ClinicalFact>()).Select(x => x.label)));
            b.AppendLine("Critical missed: " + string.Join(", ", (s.criticalMissed ?? Array.Empty<ClinicalFact>()).Select(x => x.label)));
            return b.ToString();
        }
        public static string Procedural(RunResult r)
        {
            var s = r.surgery;
            if (s?.available != true) return "PROCEDURAL SKILL\n\nNot available\nAwaiting the surgery grader.\nNo score has been inferred.";
            var b = new StringBuilder("PROCEDURAL SKILL\n" + s.total.ToString("0.#") + " / " + s.max.ToString("0.#") + "  ·  " + s.grade + "\n");
            b.AppendLine("Milestones reached: " + (s.milestones?.Length ?? 0));
            foreach (var item in (s.milestones ?? Array.Empty<TimedFact>()).Take(4)) b.AppendLine(item.atSeconds.ToString("0.0") + " s · " + item.label);
            b.AppendLine("Guardrails: " + (s.guardrailViolations?.Length ?? 0) + "  ·  Order deviations: " + (s.orderDeviations?.Length ?? 0));
            b.AppendLine("Blood loss: " + s.bloodLossMl.ToString("0.#") + " mL  ·  Hints: " + (s.hints?.Length ?? 0));
            b.AppendLine("Decisions: " + string.Join(", ", (s.decisions ?? Array.Empty<DecisionFact>()).Select(x => x.label + (x.correct ? " (correct)" : " (review)"))));
            b.AppendLine(s.economy?.available == true ? "Path L/R: " + s.economy.leftPathMeters.ToString("0.00") + " / " + s.economy.rightPathMeters.ToString("0.00") + " m · " + s.economy.durationSeconds.ToString("0") + " s" : "Motion economy: not measured");
            return b.ToString();
        }
        void Update()
        {
            if (controller?.Result == null || controller.Phase != "replay") return;
            var video = controller.replay;
            timeline.text = (video.Playing ? "Playing" : "Paused") + "  " + video.Position.ToString("0.0") + " / " + video.Duration.ToString("0.0") + " s\n" + RunResultContract.ReplayLabel(controller.Result.replay, video.Fallback);
        }
    }
}
