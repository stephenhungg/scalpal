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
    // The pre-op office: a 1:1 choice-based interview with the patient voice (POST /interviews, docs/office-interview.md).
    // Each round offers four clinician moves; the learner taps one (ray or pinch) or holds a button and says it. The
    // patient agent hears only the picks (microphone muted throughout) and replies in character. Jarvis is not in the
    // office: after the last round the scorecard is shown on screen, then the theatre card.
    public sealed class NativeEncounterSession : MonoBehaviour
    {
        public string baseUrl = "http://localhost:8787";
        public int timeoutSeconds = 12;
        public QuestJarvisVoice voice;
        public EncounterPatientPresentation patient;
        public QuestSessionBridge realtime;
        string sharedSessionId = "", sharedAttemptId = "";
        bool waitingForAttempt, attemptRejected;
        // encounterId carries the interview id (int-...); phase is interview or scored.
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
        // Who said LastResponse: the patient or parent by name, or the learner. Never a blend.
        public string LastSpeaker { get; private set; } = "";
        // The patient voice connects as soon as an interview starts unless the learner turned voice off.
        public bool VoiceEnabled { get; private set; } = true;
        public bool VoiceLive => voice && (voice.Connected || voice.Status == "connecting");
        public string Role => "patient"; // the only voice in the office
        public bool Busy => blocking > 0 || waitingForAttempt;
        // Held answer button: recording a spoken answer locally (never streamed to the patient agent).
        public bool TalkHeld { get; private set; }
        // The round the learner is answering, shown only once the patient has finished speaking.
        public InterviewRoundView Round => RoundVisible ? round : null;
        public bool RoundVisible => round != null && !AwaitingPatient && State?.phase == "interview";
        public bool AwaitingPatient { get; private set; }
        // Re-ask or recording hints under the choices ("Say A, B, C or D, or tap one").
        public string AnswerNote { get; private set; } = "";
        // Exam and test results from picks, shown on the findings panel, never spoken.
        public readonly List<InterviewFinding> Findings = new List<InterviewFinding>();
        // The finding returned by the latest pick (null when it had none); the dialogue box shows it as one "Result:" line.
        public InterviewFinding LastFinding { get; private set; }
        // True when the patient has no authored interview (404 no_interview); such patients skip to surgery.
        public bool NoInterview { get; private set; }
        // Skip to surgery: no interview; State.phase "skipped", Score is the case's run context (kind "skipped").
        public bool Skipped => State?.phase == "skipped";
        public string SharedSessionId => sharedSessionId;
        public string SharedAttemptId => sharedAttemptId;
        // Interview turns sent to the patient agent (direction + clinician move), for diagnostics and validation.
        public int TurnsSent { get; private set; }
        public string LastDirection { get; private set; } = "";
        public string LastClinicianMove { get; private set; } = "";
        public event Action Changed;
        // One spoken or displayed dialogue line for the shared dialogue box. speaker: learner | patient | parent.
        public event Action<string, string> Line;
        string encounterId = "", patientId = "", greeting = "";
        PatientSummary casePatient;
        SurgicalCase authoredCase;
        InterviewRoundView round;
        EncounterScore pendingScore;
        int generation, blocking, responsesAtTurn;
        float awaitStarted, quietSince;
        bool heardSpeech;
        bool working;
        UnityWebRequest activeRequest;
        readonly Queue<Operation> pending = new Queue<Operation>();
        public const float PatientReplyTimeout = 25, PatientSilenceTimeout = 15, MinimumAnswerSeconds = .3f;
        sealed class Response { public long code; public string text = ""; public bool ok; public EncounterError error; }
        sealed class Operation
        {
            public string method, path, body;
            public int epoch;
            public bool blocking;
            public Action<Response> complete, failed;
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
            catch (Exception exception) { Debug.LogWarning("SCALPAL_OFFICE_CONFIG_INVALID " + exception.GetType().Name); SetStatus("Development encounter configuration is invalid."); }
        }
        void OnEnable()
        {
            if (realtime) { realtime.AttemptStarted += OfficeAttemptStarted; realtime.AttemptFailed += OfficeAttemptFailed; }
            if (voice) { voice.MicrophoneMuted = true; voice.Transcript += Transcript; voice.StatusChanged += VoiceStatus; voice.ModeChanged += VoiceMode; }
        }
        void OnDisable()
        {
            if (realtime) { realtime.AttemptStarted -= OfficeAttemptStarted; realtime.AttemptFailed -= OfficeAttemptFailed; }
            if (voice) { voice.Transcript -= Transcript; voice.StatusChanged -= VoiceStatus; voice.ModeChanged -= VoiceMode; }
            Invalidate(); State = null; Score = null; encounterId = ""; LastResponse = ""; LastSpeaker = ""; Status = "Choose a synthetic patient to begin.";
        }
        void Invalidate()
        {
            if (TalkHeld && voice) voice.CancelAnswerRecording();
            TalkHeld = false; waitingForAttempt = false;
            if (voice) voice.MicrophoneMuted = true;
            generation++; if (voice) voice.Disconnect();
            activeRequest?.Abort(); StopAllCoroutines(); activeRequest = null; pending.Clear(); working = false; blocking = 0;
            round = null; pendingScore = null; AwaitingPatient = false; AnswerNote = ""; Findings.Clear(); LastFinding = null;
        }
        public void LoadPatients()
        {
            if (Busy) return;
            SetStatus("Loading synthetic patients…");
            Enqueue("GET", "/patients", null, reply =>
            {
                var list = Parse<EncounterReply>(reply.text);
                if (list?.patients == null) { SetStatus("Patient list reply is incomplete. Reload the list."); return; }
                Patients = list.patients;
                SetStatus("Choose an available synthetic patient.");
            });
        }
        public void StartPatient(string id) => StartPatient(id, false);
        // skipToSurgery: from the explore card's "Skip to surgery": load the case and attempt, then go to theatre.
        public void StartPatient(string id, bool skipToSurgery)
        {
            if (!EncounterContract.ValidPatientId(id)) { SetStatus("Choose a canonical patient subject."); return; }
            Invalidate(); EncounterOfficeRoute.ClearSurgery(); patientId = id; encounterId = ""; AuthoredProcedureId = "";
            sharedSessionId = sharedAttemptId = ""; casePatient = null; authoredCase = null; NoInterview = false; TurnsSent = 0; LastDirection = LastClinicianMove = "";
            State = null; Score = null; LastResponse = ""; LastSpeaker = ""; Suspended = false;
            if (patient) patient.Select(null);
            SetStatus("Loading synthetic patient…");
            Enqueue("GET", "/patients/" + Uri.EscapeDataString(id) + "/case", null, reply =>
            {
                var authored = Parse<EncounterReply>(reply.text);
                if (authored == null || authored.patientId != id || authored.brief?.synthetic != true || string.IsNullOrEmpty(authored.procedureId)
                    || authored.status != "ready" && authored.status != "needs_review")
                { SetStatus("This patient is unavailable for a synthetic encounter."); return; }
                AuthoredProcedureId = authored.procedureId; casePatient = authored.patient; authoredCase = Parse<SurgicalCase>(reply.text);
                if (realtime) StartCoroutine(BeginOfficeAttempt(id, generation, skipToSurgery));
                else if (skipToSurgery) SkipToSurgery();
                else CreateInterview(id); // Isolated fixture/editor consumer has no shared bridge.
            });
        }
        IEnumerator BeginOfficeAttempt(string id, int epoch, bool skipToSurgery)
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
            if (skipToSurgery) SkipToSurgery();
            else CreateInterview(id);
        }
        void OfficeAttemptStarted(string id)
        {
            if (!waitingForAttempt || !realtime || !realtime.Paired) return;
            sharedSessionId = realtime.SessionId; sharedAttemptId = id; waitingForAttempt = false;
        }
        void OfficeAttemptFailed(string reason) { if (waitingForAttempt) { attemptRejected = true; SetStatus(reason); } }
        void CreateInterview(string id)
        {
            Enqueue("POST", "/interviews", JsonUtility.ToJson(new PatientRequest { patientId = id }), response =>
            {
                var reply = Parse<InterviewReply>(response.text);
                if (reply == null || reply.patientId != id || !QuestJarvisVoice.ValidInterviewId(reply.interviewId) || reply.phase != "interview"
                    || reply.speaker != "patient" && reply.speaker != "parent" || !ValidRound(reply.round))
                { SetStatus("Interview identity mismatch. Choose a patient again."); return; }
                encounterId = reply.interviewId; greeting = reply.openingLine ?? "";
                State = new EncounterState
                {
                    encounterId = reply.interviewId, phase = reply.phase, patientId = reply.patientId, patientName = reply.patientName,
                    speakerName = reply.speakerName, speaker = reply.speaker, patientAge = reply.patientAge, patientSex = reply.patientSex,
                    speakerAge = reply.speakerAge, speakerSex = reply.speakerSex
                };
                // An older service omits demographics: the case still knows the patient's age and sex.
                if (State.patientAge <= 0 && casePatient != null) State.patientAge = casePatient.age;
                if (string.IsNullOrEmpty(State.patientSex) && casePatient != null) State.patientSex = casePatient.sex;
                if (State.speaker == "patient") { if (State.speakerAge <= 0) State.speakerAge = State.patientAge; if (string.IsNullOrEmpty(State.speakerSex)) State.speakerSex = State.patientSex; }
                round = reply.round;
                if (patient) { patient.Select(State); patient.SetState("listening"); if (voice) patient.BindVoice(voice, true); }
                Say(PatientSpeaker(), greeting);
                SetStatus("Listen to " + FirstName() + ", then choose your next move.");
                // The first round appears once the opening line has been spoken.
                AwaitPatient();
                if (VoiceEnabled) ConnectVoice();
                else PlayAuthoredSpeech(greeting);
                ReleaseIfDone(); // with no voice the round shows at once
            }, response =>
            {
                if (response.code == 404 && response.error?.code == "no_interview")
                {
                    // No authored interview: the same path as Skip to surgery.
                    NoInterview = true;
                    SkipToSurgery();
                }
                else SetStatus(Message(response));
            });
        }
        static bool ValidRound(InterviewRoundView view) => view != null && view.number >= 1 && view.of >= view.number && !string.IsNullOrEmpty(view.prompt)
            && view.choices != null && view.choices.Length == 4 && Array.TrueForAll(view.choices, c => c != null && !string.IsNullOrEmpty(c.text) && c.key != null && c.key.Length == 1 && c.key[0] >= 'A' && c.key[0] <= 'D');
        // Snapshot the scored interview's original shared attempt before changing scenes.
        // The handoff flow owns presentation choice, transition and Time-Out.
        public bool TryPrepareHandoff(out EncounterSurgeryHandoff handoff, out string reason)
        {
            handoff = null;
            if (Busy || !SurgeryReady)
            { reason = "Finish the interview before entering the OR."; return false; }
            if (realtime && (!realtime.Paired || realtime.SessionId != sharedSessionId || realtime.AttemptId != sharedAttemptId
                || !realtime.TryGetEncounterBinding(encounterId, out var boundSession, out var boundAttempt, out var boundPatient, out var boundPhase)
                || boundSession != sharedSessionId || boundAttempt != sharedAttemptId || boundPatient != patientId || boundPhase != "scored"))
            { reason = "The scored interview must belong to the current paired attempt before entering the OR."; return false; }
            if (!EncounterOfficeRoute.PrepareSurgery(State, Score, AuthoredProcedureId, baseUrl, out reason, sharedSessionId, sharedAttemptId))
                return false;
            if (!EncounterOfficeRoute.TakeSurgery(out handoff))
            { reason = "The scored interview handoff could not be prepared. Refresh the scorecard."; return false; }
            reason = "";
            return true;
        }
        public void ContinueToSurgery() => Scalpal.Handoff.HandoffFlow.OpenFromOffice(this);
        // Leave the interview (or never start it) and go to the Theatre card for the surgery this case needs.
        public bool SkipToSurgery()
        {
            if (authoredCase == null || authoredCase.patientId != patientId || string.IsNullOrEmpty(AuthoredProcedureId) || waitingForAttempt)
            { SetStatus("Choose a patient before skipping to surgery."); return false; }
            if (realtime && (string.IsNullOrEmpty(sharedSessionId) || string.IsNullOrEmpty(sharedAttemptId)))
            { SetStatus("Pair the headset session, then skip to surgery again."); return false; }
            Invalidate(); // stops the interview, its voice and any pending answer
            encounterId = "";
            State = new EncounterState { encounterId = "", phase = "skipped", patientId = patientId, patientName = authoredCase.patient?.name ?? "", speaker = "patient",
                patientAge = casePatient?.age ?? 0, patientSex = casePatient?.sex ?? "", assessment = new EncounterAssessment() };
            Score = EncounterContract.SkippedRunContext(authoredCase);
            if (patient) patient.SetState("resting");
            SetStatus((NoInterview ? "No office interview for this patient yet. " : "Interview skipped. ") + "Choose how to go to theatre for " + Score.procedureTitle + ".");
            Scalpal.Handoff.HandoffFlow.OpenSkipped(this);
            return true;
        }
        // Tap (controller ray trigger or hand pinch) on choice A to D.
        public void Choose(string key)
        {
            if (!RoundVisible || Busy || key == null || key.Length != 1 || key[0] < 'A' || key[0] > 'D') return;
            Answer(JsonUtility.ToJson(new InterviewAnswerKey { key = key }));
        }
        void Answer(string body)
        {
            AnswerNote = ""; SetStatus("Sending your answer…");
            int answered = round?.number ?? 0;
            Enqueue("POST", EncounterContract.OfficePath(encounterId) + "/answer", body, response => AdoptPick(Parse<InterviewReply>(response.text), answered), response =>
            {
                var reply = Parse<InterviewReply>(response.text);
                if (response.code == 422 && response.error?.code == "unclear_answer")
                {
                    AnswerNote = "Say A, B, C or D, or tap one." + (string.IsNullOrWhiteSpace(reply?.heard) ? "" : " Heard: \"" + reply.heard.Trim() + "\"");
                    SetStatus("That answer was unclear.");
                }
                else { AnswerNote = Message(response); SetStatus(AnswerNote); }
            });
        }
        void AdoptPick(InterviewReply reply, int answered)
        {
            // JsonUtility turns a JSON null object into a default instance: an empty round or scorecard means none.
            if (reply != null && reply.next != null && reply.next.number == 0) reply.next = null;
            if (reply != null && reply.scorecard != null && string.IsNullOrEmpty(reply.scorecard.kind)) reply.scorecard = null;
            if (reply?.choice == null || reply.patient == null || reply.state == null || reply.state.interviewId != encounterId || reply.state.patientId != patientId
                || round == null || round.number != answered || reply.next != null && !ValidRound(reply.next) || reply.next == null && reply.scorecard == null)
            { SetStatus("Interview answer reply is incomplete. Refresh state."); return; }
            if (reply.scorecard != null && !ValidScore(reply.scorecard)) { SetStatus("Interview scorecard does not match this patient. Refresh state."); return; }
            Say("learner", reply.choice.key + ") " + reply.choice.text);
            LastFinding = reply.finding != null && !string.IsNullOrEmpty(reply.finding.text) ? reply.finding : null;
            if (LastFinding != null) Findings.Add(LastFinding);
            round = reply.next;
            pendingScore = reply.scorecard; // shown once the patient's closing line has been spoken
            // The patient replies to the pick: a silent direction, then the move as the user turn.
            string direction = reply.patient.direction ?? "";
            if (!string.IsNullOrWhiteSpace(reply.patient.closing)) direction = direction + " Then close with, in your own words: \"" + reply.patient.closing.Trim() + "\"";
            if (voice && voice.Connected)
            {
                responsesAtTurn = voice.AgentResponses;
                if (voice.SendInterviewTurn(direction, reply.patient.clinicianMove))
                { TurnsSent++; LastDirection = direction.Trim(); LastClinicianMove = reply.patient.clinicianMove.Trim(); }
            }
            SetStatus(round != null ? "Listen to " + FirstName() + "'s reply." : "Interview complete.");
            AwaitPatient();
            ReleaseIfDone();
        }
        bool ValidScore(EncounterScore card) => card.kind == "interview" && card.max == 100 && card.total >= 0 && card.total <= 100 && !string.IsNullOrEmpty(card.grade)
            && card.patientId == patientId && card.procedureId == AuthoredProcedureId && card.carryoverItems != null && card.rounds != null;
        void AwaitPatient()
        {
            AwaitingPatient = true; heardSpeech = false;
            awaitStarted = quietSince = Time.realtimeSinceStartup;
            if (voice && !(voice.Connected || voice.Status == "connecting")) responsesAtTurn = voice.AgentResponses;
            Notify();
        }
        void Update() => ReleaseIfDone();
        void ReleaseIfDone() { if (AwaitingPatient && PatientDone(Time.realtimeSinceStartup)) ReleaseRound(); }
        // The next round waits until the patient's reply has finished: the agent's turn is complete and its audio has
        // drained. With no live voice nothing more is coming; a silent provider cannot strand the learner.
        bool PatientDone(float now)
        {
            if (!voice) return true;
            bool sounding = voice.Mode == "speaking" || voice.PlaybackLevel > 0;
            if (sounding) { heardSpeech = true; quietSince = now; return false; }
            if (!voice.Connected && voice.Status != "connecting") return !voice.PlaybackActive;
            float waited = now - awaitStarted;
            if (voice.Status == "connecting") return waited > PatientReplyTimeout;
            bool replied = voice.AgentResponses > responsesAtTurn;
            if (heardSpeech && replied) return now - quietSince > .6f;
            return waited > (heardSpeech ? PatientReplyTimeout : PatientSilenceTimeout);
        }
        void ReleaseRound()
        {
            AwaitingPatient = false;
            if (pendingScore != null)
            {
                Score = pendingScore; pendingScore = null; State.phase = "scored";
                // The plan and diagnosis picks stand in for the learner's committed proposal in the OR handoff.
                State.assessment = new EncounterAssessment { diagnosis = EncounterContract.Picked(Score, "diagnosis"), procedure = EncounterContract.Picked(Score, "plan") };
                if (patient) patient.SetState("resting");
                SetStatus("Interview scored " + Score.total + "/100. Review your scorecard, then go to theatre for " + Score.procedureTitle + ".");
                return;
            }
            if (round != null) SetStatus("Round " + round.number + " of " + round.of + ": point and pull trigger, or hold B and say it.");
            Notify();
        }
        public void RefreshState()
        {
            if (string.IsNullOrEmpty(encounterId) || Busy) return;
            Enqueue("GET", EncounterContract.OfficePath(encounterId), null, response =>
            {
                var reply = Parse<InterviewReply>(response.text);
                if (reply == null || reply.interviewId != encounterId || reply.patientId != patientId) { SetStatus("Stale or mismatched interview response."); return; }
                if (reply.phase == "scored" && Score == null)
                    Enqueue("GET", EncounterContract.OfficePath(encounterId) + "/score", null, scored =>
                    {
                        var card = Parse<InterviewReply>(scored.text)?.scorecard;
                        if (card == null || !ValidScore(card)) { SetStatus("Interview scorecard is incomplete. Refresh state."); return; }
                        round = null; pendingScore = card; ReleaseRound();
                    });
                else if (reply.phase == "interview" && ValidRound(reply.round)) { round = reply.round; SetStatus("Interview reconciled with the service."); }
                else SetStatus("Interview reconciled with the service.");
            });
        }
        public void StartVoice()
        {
            if (Busy) return;
            ConnectVoice();
        }
        // Also used from within a completing request (interview start), where Busy is still true.
        void ConnectVoice()
        {
            if (!focused || paused || !voice || State == null || State.phase != "interview") return;
            Suspended = false;
            try
            {
                voice.MicrophoneMuted = true; // the patient hears only the picks, for the whole interview
                voice.ConfigureEndpoint(baseUrl);
                // The patient speaks from the seated speaker's mouth in 3D.
                if (patient) patient.BindVoice(voice, true);
                voice.ConnectInterview(encounterId, patientId);
                responsesAtTurn = voice.AgentResponses;
            }
            catch (ArgumentException) { SetStatus("Set a valid HTTP(S) encounter service URL."); }
        }
        // Hold-to-answer: record locally while held; on release send the WAV to the interview for matching.
        public void SetTalkHeld(bool held)
        {
            if (held == TalkHeld) return;
            if (held)
            {
                if (!focused || paused || !voice || !RoundVisible || Busy) return;
                if (!voice.BeginAnswerRecording()) { AnswerNote = voice.LastError; Notify(); return; }
                voice.MicrophoneMuted = true;
                TalkHeld = true; AnswerNote = "Listening… release to send your answer.";
                SetStatus("Say A, B, C or D, or the move itself.");
                return;
            }
            TalkHeld = false;
            if (!voice) { Notify(); return; }
            var samples = voice.EndAnswerRecording(out int rate);
            SubmitRecordedAnswer(samples, rate);
        }
        void SubmitRecordedAnswer(float[] samples, int rate)
        {
            if (!RoundVisible) { AnswerNote = ""; Notify(); return; }
            if (samples == null || rate <= 0 || samples.Length < rate * MinimumAnswerSeconds)
            { AnswerNote = "Too short. Hold the button while you say A, B, C or D."; Notify(); return; }
            AnswerNote = "Listening back…";
            Answer(JsonUtility.ToJson(new InterviewAnswerAudio { audio = Convert.ToBase64String(QuestJarvisVoice.EncodeWav(samples, rate)), mimeType = "audio/wav" }));
        }
        public void StopVoice()
        {
            VoiceEnabled = false;
            if (TalkHeld && voice) voice.CancelAnswerRecording();
            TalkHeld = false;
            if (voice) { voice.MicrophoneMuted = true; voice.Disconnect(); }
            if (patient) patient.SetState(State?.phase == "interview" ? "listening" : "resting");
            SetStatus("Voice off. Read and tap through the interview; select Voice on to hear the patient.");
        }
        // Explicit Voice on/off toggle. Off disconnects and stops auto-connect; on (or a retry after a failure or
        // focus loss) connects the patient voice.
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
                if (voice) voice.MicrophoneMuted = true;
                string reason = voice && !string.IsNullOrEmpty(voice.LastError) ? voice.LastError : "unknown error";
                if (!Suspended && round != null && round.number == 1 && AwaitingPatient) PlayAuthoredSpeech(greeting);
                SetStatus("Patient voice unavailable: " + reason + " Read and tap through the interview, or select Voice on to retry.");
            }
            else if (value == "connected") SetStatus("Listen to " + FirstName() + ".");
            else if (value == "connecting") SetStatus("Connecting " + EncounterContract.SpeakerLabel(State, "patient") + " voice…");
            else if (value != "offline" && value != "disconnected") SetStatus("Patient voice: " + value);
            Notify();
        }
        void VoiceMode(string mode) { if (patient && State?.phase == "interview") patient.SetState(mode == "speaking" ? "speaking" : "listening"); Notify(); }
        void Transcript(string source, string text)
        {
            // Picks are shown when made; the agent's echo of a [CLINICIAN] turn is not learner speech.
            if (source == "user" || string.IsNullOrWhiteSpace(text) || State == null) return;
            Say(PatientSpeaker(), text);
            Enqueue("POST", EncounterContract.OfficePath(encounterId) + "/transcript", JsonUtility.ToJson(new TranscriptRequest { speaker = "patient", text = text }),
                null, response => Debug.LogWarning("SCALPAL_OFFICE_TRANSCRIPT_MIRROR_FAILED status=" + response.code), false);
            Notify();
        }
        void PlayAuthoredSpeech(string display)
        {
            if (!focused || paused || Suspended || !voice || voice.Connected || voice.Status == "connecting") return;
            voice.InterruptPlayback();
            var clip = EncounterPatientSpeech.Find(patientId, "greeting", "", display);
            if (clip) voice.PlayLocalSpeech(clip);
        }
        void Enqueue(string method, string path, string body, Action<Response> complete, Action<Response> failed = null, bool blocks = true)
        {
            if (pending.Count >= 64) { SetStatus("Too many pending requests. Wait and refresh state."); return; }
            pending.Enqueue(new Operation { method = method, path = path, body = body, epoch = generation, blocking = blocks, complete = complete, failed = failed });
            if (blocks) blocking++;
            if (!working) { working = true; if (Application.isPlaying) StartCoroutine(Drain()); }
            Notify();
        }
        IEnumerator Drain()
        {
            while (pending.Count > 0)
            {
                var op = pending.Dequeue();
                if (op.epoch != generation) continue;
                var response = new Response();
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    response.error = new EncounterError { code = "invalid_endpoint", message = "Set a valid HTTP(S) service URL." };
                else using (var request = new UnityWebRequest(baseUrl.TrimEnd('/') + op.path, op.method))
                {
                    activeRequest = request; request.downloadHandler = new DownloadHandlerBuffer();
                    // Spoken answers are transcribed and classified server side.
                    request.timeout = op.path.EndsWith("/answer", StringComparison.Ordinal) ? Mathf.Max(timeoutSeconds, 25) : op.path == "/interviews" ? timeoutSeconds : Mathf.Min(timeoutSeconds, 4);
                    if (op.body != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(op.body)); request.SetRequestHeader("Content-Type", "application/json"); }
                    yield return request.SendWebRequest(); activeRequest = null;
                    if (op.epoch != generation) yield break;
                    response.code = request.responseCode; response.text = request.downloadHandler.text ?? "";
                    response.ok = request.result == UnityWebRequest.Result.Success;
                    var parsed = Parse<EncounterReply>(response.text);
                    if (EncounterContract.HasError(parsed)) { response.ok = false; response.error = parsed.error; }
                    else if (!response.ok) response.error = new EncounterError { code = "service_unavailable", message = "Service unavailable. Refresh state before retrying; an action may have reached the service." };
                }
                if (op.epoch != generation) yield break;
                if (op.blocking) blocking = Mathf.Max(0, blocking - 1);
                if (response.ok) op.complete?.Invoke(response);
                else if (op.failed != null) op.failed(response);
                else SetStatus(Message(response));
                Notify();
            }
            working = false; Notify();
        }
        static string Message(Response response) => !string.IsNullOrEmpty(response.error?.message) ? response.error.message : "Service unavailable. Refresh state before retrying.";
        static T Parse<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonUtility.FromJson<T>(json); }
            catch (ArgumentException) { return null; }
        }
        void OnApplicationFocus(bool focus) { focused = focus; if (!focus) SuspendVoice(); }
        void OnApplicationPause(bool pause) { paused = pause; if (pause) SuspendVoice(); }
        void SuspendVoice()
        {
            Suspended = true;
            if (TalkHeld && voice) voice.CancelAnswerRecording();
            TalkHeld = false;
            if (voice) voice.MicrophoneMuted = true;
            // The transport handles the OS microphone permission prompt separately. Never reconnect on focus return.
            if (patient) patient.SetState("resting");
            SetStatus("Voice paused. Select Voice on to hear the patient again; choices still work.");
        }
        void SetStatus(string text) { Status = text; Notify(); }
        string PatientSpeaker() => State?.speaker == "parent" ? "parent" : "patient";
        string FirstName()
        {
            string name = State == null ? "" : State.speaker == "parent" ? State.speakerName : State.patientName;
            if (string.IsNullOrWhiteSpace(name)) return "the patient";
            name = name.Trim(); int space = name.IndexOf(' ');
            return space > 0 ? name.Substring(0, space) : name;
        }
        // One path for every spoken line: the panel's labeled last line and the dialogue box (role keys:
        // patient, parent, learner).
        void Say(string role, string text, bool show = true)
        {
            LastSpeaker = role == "learner" ? EncounterContract.LearnerLabel : EncounterContract.SpeakerLabel(State, "patient");
            LastResponse = text ?? "";
            if (show && !string.IsNullOrWhiteSpace(text)) Line?.Invoke(role, text);
        }
        void Notify() => Changed?.Invoke();
    }
}
