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
    // Configure/Connect must be called on the Unity main thread from an explicit voice UI action.
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

        Connection active;
        int generation;
        AudioClip microphoneClip, playbackClip;
        string microphoneDevice, patientId = "", prompt = "", firstMessage = "", initialContext = "";
        string lastContext = "";
        bool encounterMode;
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
        readonly HashSet<string> pendingTools = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> seenTools = new HashSet<string>(StringComparer.Ordinal);

        sealed class Connection
        {
            public readonly int Generation;
            public readonly ClientWebSocket Socket = new ClientWebSocket();
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();
            public readonly ConcurrentQueue<string> Outgoing = new ConcurrentQueue<string>();
            public readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0);
            public volatile string Error;
            public volatile bool Open;
            public Connection(int generation) { Generation = generation; }
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
            encounterMode = true;
            if (!isActiveAndEnabled || !ValidEncounterId(encounterId) || string.IsNullOrEmpty(selectedPatientId))
            { Fail("A valid encounter and patient are required."); return; }
            CoachSessionId = encounterId;
            patientId = selectedPatientId;
            LastError = "";
            SetStatus("connecting");
            StartCoroutine(Begin(generation));
        }

        public void Connect(string coachSessionId)
        {
            Disconnect();
            encounterMode = false;
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
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone, callbacks);
                while (!answered && epoch == generation) yield return null;
                if (epoch != generation) yield break;
                if (!granted) { Fail("Microphone permission was denied."); yield break; }
            }
#else
            if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
            {
                yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
                if (epoch != generation) yield break;
                if (!Application.HasUserAuthorization(UserAuthorization.Microphone)) { Fail("Microphone permission was denied."); yield break; }
            }
