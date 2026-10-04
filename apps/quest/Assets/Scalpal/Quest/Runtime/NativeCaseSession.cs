using System;
using System.Collections;
using System.IO;
using System.Text;
using Scalpal.Anatomy;
using Scalpal.Instruments;
using Scalpal.Handoff;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Realtime;
using Scalpal.Voice;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // One exercise across AR/VR. The case engine owns progression; adapters report real inputs.
    [DefaultExecutionOrder(75)]
    public sealed partial class NativeCaseSession : MonoBehaviour
    {
        public NativeWorkbench workbench;
        public AnatomyExerciseBinding exercise;
        public AnatomyController anatomy, preview;
        public CoachRelay coach;
        public QuestSessionBridge realtime;
        public QuestJarvisVoice voice;
        public Transform patientFrame;
        public TextMesh status;
        public NativePresentation presentation;
        public NativeBodyRegistration bodyRegistration;
        public string PresentationMode => presentation ? presentation.CoachMode : "virtual";
        public bool RegistrationReady => workbench && workbench.IsReady && (!presentation || !presentation.passthrough
            || (bodyRegistration && bodyRegistration.Accepted && bodyRegistration.CandidateValid));
        public string coachBaseUrl = "http://localhost:8787";
        public const string PatientId = "patient-demo-multi-source";
        public const string ProcedureId = "lap_appendectomy";
        public bool HasHandoff => HandoffRun.Current != null;
        public bool HandoffVerified { get; private set; }
        public bool CaptionFallbackAllowed { get; private set; }
        public bool CoachPrepared => !string.IsNullOrEmpty(coachSessionId);
        public bool Busy => busy;
        public bool AttemptNeedsRetry => attemptFailed;
        public SurgicalCase ReviewedCase => candidate;
        public const string ContentVersion = "0.1.0";
        string standalonePatientId = PatientId, standaloneProcedureId = ProcedureId;
        public string SelectedPatientId { get => HandoffRun.Current?.patientId ?? standalonePatientId; private set => standalonePatientId = value; }
        public string SelectedProcedureId { get => HandoffRun.Current?.procedureId ?? standaloneProcedureId; private set => standaloneProcedureId = value; }
        public EncounterSurgeryHandoff OfficeHandoff { get; private set; }
        public string Phase { get; private set; } = "Startup";
        public bool Practicing => Phase == "Practicing" && !practicePaused;
        public string Message { get; private set; } = "Loading synthetic case";
        NativeProcedureInput input;
        NativeTissueSimulation tissueSimulation;
        NativeVolumeSimulation volumeSimulation;
        NativeVesselSimulation vesselSimulation;
        SurgicalCase candidate;
        string coachSessionId = "", voicePrompt = "", voiceGreeting = "", voiceContext = "";
        string selected = "", highlighted = "";
        bool busy, reviewed, practicePaused, rotating = true, attemptRequested, voiceRequested;
        bool previousB, previousX, previousY;
        bool sharedAttemptReady, attemptFailed;
        string boundSharedSession = "", boundSharedAttempt = "";
        Vector3 previewScale;
        bool SharedMatches => sharedAttemptReady && realtime.Paired && realtime.SessionId == boundSharedSession && realtime.AttemptId == boundSharedAttempt
            && (OfficeHandoff == null || OfficeSharedMatches(out _))
            && (!HasHandoff || HandoffRun.Current.sourceOffice == null || HandoffSourceMatches(out _));
        int generation, completedSteps, mistakes;
        float nextUi, nextContext;
        string lastVoiceContextKey = "";
        bool handoffVoiceAllowed;

        [Serializable] public class DevelopmentConfig
        {
            public string uri, database, joinCode, preferredSessionId, coachBaseUrl;
        }
        [Serializable] public class CreateRequest { public string patientId, mode, encounterId; }
        [Serializable] class Created
        {
            public string sessionId, context, systemPrompt, firstMessage;
            public CoachSnapshotState snapshot;
        }
        [Serializable] class ContextReply { public string context, contextKey; public CoachSnapshotState snapshot; }

        void Awake()
        {
            if (HasHandoff || !EncounterOfficeRoute.TakeSurgery(out var handoff)) return;
            OfficeHandoff = handoff;
            SelectedPatientId = handoff.patientId;
            SelectedProcedureId = handoff.procedureId;
            coachBaseUrl = handoff.serviceUrl;
            if (presentation) { presentation.passthrough = true; presentation.Apply(); }
        }

        void Start()
        {
            if (!workbench || !exercise || !anatomy || !preview || !coach || !realtime || !voice || !status)
            { Message = "Session bindings missing"; enabled = false; return; }
            workbench.externalSessionControls = true;
            workbench.RetryRequested += WorkbenchRetry;
            ReadDevelopmentConfig();
            if (HasHandoff) coachBaseUrl = HandoffRun.Current.serviceUrl;
            coach.ConfigureEndpoint(coachBaseUrl);
            voice.ConfigureEndpoint(coachBaseUrl);
            input = GetComponent<NativeProcedureInput>();
            if (!input) input = gameObject.AddComponent<NativeProcedureInput>();
            input.portMarkers = patientFrame.GetComponentsInChildren<NativePortMarker>(true);
            patientFrame.gameObject.SetActive(false);
            input.Initialize(exercise, workbench, () => Practicing && SharedMatches && RegistrationReady);
            tissueSimulation = GetComponent<NativeTissueSimulation>();
            if (!tissueSimulation) tissueSimulation = gameObject.AddComponent<NativeTissueSimulation>();
            tissueSimulation.Initialize(anatomy, workbench, () => isActiveAndEnabled && input.InteractionReady);
            volumeSimulation = GetComponent<NativeVolumeSimulation>();
            if (!volumeSimulation) volumeSimulation = gameObject.AddComponent<NativeVolumeSimulation>();
            volumeSimulation.Initialize(anatomy.transform.parent, workbench, () => isActiveAndEnabled && input.InteractionReady && anatomy.RegistrationValid);
            vesselSimulation = GetComponent<NativeVesselSimulation>();
            if (!vesselSimulation) vesselSimulation = gameObject.AddComponent<NativeVesselSimulation>();
            vesselSimulation.Initialize(anatomy, workbench, volumeSimulation, () => isActiveAndEnabled && input.InteractionReady);
            exercise.anatomy = anatomy; exercise.coach = coach;
            exercise.presentationMode = PresentationMode; exercise.requireCoachSynchronization = true;
            exercise.EventHandled += EventHandled;
            coach.CommandRequested += CoachCommand;
            realtime.AttemptStarted += AttemptStarted;
            realtime.AttemptFailed += AttemptFailed;
            realtime.CommandRequested += SharedCommand;
            voice.ClientToolRequested += VoiceTool;
            voice.Transcript += VoiceTranscript;
            preview.SetPreviewMode(true); preview.SetPreviewRotation(true);
            previewScale = preview.transform.parent.localScale;
            anatomy.SetRegistrationValid(false);
            realtime.Reconnect();
            StartCoroutine(LoadCase(++generation));
        }

        void ReadDevelopmentConfig()
        {
            if (!Debug.isDebugBuild) return;
            string path = Path.Combine(Application.persistentDataPath, "session-config.json");
#if UNITY_ANDROID && !UNITY_EDITOR
            // adb run-as reaches the app's private files, not Android's remounted external storage.
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
                path = Path.Combine(files.Call<string>("getAbsolutePath"), "session-config.json");
#endif
#if UNITY_EDITOR
            // An isolated Editor gate supplies its own ephemeral config, never the operator's pairing file.
            string fixturePath = Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG");
            if (!string.IsNullOrWhiteSpace(fixturePath)) path = fixturePath;
#endif
            if (!File.Exists(path)) return;
            try
            {
                var config = JsonUtility.FromJson<DevelopmentConfig>(File.ReadAllText(path));
                if (OfficeHandoff == null && !string.IsNullOrWhiteSpace(config.coachBaseUrl)) coachBaseUrl = config.coachBaseUrl;
                if (!string.IsNullOrWhiteSpace(config.uri)) realtime.uri = config.uri;
                if (!string.IsNullOrWhiteSpace(config.database)) realtime.database = config.database;
                realtime.joinCode = config.joinCode ?? "";
                realtime.preferredSessionId = config.preferredSessionId ?? "";
            }
            catch (Exception) { Message = "Development session configuration is invalid"; }
        }

        IEnumerator LoadCase(int epoch)
        {
            busy = true; Phase = "Startup";
            string json = null;
            yield return Request("GET", "/patients/" + Uri.EscapeDataString(SelectedPatientId) + "/case", null, value => json = value);
            if (epoch != generation) yield break;
            busy = false;
            try { candidate = json == null ? null : JsonUtility.FromJson<SurgicalCase>(json); }
            catch (ArgumentException) { candidate = null; }
            if (candidate == null || candidate.patientId != SelectedPatientId || string.IsNullOrEmpty(candidate.procedureId) ||
                ((HasHandoff || OfficeHandoff != null) && candidate.procedureId != SelectedProcedureId) ||
                candidate.procedure == null || candidate.brief == null || !candidate.brief.synthetic ||
                (candidate.status != "ready" && candidate.status != "needs_review"))
            { candidate = null; Message = "Case service unavailable or case/assets mismatch. Left menu: retry connection"; yield break; }
            if (HasHandoff)
            {
                busy = true;
                string stateJson = null, scoreJson = null;
                var path = "/encounters/" + Uri.EscapeDataString(HandoffRun.Current.encounterId);
                yield return Request("GET", path, null, value => stateJson = value);
                yield return Request("GET", path + "/score", null, value => scoreJson = value);
                if (epoch != generation) yield break;
                EncounterReply stateReply = null, scoreReply = null;
                try { if (stateJson != null) stateReply = JsonUtility.FromJson<EncounterReply>(stateJson); if (scoreJson != null) scoreReply = JsonUtility.FromJson<EncounterReply>(scoreJson); } catch (ArgumentException) { }
                busy = false;
                if (!EncounterSurgeryBinding.Validate(HandoffRun.Current.sourceOffice, candidate, stateReply, scoreReply, out var refusal)
                    || !HandoffRun.Verify(HandoffRun.Current, stateReply?.state, scoreReply?.scorecard, candidate, out refusal))
                { candidate = null; Message = refusal; yield break; }
                HandoffVerified = true; HandoffRun.Current.scorecard = scoreReply.scorecard; HandoffRun.Current.verifiedCase = candidate;
                reviewed = true; Phase = "Confirmed"; preview.gameObject.SetActive(false);
                Message = "Complete the theatre Time-Out before practice"; yield break;
            }
            if (OfficeHandoff == null) SelectedProcedureId = candidate.procedureId;
            Phase = "Selecting"; Message = candidate.procedure.title + " preview ready. X: anatomy layers; B: review synthetic case";
        }

        void Update()
        {
            if (!workbench) return;
            if (sharedAttemptReady && realtime.Paired && !SharedMatches)
            {
                generation++; ResetHandoffRecovery();
                sharedAttemptReady = false; attemptFailed = true;
                exercise.StopAttempt(); voice.Disconnect(); coachSessionId = ""; Phase = "Selecting";
                preview.gameObject.SetActive(true);
                if (HasHandoff) { HandoffRun.Current.practiceStarted = false; HandoffRun.Current.ResetTimeOut(); }
                Message = "Shared session/attempt changed; Left menu: start a new matching attempt";
            }
            UpdateHandoffCoachRecovery();
            bool fitValid = RegistrationReady && Practicing && SharedMatches && !recoveringLocalCoach && (!localCoachReady || coach.IsSynchronized);
            patientFrame.gameObject.SetActive(fitValid);
            anatomy.SetRegistrationValid(fitValid);
            coach.Tracking(RegistrationReady && Practicing && SharedMatches);
            if ((!HasHandoff || HandoffVerified) && realtime.Paired && !sharedAttemptReady && !attemptRequested && !attemptFailed) RequestAttempt();
            if (workbench.IsReady)
            {
                bool b = XRInput.Button(XRNode.RightHand, XRInputButton.Secondary);
                bool x = XRInput.Button(XRNode.LeftHand, XRInputButton.Primary);
                bool y = XRInput.Button(XRNode.LeftHand, XRInputButton.Secondary);
                if (b && !previousB) ConfirmAction();
                if (x && !previousX && Phase == "Selecting")
                {
                    var layers = preview.GetComponent<Scalpal.Anatomy.Tissue.AnatomyLayerView>();
                    if (layers) layers.Cycle();
                }
                if (x && !previousX && Practicing)
                {
                    if (!input.IdentifyFocused(out var reason)) Message = reason;
                }
                if (y && !previousY) ToggleVoice();
                previousB = b; previousX = x; previousY = y;
            }
            else previousB = previousX = previousY = false;
            string focus = input.FocusedPartId ?? "";
            if (focus != selected) { selected = focus; coach.Focus(focus); }
            if (Time.unscaledTime >= nextUi)
            {
                Publish(); UpdateUi(); nextUi = Time.unscaledTime + 0.25f;
            }
            if (voice.Connected && coach.Connected && Time.unscaledTime >= nextContext)
            { nextContext = Time.unscaledTime + 1; StartCoroutine(RefreshVoiceContext(generation, coach.SessionId)); }
        }

        void RequestAttempt()
        {
            if (attemptRequested || !realtime.Paired || candidate == null) return;
            if (HasHandoff && HandoffRun.Current.sourceOffice != null)
            {
                if (!HandoffSourceMatches(out var sourceReason)) { Message = sourceReason; return; }
                var ticket = HandoffRun.Current;
                if (!string.IsNullOrEmpty(ticket.attemptId))
                {
                    if (realtime.SessionId == ticket.sharedSessionId && realtime.AttemptId == ticket.attemptId) AttemptStarted(ticket.attemptId);
                    else { attemptFailed = true; Message = "The office attempt changed. Start a matching OR attempt explicitly."; }
                    return;
                }
            }
            if (OfficeHandoff != null)
            {
                if (OfficeSharedMatches(out var reason)) AttemptStarted(OfficeHandoff.attemptId);
                else Message = reason;
                return;
            }
            attemptRequested = realtime.BeginAttempt(SelectedProcedureId, ContentVersion);
        }
        bool HandoffSourceMatches(out string reason)
        {
            var ticket = HandoffRun.Current;
            reason = "Waiting for the committed scored office encounter in the paired session.";
            if (ticket?.sourceOffice == null || !realtime || !realtime.Paired || realtime.SessionId != ticket.sourceOffice.sharedSessionId) return false;
            if (!realtime.TryGetEncounterBinding(ticket.encounterId, out var session, out var attempt, out var patient, out var phase)
                || !HandoffRun.SourceBindingMatches(ticket, session, attempt, patient, phase)) return false;
            reason = ""; return true;
        }
        bool OfficeSharedMatches(out string reason)
        {
            var handoff = OfficeHandoff;
            if (handoff == null || string.IsNullOrEmpty(handoff.sharedSessionId) || string.IsNullOrEmpty(handoff.attemptId))
            { reason = "The office handoff has no shared attempt. Return to the office for a new case."; return false; }
            if (!realtime || !realtime.Paired)
            { reason = "Waiting for the office's paired headset session."; return false; }
            if (realtime.SessionId != handoff.sharedSessionId || realtime.AttemptId != handoff.attemptId)
            { reason = "The shared session or attempt differs from the office encounter. Return to the office for a new case."; return false; }
            if (!realtime.TryGetEncounterBinding(handoff.encounterId, out var sessionId, out var attemptId, out var patientId, out var phase))
            { reason = "Waiting for the committed office encounter in the shared session."; return false; }
            if (sessionId != handoff.sharedSessionId || attemptId != handoff.attemptId || patientId != SelectedPatientId || phase != "scored")
            { reason = "The shared encounter does not match this scored patient and attempt."; return false; }
            reason = ""; return true;
        }
        void AttemptStarted(string id)
        {
            if (OfficeHandoff != null && (id != OfficeHandoff.attemptId || !OfficeSharedMatches(out _))) return;
            if (HasHandoff && HandoffRun.Current.sourceOffice != null && (!HandoffSourceMatches(out _)
                || !string.IsNullOrEmpty(HandoffRun.Current.attemptId) && id != HandoffRun.Current.attemptId)) return;
            attemptRequested = false; sharedAttemptReady = true; attemptFailed = false;
            boundSharedSession = realtime.SessionId; boundSharedAttempt = id;
            if (HasHandoff) { HandoffRun.Current.sharedSessionId = boundSharedSession; HandoffRun.Current.attemptId = id; }
            Publish();
        }
        void AttemptFailed(string reason) { attemptRequested = false; sharedAttemptReady = false; attemptFailed = true; Message = reason; }

        public bool ConfirmAction()
        {
            if (busy || candidate == null || HasHandoff && !HandoffRun.Current.timeOutConfirmed) return false;
            if (presentation && presentation.passthrough && bodyRegistration && !bodyRegistration.Accepted
                && (Phase == "Confirmed" || Phase == "Practicing"))
            {
                Message = bodyRegistration.Status;
                return false; // Body acquisition is automatic; B only confirms the case.
            }
            if (Phase == "Selecting")
            {
                reviewed = true; Phase = "Confirmed"; rotating = false; preview.SetPreviewRotation(false);
                Message = "Review the synthetic patient risks. B: acknowledge and begin"; Publish(); return true;
            }
            if (Phase == "Confirmed")
            {
                if (!RegistrationReady) { Message = "Waiting for a valid accepted body fit"; return false; }
                if (!SharedMatches || attemptRequested)
                { Message = "Waiting for a confirmed shared headset session"; return false; }
                StartCoroutine(StartPractice(generation)); return true;
            }
            if (Practicing)
            {
                if (!input.Confirm(out var reason)) { Message = reason; return false; }
                return true;
            }
            return false;
        }

        IEnumerator StartPractice(int epoch)
        {
            busy = true; CaptionFallbackAllowed = false; Message = "Creating a fresh matched coach session";
            string json = null;
            if (OfficeHandoff != null)
            {
                string caseJson = null, encounterJson = null, scoreJson = null;
                yield return Request("GET", "/patients/" + Uri.EscapeDataString(SelectedPatientId ?? "") + "/case", null, value => caseJson = value);
                if (epoch != generation) yield break;
                string encounterPath = "/encounters/" + Uri.EscapeDataString(OfficeHandoff.encounterId ?? "");
                yield return Request("GET", encounterPath, null, value => encounterJson = value);
                if (epoch != generation) yield break;
                yield return Request("GET", encounterPath + "/score", null, value => scoreJson = value);
                if (epoch != generation) yield break;
                SurgicalCase liveCase = null; EncounterReply liveEncounter = null, liveScore = null;
                try
                {
                    if (caseJson != null) liveCase = JsonUtility.FromJson<SurgicalCase>(caseJson);
                    if (encounterJson != null) liveEncounter = JsonUtility.FromJson<EncounterReply>(encounterJson);
                    if (scoreJson != null) liveScore = JsonUtility.FromJson<EncounterReply>(scoreJson);
                }
                catch (ArgumentException) { }
                if (!EncounterSurgeryBinding.Validate(OfficeHandoff, liveCase, liveEncounter, liveScore, out var bindingReason))
                { busy = false; Message = bindingReason + " Return to the office or confirm to retry verification."; yield break; }
                if (liveCase.caseId != candidate.caseId)
                { busy = false; Message = "The surgical case changed after review; return to the office."; yield break; }
                candidate = liveCase;
            }
            if (!SharedMatches || !RegistrationReady)
            { busy = false; Message = "Shared attempt or body fit changed while verifying; confirm and retry"; yield break; }
            long responseCode = 0;
            yield return Request("POST", "/coach/sessions", JsonUtility.ToJson(CoachRequest()), value => json = value, code => responseCode = code);
            if (epoch != generation) yield break;
            if (!SharedMatches || !RegistrationReady) { busy = false; Message = "Shared attempt or body fit changed while loading; confirm and retry"; yield break; }
            Created created = null;
            try { if (json != null) created = JsonUtility.FromJson<Created>(json); } catch (ArgumentException) { }
            if (HasHandoff && json == null && (responseCode == 0 || responseCode >= 500))
            { busy = false; CaptionFallbackAllowed = true; Message = "Coach offline. Captions and authored local scoring are available."; yield break; }
            if (created?.snapshot == null || string.IsNullOrEmpty(created.sessionId) || created.snapshot.caseId != candidate.caseId ||
                created.snapshot.patientId != SelectedPatientId || created.snapshot.procedureId != SelectedProcedureId || created.snapshot.mode != PresentationMode)
            { busy = false; Message = "Coach case differs from the reviewed case; restart selection"; yield break; }
            coachSessionId = created.sessionId;
            voicePrompt = created.systemPrompt; voiceGreeting = HasHandoff ? "Scrubbed in with you. Time-out: confirm patient, procedure and site." : created.firstMessage; voiceContext = created.context;
            if (HasHandoff)
            {
                busy = false; Message = "Jarvis ready. Confirm the Time-Out rows.";
                ConnectVoice(); yield break;
            }
            BeginReviewedPractice();
        }

        public CreateRequest CoachRequest() => new CreateRequest { patientId = SelectedPatientId, mode = PresentationMode, encounterId = HandoffRun.Current?.encounterId ?? OfficeHandoff?.encounterId };
        public bool PrepareTimeOut()
        {
            if (!HasHandoff || !HandoffVerified || busy || !SharedMatches || !RegistrationReady || CoachPrepared) return false;
            StartCoroutine(StartPractice(generation)); return true;
        }
        public bool FinishTimeOut(bool captionsOnly = false)
        {
            if (!HasHandoff || !HandoffVerified || busy || !SharedMatches || !RegistrationReady || !HandoffRun.Current.AllConfirmed || (!CoachPrepared && !(captionsOnly && CaptionFallbackAllowed))) return false;
            HandoffRun.Current.timeOutConfirmed = true;
            exercise.requireCoachSynchronization = CoachPrepared;
            return BeginReviewedPractice();
        }
        bool BeginReviewedPractice()
        {
            exercise.explicitCoachSessionId = coachSessionId;
            if (!exercise.SelectCase(new ScalpalBundle { cases = new[] { candidate } }, candidate.caseId, reviewed, out var reason))
            { busy = false; Message = reason; return false; }
            preview.gameObject.SetActive(false);
            localCaptionAttempt = HasHandoff && !exercise.requireCoachSynchronization;
            Phase = "Practicing"; practicePaused = false;
            if (HasHandoff) HandoffRun.Current.practiceStarted = true;
            anatomy.SetRegistrationValid(RegistrationReady); coach.Tracking(RegistrationReady);
            busy = false; Message = "Practice starts after coach synchronization";
            realtime.AppendEvent("practice_started", exercise.Current?.id, null, "Generic teaching anatomy; " + PresentationMode, Time.realtimeSinceStartupAsDouble * 1000);
            if (voiceRequested && !voice.Connected) ConnectVoice();
            Publish(); return true;
        }

        void WorkbenchRetry() { if (!HasHandoff) Retry(); }

        public void Retry()
        {
            if (busy) return;
            if (OfficeHandoff != null && candidate != null)
            {
                ReturnToOffice();
                return;
            }
            ResetHandoffRecovery();
            if (HasHandoff)
            {
                if (HandoffRun.Current.practiceStarted || attemptFailed) HandoffRun.Current.attemptId = "";
                HandoffRun.Current.ResetTimeOut(); HandoffRun.Current.practiceStarted = false;
            }
            if (tissueSimulation) tissueSimulation.ResetTissues();
            if (volumeSimulation) volumeSimulation.ResetTissues();
            if (vesselSimulation) vesselSimulation.ResetTissues();
            generation++; voice.Disconnect(); coachSessionId = "";
            sharedAttemptReady = false; attemptFailed = false; attemptRequested = false;
            exercise.StopAttempt(); workbench.ResetWorkbench();
            if (bodyRegistration) bodyRegistration.ResetFit();
            patientFrame.gameObject.SetActive(false);
            completedSteps = mistakes = 0; practicePaused = false; reviewed = HasHandoff; highlighted = selected = "";
            preview.gameObject.SetActive(true); preview.SetPreviewMode(true); preview.RestoreVisibility();
            preview.SetPreviewRotation(true); rotating = true;
            if (HasHandoff) preview.gameObject.SetActive(false);
            preview.transform.parent.localScale = previewScale;
            anatomy.ClearHighlight(); preview.ClearHighlight();
            var previewLayers = preview.GetComponent<Scalpal.Anatomy.Tissue.AnatomyLayerView>();
            if (previewLayers) previewLayers.Show(Scalpal.Anatomy.Tissue.AnatomyLayerView.Layer.Organs);
            Phase = candidate == null ? "Startup" : HasHandoff ? "Confirmed" : "Selecting";
            Message = "New attempt. B: review the synthetic case";
            RequestAttempt();
            if (candidate == null) StartCoroutine(LoadCase(generation));
            Publish();
        }

        void EventHandled(CaseEvent action, CaseResult result)
        {
            RecordLocalCoachEvent(action, result.stepId);
            if (result.mistake != null)
            {
                mistakes++; Message = result.mistake.feedback;
                voice.SendUserMessage("Simulator feedback: " + Message);
            }
            else if (result.advanced) { completedSteps++; Message = "Step completed"; }
            else Message = "Action recorded; complete the remaining targets";
            // This schema allows bounded native event labels, not raw event enum names.
            realtime.AppendEvent(result.mistake != null ? "mistake" : result.advanced ? "step_completed" : "tool_action",
                result.stepId, string.IsNullOrEmpty(action.id) ? null : action.id, Message, Time.realtimeSinceStartupAsDouble * 1000);
            if (result.completed)
            {
                Phase = "Recap"; voice.Disconnect(); anatomy.SetRegistrationValid(false); coach.Tracking(false);
                Message = $"Complete: {completedSteps}/{candidate.procedure.steps.Length} steps, {mistakes} authored warnings. Left menu: new attempt";
                realtime.ReportAttemptResult((uint)completedSteps, (uint)candidate.procedure.steps.Length,
                    (uint)mistakes, 0, candidate.procedure.title + " rehearsal completed; " + PresentationMode);
            }
            Publish();
        }

        void CoachCommand(CoachCommand command)
        {
            bool applied = false;
            if (command.action == "clear_highlight") { anatomy.ClearHighlight(); highlighted = ""; applied = true; }
            else if (command.action == "highlight" && anatomy.CanDisplay)
            { applied = anatomy.Highlight(command.targetId); if (applied) highlighted = command.targetId; }
            coach.Ack(command.commandId, applied, applied ? "" : "Selected practice anatomy unavailable");
            Publish();
        }

        void SharedCommand(Command command)
        {
            if (!SharedMatches) { realtime.ResolveCommand(command, "rejected", "The native case is not bound to this shared attempt"); return; }
            bool applied = false; string reason = "Action unavailable in this native phase";
            var activeAnatomy = Phase == "Selecting" || Phase == "Confirmed" ? preview : anatomy;
            switch (command.Action)
            {
                case "previewExercise": applied = Phase == "Selecting" && command.TargetId == SelectedProcedureId; break;
                case "confirmExercise": applied = command.TargetId == SelectedProcedureId && Phase == "Selecting" && ConfirmAction(); break;
                case "rotatePreview":
                    if (Phase == "Selecting" && command.ArgBool.HasValue)
                    { rotating = command.ArgBool.Value; preview.SetPreviewRotation(rotating); applied = true; }
                    break;
                case "zoomPreview":
                    if (Phase == "Selecting" && command.ArgNumber.HasValue && command.ArgNumber.Value >= 0.5 && command.ArgNumber.Value <= 2)
                    { preview.transform.parent.localScale = previewScale * (float)command.ArgNumber.Value; applied = true; }
                    break;
                case "highlightStructure":
                    applied = activeAnatomy.CanDisplay && activeAnatomy.Highlight(command.TargetId);
                    if (applied) highlighted = command.TargetId; break;
                case "isolateStructure": applied = activeAnatomy.CanDisplay && activeAnatomy.Isolate(command.TargetId); break;
                case "restoreContext":
                    if (activeAnatomy.CanDisplay) { activeAnatomy.RestoreVisibility(); applied = true; } break;
                case "pausePractice":
                    if (Phase == "Practicing") { practicePaused = true; anatomy.SetRegistrationValid(false); coach.Tracking(false); applied = true; } break;
                case "resumePractice":
                    if (Phase == "Practicing" && RegistrationReady) { practicePaused = false; applied = true; } break;
                case "requestHint":
                    realtime.ResolveCommand(command, "unavailable", "Coach hints are available through native Jarvis; async hint confirmation is not implemented"); return;
            }
            Publish(); realtime.ResolveCommand(command, applied ? "applied" : "rejected", applied ? null : reason);
        }

        void ToggleVoice()
        {
            if (voice.Connected || voice.Status == "connecting") { voiceRequested = false; voice.Disconnect(); }
            else { voiceRequested = true; if (!string.IsNullOrEmpty(coachSessionId)) ConnectVoice(); else Message = "Voice enabled for the next confirmed practice session"; }
        }
        public void SetHandoffVoiceAllowed(bool value)
        {
            handoffVoiceAllowed = value;
            if (!value && voice) voice.Disconnect();
        }
        public void ReconnectTimeOutVoice()
        {
            if (handoffVoiceAllowed && CoachPrepared && !voice.Connected && voice.Status != "connecting") ConnectVoice();
        }
        void ConnectVoice() { if (HasHandoff && !handoffVoiceAllowed) return; lastVoiceContextKey = ""; voice.ConfigureConversation(voicePrompt, voiceGreeting, voiceContext); voice.Connect(coachSessionId); }
        void VoiceTool(QuestJarvisVoice.ToolRequest request)
        {
            voice.ResolveClientTool(request, "This action is unavailable in the native exercise", true);
        }
        void VoiceTranscript(string source, string text)
        {
            // Transcripts stay in memory, never in logs or shared exercise event storage.
            if (source == "agent" && !string.IsNullOrWhiteSpace(text)) Message = text.Length > 180 ? text.Substring(0, 180) : text;
        }
        IEnumerator RefreshVoiceContext(int epoch, string sid)
        {
            string json = null;
            yield return Request("GET", "/coach/sessions/" + Uri.EscapeDataString(sid), null, value => json = value);
            if (epoch != generation || coach.SessionId != sid) yield break;
            ContextReply reply = null;
            try { if (json != null) reply = JsonUtility.FromJson<ContextReply>(json); } catch (ArgumentException) { }
            if (reply?.snapshot?.sessionId == sid && voice.Connected
                && !string.IsNullOrEmpty(reply.contextKey) && reply.contextKey != lastVoiceContextKey)
            {
                lastVoiceContextKey = reply.contextKey;
                voice.SendContext(reply.context);
            }
        }

        void Publish()
        {
            var displayed = Phase == "Selecting" || Phase == "Confirmed" ? preview : anatomy;
            highlighted = displayed ? displayed.HighlightedPartId : "";
            if (!realtime || candidate == null || !SharedMatches) return;
            realtime.PublishSnapshot(Phase, exercise.Current?.id, (uint)completedSteps, (uint)candidate.procedure.steps.Length,
                selected, highlighted, rotating && Phase == "Selecting", practicePaused || (Phase == "Practicing" && !exercise.CanScore),
                RegistrationReady && Practicing && SharedMatches,
                presentation && presentation.passthrough ? "Automatically acquired generic surface body fit" : "Authored virtual mannequin fit");
        }
        public bool ReturnToOffice()
        {
            if (OfficeHandoff == null) { Message = "No office encounter is bound to this practice."; return false; }
            if (!Application.CanStreamedLevelBeLoaded(EncounterOfficeRoute.OfficeScene))
            { Message = "The diagnosis office is not included in this player."; return false; }
            try { EncounterOfficeRoute.SelectPatient(SelectedPatientId, coachBaseUrl); }
            catch (ArgumentException) { Message = "The office subject or service endpoint is unavailable."; return false; }
            generation++; StopAllCoroutines(); busy = false; practicePaused = true;
            sharedAttemptReady = false; attemptRequested = false; attemptFailed = true;
            voiceRequested = false; coachSessionId = ""; Phase = "Startup";
            if (voice) voice.Disconnect();
            if (exercise) exercise.StopAttempt();
            if (anatomy) anatomy.SetRegistrationValid(false);
            if (coach) { coach.Tracking(false); coach.UseSession(""); }
            if (patientFrame) patientFrame.gameObject.SetActive(false);
            SceneManager.LoadScene(EncounterOfficeRoute.OfficeScene);
            return true;
        }

        public bool TryChangePresentation(bool passthrough)
        {
            if (!presentation || busy || (!HasHandoff && Phase != "Selecting" && Phase != "Recap")) return false;
            if (presentation.passthrough == passthrough) return true;
            if (HasHandoff && !HandoffRun.CanChoose(passthrough ? "mixed_reality" : "virtual", HandoffRun.Preflight)) return false;
            bool newAttempt = !HasHandoff || HandoffRun.SwitchNeedsNewAttempt(HandoffRun.Current);
            generation++; voice.Disconnect(); coachSessionId = "";
            presentation.passthrough = passthrough;
            if (bodyRegistration) bodyRegistration.StopTracking();
            presentation.Apply(); exercise.presentationMode = PresentationMode;
            if (HasHandoff) { HandoffRun.Current.presentationMode = PresentationMode; HandoffRun.Current.ResetTimeOut(); }
            if (newAttempt) Retry();
            else { Phase = "Confirmed"; Message = "Mode changed. Office score and attempt kept."; }
            return true;
        }
        public void PauseHandoffPractice()
        {
            practicePaused = true; SetHandoffVoiceAllowed(false); anatomy.SetRegistrationValid(false); coach.Tracking(false); voice.Disconnect();
        }
        public bool ResumeHandoffPractice()
        {
            if (!RegistrationReady || !SharedMatches) return false;
            practicePaused = false; if (CoachPrepared) ConnectVoice(); return true;
        }

        void UpdateUi()
        {
            string body;
            if (Phase == "Confirmed")
            {
                body = candidate.presentation + "\nSynthetic case risks:";
                foreach (var flag in candidate.brief.flags ?? Array.Empty<RiskFlag>()) body += "\n" + flag.title;
                body += "\nB: acknowledge brief and start";
            }
            else if (Phase == "Recap" && OfficeHandoff != null)
            {
                body = "Office: " + OfficeHandoff.scorecard.total + "/" + OfficeHandoff.scorecard.max + " · " + OfficeHandoff.scorecard.grade
                    + "\nSurgery: " + completedSteps + "/" + candidate.procedure.steps.Length + " steps · " + mistakes + " authored warnings"
                    + "\nLeft menu: choose another patient";
            }
            else if (Phase == "Practicing")
            {
                var step = exercise.Current;
                body = step == null ? "Finishing case" : $"{completedSteps + 1}/{candidate.procedure.steps.Length}: {step.title}\n{step.instruction}\nTool: {step.instrumentId}\nX: identify looked-at anatomy | B: confirm\nFocus: {selected}";
            }
            else
            {
                var layers = preview.GetComponent<Scalpal.Anatomy.Tissue.AnatomyLayerView>();
                body = "B: review " + (candidate?.procedure?.shortTitle ?? SelectedProcedureId) + " | Y: enable voice | A: reset tools | Left menu: " + (OfficeHandoff == null ? "new attempt" : "another patient");
                if (Phase == "Selecting" && layers) body += "\nX: anatomy layers | Showing: " + layers.Current;
            }
            if (Phase == "Confirmed" && OfficeHandoff != null)
                body = "Patient: " + OfficeHandoff.scorecard.patientName + "\nReviewed surgery: " + OfficeHandoff.procedureTitle
                    + "\nOffice: " + OfficeHandoff.scorecard.total + "/100 · " + OfficeHandoff.scorecard.grade
                    + "\nLearner proposed: " + OfficeHandoff.assessment.procedure + "\n" + body;
            string registration = presentation && presentation.passthrough ? "\n" + (bodyRegistration ? bodyRegistration.Status : "Body registration missing") : "\nVirtual mannequin fit";
            status.text = $"SCALPAL | {Phase} | {PresentationMode}\n{body}\n{Message}{registration}\nXR: {(workbench.IsReady ? "ready" : "paused")} | Shared: {realtime.Status}\nCoach: {(exercise.CoachMatches ? "synchronized" : coach.SyncFailureReason)} | Voice: {voice.Status}" + (OfficeHandoff == null ? "\nRight stick: AR/VR in selection" : "\nFull VR office practice");
        }
        IEnumerator Request(string method, string path, string body, Action<string> receive, Action<long> statusCode = null)
        {
            using (var request = new UnityWebRequest(coachBaseUrl.TrimEnd('/') + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 8;
                if (body != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)); request.SetRequestHeader("Content-Type", "application/json"); }
                yield return request.SendWebRequest();
                statusCode?.Invoke(request.responseCode);
                if (request.result == UnityWebRequest.Result.Success) receive(request.downloadHandler.text);
            }
        }
        void OnDestroy()
        {
            generation++;
            if (workbench) workbench.RetryRequested -= WorkbenchRetry;
            if (exercise) exercise.EventHandled -= EventHandled;
            if (coach) coach.CommandRequested -= CoachCommand;
            if (realtime) { realtime.AttemptStarted -= AttemptStarted; realtime.AttemptFailed -= AttemptFailed; realtime.CommandRequested -= SharedCommand; }
            if (voice) { voice.ClientToolRequested -= VoiceTool; voice.Transcript -= VoiceTranscript; voice.Disconnect(); }
        }
    }
}
