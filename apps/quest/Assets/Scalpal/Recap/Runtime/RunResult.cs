using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Scalpal.Recap
{
    // JSON fields deliberately match the server scorecard and companion. No grading happens here.
    [Serializable] public sealed class RunResult
    {
        public string schemaVersion = "scalpal.run_result.v1";
        public string runId = "", sessionId = "", attemptId = "", encounterId = "", patientId = "", procedureId = "";
        public bool isSample;
        public DiagnosisScorecard diagnosis;
        public SurgeryGrade surgery = new SurgeryGrade();
        public ReplayResult replay = new ReplayResult();
        public DemoFlags demo = new DemoFlags();
    }
    [Serializable] public sealed class DiagnosisScorecard
    {
        public int total, max;
        public string grade, procedureId, procedureTitle, diagnosisGiven, diagnosisExpected, diagnosisResult, spoken;
        public bool procedureChosenCorrectly;
        public ScoreSection[] sections = Array.Empty<ScoreSection>();
        public ClinicalFact[] criticalMissed = Array.Empty<ClinicalFact>(), criticalFound = Array.Empty<ClinicalFact>();
        public string[] differentialNamed = Array.Empty<string>(), differentialSuggestions = Array.Empty<string>(), feedback = Array.Empty<string>();
        public CarryoverRisk[] risksFound = Array.Empty<CarryoverRisk>(), risksMissed = Array.Empty<CarryoverRisk>();
    }
    [Serializable] public sealed class ScoreSection { public string id, label; public int score, max; public string[] found, missed; }
    [Serializable] public sealed class ClinicalFact { public string kind, id, label, why; }
    [Serializable] public sealed class CarryoverRisk { public string id, type, label, severity, source; }
    [Serializable] public class TimedFact { public string id, label; public double atSeconds; }
    [Serializable] public sealed class DecisionFact : TimedFact { public bool correct; }
    [Serializable] public sealed class SurgeryEconomy { public bool available; public double leftPathMeters, rightPathMeters, durationSeconds; }
    [Serializable] public sealed class SurgeryGrade
    {
        public bool available;
        public double total, max, bloodLossMl;
        public string grade = "";
        public TimedFact[] milestones = Array.Empty<TimedFact>(), guardrailViolations = Array.Empty<TimedFact>(), orderDeviations = Array.Empty<TimedFact>(), hints = Array.Empty<TimedFact>();
        public DecisionFact[] decisions = Array.Empty<DecisionFact>();
        public SurgeryEconomy economy = new SurgeryEconomy();
    }
    [Serializable] public sealed class ReplayResult
    {
        public string jobId = "", status = "failed", failureReason = "Capture has not supplied a motion job.";
        public string sourceVideoUrl = "", replayVideoUrl = "", source = "learner";
        public double durationSeconds, captureStartRunSeconds;
        public bool clockAligned;
    }
    [Serializable] public sealed class DemoFlags
    {
        public bool enabled, showSuggestedQuestions, skipMarking, preExpose, timeLapseNonKeySteps;
        public string patientId = "patient-demo-multi-source";
        public double replayHighlightSeconds = 20;
        public static DemoFlags JudgePath(bool enabled) => new DemoFlags { enabled = enabled, showSuggestedQuestions = enabled, skipMarking = enabled, preExpose = enabled, timeLapseNonKeySteps = enabled };
    }
    public sealed class RecapFeedback
    {
        public readonly List<string> strengths = new List<string>(), improvements = new List<string>();
        public string takeAway;
    }
    public static class RunResultContract
    {
        public static RunResult Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 262144) throw new ArgumentException("RunResult is empty or exceeds 256 KiB.");
            var result = JsonUtility.FromJson<RunResult>(json);
            Validate(result);
            return result;
        }
        public static void Validate(RunResult r)
        {
            if (r == null || r.schemaVersion != "scalpal.run_result.v1" || string.IsNullOrWhiteSpace(r.runId) || string.IsNullOrWhiteSpace(r.attemptId) || string.IsNullOrWhiteSpace(r.sessionId))
                throw new ArgumentException("RunResult needs its version, runId and attemptId.");
            if (r.replay == null || !new[] { "queued", "processing", "ready", "failed" }.Contains(r.replay.status)) throw new ArgumentException("Unknown replay state.");
            if (!new[] { "learner", "rehearsal", "sample" }.Contains(r.replay.source)) throw new ArgumentException("Unknown replay provenance.");
            if (r.replay.status == "failed" && string.IsNullOrWhiteSpace(r.replay.failureReason)) throw new ArgumentException("Failed replay needs a reason.");
            if (r.replay.status == "ready" && string.IsNullOrWhiteSpace(r.replay.replayVideoUrl)) throw new ArgumentException("Ready replay needs a video URL.");
            if (!Nonnegative(r.replay.durationSeconds) || !Nonnegative(r.replay.captureStartRunSeconds)) throw new ArgumentException("Invalid replay clock.");
            if (r.demo == null || !Nonnegative(r.demo.replayHighlightSeconds)) throw new ArgumentException("Invalid demo flags.");
            foreach (var url in new[] { r.replay.sourceVideoUrl, r.replay.replayVideoUrl })
                if (!string.IsNullOrEmpty(url) && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http" && uri.Scheme != "https")) throw new ArgumentException("Replay URLs must use HTTP(S).");
            if (r.diagnosis != null && (r.diagnosis.max <= 0 || r.diagnosis.total < 0 || r.diagnosis.total > r.diagnosis.max)) throw new ArgumentException("Invalid diagnosis score.");
            var s = r.surgery;
            if (s == null || !s.available) return;
            if (!Nonnegative(s.total) || !Nonnegative(s.max) || s.max <= 0 || s.total > s.max || !Nonnegative(s.bloodLossMl)) throw new ArgumentException("Invalid surgery grade.");
            foreach (var fact in (s.milestones ?? Array.Empty<TimedFact>()).Concat(s.guardrailViolations ?? Array.Empty<TimedFact>()).Concat(s.orderDeviations ?? Array.Empty<TimedFact>()).Concat(s.hints ?? Array.Empty<TimedFact>()).Concat(s.decisions ?? Array.Empty<DecisionFact>()))
                if (fact == null || string.IsNullOrWhiteSpace(fact.id) || string.IsNullOrWhiteSpace(fact.label) || !Nonnegative(fact.atSeconds)) throw new ArgumentException("Invalid timed fact.");
            if (s.economy != null && s.economy.available && (!Nonnegative(s.economy.leftPathMeters) || !Nonnegative(s.economy.rightPathMeters) || !Nonnegative(s.economy.durationSeconds))) throw new ArgumentException("Invalid measured economy.");
        }
        static bool Nonnegative(double n) => !double.IsNaN(n) && !double.IsInfinity(n) && n >= 0;
        public static RecapFeedback Feedback(RunResult r)
        {
            var f = new RecapFeedback();
            foreach (var item in r.diagnosis?.criticalFound ?? Array.Empty<ClinicalFact>()) if (!string.IsNullOrWhiteSpace(item?.label) && f.strengths.Count < 2) f.strengths.Add("Elicited: " + item.label);
            foreach (var item in r.diagnosis?.criticalMissed ?? Array.Empty<ClinicalFact>()) if (!string.IsNullOrWhiteSpace(item?.label) && f.improvements.Count < 2) f.improvements.Add("Revisit: " + item.label);
            if (r.surgery?.available == true)
            {
                foreach (var item in r.surgery.milestones ?? Array.Empty<TimedFact>()) if (f.strengths.Count < 2) f.strengths.Add("Reached: " + item.label);
                foreach (var item in r.surgery.guardrailViolations ?? Array.Empty<TimedFact>()) if (f.improvements.Count < 2) f.improvements.Add("Review: " + item.label + " at " + item.atSeconds.ToString("0.0") + " s");
            }
            f.takeAway = f.improvements.Count > 0 ? "Next attempt — " + f.improvements[0] : f.strengths.Count > 0 ? "Carry forward — " + f.strengths[0] : "No logged facts yet. Reflect on one deliberate action for your next attempt.";
            return f;
        }
        public static bool TryClipTime(RunResult r, TimedFact fact, out double seconds)
        {
            seconds = fact.atSeconds - r.replay.captureStartRunSeconds;
            return r.replay.source == "learner" && r.replay.clockAligned && seconds >= 0 && seconds <= r.replay.durationSeconds;
        }
        public static string ReplayLabel(ReplayResult r, bool fallback) => fallback ? "SYNTHETIC SAMPLE · not your recording. Kinematic replay, not a trained robot." : r.source == "rehearsal" ? "REHEARSAL CLIP · not this attempt. Kinematic replay, not a trained robot." : r.source == "sample" ? "SYNTHETIC SAMPLE · not your recording. Kinematic replay, not a trained robot." : "Your hand motion, retargeted to a robot hand. Kinematic replay, not a trained robot.";
    }
}
