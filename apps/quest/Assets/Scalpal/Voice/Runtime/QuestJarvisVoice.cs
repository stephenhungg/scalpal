using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Voice
{
    // Native transport for Matthew's existing agent, not another coach or progression engine.
    // Protocol: https://elevenlabs.io/docs/eleven-agents/api-reference/eleven-agents/websocket
    // Configure/Connect must be called on the Unity main thread, from a voice UI action or (office) the start of an encounter role.
    public sealed class QuestJarvisVoice : MonoBehaviour
    {
        public sealed class ToolRequest
        {
            public string ToolCallId, ToolName, ParametersJson;
            public int ConnectionGeneration;
        }

        [SerializeField] string baseUrl = "http://localhost:8787";
        [SerializeField] int timeoutSeconds = 10;
        [SerializeField] AudioSource speaker;
        bool microphoneMuted;
        public bool MicrophoneMuted
        {
            get => microphoneMuted;
            set
            {
                if (microphoneMuted == value) return;
                microphoneMuted = value;
                // Starting a talk hold must discard samples captured before the press.
                if (!value && microphoneClip && microphoneReady)
                { microphoneCursor = Microphone.GetPosition(microphoneDevice); lastCapture = Time.realtimeSinceStartup; }
            }
        }
        // The output source. The office moves it to the speaking patient's mouth; a live stream follows the move.
        public AudioSource Speaker
        {
            get => speaker;
            set
            {
                if (speaker == value) return;
                if (speaker) { speaker.Stop(); speaker.clip = null; }
                speaker = value;
                if (speaker && playbackClip) { speaker.playOnAwake = false; speaker.loop = true; speaker.clip = playbackClip; speaker.Play(); }
            }
        }
        public string Status { get; private set; } = "disconnected";
        public string Mode { get; private set; } = "listening";
        public string LastError { get; private set; } = "";
        public string CoachSessionId { get; private set; } = "";
        public bool Connected => Status == "connected";
        public bool PlaybackActive => Connected || localSpeech;
        // The level of PCM consumed by the audio callback, not text arrival or provider mode.
        public float PlaybackLevel
        {
            get
            {
                long stamp = Interlocked.Read(ref playbackTimestamp);
                if (!PlaybackActive || stamp == 0 ||
                    (System.Diagnostics.Stopwatch.GetTimestamp() - stamp) / (double)System.Diagnostics.Stopwatch.Frequency > .15) return 0;
                return playbackLevel;
            }
        }
        public event Action<string> StatusChanged;
        public event Action<string> ModeChanged;
        public event Action<string, string> Transcript; // source: user or agent
        public event Action<ToolRequest> ClientToolRequested;
        // Completed agent turns (agent_response events) on the current connection; the office interview waits
        // for one after each pick before it shows the next round.
        public int AgentResponses { get; private set; }
        public bool AnswerRecording => answerSamples != null;
        public const float MaxAnswerSeconds = 19.5f; // the service accepts spoken answers under about 20 s

        Connection active;
        int generation;
        AudioClip microphoneClip, playbackClip;
        string microphoneDevice, patientId = "", prompt = "", firstMessage = "", initialContext = "";
        string lastContext = "";
        bool encounterMode, interviewMode;
        // Hold-to-answer: learner speech recorded locally for POST /interviews/:id/answer, never streamed to the agent.
        List<float> answerSamples;
        AudioClip answerClip;
        string answerDevice;
        int answerRate, answerCursor;
        string encounterRole = "patient", encounterVoiceId = "";
        int microphoneCursor, microphoneRate, microphoneChunk;
        float microphoneStarted, lastCapture, connectionStarted;
        bool microphoneReady;
        readonly object audioLock = new object();
        readonly Queue<float> outputSamples = new Queue<float>();
        int outputRate;
        bool localSpeech;
        volatile float playbackLevel;
        long playbackTimestamp;
        int lastAudioEventId = -1, interruptedThrough = -1;
        // A local barge-in suppresses agent audio until a turn boundary. Boundaries: an identified
        // provider interruption, a non-empty learner transcript, a new agent_response, or the talk
        // control released (microphone muted) with no suppressed audio for InterruptionGraceSeconds.
        // Without the last two, silence or empty ASR after a press left the agent mute until reconnect.
        public const float InterruptionGraceSeconds = 2.5f;
        bool awaitingInterruption;
        float lastSuppressedAudio;
        bool permissionPending, applicationFocused = true, applicationPaused;
        readonly HashSet<string> pendingTools = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> seenTools = new HashSet<string>(StringComparer.Ordinal);

        sealed class Connection : IDisposable
        {
            readonly object lifetime = new object();
            bool disposed;
            public readonly int Generation;
            public readonly ClientWebSocket Socket = new ClientWebSocket();
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();
            public readonly ConcurrentQueue<string> Outgoing = new ConcurrentQueue<string>();
            public readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0);
            public volatile string Error;
            public volatile bool Open;
            public Connection(int generation) { Generation = generation; }
            public void RequestStop()
            {
                lock (lifetime)
                {
                    if (disposed) return;
                    Open = false;
                    Cancel.Cancel();
                    try { Socket.Abort(); } catch (ObjectDisposedException) { }
                }
            }
            public void Enqueue(string json)
            {
                lock (lifetime)
                {
                    if (disposed || !Open) return;
                    Outgoing.Enqueue(json); SendSignal.Release();
                }
            }
            public void Dispose()
            {
                lock (lifetime)
                {
                    if (disposed) return;
                    disposed = true; Open = false;
                    Socket.Dispose(); SendSignal.Dispose(); Cancel.Dispose();
                }
            }
        }

        public void ConfigureEndpoint(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("Voice service needs an absolute HTTP(S) URL.", nameof(url));
            if (baseUrl == url.TrimEnd('/')) return;
            Disconnect();
            baseUrl = url.TrimEnd('/');
        }

        // Pass the systemPrompt/firstMessage/context returned by POST /coach/sessions.
        public void ConfigureConversation(string systemPrompt, string greeting, string context = "")
        {
            prompt = systemPrompt ?? "";
            firstMessage = greeting ?? "";
            initialContext = context ?? "";
        }

        // Reuse the native transport for the service's separate patient/attending roles.
        public void ConfigureEncounterConversation(string systemPrompt, string greeting, string voiceId, string role)
        {
            if (role != "patient" && role != "attending") throw new ArgumentException("Unknown encounter voice role.", nameof(role));
            ConfigureConversation(systemPrompt, greeting);
            encounterRole = role;
            encounterVoiceId = voiceId ?? "";
        }

        public void ConnectEncounter(string encounterId, string selectedPatientId)
        {
            Disconnect();
            encounterMode = true; interviewMode = false;
            if (!applicationFocused || applicationPaused) { Fail("Return to the app and start voice explicitly."); return; }
            if (!isActiveAndEnabled || !ValidEncounterId(encounterId) || string.IsNullOrEmpty(selectedPatientId))
            { Fail("A valid encounter and patient are required."); return; }
            CoachSessionId = encounterId;
            patientId = selectedPatientId;
            LastError = "";
            SetStatus("connecting");
            StartCoroutine(Begin(generation));
        }

        // The choice-based office interview (/interviews): the patient agent's prompt, first line and voice come
        // from GET /interviews/:id/connection. The microphone stays muted; the patient hears only the picks.
        public void ConnectInterview(string interviewId, string selectedPatientId)
        {
            Disconnect();
            encounterMode = true; interviewMode = true; encounterRole = "patient";
            MicrophoneMuted = true; // the patient hears only the picks, for the whole interview
            if (!applicationFocused || applicationPaused) { Fail("Return to the app and start voice explicitly."); return; }
            if (!isActiveAndEnabled || !ValidInterviewId(interviewId) || string.IsNullOrEmpty(selectedPatientId))
            { Fail("A valid interview and patient are required."); return; }
            CoachSessionId = interviewId;
            patientId = selectedPatientId;
            LastError = "";
            SetStatus("connecting");
            StartCoroutine(Begin(generation));
        }

        public void Connect(string coachSessionId)
        {
            Disconnect();
            encounterMode = false; interviewMode = false;
            if (!applicationFocused || applicationPaused) { Fail("Return to the app and start voice explicitly."); return; }
            if (!isActiveAndEnabled || !ValidSessionId(coachSessionId)) { Fail("A valid coach session is required."); return; }
            CoachSessionId = coachSessionId;
            LastError = "";
            SetStatus("connecting");
            StartCoroutine(Begin(generation));
        }

        IEnumerator Begin(int epoch)
        {
            // No permission prompt, recording or network connection happens on Awake/OnEnable.
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
            {
                bool answered = false, granted = false;
                var callbacks = new UnityEngine.Android.PermissionCallbacks();
                callbacks.PermissionGranted += _ => { answered = true; granted = true; };
                callbacks.PermissionDenied += _ => answered = true;
                permissionPending = true;
                try
                {
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
                    while (!answered && epoch == generation) yield return null;
                }
                finally { if (epoch == generation) permissionPending = false; }
                if (epoch != generation) yield break;
                if (!granted) { Fail("Microphone permission was denied."); yield break; }
            }
#else
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                permissionPending = true;
                try { yield return Application.RequestUserAuthorization(UserAuthorization.Microphone); }
                finally { if (epoch == generation) permissionPending = false; }
                if (epoch != generation) yield break;
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { Fail("Microphone permission was denied."); yield break; }
            }
#endif
            while (epoch == generation && (!applicationFocused || applicationPaused)) yield return null;
            if (epoch != generation) yield break;
            yield return BeginConversation(epoch);
        }

        // Kept separate so deterministic HTTP tests can exercise identity/roles without a mic or socket.
        IEnumerator BeginConversation(int epoch)
        {
            if (interviewMode) { yield return BeginInterview(epoch); yield break; }
            Reply state = null;
            yield return Http("GET", SessionPath, null, r => state = r);
            if (epoch != generation) yield break;
            if (state == null || !state.ok || (encounterMode
                ? state.state == null || state.state.encounterId != CoachSessionId || state.state.patientId != patientId ||
                    (encounterRole == "patient" ? state.state.phase != "interview" : state.state.phase != "attending")
                : state.snapshot == null || state.snapshot.sessionId != CoachSessionId))
            { Fail("Cannot load the selected conversation state and role."); yield break; }
            if (!encounterMode) patientId = state.snapshot.patientId ?? "";
            if (!string.IsNullOrEmpty(state.context)) initialContext = state.context;
            Reply connection = null;
            // An encounter connection names its encounter: the service picks the patient agent during the interview
            // and Jarvis as attending afterwards, and the returned role must match the role this client expects.
            yield return Http("GET", encounterMode ? "/jarvis/connection?encounterId=" + Uri.EscapeDataString(CoachSessionId) : "/jarvis/connection", null, r => connection = r);
            if (epoch != generation) yield break;
            if (connection == null || !connection.ok)
            {
                string code = connection?.error?.code ?? "service_unreachable";
                Fail("Jarvis connection unavailable (" + code + "); check the existing voice service configuration.");
                yield break;
            }
            if (encounterMode && connection.role != encounterRole)
            { Fail("Voice service bound the " + (string.IsNullOrEmpty(connection.role) ? "wrong" : connection.role) + " agent; expected " + encounterRole + "."); yield break; }
            OpenSocket(connection, BuildInitiation(encounterMode ? encounterRole : state.snapshot.mode ?? ""), epoch);
        }

        IEnumerator BeginInterview(int epoch)
        {
            Reply state = null;
            string path = "/interviews/" + Uri.EscapeDataString(CoachSessionId);
            yield return Http("GET", path, null, r => state = r);
            if (epoch != generation) yield break;
            if (state == null || !state.ok || state.interviewId != CoachSessionId || state.patientId != patientId || state.phase != "interview")
            { Fail("Cannot load the selected interview."); yield break; }
            Reply connection = null;
            yield return Http("GET", path + "/connection", null, r => connection = r);
            if (epoch != generation) yield break;
            if (connection == null || !connection.ok)
            { Fail("Patient voice unavailable (" + (connection?.error?.code ?? "service_unreachable") + "); tap through the interview silently."); yield break; }
            if (connection.role != "patient") { Fail("Voice service bound the " + (string.IsNullOrEmpty(connection.role) ? "wrong" : connection.role) + " agent; expected patient."); yield break; }
            // The server builds the patient's prompt, opening line and voice; the client never authors them.
            prompt = connection.prompt ?? ""; firstMessage = connection.firstMessage ?? ""; encounterVoiceId = connection.voiceId ?? "";
            OpenSocket(connection, BuildInitiation("patient"), epoch);
        }

        void OpenSocket(Reply connection, string initJson, int epoch)
        {
            string url = connection.signedUrl;
            if (string.IsNullOrEmpty(url) && connection.mode == "public" && !string.IsNullOrEmpty(connection.agentId))
                url = "wss://api.elevenlabs.io/v1/convai/conversation?agent_id=" + Uri.EscapeDataString(connection.agentId);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var ws) || ws.Scheme != "wss" || ws.Host != "api.elevenlabs.io")
            { Fail("Voice service did not return a supported ElevenLabs WebSocket connection."); return; }
            active = new Connection(epoch);
            connectionStarted = Time.realtimeSinceStartup;
            _ = RunSocket(active, ws, initJson);
        }

        string BuildInitiation(string mode)
        {
            var variables = new DynamicVariables { coach_session_id = encounterMode ? "" : CoachSessionId,
                encounter_id = encounterMode ? CoachSessionId : "", session_id = CoachSessionId,
                patient_id = patientId, mode = mode, context = initialContext };
            // JsonUtility invents default objects for null Serializable fields. Schemas must omit optional keys themselves.
            bool hasPrompt = !string.IsNullOrWhiteSpace(prompt), hasGreeting = !string.IsNullOrWhiteSpace(firstMessage);
            var tts = encounterMode && !string.IsNullOrWhiteSpace(encounterVoiceId) ? new TtsOverride { voice_id = encounterVoiceId } : null;
            if (!hasPrompt && !hasGreeting)
                return tts == null ? JsonUtility.ToJson(new BasicInitiation { dynamic_variables = variables }) :
                    JsonUtility.ToJson(new TtsInitiation { dynamic_variables = variables, conversation_config_override = new TtsOverrides { tts = tts } });
            if (!hasGreeting)
            {
                var onlyPrompt = new PromptAgent { prompt = new Prompt { prompt = prompt } };
                return tts == null ? JsonUtility.ToJson(new PromptInitiation { dynamic_variables = variables, conversation_config_override = new PromptOverrides { agent = onlyPrompt } }) :
                    JsonUtility.ToJson(new VoicedPromptInitiation { dynamic_variables = variables, conversation_config_override = new VoicedPromptOverrides { agent = onlyPrompt, tts = tts } });
            }
            if (!hasPrompt)
            {
                var onlyGreeting = new GreetingAgent { first_message = firstMessage };
                return tts == null ? JsonUtility.ToJson(new GreetingInitiation { dynamic_variables = variables, conversation_config_override = new GreetingOverrides { agent = onlyGreeting } }) :
                    JsonUtility.ToJson(new VoicedGreetingInitiation { dynamic_variables = variables, conversation_config_override = new VoicedGreetingOverrides { agent = onlyGreeting, tts = tts } });
            }
            var agent = new AgentOverride { prompt = new Prompt { prompt = prompt }, first_message = firstMessage };
            if (tts != null)
                return JsonUtility.ToJson(new VoicedInitiation { dynamic_variables = variables,
                    conversation_config_override = new VoicedOverrides { agent = agent, tts = tts } });
            return JsonUtility.ToJson(new Initiation { dynamic_variables = variables,
                conversation_config_override = new Overrides { agent = agent } });
        }

        static async Task RunSocket(Connection connection, Uri uri, string initialization)
        {
            Task send = null;
            try
            {
                await connection.Socket.ConnectAsync(uri, connection.Cancel.Token).ConfigureAwait(false);
                await Send(connection, initialization).ConfigureAwait(false);
                connection.Open = true;
                send = SendLoop(connection);
                var bytes = new byte[16384];
                while (!connection.Cancel.IsCancellationRequested)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(bytes), connection.Cancel.Token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) { connection.Error = "Jarvis ended the voice connection."; return; }
                            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException();
                            message.Write(bytes, 0, result.Count);
                            if (message.Length > 2 * 1024 * 1024) throw new InvalidDataException();
                        } while (!result.EndOfMessage);
                        if (connection.Incoming.Count >= 256) throw new InvalidDataException();
                        connection.Incoming.Enqueue(Encoding.UTF8.GetString(message.ToArray()));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                // Exception messages may include signed URLs. Never log them or the raw protocol.
                connection.Error = "Voice WebSocket failed (" + exception.GetType().Name + ").";
            }
            finally
            {
                connection.Open = false;
                connection.RequestStop();
                if (send != null) { try { await send.ConfigureAwait(false); } catch (Exception) { } }
                connection.Dispose(); // Send/receive have finished before their token and signal are disposed.
            }
        }

        static Task Send(Connection connection, string json) => connection.Socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, connection.Cancel.Token);

        static async Task SendLoop(Connection connection)
        {
            try
            {
                while (!connection.Cancel.IsCancellationRequested)
                {
                    await connection.SendSignal.WaitAsync(connection.Cancel.Token).ConfigureAwait(false);
                    if (connection.Outgoing.TryDequeue(out var json)) await Send(connection, json).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                connection.Error = "Voice send failed (" + exception.GetType().Name + ").";
                connection.RequestStop();
            }
        }

        void Update()
        {
            if (answerClip) PollAnswerMicrophone();
            if (localSpeech)
            {
                int remaining;
                lock (audioLock) remaining = outputSamples.Count;
                if (remaining == 0 && PlaybackLevel == 0) StopLocalSpeech();
            }
            var connection = active;
            if (connection == null) return;
            if (!string.IsNullOrEmpty(connection.Error)) { Fail(connection.Error); return; }
            if (Status == "connecting" && Time.realtimeSinceStartup - connectionStarted > 20)
            { Fail("Timed out waiting for Jarvis audio negotiation."); return; }
            for (int i = 0; i < 32 && connection.Incoming.TryDequeue(out var json); i++)
            {
                try { Handle(json); }
                catch (Exception exception) { Fail("Voice protocol failed (" + exception.GetType().Name + ")."); return; }
                if (active != connection) return;
            }
            ReleaseSilentInterruption(Time.realtimeSinceStartup);
            if (Connected) CaptureMicrophone();
            int buffered;
            lock (audioLock) buffered = outputSamples.Count;
            if (Mode == "speaking" && buffered == 0 && PlaybackLevel == 0) SetMode("listening");
        }

        void Handle(string json)
        {
            var message = JsonUtility.FromJson<Incoming>(json);
            switch (message.type)
            {
                case "conversation_initiation_metadata":
                    var meta = message.conversation_initiation_metadata_event;
                    if (meta == null || !TryPcmRate(meta.user_input_audio_format, out microphoneRate) || !TryPcmRate(meta.agent_output_audio_format, out outputRate))
                    { Fail("Jarvis requires unsupported audio encoding; native transport supports PCM16 only."); return; }
                    StartAudio();
                    if (active != null) { SetStatus("connected"); SendContext(initialContext); }
                    break;
                case "ping":
                    if (message.ping_event != null) Queue(JsonUtility.ToJson(new Pong { event_id = message.ping_event.event_id }));
                    break;
                case "audio":
                    if (!Connected || message.audio_event == null) break;
                    lastAudioEventId = Math.Max(lastAudioEventId, message.audio_event.event_id);
                    if (awaitingInterruption)
                    {
                        // Chunks after a press are the interrupted response's tail; their ids stay discarded.
                        interruptedThrough = Math.Max(interruptedThrough, message.audio_event.event_id);
                        lastSuppressedAudio = Time.realtimeSinceStartup;
                        break;
                    }
                    if (message.audio_event.event_id <= interruptedThrough) break;
                    var decoded = DecodePcm(Convert.FromBase64String(message.audio_event.audio_base_64));
                    lock (audioLock)
                    {
                        if (outputSamples.Count + decoded.Length > outputRate * 20) throw new InvalidDataException("Audio backlog.");
                        foreach (float sample in decoded) outputSamples.Enqueue(sample);
                    }
                    SetMode("speaking");
                    break;
                case "interruption":
                    int interruptionId = message.interruption_event?.event_id ?? 0;
                    interruptedThrough = Math.Max(interruptedThrough, interruptionId > 0 ? interruptionId : lastAudioEventId);
                    // A missing/empty payload cannot establish an ordered server boundary for a local barge-in.
                    if (interruptionId > 0) awaitingInterruption = false;
                    ClearAudio();
                    SetMode("listening");
                    break;
                case "user_transcript":
                    if (!string.IsNullOrWhiteSpace(message.user_transcription_event?.user_transcript)) awaitingInterruption = false;
                    Transcript?.Invoke("user", message.user_transcription_event?.user_transcript ?? "");
                    break;
                case "agent_response":
                    // A new agent turn is a server boundary: the server heard the (unmuted) held microphone
                    // and moved on. Its audio ids follow the suppressed ones, which stay discarded.
                    awaitingInterruption = false;
                    AgentResponses++;
                    Transcript?.Invoke("agent", message.agent_response_event?.agent_response ?? "");
                    break;
                case "client_tool_call":
                    var tool = message.client_tool_call;
                    if (tool == null || string.IsNullOrEmpty(tool.tool_call_id) || !seenTools.Add(tool.tool_call_id)) break;
                    if (seenTools.Count > 512) { Fail("Voice tool-call limit reached; reconnect explicitly."); return; }
                    if (tool.expects_response) pendingTools.Add(tool.tool_call_id);
                    StartCoroutine(ExecuteTool(tool, generation, ExtractObject(json, "parameters")));
                    break;
                case "client_error": case "error": case "guardrail_triggered":
                    Fail("Jarvis reported a conversation error or guardrail stop.");
                    break;
            }
        }

        void StartAudio()
        {
            if (microphoneClip != null) throw new InvalidDataException("Duplicate audio metadata.");
            if (Microphone.devices.Length == 0) { Fail("No Quest microphone is available."); return; }
            microphoneDevice = Microphone.devices[0];
            microphoneClip = Microphone.Start(microphoneDevice, true, 1, microphoneRate);
            if (microphoneClip == null || microphoneClip.frequency != microphoneRate)
            { Fail("Microphone could not provide the requested PCM sample rate."); return; }
            microphoneChunk = Math.Max(1, microphoneRate / 20); // 50 ms packets, mono PCM16 LE
            microphoneCursor = 0;
            microphoneReady = false;
            microphoneStarted = lastCapture = Time.realtimeSinceStartup;
            StartPlayback();
        }

        void StartPlayback()
        {
            if (speaker == null) speaker = gameObject.AddComponent<AudioSource>();
            speaker.playOnAwake = false;
            speaker.loop = true;
            playbackClip = AudioClip.Create("Jarvis streamed PCM", outputRate, 1, outputRate, true, ReadAudio);
            speaker.clip = playbackClip;
            speaker.Play();
        }

        // Explicit, authored offline speech uses the same PCM output and mouth envelope.
        // It does not start a microphone, socket or a second conversation engine.
        public bool PlayLocalSpeech(AudioClip clip)
        {
            if (!applicationFocused || applicationPaused || !clip || clip.length > 30 || clip.channels < 1) return false;
            var samples = new float[clip.samples * clip.channels];
            if (!clip.GetData(samples, 0)) return false;
            Disconnect();
            outputRate = clip.frequency;
            lock (audioLock)
            {
                for (int i = 0; i < samples.Length; i += clip.channels)
                {
                    float sum = 0;
                    for (int channel = 0; channel < clip.channels; channel++) sum += samples[i + channel];
                    outputSamples.Enqueue(sum / clip.channels);
                }
            }
            localSpeech = true;
            StartPlayback();
            SetStatus("offline"); SetMode("speaking");
            return true;
        }

        void StopLocalSpeech()
        {
            localSpeech = false;
            if (speaker) { speaker.Stop(); speaker.clip = null; }
            if (playbackClip) { Destroy(playbackClip); playbackClip = null; }
            ResetPlaybackLevel();
            SetMode("listening");
        }

        // Barge-in is hold-to-talk only: CaptureMicrophone sends zero PCM while agent output is pending
        // (EncodeMicrophonePcm(..., AgentOutputPending())), so the provider cannot hear the learner over
        // the agent. A talk press must call this to stop playback before learner audio is sent.
        public void InterruptPlayback()
        {
            // A held talk action stops this response locally. VAD/user transcript supplies the server turn boundary.
            if (Connected && AgentOutputPending())
            {
                interruptedThrough = Math.Max(interruptedThrough, lastAudioEventId);
                awaitingInterruption = true;
                lastSuppressedAudio = Time.realtimeSinceStartup;
            }
            if (localSpeech) StopLocalSpeech();
            ClearAudio(); SetMode("listening");
        }

        // Talk released without speech (or ASR returned nothing): once the interrupted response's tail
        // has stopped arriving, the next agent reply must be audible. A held talk keeps suppression.
        void ReleaseSilentInterruption(float now)
        {
            if (awaitingInterruption && MicrophoneMuted && now - lastSuppressedAudio >= InterruptionGraceSeconds) awaitingInterruption = false;
        }

        void CaptureMicrophone()
        {
            if (microphoneClip == null) return;
            int cursor = Microphone.GetPosition(microphoneDevice);
            float now = Time.realtimeSinceStartup;
            if (cursor <= 0 && !microphoneReady)
            {
                if (now - microphoneStarted > 5) Fail("Microphone did not start recording.");
                return;
            }
            if (!microphoneReady) { microphoneReady = true; microphoneCursor = cursor; lastCapture = now; return; }
            // A long main-thread stall makes circular-buffer age ambiguous. Drop old audio explicitly.
            if (now - lastCapture > 0.8f) { microphoneCursor = cursor; lastCapture = now; return; }
            lastCapture = now;
            int available = (cursor - microphoneCursor + microphoneClip.samples) % microphoneClip.samples;
            while (available >= microphoneChunk)
            {
                var samples = new float[microphoneChunk * microphoneClip.channels];
                if (!microphoneClip.GetData(samples, microphoneCursor)) { Fail("Microphone PCM read failed."); return; }
                microphoneCursor = (microphoneCursor + microphoneChunk) % microphoneClip.samples;
                available -= microphoneChunk;
                if (answerSamples != null && !answerClip) AppendAnswer(samples, microphoneClip.channels);
                // Keep the audio clock/VAD running during hold-to-talk silence; never send captured muted speech.
                Queue(JsonUtility.ToJson(new AudioInput { user_audio_chunk = Convert.ToBase64String(EncodeMicrophonePcm(samples, microphoneClip.channels, MicrophoneMuted, AgentOutputPending())) }));
                if (!Connected) return;
            }
        }

        // Unity invokes this on its audio thread. Only plain data/locking here, no Unity API calls.
        void ReadAudio(float[] samples)
        {
            lock (audioLock)
            {
                for (int i = 0; i < samples.Length; i++) samples[i] = outputSamples.Count > 0 ? outputSamples.Dequeue() : 0;
                playbackLevel = MeasurePlaybackLevel(samples);
                Interlocked.Exchange(ref playbackTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        bool AgentOutputPending()
        {
            lock (audioLock) return Mode == "speaking" || outputSamples.Count > 0 || PlaybackLevel > 0;
        }

        public static float MeasurePlaybackLevel(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0;
            double energy = 0;
            foreach (float sample in samples)
                if (!float.IsNaN(sample) && !float.IsInfinity(sample)) energy += (double)sample * sample;
            // A quiet noise floor keeps silent gaps shut; speech RMS maps to a bounded jaw opening.
            return (float)Math.Min(1, Math.Max(0, (Math.Sqrt(energy / samples.Length) - .008) * 6));
        }

        void ResetPlaybackLevel()
        {
            playbackLevel = 0;
            Interlocked.Exchange(ref playbackTimestamp, 0);
        }

        public void SendContext(string context)
        {
            if (!Connected || string.IsNullOrEmpty(context) || context == lastContext) return;
            lastContext = context;
            Queue(JsonUtility.ToJson(new TextMessage { type = "contextual_update", text = context }));
        }

        public void SendUserMessage(string text)
        {
            if (Connected && !string.IsNullOrEmpty(text)) Queue(JsonUtility.ToJson(new TextMessage { type = "user_message", text = text }));
        }

        // One interview pick for the patient agent: a silent direction (contextual update, never deduplicated:
        // two rounds may legitimately repeat a cue) and then the clinician's move as the user turn.
        public bool SendInterviewTurn(string direction, string clinicianMove)
        {
            if (!Connected || string.IsNullOrWhiteSpace(clinicianMove)) return false;
            if (!string.IsNullOrWhiteSpace(direction)) Queue(JsonUtility.ToJson(new TextMessage { type = "contextual_update", text = "[DIRECTION] " + direction.Trim() }));
            Queue(JsonUtility.ToJson(new TextMessage { type = "user_message", text = "[CLINICIAN] " + clinicianMove.Trim() }));
            return true;
        }

        // Hold-to-answer. A live connection's microphone is tapped (it keeps sending silence to the agent);
        // otherwise the default microphone records for this answer only. Returns false with LastError set.
        public bool BeginAnswerRecording()
        {
            if (answerSamples != null) return true;
            if (!applicationFocused || applicationPaused) { LastError = "Return to the app to answer by voice."; return false; }
            if (Status == "connecting") { LastError = "The patient voice is still connecting; tap a choice or hold again in a moment."; return false; }
            if (microphoneClip)
            {
                answerRate = microphoneRate; answerSamples = new List<float>(answerRate * 4);
                return true;
            }
            if (!MicrophoneAuthorized()) { LastError = "Allow the microphone, then hold again (or tap a choice)."; return false; }
            if (Microphone.devices.Length == 0) { LastError = "No Quest microphone is available; tap a choice."; return false; }
            answerDevice = Microphone.devices[0]; answerRate = 16000; answerCursor = 0;
            answerClip = Microphone.Start(answerDevice, true, 20, answerRate);
            if (!answerClip) { LastError = "The microphone could not start; tap a choice."; return false; }
            answerRate = answerClip.frequency; answerSamples = new List<float>(answerRate * 4);
            return true;
        }

        public float[] EndAnswerRecording(out int rate)
        {
            rate = answerRate;
            if (answerSamples == null) return new float[0];
            if (answerClip)
            {
                PollAnswerMicrophone();
                Microphone.End(answerDevice); Destroy(answerClip); answerClip = null;
            }
            var samples = answerSamples.ToArray(); answerSamples = null;
            return samples;
        }

        public void CancelAnswerRecording() { EndAnswerRecording(out _); }

        void PollAnswerMicrophone()
        {
            if (!answerClip || answerSamples == null) return;
            int cursor = Microphone.GetPosition(answerDevice);
            int available = (cursor - answerCursor + answerClip.samples) % answerClip.samples;
            if (available <= 0) return;
            var samples = new float[available * answerClip.channels];
            if (answerClip.GetData(samples, answerCursor)) AppendAnswer(samples, answerClip.channels);
            answerCursor = cursor;
        }

        void AppendAnswer(float[] interleaved, int channels)
        {
            int limit = (int)(answerRate * MaxAnswerSeconds);
            for (int i = 0; i + channels <= interleaved.Length && answerSamples.Count < limit; i += channels)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += interleaved[i + c];
                answerSamples.Add(sum / channels);
            }
        }

        static bool MicrophoneAuthorized()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone)) return true;
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
            return false;
#else
            if (Application.HasUserAuthorization(UserAuthorization.Microphone)) return true;
            Application.RequestUserAuthorization(UserAuthorization.Microphone);
            return false;
#endif
        }

        // Mono PCM16 little-endian RIFF/WAVE, as POST /interviews/:id/answer {audio, mimeType: "audio/wav"} expects.
        public static byte[] EncodeWav(float[] mono, int rate)
        {
            if (mono == null || rate <= 0) throw new ArgumentException("WAV needs mono samples and a positive rate.");
            var pcm = EncodePcm(mono, 1);
            var wav = new byte[44 + pcm.Length];
            void Text(int at, string value) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)value[i]; }
            void Int(int at, int value) { wav[at] = (byte)value; wav[at + 1] = (byte)(value >> 8); wav[at + 2] = (byte)(value >> 16); wav[at + 3] = (byte)(value >> 24); }
            void Short(int at, int value) { wav[at] = (byte)value; wav[at + 1] = (byte)(value >> 8); }
            Text(0, "RIFF"); Int(4, 36 + pcm.Length); Text(8, "WAVE");
            Text(12, "fmt "); Int(16, 16); Short(20, 1); Short(22, 1); Int(24, rate); Int(28, rate * 2); Short(32, 2); Short(34, 16);
            Text(36, "data"); Int(40, pcm.Length);
            Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
            return wav;
        }

        public bool OwnsClientTool(ToolRequest request) => request != null && request.ConnectionGeneration == generation && pendingTools.Contains(request.ToolCallId);

        public void ResolveClientTool(ToolRequest request, string result, bool isError = false)
        {
            if (request == null || request.ConnectionGeneration != generation || !pendingTools.Remove(request.ToolCallId)) return;
            Queue(JsonUtility.ToJson(new ToolResult { tool_call_id = request.ToolCallId, result = result ?? "", is_error = isError }));
        }

        IEnumerator ExecuteTool(ToolCall tool, int epoch, string rawParameters)
        {
            if (encounterMode)
            {
                yield return ExecuteExternalTool(tool, epoch, rawParameters);
                yield break;
            }
            switch (tool.tool_name)
            {
                case "get_surgery_state":
                case "get_hint":
                case "explain_structure":
                case "highlight_structure":
                case "get_patient_brief":
                case "check_preop":
                    // Use Matthew's single shared implementation, preserving the full JSON
                    // parameters. The server reports a highlight only after the scene ACK.
                    Reply reply = null;
                    string path = SessionPath + "/tools/" + Uri.EscapeDataString(tool.tool_name);
                    yield return Http("POST", path, string.IsNullOrEmpty(rawParameters) ? "{}" : rawParameters, r => reply = r);
                    if (epoch != generation) yield break;
                    bool error = reply?.ok != true || reply.result == null;
                    ResolveClientTool(new ToolRequest { ToolCallId = tool.tool_call_id, ConnectionGeneration = epoch },
                        error ? "Coach service could not complete this tool request." : reply.result, error);
                    yield break;
                default:
                    yield return ExecuteExternalTool(tool, epoch, rawParameters);
                    yield break;
            }
        }

        IEnumerator ExecuteExternalTool(ToolCall tool, int epoch, string rawParameters)
        {
                    var external = new ToolRequest { ToolCallId = tool.tool_call_id, ToolName = tool.tool_name, ParametersJson = rawParameters, ConnectionGeneration = epoch };
                    if (ClientToolRequested != null)
                    {
                        ClientToolRequested.Invoke(external);
                        float until = Time.realtimeSinceStartup + 10;
                        while (epoch == generation && pendingTools.Contains(tool.tool_call_id) && Time.realtimeSinceStartup < until) yield return null;
                        if (epoch == generation && pendingTools.Contains(tool.tool_call_id)) ResolveClientTool(external, "Headset did not acknowledge the supported action in time.", true);
                    }
                    else ResolveClientTool(external, "This client tool is not supported by the headset.", true);
        }

        string SessionPath => (interviewMode ? "/interviews/" : encounterMode ? "/encounters/" : "/coach/sessions/") + Uri.EscapeDataString(CoachSessionId);

        IEnumerator Http(string method, string path, string body, Action<Reply> complete)
        {
            using (var request = new UnityWebRequest(baseUrl.TrimEnd('/') + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.timeout = timeoutSeconds;
                yield return request.SendWebRequest();
                Reply reply = null;
                if (request.result == UnityWebRequest.Result.Success || request.result == UnityWebRequest.Result.ProtocolError)
                {
                    try { reply = JsonUtility.FromJson<Reply>(request.downloadHandler.text); } catch (ArgumentException) { }
                    if (reply != null) reply.ok = request.result == UnityWebRequest.Result.Success;
                }
                complete(reply ?? new Reply());
            }
        }

        void Queue(string json)
        {
            var connection = active;
            if (connection == null || !connection.Open) return;
            if (connection.Outgoing.Count >= 100) { Fail("Voice connection cannot keep up with microphone audio."); return; }
            connection.Enqueue(json);
        }

        public void Disconnect()
        {
            localSpeech = false;
            permissionPending = false;
            generation++;
            StopAllCoroutines();
            var connection = active;
            active = null;
            if (connection != null) connection.RequestStop();
            if (microphoneClip != null)
            {
                Microphone.End(microphoneDevice);
                Destroy(microphoneClip);
                microphoneClip = null;
            }
            if (speaker != null) { speaker.Stop(); speaker.clip = null; }
            if (playbackClip != null) { Destroy(playbackClip); playbackClip = null; }
            lock (audioLock) { outputSamples.Clear(); ResetPlaybackLevel(); }
            pendingTools.Clear(); seenTools.Clear(); lastContext = ""; AgentResponses = 0;
            lastAudioEventId = interruptedThrough = -1; awaitingInterruption = false;
            patientId = ""; CoachSessionId = "";
            SetMode("listening");
            SetStatus("disconnected");
        }

        void ClearAudio()
        {
            lock (audioLock) { outputSamples.Clear(); ResetPlaybackLevel(); }
            if (speaker != null && playbackClip != null) { speaker.Stop(); speaker.Play(); }
        }
        void Fail(string message) { Disconnect(); LastError = message; Debug.LogWarning("SCALPAL_VOICE_ERROR role=" + encounterRole + " " + message); SetStatus("error"); }
        void SetStatus(string value)
        {
            if (Status == value) return;
            Status = value;
            if (Application.isPlaying) Debug.Log("SCALPAL_VOICE_STATUS " + value + " role=" + (encounterMode ? encounterRole : "coach") + " spatial=" + (speaker ? speaker.spatialBlend : -1));
            StatusChanged?.Invoke(value);
        }
        void SetMode(string value) { if (Mode != value) { Mode = value; ModeChanged?.Invoke(value); } }
        void OnDisable() { CancelAnswerRecording(); Disconnect(); }
        // Only an actual outstanding OS permission request survives its permission-dialog suspension.
        void OnApplicationPause(bool paused)
        {
            applicationPaused = paused;
            if (paused && !permissionPending && (Status == "connecting" || active != null || localSpeech)) Disconnect();
        }
        void OnApplicationFocus(bool focused)
        {
            applicationFocused = focused;
            if (!focused && !permissionPending && (Status == "connecting" || active != null || PlaybackActive)) Disconnect();
        }

        public static bool TryPcmRate(string format, out int rate)
        {
            rate = 0;
            return format != null && format.StartsWith("pcm_", StringComparison.Ordinal) && int.TryParse(format.Substring(4), out rate)
                && (rate == 8000 || rate == 16000 || rate == 22050 || rate == 24000 || rate == 44100 || rate == 48000);
        }
        public static byte[] EncodePcm(float[] interleaved, int channels)
        {
            if (channels <= 0 || interleaved.Length % channels != 0) throw new ArgumentException("PCM channel layout.");
            var bytes = new byte[interleaved.Length / channels * 2];
            for (int frame = 0; frame < bytes.Length / 2; frame++)
            {
                float sample = 0;
                for (int channel = 0; channel < channels; channel++) sample += interleaved[frame * channels + channel];
                sample = Math.Max(-1f, Math.Min(1f, sample / channels));
                short value = (short)Math.Round(sample * (sample < 0 ? 32768 : 32767));
                bytes[frame * 2] = (byte)(value & 255); bytes[frame * 2 + 1] = (byte)((value >> 8) & 255);
            }
            return bytes;
        }

        public static byte[] EncodeMicrophonePcm(float[] samples, int channels, bool muted, bool agentSpeaking = false)
        {
            // Half duplex suppresses captured speaker echo; this does not configure acoustic echo cancellation.
            return EncodePcm(muted || agentSpeaking ? new float[samples.Length] : samples, channels);
        }
        public static float[] DecodePcm(byte[] bytes)
        {
            if (bytes.Length % 2 != 0) throw new InvalidDataException("Incomplete PCM sample.");
            var samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8) / 32768f;
            return samples;
        }
        static bool ValidSessionId(string id)
        {
            if (id == null || !id.StartsWith("coach-", StringComparison.Ordinal) || id.Length < 12 || id.Length > 46) return false;
            for (int i = 6; i < id.Length; i++) if (!(id[i] >= 'a' && id[i] <= 'z') && !(id[i] >= '0' && id[i] <= '9')) return false;
            return true;
        }
        public static bool ValidInterviewId(string id)
        {
            if (id == null || !id.StartsWith("int-", StringComparison.Ordinal) || id.Length < 10 || id.Length > 44) return false;
            for (int i = 4; i < id.Length; i++) if (!(id[i] >= 'a' && id[i] <= 'z') && !(id[i] >= '0' && id[i] <= '9')) return false;
            return true;
        }
        public static bool ValidEncounterId(string id)
        {
            if (id == null || !id.StartsWith("enc-", StringComparison.Ordinal) || id.Length < 10 || id.Length > 44) return false;
            for (int i = 4; i < id.Length; i++) if (!(id[i] >= 'a' && id[i] <= 'z') && !(id[i] >= '0' && id[i] <= '9')) return false;
            return true;
        }
        static string ExtractObject(string json, string property)
        {
            int name = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);
            if (name < 0) return "{}";
            int start = json.IndexOf('{', name);
            if (start < 0) return "{}";
            bool quoted = false, escaped = false;
            int depth = 0;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (quoted) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') quoted = false; continue; }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return json.Substring(start, i - start + 1);
            }
            return "{}";
        }

        [Serializable, UnityEngine.Scripting.Preserve] sealed class Incoming { public string type; public Metadata conversation_initiation_metadata_event; public Ping ping_event; public AudioEvent audio_event; public Interruption interruption_event; public UserEvent user_transcription_event; public AgentEvent agent_response_event; public ToolCall client_tool_call; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Metadata { public string user_input_audio_format, agent_output_audio_format; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Ping { public int event_id; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class AudioEvent { public string audio_base_64; public int event_id; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Interruption { public int event_id; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class UserEvent { public string user_transcript; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class AgentEvent { public string agent_response; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class ToolCall { public string tool_name, tool_call_id; public bool expects_response; public ToolParameters parameters; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class ToolParameters { public string structure; public string[] selected; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Reply { public bool ok; public string signedUrl, agentId, mode, context, result, role, interviewId, patientId, phase, prompt, firstMessage, voiceId; public Snapshot snapshot; public EncounterState state; public ServiceError error; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class EncounterState { public string encounterId, patientId, phase; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class ServiceError { public string code; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Snapshot { public string sessionId, patientId, mode; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class AudioInput { public string user_audio_chunk; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Pong { public string type = "pong"; public int event_id; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class TextMessage { public string type, text; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class ToolResult { public string type = "client_tool_result", tool_call_id, result; public bool is_error; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class DynamicVariables { public string coach_session_id, encounter_id, session_id, patient_id, mode, context; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class BasicInitiation { public string type = "conversation_initiation_client_data"; public DynamicVariables dynamic_variables; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Initiation { public string type = "conversation_initiation_client_data"; public Overrides conversation_config_override; public DynamicVariables dynamic_variables; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class VoicedInitiation { public string type = "conversation_initiation_client_data"; public VoicedOverrides conversation_config_override; public DynamicVariables dynamic_variables; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class VoicedOverrides { public AgentOverride agent; public TtsOverride tts; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Overrides { public AgentOverride agent; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class TtsOverride { public string voice_id; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class AgentOverride { public Prompt prompt; public string first_message; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Prompt { public string prompt; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class PromptAgent { public Prompt prompt; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class GreetingAgent { public string first_message; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class PromptOverrides { public PromptAgent agent; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class VoicedPromptOverrides { public PromptAgent agent; public TtsOverride tts; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class GreetingOverrides { public GreetingAgent agent; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class VoicedGreetingOverrides { public GreetingAgent agent; public TtsOverride tts; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class TtsOverrides { public TtsOverride tts; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class PromptInitiation { public string type = "conversation_initiation_client_data"; public DynamicVariables dynamic_variables; public PromptOverrides conversation_config_override; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class VoicedPromptInitiation { public string type = "conversation_initiation_client_data"; public DynamicVariables dynamic_variables; public VoicedPromptOverrides conversation_config_override; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class GreetingInitiation { public string type = "conversation_initiation_client_data"; public DynamicVariables dynamic_variables; public GreetingOverrides conversation_config_override; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class VoicedGreetingInitiation { public string type = "conversation_initiation_client_data"; public DynamicVariables dynamic_variables; public VoicedGreetingOverrides conversation_config_override; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class TtsInitiation { public string type = "conversation_initiation_client_data"; public DynamicVariables dynamic_variables; public TtsOverrides conversation_config_override; }
    }
}
