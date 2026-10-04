using System;
using System.Collections;
using System.IO;
using System.Text;
using Scalpal.Anatomy;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Realtime;
using Scalpal.Voice;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // One exercise across AR/VR. The case engine owns progression; adapters report real inputs.
    [DefaultExecutionOrder(75)]
    public sealed class NativeCaseSession : MonoBehaviour
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
        public const string ContentVersion = "0.1.0";
        public string Phase { get; private set; } = "Startup";
        public bool Practicing => Phase == "Practicing" && !practicePaused;
        public string Message { get; private set; } = "Loading synthetic case";
        NativeProcedureInput input;
        NativeTissueSimulation tissueSimulation;
        SurgicalCase candidate;
        string coachSessionId = "", voicePrompt = "", voiceGreeting = "", voiceContext = "";
        string selected = "", highlighted = "";
        bool busy, reviewed, practicePaused, rotating = true, attemptRequested, voiceRequested;
        bool previousB, previousX, previousY;
        bool sharedAttemptReady, attemptFailed;
        string boundSharedSession = "", boundSharedAttempt = "";
        Vector3 previewScale;
        bool SharedMatches => sharedAttemptReady && realtime.Paired && realtime.SessionId == boundSharedSession && realtime.AttemptId == boundSharedAttempt;
        int generation, completedSteps, mistakes;
        float nextUi, nextContext;
        string lastVoiceContextKey = "";

        [Serializable] public class DevelopmentConfig
        {
            public string uri, database, joinCode, preferredSessionId, coachBaseUrl;
        }
        [Serializable] class CreateRequest { public string patientId = PatientId; public string mode = "virtual"; }
        [Serializable] class Created
        {
            public string sessionId, context, systemPrompt, firstMessage;
            public CoachSnapshotState snapshot;
        }
        [Serializable] class ContextReply { public string context, contextKey; public CoachSnapshotState snapshot; }

        void Start()
        {
            if (!workbench || !exercise || !anatomy || !preview || !coach || !realtime || !voice || !status)
            { Message = "Session bindings missing"; enabled = false; return; }
            workbench.externalSessionControls = true;
            workbench.ResetRequested += Retry;
            ReadDevelopmentConfig();
            coach.ConfigureEndpoint(coachBaseUrl);
            voice.ConfigureEndpoint(coachBaseUrl);
            input = GetComponent<NativeProcedureInput>() ?? gameObject.AddComponent<NativeProcedureInput>();
            input.portMarkers = patientFrame.GetComponentsInChildren<NativePortMarker>(true);
            patientFrame.gameObject.SetActive(false);
            input.Initialize(exercise, workbench, () => Practicing && SharedMatches && RegistrationReady);
            tissueSimulation = GetComponent<NativeTissueSimulation>() ?? gameObject.AddComponent<NativeTissueSimulation>();
            tissueSimulation.Initialize(anatomy, workbench, () => isActiveAndEnabled && input.InteractionReady);
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
            if (!File.Exists(path)) return;
            try
            {
                var config = JsonUtility.FromJson<DevelopmentConfig>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(config.coachBaseUrl)) coachBaseUrl = config.coachBaseUrl;
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
            yield return Request("GET", "/patients/" + PatientId + "/case", null, value => json = value);
            if (epoch != generation) yield break;
            busy = false;
            try { candidate = json == null ? null : JsonUtility.FromJson<SurgicalCase>(json); }
            catch (ArgumentException) { candidate = null; }
            if (candidate == null || candidate.patientId != PatientId || candidate.procedureId != ProcedureId ||
                candidate.procedure == null || candidate.brief == null || !candidate.brief.synthetic ||
                (candidate.status != "ready" && candidate.status != "needs_review"))
            { candidate = null; Message = "Case service unavailable or case/assets mismatch. A: retry connection"; yield break; }
            Phase = "Selecting"; Message = "Appendectomy preview ready. X: anatomy layers; B: review synthetic case";
        }

        void Update()
        {
            if (!workbench) return;
            if (sharedAttemptReady && realtime.Paired && !SharedMatches)
            {
                sharedAttemptReady = false; attemptFailed = true;
                exercise.StopAttempt(); voice.Disconnect(); coachSessionId = ""; Phase = "Selecting";
                preview.gameObject.SetActive(true);
                Message = "Shared session/attempt changed; A: start a new matching attempt";
            }
            bool fitValid = RegistrationReady && Practicing && SharedMatches;
            patientFrame.gameObject.SetActive(fitValid);
            anatomy.SetRegistrationValid(fitValid);
            coach.Tracking(fitValid);
            if (realtime.Paired && !sharedAttemptReady && !attemptRequested && !attemptFailed) RequestAttempt();
            if (workbench.IsReady)
            {
                var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool b);
                left.TryGetFeatureValue(CommonUsages.primaryButton, out bool x);
                left.TryGetFeatureValue(CommonUsages.secondaryButton, out bool y);
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
            if (attemptRequested || !realtime.Paired) return;
            attemptRequested = realtime.BeginAttempt(ProcedureId, ContentVersion);
        }
        void AttemptStarted(string id)
        {
            attemptRequested = false; sharedAttemptReady = true; attemptFailed = false;
            boundSharedSession = realtime.SessionId; boundSharedAttempt = id; Publish();
        }
        void AttemptFailed(string reason) { attemptRequested = false; sharedAttemptReady = false; attemptFailed = true; Message = reason; }

        public bool ConfirmAction()
        {
            if (busy || candidate == null) return false;
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
            busy = true; Message = "Creating a fresh matched coach session";
            string json = null;
            yield return Request("POST", "/coach/sessions", JsonUtility.ToJson(new CreateRequest { mode = PresentationMode }), value => json = value);
            if (epoch != generation) yield break;
            if (!SharedMatches || !RegistrationReady) { busy = false; Message = "Shared attempt or body fit changed while loading; confirm and retry"; yield break; }
            Created created = null;
            try { if (json != null) created = JsonUtility.FromJson<Created>(json); } catch (ArgumentException) { }
            if (created?.snapshot == null || created.snapshot.caseId != candidate.caseId ||
                created.snapshot.patientId != PatientId || created.snapshot.procedureId != ProcedureId || created.snapshot.mode != PresentationMode)
            { busy = false; Message = "Coach case differs from the reviewed case; restart selection"; yield break; }
            coachSessionId = created.sessionId;
            voicePrompt = created.systemPrompt; voiceGreeting = created.firstMessage; voiceContext = created.context;
            exercise.explicitCoachSessionId = coachSessionId;
            if (!exercise.SelectCase(new ScalpalBundle { cases = new[] { candidate } }, candidate.caseId, reviewed, out var reason))
            { busy = false; Message = reason; yield break; }
            preview.gameObject.SetActive(false);
            Phase = "Practicing"; practicePaused = false;
            anatomy.SetRegistrationValid(RegistrationReady); coach.Tracking(RegistrationReady);
            busy = false; Message = "Practice starts after coach synchronization";
            realtime.AppendEvent("practice_started", exercise.Current?.id, null, "Generic teaching anatomy; " + PresentationMode, Time.realtimeSinceStartupAsDouble * 1000);
            if (voiceRequested) ConnectVoice();
            Publish();
        }

        public void Retry()
        {
            if (busy) return;
            if (tissueSimulation) tissueSimulation.ResetTissues();
            generation++; voice.Disconnect(); coachSessionId = "";
            sharedAttemptReady = false; attemptFailed = false; attemptRequested = false;
            exercise.StopAttempt(); workbench.ResetWorkbench();
            if (bodyRegistration) bodyRegistration.ResetFit();
            patientFrame.gameObject.SetActive(false);
            completedSteps = mistakes = 0; practicePaused = reviewed = false; highlighted = selected = "";
            preview.gameObject.SetActive(true); preview.SetPreviewMode(true); preview.RestoreVisibility();
            preview.SetPreviewRotation(true); rotating = true;
            preview.transform.parent.localScale = previewScale;
            anatomy.ClearHighlight();
            var previewLayers = preview.GetComponent<Scalpal.Anatomy.Tissue.AnatomyLayerView>();
            if (previewLayers) previewLayers.Show(Scalpal.Anatomy.Tissue.AnatomyLayerView.Layer.Organs);
            Phase = candidate == null ? "Startup" : "Selecting";
            Message = "New attempt. B: review the synthetic case";
            RequestAttempt();
            if (candidate == null) StartCoroutine(LoadCase(generation));
            Publish();
        }

        void EventHandled(CaseEvent action, CaseResult result)
        {
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
                Phase = "Recap"; anatomy.SetRegistrationValid(false); coach.Tracking(false);
                Message = $"Complete: {completedSteps}/{candidate.procedure.steps.Length} steps, {mistakes} authored warnings. A: new attempt";
                realtime.ReportAttemptResult((uint)completedSteps, (uint)candidate.procedure.steps.Length,
                    (uint)mistakes, 0, "Generic appendectomy rehearsal completed; " + PresentationMode);
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
                case "previewExercise": applied = Phase == "Selecting" && command.TargetId == ProcedureId; break;
                case "confirmExercise": applied = command.TargetId == ProcedureId && Phase == "Selecting" && ConfirmAction(); break;
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
        void ConnectVoice() { lastVoiceContextKey = ""; voice.ConfigureConversation(voicePrompt, voiceGreeting, voiceContext); voice.Connect(coachSessionId); }
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
            if (!realtime || candidate == null || !SharedMatches) return;
            realtime.PublishSnapshot(Phase, exercise.Current?.id, (uint)completedSteps, (uint)candidate.procedure.steps.Length,
                selected, highlighted, rotating && Phase == "Selecting", practicePaused || (Phase == "Practicing" && !exercise.CanScore),
                RegistrationReady && Practicing && SharedMatches,
                presentation && presentation.passthrough ? "Automatically acquired generic surface body fit" : "Authored virtual mannequin fit");
        }
        public bool TryChangePresentation(bool passthrough)
        {
            if (!presentation || busy || (Phase != "Selecting" && Phase != "Recap")) return false;
            if (presentation.passthrough == passthrough) return true;
            // A presentation change starts a new attempt; old commands/voice cannot cross modes.
            presentation.passthrough = passthrough;
            if (bodyRegistration) bodyRegistration.StopTracking();
            presentation.Apply();
            exercise.presentationMode = PresentationMode;
            Retry();
            return true;
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
            else if (Phase == "Practicing")
            {
                var step = exercise.Current;
                body = step == null ? "Finishing case" : $"{completedSteps + 1}/{candidate.procedure.steps.Length}: {step.title}\n{step.instruction}\nTool: {step.instrumentId}\nX: identify looked-at anatomy | B: confirm\nFocus: {selected}";
            }
            else
            {
                var layers = preview.GetComponent<Scalpal.Anatomy.Tissue.AnatomyLayerView>();
                body = "B: review appendectomy | Y: enable voice | A: retry";
                if (Phase == "Selecting" && layers) body += "\nX: anatomy layers | Showing: " + layers.Current;
            }
            string registration = presentation && presentation.passthrough ? "\n" + (bodyRegistration ? bodyRegistration.Status : "Body registration missing") : "\nVirtual mannequin fit";
            status.text = $"SCALPAL | {Phase} | {PresentationMode}\n{body}\n{Message}{registration}\nXR: {(workbench.IsReady ? "ready" : "paused")} | Shared: {realtime.Status}\nCoach: {(exercise.CoachMatches ? "synchronized" : coach.SyncFailureReason)} | Voice: {voice.Status}\nRight stick: AR/VR in selection";
        }
        IEnumerator Request(string method, string path, string body, Action<string> receive)
        {
            using (var request = new UnityWebRequest(coachBaseUrl.TrimEnd('/') + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 8;
                if (body != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)); request.SetRequestHeader("Content-Type", "application/json"); }
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success) receive(request.downloadHandler.text);
            }
        }
        void OnDestroy()
        {
            generation++;
            if (workbench) workbench.ResetRequested -= Retry;
            if (exercise) exercise.EventHandled -= EventHandled;
            if (coach) coach.CommandRequested -= CoachCommand;
            if (realtime) { realtime.AttemptStarted -= AttemptStarted; realtime.AttemptFailed -= AttemptFailed; realtime.CommandRequested -= SharedCommand; }
            if (voice) { voice.ClientToolRequested -= VoiceTool; voice.Transcript -= VoiceTranscript; voice.Disconnect(); }
        }
    }
}
