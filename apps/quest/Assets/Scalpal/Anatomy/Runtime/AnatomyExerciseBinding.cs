using System;
using System.Collections.Generic;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Exercises.Coach;
using UnityEngine;

namespace Scalpal.Anatomy
{
    // Scene input boundary. Every scoring input, including UI and voice identification, goes here.
    // EventHandled is the integration point for an explicitly matched coach session.
    [DisallowMultipleComponent]
    public sealed class AnatomyExerciseBinding : MonoBehaviour
    {
        public AnatomyController anatomy;
        public CoachRelay coach;
        [Tooltip("Live mode pauses input until a fresh matching Jarvis session is synchronized. Off runs locally.")]
        public bool requireCoachSynchronization;
        [Tooltip("Explicit fresh coach session created for the selected case. Blank retains the legacy current-session lookup.")]
        public string explicitCoachSessionId = "";
        public string presentationMode = "mixed_reality";
        CaseRunner runner;
        SurgicalCase selectedCase;
        AnatomyController selectedAnatomy;
        CoachRelay selectedCoach;
        bool liveAttempt;
        string boundSessionId = "";
        string selectedPresentationMode = "";
        readonly HashSet<string> instruments = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> structures = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> ports = new HashSet<string>(StringComparer.Ordinal);
        public string SelectedInstrumentId { get; private set; } = "";
        public SurgicalCase SelectedCase => selectedCase;
        public ProcedureStep Current => runner?.Current;
        public BodyState Body => runner?.Body;
        public bool Completed => runner != null && runner.Completed;
        public bool CanScore => isActiveAndEnabled && runner != null && anatomy != null &&
            anatomy == selectedAnatomy && anatomy.isActiveAndEnabled && anatomy.RegistrationValid && !anatomy.PreviewMode &&
            requireCoachSynchronization == liveAttempt && (!liveAttempt || (presentationMode == selectedPresentationMode && CoachMatches));
        public bool CoachMatches => coach != null && coach == selectedCoach && selectedCase != null && coach.Connected && coach.IsSynchronized &&
            (string.IsNullOrEmpty(boundSessionId) || coach.SessionId == boundSessionId) &&
            coach.SessionPatientId == selectedCase.patientId && coach.SessionProcedureId == selectedCase.procedureId &&
            coach.SessionCaseId == selectedCase.caseId && coach.SessionMode == selectedPresentationMode &&
            coach.SessionInitialStepId == selectedCase.procedure.firstStep;
        public event Action<ProcedureStep> StepStarted;
        public event Action<ProcedureStep, StepMistake> MistakeMade;
        public event Action CaseCompleted;
        public event Action<CaseEvent, CaseResult> EventHandled;

