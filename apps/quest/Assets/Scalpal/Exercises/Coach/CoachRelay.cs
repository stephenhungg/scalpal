// Bridges the headset to Jarvis. Forwards exercise events (the same CaseEvents fed to CaseRunner),
// what the learner is looking at, and registration validity to the coach service, in order. Polls for
// highlight requests from Jarvis; the scene applies them and calls Ack so Jarvis only claims what happened.
// Talks to services/preop /coach routes today; the same messages move to SpacetimeDB rows later.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Scalpal.Exercises.Engine;
using Scalpal.Exercises.Data;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Exercises.Coach
{
    [Serializable]
    public class CoachEventDto
    {
        public string type;
        public string portId;
        public string structureId;
        public string instrumentId;
        public bool valid;
        public string eventId;
        public string stepId;
        public BodyAction evidence;
        public bool active;
        public float rateMlPerMin, totalMl;
    }

    [Serializable]
    public class CoachEventBatch
    {
        public CoachEventDto[] events;
    }

    [Serializable]
    public class CoachCommand
    {
        public string commandId;
        public string action; // "highlight" or "clear_highlight"
        public string targetId; // anatomy id; the mesh is "anat_" + targetId
        public string status;
        public string reason;
        public string requestedAt;
    }

    [Serializable]
    public class CoachCommandList
    {
        public CoachCommand[] commands;
    }

    [Serializable]
    public class CoachAck
    {
        public string status;
        public string reason;
    }

    [Serializable]
    public class CoachCurrent
    {
        public string sessionId;
        public string patientId;
        public string procedureId;
    }

    [Serializable]
    public class CoachEventReceipt
    {
        public bool accepted;
        public bool applied;
        public string reason;
    }

    [Serializable]
    public class CoachEventResponse
    {
        public CoachEventReceipt[] results;
    }

    [Serializable]
    public class CoachSessionState
    {
        public CoachSnapshotState snapshot;
    }

    [Serializable]
    public class CoachSnapshotState
    {
        public BodyGrade bodyGrade;
        public string sessionId;
        public string patientId;
        public string procedureId;
        public string caseId;
        public string mode;
        public string status;
        public int version;
        public int eventCount;
        public int stepNumber;
        public int completedCount;
        public CoachInitialStep step;
    }

    [Serializable]
    public class CoachInitialStep { public string id; }

    [Serializable]
    public class CoachCommandResponse { public CoachCommand command; }

    [Serializable]
    public class CoachAlertDto
    {
        public string id, kind, priority, tier, stepId, say, reflexKey, reflexRoute, simEvent;
        public string[] highlight;
        public int seq, version;
    }

    [Serializable]
    public class CoachAlertFeed
    {
        public CoachAlertDto[] alerts;
        public int latestSeq;
        public CoachSnapshotState snapshot;
    }

    public class CoachRelay : MonoBehaviour
    {
        [Tooltip("Same service as ScalpalPreopService: use the Mac LAN address on Quest.")]
        [SerializeField] string baseUrl = "http://localhost:8787";
        [SerializeField] float commandPollSeconds = 0.5f;
        [SerializeField] int timeoutSeconds = 5;

        readonly Queue<CoachEventDto> pending = new Queue<CoachEventDto>();
        const int MaxDeliveryAttempts = 3;
        readonly HashSet<string> offeredCommands = new HashSet<string>();
        readonly HashSet<string> acknowledgedCommands = new HashSet<string>();
        readonly Dictionary<string, CoachAck> pendingAcks = new Dictionary<string, CoachAck>();
        bool sending;
        bool failed;
        bool trackingValid;
        string lastFocus = "";
        int generation;
        int adoption;

        public string SessionId { get; private set; } = "";
        public string SessionPatientId { get; private set; } = "";
        public string SessionProcedureId { get; private set; } = "";
        public string SessionInitialStepId { get; private set; } = "";
        public string SessionCaseId { get; private set; } = "";
        public string SessionMode { get; private set; } = "";
        public bool Connected => !string.IsNullOrEmpty(SessionId);
        // True after initial tracking state was acknowledged, false after any scoring delivery failure.
        public bool IsSynchronized { get; private set; }
        public bool DeliveryIdle => pending.Count == 0 && !sending;
        public string SyncFailureReason { get; private set; } = "";
        public event Action<CoachCommand> CommandRequested;
        public event Action<string> SessionAdopted;
        public event Action<string> SyncFailed;
        public event Action SessionReset;
        public event Action<CoachAlertDto> AlertReceived;
        public int AlertCursor { get; private set; }
        public CoachSnapshotState AlertSnapshot { get; private set; }

        // A clip must remain on this session's reflex route on the configured service.
        public bool TryReflexUrl(string route, out string url)
        {
            url = "";
            if (!Connected || string.IsNullOrEmpty(route)) return false;
            string prefix = "/jarvis/reflex/" + Uri.EscapeDataString(SessionId) + "/";
            if (!route.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string key = route.Substring(prefix.Length);
            if (key.Length == 0 || key.Length > 120) return false;
            foreach (char c in key) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) return false;
            if (key.Contains("..")) return false;
            url = baseUrl.TrimEnd('/') + route;
            return true;
        }

        public void ConfigureEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Coach endpoint is required.", nameof(url));
            if (baseUrl == url) return;
            UseSession("");
            baseUrl = url.TrimEnd('/');
        }

        public void AdoptCurrentSession(string patientId, string expectedProcedureId = "", string expectedCaseId = "",
            string expectedMode = "", string expectedInitialStepId = "")
        {
            // A new adoption invalidates both outstanding lookup requests and the previous session.
            UseSession("");
            int request = adoption;
            if (!isActiveAndEnabled) return;
            StartCoroutine(Adopt(patientId, expectedProcedureId, expectedCaseId, expectedMode, expectedInitialStepId, request));
        }

        // Native selection pairs a session created for this exact case and presentation mode.
        public void AdoptSession(string sessionId, string patientId, string procedureId, string caseId, string mode, string initialStepId)
        {
            UseSession("");
            if (!isActiveAndEnabled) return;
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(patientId) || string.IsNullOrWhiteSpace(procedureId)
                || string.IsNullOrWhiteSpace(caseId) || string.IsNullOrWhiteSpace(initialStepId)
                || (mode != "virtual" && mode != "mixed_reality"))
            {
                FailSync("Explicit coach pairing requires session, patient, procedure, case, mode and initial step.");
                return;
            }
            StartCoroutine(AdoptKnown(sessionId, patientId, procedureId, caseId, mode, initialStepId, adoption));
        }

        public void UseSession(string sessionId, string patientId = "", string procedureId = "", string initialStepId = "",
            string caseId = "", string mode = "")
        {
            adoption++;
            sessionId = sessionId ?? "";
            if (Connected && SessionId == sessionId && SessionPatientId == (patientId ?? "")
                && SessionProcedureId == (procedureId ?? "") && SessionInitialStepId == (initialStepId ?? "")
                && SessionCaseId == (caseId ?? "") && SessionMode == (mode ?? "")) return;
            generation++;
            AlertCursor = 0;
            AlertSnapshot = null;
            SessionReset?.Invoke();
            SessionId = sessionId;
            SessionPatientId = patientId ?? "";
            SessionProcedureId = procedureId ?? "";
            SessionInitialStepId = initialStepId ?? "";
            SessionCaseId = caseId ?? "";
            SessionMode = mode ?? "";
            pending.Clear();
            offeredCommands.Clear();
            acknowledgedCommands.Clear();
            pendingAcks.Clear();
            sending = false;
            lastFocus = "";
            failed = false;
            IsSynchronized = false;
            SyncFailureReason = "";
            if (!Connected || !isActiveAndEnabled) return;
            // Registration is unknown/invalid until the scene supplies it. Never inherit server true.
            pending.Enqueue(new CoachEventDto { type = "tracking", valid = trackingValid, eventId = NewEventId(), stepId = "" });
            SessionAdopted?.Invoke(SessionId);
            StartFlush();
            StartCoroutine(PollCommands(SessionId, generation));
            StartCoroutine(PollAlerts(SessionId, generation));
        }

        void OnDisable() { UseSession(""); }

        // The caller must gate local scoring on IsSynchronized when a live coach is connected.
        public void Forward(CaseEvent e, string stepId = "")
        {
            if (!Connected || !IsSynchronized || failed) return;
            switch (e.type)
            {
                case CaseEventType.Surgery:
                    // Copy now: later physical samples must never mutate an already queued retry body.
                    if (e.evidence == null) return;
                    var evidence = JsonUtility.FromJson<BodyAction>(JsonUtility.ToJson(e.evidence));
                    pending.Enqueue(new CoachEventDto { type = "surgery", evidence = evidence,
                        eventId = NewEventId(), stepId = stepId ?? "" });
                    StartFlush();
                    break;
                case CaseEventType.PlacePort: Enqueue("place_port", portId: e.id, stepId: stepId); break;
                case CaseEventType.Touch: Enqueue("touch", structureId: e.id, instrumentId: e.instrumentId, stepId: stepId); break;
                case CaseEventType.Identify: Enqueue("identify", structureId: e.id, stepId: stepId); break;
                case CaseEventType.Confirm: Enqueue("confirm", stepId: stepId); break;
                case CaseEventType.Finish: Enqueue("finish", stepId: stepId); break;
            }
        }

        public void Bleeding(string structureId, bool active, float rateMlPerMin, float totalMl)
        {
            if (!Connected || !IsSynchronized || failed || string.IsNullOrEmpty(structureId)
                || float.IsNaN(rateMlPerMin) || float.IsInfinity(rateMlPerMin) || rateMlPerMin < 0
                || float.IsNaN(totalMl) || float.IsInfinity(totalMl) || totalMl < 0) return;
            pending.Enqueue(new CoachEventDto { type = "bleeding", structureId = structureId, active = active,
                rateMlPerMin = rateMlPerMin, totalMl = totalMl, eventId = NewEventId(), stepId = "" });
            StartFlush();
        }

        public void Focus(string structureId)
        {
            if (!Connected || failed) return;
            structureId = structureId ?? "";
            if (structureId == lastFocus) return;
            lastFocus = structureId;
            Enqueue("focus", structureId: structureId);
        }

        public void Tracking(bool valid)
        {
            if (valid == trackingValid) return;
            trackingValid = valid;
            if (Connected && !failed) Enqueue("tracking", valid: valid);
        }

        public void Ack(string commandId, bool applied, string reason = "")
        {
            // Keep an offered command pending until its acknowledgement is confirmed.
            // Polling must not apply the scene effect again while this same result retries.
            if (!Connected || failed || !offeredCommands.Contains(commandId ?? "") || pendingAcks.ContainsKey(commandId)) return;
            var acknowledgement = new CoachAck { status = applied ? "applied" : "rejected", reason = reason ?? "" };
            pendingAcks.Add(commandId, acknowledgement);
            StartCoroutine(SendAck(SessionId, generation, commandId, acknowledgement));
        }

        IEnumerator SendAck(string sid, int epoch, string commandId, CoachAck acknowledgement)
        {
            var body = JsonUtility.ToJson(acknowledgement);
            for (int attempt = 0; attempt < MaxDeliveryAttempts; attempt++)
            {
                if (!Current(sid, epoch) || failed) yield break;
                string json = null;
                yield return Request("POST", SessionPath(sid) + "/commands/" + Uri.EscapeDataString(commandId) + "/ack", body, r => json = r);
                if (!Current(sid, epoch) || failed) yield break;
                CoachCommandResponse response = null;
                try { if (json != null) response = JsonUtility.FromJson<CoachCommandResponse>(json); }
                catch (ArgumentException) { }
                if (response?.command != null && response.command.commandId == commandId && response.command.status == acknowledgement.status)
                {
                    acknowledgedCommands.Add(commandId);
                    offeredCommands.Remove(commandId);
                    pendingAcks.Remove(commandId);
                    yield break;
                }
                if (attempt + 1 < MaxDeliveryAttempts) yield return new WaitForSeconds(0.25f * (attempt + 1));
            }
            FailSync("command acknowledgement failed after three attempts; start a new coach attempt");
        }

        static string NewEventId() => "unity-" + Guid.NewGuid().ToString("N");

        void Enqueue(string type, string portId = "", string structureId = "", string instrumentId = "", bool valid = false, string stepId = "")
        {
            if (!Connected || failed) return;
            pending.Enqueue(new CoachEventDto { type = type, portId = portId, structureId = structureId, instrumentId = instrumentId,
                valid = valid, eventId = NewEventId(), stepId = stepId ?? "" });
            StartFlush();
        }

        void StartFlush()
        {
            if (sending || !Connected || failed || pending.Count == 0) return;
            sending = true;
            StartCoroutine(Flush(SessionId, generation));
        }

        IEnumerator Flush(string sid, int epoch)
        {
            while (pending.Count > 0 && Current(sid, epoch) && !failed)
            {
                // Keep these DTOs queued with their original IDs until every receipt confirms
                // delivery. Retrying the same body cannot count a clip/touch twice on the server.
                var events = pending.ToArray();
                var body = JsonUtility.ToJson(new CoachEventBatch { events = events });
                bool delivered = false;
                for (int attempt = 0; attempt < MaxDeliveryAttempts; attempt++)
                {
                    if (!Current(sid, epoch) || failed) yield break;
                    string json = null;
                    yield return Request("POST", SessionPath(sid) + "/events", body, r => json = r);
                    if (!Current(sid, epoch) || failed) yield break;
                    CoachEventResponse response = null;
                    try { if (json != null) response = JsonUtility.FromJson<CoachEventResponse>(json); }
                    catch (ArgumentException) { }
                    if (response?.results != null && response.results.Length == events.Length)
                    {
                        delivered = true;
                        foreach (var result in response.results)
                            if (!ReceiptDelivered(result)) { delivered = false; break; }
                        if (!delivered)
                        {
                            FailSync("coach returned a malformed or rejected event receipt; start a new attempt");
                            break;
                        }
                        for (int i = 0; i < events.Length; i++) pending.Dequeue();
                        IsSynchronized = true;
                        break;
                    }
                    // Pause new local scoring during an uncertain delivery; already queued
                    // events are retried in order. A successful duplicate receipt restores sync.
                    IsSynchronized = false;
                    if (attempt + 1 < MaxDeliveryAttempts) yield return new WaitForSeconds(0.25f * (attempt + 1));
                }
                if (!delivered && !failed)
                    FailSync("coach event delivery failed after three attempts; start a new attempt");
            }
            if (Current(sid, epoch)) sending = false;
        }

        static bool ReceiptDelivered(CoachEventReceipt receipt)
        {
            if (receipt == null || !receipt.accepted || receipt.reason == null) return false;
            if (receipt.applied) return receipt.reason == "";
            return receipt.reason == "duplicate" || receipt.reason == "tracking_invalid" || receipt.reason == "case_completed";
        }

        void FailSync(string reason)
        {
            if (failed) return;
            failed = true;
            IsSynchronized = false;
            pending.Clear();
            SyncFailureReason = reason;
            SyncFailed?.Invoke(reason);
            Debug.LogWarning("CoachRelay: " + reason);
        }

        IEnumerator Adopt(string patientId, string expectedProcedureId, string expectedCaseId,
            string expectedMode, string expectedInitialStepId, int request)
        {
            string json = null;
            yield return Request("GET", "/coach/current?patientId=" + Uri.EscapeDataString(patientId ?? ""), null, r => json = r);
            if (request != adoption || !isActiveAndEnabled) yield break;
            CoachCurrent current = null;
            try { if (json != null) current = JsonUtility.FromJson<CoachCurrent>(json); }
            catch (ArgumentException) { }
            if (current == null || string.IsNullOrEmpty(current.sessionId) || current.patientId != patientId
                || (!string.IsNullOrEmpty(expectedProcedureId) && current.procedureId != expectedProcedureId))
            {
                FailSync("no matching live Jarvis session for this patient and procedure; start it on the laptop");
                yield break;
            }
            yield return AdoptKnown(current.sessionId, patientId, current.procedureId, expectedCaseId, expectedMode, expectedInitialStepId, request);
        }

        IEnumerator AdoptKnown(string sessionId, string patientId, string procedureId, string caseId,
            string mode, string initialStepId, int request)
        {
            if (request != adoption || !isActiveAndEnabled) yield break;
            string stateJson = null;
            yield return Request("GET", SessionPath(sessionId), null, r => stateJson = r);
            if (request != adoption || !isActiveAndEnabled) yield break;
            CoachSessionState state = null;
            try { if (stateJson != null) state = JsonUtility.FromJson<CoachSessionState>(stateJson); }
            catch (ArgumentException) { }
            var snapshot = state?.snapshot;
            if (snapshot == null || snapshot.sessionId != sessionId || snapshot.patientId != patientId
                || snapshot.procedureId != procedureId || string.IsNullOrEmpty(snapshot.caseId)
                || (!string.IsNullOrEmpty(caseId) && snapshot.caseId != caseId)
                || (snapshot.mode != "virtual" && snapshot.mode != "mixed_reality")
                || (!string.IsNullOrEmpty(mode) && snapshot.mode != mode)
                || snapshot.eventCount != 0 || snapshot.completedCount != 0 || snapshot.stepNumber != 1
                || (snapshot.status != "active" && snapshot.status != "paused")
                || string.IsNullOrEmpty(snapshot.step?.id)
                || (!string.IsNullOrEmpty(initialStepId) && snapshot.step.id != initialStepId))
            {
                FailSync("Jarvis session does not match this untouched case, presentation mode and initial step; start a new session");
                yield break;
            }
            // Highlight/tracking operations may increment version while eventCount remains zero.
            UseSession(sessionId, patientId, procedureId, snapshot.step.id, snapshot.caseId, snapshot.mode);
        }

        IEnumerator PollCommands(string sid, int epoch)
        {
            var wait = new WaitForSeconds(commandPollSeconds);
            while (Current(sid, epoch) && !failed)
            {
                string json = null;
                yield return Request("GET", SessionPath(sid) + "/commands", null, r => json = r);
                if (!Current(sid, epoch) || failed) yield break;
                CoachCommandList list = null;
                try { if (json != null) list = JsonUtility.FromJson<CoachCommandList>(json); }
                catch (ArgumentException) { }
                foreach (var c in list?.commands ?? new CoachCommand[0])
                {
                    if (!Current(sid, epoch) || failed) yield break;
                    if (c == null || string.IsNullOrEmpty(c.commandId) || c.status != "pending"
                        || acknowledgedCommands.Contains(c.commandId) || offeredCommands.Contains(c.commandId)) continue;
                    offeredCommands.Add(c.commandId);
                    CommandRequested?.Invoke(c);
                }
                yield return wait;
            }
        }

        IEnumerator PollAlerts(string sid, int epoch)
        {
            var wait = new WaitForSecondsRealtime(Mathf.Max(.25f, commandPollSeconds));
            while (Current(sid, epoch) && !failed)
            {
                if (!IsSynchronized) { yield return wait; continue; }
                string json = null;
                yield return Request("GET", SessionPath(sid) + "/alerts?after=" + AlertCursor, null, value => json = value);
                if (!Current(sid, epoch) || failed) yield break;
                CoachAlertFeed feed = null;
                try { if (json != null) feed = JsonUtility.FromJson<CoachAlertFeed>(json); }
                catch (ArgumentException) { }
                if (IsSynchronized && ValidAlertFeed(feed, sid, AlertCursor))
                {
                    AlertSnapshot = feed.snapshot;
                    foreach (var alert in feed.alerts ?? Array.Empty<CoachAlertDto>())
                    {
                        if (!Current(sid, epoch) || failed) yield break;
                        if (alert == null || alert.seq <= AlertCursor || alert.seq > feed.latestSeq) continue;
                        AlertCursor = alert.seq;
                        AlertReceived?.Invoke(alert);
                    }
                    AlertCursor = feed.latestSeq;
                }
                yield return wait;
            }
        }

        // Validate the entire page before moving its cursor; a malformed page is retried intact.
        public static bool ValidAlertFeed(CoachAlertFeed feed, string sessionId, int cursor)
        {
            if (feed?.snapshot == null || feed.snapshot.sessionId != sessionId || feed.latestSeq < cursor
                || feed.snapshot.version < 0 || feed.alerts == null) return false;
            int previous = cursor;
            foreach (var alert in feed.alerts)
            {
                if (alert == null || alert.seq <= previous || alert.seq > feed.latestSeq
                    || alert.version < 0 || alert.version > feed.snapshot.version || string.IsNullOrEmpty(alert.id)
                    || string.IsNullOrEmpty(alert.say)) return false;
                previous = alert.seq;
            }
            return previous == feed.latestSeq;
        }

        bool Current(string sid, int epoch) => isActiveAndEnabled && Connected && SessionId == sid && generation == epoch;
        static string SessionPath(string sid) => "/coach/sessions/" + Uri.EscapeDataString(sid);

        IEnumerator Request(string method, string path, string body, Action<string> onOk)
        {
            using (var req = new UnityWebRequest(baseUrl.TrimEnd('/') + path, method))
            {
                req.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.timeout = timeoutSeconds;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success) onOk?.Invoke(req.downloadHandler.text);
                else Debug.LogWarning($"CoachRelay: {method} {path} failed ({req.responseCode}).");
            }
        }
    }
}
