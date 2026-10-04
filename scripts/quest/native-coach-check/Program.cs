using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEngine;
using UnityEngine.Networking;

static class Program
{
    static string Endpoint;
    const string Patient = "patient-demo-multi-source";
    static SurgicalCase candidate;
    static int checks, actions, lostResponses;
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [Serializable] public class Created { public string sessionId; public CoachSnapshotState snapshot; }
    [Serializable] public class State { public ExtendedSnapshot snapshot; }
    [Serializable] public class ExtendedSnapshot
    {
        public int version, eventCount, completedCount;
        public string status;
        public bool desynced;
        public int resyncCount;
        public int mistakeCount;
        public bool trackingValid;
        public Progress step;
    }
    [Serializable] public class Progress : CoachInitialStep { public string progressText; }

    static void Main()
    {
        Endpoint = Environment.GetEnvironmentVariable("SCALPAL_COACH_TEST_ENDPOINT");
        Uri endpoint;
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1"
            || endpoint.AbsolutePath != "/" || endpoint.Query != "" || endpoint.UserInfo != "")
            throw new Exception("Harness requires an isolated loopback endpoint from run.py");
        Endpoint = Endpoint.TrimEnd('/');
        var bundle = JsonUtility.FromJson<ScalpalBundle>(File.ReadAllText("apps/quest/Assets/Scalpal/Exercises/Resources/scalpal_bundle.json"));
        candidate = bundle.cases.Single(item => item.patientId == Patient && item.procedureId == "lap_appendectomy");
        var liveCase = JsonUtility.FromJson<SurgicalCase>(Http("GET", "/patients/" + Patient + "/case"));
        Check(liveCase.caseId == candidate.caseId && liveCase.procedure.steps.Select(x => x.id).SequenceEqual(candidate.procedure.steps.Select(x => x.id)),
            "packaged native case matches actual local service case");
        Adoption();
        Playthrough();
        QueuedBatch();
        AckRetries();
        DeliveryFailures();
        SessionBoundaries();
        AdversarialReceipts();
        Console.WriteLine("SCALPAL_NATIVE_COACH_VALIDATION_OK checks=" + checks + " actions=" + actions + " lostResponses=" + lostResponses
            + " production CoachRelay/CaseRunner + isolated actual Hono coach; coroutine/network timing doubles; no Unity/XR/provider execution");
    }

    static void Adoption()
    {
        string sid = Create();
        Http("POST", Path(sid) + "/events", "{\"events\":[{\"type\":\"tracking\",\"valid\":false,\"eventId\":\"test-initial-pause\"}]}");
        var untouched = Snapshot(sid);
        Check(untouched.version > 0 && untouched.status == "paused" && untouched.eventCount == 0, "real tracking pause changes version while untouched");
        foreach (var wrong in new[] { "case", "mode", "step", "patient", "procedure" })
        {
            Scheduler.Reset();
            var relay = Relay();
            relay.AdoptSession(sid, wrong == "patient" ? "wrong-patient" : Patient,
                wrong == "procedure" ? "wrong-procedure" : candidate.procedureId,
                wrong == "case" ? "wrong-case" : candidate.caseId,
                wrong == "mode" ? "mixed_reality" : "virtual",
                wrong == "step" ? "wrong-initial-step" : candidate.procedure.firstStep);
            Pump(); Deliver(Pending("GET", Path(sid))); Pump();
            Check(!relay.Connected && !relay.IsSynchronized && relay.SyncFailureReason.Length > 0, "exact " + wrong + " mismatch fails explicit adoption");
            Check(!Scheduler.Requests.Any(r => r.method == "POST"), "rejected adoption sends no scoring/tracking events");
        }
        var matched = Adopt(sid);
        Check(matched.IsSynchronized && matched.SessionMode == "virtual" && matched.SessionCaseId == candidate.caseId,
            "real paused version-nonzero fresh session adopts with exact native identities");
        EnableTracking(matched);
        matched.Forward(CaseEvent.PlacePort("umbilical"), candidate.procedure.firstStep);
        Pump(); Deliver(Pending("POST", Path(sid) + "/events")); Pump();
        Check(Snapshot(sid).eventCount == 1, "adopted session scored one actual event");
        Scheduler.Reset();
        var reused = Relay();
        Pair(reused, sid); Pump(); Deliver(Pending("GET", Path(sid))); Pump();
        Check(!reused.Connected && reused.SyncFailureReason.Length > 0, "progressed server session cannot be adopted as a fresh attempt");
    }

    static void Playthrough()
    {
        string sid = Create();
        var relay = Adopt(sid); EnableTracking(relay);
        var runner = new CaseRunner(candidate.procedure);
        int completed = 0;
        runner.StepCompleted += _ => completed++;
        while (!runner.Completed)
        {
            var before = runner.Current;
            var events = CaseRunner.PerfectEvents(before).ToArray();
            foreach (var action in events)
            {
                string beforeStep = runner.Current.id;
                var result = runner.Handle(action);
                relay.Forward(action, beforeStep); Pump();
                var request = Pending("POST", Path(sid) + "/events");
                var batch = JsonUtility.FromJson<CoachEventBatch>(Body(request));
                Check(batch.events.Length == 1 && batch.events[0].stepId == beforeStep && batch.events[0].eventId.Length > 0,
                    "actual relay captures a persistent ID and the BEFORE-step for this authored action");
                if (before.check.type == "apply_count" || result.advanced)
                {
                    string exactBody = Body(request);
                    // Server commits, but Unity never sees this response. Retrying must be identical.
                    Send(request); request.Complete("", false); Pump(true);
                    Check(!relay.IsSynchronized, "uncertain delivery pauses new local scoring");
                    var retry = Pending("POST", Path(sid) + "/events");
                    Check(Body(retry) == exactBody, "response loss retries byte-identical event IDs and BEFORE-step");
                    var receipt = JsonUtility.FromJson<CoachEventResponse>(Deliver(retry)); Pump();
                    Check(receipt.results.All(item => item.accepted && !item.applied && item.reason == "duplicate"), "actual server confirms retry as delivered duplicate");
                    lostResponses++;
                }
                else { Deliver(request); Pump(); }
                actions++;
                var snapshot = Snapshot(sid);
                Check(relay.IsSynchronized && snapshot.eventCount == actions && snapshot.completedCount == completed,
                    "actual local/server counts stay equal after delayed/duplicate delivery");
                Check(snapshot.step.id == (runner.Current == null ? "" : runner.Current.id) && snapshot.mistakeCount == runner.Mistakes.Count && !snapshot.desynced,
                    "actual server and packaged CaseRunner have matching step/mistakes without synthetic resync");
            }
        }
        Check(Snapshot(sid).status == "completed" && completed == candidate.procedure.steps.Length, "actual HTTP/local appendectomy both reach completion");
        relay.Forward(CaseEvent.Confirm(), ""); Pump();
        var done = JsonUtility.FromJson<CoachEventResponse>(Deliver(Pending("POST", Path(sid) + "/events"))); Pump();
        Check(done.results[0].accepted && !done.results[0].applied && done.results[0].reason == "case_completed" && relay.IsSynchronized,
            "completed delivered receipt is acknowledged without rescoring");
    }

    static void AckRetries()
    {
        string sid = Create();
        var relay = Adopt(sid);
        var command = JsonUtility.FromJson<CoachCommandResponse>(Http("POST", Path(sid) + "/commands", "{\"action\":\"highlight\",\"structure\":\"appendix\"}")).command;
        int effects = 0;
        relay.CommandRequested += c => { effects++; relay.Ack(c.commandId, true, "synthetic scene accepted"); };
        var poll = Pending("GET", Path(sid) + "/commands");
        string pendingBody = Deliver(poll); Pump();
        string ackPath = Path(sid) + "/commands/" + command.commandId + "/ack";
        var first = Pending("POST", ackPath); string exact = Body(first);
        Send(first); first.Complete("", false); Pump(true);
        var second = Pending("POST", ackPath);
        Check(Body(second) == exact && effects == 1 && !Get<HashSet<string>>(relay, "acknowledgedCommands").Contains(command.commandId),
            "uncertain ack retains its exact result and does not mark seen early");
        // Poll repeats a stale pending snapshot while the acknowledgement retries.
        Pending("GET", Path(sid) + "/commands").Complete(pendingBody); Pump(true);
        Check(effects == 1, "duplicate pending command never repeats scene effects during ack retry");
        second.Complete("", false); Pump(true);
        var third = Pending("POST", ackPath);
        Check(Body(third) == exact, "third acknowledgement attempt preserves result");
        Deliver(third); Pump();
        Check(Get<HashSet<string>>(relay, "acknowledgedCommands").Contains(command.commandId)
            && Get<Dictionary<string, CoachAck>>(relay, "pendingAcks").Count == 0, "real idempotent ack reply confirms delivery");
        int posts = Scheduler.Requests.Count(r => r.url.EndsWith(ackPath));
        relay.Ack(command.commandId, true); Pump();
        Check(posts == 3 && Scheduler.Requests.Count(r => r.url.EndsWith(ackPath)) == posts && effects == 1, "confirmed command has exactly three ACK attempts and one scene effect");
        var terminal = JsonUtility.FromJson<CoachCommandResponse>(Http("POST", ackPath, "{\"status\":\"rejected\",\"reason\":\"late inconsistent retry\"}")).command;
        Check(terminal.status == "applied" && terminal.reason == "synthetic scene accepted", "terminal command result cannot be overwritten by an inconsistent late acknowledgement");

        sid = Create(); relay = Adopt(sid);
        command = JsonUtility.FromJson<CoachCommandResponse>(Http("POST", Path(sid) + "/commands", "{\"action\":\"highlight\",\"structure\":\"appendix\"}")).command;
        effects = 0;
        relay.CommandRequested += c => { effects++; relay.Ack(c.commandId, false, "synthetic geometry unavailable"); };
        Deliver(Pending("GET", Path(sid) + "/commands")); Pump();
        ackPath = Path(sid) + "/commands/" + command.commandId + "/ack";
        for (int retry = 0; retry < 3; retry++) { Pending("POST", ackPath).Complete("", false); Pump(true); }
        Check(!relay.IsSynchronized && relay.SyncFailureReason.Length > 0 && effects == 1,
            "exhausted rejected-result acknowledgement fails closed without repeating the scene decision");
        int before = Scheduler.Requests.Count;
        relay.Ack(command.commandId, false); Pump(true);
        Check(Scheduler.Requests.Count == before && !Get<HashSet<string>>(relay, "acknowledgedCommands").Contains(command.commandId),
            "uncertain exhausted command is never falsely acknowledged or retried a fourth time");
    }

    static void QueuedBatch()
    {
        string sid = Create(); var relay = Adopt(sid); EnableTracking(relay);
        var runner = new CaseRunner(candidate.procedure);
        int completed = 0; runner.StepCompleted += _ => completed++;
        var queuedIds = new HashSet<string>();
        foreach (string port in new[] { "umbilical", "left_lower", "suprapubic" })
        {
            string step = runner.Current.id;
            runner.Handle(CaseEvent.PlacePort(port)); relay.Forward(CaseEvent.PlacePort(port), step); Pump();
        }
        // Only the first request is in flight; the two later intentional actions remain queued.
        var first = Pending("POST", Path(sid) + "/events");
        Check(JsonUtility.FromJson<CoachEventBatch>(Body(first)).events.Length == 1, "in-flight request never absorbs later queued events");
        Deliver(first); Pump();
        var batch = Pending("POST", Path(sid) + "/events");
        var events = JsonUtility.FromJson<CoachEventBatch>(Body(batch)).events;
        Check(events.Length == 2 && events.All(item => item.stepId == "working_ports" && queuedIds.Add(item.eventId)),
            "later actions remain a distinct ordered batch with original step IDs");
        string exact = Body(batch); Send(batch); batch.Complete("", false); Pump(true);
        var retry = Pending("POST", Path(sid) + "/events");
        Check(Body(retry) == exact, "whole committed batch retries the same two identities in order");
        var receipts = JsonUtility.FromJson<CoachEventResponse>(Deliver(retry)); Pump();
        Check(receipts.results.Length == 2 && receipts.results.All(item => item.accepted && !item.applied && item.reason == "duplicate"),
            "real server returns independent delivered duplicate receipts for a lost batch response");
        Check(Snapshot(sid).eventCount == 3 && Snapshot(sid).completedCount == completed && Snapshot(sid).step.id == runner.Current.id && relay.IsSynchronized,
            "queued batch retry leaves real server/local progression equal");
    }

    static void DeliveryFailures()
    {
        foreach (bool malformed in new[] { false, true })
        {
            string sid = Create(); var relay = Adopt(sid); EnableTracking(relay);
            relay.Forward(CaseEvent.PlacePort("umbilical"), candidate.procedure.firstStep); Pump();
            string exact = Body(Pending("POST", Path(sid) + "/events"));
            int failures = 0; relay.SyncFailed += _ => failures++;
            for (int retry = 0; retry < 3; retry++)
            {
                var request = Pending("POST", Path(sid) + "/events");
                Check(Body(request) == exact, "bounded failed request retains original event identity");
                request.Complete(malformed ? "{\"results\":[]}" : "", malformed); Pump(true);
            }
            Check(!relay.IsSynchronized && relay.SyncFailureReason.Length > 0 && failures == 1, "transport/receipt shape exhaustion fails closed once");
            int count = Scheduler.Requests.Count;
            relay.Forward(CaseEvent.PlacePort("left_lower"), "working_ports"); relay.Tracking(false); Pump(true);
            Check(Scheduler.Requests.Count == count, "failed session accepts no future events or fourth retry");
        }
        string invalidSid = Create(); var invalidRelay = Adopt(invalidSid); EnableTracking(invalidRelay);
        invalidRelay.Forward(CaseEvent.PlacePort("umbilical"), candidate.procedure.firstStep); Pump();
        Pending("POST", Path(invalidSid) + "/events").Complete("{\"results\":[{\"accepted\":false,\"applied\":false,\"reason\":\"invalid: synthetic rejection\"}]}"); Pump();
        Check(!invalidRelay.IsSynchronized && invalidRelay.SyncFailureReason.Length > 0, "well-shaped rejected receipt fails closed immediately");
    }

    static void SessionBoundaries()
    {
        string oldSid = Create(), nextSid = Create();
        var relay = Adopt(oldSid); EnableTracking(relay);
        relay.Forward(CaseEvent.PlacePort("umbilical"), candidate.procedure.firstStep); Pump();
        var oldRequest = Pending("POST", Path(oldSid) + "/events");
        Pair(relay, nextSid); Pump(); Deliver(Pending("GET", Path(nextSid))); Pump();
        Check(relay.SessionId == nextSid && !relay.IsSynchronized, "replacement owns its own unsynchronized state");
        oldRequest.Complete("", false); Pump(true);
        Check(relay.SyncFailureReason == "", "old delivery failure cannot poison replacement generation");
        var nextRequest = Pending("POST", Path(nextSid) + "/events");
        Check(!Body(nextRequest).Contains("umbilical"), "old queued scoring does not cross coach adoption");
        Deliver(nextRequest); Pump(); Check(relay.IsSynchronized, "replacement synchronizes independently");
        var oldPoll = Pending("GET", Path(oldSid) + "/commands");
        int effects = 0; relay.CommandRequested += _ => effects++;
        oldPoll.Complete("{\"commands\":[{\"commandId\":\"synthetic-old\",\"status\":\"pending\",\"action\":\"highlight\",\"targetId\":\"appendix\"}]}"); Pump();
        Check(effects == 0, "old poll cannot emit a scene effect into replacement session");
        var nextPoll = Pending("GET", Path(nextSid) + "/commands");
        relay.isActiveAndEnabled = false;
        typeof(CoachRelay).GetMethod("OnDisable", Private).Invoke(relay, null);
        nextPoll.Complete("{\"commands\":[{\"commandId\":\"synthetic-disabled\",\"status\":\"pending\"}]}"); Pump(true);
        Check(!relay.Connected && !relay.IsSynchronized && effects == 0, "disable clears ownership and suppresses delayed commands");
    }

    static void AdversarialReceipts()
    {
        string sid = Create(); var relay = Adopt(sid); EnableTracking(relay);
        var runner = new CaseRunner(candidate.procedure);
        // Demonstrates the caller's required registration gate: scoring locally while server
        // tracking is paused loses this event even though the transport receipt is delivered.
        relay.Tracking(false); Pump(); Deliver(Pending("POST", Path(sid) + "/events")); Pump();
        runner.Handle(CaseEvent.PlacePort("umbilical"));
        relay.Forward(CaseEvent.PlacePort("umbilical"), candidate.procedure.firstStep); Pump();
        var outcome = JsonUtility.FromJson<CoachEventResponse>(Deliver(Pending("POST", Path(sid) + "/events"))); Pump();
        Check(outcome.results[0].accepted && !outcome.results[0].applied && outcome.results[0].reason == "tracking_invalid" && relay.IsSynchronized,
            "tracking-invalid receipt is delivered but does not prove local/server scoring parity");
        Check(runner.Current.id != Snapshot(sid).step.id, "adversarial missing caller gate demonstrates actual scoring divergence");
        Console.WriteLine("OBSERVED_COACH_CALLER_BOUNDARY tracking_invalid is delivered while local scoring can diverge if registration ordering is bypassed");
        EnableTracking(relay);
        // The normal next BEFORE-step metadata reconciles a behind coach to headset authority.
        string recoveredStep = runner.Current.id;
        var action = CaseRunner.PerfectEvents(runner.Current).First(); runner.Handle(action);
        relay.Forward(action, recoveredStep); Pump(); Deliver(Pending("POST", Path(sid) + "/events")); Pump();
        Check(Snapshot(sid).resyncCount == 1 && Snapshot(sid).step.id == runner.Current.id, "future correct BEFORE-step silently reconciles dropped prior progress");
        relay.Forward(CaseEvent.PlacePort("umbilical"), candidate.procedure.firstStep); Pump();
        int scoredBefore = Snapshot(sid).eventCount;
        var stale = JsonUtility.FromJson<CoachEventResponse>(Deliver(Pending("POST", Path(sid) + "/events"))); Pump();
        Check(!stale.results[0].accepted && !stale.results[0].applied && stale.results[0].reason == "step_desynchronized"
            && Snapshot(sid).eventCount == scoredBefore && !relay.IsSynchronized && relay.SyncFailureReason.Length > 0,
            "stale BEFORE-step rejects server scoring and forces actual relay synchronization failure");

        sid = Create(); relay = Adopt(sid); EnableTracking(relay);
        string before = Snapshot(sid).step.id;
        relay.Forward(CaseEvent.PlacePort("umbilical"), "synthetic-unknown-step"); Pump();
        var unknownRequest = Pending("POST", Path(sid) + "/events");
        string original = Body(unknownRequest);
        var initialRejection = JsonUtility.FromJson<CoachEventResponse>(Send(unknownRequest));
        Check(!initialRejection.results[0].accepted && initialRejection.results[0].reason == "step_desynchronized",
            "actual server rejects unknown metadata before its acknowledgement is lost");
        unknownRequest.Complete("", false); Pump(true);
        Check(!relay.IsSynchronized, "lost rejection receipt never restores relay synchronization");
        var unknownRetry = Pending("POST", Path(sid) + "/events");
        Check(Body(unknownRetry) == original, "unknown-step retry retains identical rejected event identity");
        var unknown = JsonUtility.FromJson<CoachEventResponse>(Deliver(unknownRetry)); Pump();
        Check(!unknown.results[0].accepted && !unknown.results[0].applied && unknown.results[0].reason == "step_desynchronized"
            && Snapshot(sid).eventCount == 0 && Snapshot(sid).step.id == before && !relay.IsSynchronized,
            "lost unknown-step rejection stays rejected on retry instead of becoming an accepted duplicate");
    }

    static string Create() { return JsonUtility.FromJson<Created>(Http("POST", "/coach/sessions", "{\"patientId\":\"" + Patient + "\",\"mode\":\"virtual\"}")).sessionId; }
    static void Pair(CoachRelay relay, string sid) { relay.AdoptSession(sid, Patient, candidate.procedureId, candidate.caseId, "virtual", candidate.procedure.firstStep); }
    static CoachRelay Adopt(string sid)
    {
        Scheduler.Reset(); var relay = Relay(); Pair(relay, sid); Pump();
        Deliver(Pending("GET", Path(sid))); Pump();
        Check(relay.Connected && !relay.IsSynchronized, "fetch alone never enables scoring before tracking receipt");
        Deliver(Pending("POST", Path(sid) + "/events")); Pump();
        Check(relay.IsSynchronized, "actual server receipt synchronizes adopted relay");
        return relay;
    }
    static CoachRelay Relay() { var relay = new CoachRelay(); relay.ConfigureEndpoint(Endpoint); return relay; }
    static void EnableTracking(CoachRelay relay)
    {
        relay.Tracking(true); Pump(); Deliver(Pending("POST", Path(relay.SessionId) + "/events")); Pump();
        Check(Snapshot(relay.SessionId).trackingValid, "true registration reaches actual server in FIFO order");
    }
    static ExtendedSnapshot Snapshot(string sid) { return JsonUtility.FromJson<State>(Http("GET", Path(sid))).snapshot; }
    static string Path(string sid) { return "/coach/sessions/" + sid; }
    static void Pump(bool clocks = false) { for (int i = 0; i < 5; i++) Scheduler.Pump(clocks); }
    static UnityWebRequest Pending(string method, string tail)
    {
        var request = Scheduler.Requests.LastOrDefault(r => r.method == method && r.url.EndsWith(tail) && r.result == UnityWebRequest.Result.InProgress);
        if (request == null) throw new Exception("Expected pending " + method + " transport request was absent");
        return request;
    }
    static string Body(UnityWebRequest request) { return request.uploadHandler == null ? null : Encoding.UTF8.GetString(request.uploadHandler.data); }
    static string Send(UnityWebRequest request) { return Http(request.method, request.url.Substring(Endpoint.Length), Body(request)); }
    static string Deliver(UnityWebRequest request) { string json = Send(request); request.Complete(json); return json; }
    static string Http(string method, string path, string body = null)
    {
        // Fixed loopback destination. Never follows redirects to a provider endpoint.
        if (!path.StartsWith("/") || path.Any(char.IsWhiteSpace) || path.Contains("\"") || path.Contains("\\"))
            throw new Exception("Invalid local validation route");
        var info = new ProcessStartInfo("/usr/bin/curl", "-fsS --noproxy '*' --max-time 5 --max-redirs 0 -X " + method
            + " -H \"Content-Type: application/json\" " + (body == null ? "" : "--data-binary @- ") + Endpoint + path)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (var process = Process.Start(info))
        {
            if (body != null) process.StandardInput.Write(body);
            process.StandardInput.Close();
            string response = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Local coach HTTP validation request failed, curl exit=" + process.ExitCode);
            return response;
        }
    }
    static T Get<T>(object target, string field) { return (T)target.GetType().GetField(field, Private).GetValue(target); }
    static void Check(bool value, string message) { if (!value) throw new Exception("Native coach validation failed: " + message); checks++; }
}
