using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Semantic evidence only. This does not establish real tool contact, tissue physics,
    // registration quality, voice delivery or a complete physical headset playthrough.
    public static class OpenBodyValidation
    {
        static int checks;
        static void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Open body validation: " + message);
        }
        static BodyAction Action(string id, string verb, string tissue, string instrument, double time = 0, float distance = 0)
            => new BodyAction { actionId = id, verb = verb, tissueId = tissue, layer = tissue,
                instrumentId = instrument, instrumentInstanceId = id + "-tool", registered = true,
                timeMs = time, distanceMm = distance, lengthMm = verb == "cut" ? 4 : 0 };
        static void Expose(CaseRunner runner, Procedure procedure)
        {
            foreach (var step in procedure.steps.Take(5))
                foreach (var e in CaseRunner.PerfectEvents(step)) runner.Handle(e);
        }
        [MenuItem("Scalpal/Surgery/Validate Open Body")]
        public static void Run()
        {
            checks = 0;
            var asset = Resources.Load<TextAsset>("scalpal_bundle");
            Require(asset != null, "packaged bundle exists");
            var bundle = JsonUtility.FromJson<ScalpalBundle>(asset.text);
            var procedure = bundle?.procedures?.FirstOrDefault(p => p.id == "open_appendectomy");
            Require(procedure?.openBody != null, "packaged open appendectomy contains body definitions");
            Require(procedure.steps.Length == 10 && procedure.openBody.milestones.Length == 10, "ten authored milestones");
            Require(procedure.steps.All(s => s.check.type == "body_predicate"), "all open case checks use shared predicates");
            Require(procedure.openBody.decisions.Any(d => d.id == "true_base" && d.correctChoice == "true_base"), "true-base decision authored in data");
            VerifyPerfect(procedure);
            VerifyConsequences(procedure);
            VerifyGuardrailDeserialization(procedure);
            VerifyInputBoundary(procedure);
            VerifyBase(procedure);
            VerifySpatialControl(procedure);
            VerifyFinish(procedure);
            Debug.Log("SCALPAL_OPEN_BODY_VALIDATION_OK: " + checks + " synthetic body, case, off-path, timing and reset assertions; no physical headset test");
        }
        static void VerifyPerfect(Procedure procedure)
        {
            var runner = new CaseRunner(procedure);
            var started = new List<string>();
            var completed = new List<string>();
            int endings = 0;
            runner.StepStarted += s => started.Add(s.id);
            runner.StepCompleted += s => completed.Add(s.id);
            runner.CaseCompleted += () => endings++;
            runner.Begin();
            int actions = 0;
            foreach (var step in procedure.steps)
            {
                Require(runner.Current?.id == step.id, "expected guidance before " + step.id);
                bool advanced = false;
                foreach (var e in CaseRunner.PerfectEvents(step)) { actions++; advanced |= runner.Handle(e).advanced; }
                Require(advanced && runner.Achieved.Contains(step.id), "ideal actions satisfy " + step.id);
            }
            Require(runner.Completed && endings == 1, "ideal run completes exactly once");
            Require(runner.Mistakes.Count == 0 && runner.OrderDeviations.Count == 0, "ideal run has no safety or order deviations");
            Require(started.SequenceEqual(procedure.steps.Select(s => s.id)), "expected guidance sequence");
            Require(completed.Distinct().Count() == 10 && completed.Count == 10, "all milestone callbacks fire once");
            Require(runner.Body.Log.Count == actions, "every submitted ideal action logged");
            Require(runner.Body.Get("", "activeBleeds") == 0 && runner.Body.Get("", "bloodLostMl") == 0, "ideal run dry field");
            // Achievement callbacks are historical, readiness is evaluated against live state.
            // Inject the new bleed before closing so the organ remains physically exposed.
            var live = new CaseRunner(procedure);
            int liveCallbacks = 0;
            live.StepCompleted += _ => liveCallbacks++;
            foreach (var step in procedure.steps.Take(9)) foreach (var e in CaseRunner.PerfectEvents(step)) live.Handle(e);
            live.Handle(CaseEvent.Surgery(Action("new-bleed", "cut", "appendicular_artery", "scalpel", 1000)));
            Require(live.Current.id == "divide_mesoappendix", "new bleeding invalidates live no-active-bleed milestones");
            live.Handle(CaseEvent.Surgery(Action("new-seal", "seal", "appendicular_artery", "hook_cautery", 1000)));
            Require(live.Current.id == "close" && liveCallbacks == 9, "hemostasis restores readiness without duplicate milestone callbacks");
            var reset = new CaseRunner(procedure);
            Require(reset.Body.Log.Count == 0 && reset.Body.Get("muscle", "bladeUsed") == 0 && reset.Achieved.Count == 0,
                "new attempt restores empty body and milestone history");
        }
        static void VerifyConsequences(Procedure procedure)
        {
            var runner = new CaseRunner(procedure);
            int closeCallbacks = 0;
            runner.StepCompleted += s => { if (s.id == "close") closeCallbacks++; };
            runner.Handle(CaseEvent.Surgery(Action("early-close", "close", "skin", "assistant")));
            Require(runner.Achieved.Contains("close") && runner.Current.id == "mark_incision", "off-order milestone recognized without gating current guidance");
            Require(runner.OrderDeviations.Contains("close"), "order deviation recorded");
            runner.Handle(CaseEvent.Surgery(Action("repeat-close", "close", "skin", "assistant")));
            Require(closeCallbacks == 1, "repeated milestone state does not duplicate callback");
            runner.Handle(CaseEvent.Surgery(Action("hidden-cut", "cut", "terminal_ileum", "scalpel")));
            Require(runner.Body.Get("", "contamination") == 0 && runner.Body.Log.Last().outcomes.Contains("not_exposed"), "unexposed bowel cut physically blocked");
            Expose(runner, procedure);
            var injury = runner.Handle(CaseEvent.Surgery(Action("bowel-cut", "cut", "terminal_ileum", "scalpel")));
            Require(runner.Body.Get("", "contamination") == 1 && injury.mistake != null, "off-path bowel injury contaminates and emits guardrail");
            Require(runner.Body.Log.Last().outcomes.Contains("critical_injury"), "critical tissue consequence emitted");
            runner.Handle(CaseEvent.Surgery(Action("unsecured-cut", "cut", "mesoappendix", "scalpel", 1000)));
            Require(runner.Body.Get("", "activeBleeds") == 1 && runner.Mistakes.Any(m => m.id == "cut_before_control"), "cut before clamp bleeds and raises guardrail");
            runner.Handle(CaseEvent.Surgery(Action("control", "clamp", "mesoappendix", "hemostat", 2000)));
            Require(runner.Body.Get("", "activeBleeds") == 0 && Math.Abs(runner.Body.Get("", "bloodLostMl") - 2) < .0001,
                "clamp stops bleeding after one second of flow");
            Require(Math.Abs(runner.Body.Get("", "activeBleedSeconds") - 1) < .0001, "active bleed duration scored");
            runner.Handle(CaseEvent.Surgery(Action("artery-cut", "cut", "appendicular_artery", "scalpel", 3000)));
            runner.Handle(CaseEvent.Surgery(Action("artery-tie", "tie", "appendicular_artery", "suture_tie", 4000)));
            Require(runner.Body.Get("", "activeBleeds") == 0 && Math.Abs(runner.Body.Get("", "bloodLostMl") - 4) < .0001, "tie stops separate vessel bleeding");
            var suction = Action("clean", "suction", "mesoappendix", "suction_irrigator", 4000); suction.durationMs = 1000;
            runner.Handle(CaseEvent.Surgery(suction));
            Require(runner.Body.Get("", "poolMl") == 0 && runner.Body.Get("", "bloodLostMl") == 4, "suction clears pool without erasing blood-loss history");
            var dummy = new Procedure { id = "dummy", firstStep = procedure.firstStep, steps = procedure.steps,
                openBody = new OpenBodyCase { version = procedure.openBody.version, tissues = procedure.openBody.tissues, milestones = procedure.openBody.milestones,
                    guardrails = Array.Empty<BodyGuardrail>(), decisions = Array.Empty<BodyDecision>() } };
            var second = new CaseRunner(dummy);
            Expose(second, procedure);
            second.Handle(CaseEvent.Surgery(Action("other-bowel", "cut", "terminal_ileum", "scalpel")));
            Require(second.Body.Get("", "contamination") == 1 && second.Mistakes.Count == 0, "same tissue consequences in second case with different guardrails");
        }
        static void VerifyGuardrailDeserialization(Procedure procedure)
        {
            // Exercise the real Unity decoder, including its omitted nested-object behavior.
            var outcomeRule = JsonUtility.FromJson<BodyGuardrail>("{\"id\":\"regression_leak\",\"outcome\":\"hollow_leak\",\"tissueId\":\"\",\"severity\":\"high\",\"feedback\":\"Recorded leak.\"}");
            var numericRule = JsonUtility.FromJson<BodyGuardrail>("{\"id\":\"regression_mark\",\"outcome\":\"\",\"tissueId\":\"skin\",\"verb\":\"mark\",\"eventPredicate\":{\"tissueId\":\"\",\"fact\":\"distanceMm\",\"op\":\"gte\",\"value\":20},\"severity\":\"moderate\",\"feedback\":\"Recheck landmark placement.\"}");
            var malformed = new BodyGuardrail { id = "malformed_predicate", tissueId = "skin", verb = "mark",
                eventPredicate = new BodyPredicate { fact = "unsupported_measurement", op = "gte", value = 0 }, severity = "high", feedback = "Must not fire." };
            var plan = new Procedure { id = "guardrail_decode_fixture", steps = procedure.steps, firstStep = procedure.firstStep,
                openBody = new OpenBodyCase { version = procedure.openBody.version, tissues = procedure.openBody.tissues, milestones = procedure.openBody.milestones,
                    decisions = procedure.openBody.decisions, guardrails = new[]{ outcomeRule, numericRule, malformed } } };
            var runner = new CaseRunner(plan);
            var mark = Action("mark-inside", "mark", "skin", "skin_marker", distance:19.9f); mark.lengthMm = 60;
            Require(runner.Handle(CaseEvent.Surgery(mark)).mistake == null, "decoded numeric predicate rejects below-threshold event");
            mark = Action("mark-outside", "mark", "skin", "skin_marker", distance:20); mark.lengthMm = 60;
            Require(runner.Handle(CaseEvent.Surgery(mark)).mistake?.id == "regression_mark", "decoded numeric event predicate fires at authored inclusive threshold");
            var cut = Action("same-distance-wrong-verb", "cut", "skin", "scalpel", distance:25);
            Require(runner.Handle(CaseEvent.Surgery(cut)).mistake == null, "numeric guardrail retains its authored verb filter");
            Expose(runner, procedure);
            var injury = runner.Handle(CaseEvent.Surgery(Action("decoded-leak", "cut", "terminal_ileum", "scalpel")));
            Require(injury.mistake?.id == "regression_leak", "outcome guardrail survives an omitted eventPredicate after JsonUtility decode");
            outcomeRule.eventPredicate = new BodyPredicate();
            injury = runner.Handle(CaseEvent.Surgery(Action("default-nested-leak", "cut", "terminal_ileum", "scalpel")));
            Require(injury.mistake?.id == "regression_leak", "all-default nested predicate also preserves original outcome guardrail");
            Require(!runner.Mistakes.Any(m => m.id == "malformed_predicate"), "unknown nonempty predicate never broadens into an unconditional guardrail");
        }

        static void VerifyInputBoundary(Procedure procedure)
        {
            var state = new BodyState(procedure.openBody.tissues);
            var action = Action("sample", "mark", "skin", "skin_marker", 1000); action.lengthMm = 60;
            Require(state.Apply(action) != null, "valid registered action accepted");
            Require(state.Apply(action) == null && state.Log.Count == 1, "duplicate action rejected");
            action.position.x = 900;
            Require(state.Log[0].action.position.x == 0, "log snapshots input positions");
            action = Action("old", "mark", "skin", "skin_marker", 999);
            Require(state.Apply(action) == null, "backward action clock rejected");
            action.timeMs = 1001; action.registered = false;
            Require(state.Apply(action) == null, "invalid registration rejected");
            action.registered = true; action.coordinateFrame = "unity_world_m";
            Require(state.Apply(action) == null, "foreign coordinate frame rejected");
            action.coordinateFrame = "registered_torso_m"; action.lengthMm = float.NaN;
            Require(state.Apply(action) == null, "nonfinite measurement rejected");
            action.lengthMm = 1; action.layer = "fascia";
            Require(state.Apply(action) == null, "tissue-layer mismatch rejected");
            action.layer = "skin"; action.instrumentId = "scalpel";
            Require(state.Apply(action) == null, "unsupported tool verb rejected");
            action.instrumentId = "skin_marker";
            Require(state.Apply(action) != null, "rejected attempt does not consume action identity");
            Require(state.Log.Count == 2, "only accepted semantic actions enter history");
            Require(!state.Test(new BodyPredicate { tissueId = "appendix", fact = "stumpLengthMm", op = "lte", value = 5 }), "missing stump length cannot pass threshold");
            var split = new CaseRunner(procedure);
            foreach (var step in procedure.steps.Take(3)) foreach (var e in CaseRunner.PerfectEvents(step)) split.Handle(e);
            action = Action("one-retractor", "retract", "muscle", "retractor"); action.separationMm = 20;
            split.Handle(CaseEvent.Surgery(action));
            Require(split.Body.Get("muscle", "opened") == 0, "one retractor cannot create a two-handed split");
            action.actionId = "same-retractor"; action.secondaryInstanceId = action.instrumentInstanceId;
            split.Handle(CaseEvent.Surgery(action));
            Require(split.Body.Get("muscle", "opened") == 0, "one tool cannot impersonate two retractors");
        }
        static void VerifySpatialControl(Procedure procedure)
        {
            var runner = new CaseRunner(procedure);
            var touch = Action("zero-cut", "cut", "skin", "scalpel"); touch.lengthMm = 0;
            runner.Handle(CaseEvent.Surgery(touch));
            Require(runner.Body.Get("skin", "opened") == 0 && runner.Body.Log.Last().outcomes.Contains("no_cut"), "zero-length contact cannot open a layer");
            Expose(runner, procedure);
            var first = Action("clamp-a", "clamp", "mesoappendix", "hemostat", distance:5);
            var second = Action("clamp-b", "clamp", "mesoappendix", "hemostat", distance:15);
            runner.Handle(CaseEvent.Surgery(first)); runner.Handle(CaseEvent.Surgery(second));
            runner.Handle(CaseEvent.Surgery(Action("divide", "cut", "mesoappendix", "metzenbaum_scissors", distance:10)));
            runner.Handle(CaseEvent.Surgery(Action("tie-proximal-a", "tie", "mesoappendix", "suture_tie", distance:3)));
            runner.Handle(CaseEvent.Surgery(Action("tie-proximal-b", "tie", "mesoappendix", "suture_tie", distance:5)));
            Require(runner.Body.Get("mesoappendix", "tieCount") == 2 && !runner.Achieved.Contains("divide_mesoappendix"), "two ties on one side do not secure both sides of division");
            runner.Handle(CaseEvent.Surgery(Action("tie-distal", "tie", "mesoappendix", "suture_tie", distance:15)));
            Require(runner.Achieved.Contains("divide_mesoappendix"), "ties on both sides secure mesoappendix milestone");
            var baseRunner = new CaseRunner(procedure); Expose(baseRunner, procedure);
            foreach (var e in CaseRunner.PerfectEvents(procedure.steps.First(s => s.id == "ligate_base")))
            {
                if (e.evidence.verb == "clamp" && e.evidence.distanceMm > 5) continue;
                baseRunner.Handle(e);
            }
            Require(!baseRunner.Achieved.Contains("ligate_base") && baseRunner.Body.Get("appendix", "cutBetweenTieAndClamp") == 0,
                "short cut without distal clamp does not satisfy controlled base division");
            var muscle = new CaseRunner(procedure);
            foreach (var step in procedure.steps.Take(3)) foreach (var e in CaseRunner.PerfectEvents(step)) muscle.Handle(e);
            muscle.Handle(CaseEvent.Surgery(Action("muscle-cut", "cut", "muscle", "scalpel")));
            foreach (var e in CaseRunner.PerfectEvents(procedure.steps.First(s => s.id == "split_muscle"))) muscle.Handle(e);
            Require(muscle.Body.Get("muscle", "bladeUsed") == 1 && !muscle.Achieved.Contains("split_muscle"),
                "later retraction cannot erase prior muscle cutting");
        }

        static void VerifyFinish(Procedure procedure)
        {
            var runner = new CaseRunner(procedure);
            foreach (var step in procedure.steps.Take(3)) foreach (var e in CaseRunner.PerfectEvents(step)) runner.Handle(e);
            runner.Handle(CaseEvent.Surgery(Action("irreversible-muscle", "cut", "muscle", "scalpel", 1000)));
            var before = runner.Achieved.ToArray();
            int endings = 0; runner.CaseCompleted += () => endings++;
            var result = runner.Handle(CaseEvent.Finish());
            Require(result.completed && !result.advanced && runner.Completed, "explicit finish ends incomplete attempt");
            Require(before.SequenceEqual(runner.Achieved), "finish does not award missing milestones");
            var grade = runner.Grade;
            Require(!grade.complete && grade.missingMilestones.Contains("split_muscle") && grade.guardrailIds.Contains("split_dont_cut"), "grade preserves incomplete goals and irreversible safety findings");
            Require(grade.rubric == "illustrative_v1_uncalibrated" && grade.availablePoints == 80 && grade.unscoredEconomyWeight == 20 &&
                grade.economyPoints == -1 && !grade.economyMeasured, "missing economy metrics are explicitly unscored");
            Require(grade.actionCount == runner.Body.Log.Count && grade.durationMs == 1000 && grade.missingMetrics.Contains("leftHandPathLengthM"), "grade reports measured action count and clock duration");
            int count = runner.Body.Log.Count;
            runner.Handle(CaseEvent.Finish());
            foreach (var step in procedure.steps) foreach (var e in CaseRunner.PerfectEvents(step)) runner.Handle(e);
            Require(endings == 1 && runner.Body.Log.Count == count && runner.Completed, "repeated finish and later events cannot resume or mutate a final attempt");
            grade.missingMilestones[0] = "caller-mutated";
            Require(!runner.Grade.missingMilestones.Contains("caller-mutated"), "Grade access returns an isolated final snapshot");
            var perfect = new CaseRunner(procedure);
            foreach (var step in procedure.steps) foreach (var e in CaseRunner.PerfectEvents(step)) perfect.Handle(e);
            Require(perfect.Grade != null && perfect.Grade.complete && perfect.Grade.reason == "goals_reached" && perfect.Grade.earnedPoints == 80,
                "normal completion uses the same deterministic grade");
            count = perfect.Body.Log.Count;
            perfect.Handle(CaseEvent.Surgery(Action("after-completion", "cut", "cecum", "scalpel")));
            Require(perfect.Body.Log.Count == count, "normal completed attempt is frozen too");
        }

        static void VerifyBase(Procedure procedure)
        {
            var runner = new CaseRunner(procedure);
            Expose(runner, procedure);
            var step = procedure.steps.First(s => s.id == "ligate_base");
            foreach (var e in CaseRunner.PerfectEvents(step))
            {
                var input = e;
                if (input.evidence.verb == "decide") input.evidence.choice = "appendix_tip";
                if (input.evidence.verb == "cut") input.evidence.distanceMm = 12;
                runner.Handle(input);
            }
            Require(!runner.Achieved.Contains("ligate_base"), "wrong decision and long stump cannot complete base milestone");
            Require(runner.Body.Get("appendix", "stumpLengthMm") == 12, "long stump measurement remains queryable");
            runner.Handle(CaseEvent.Surgery(new BodyAction { actionId = "correct-decision", verb = "decide", tissueId = "appendix", layer = "appendix",
                instrumentId = "decision", registered = true, choice = "true_base" }));
            Require(!runner.Achieved.Contains("ligate_base"), "correct answer alone does not repair long stump");
            runner.Handle(CaseEvent.Surgery(Action("short-stump", "cut", "appendix", "scalpel", distance:4)));
            Require(runner.Achieved.Contains("ligate_base") && runner.Body.Get("appendix", "stumpLengthMm") == 4,
                "short tied stump and correct decision complete base independently of expected order");
            Require(runner.Current.id == "deliver_appendix", "earlier delivery guidance remains after base milestone");
        }
    }
}