        // Review-required cases need an explicit UI acknowledgement of their synthetic brief.
        // A failed selection preserves the current attempt and reports precisely what blocked it.
        public bool SelectCase(ScalpalBundle bundle, string caseId, bool acknowledgeReview, out string reason)
        {
            SurgicalCase candidate = null;
            foreach (var item in bundle?.cases ?? Array.Empty<SurgicalCase>())
            {
                if (item == null || item.caseId != caseId) continue;
                if (candidate != null) return Reject("duplicate case id", out reason);
                candidate = item;
            }
            if (candidate == null || string.IsNullOrWhiteSpace(caseId)) return Reject("case not in bundle", out reason);
            if (candidate.status != "ready" && !(candidate.status == "needs_review" && acknowledgeReview))
                return Reject("case unavailable or requires brief review", out reason);
            if (anatomy == null) return Reject("anatomy unavailable", out reason);
            if (requireCoachSynchronization && presentationMode != "virtual" && presentationMode != "mixed_reality")
                return Reject("unknown presentation mode", out reason);
            var procedure = candidate.procedure;
            if (procedure == null || procedure.id != candidate.procedureId || procedure.steps == null || procedure.steps.Length == 0)
                return Reject("invalid procedure", out reason);
            var required = new HashSet<string>(StringComparer.Ordinal);
            var allowedInstruments = new HashSet<string>(StringComparer.Ordinal);
            foreach (var instrument in candidate.instruments ?? Array.Empty<Instrument>())
                if (instrument != null && !string.IsNullOrEmpty(instrument.id)) allowedInstruments.Add(instrument.id);
            var allowedPorts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in procedure.ports ?? Array.Empty<Port>()) if (port != null) allowedPorts.Add(port.id);
            var steps = new Dictionary<string, ProcedureStep>(StringComparer.Ordinal);
            foreach (var step in procedure.steps)
            {
                if (step == null || string.IsNullOrEmpty(step.id) || steps.ContainsKey(step.id) || step.check == null ||
                    !allowedInstruments.Contains(step.instrumentId)) return Reject("invalid step or instrument", out reason);
                steps.Add(step.id, step);
                if (step.check.type == "touch_target" || step.check.type == "identify_targets" || step.check.type == "apply_count")
                    foreach (var target in step.targets ?? Array.Empty<string>()) required.Add(target);
                foreach (var mistake in step.mistakes ?? Array.Empty<StepMistake>()) if (mistake != null) required.Add(mistake.structure);
                var check = step.check;
                if (check.type != "place_ports" && check.type != "touch_target" && check.type != "identify_targets" &&
                    check.type != "apply_count" && check.type != "confirm" && (check.type != "body_predicate" || procedure.openBody == null)) return Reject("unsupported authored check", out reason);
                foreach (var target in check.targets ?? Array.Empty<string>())
                {
                    if (check.type == "place_ports")
                    {
                        if (!allowedPorts.Contains(target)) return Reject("unknown procedure port", out reason);
                    }
                    else if (check.type != "confirm" && check.type != "body_predicate") required.Add(target);
                }
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var next = procedure.firstStep;
            while (!string.IsNullOrEmpty(next))
            {
                if (!steps.TryGetValue(next, out var step) || !visited.Add(next)) return Reject("broken or cyclic step graph", out reason);
                next = step.next;
            }
            if (visited.Count != steps.Count) return Reject("unreachable procedure steps", out reason);
            anatomy.RebuildIndex();
            var missing = new List<string>();
            foreach (var id in required)
            {
                if (!anatomy.TryGetPart(id, out var part) || !HasOwnedCollider(part)) missing.Add(id ?? "<empty>");
            }
            if (missing.Count > 0) { missing.Sort(StringComparer.Ordinal); return Reject("missing anatomy or collider: " + string.Join(", ", missing), out reason); }
            var displayed = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (var id in procedure.structures ?? Array.Empty<string>())
                if (anatomy.TryGetPart(id, out _)) displayed.Add(id);
            anatomy.ShowAllSystems();
            anatomy.SetExerciseParts(displayed);
            anatomy.SetPreviewRotation(false);
            anatomy.SetPreviewMode(false);
            if (selectedCoach != null) selectedCoach.UseSession("");
            liveAttempt = requireCoachSynchronization;
            selectedCoach = coach;
            boundSessionId = liveAttempt && !string.IsNullOrWhiteSpace(explicitCoachSessionId) ? explicitCoachSessionId : "";
            selectedPresentationMode = presentationMode;
            selectedCase = candidate;
            selectedAnatomy = anatomy;
            instruments.Clear(); instruments.UnionWith(allowedInstruments);
            structures.Clear(); structures.UnionWith(required);
            ports.Clear(); ports.UnionWith(allowedPorts);
            SelectedInstrumentId = "";
            runner = new CaseRunner(procedure);
            runner.StepStarted += step => StepStarted?.Invoke(step);
            runner.MistakeMade += (step, mistake) => MistakeMade?.Invoke(step, mistake);
            runner.CaseCompleted += () => CaseCompleted?.Invoke();
            if (requireCoachSynchronization && coach != null)
            {
                if (!string.IsNullOrEmpty(boundSessionId))
                    coach.AdoptSession(boundSessionId, candidate.patientId, candidate.procedureId,
                        candidate.caseId, selectedPresentationMode, procedure.firstStep);
                else
                    coach.AdoptCurrentSession(candidate.patientId, candidate.procedureId, candidate.caseId,
                        selectedPresentationMode, procedure.firstStep);
            }
            runner.Begin();
            reason = "";
            return true;
        }

