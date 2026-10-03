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

    public class CoachRelay : MonoBehaviour
    {
        [Tooltip("Same service as ScalpalPreopService: the Mac's LAN address on Quest, e.g. http://192.168.1.20:8787.")]
        [SerializeField] string baseUrl = "http://localhost:8787";
        [SerializeField] float commandPollSeconds = 0.5f;
        [SerializeField] int timeoutSeconds = 5;

        readonly Queue<CoachEventDto> pending = new Queue<CoachEventDto>();
        bool sending;
        string lastFocus = "";
        bool lastTrackingValid = true;

        public string SessionId { get; private set; } = "";
        public bool Connected => !string.IsNullOrEmpty(SessionId);

        // Jarvis asked to highlight (or clear) a structure. Apply it, then call Ack.
        public event Action<CoachCommand> CommandRequested;
        public event Action<string> SessionAdopted;

        // The laptop Jarvis page starts the session; the headset adopts the newest one for this patient.
        public void AdoptCurrentSession(string patientId) => StartCoroutine(Adopt(patientId));

        public void UseSession(string sessionId)
        {
            SessionId = sessionId ?? "";
            if (Connected) SessionAdopted?.Invoke(SessionId);
        }

        // Call right after runner.Handle(e) with the same event.
        public void Forward(CaseEvent e)
        {
            switch (e.type)
            {
                case CaseEventType.PlacePort: Enqueue("place_port", portId: e.id); break;
                case CaseEventType.Touch: Enqueue("touch", structureId: e.id, instrumentId: e.instrumentId); break;
                case CaseEventType.Identify: Enqueue("identify", structureId: e.id); break;
                case CaseEventType.Confirm: Enqueue("confirm"); break;
            }
        }

        // Structure under the gaze ray or instrument tip ("" for none). Only changes are sent.
        public void Focus(string structureId)
        {
            structureId = structureId ?? "";
            if (structureId == lastFocus) return;
            lastFocus = structureId;
            Enqueue("focus", structureId: structureId);
        }

        // Registration validity from the torso tracker. Invalid pauses coaching and scoring.
        public void Tracking(bool valid)
        {
            if (valid == lastTrackingValid) return;
            lastTrackingValid = valid;
            Enqueue("tracking", valid: valid);
        }

        public void Ack(string commandId, bool applied, string reason = "")
        {
            if (!Connected) return;
            var body = JsonUtility.ToJson(new CoachAck { status = applied ? "applied" : "rejected", reason = reason ?? "" });
            StartCoroutine(Request("POST", $"/coach/sessions/{SessionId}/commands/{commandId}/ack", body, null));
        }

        void Enqueue(string type, string portId = "", string structureId = "", string instrumentId = "", bool valid = false)
        {
            pending.Enqueue(new CoachEventDto { type = type, portId = portId, structureId = structureId, instrumentId = instrumentId, valid = valid });
            if (!sending && Connected) StartCoroutine(Flush());
        }

        // One batch in flight at a time keeps events in order.
        IEnumerator Flush()
        {
            sending = true;
            while (pending.Count > 0 && Connected)
            {
                var batch = new CoachEventBatch { events = pending.ToArray() };
                pending.Clear();
                yield return Request("POST", $"/coach/sessions/{SessionId}/events", JsonUtility.ToJson(batch), null);
            }
            sending = false;
        }

        IEnumerator Adopt(string patientId)
        {
            string json = null;
            yield return Request("GET", $"/coach/current?patientId={patientId}", null, r => json = r);
            var current = json != null ? JsonUtility.FromJson<CoachCurrent>(json) : null;
            if (current == null || string.IsNullOrEmpty(current.sessionId))
            {
                Debug.LogWarning("CoachRelay: no live Jarvis session for this patient yet. Start one from the laptop page.");
                yield break;
            }
            UseSession(current.sessionId);
            if (pending.Count > 0 && !sending) StartCoroutine(Flush());
            StartCoroutine(PollCommands());
        }

        IEnumerator PollCommands()
        {
            var seen = new HashSet<string>();
            var wait = new WaitForSeconds(commandPollSeconds);
            while (Connected)
            {
                string json = null;
                yield return Request("GET", $"/coach/sessions/{SessionId}/commands", null, r => json = r);
                var list = json != null ? JsonUtility.FromJson<CoachCommandList>(json) : null;
                foreach (var c in list?.commands ?? new CoachCommand[0])
                {
                    if (seen.Add(c.commandId)) CommandRequested?.Invoke(c);
                }
                yield return wait;
            }
        }

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
