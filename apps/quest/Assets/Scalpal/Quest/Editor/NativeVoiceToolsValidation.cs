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
                Assert(candidate.procedureId == NativeCaseSession.ProcedureId, "isolated actual service supplies native appendectomy case");
                string sid = JsonUtility.FromJson<Created>(Http(endpoint, "POST", "/coach/sessions",
                    "{\"patientId\":\"" + candidate.patientId + "\",\"mode\":\"virtual\"}")).sessionId;
                // Make another case the global latest session. Native calls must still use sid.
                string otherSid = JsonUtility.FromJson<Created>(Http(endpoint, "POST", "/coach/sessions",
                    "{\"patientId\":\"patient-demo-001\",\"mode\":\"virtual\"}")).sessionId;
                Assert(!string.IsNullOrEmpty(sid) && otherSid != sid, "two independent synthetic coach sessions exist");

                fixture = new GameObject("NativeVoiceToolsFixture");
                fixture.SetActive(false);
                voice = fixture.AddComponent<QuestJarvisVoice>();
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
        static void Assert(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Native voice tools validation failed: " + reason); checks++; }
    }
}
