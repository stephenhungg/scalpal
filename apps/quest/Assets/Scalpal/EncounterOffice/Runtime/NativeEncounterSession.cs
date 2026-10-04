using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.IO;
using Scalpal.Voice;
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
        public EncounterState State { get; private set; }
        public EncounterScore Score { get; private set; }
        public string Status { get; private set; } = "Choose Priya or Jonah to begin.";
        public string LastResponse { get; private set; } = "";
        public string Role { get; private set; } = "patient";
        public bool Busy => working || pending.Count > 0;
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
        [Serializable] sealed class EndpointConfig { public string coachBaseUrl, encounterBaseUrl; }
        void Start()
        {
            if (!Debug.isDebugBuild) return;
            string path = Path.Combine(Application.persistentDataPath, "session-config.json");
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var files = activity.Call<AndroidJavaObject>("getFilesDir"))
                path = Path.Combine(files.Call<string>("getAbsolutePath"), "session-config.json");
#endif
            if (!File.Exists(path)) return;
            try
            {
                var config = JsonUtility.FromJson<EndpointConfig>(File.ReadAllText(path));
                string selected = string.IsNullOrWhiteSpace(config.encounterBaseUrl) ? config.coachBaseUrl : config.encounterBaseUrl;
                if (!string.IsNullOrWhiteSpace(selected) && Uri.TryCreate(selected, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) baseUrl = selected.TrimEnd('/');
                else SetStatus("Development encounter service URL is invalid.");
            }
            catch (Exception) { SetStatus("Development encounter configuration is invalid."); }
        }
        void OnEnable()
        {
            if (voice) voice.MicrophoneMuted = true;
            if (voice) { voice.ClientToolRequested += VoiceTool; voice.Transcript += Transcript; voice.StatusChanged += VoiceStatus; voice.ModeChanged += VoiceMode; }
        }
        void OnDisable()
        {
            if (voice) { voice.ClientToolRequested -= VoiceTool; voice.Transcript -= Transcript; voice.StatusChanged -= VoiceStatus; voice.ModeChanged -= VoiceMode; }
            Invalidate(); State = null; Score = null; encounterId = ""; Role = "patient"; LastResponse = ""; Status = "Choose Priya or Jonah to begin.";
        }
        void Invalidate()
        {
            TalkHeld = false;
            if (voice) voice.MicrophoneMuted = true;
            generation++; if (voice) voice.Disconnect();
            activeRequest?.Abort(); StopAllCoroutines(); activeRequest = null; pending.Clear(); working = false;
        }
        public void StartPatient(string id)
        {
            if (id != EncounterContract.FemalePatientId && id != EncounterContract.MalePatientId) throw new ArgumentException("Only selected authored adult demo cases are supported.");
            Invalidate(); patientId = id; encounterId = ""; State = null; Score = null; Role = "patient"; Draft = new EncounterAssessment(); LastResponse = "";
            if (patient) patient.Select(id);
            SetStatus("Loading synthetic encounter…");
            Enqueue("POST", "/encounters", JsonUtility.ToJson(new PatientRequest { patientId = id }), false, reply =>
            {
                if (reply.state == null || reply.state.patientId != id || reply.encounterId != reply.state.encounterId || reply.speaker != "patient") { SetStatus("Encounter identity mismatch. Choose a patient again."); return; }
                encounterId = reply.encounterId; State = reply.state; prompt = reply.patientPrompt; greeting = reply.patientFirstMessage; voiceId = reply.voiceId;
                LastResponse = "Patient: " + greeting; SetStatus("Interview ready. Hold grip to talk, or select questions.");
                if (patient) patient.SetState("listening");
                PlayAuthoredSpeech("greeting", "", greeting);
            });
        }
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
            LastResponse = "Jarvis: " + greeting; if (patient) patient.SetState("resting"); SetStatus("Present your diagnosis, differential, plan and timing to Jarvis.");
        }
        void AdoptScore(EncounterReply reply, string status)
        {
            if (reply.scorecard == null || string.IsNullOrEmpty(reply.scorecard.grade) || reply.scorecard.max != 100 || reply.scorecard.total < 0 || reply.scorecard.total > 100)
            { SetStatus("Assessment score reply is incomplete. Refresh state."); return; }
            Score = reply.scorecard; SetStatus(status);
        }
        public void SubmitAssessment()
        {
            if (Busy || State == null || State.phase != "attending") return;
            Tool("record_assessment", JsonUtility.ToJson(Draft));
        }
        public void RequestSummary() => Tool("get_encounter_summary", "{}");
        public void StartVoice()
        {
            if (!voice || State == null || Busy || State.phase == "scored") return;
            if ((Role == "patient" && State.phase != "interview") || (Role == "attending" && State.phase != "attending"))
            { SetStatus("Refresh the encounter to load the correct conversation role before starting voice."); return; }
            try
            {
                voice.MicrophoneMuted = !OpenMicrophone && !TalkHeld;
                voice.ConfigureEndpoint(baseUrl);
                voice.ConfigureEncounterConversation(prompt, greeting, voiceId, Role);
                voice.ConnectEncounter(encounterId, patientId);
            }
            catch (ArgumentException) { SetStatus("Set a valid HTTP(S) encounter service URL."); }
        }
        public void SetTalkHeld(bool held)
        {
            if (held && (State == null || State.phase == "scored" || !voice)) return;
            if (held == TalkHeld) return;
            TalkHeld = held;
            if (voice)
            {
                voice.MicrophoneMuted = !OpenMicrophone && !held;
                if (held)
                {
                    voice.InterruptPlayback();
                    if (!voice.Connected && voice.Status != "connecting") StartVoice();
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
        public void StopVoice() { TalkHeld = false; if (voice) { voice.MicrophoneMuted = true; voice.Disconnect(); } if (patient) patient.SetState(Role == "patient" ? "listening" : "resting"); SetStatus("Voice stopped. Visual controls remain available."); }
        void VoiceStatus(string value)
        {
            if (value == "error")
            {
                TalkHeld = false;
                if (voice) voice.MicrophoneMuted = true;
                SetStatus("Live voice unavailable. Authored offline speech and visual questions work.");
                if (Role == "patient") PlayAuthoredSpeech("greeting", "", greeting);
            }
            else if (value == "offline") SetStatus("Offline patient voice. Select questions to hear authored answers.");
            else if (value == "connected") SetStatus(OpenMicrophone ? "Open mic enabled." : "Voice ready. Hold grip to talk; release for the reply.");
            else { SetStatus((Role == "patient" ? "Patient voice: " : "Jarvis voice: ") + value); }
        }
        void VoiceMode(string mode) { if (patient && Role == "patient") patient.SetState(mode == "speaking" ? "speaking" : "listening"); Notify(); }
        void Transcript(string source, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || State == null) return;
            string speaker = source == "user" ? "learner" : Role == "patient" ? "patient" : "coach";
            LastResponse = (speaker == "coach" ? "Jarvis" : speaker) + ": " + text;
            Enqueue("POST", "/encounters/" + Uri.EscapeDataString(encounterId) + "/transcript", JsonUtility.ToJson(new TranscriptRequest { speaker = speaker, text = text }), false, null);
            Notify();
        }
        void VoiceTool(QuestJarvisVoice.ToolRequest request) => Tool(request.ToolName, request.ParametersJson, request);
        void PlayAuthoredSpeech(string tool, string argument, string display)
        {
            if (!voice || Role != "patient" || voice.Connected || voice.Status == "connecting") return;
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
                LastResponse = (Role == "patient" ? "Authored patient response / reaction:\n" : "Jarvis:\n") + (reply.display ?? reply.result);
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
        void OnApplicationFocus(bool focus) { if (!focus) { SetTalkHeld(false); if (voice && voice.PlaybackActive) voice.Disconnect(); } }
        void OnApplicationPause(bool pause) { if (pause) { SetTalkHeld(false); if (voice && voice.PlaybackActive) voice.Disconnect(); } }
        void SetStatus(string text) { Status = text; Notify(); }
        void Notify() => Changed?.Invoke();
    }
}
