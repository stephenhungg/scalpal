// Step engine for one procedure. Mirrors services/preop/src/engine.ts exactly; the .NET check in
// services/preop/unity-check plays every bundled case through this class to prove none dead-ends.
// Pure C# so scene code feeds it events (collider touches, voice identifications, UI confirms).
using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Exercises.Data;

namespace Scalpal.Exercises.Engine
{
    public enum CaseEventType
    {
        PlacePort,
        Touch,
        Identify,
        Confirm,
        Surgery,
    }

    public struct CaseEvent
    {
        public CaseEventType type;
        public string id;
        public string instrumentId;
        public BodyAction evidence;

        public static CaseEvent Surgery(BodyAction evidence) => new CaseEvent { type = CaseEventType.Surgery, id = evidence?.tissueId ?? "", instrumentId = evidence?.instrumentId ?? "", evidence = evidence };

        public static CaseEvent PlacePort(string portId) => new CaseEvent { type = CaseEventType.PlacePort, id = portId, instrumentId = "" };
        public static CaseEvent Touch(string structureId, string instrumentId) => new CaseEvent { type = CaseEventType.Touch, id = structureId, instrumentId = instrumentId };
        public static CaseEvent Identify(string structureId) => new CaseEvent { type = CaseEventType.Identify, id = structureId, instrumentId = "" };
        public static CaseEvent Confirm() => new CaseEvent { type = CaseEventType.Confirm, id = "", instrumentId = "" };
    }

    public struct CaseResult
    {
        public bool advanced;
        public bool completed;
        public StepMistake mistake;
        public string stepId;
    }

    public class CaseRunner
    {
        readonly Dictionary<string, ProcedureStep> steps = new Dictionary<string, ProcedureStep>();
        readonly HashSet<string> progress = new HashSet<string>();
        readonly List<StepMistake> mistakes = new List<StepMistake>();
        int applied;
        readonly HashSet<string> achieved = new HashSet<string>();
        readonly List<string> orderDeviations = new List<string>();
        public BodyState Body { get; }
        public IReadOnlyCollection<string> Achieved => achieved;
        public IReadOnlyCollection<string> CompletedMilestones => achieved;
        public IReadOnlyList<string> OrderDeviations => orderDeviations;

        public Procedure Procedure { get; }
        public ProcedureStep Current { get; private set; }
        public bool Completed => Current == null;
        public IReadOnlyList<StepMistake> Mistakes => mistakes;

        public event Action<ProcedureStep> StepStarted;
        public event Action<ProcedureStep> StepCompleted;
        public event Action<ProcedureStep, StepMistake> MistakeMade;
        public event Action CaseCompleted;

        public CaseRunner(Procedure procedure)
        {
            Procedure = procedure ?? throw new ArgumentNullException(nameof(procedure));
            Body = procedure.openBody == null ? null : new BodyState(procedure.openBody.tissues);
            foreach (var step in procedure.steps ?? Array.Empty<ProcedureStep>()) steps[step.id] = step;
            Current = Lookup(procedure.firstStep);
        }

        // Call once scene listeners are attached so the first step's UI and highlights appear.
        public void Begin()
        {
            if (Current != null) StepStarted?.Invoke(Current);
            else CaseCompleted?.Invoke();
        }

