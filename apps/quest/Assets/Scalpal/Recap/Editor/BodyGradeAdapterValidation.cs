using System;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    public static class BodyGradeAdapterValidation
    {
        static int checks;
        static void Check(bool value, string label) { checks++; if (!value) throw new InvalidOperationException("Body grade adapter: " + label); }
        [MenuItem("Scalpal/Recap/Verify Body Grade Adapter")]
        public static void Run()
        {
            checks = 0;
            var bundle = JsonUtility.FromJson<ScalpalBundle>(Resources.Load<TextAsset>("scalpal_bundle").text);
            var selected = bundle.cases.First(c => c.procedureId == "open_appendectomy" && c.patientId == "patient-demo-multi-source");
            var root = new GameObject("Actual body-grade boundary");
            var integration = RecapSessionIntegration.Ensure();
            var context = integration.GetComponent<RecapRunContext>();
            try
            {
                var anatomy = root.AddComponent<AnatomyController>();
                foreach (var item in selected.anatomy)
                {
                    var shape = GameObject.CreatePrimitive(PrimitiveType.Cube); shape.transform.SetParent(root.transform, false);
                    var part = shape.AddComponent<AnatomyPart>(); part.stableId = item.id; part.system = item.system;
                }
                var binding = root.AddComponent<AnatomyExerciseBinding>(); binding.anatomy = anatomy;
                Check(binding.SelectCase(bundle, selected.caseId, true, out var reason), "actual open-body case selected: " + reason);
                anatomy.SetRegistrationValid(true);
                integration.Begin((Scalpal.EncounterOffice.EncounterSurgeryHandoff)null, selected, "body-session", "body-attempt", "body-coach", "http://localhost:8787", "");
                integration.Observe(binding);
                double actionTime = 0;
                // Actual perfect events from the producer, with explicit active-interaction timestamps.
                foreach (var step in selected.procedure.steps.Take(3))
                    foreach (var action in CaseRunner.PerfectEvents(step))
                    {
                        action.evidence.timeMs = actionTime += 250;
                        if (action.evidence.verb == "mark") action.evidence.choice = "assisted_premark";
                        Check(binding.Submit(action, out _, out reason), "real ideal action accepted: " + reason);
                    }
                var injury = new BodyAction { actionId = "body-adapter-injury", tissueId = "muscle", layer = "muscle", verb = "cut", instrumentId = "scalpel",
                    instrumentInstanceId = "real-fixture-scalpel", registered = true, lengthMm = 4, timeMs = actionTime += 250 };
                Check(binding.Submit(CaseEvent.Surgery(injury), out _, out reason), "accepted body injury reaches actual guardrail: " + reason);
                var decision = selected.procedure.openBody.decisions[0];
                var choice = new BodyAction { actionId = "body-adapter-choice", tissueId = "appendix", layer = "appendix", verb = "decide", instrumentId = "decision",
                    registered = true, choice = decision.choices[0], timeMs = actionTime += 250 };
                Check(binding.Submit(CaseEvent.Surgery(choice), out _, out reason), "actual recorded decision accepted: " + reason);
                Check(binding.Submit(CaseEvent.Finish(), out _, out reason), "real explicit Finish accepted: " + reason);
                var producer = binding.Grade;
                Check(producer != null && !producer.complete && producer.reason == "learner_finished", "real engine emits incomplete final grade");
                Check(integration.Complete(binding, "body-attempt"), "finalized incomplete attempt closes recap");
                Check(context.result.surgery.available && context.result.surgery.total == producer.earnedPoints && context.result.surgery.max == producer.availablePoints,
                    "recap consumes exact producer points including incomplete finish");
                var mapped = context.result.surgery;
                Check(!mapped.complete && mapped.completionReason == producer.reason && mapped.rubric == producer.rubric
                    && mapped.missingMilestones.SequenceEqual(producer.missingMilestones), "incomplete outcome and illustrative rubric retained without normalization");
                Check(mapped.milestones.Select(m => m.id).SequenceEqual(producer.metMilestones)
                    && mapped.milestones.All(m => m.timeKnown && m.atSeconds > 0), "milestone IDs come from final grade and times from observed accepted records");
                Check(mapped.guardrailViolations.Select(g => g.id).SequenceEqual(producer.guardrailIds)
                    && mapped.guardrailViolations.Any(g => g.id == "split_dont_cut" && g.timeKnown && g.atSeconds == injury.timeMs / 1000),
                    "real irreversible guardrail retains exact active-interaction time");
                Check(mapped.correctDecisions == producer.correctDecisions && mapped.decisionCount == producer.decisionCount && mapped.decisionSummaryAvailable,
                    "decision score is copied from producer aggregate");
                Check(mapped.decisions.Length == 1 && !mapped.decisions[0].correctnessAvailable && mapped.decisions[0].atSeconds == choice.timeMs / 1000
                    && mapped.decisions[0].label.Contains(choice.choice), "raw final decision recorded without regrading correctness");
                Check(!mapped.hintsAvailable && !mapped.economy.available && mapped.missingMetrics.SequenceEqual(producer.missingMetrics)
                    && mapped.bloodLossMl == producer.bloodLostMl, "missing hints/paths and measured blood loss retain producer meaning");
                Check(mapped.demoAssisted && !context.result.demo.enabled, "actual assisted premark stays marked even with operator flag off");
                Check(mapped.eventClock == "active_interaction", "source clock is explicit and not mislabeled OR elapsed time");
                context.result.replay.source = "learner"; context.result.replay.clockAligned = true; context.result.replay.durationSeconds = 60;
                Check(!RunResultContract.TryClipTime(context.result, mapped.guardrailViolations[0], out _), "different active/run clocks cannot scrub despite generic aligned flag");
                var late = BodyGradeAdapter.Snapshot(binding);
                Check(late.milestones.All(m => !m.timeKnown) && late.guardrailViolations.All(g => !g.timeKnown), "late snapshot never invents missing milestone or guardrail timestamps");
                Check(!integration.Complete(binding, "body-attempt"), "repeated producer finish cannot close recap twice");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(context.gameObject); }
            Debug.Log("SCALPAL_BODY_GRADE_ADAPTER_OK checks=" + checks);
        }
    }
}
