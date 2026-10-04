using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;

namespace Scalpal.Recap
{
    // Copies the surgery owner's final rubric. These observers retain event times;
    // they never evaluate body predicates, assign points, or advance a procedure.
    public sealed class BodyGradeAdapter : IDisposable
    {
        readonly AnatomyExerciseBinding exercise;
        readonly SurgicalCase selected;
        readonly Dictionary<string, TimedFact> milestones = new Dictionary<string, TimedFact>();
        readonly Dictionary<string, TimedFact> order = new Dictionary<string, TimedFact>();
        readonly List<TimedFact> guards = new List<TimedFact>();

        public BodyGradeAdapter(AnatomyExerciseBinding exercise)
        {
            this.exercise = exercise;
            selected = exercise ? exercise.SelectedCase : null;
            if (exercise) { exercise.StepCompleted += Milestone; exercise.MistakeMade += Guardrail; }
        }
        public void Dispose()
        {
            if (exercise) { exercise.StepCompleted -= Milestone; exercise.MistakeMade -= Guardrail; }
        }
        bool Matches(AnatomyExerciseBinding source) => source == exercise && source.SelectedCase == selected;
        TimedFact Observed(string id, string label)
        {
            var record = exercise.Body?.Log.LastOrDefault();
            return new TimedFact { id = id, label = string.IsNullOrEmpty(label) ? id : label,
                timeKnown = record?.action != null, atSeconds = record?.action == null ? 0 : record.action.timeMs / 1000 };
        }
        void Milestone(ProcedureStep step)
        {
            if (step == null || exercise.SelectedCase != selected) return;
            milestones[step.id] = Observed(step.id, step.title);
            if (exercise.OrderDeviations?.Contains(step.id) == true) order[step.id] = Observed(step.id, step.title);
        }
        void Guardrail(ProcedureStep step, StepMistake mistake)
        {
            if (mistake != null && exercise.SelectedCase == selected) guards.Add(Observed(mistake.id, mistake.feedback));
        }
        static TimedFact Copy(TimedFact fact) => new TimedFact { id = fact.id, label = fact.label, atSeconds = fact.atSeconds, timeKnown = fact.timeKnown };
        static TimedFact Untimed(string id, string label) => new TimedFact { id = id, label = string.IsNullOrEmpty(label) ? id : label, timeKnown = false };

        public static SurgeryGrade Snapshot(AnatomyExerciseBinding source, BodyGradeAdapter observed = null)
        {
            var grade = source ? source.Grade : null;
            var procedure = source?.SelectedCase?.procedure;
            if (grade == null || procedure?.openBody == null || source.Body == null) return new SurgeryGrade { available = false };
            if (observed != null && !observed.Matches(source)) observed = null;
            var steps = procedure.steps ?? Array.Empty<ProcedureStep>();
            var rules = procedure.openBody.guardrails ?? Array.Empty<BodyGuardrail>();
            string StepLabel(string id) => steps.FirstOrDefault(s => s.id == id)?.title ?? id;
            var guardsById = (observed?.guards ?? new List<TimedFact>()).GroupBy(f => f.id)
                .ToDictionary(g => g.Key, g => new Queue<TimedFact>(g));
            var mapped = new SurgeryGrade
            {
                available = true, total = grade.earnedPoints, max = grade.availablePoints,
                grade = "Illustrative · uncalibrated", rubric = grade.rubric, complete = grade.complete,
                completionReason = grade.reason, bloodLossMl = grade.bloodLostMl, eventClock = "active_interaction",
                missingMilestones = (string[])(grade.missingMilestones ?? Array.Empty<string>()).Clone(),
                missingMetrics = (string[])(grade.missingMetrics ?? Array.Empty<string>()).Clone(),
                hintsAvailable = false, decisionSummaryAvailable = true, correctDecisions = grade.correctDecisions, decisionCount = grade.decisionCount,
                economy = new SurgeryEconomy { available = false, durationSeconds = grade.durationMs / 1000 },
                demoAssisted = source.Body.Log.Any(r => r.action?.choice == "assisted_premark"),
                milestones = (grade.metMilestones ?? Array.Empty<string>()).Select(id => observed != null && observed.milestones.TryGetValue(id, out var fact)
                    ? Copy(fact) : Untimed(id, StepLabel(id))).ToArray(),
                guardrailViolations = (grade.guardrailIds ?? Array.Empty<string>()).Select(id => guardsById.TryGetValue(id, out var facts) && facts.Count > 0
                    ? Copy(facts.Dequeue()) : Untimed(id, rules.FirstOrDefault(r => r.id == id)?.feedback ?? id)).ToArray(),
                orderDeviations = (source.OrderDeviations ?? Array.Empty<string>()).Select(id => observed != null && observed.order.TryGetValue(id, out var fact)
                    ? Copy(fact) : Untimed(id, StepLabel(id))).ToArray()
            };
            // Preserve each final recorded choice, but don't duplicate the producer's
            // correctness calculation. Its aggregate counts above are authoritative.
            var choices = new List<DecisionFact>();
            foreach (var decision in procedure.openBody.decisions ?? Array.Empty<BodyDecision>())
            {
                var record = source.Body.Log.LastOrDefault(r => r.action?.verb == "decide" && (decision.choices ?? Array.Empty<string>()).Contains(r.action.choice));
                if (record == null) continue;
                choices.Add(new DecisionFact { id = decision.id, label = (decision.prompt ?? decision.id) + " — recorded choice: " + record.action.choice,
                    atSeconds = record.action.timeMs / 1000, timeKnown = true, correctnessAvailable = false });
            }
            mapped.decisions = choices.ToArray();
            return mapped;
        }
    }
}
