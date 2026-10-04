// Runs one surgical case in the scene: load, brief, pre-op safety check, operate, complete.
// Scene inputs (instrument tips, port targets, gaze/voice identification, confirm buttons) call
// PlacePort/Touch/Identify/Confirm. Input is ignored unless the case is operating and registration
// is valid, so a tracking loss can never score. UI listens to the events and binds buttons to the
// ScalpalAction objects in each payload via Dispatch, so every button routes somewhere.
using System;
using System.Collections;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Exercises.Preop;
using UnityEngine;

namespace Scalpal.Experience
{
    public enum CasePhase
    {
        Idle,
        Loading,
        Unavailable,
        Briefing,
        PreopCheck,
        Operating,
        Complete,
    }

    [DisallowMultipleComponent]
    public sealed class CaseDirector : MonoBehaviour
    {
        [SerializeField] ScalpalPreopService service;
        [Tooltip("Practice anatomy under the registered torso root, not the selection preview.")]
        [SerializeField] AnatomyController anatomy;
        [Tooltip("Optional. Forwards steps to Jarvis and adopts the laptop's coach session.")]
        [SerializeField] CoachRelay relay;
        [Tooltip("Optional. Spawns touchable port sites for the loaded case.")]
        [SerializeField] PortLayout ports;
        [SerializeField] int maxAutoRetrySeconds = 10;

        CaseRunner runner;

        // The enabled director, so generated instrument prefabs can find it without scene wiring.
        public static CaseDirector Active { get; private set; }

        public CasePhase Phase { get; private set; } = CasePhase.Idle;
        public SurgicalCase Case { get; private set; }
        public PreopCheckResult PreopResult { get; private set; }
        public ProcedureStep CurrentStep => runner?.Current;
        public int MistakeCount => runner?.Mistakes.Count ?? 0;

        public event Action<CasePhase> PhaseChanged;
        public event Action<SurgicalCase> CaseReady;
        public event Action<PreopCheckResult> PreopChecked;
        // The step plus this patient's notes for it (anticoagulation on the bleeding step, and so on).
        public event Action<ProcedureStep, string[]> StepStarted;
        public event Action<ProcedureStep> StepCompleted;
        public event Action<ProcedureStep, StepMistake> MistakeMade;
        public event Action CaseCompleted;
        // Fired when input arrives while registration is invalid; show "hold still, re-aligning".
        public event Action InputBlockedByTracking;
        public event Action<ErrorResponse> Failed;

        void OnEnable()
        {
            Active = this;
            if (service != null)
            {
                service.CaseLoaded += OnCaseLoaded;
                service.PreopChecked += OnPreopChecked;
                service.RequestFailed += OnFailed;
            }
            if (anatomy != null) anatomy.RegistrationChanged += OnRegistrationChanged;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            if (service != null)
            {
                service.CaseLoaded -= OnCaseLoaded;
                service.PreopChecked -= OnPreopChecked;
                service.RequestFailed -= OnFailed;
            }
            if (anatomy != null) anatomy.RegistrationChanged -= OnRegistrationChanged;
            DetachRunner();
        }

        public void OpenCase(string patientId)
        {
            if (service == null || string.IsNullOrEmpty(patientId)) return;
            SetPhase(CasePhase.Loading);
            service.LoadCase(patientId);
        }

        // For buttons bound to payload actions. The pre-op check goes through SubmitPreopCheck.
        public void Dispatch(ScalpalAction action)
        {
            if (service == null || action == null) return;
            if (action.route != null && action.route.EndsWith("/case")) SetPhase(CasePhase.Loading);
            service.Dispatch(action);
        }

        public void SubmitPreopCheck(string[] selectedFlagTypes)
        {
            if (Case == null || Phase != CasePhase.Briefing && Phase != CasePhase.PreopCheck) return;
            SetPhase(CasePhase.PreopCheck);
            service.SubmitPreopCheck(Case.patientId, selectedFlagTypes ?? new string[0]);
        }

        // The pre-op check teaches but does not gate: the learner can scrub in after seeing the result.
        public void BeginProcedure()
        {
            if (Case == null || string.IsNullOrEmpty(Case.procedureId) || Phase == CasePhase.Operating) return;
            DetachRunner();
            runner = new CaseRunner(Case.procedure);
            runner.StepStarted += OnStepStarted;
            runner.StepCompleted += OnStepCompleted;
            runner.MistakeMade += OnMistake;
            runner.CaseCompleted += OnCaseCompleted;
            SetPhase(CasePhase.Operating);
            runner.Begin();
        }

