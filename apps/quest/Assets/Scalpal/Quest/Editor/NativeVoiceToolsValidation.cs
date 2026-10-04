using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Voice;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Quest.Editor
{
    // Actual production tool/HTTP coroutines and actual isolated Hono routes. Only the
    // voice connection metadata/outgoing queue is synthetic; no socket is connected.
    public static class NativeVoiceToolsValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks, calls;
        [Serializable] sealed class Created { public string sessionId; }
        [Serializable] sealed class Commands { public Command[] commands; }
        [Serializable] sealed class Command { public string commandId, targetId, status; }
        [Serializable] sealed class ToolResult { public string type, tool_call_id, result; public bool is_error; }
        [Serializable] sealed class SelectedArray { public string[] selected; }
        [Serializable] sealed class SelectedString { public string selected; }
        [Serializable] sealed class Init { public string type; public Variables dynamic_variables; public Config conversation_config_override; }
        [Serializable] sealed class Variables { public string coach_session_id,encounter_id,session_id,patient_id,mode,context; }
        [Serializable] sealed class Config { public Agent agent; public Tts tts; }
        [Serializable] sealed class Agent { public Prompt prompt; public string first_message; }
        [Serializable] sealed class Prompt { public string prompt; }
        [Serializable] sealed class Tts { public string voice_id; }
        [Serializable] sealed class ContextUpdate { public string type, text; }
        sealed class Exchange { public string url, method, body; }

        [MenuItem("Scalpal/Quest/Validate Native Voice Tools")]
        public static void Run()
        {
            checks = calls = 0;
            GameObject fixture = null;
            Process server = null;
            QuestJarvisVoice voice = null;
            var connections = new List<object>();
            try
            {
                string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
                string service = Path.Combine(repo, "services/preop");
                var info = new ProcessStartInfo("/usr/bin/env", "node --import " + Quote(Path.Combine(service, "node_modules/tsx/dist/loader.mjs"))
                    + " " + Quote(Path.Combine(repo, "scripts/quest/native-coach-check/server.ts")))
                {
                    WorkingDirectory = service, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                server = Process.Start(info);
                var ready = server.StandardOutput.ReadLineAsync();
                if (!ready.Wait(15000)) throw new InvalidOperationException("Isolated voice tools fixture did not become ready");
                const string marker = "SCALPAL_COACH_TEST_ENDPOINT=";
                string line = ready.Result ?? "";
                if (!line.StartsWith(marker, StringComparison.Ordinal)) throw new InvalidOperationException("Isolated voice tools fixture failed to start");
                string endpoint = line.Substring(marker.Length);
                if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1")
                    throw new InvalidOperationException("Voice fixture must use an ephemeral loopback endpoint");

                var candidate = JsonUtility.FromJson<SurgicalCase>(Http(endpoint, "GET", "/patients/" + NativeCaseSession.PatientId + "/case"));
                Assert(candidate.procedureId == NativeCaseSession.ProcedureId || candidate.procedureId == "open_appendectomy", "isolated actual service supplies native appendectomy case");
                string sid = JsonUtility.FromJson<Created>(Http(endpoint, "POST", "/coach/sessions",
                    "{\"patientId\":\"" + candidate.patientId + "\",\"mode\":\"virtual\"}")).sessionId;
                // Make another case the global latest session. Native calls must still use sid.
                string otherSid = JsonUtility.FromJson<Created>(Http(endpoint, "POST", "/coach/sessions",
                    "{\"patientId\":\"patient-demo-001\",\"mode\":\"virtual\"}")).sessionId;
                Assert(!string.IsNullOrEmpty(sid) && otherSid != sid, "two independent synthetic coach sessions exist");

                fixture = new GameObject("NativeVoiceToolsFixture");
                fixture.SetActive(false);
                voice = fixture.AddComponent<QuestJarvisVoice>();
                ValidateInitiation(voice);
                ValidateAudioLifecycle(voice,connections);
                ValidateSilentBargeIn(voice,connections);
                voice.ConfigureEndpoint(endpoint);
                Set(voice, "generation", 100);
                Property(voice, "CoachSessionId", sid);
                Property(voice, "Status", "connected");
                object connection = NewConnection(voice, 100); connections.Add(connection);
                var outgoing = Outgoing(connection);

                var state = Invoke(voice, outgoing, "get_surgery_state", "{}", sid);
                Assert(!state.is_error && state.result.Contains("appendectomy"), "surgery state belongs to the explicitly bound case rather than latest coach session");
                var hint = Invoke(voice, outgoing, "get_hint", "{}", sid);
                Assert(!hint.is_error && hint.result.StartsWith("Hint tier 1 of 3:", StringComparison.Ordinal), "native hint returns shared server tool text");
                var anatomy = Invoke(voice, outgoing, "explain_structure", "{ \"structure\": \"appendicular artery\", \"extra\": {\"preserved\":true} }", sid);
                Assert(!anatomy.is_error && anatomy.result.Contains("ileocolic"), "native explanation receives actual structure facts from server");
                var pending = Invoke(voice, outgoing, "highlight_structure", "{\"structure\":\"appendix\"}", sid);
                Assert(!pending.is_error && pending.result.Contains("has not confirmed") && !pending.result.StartsWith("Highlighted", StringComparison.Ordinal),
                    "unacknowledged highlight never claims a scene effect");
                var brief = Invoke(voice, outgoing, "get_patient_brief", "{}", sid);
                Assert(!brief.is_error && brief.result.Contains("Checklist option types:"), "native brief receives shared server checklist context");
                string[] present = candidate.brief.flags.Select(flag => flag.type).Distinct().ToArray();
                Assert(present.Length > 0, "recorded native case has meaningful selected checklist flags");
                string arrayJson = JsonUtility.ToJson(new SelectedArray { selected = present });
                string commaJson = JsonUtility.ToJson(new SelectedString { selected = string.Join(",", present) });
                var arrayCheck = Invoke(voice, outgoing, "check_preop", arrayJson, sid);
                var commaCheck = Invoke(voice, outgoing, "check_preop", commaJson, sid);
                Assert(!arrayCheck.is_error && !commaCheck.is_error && arrayCheck.result == commaCheck.result && arrayCheck.result.Contains("You caught all"),
                    "raw selected array and comma-separated string both reach actual shared preop scoring intact");

                // Resolve only the new command produced by this second highlight call.
                var existing = JsonUtility.FromJson<Commands>(Http(endpoint, "GET", Session(sid) + "/commands"));
                var seen = new HashSet<string>((existing.commands ?? Array.Empty<Command>()).Select(command => command.commandId));
                bool acknowledged = false;
                var applied = Invoke(voice, outgoing, "highlight_structure", "{\"structure\":\"cecum\"}", sid, () =>
                {
                    if (acknowledged) return;
                    var list = JsonUtility.FromJson<Commands>(Http(endpoint, "GET", Session(sid) + "/commands"));
                    var command = (list.commands ?? Array.Empty<Command>()).FirstOrDefault(item => !seen.Contains(item.commandId) && item.targetId == "cecum");
                    if (command == null) return;
                    Http(endpoint, "POST", Session(sid) + "/commands/" + Uri.EscapeDataString(command.commandId) + "/ack",
                        "{\"status\":\"applied\",\"reason\":\"synthetic scene acknowledgement\"}");
                    acknowledged = true;
                });
                Assert(acknowledged && !applied.is_error && applied.result.StartsWith("Highlighted the cecum", StringComparison.Ordinal),
                    "native highlight claims success only after explicit actual HTTP scene acknowledgement");

                Property(voice, "CoachSessionId", "coach-notpresent");
                var missing = Invoke(voice, outgoing, "get_surgery_state", "{}", "coach-notpresent");
                Assert(missing.is_error && missing.result == "Coach service could not complete this tool request.", "actual missing-session HTTP error becomes a native tool error receipt");
                Property(voice, "CoachSessionId", sid);

                // Start a real request, then replace voice ownership before completing it.
                string oldId = "synthetic-old-generation";
                Get<HashSet<string>>(voice, "pendingTools").Add(oldId);
                var interrupted = new Pump(ToolRoutine(voice, "get_surgery_state", oldId, "{}"));
                Assert(interrupted.Step() && interrupted.exchanges.Count == 1, "old-generation request is actually in flight before connection replacement");
                voice.Disconnect();
                int nextEpoch = Get<int>(voice, "generation");
                Property(voice, "CoachSessionId", otherSid); Property(voice, "Status", "connected");
                object replacement = NewConnection(voice, nextEpoch); connections.Add(replacement);
                interrupted.Run();
                Assert(outgoing.IsEmpty && Outgoing(replacement).IsEmpty, "late response emits no tool outcome into old or replacement voice connection");
                Assert(!Get<HashSet<string>>(voice, "pendingTools").Contains(oldId), "disconnect clears abandoned tool ownership");
                ValidateContextFeed(voice, connections, endpoint, fixture);
                UnityEngine.Debug.Log("SCALPAL_NATIVE_VOICE_TOOLS_VALIDATION_OK checks=" + checks + " calls=" + calls
                    + " actual ExecuteTool/UnityWebRequest + isolated Hono; synthetic outgoing connection only; no WSS/microphone/provider");
            }
            finally
            {
                if (voice) voice.Disconnect();
                if (fixture) UnityEngine.Object.DestroyImmediate(fixture);
                foreach (var connection in connections)
                    foreach (var field in new[] { "Socket", "Cancel", "SendSignal" })
                        if (connection.GetType().GetField(field).GetValue(connection) is IDisposable resource) resource.Dispose();
                if (server != null)
                {
                    if (!server.HasExited) server.Kill();
                    server.WaitForExit(5000); server.Dispose();
                }
            }
        }

        static ToolResult Invoke(QuestJarvisVoice voice, ConcurrentQueue<string> outgoing, string name, string raw, string sid, Action whileWaiting = null)
        {
            string id = "synthetic-call-" + (++calls);
            Assert(outgoing.IsEmpty, "each native tool starts without a leftover outgoing outcome");
            Get<HashSet<string>>(voice, "pendingTools").Add(id);
            var pump = new Pump(ToolRoutine(voice, name, id, raw)); pump.Run(whileWaiting);
            Assert(pump.exchanges.Count == 1 && pump.exchanges[0].method == "POST"
                && new Uri(pump.exchanges[0].url).AbsolutePath == Session(sid) + "/tools/" + name
                && pump.exchanges[0].body == raw, "actual native request preserves exact coach session and unparsed tool parameter JSON");
            Assert(outgoing.TryDequeue(out string json) && outgoing.IsEmpty, "actual native tool resolves exactly one outgoing outcome");
            var result = JsonUtility.FromJson<ToolResult>(json);
            Assert(result.type == "client_tool_result" && result.tool_call_id == id && result.result != null
                && !Get<HashSet<string>>(voice, "pendingTools").Contains(id), "native result receipt matches its call and clears pending ownership");
            return result;
        }
        static void ValidateInitiation(QuestJarvisVoice voice)
        {
            const string authored="Quote \"here\", backslash \\, newline\n tab\t and café";
            foreach(var role in new[]{"virtual","attending","patient"})
            {
                bool encounter=role!="virtual";string id=encounter?"enc-abcdef123456":"coach-abcdef123456";
                Set(voice,"encounterMode",encounter);Set(voice,"patientId","patient-demo-multi-source");Property(voice,"CoachSessionId",id);
                if(encounter)voice.ConfigureEncounterConversation(authored,authored,role=="patient"?"patient-voice":"",role);
                else voice.ConfigureConversation(authored,authored,authored);
                string json=(string)Call(voice,"BuildInitiation",role);var init=JsonUtility.FromJson<Init>(json);
                Assert(init.type=="conversation_initiation_client_data"&&init.dynamic_variables.session_id==id&&init.dynamic_variables.patient_id=="patient-demo-multi-source"&&init.dynamic_variables.mode==role,"actual production initiation preserves exact identity and role: "+role);
                Assert(init.dynamic_variables.coach_session_id==(encounter?"":id)&&init.dynamic_variables.encounter_id==(encounter?id:""),"coach and encounter identifiers stay distinct in actual serialized initiation: "+role);
                Assert(init.conversation_config_override.agent.prompt.prompt==authored&&init.conversation_config_override.agent.first_message==authored,"actual JsonUtility initiation escapes prompt/greeting and round-trips authored text: "+role);
                Assert(json.Contains("\"tts\"")==(role=="patient"),"actual surgery/attending serializer omits tts; patient includes explicit voice: "+role);
                if(role=="patient")Assert(init.conversation_config_override.tts.voice_id=="patient-voice","serialized patient TTS voice matches configured voice");
                else if(!encounter)Assert(init.dynamic_variables.context==authored,"surgery dynamic context round-trips escaped text");
                foreach(var optional in new[]{new[]{"",""},new[]{authored,""},new[]{"",authored},new[]{" ","\t"}})
                {
                    if(encounter)voice.ConfigureEncounterConversation(optional[0],optional[1],role=="patient"?"patient-voice":"",role);
                    else voice.ConfigureConversation(optional[0],optional[1]);
                    json=(string)Call(voice,"BuildInitiation",role);init=JsonUtility.FromJson<Init>(json);
                    Assert(json.Contains("\"prompt\"")==!string.IsNullOrWhiteSpace(optional[0])&&json.Contains("\"first_message\"")==!string.IsNullOrWhiteSpace(optional[1]),"empty optional prompt/greeting keys are absent rather than synthesized default objects: "+role);
                    Assert(json.Contains("\"tts\"")==(role=="patient")&&init.dynamic_variables.session_id==id,"optional overrides preserve exact identity and only patient voice TTS: "+role);
                }
            }
            Set(voice,"encounterMode",false);voice.ConfigureConversation("","");voice.Disconnect();
        }
        static void ValidateAudioLifecycle(QuestJarvisVoice voice,List<object> connections)
        {
            Property(voice,"Status","connected");Set(voice,"outputRate",16000);
            var connection=NewConnection(voice,Get<int>(voice,"generation"));connections.Add(connection);
            string pcm=Convert.ToBase64String(QuestJarvisVoice.EncodePcm(new[]{.3f,-.3f,.3f},1));
            void Audio(int id) => Call(voice,"Handle","{\"type\":\"audio\",\"audio_event\":{\"event_id\":"+id+",\"audio_base_64\":\""+pcm+"\"}}");
            var queue=Get<Queue<float>>(voice,"outputSamples");Audio(10);
            Assert(queue.Count==3&&(bool)Call(voice,"AgentOutputPending"),"actual provider audio queues decoded PCM and gates microphone while output is pending");
            var silence=QuestJarvisVoice.DecodePcm(QuestJarvisVoice.EncodeMicrophonePcm(new[]{.8f},1,false,(bool)Call(voice,"AgentOutputPending")));
            Assert(silence.Single()==0,"production playback gate sends timed silence for unmuted input during agent speech");
            Call(voice,"Handle","{\"type\":\"interruption\",\"interruption_event\":{\"event_id\":10}}");Audio(9);Audio(10);
            Assert(queue.Count==0&&voice.Mode=="listening","provider interruption discards buffered and late audio through the interrupted event ID");
            Audio(11);Assert(queue.Count==3,"later provider response above the interruption cutoff remains playable");
            voice.InterruptPlayback();Audio(12);Call(voice,"Handle","{\"type\":\"audio\",\"audio_event\":{\"audio_base_64\":\""+pcm+"\"}}");
            Call(voice,"Handle","{\"type\":\"interruption\"}");
            Assert(queue.Count==0&&voice.PlaybackLevel==0&&!(bool)Call(voice,"AgentOutputPending"),"explicit held-talk interruption drops late/idless chunks and opens half-duplex input");
            Assert(Get<bool>(voice,"awaitingInterruption"),"idless interruption cannot release local suppression before a known server turn boundary");
            var held=QuestJarvisVoice.DecodePcm(QuestJarvisVoice.EncodeMicrophonePcm(new[]{.8f},1,false,(bool)Call(voice,"AgentOutputPending")));
            Assert(held.Single()>.79f,"explicit barge-in permits held learner PCM after stopping agent playback");
            Call(voice,"Handle","{\"type\":\"interruption\",\"interruption_event\":{\"event_id\":12}}");Audio(12);Audio(13);
            Assert(queue.Count==3,"server turn boundary releases local suppression while retaining interrupted-ID cutoff");
            voice.InterruptPlayback();Audio(14);
            Call(voice,"Handle","{\"type\":\"user_transcript\",\"user_transcription_event\":{\"user_transcript\":\"New question\"}}");Audio(14);Audio(15);
            Assert(queue.Count==3,"finalized learner turn releases local suppression without replaying discarded prior response IDs");
            Call(voice,"OnApplicationFocus",false);int suspendedGeneration=Get<int>(voice,"generation");
            Call(voice,"OnApplicationFocus",true);Call(voice,"OnApplicationPause",false);
            Assert(!voice.PlaybackActive&&voice.Status=="disconnected"&&Get<object>(voice,"active")==null&&Get<int>(voice,"generation")==suspendedGeneration,"focus restoration never reconnects voice or resumes a canceled conversation");
            Property(voice,"Status","connected");connections.Add(NewConnection(voice,Get<int>(voice,"generation")));Audio(1);
            Assert(queue.Count==3,"new conversation generation resets the old interruption filter");
            voice.Disconnect();
            Property(voice,"Status","connecting");int connectingGeneration=Get<int>(voice,"generation");Call(voice,"OnApplicationFocus",false);Call(voice,"OnApplicationFocus",true);
            Assert(Get<int>(voice,"generation")>connectingGeneration&&voice.Status=="disconnected"&&Get<object>(voice,"active")==null,"focus loss cancels HTTP/connecting negotiation and restoration cannot finish or restart it");
            Property(voice,"Status","connecting");connectingGeneration=Get<int>(voice,"generation");Call(voice,"OnApplicationPause",true);Call(voice,"OnApplicationPause",false);
            Assert(Get<int>(voice,"generation")>connectingGeneration&&voice.Status=="disconnected","pause cancels negotiation even before a socket exists");
            Property(voice,"Status","connecting");Set(voice,"permissionPending",true);int permissionGeneration=Get<int>(voice,"generation");Call(voice,"OnApplicationPause",true);Call(voice,"OnApplicationFocus",false);
            Assert(Get<int>(voice,"generation")==permissionGeneration&&voice.Status=="connecting","actual outstanding first-use permission prompt survives OS-dialog focus/pause");
            voice.Disconnect();Assert(!Get<bool>(voice,"permissionPending"),"explicit stop clears permission request ownership");Call(voice,"OnApplicationFocus",true);Call(voice,"OnApplicationPause",false);
            Assert(Get<AudioClip>(voice,"microphoneClip")==null,"protocol/lifecycle regression never opened a real microphone");
            Call(connection,"Dispose");Call(connection,"Dispose");Call(connection,"RequestStop");Call(connection,"Enqueue","ignored-after-disposal");
            bool disposed=false;try { ((SemaphoreSlim)connection.GetType().GetField("SendSignal").GetValue(connection)).Wait(0); }catch(ObjectDisposedException){disposed=true;}
            Assert(disposed,"connection signal resources dispose after ownership ends; repeated stop/dispose/queue remain safe");
        }

        // Learner presses talk while the agent speaks, then says nothing (or ASR is empty, or the provider's
        // interruption carries no id). The interrupted tail must stay discarded, but the next agent
        // reply must be audible; before the bounded release the agent stayed mute until reconnect.
        static void ValidateSilentBargeIn(QuestJarvisVoice voice,List<object> connections)
        {
            string pcm=Convert.ToBase64String(QuestJarvisVoice.EncodePcm(new[]{.3f,-.3f,.3f},1));
            void Audio(int id) => Call(voice,"Handle","{\"type\":\"audio\",\"audio_event\":{\"event_id\":"+id+",\"audio_base_64\":\""+pcm+"\"}}");
            Queue<float> Fresh()
            {
                voice.Disconnect();Property(voice,"Status","connected");Set(voice,"outputRate",16000);
                connections.Add(NewConnection(voice,Get<int>(voice,"generation")));
                return Get<Queue<float>>(voice,"outputSamples");
            }
            int grace=(int)(QuestJarvisVoice.InterruptionGraceSeconds*1000)+150;

            // Press, release at once, silence.
            var queue=Fresh();voice.MicrophoneMuted=true;Audio(20);
            Assert(queue.Count==3&&voice.Mode=="speaking","agent reply is playing before the talk press");
            voice.MicrophoneMuted=false;voice.InterruptPlayback();Audio(20);
            Assert(queue.Count==0,"talk press discards the interrupted response's buffered and late audio");
            voice.MicrophoneMuted=true;Call(voice,"Handle","{\"type\":\"interruption\"}");Call(voice,"Update");Audio(20);
            Assert(queue.Count==0,"interrupted tail still arriving right after release stays discarded");
            Thread.Sleep(grace);Call(voice,"Update");Audio(20);
            Assert(queue.Count==0,"interrupted response's ids stay discarded after the silent release");
            Audio(21);
            Assert(queue.Count==3,"silent talk press: next agent reply is audible after release and the grace period");

            // Press and hold silently past the grace: still suppressed while held, audible after release.
            queue=Fresh();voice.MicrophoneMuted=true;Audio(40);voice.MicrophoneMuted=false;voice.InterruptPlayback();
            Thread.Sleep(grace);Call(voice,"Update");
            Assert(Get<bool>(voice,"awaitingInterruption"),"a held talk keeps suppression without a turn boundary");
            voice.MicrophoneMuted=true;Call(voice,"Update");Audio(41);
            Assert(queue.Count==3,"silent hold: release after the tail went quiet makes the next reply audible");

            // Microphone left open (surgery warnings call InterruptPlayback without a talk control): a new
            // agent turn is the boundary.
            queue=Fresh();voice.MicrophoneMuted=false;Audio(30);voice.InterruptPlayback();Audio(30);Audio(31);
            Assert(queue.Count==0,"open-microphone interruption suppresses audio until a turn boundary");
            Call(voice,"Handle","{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"Next turn.\"}}");
            Audio(30);Audio(31);
            Assert(queue.Count==0,"new agent turn keeps every suppressed response id discarded");
            Audio(32);
            Assert(queue.Count==3,"new agent turn after an interruption is audible without learner speech");
            voice.Disconnect();voice.MicrophoneMuted=false;
        }

        // Production NativeCaseSession.RefreshVoiceContext against the isolated service: the laptop's
        // context feed semantics (full card on structural change or ~10 s, one-line deltas between,
        // nothing while unchanged) instead of the whole state card on every change.
        static void ValidateContextFeed(QuestJarvisVoice voice, List<object> connections, string endpoint, GameObject fixture)
        {
            string sid = JsonUtility.FromJson<Created>(Http(endpoint, "POST", "/coach/sessions",
                "{\"patientId\":\"" + NativeCaseSession.PatientId + "\",\"mode\":\"virtual\"}")).sessionId;
            var session = fixture.AddComponent<NativeCaseSession>();
            var relay = fixture.AddComponent<CoachRelay>();
            session.voice = voice; session.coach = relay; session.coachBaseUrl = endpoint;
            Property(relay, "SessionId", sid);
            voice.Disconnect(); Property(voice, "Status", "connected");
            object connection = NewConnection(voice, Get<int>(voice, "generation")); connections.Add(connection);
            var outgoing = Outgoing(connection);
            var sent = new List<string>();
            int Poll()
            {
                var routine = (IEnumerator)typeof(NativeCaseSession).GetMethod("RefreshVoiceContext", Private).Invoke(session, new object[] { Get<int>(session, "generation"), sid });
                var pump = new Pump(routine); pump.Run();
                Assert(pump.exchanges.Count == 1, "one context request per poll");
                int count = 0;
                while (outgoing.TryDequeue(out string json))
                {
                    var update = JsonUtility.FromJson<ContextUpdate>(json);
                    Assert(update.type == "contextual_update", "native context reaches the agent as a contextual update");
                    sent.Add(update.text); count++;
                }
                return count;
            }
            void Event(string json) => Http(endpoint, "POST", Session(sid) + "/events", json);
            bool Full(string text) => text.StartsWith("[LIVE SURGERY STATE v", StringComparison.Ordinal);
            bool Delta(string text) => text.StartsWith("[STATE DELTA v", StringComparison.Ordinal) && !text.Contains("[LIVE SURGERY STATE");

            Assert(Poll() == 1 && Full(sent.Last()), "a connected voice first receives the full state card");
            for (int i = 0; i < 3; i++) Assert(Poll() == 0, "unchanged state is never resent on later polls");
            Event("{\"event\":{\"type\":\"instrument\",\"instrumentId\":\"scalpel\",\"hand\":\"right\",\"held\":true}}");
            Assert(Poll() == 1 && Delta(sent.Last()) && sent.Last().Contains("Picked up the scalpel"), "a non-structural event is sent as a one-line state delta, not the full card");
            Assert(Poll() == 0, "a delta is sent once");
            Event("{\"event\":{\"type\":\"instrument\",\"instrumentId\":\"scalpel\",\"hand\":\"right\",\"held\":false}}");
            Assert(Poll() == 1 && Delta(sent.Last()), "consecutive events between full cards stay deltas");
            Http(endpoint, "POST", Session(sid) + "/simulate", "{\"kind\":\"correct_action\"}");
            Assert(Poll() == 1 && Full(sent.Last()), "a milestone is structural and sends a fresh full card at once");
            Event("{\"event\":{\"type\":\"instrument\",\"instrumentId\":\"scalpel\",\"hand\":\"left\",\"held\":true}}");
            Assert(Poll() == 1 && Delta(sent.Last()), "events after a full card are deltas again");
            Set(session, "voiceContextForce", true);
            Assert(Poll() == 1 && Full(sent.Last()), "a reconnected voice is forced one full card");
            Assert(sent.Count(Full) == 3 && sent.Count(Delta) == 3, "full cards only first, on structural change and on reconnect");
            voice.Disconnect();
            UnityEngine.Object.DestroyImmediate(session); UnityEngine.Object.DestroyImmediate(relay);
        }

        static IEnumerator ToolRoutine(QuestJarvisVoice voice, string name, string id, string raw)
        {
            var type = typeof(QuestJarvisVoice).GetNestedType("ToolCall", BindingFlags.NonPublic);
            object tool = Activator.CreateInstance(type, true);
            type.GetField("tool_name").SetValue(tool, name); type.GetField("tool_call_id").SetValue(tool, id);
            type.GetField("expects_response").SetValue(tool, true);
            return (IEnumerator)typeof(QuestJarvisVoice).GetMethod("ExecuteTool", Private).Invoke(voice, new[] { tool, Get<int>(voice, "generation"), raw });
        }
        static object NewConnection(QuestJarvisVoice voice, int epoch)
        {
            var type = typeof(QuestJarvisVoice).GetNestedType("Connection", BindingFlags.NonPublic);
            object connection = Activator.CreateInstance(type, new object[] { epoch });
            type.GetField("Open").SetValue(connection, true); Set(voice, "active", connection);
            return connection;
        }
        static ConcurrentQueue<string> Outgoing(object connection) => (ConcurrentQueue<string>)connection.GetType().GetField("Outgoing").GetValue(connection);
        static string Session(string sid) => "/coach/sessions/" + Uri.EscapeDataString(sid);
        static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

        sealed class Pump
        {
            readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
            AsyncOperation waiting;
            public readonly List<Exchange> exchanges = new List<Exchange>();
            public Pump(IEnumerator routine) { stack.Push(routine); }
            public bool Step()
            {
                if (waiting != null && !waiting.isDone) return true;
                waiting = null;
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    if (!top.MoveNext()) { stack.Pop(); continue; }
                    if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                    if (top.Current is UnityWebRequestAsyncOperation operation)
                    {
                        var request = operation.webRequest;
                        exchanges.Add(new Exchange { url = request.url, method = request.method,
                            body = request.uploadHandler == null ? null : Encoding.UTF8.GetString(request.uploadHandler.data) });
                    }
                    waiting = top.Current as AsyncOperation;
                    return true;
                }
                return false;
            }
            public void Run(Action whileWaiting = null)
            {
                var deadline = Stopwatch.StartNew();
                long nextHook = 0;
                while (Step())
                {
                    if (deadline.ElapsedMilliseconds > 15000) throw new InvalidOperationException("Native voice tool fixture coroutine timed out");
                    if (whileWaiting != null && deadline.ElapsedMilliseconds >= nextHook)
                    { whileWaiting(); nextHook = deadline.ElapsedMilliseconds + 100; }
                    Thread.Sleep(5);
                }
            }
        }

        static string Http(string endpoint, string method, string path, string body = null)
        {
            using (var request = new UnityWebRequest(endpoint + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 5;
                if (body != null)
                { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)); request.SetRequestHeader("Content-Type", "application/json"); }
                var operation = request.SendWebRequest(); var clock = Stopwatch.StartNew();
                while (!operation.isDone)
                {
                    if (clock.ElapsedMilliseconds > 7000) throw new InvalidOperationException("Isolated fixture HTTP timed out");
                    Thread.Sleep(5);
                }
                if (request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException("Isolated fixture HTTP request failed, status=" + request.responseCode);
                return request.downloadHandler.text;
            }
        }
        static void Property(object target, string name, object value) => target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value });
        static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private).GetValue(target);
        static object Call(object target,string name,params object[] args) => target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).Invoke(target,args);
        static void Assert(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Native voice tools validation failed: " + reason); checks++; }
    }
}