        public CaseResult Handle(CaseEvent e)
        {
            if (Body != null) return HandleBody(e);
            var step = Current;
            if (step == null) return new CaseResult { completed = true, stepId = "" };

            var mistake = MatchMistake(step, e);
            if (mistake != null)
            {
                mistakes.Add(mistake);
                MistakeMade?.Invoke(step, mistake);
                return new CaseResult { mistake = mistake, stepId = step.id };
            }

            var check = step.check;
            var targets = check.targets ?? Array.Empty<string>();
            switch (check.type)
            {
                case "place_ports":
                    if (e.type == CaseEventType.PlacePort && Array.IndexOf(targets, e.id) >= 0) progress.Add(e.id);
                    break;
                case "touch_target":
                    if (e.type == CaseEventType.Touch && e.instrumentId == step.instrumentId && Array.IndexOf(targets, e.id) >= 0) progress.Add(e.id);
                    break;
                case "identify_targets":
                    if (e.type == CaseEventType.Identify && Array.IndexOf(targets, e.id) >= 0) progress.Add(e.id);
                    break;
                case "apply_count":
                    if (e.type == CaseEventType.Touch && e.instrumentId == step.instrumentId && Array.IndexOf(targets, e.id) >= 0) applied++;
                    break;
                case "confirm":
                    if (e.type == CaseEventType.Confirm) progress.Add("confirm");
                    break;
            }

            if (!IsSatisfied(step)) return new CaseResult { stepId = step.id };

            progress.Clear();
            applied = 0;
            Current = string.IsNullOrEmpty(step.next) ? null : Lookup(step.next);
            StepCompleted?.Invoke(step);
            if (Current != null) StepStarted?.Invoke(Current);
            else CaseCompleted?.Invoke();
            return new CaseResult { advanced = true, completed = Current == null, stepId = step.id };
        }

        CaseResult HandleBody(CaseEvent e)
        {
            var previous = Current;
            var result = new CaseResult { completed = Completed, stepId = previous?.id ?? "" };
            if (e.type != CaseEventType.Surgery) return result;
            var record = Body.Apply(e.evidence);
            if (record == null) return result;
            var plan = Procedure.openBody;
            foreach (var rule in plan.guardrails ?? Array.Empty<BodyGuardrail>())
            {
                if ((!string.IsNullOrEmpty(rule.tissueId) && rule.tissueId != record.action.tissueId) ||
                    Array.IndexOf(record.outcomes, rule.outcome) < 0) continue;
                var mistake = new StepMistake { id = rule.id, trigger = "wrong_order", structure = record.action.tissueId,
                    severity = rule.severity, feedback = rule.feedback };
                mistakes.Add(mistake);
                if (result.mistake == null) result.mistake = mistake;
                MistakeMade?.Invoke(previous, mistake);
            }
            var satisfied = new HashSet<string>();
            foreach (var milestone in plan.milestones ?? Array.Empty<BodyMilestone>())
            {
                if (!(milestone.predicates ?? Array.Empty<BodyPredicate>()).All(Body.Test)) continue;
                satisfied.Add(milestone.id);
                if (!achieved.Add(milestone.id)) continue;
                if (previous != null && previous.id != milestone.id) orderDeviations.Add(milestone.id);
                result.advanced = true;
                var completed = Lookup(milestone.id);
                if (completed != null) StepCompleted?.Invoke(completed);
            }
            Current = (Procedure.steps ?? Array.Empty<ProcedureStep>()).FirstOrDefault(step => !satisfied.Contains(step.id));
            result.completed = Completed;
            if (Current != previous)
            {
                if (Current != null) StepStarted?.Invoke(Current);
                else CaseCompleted?.Invoke();
            }
            return result;
        }

        bool IsSatisfied(ProcedureStep step)
        {
            var check = step.check;
            if (check.type == "confirm") return progress.Contains("confirm");
            if (check.type == "apply_count") return applied >= check.count;
            foreach (var t in check.targets ?? Array.Empty<string>())
            {
                if (!progress.Contains(t)) return false;
            }
            return progress.Count >= check.count;
        }

        static StepMistake MatchMistake(ProcedureStep step, CaseEvent e)
        {
            foreach (var m in step.mistakes ?? Array.Empty<StepMistake>())
            {
                if (m.structure != e.id) continue;
                if (m.trigger == "wrong_identification" && e.type == CaseEventType.Identify) return m;
                if (m.trigger != "wrong_identification" && e.type == CaseEventType.Touch) return m;
            }
            return null;
        }

        ProcedureStep Lookup(string id) => !string.IsNullOrEmpty(id) && steps.TryGetValue(id, out var s) ? s : null;