        // Loading or abandoning a case invalidates the old runner, even if a tracker later reacquires.
        void OnDisable() { StopAttempt(); }

        public void StopAttempt()
        {
            if (selectedCoach != null) selectedCoach.UseSession("");
            runner = null;
            selectedCase = null;
            selectedAnatomy = null;
            selectedCoach = null;
            boundSessionId = "";
            selectedPresentationMode = "";
            instruments.Clear(); structures.Clear(); ports.Clear();
            SelectedInstrumentId = "";
            if (anatomy != null) anatomy.SetRegistrationValid(false);
        }

        public bool SelectInstrument(string id)
        {
            if (id == null || !instruments.Contains(id)) return false;
            SelectedInstrumentId = id;
            return true;
        }

        // Call once per intentional tool activation. Do not call every OnTriggerStay frame:
        // authored apply_count checks represent separate learner actions.
        public bool TouchCollider(Collider hit, out CaseResult result, out string reason)
        {
            result = default;
            if (hit == null || !hit.enabled || !hit.gameObject.activeInHierarchy)
                return Reject("inactive anatomy collider", out reason);
            var part = hit.GetComponentInParent<AnatomyPart>(true);
            if (part == null || anatomy == null || !anatomy.TryGetPart(part.stableId, out var owned) || owned != part)
                return Reject("collider does not belong to selected anatomy", out reason);
            return Submit(CaseEvent.Touch(part.stableId, SelectedInstrumentId), out result, out reason);
        }

        public bool Submit(CaseEvent input, out CaseResult result, out string reason)
        {
            result = default;
            if (!CanScore) return Reject("practice unavailable, preview active, registration invalid, or coach unsynchronized", out reason);
            if (runner.Completed && runner.Body == null) return Reject("case already completed", out reason);
            if (input.type == CaseEventType.Touch || input.type == CaseEventType.Identify)
            {
                if (!structures.Contains(input.id ?? "") || !anatomy.TryGetPart(input.id, out var part) ||
                    !part.IsVisible || !part.HasVisibleGeometry || !HasOwnedCollider(part, true))
                    return Reject("target missing, hidden, or not in this case", out reason);
                if (input.type == CaseEventType.Touch && !instruments.Contains(input.instrumentId ?? ""))
                    return Reject("unknown instrument", out reason);
            }
            else if (input.type == CaseEventType.PlacePort)
            {
                if (!ports.Contains(input.id ?? "")) return Reject("unknown port", out reason);
            }
            else if (input.type == CaseEventType.Surgery)
            {
                var evidence = input.evidence;
                if (runner.Body == null || !BodyState.ValidBodyAction(evidence) || !evidence.registered ||
                    Array.Find(runner.Body.Tissues, tissue => tissue.id == evidence.tissueId && tissue.layer == evidence.layer) == null ||
                    (!instruments.Contains(evidence.instrumentId) && evidence.instrumentId != "assistant" && evidence.instrumentId != "decision"))
                    return Reject("invalid surgical evidence, target, or instrument", out reason);
            }
            else if (input.type != CaseEventType.Confirm) return Reject("unknown input type", out reason);
            if (liveAttempt && string.IsNullOrEmpty(boundSessionId)) boundSessionId = coach.SessionId;
            var beforeStepId = runner.Current?.id ?? "";
            var forwardingCoach = liveAttempt ? coach : null;
            result = runner.Handle(input);
            // Step callbacks may advance Current; send the step which validated this action.
            if (forwardingCoach != null && CoachMatches) forwardingCoach.Forward(input, beforeStepId);
            EventHandled?.Invoke(input, result);
            reason = "";
            return true;
        }

        static bool HasOwnedCollider(AnatomyPart part, bool requireActive = false)
        {
            foreach (var collider in part.GetComponentsInChildren<Collider>(true))
                if (collider.GetComponentInParent<AnatomyPart>(true) == part &&
                    (!requireActive || (collider.enabled && collider.gameObject.activeInHierarchy))) return true;
            return false;
        }
        static bool Reject(string message, out string reason) { reason = message; return false; }
    }
}
