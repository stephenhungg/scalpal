// Step engine for one procedure. Mirrors services/preop/src/engine.ts exactly; the .NET check in
// services/preop/unity-check plays every bundled case through this class to prove none dead-ends.
// Pure C# so scene code feeds it events (collider touches, voice identifications, UI confirms).
using System;
using System.Collections.Generic;
using Scalpal.Exercises.Data;

namespace Scalpal.Exercises.Engine
{
    public enum CaseEventType
    {
        PlacePort,
        Touch,
        Identify,
        Confirm,
    }

    public struct CaseEvent
    {
        public CaseEventType type;
        public string id;
        public string instrumentId;

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

        // The ideal events for a step: drives demo autoplay and the .NET playthrough check.
        public static IEnumerable<CaseEvent> PerfectEvents(ProcedureStep step)
        {
            var check = step.check;
            var targets = check.targets ?? Array.Empty<string>();
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