        // Synthetic fixtures only. Runtime adapters provide actual measured actions.
        static IEnumerable<BodyAction> PerfectBodyActions(ProcedureStep step)
        {
            var events = new List<BodyAction>();
            BodyAction A(string verb, string tissue, string instrument, float length = 0, float depth = 0,
                float distance = 0, float separation = 0, float duration = 0, string choice = "", string instance = "tool-1", string secondary = "")
                => new BodyAction { verb = verb, tissueId = tissue, layer = tissue, instrumentId = instrument,
                    instrumentInstanceId = instance, secondaryInstanceId = secondary, registered = true, lengthMm = length, depthMm = depth,
                    distanceMm = distance, separationMm = separation, durationMs = duration, choice = choice };
            switch (step.id)
            {
                case "mark_incision": events.Add(A("mark", "skin", "skin_marker", length:60)); break;
                case "incise_skin":
                    events.Add(A("cut", "skin", "scalpel", length:60, depth:2));
                    events.Add(A("cut", "fat", "scalpel", length:60, depth:10)); break;
                case "open_fascia": events.Add(A("cut", "fascia", "metzenbaum_scissors", length:60)); break;
                case "split_muscle": events.Add(A("retract", "muscle", "retractor", separation:20, secondary:"retractor-2")); break;
                case "open_peritoneum":
                    events.Add(A("grasp", "peritoneum", "toothed_forceps", depth:10));
                    events.Add(A("cut", "peritoneum", "scalpel", length:4)); break;
                case "deliver_appendix": events.Add(A("grasp", "appendix", "babcock", depth:20)); break;
                case "divide_mesoappendix":
                    events.Add(A("clamp", "mesoappendix", "hemostat", distance:5, instance:"clamp-a"));
                    events.Add(A("clamp", "mesoappendix", "hemostat", distance:15, instance:"clamp-b"));
                    events.Add(A("cut", "mesoappendix", "metzenbaum_scissors", length:4, distance:10));
                    events.Add(A("tie", "mesoappendix", "suture_tie", distance:5));
                    events.Add(A("tie", "mesoappendix", "suture_tie", distance:15)); break;
                case "ligate_base":
                    events.Add(A("decide", "appendix", "decision", choice:"true_base"));
                    events.Add(A("clamp", "appendix", "right_angle_clamp", distance:3));
                    events.Add(A("tie", "appendix", "suture_tie", distance:3));
                    events.Add(A("clamp", "appendix", "right_angle_clamp", distance:8));
                    events.Add(A("cut", "appendix", "scalpel", length:4, distance:4)); break;
                case "inspect_clean":
                    events.Add(A("suction", "mesoappendix", "suction_irrigator", duration:1000));
                    events.Add(A("inspect", "mesoappendix", "suction_irrigator", duration:1000));
                    events.Add(A("inspect", "appendix", "suction_irrigator", duration:1000)); break;
                case "close": events.Add(A("close", "skin", "assistant")); break;
            }
            for (int i = 0; i < events.Count; i++) { events[i].actionId = step.id + "-" + i; yield return events[i]; }
        }

        // The ideal events for a step: drives demo autoplay and the .NET playthrough check.
        public static IEnumerable<CaseEvent> PerfectEvents(ProcedureStep step)
        {
            var check = step.check;
            var targets = check.targets ?? Array.Empty<string>();
            if (check.type == "body_predicate") { foreach (var e in PerfectBodyActions(step)) yield return CaseEvent.Surgery(e); yield break; }
            switch (check.type)
            {
                case "place_ports":
                    foreach (var t in targets) yield return CaseEvent.PlacePort(t);
                    break;
                case "touch_target":
                    foreach (var t in targets) yield return CaseEvent.Touch(t, step.instrumentId);
                    break;
                case "identify_targets":
                    foreach (var t in targets) yield return CaseEvent.Identify(t);
                    break;
                case "apply_count":
                    for (var i = 0; i < check.count; i++) yield return CaseEvent.Touch(targets.Length > 0 ? targets[0] : "", step.instrumentId);
                    break;
                case "confirm":
                    yield return CaseEvent.Confirm();
                    break;
            }
        }
    }
}
