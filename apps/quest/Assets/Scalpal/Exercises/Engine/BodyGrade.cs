using System;
using System.Linq;
using System.Collections.Generic;
using Scalpal.Exercises.Data;

namespace Scalpal.Exercises.Engine
{
    [Serializable]
    public sealed class BodyGrade
    {
        public string rubric = "illustrative_v1_uncalibrated";
        public string reason;
        public bool complete;
        public string[] metMilestones, missingMilestones, guardrailIds, missingMetrics;
        public int correctDecisions, decisionCount;
        public double safetyPoints, decisionPoints, tissuePoints, earnedPoints;
        public int economyPoints = -1, availablePoints = 80, unscoredEconomyWeight = 20;
        public bool economyMeasured;
        public int actionCount;
        public double durationMs, bloodLostMl, activeBleeds;
        public bool contamination;
        public BodyGrade Copy()
        {
            var copy = (BodyGrade)MemberwiseClone();
            copy.metMilestones = (string[])metMilestones.Clone();
            copy.missingMilestones = (string[])missingMilestones.Clone();
            copy.guardrailIds = (string[])guardrailIds.Clone();
            copy.missingMetrics = (string[])missingMetrics.Clone();
            return copy;
        }
    }
    public static class BodyGrader
    {
        // Illustrative authored penalties, never a clinical proficiency band. Hand paths
        // are not measured; action counts and duration do not substitute for economy.
        public static BodyGrade Calculate(OpenBodyCase plan, BodyState body, IReadOnlyList<StepMistake> mistakes, string reason)
        {
            var met = plan.milestones.Where(m => m.predicates.All(body.Test)).Select(m => m.id).ToArray();
            var missing = plan.milestones.Where(m => !met.Contains(m.id)).Select(m => m.id).ToArray();
            int correct = plan.decisions.Count(d => body.Log.Reverse().FirstOrDefault(r => r.action.verb == "decide" &&
                d.choices.Contains(r.action.choice))?.action.choice == d.correctChoice);
            double blood = body.Get("", "bloodLostMl"), active = body.Get("", "activeBleeds");
            bool contaminated = body.Get("", "contamination") > 0;
            int penalties = mistakes.Sum(m => m.severity == "high" ? 10 : m.severity == "moderate" ? 5 : 2);
            double safety = Math.Max(0, 50 - penalties - Math.Min(10, blood / 10) - (active > 0 ? 5 : 0) - (contaminated ? 5 : 0));
            double decisions = plan.decisions.Length > 0 ? 20.0 * correct / plan.decisions.Length : 20;
            double tissue = Math.Max(0, 10 - body.Log.Sum(r => r.outcomes.Sum(o => o == "muscle_cut" ? 5 : o == "rough_handling" || o == "across_fibers" ? 1 : 0)));
            return new BodyGrade { reason = reason, complete = missing.Length == 0, metMilestones = met,
                missingMilestones = missing, guardrailIds = mistakes.Select(m => m.id).ToArray(),
                correctDecisions = correct, decisionCount = plan.decisions.Length, safetyPoints = safety,
                decisionPoints = decisions, tissuePoints = tissue, earnedPoints = safety + decisions + tissue,
                actionCount = body.Log.Count(r => !BodyState.IsTelemetry(r.action)), durationMs = body.Log.Count == 0 ? 0 : body.Log[body.Log.Count-1].action.timeMs - body.Log[0].action.timeMs,
                bloodLostMl = blood, activeBleeds = active, contamination = contaminated,
                missingMetrics = new[]{ "leftHandPathLengthM", "rightHandPathLengthM", "calibratedEconomyThresholds", "hintsUsed" } };
        }
    }
}
