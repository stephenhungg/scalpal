// Bridges the headset to Jarvis. Forwards exercise events (the same CaseEvents fed to CaseRunner),
// what the learner is looking at, and registration validity to the coach service, in order. Polls for
// highlight requests from Jarvis; the scene applies them and calls Ack so Jarvis only claims what happened.
// Talks to services/preop /coach routes today; the same messages move to SpacetimeDB rows later.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Scalpal.Exercises.Engine;
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
        public string sessionId;
        public string patientId;
        public string procedureId;
        public string status;
        public int version;
        public int stepNumber;
        public int completedCount;
        public CoachInitialStep step;
    }

    [Serializable]
    public class CoachInitialStep { public string id; }

    public class CoachRelay : MonoBehaviour
    {
        [Tooltip("Same service as ScalpalPreopService: use the Mac LAN address on Quest.")]
        [SerializeField] string baseUrl = "http://localhost:8787";
        [SerializeField] float commandPollSeconds = 0.5f;
        [SerializeField] int timeoutSeconds = 5;

        readonly Queue<CoachEventDto> pending = new Queue<CoachEventDto>();
        readonly HashSet<string> offeredCommands = new HashSet<string>();
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
        public bool Connected => !string.IsNullOrEmpty(SessionId);
        // True after initial tracking state was acknowledged, false after any scoring delivery failure.
        public bool IsSynchronized { get; private set; }
        public string SyncFailureReason { get; private set; } = "";
        public event Action<CoachCommand> CommandRequested;
        public event Action<string> SessionAdopted;
        public event Action<string> SyncFailed;

        public void ConfigureEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("Coach endpoint is required.", nameof(url));
            if (baseUrl == url) return;
            UseSession("");
            baseUrl = url.TrimEnd('/');
        }

        public void AdoptCurrentSession(string patientId, string expectedProcedureId = "")
        {
            // A new adoption invalidates both outstanding lookup requests and the previous session.
            UseSession("");
            int request = adoption;
            StartCoroutine(Adopt(patientId, expectedProcedureId, request));
        }

        public void UseSession(string sessionId, string patientId = "", string procedureId = "", string initialStepId = "")
        {
            adoption++;
            sessionId = sessionId ?? "";
            if (Connected && SessionId == sessionId && SessionPatientId == (patientId ?? "")
                && SessionProcedureId == (procedureId ?? "")) return;
            generation++;
            SessionId = sessionId;
            SessionPatientId = patientId ?? "";
            SessionProcedureId = procedureId ?? "";
            SessionInitialStepId = initialStepId ?? "";
            pending.Clear();
            offeredCommands.Clear();
            sending = false;
            lastFocus = "";
            failed = false;
            IsSynchronized = false;
            SyncFailureReason = "";
            if (!Connected) return;
            // Registration is unknown/invalid until the scene supplies it. Never inherit server true.
            pending.Enqueue(new CoachEventDto { type = "tracking", valid = trackingValid });
            SessionAdopted?.Invoke(SessionId);
            StartFlush();
            StartCoroutine(PollCommands(SessionId, generation));
        }

        void OnDisable() { UseSession(""); }

        // The caller must gate local scoring on IsSynchronized when a live coach is connected.
        public void Forward(CaseEvent e)
        {
            if (!Connected || !IsSynchronized || failed) return;
            switch (e.type)
            {
                case CaseEventType.PlacePort: Enqueue("place_port", portId: e.id); break;
                case CaseEventType.Touch: Enqueue("touch", structureId: e.id, instrumentId: e.instrumentId); break;
                case CaseEventType.Identify: Enqueue("identify", structureId: e.id); break;
                case CaseEventType.Confirm: Enqueue("confirm"); break;
            }
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
            // Only acknowledge a command actually offered by this current session's poll.
            if (!Connected || failed || !offeredCommands.Remove(commandId ?? "")) return;
            var body = JsonUtility.ToJson(new CoachAck { status = applied ? "applied" : "rejected", reason = reason ?? "" });
            StartCoroutine(SendAck(SessionId, generation, commandId, body));
        }

        IEnumerator SendAck(string sid, int epoch, string commandId, string body)
        {
            bool ok = false;
            yield return Request("POST", SessionPath(sid) + "/commands/" + Uri.EscapeDataString(commandId) + "/ack", body, _ => ok = true);
            if (Current(sid, epoch) && !ok) FailSync("command acknowledgement failed; restart the coach attempt before resuming");
        }

        void Enqueue(string type, string portId = "", string structureId = "", string instrumentId = "", bool valid = false)
        {
            if (!Connected || failed) return;
            pending.Enqueue(new CoachEventDto { type = type, portId = portId, structureId = structureId, instrumentId = instrumentId, valid = valid });
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
                var events = pending.ToArray();
                pending.Clear();
                string json = null;
                yield return Request("POST", SessionPath(sid) + "/events", JsonUtility.ToJson(new CoachEventBatch { events = events }), r => json = r);
                if (!Current(sid, epoch)) yield break;
                CoachEventResponse response = null;
                try { if (json != null) response = JsonUtility.FromJson<CoachEventResponse>(json); }
                catch (ArgumentException) { }
                bool accepted = response?.results != null && response.results.Length == events.Length;
                if (accepted)
                    foreach (var result in response.results) if (result == null || !result.accepted) accepted = false;
                if (!accepted)
                {
                    // A timeout can occur after the server applied a touch. Retrying could double clips.
                    FailSync("coach event delivery failed or was rejected; local and remote progress may differ. Start a new attempt");
                    break;
                }
                IsSynchronized = true;
            }
            if (Current(sid, epoch)) sending = false;
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

        IEnumerator Adopt(string patientId, string expectedProcedureId, int request)
        {
            string json = null;
            yield return Request("GET", "/coach/current?patientId=" + Uri.EscapeDataString(patientId ?? ""), null, r => json = r);
            if (request != adoption) yield break;
            CoachCurrent current = null;
            try { if (json != null) current = JsonUtility.FromJson<CoachCurrent>(json); }
            catch (ArgumentException) { }
            if (current == null || string.IsNullOrEmpty(current.sessionId) || current.patientId != patientId
                || (!string.IsNullOrEmpty(expectedProcedureId) && current.procedureId != expectedProcedureId))
            {
                FailSync("no matching live Jarvis session for this patient and procedure; start it on the laptop");
                yield break;
            }
            string stateJson = null;
            yield return Request("GET", SessionPath(current.sessionId), null, r => stateJson = r);
            if (request != adoption) yield break;
            CoachSessionState state = null;
            try { if (stateJson != null) state = JsonUtility.FromJson<CoachSessionState>(stateJson); }
            catch (ArgumentException) { }
            var snapshot = state?.snapshot;
            if (snapshot == null || snapshot.sessionId != current.sessionId || snapshot.patientId != current.patientId
                || snapshot.procedureId != current.procedureId || snapshot.version != 0
                || snapshot.completedCount != 0 || snapshot.stepNumber != 1 || snapshot.status != "active"
                || string.IsNullOrEmpty(snapshot.step?.id))
            {
                FailSync("Jarvis session is not a fresh untouched attempt; start a new session before joining from Unity");
                yield break;
            }
            UseSession(current.sessionId, current.patientId, current.procedureId, snapshot.step.id);
        }

        IEnumerator PollCommands(string sid, int epoch)
        {
            var seen = new HashSet<string>();
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
                    if (c == null || string.IsNullOrEmpty(c.commandId) || !seen.Add(c.commandId)) continue;
                    offeredCommands.Add(c.commandId);
                    CommandRequested?.Invoke(c);
                }
                yield return wait;
            }
        }

        bool Current(string sid, int epoch) => Connected && SessionId == sid && generation == epoch;
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