#endif
            yield return BeginConversation(epoch);
        }

        // Kept separate so deterministic HTTP tests can exercise identity/roles without a mic or socket.
        IEnumerator BeginConversation(int epoch)
        {
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
            yield return Http("GET", encounterMode && encounterRole == "patient" ? "/jarvis/connection?agent=patient" : "/jarvis/connection", null, r => connection = r);
            if (epoch != generation) yield break;
            if (connection == null || !connection.ok)
            {
                string code = connection?.error?.code ?? "service_unreachable";
                Fail("Jarvis connection unavailable (" + code + "); check the existing voice service configuration.");
                yield break;
            }
            string url = connection.signedUrl;
            if (string.IsNullOrEmpty(url) && connection.mode == "public" && !string.IsNullOrEmpty(connection.agentId))
                url = "wss://api.elevenlabs.io/v1/convai/conversation?agent_id=" + Uri.EscapeDataString(connection.agentId);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var ws) || ws.Scheme != "wss" || ws.Host != "api.elevenlabs.io")
            { Fail("Voice service did not return a supported ElevenLabs WebSocket connection."); yield break; }
            string initJson = BuildInitiation(encounterMode ? encounterRole : state.snapshot.mode ?? "");
            active = new Connection(epoch);
            connectionStarted = Time.realtimeSinceStartup;
            _ = RunSocket(active, ws, initJson);
        }

        string BuildInitiation(string mode)
        {
            var variables = new DynamicVariables { coach_session_id = encounterMode ? "" : CoachSessionId,
                encounter_id = encounterMode ? CoachSessionId : "", session_id = CoachSessionId,
                patient_id = patientId, mode = mode, context = initialContext };
            // Separate schemas keep the original coach/default voice free of an empty TTS override.
            if (string.IsNullOrEmpty(prompt)) return JsonUtility.ToJson(new BasicInitiation { dynamic_variables = variables });
            var agent = new AgentOverride { prompt = new Prompt { prompt = prompt }, first_message = firstMessage };
            if (encounterMode && !string.IsNullOrEmpty(encounterVoiceId))
                return JsonUtility.ToJson(new VoicedInitiation { dynamic_variables = variables,
                    conversation_config_override = new VoicedOverrides { agent = agent, tts = new TtsOverride { voice_id = encounterVoiceId } } });
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
                connection.Cancel.Cancel();
                connection.Socket.Abort();
                if (send != null) { try { await send.ConfigureAwait(false); } catch (Exception) { } }
                connection.Socket.Dispose();
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
                connection.Cancel.Cancel();
            }
        }

        void Update()
        {
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
                    var decoded = DecodePcm(Convert.FromBase64String(message.audio_event.audio_base_64));
                    lock (audioLock)
                    {
                        if (outputSamples.Count + decoded.Length > outputRate * 20) throw new InvalidDataException("Audio backlog.");
                        foreach (float sample in decoded) outputSamples.Enqueue(sample);
                    }
                    SetMode("speaking");
                    break;
                case "interruption":
                    ClearAudio();
                    SetMode("listening");
                    break;
                case "user_transcript":
                    Transcript?.Invoke("user", message.user_transcription_event?.user_transcript ?? "");
                    break;
                case "agent_response":
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
            if (!clip || clip.length > 30 || clip.channels < 1) return false;
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

        public void InterruptPlayback()
        {
            if (localSpeech) StopLocalSpeech();
            ClearAudio(); SetMode("listening");
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
                // Keep the audio clock/VAD running during hold-to-talk silence; never send captured muted speech.
                Queue(JsonUtility.ToJson(new AudioInput { user_audio_chunk = Convert.ToBase64String(EncodeMicrophonePcm(samples, microphoneClip.channels, MicrophoneMuted)) }));
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

        string SessionPath => (encounterMode ? "/encounters/" : "/coach/sessions/") + Uri.EscapeDataString(CoachSessionId);

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
            connection.Outgoing.Enqueue(json);
            connection.SendSignal.Release();
        }

        public void Disconnect()
        {
            localSpeech = false;
            generation++;
            StopAllCoroutines();
            var connection = active;
            active = null;
            if (connection != null)
            {
                connection.Cancel.Cancel();
                try { connection.Socket.Abort(); } catch (ObjectDisposedException) { }
            }
            if (microphoneClip != null)
            {
                Microphone.End(microphoneDevice);
                Destroy(microphoneClip);
                microphoneClip = null;
            }
            if (speaker != null) { speaker.Stop(); speaker.clip = null; }
            if (playbackClip != null) { Destroy(playbackClip); playbackClip = null; }
            lock (audioLock) { outputSamples.Clear(); ResetPlaybackLevel(); }
            pendingTools.Clear(); seenTools.Clear(); lastContext = "";
            patientId = ""; CoachSessionId = "";
            SetMode("listening");
            SetStatus("disconnected");
        }

        void ClearAudio()
        {
            lock (audioLock) { outputSamples.Clear(); ResetPlaybackLevel(); }
            if (speaker != null && playbackClip != null) { speaker.Stop(); speaker.Play(); }
        }
        void Fail(string message) { Disconnect(); LastError = message; SetStatus("error"); }
        void SetStatus(string value) { if (Status != value) { Status = value; StatusChanged?.Invoke(value); } }
        void SetMode(string value) { if (Mode != value) { Mode = value; ModeChanged?.Invoke(value); } }
        void OnDisable() => Disconnect();
        // Android's permission dialog may pause the app during Begin. Do not cancel that prompt.
        void OnApplicationPause(bool paused) { if (paused && (active != null || localSpeech)) Disconnect(); }
        void OnApplicationFocus(bool focused) { if (!focused && PlaybackActive) Disconnect(); }

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

        public static byte[] EncodeMicrophonePcm(float[] samples, int channels, bool muted)
        {
            return EncodePcm(muted ? new float[samples.Length] : samples, channels);
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

        [Serializable, UnityEngine.Scripting.Preserve] sealed class Incoming { public string type; public Metadata conversation_initiation_metadata_event; public Ping ping_event; public AudioEvent audio_event; public UserEvent user_transcription_event; public AgentEvent agent_response_event; public ToolCall client_tool_call; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Metadata { public string user_input_audio_format, agent_output_audio_format; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Ping { public int event_id; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class AudioEvent { public string audio_base_64; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class UserEvent { public string user_transcript; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class AgentEvent { public string agent_response; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class ToolCall { public string tool_name, tool_call_id; public bool expects_response; public ToolParameters parameters; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class ToolParameters { public string structure; public string[] selected; }
        [Serializable, UnityEngine.Scripting.Preserve] sealed class Reply { public bool ok; public string signedUrl, agentId, mode, context, result; public Snapshot snapshot; public EncounterState state; public ServiceError error; }
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
    }
}
