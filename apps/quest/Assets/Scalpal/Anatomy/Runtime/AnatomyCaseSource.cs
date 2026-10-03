using System;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Preop;
using UnityEngine;

namespace Scalpal.Anatomy
{
    // Route patient selection through LoadPatient so every request invalidates the old attempt first.
    // The service currently has no request IDs. Serialize loads and match the returned patient ID.
    [DisallowMultipleComponent]
    public sealed class AnatomyCaseSource : MonoBehaviour
    {
        public ScalpalPreopService service;
        public AnatomyExerciseBinding exercise;
        ScalpalPreopService subscribedService;
        string requestedPatient;
        public bool IsLoading { get; private set; }
        public SurgicalCase PendingCase { get; private set; }
        public string Status { get; private set; } = "Select a patient.";
        public event Action<SurgicalCase> CasePending;
        public event Action<SurgicalCase> CaseStarted;
        public event Action<string> StatusChanged;

        void OnEnable() { Rebind(); }
        void OnDisable() { Unbind(); Stop("Case source disabled."); }
        public void Rebind()
        {
            Unbind();
            Stop("Select a patient.");
            if (!isActiveAndEnabled || service == null) return;
            subscribedService = service;
            subscribedService.CaseLoaded += ReceiveCase;
            subscribedService.RequestFailed += ReceiveFailure;
        }
        void Unbind()
        {
            if (subscribedService == null) return;
            subscribedService.CaseLoaded -= ReceiveCase;
            subscribedService.RequestFailed -= ReceiveFailure;
            subscribedService = null;
        }
        public bool LoadPatient(string patientId)
        {
            if (IsLoading) { SetStatus("A case request is already loading."); return false; }
            if (!isActiveAndEnabled || exercise == null || service == null || subscribedService != service || string.IsNullOrWhiteSpace(patientId))
            {
                Stop("Case source unavailable or patient ID missing.");
                return false;
            }
            Stop("Loading case.");
            requestedPatient = patientId;
            IsLoading = true;
            if (exercise.coach != null)
            {
                if (string.IsNullOrWhiteSpace(service.BaseUrl))
                {
                    Stop("Case service endpoint is missing.");
                    return false;
                }
                exercise.coach.ConfigureEndpoint(service.BaseUrl);
            }
            service.LoadCase(patientId);
            return true;
        }
        void ReceiveCase(SurgicalCase candidate)
        {
            if (!IsLoading) return;
            if (candidate == null || candidate.patientId != requestedPatient)
            {
                Stop("Case response did not match the requested patient.");
                return;
            }
            IsLoading = false;
            requestedPatient = null;
            PendingCase = candidate;
            if (candidate.status == "ready") StartPending(false);
            else if (candidate.status == "needs_review")
            {
                SetStatus("Review the case brief before starting.");
                CasePending?.Invoke(candidate);
            }
            else
            {
                Stop("Case unavailable: " + (candidate.statusReason ?? candidate.status));
            }
        }
        public bool AcknowledgeReviewAndStart()
        {
            if (PendingCase == null || PendingCase.status != "needs_review" || IsLoading)
            {
                SetStatus("No case is awaiting review.");
                return false;
            }
            return StartPending(true);
        }
        bool StartPending(bool acknowledged)
        {
            var candidate = PendingCase;
            if (!isActiveAndEnabled || candidate == null || exercise == null)
            {
                Stop("Case source unavailable.");
                return false;
            }
            exercise.StopAttempt();
            if (!exercise.SelectCase(new ScalpalBundle { cases = new[] { candidate } }, candidate.caseId, acknowledged, out var reason))
            {
                Stop(reason);
                return false;
            }
            PendingCase = null;
            SetStatus("Case selected. Registration is required before practice.");
            CaseStarted?.Invoke(candidate);
            return true;
        }
        void ReceiveFailure(ErrorResponse error)
        {
            // Service failures have no route/request correlation. Conservatively pause even if
            // another request on this shared service failed. Never resume an old attempt silently.
            Stop(error?.error?.message ?? "Case service request failed.");
        }
        void Stop(string status)
        {
            IsLoading = false;
            requestedPatient = null;
            PendingCase = null;
            if (exercise != null) exercise.StopAttempt();
            SetStatus(status);
        }
        void SetStatus(string status) { Status = status; StatusChanged?.Invoke(status); }
    }
}