        public bool PlacePort(string portId) => Feed(CaseEvent.PlacePort(portId));
        public bool Touch(string structureId, string instrumentId) => Feed(CaseEvent.Touch(structureId, instrumentId));
        public bool Identify(string structureId) => Feed(CaseEvent.Identify(structureId));
        public bool Confirm() => Feed(CaseEvent.Confirm());

        // What the tip or gaze is over, for Jarvis context. Not scored.
        public void Focus(string structureId)
        {
            if (relay != null && Phase == CasePhase.Operating) relay.Focus(structureId);
        }

        // Demo autoplay: performs the current step perfectly. Same scoring path as real input.
        public void AutoplayStep()
        {
            var step = runner?.Current;
            if (step == null) return;
            foreach (var e in CaseRunner.PerfectEvents(step)) Feed(e);
        }

        bool Feed(CaseEvent e)
        {
            if (Phase != CasePhase.Operating || runner == null) return false;
            if (anatomy != null && !anatomy.CanDisplay)
            {
                InputBlockedByTracking?.Invoke();
                return false;
            }
            var result = runner.Handle(e);
            if (relay != null) relay.Forward(e);
            return result.advanced || result.mistake != null;
        }

        void OnCaseLoaded(SurgicalCase kase)
        {
            Case = kase;
            PreopResult = null;
            DetachRunner();
            if (string.IsNullOrEmpty(kase.procedureId))
            {
                SetPhase(CasePhase.Unavailable);
                CaseReady?.Invoke(kase);
                if (kase.status == "retry") StartCoroutine(RetryAfter(kase));
                return;
            }
            if (ports != null) ports.Build(kase);
            if (relay != null) relay.AdoptCurrentSession(kase.patientId);
            if (anatomy != null)
            {
                anatomy.RestoreVisibility();
                var first = kase.brief.highlightStructures.FirstOrDefault(id => anatomy.TryGetPart(id, out _));
                if (first != null) anatomy.Highlight(first);
                else anatomy.ClearHighlight();
            }
            SetPhase(CasePhase.Briefing);
            CaseReady?.Invoke(kase);
        }

        IEnumerator RetryAfter(SurgicalCase kase)
        {
            var wait = Mathf.Max(1f, Mathf.Min(kase.retryAfterSeconds, maxAutoRetrySeconds));
            yield return new WaitForSeconds(wait);
            if (Case == kase && Phase == CasePhase.Unavailable) OpenCase(kase.patientId);
        }

        void OnPreopChecked(PreopCheckResult result)
        {
            if (Case == null || result.patientId != Case.patientId) return;
            PreopResult = result;
            PreopChecked?.Invoke(result);
        }

        void OnStepStarted(ProcedureStep step)
        {
            var notes = Case.considerations.Where(c => c.stepId == step.id).Select(c => c.note).ToArray();
            if (anatomy != null)
            {
                var target = step.check.type == "place_ports" ? null : step.targets.FirstOrDefault(id => anatomy.TryGetPart(id, out _));
                if (target != null) anatomy.Highlight(target);
                else anatomy.ClearHighlight();
            }
            if (ports != null) ports.SetActivePorts(step.check.type == "place_ports" ? step.portIds : new string[0]);
            StepStarted?.Invoke(step, notes);
        }

        void OnStepCompleted(ProcedureStep step) => StepCompleted?.Invoke(step);

        void OnMistake(ProcedureStep step, StepMistake mistake) => MistakeMade?.Invoke(step, mistake);

        void OnCaseCompleted()
        {
            if (anatomy != null) anatomy.ClearHighlight();
            if (ports != null) ports.SetActivePorts(new string[0]);
            SetPhase(CasePhase.Complete);
            CaseCompleted?.Invoke();
        }

        void OnFailed(ErrorResponse error)
        {
            if (Phase == CasePhase.Loading) SetPhase(CasePhase.Unavailable);
            Failed?.Invoke(error);
        }

        void OnRegistrationChanged(bool valid)
        {
            if (!valid && anatomy != null) anatomy.ClearHighlight();
            else if (valid && runner?.Current != null) OnStepStarted(runner.Current);
        }

        void DetachRunner()
        {
            if (runner == null) return;
            runner.StepStarted -= OnStepStarted;
            runner.StepCompleted -= OnStepCompleted;
            runner.MistakeMade -= OnMistake;
            runner.CaseCompleted -= OnCaseCompleted;
            runner = null;
        }

        void SetPhase(CasePhase phase)
        {
            if (Phase == phase) return;
            Phase = phase;
            PhaseChanged?.Invoke(phase);
        }
    }
}
