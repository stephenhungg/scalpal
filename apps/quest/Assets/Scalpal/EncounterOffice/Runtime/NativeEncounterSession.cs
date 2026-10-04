using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Scalpal.Voice;
using Scalpal.Exercises.Data;
using Scalpal.Realtime;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.EncounterOffice
{
    public sealed class NativeEncounterSession : MonoBehaviour
    {
        public string baseUrl = "http://localhost:8787";
        public int timeoutSeconds = 12;
        public QuestJarvisVoice voice;
        public EncounterPatientPresentation patient;
        public QuestSessionBridge realtime;
        string sharedSessionId = "", sharedAttemptId = "";
        bool waitingForAttempt, attemptRejected;
        public EncounterState State { get; private set; }
        public EncounterScore Score { get; private set; }
        public string Status { get; private set; } = "Choose a synthetic patient to begin.";
        public PatientListEntry[] Patients { get; private set; } = new PatientListEntry[0];
        public string SelectedPatientId => patientId;
        public string AuthoredProcedureId { get; private set; } = "";
        public bool Suspended { get; private set; }
        bool focused = true, paused;
        public bool SurgeryReady => State?.phase == "scored" && Score != null && Score.procedureId == AuthoredProcedureId;
        public string LastResponse { get; private set; } = "";
        // Who said LastResponse: the patient or parent by name, "Jarvis · Attending", or the learner. Never a blend.
        public string LastSpeaker { get; private set; } = "";
        // The patient voice connects as soon as an encounter starts and Jarvis connects at the presentation,
        // unless the learner turned voice off. Grip-to-talk only gates the microphone, not the connection.
        public bool VoiceEnabled { get; private set; } = true;
        public bool VoiceLive => voice && (voice.Connected || voice.Status == "connecting");
        public string Role { get; private set; } = "patient";
        public bool Busy => working || pending.Count > 0 || waitingForAttempt;
        public bool TalkHeld { get; private set; }
        public bool OpenMicrophone { get; private set; }
        public event Action Changed;
        public EncounterAssessment Draft = new EncounterAssessment();
        string encounterId = "", patientId = "", prompt = "", greeting = "", voiceId = "";
        int generation;
        bool working;
        UnityWebRequest activeRequest;
        readonly Queue<Operation> pending = new Queue<Operation>();
        sealed class Operation
        {
            public string method, path, body;
            public int epoch;
            public string id, patientId;
            public bool needsState;
            public float queuedAt;
            public Action<EncounterReply> complete;
            public QuestJarvisVoice.ToolRequest tool;
        }
        [Serializable] sealed class EndpointConfig { public string coachBaseUrl, encounterBaseUrl, uri, database, joinCode, preferredSessionId; }
        void Start()
        {
            ReadDevelopmentConfig();
            if (realtime) realtime.Reconnect();
            bool hasSelection = EncounterOfficeRoute.TakePatient(out var selectedPatient, out var selectedEndpoint);
            if (!string.IsNullOrEmpty(selectedEndpoint)) baseUrl = selectedEndpoint;
            if (hasSelection) StartPatient(selectedPatient);
            else LoadPatients();
        }
        void ReadDevelopmentConfig()
        {
            if (!Debug.isDebugBuild) return;
            string path = Path.Combine(Application.persistentDataPath, "session-config.json");
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
                path = Path.Combine(files.Call<string>("getAbsolutePath"), "session-config.json");
#endif
#if UNITY_EDITOR
            string fixturePath = Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG");
            if (!string.IsNullOrWhiteSpace(fixturePath)) path = fixturePath;
#endif
            if (!File.Exists(path)) return;
            try
            {
                var config = JsonUtility.FromJson<EndpointConfig>(File.ReadAllText(path));
                string selected = string.IsNullOrWhiteSpace(config.encounterBaseUrl) ? config.coachBaseUrl : config.encounterBaseUrl;
                if (!string.IsNullOrWhiteSpace(selected) && Uri.TryCreate(selected, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) baseUrl = selected.TrimEnd('/');
                else SetStatus("Development encounter service URL is invalid.");
                if (realtime)
                {
                    if (!string.IsNullOrWhiteSpace(config.uri)) realtime.uri = config.uri;
                    if (!string.IsNullOrWhiteSpace(config.database)) realtime.database = config.database;
                    realtime.joinCode = config.joinCode ?? "";
                    realtime.preferredSessionId = config.preferredSessionId ?? "";
                }
            }
            catch (Exception) { SetStatus("Development encounter configuration is invalid."); }
        }
        void OnEnable()
        {
            if (realtime) { realtime.AttemptStarted += OfficeAttemptStarted; realtime.AttemptFailed += OfficeAttemptFailed; }
            if (voice) voice.MicrophoneMuted = true;
            if (voice) { voice.ClientToolRequested += VoiceTool; voice.Transcript += Transcript; voice.StatusChanged += VoiceStatus; voice.ModeChanged += VoiceMode; }
        }
        void OnDisable()
        {
            if (realtime) { realtime.AttemptStarted -= OfficeAttemptStarted; realtime.AttemptFailed -= OfficeAttemptFailed; }
            if (voice) { voice.ClientToolRequested -= VoiceTool; voice.Transcript -= Transcript; voice.StatusChanged -= VoiceStatus; voice.ModeChanged -= VoiceMode; }
            Invalidate(); State = null; Score = null; encounterId = ""; Role = "patient"; LastResponse = ""; LastSpeaker = ""; Status = "Choose a synthetic patient to begin.";
        }
        void Invalidate()
        {
            TalkHeld = false; waitingForAttempt = false;
            if (voice) voice.MicrophoneMuted = true;
            generation++; if (voice) voice.Disconnect();
            activeRequest?.Abort(); StopAllCoroutines(); activeRequest = null; pending.Clear(); working = false;
        }
        public void LoadPatients()
        {
            if (Busy) return;
            SetStatus("Loading synthetic patients…");
            Enqueue("GET", "/patients", null, false, reply =>
            {
                if (reply.patients == null) { SetStatus("Patient list reply is incomplete. Reload the list."); return; }
                Patients = reply.patients;
                SetStatus("Choose an available synthetic patient.");
            });
        }
        public void StartPatient(string id)
        {
            if (!EncounterContract.ValidPatientId(id)) { SetStatus("Choose a canonical patient subject."); return; }
            Invalidate(); EncounterOfficeRoute.ClearSurgery(); patientId = id; encounterId = ""; AuthoredProcedureId = "";
            sharedSessionId = sharedAttemptId = "";
            State = null; Score = null; Role = "patient"; Draft = new EncounterAssessment(); LastResponse = ""; LastSpeaker = ""; Suspended = false;
            if (patient) patient.Select(null);
            SetStatus("Loading synthetic encounter…");
            Enqueue("GET", "/patients/" + Uri.EscapeDataString(id) + "/case", null, false, authored =>
            {
                if (authored.patientId != id || authored.brief?.synthetic != true || string.IsNullOrEmpty(authored.procedureId)
                    || authored.status != "ready" && authored.status != "needs_review")
                { SetStatus("This patient is unavailable for a synthetic encounter."); return; }
                AuthoredProcedureId = authored.procedureId;
                if (realtime) StartCoroutine(BeginOfficeAttempt(id, generation));
                else CreateEncounter(id); // Isolated fixture/editor consumer has no shared bridge.
            });
        }
        IEnumerator BeginOfficeAttempt(string id, int epoch)
        {
            waitingForAttempt = true; attemptRejected = false;
            SetStatus("Waiting for the paired headset session…");
            float deadline = Time.realtimeSinceStartup + 20;
            while (!realtime.Paired && Time.realtimeSinceStartup < deadline && epoch == generation) yield return null;
            if (epoch != generation) yield break;
            if (!realtime.Paired || !realtime.BeginAttempt(AuthoredProcedureId, "0.1.0"))
            { waitingForAttempt = false; SetStatus("Pair the headset session, then choose this patient again."); yield break; }
            deadline = Time.realtimeSinceStartup + 20;
            while (waitingForAttempt && !attemptRejected && Time.realtimeSinceStartup < deadline && epoch == generation) yield return null;
            if (epoch != generation) yield break;
            if (waitingForAttempt || attemptRejected || !realtime.Paired || realtime.SessionId != sharedSessionId || realtime.AttemptId != sharedAttemptId)
            { waitingForAttempt = false; SetStatus("Shared attempt did not confirm. Choose the patient again."); yield break; }
            CreateEncounter(id);
        }
        void OfficeAttemptStarted(string id)
        {
            if (!waitingForAttempt || !realtime || !realtime.Paired) return;
            sharedSessionId = realtime.SessionId; sharedAttemptId = id; waitingForAttempt = false;
        }
        void OfficeAttemptFailed(string reason) { if (waitingForAttempt) { attemptRejected = true; SetStatus(reason); } }
        void CreateEncounter(string id)
        {
            Enqueue("POST", "/encounters", JsonUtility.ToJson(new PatientRequest { patientId = id }), false, reply =>
                {
                    if (reply.state == null || reply.state.patientId != id || reply.encounterId != reply.state.encounterId
                        || !QuestJarvisVoice.ValidEncounterId(reply.encounterId) || reply.state.phase != "interview"
                        || reply.speaker != "patient" && reply.speaker != "parent" || reply.speaker != reply.state.speaker)
                    { SetStatus("Encounter identity mismatch. Choose a patient again."); return; }
                    encounterId = reply.encounterId; State = reply.state; prompt = reply.patientPrompt; greeting = reply.patientFirstMessage; voiceId = reply.voiceId;
                    if (patient) { patient.Select(State); patient.SetState("listening"); if (voice) patient.BindVoice(voice, true); }
                    Say(EncounterContract.SpeakerLabel(State, "patient"), greeting);
                    SetStatus("Interview ready. Hold grip to talk, or select questions.");
                    // The patient greets the learner out loud as the encounter starts. A failed connection plays the
                    // bundled greeting from VoiceStatus; with voice off the bundled greeting plays directly.
                    if (VoiceEnabled) ConnectVoice();
                    else PlayAuthoredSpeech("greeting", "", greeting);
                });
        }
        // Snapshot the scored encounter's original shared attempt before changing scenes.
        // The handoff flow owns presentation choice, transition and Time-Out.
        public bool TryPrepareHandoff(out EncounterSurgeryHandoff handoff, out string reason)
        {
            handoff = null;
            if (Busy || !SurgeryReady)
            { reason = "Complete Jarvis feedback before entering the OR."; return false; }
            if (realtime && (!realtime.Paired || realtime.SessionId != sharedSessionId || realtime.AttemptId != sharedAttemptId
                || !realtime.TryGetEncounterBinding(encounterId, out var boundSession, out var boundAttempt, out var boundPatient, out var boundPhase)
                || boundSession != sharedSessionId || boundAttempt != sharedAttemptId || boundPatient != patientId || boundPhase != "scored"))
            { reason = "The scored encounter must belong to the current paired attempt before entering the OR."; return false; }
            if (!EncounterOfficeRoute.PrepareSurgery(State, Score, AuthoredProcedureId, baseUrl, out reason, sharedSessionId, sharedAttemptId))
                return false;
            if (!EncounterOfficeRoute.TakeSurgery(out handoff))
            { reason = "The scored encounter handoff could not be prepared. Refresh the scorecard."; return false; }
            reason = "";
            return true;
        }
        public void ContinueToSurgery() => Scalpal.Handoff.HandoffFlow.OpenFromOffice(this);
        public void Ask(string topic) => Tool("answer", JsonUtility.ToJson(new TopicRequest { topic = topic }));
        public void Examine(string maneuver) => Tool("examine", JsonUtility.ToJson(new ExamRequest { maneuver = maneuver }));
        public void OrderTest(string test) => Tool("order_test", JsonUtility.ToJson(new TestRequest { test = test }));
        public void RefreshState()
        {
            if (string.IsNullOrEmpty(encounterId) || Busy) return;
            Enqueue("GET", "/encounters/" + Uri.EscapeDataString(encounterId), null, true, reply =>
            {
                if (reply.state.phase == "attending")
                {
                    Enqueue("POST", "/encounters/" + Uri.EscapeDataString(encounterId) + "/attending", "{}", true, AdoptAttending);
                }
                else if (reply.state.phase == "scored")
                {
                    Role = "attending";
                    Enqueue("GET", "/encounters/" + Uri.EscapeDataString(encounterId) + "/score", null, true, scored => AdoptScore(scored, "Assessment reconciled with the service."));
                }
                else { Role = "patient"; SetStatus("Encounter reconciled with the service."); }
            });
        }
        public void SeeAttending()
        {
            if (State == null || Busy || State.phase != "interview") return;
            SetTalkHeld(false);
            if (voice) voice.Disconnect();
            Enqueue("POST", "/encounters/" + Uri.EscapeDataString(encounterId) + "/attending", "{}", true, AdoptAttending);
        }
        void AdoptAttending(EncounterReply reply)
        {
            if (reply.state?.phase != "attending" || string.IsNullOrEmpty(reply.attendingPrompt) || string.IsNullOrEmpty(reply.attendingFirstMessage))
            { SetStatus("Attending conversation context is incomplete. Refresh state."); return; }
            Role = "attending"; prompt = reply.attendingPrompt; greeting = reply.attendingFirstMessage; voiceId = "";
            Say(EncounterContract.SpeakerLabel(State, "attending"), greeting); if (patient) patient.SetState("resting"); SetStatus("Present your diagnosis, differential, plan and timing to Jarvis.");
            if (patient && voice) patient.BindVoice(voice, false);
            if (VoiceEnabled) ConnectVoice(); // the patient agent is disconnected; Jarvis joins as attending to hear the presentation
        }
        void AdoptScore(EncounterReply reply, string status)
        {
            if (reply.scorecard == null || string.IsNullOrEmpty(reply.scorecard.grade) || reply.scorecard.max != 100 || reply.scorecard.total < 0 || reply.scorecard.total > 100)
            { SetStatus("Assessment score reply is incomplete. Refresh state."); return; }
            if (reply.scorecard.patientId != patientId || reply.scorecard.procedureId != AuthoredProcedureId) { SetStatus("Reviewed procedure does not match this patient. Refresh state."); return; }
            Score = reply.scorecard; Say(EncounterContract.SpeakerLabel(State, "attending"), Score.spoken); SetStatus(status + " Review the feedback, then enter the OR for " + Score.procedureTitle + ".");
        }
        public void SubmitAssessment()
        {
            if (Busy || State == null || State.phase != "attending") return;
            Tool("record_assessment", JsonUtility.ToJson(Draft));
        }
        public void RequestSummary() => Tool("get_encounter_summary", "{}");
        public void StartVoice()
        {
            if (Busy) return;
            ConnectVoice();
        }
        // Also used from within a completing request (encounter start, attending hand-off), where Busy is still true.
        void ConnectVoice()
        {
            if (!focused || paused || !voice || State == null || State.phase == "scored") return;
            Suspended = false;
            if ((Role == "patient" && State.phase != "interview") || (Role == "attending" && State.phase != "attending"))
            { SetStatus("Refresh the encounter to load the correct conversation role before starting voice."); return; }
            try
            {
                voice.MicrophoneMuted = !OpenMicrophone && !TalkHeld;
                voice.ConfigureEndpoint(baseUrl);
                voice.ConfigureEncounterConversation(prompt, greeting, voiceId, Role);
                // The patient speaks from the seated speaker's mouth in 3D; Jarvis is not in the room.
                if (patient) patient.BindVoice(voice, Role == "patient");
                voice.ConnectEncounter(encounterId, patientId);
            }
            catch (ArgumentException) { SetStatus("Set a valid HTTP(S) encounter service URL."); }
        }
        public void SetTalkHeld(bool held)
        {
            if (held && (!focused || paused || State == null || State.phase == "scored" || !voice)) return;
            if (held == TalkHeld) return;
            TalkHeld = held;
            if (voice)
            {
                voice.MicrophoneMuted = !OpenMicrophone && !held;
                if (held)
                {
                    voice.InterruptPlayback();
                    if (!voice.Connected && voice.Status != "connecting") { VoiceEnabled = true; StartVoice(); }
                }
            }
            if (held) SetStatus("Listening while you hold grip. Release to hear the reply.");
            else if (voice && voice.Connected) SetStatus(OpenMicrophone ? "Open mic enabled." : "Hold grip to talk.");
            Notify();
        }
        public void ToggleOpenMicrophone()
        {
            OpenMicrophone = !OpenMicrophone;
            if (voice) voice.MicrophoneMuted = !OpenMicrophone && !TalkHeld;
            SetStatus(OpenMicrophone ? "Open mic enabled. Use Stop to disconnect." : "Hold grip to talk.");
        }
        public void StopVoice() { VoiceEnabled = false; TalkHeld = false; if (voice) { voice.MicrophoneMuted = true; voice.Disconnect(); } if (patient) patient.SetState(Role == "patient" ? "listening" : "resting"); SetStatus("Voice off. Visual controls remain available; select Voice on or hold grip to talk."); }
        // Explicit Voice on/off toggle. Off disconnects and stops auto-connect for later roles; on (or a retry after a
        // failure or focus loss) connects the current role.
        public void ToggleVoice()
        {
            if (VoiceEnabled && VoiceLive) { StopVoice(); return; }
            VoiceEnabled = true;
            StartVoice();
            Notify();
        }
        void VoiceStatus(string value)
        {
            if (value == "error")
            {
                TalkHeld = false;
                if (voice) voice.MicrophoneMuted = true;
                string reason = voice && !string.IsNullOrEmpty(voice.LastError) ? voice.LastError : "unknown error";
                if (Role == "patient" && !Suspended) PlayAuthoredSpeech("greeting", "", greeting);
                // After the offline greeting, whose own "offline" status must not hide why live voice failed.
                SetStatus((Role == "patient" ? "Patient" : "Jarvis") + " voice unavailable: " + reason + " Hold grip or select Voice to retry; visual questions still work.");
            }
            else if (value == "offline") SetStatus("Offline patient voice. Select questions to hear authored answers.");
            else if (value == "connected") SetStatus(OpenMicrophone ? "Open mic enabled." : "Voice ready. Hold grip to talk; release for the reply.");
            else if (value == "connecting") SetStatus((Role == "patient" ? "Connecting " + EncounterContract.SpeakerLabel(State, "patient") + " voice…" : "Connecting Jarvis · Attending voice…"));
            else { SetStatus((Role == "patient" ? "Patient voice: " : "Jarvis voice: ") + value); }
        }
        void VoiceMode(string mode) { if (patient && Role == "patient") patient.SetState(mode == "speaking" ? "speaking" : "listening"); Notify(); }
        void Transcript(string source, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || State == null) return;
            string speaker = source == "user" ? "learner" : Role == "patient" ? "patient" : "coach";
            Say(source == "user" ? EncounterContract.LearnerLabel : EncounterContract.SpeakerLabel(State, Role), text);
            Enqueue("POST", "/encounters/" + Uri.EscapeDataString(encounterId) + "/transcript", JsonUtility.ToJson(new TranscriptRequest { speaker = speaker, text = text }), false, null);
            Notify();
        }
        void VoiceTool(QuestJarvisVoice.ToolRequest request) => Tool(request.ToolName, request.ParametersJson, request);
        void PlayAuthoredSpeech(string tool, string argument, string display)
        {
            if (!focused || paused || Suspended || !voice || Role != "patient" || voice.Connected || voice.Status == "connecting") return;
            voice.InterruptPlayback(); // A changed/visual-only answer must not retain the previous spoken response.
            var clip = EncounterPatientSpeech.Find(patientId, tool, argument, display);
            if (clip) voice.PlayLocalSpeech(clip);
        }
        void Tool(string name, string body, QuestJarvisVoice.ToolRequest request = null)
        {
            if (State == null || !EncounterContract.ToolAllowed(Role, State.phase, name))
            {
                if (request != null && voice) voice.ResolveClientTool(request, "That tool is unavailable for this encounter role or phase.", true);
                SetStatus("That action is unavailable in the current encounter phase."); return;
            }
            if (request == null && Busy) return;
            Enqueue("POST", "/encounters/" + Uri.EscapeDataString(encounterId) + "/tools/" + Uri.EscapeDataString(name), string.IsNullOrEmpty(body) ? "{}" : body, true, reply =>
            {
                Say(EncounterContract.SpeakerLabel(State, Role), reply.display ?? reply.result);
                SetStatus("Recorded by the encounter service.");
                if (request == null && name == "answer")
                {
                    var topic = JsonUtility.FromJson<TopicRequest>(body);
                    PlayAuthoredSpeech("answer", topic?.topic, reply.display);
                }
                if (reply.state.phase == "scored")
                {
                    Enqueue("GET", "/encounters/" + Uri.EscapeDataString(encounterId) + "/score", null, true, scored => AdoptScore(scored, "Attending assessment complete."));
                }
            }, request);
        }
        void Enqueue(string method, string path, string body, bool needsState, Action<EncounterReply> complete, QuestJarvisVoice.ToolRequest tool = null)
        {
            if (pending.Count >= 64) { if (tool != null && voice) voice.ResolveClientTool(tool, "Encounter request queue is full.", true); SetStatus("Too many pending requests. Wait and refresh state."); return; }
            var operation = new Operation { method = method, path = path, body = body, epoch = generation, id = encounterId, patientId = patientId, needsState = needsState, complete = complete, tool = tool, queuedAt = Time.realtimeSinceStartup };
            // Tools take priority over transcript bookkeeping and remain within the transport's 10s acknowledgement window.
            if (tool != null)
            {
                var waiting = pending.ToArray(); pending.Clear();
                foreach (var previous in waiting) if (previous.tool != null) pending.Enqueue(previous);
                pending.Enqueue(operation);
                foreach (var previous in waiting) if (previous.tool == null) pending.Enqueue(previous);
            }
            else pending.Enqueue(operation);
            if (!working) { working = true; if (Application.isPlaying) StartCoroutine(Drain()); }
            Notify();
        }
        IEnumerator Drain()
        {
            while (pending.Count > 0)
            {
                var op = pending.Dequeue();
                if (op.epoch != generation || op.tool != null && (!voice || !voice.OwnsClientTool(op.tool))) continue;
                if (op.tool != null && Time.realtimeSinceStartup - op.queuedAt > 3)
                { voice.ResolveClientTool(op.tool, "Another encounter request delayed this action. Please try again.", true); continue; }
                EncounterReply reply = null;
                string failure = "";
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) failure = "Set a valid HTTP(S) service URL.";
                else using (var request = new UnityWebRequest(baseUrl.TrimEnd('/') + op.path, op.method))
                {
                    activeRequest = request; request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = op.path == "/encounters" ? timeoutSeconds : Mathf.Min(timeoutSeconds, 4);
                    if (op.body != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(op.body)); request.SetRequestHeader("Content-Type", "application/json"); }
                    yield return request.SendWebRequest(); activeRequest = null;
                    if (op.epoch != generation) yield break;
                    try { reply = JsonUtility.FromJson<EncounterReply>(request.downloadHandler.text); } catch (ArgumentException) { }
                    if (request.result != UnityWebRequest.Result.Success || reply == null || EncounterContract.HasError(reply))
                        failure = !string.IsNullOrEmpty(reply?.error?.message) ? reply.error.message : "Service unavailable. Refresh state before retrying; an action may have reached the service.";
                    else if (op.path.Contains("/tools/") && string.IsNullOrEmpty(reply.result))
                        failure = "Encounter tool reply is incomplete. Refresh state.";
                    else if (op.needsState && !EncounterContract.StateMatches(reply.state, op.id, op.patientId, State?.version ?? 0))
                        failure = "Stale or mismatched encounter response. Refresh state.";
                }
                if (op.epoch != generation) yield break;
                if (failure.Length > 0)
                {
                    if (op.tool != null && voice) voice.ResolveClientTool(op.tool, failure, true);
                    SetStatus(failure);
                }
                else
                {
                    if (op.needsState) State = reply.state;
                    // Resolve on the originating connection so the attending can speak returned score feedback.
                    if (op.tool != null && voice) voice.ResolveClientTool(op.tool, reply.result ?? "Recorded.");
                    op.complete?.Invoke(reply); Notify();
                }
            }
            working = false; Notify();
        }
        void OnApplicationFocus(bool focus) { focused = focus; if (!focus) SuspendVoice(); }
        void OnApplicationPause(bool pause) { paused = pause; if (pause) SuspendVoice(); }
        void SuspendVoice()
        {
            Suspended = true; TalkHeld = false;
            if (voice) voice.MicrophoneMuted = true;
            // The transport handles the OS microphone permission prompt separately. Never reconnect on focus return.
            if (patient) patient.SetState("resting");
            SetStatus("Voice paused. Hold grip or select Voice to resume this encounter.");
        }
        void Say(string speaker, string text) { LastSpeaker = speaker; LastResponse = text ?? ""; }
        void SetStatus(string text) { Status = text; Notify(); }
        void Notify() => Changed?.Invoke();
    }
}
