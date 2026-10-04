using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;

namespace Scalpal.Realtime
{
    // Small session adapter. XR and exercise rules remain local and never wait for networking.
    // Add no SpacetimeDBNetworkManager to this scene: this component owns FrameTick.
    public sealed class QuestSessionBridge : MonoBehaviour
    {
        public string uri = "ws://127.0.0.1:3000";
        public string database = "scalpal";
        public string joinCode = "";
        public string displayName = "Quest";
        public string preferredSessionId = "";
        public bool autoConnect = true;
        [Range(0.25f, 2f)] public float snapshotInterval = 0.5f;

        public bool Connected => connection != null && connection.IsActive;
        public bool Paired { get; private set; }
        public string SessionId { get; private set; } = "";
        public string AttemptId { get; private set; } = "";
        public string Status { get; private set; } = "Unavailable: configure a headset invite";
        public ExerciseState ObservedState { get; private set; }
        public bool AttemptPending => attemptInFlight;
        public event Action<string> AttemptFailed;
        public event Action<Command> CommandRequested;
        public event Action<string> AttemptStarted;
        public event Action<string> ResultCommitted;

        const float AttemptTimeoutSeconds = 10;

        DbConnection connection;
        SubscriptionHandle subscription;
        bool subscribed, connecting, stopping, dirty, snapshotInFlight, attemptInFlight, attemptCommitted, joinRejected;
        float reconnectAt, nextPublish, connectStarted, attemptStartedAt;
        string attemptBeforeStart, requestedExercise, requestedVersion, committedAttemptId;
        DateTimeOffset committedAttemptAt;
        long generation, sentGeneration, acknowledgedGeneration;
        string sentAttempt;
        Snapshot latest;
        Command dispatchedCommand;
        string resolution, resolutionReason;
        long resolutionGeneration;
        bool resolutionInFlight;
        string resultPendingAttempt;
        readonly HashSet<string> dispatchedIds = new HashSet<string>();
        readonly Queue<string> dispatchOrder = new Queue<string>();

        sealed class Snapshot
        {
            public string phase, stepId, selected, highlighted, registration, reason;
            public uint index, count;
            public bool rotating, paused;
        }

        void OnEnable() { if (autoConnect) Reconnect(); }
        void OnDisable() => Close();
        void OnDestroy() => Close();

        public void Reconnect()
        {
            Close();
            stopping = false;
            reconnectAt = 0;
            Connect();
        }

        void Connect()
        {
            if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(database) || string.IsNullOrWhiteSpace(joinCode))
            {
                SetStatus("Unavailable: configure URI, database and a headset invite");
                return;
            }
            connecting = true;
            connectStarted = Time.realtimeSinceStartup;
            try
            {
                connection = DbConnection.Builder().WithUri(uri).WithDatabaseName(database)
                    .WithToken(ReadToken()).WithCompression(Compression.None)
                    .OnConnect((conn, identity, token) =>
                    {
                        if (stopping || conn != connection) return;
                        connecting = false;
                        SaveToken(token);
                        HookReducers(conn);
                        // Invites authorize joining, not an existing identity's reads. A rotated
                        // invite must not prevent a persisted headset member from resubscribing.
                        subscription = conn.SubscriptionBuilder().OnApplied(context =>
                            { if (!stopping && conn == connection) subscribed = true; })
                            .OnError((context, error) =>
                            {
                                if (stopping || conn != connection) return;
                                subscribed = false; Paired = false;
                                SetStatus("Session subscription unavailable");
                            })
                            .Subscribe(new[] { "SELECT * FROM my_sessions", "SELECT * FROM my_memberships", "SELECT * FROM session_exercise_state", "SELECT * FROM session_commands", "SELECT * FROM session_encounters" });
                        conn.Reducers.JoinSession(joinCode.Trim().ToUpperInvariant(), displayName);
                        SetStatus("Connected: checking headset membership");
                    })
                    .OnConnectError(error => Disconnected("Connection unavailable"))
                    .OnDisconnect((conn, error) => { if (conn == connection) Disconnected("Disconnected"); })
                    .Build();
                SetStatus("Connecting");
            }
            catch (Exception) { Disconnected("Connection unavailable"); }
        }

        void HookReducers(DbConnection conn)
        {
            conn.Reducers.OnJoinSession += (ctx, code, name) =>
            {
                if (stopping || conn != connection) return;
                joinRejected = !(ctx.Event.Status is SpacetimeDB.Status.Committed);
                // ObserveSession decides pairing only after the membership subscription applies.
            };
            conn.Reducers.OnStartAttempt += (ctx, session, exercise, version) =>
            {
                if (conn != connection || !attemptInFlight || session != SessionId ||
                    exercise != requestedExercise || version != requestedVersion ||
                    !(conn.Identity is Identity identity) || !identity.Equals(ctx.Event.CallerIdentity)) return;
                if (ctx.Event.Status is SpacetimeDB.Status.Committed)
                {
                    attemptCommitted = true;
                    committedAttemptAt = (DateTimeOffset)ctx.Event.Timestamp;
                    CaptureCommittedAttempt();
                }
                else FailAttempt("Attempt request rejected", "Shared attempt request rejected; retry explicitly");
            };
            conn.Reducers.OnPublishExerciseState += (ctx, session, attempt, mode, step, index, count, selected, clearSelected, highlighted, clearHighlighted, rotating, paused, registration, reason, recording) =>
            {
                if (!snapshotInFlight || session != SessionId || attempt != sentAttempt) return;
                snapshotInFlight = false;
                if (ctx.Event.Status is SpacetimeDB.Status.Committed) acknowledgedGeneration = sentGeneration;
                else
                {
                    SetStatus("Snapshot rejected");
                    if (resolution == "applied") { resolution = "failed"; resolutionReason = "Confirmed scene state could not be published"; }
                }
            };
            conn.Reducers.OnResolveCommand += (ctx, commandId, outcome, reason) =>
            {
                if (dispatchedCommand == null || commandId != dispatchedCommand.CommandId) return;
                resolutionInFlight = false;
                resolution = null;
                // Keep a rejected acknowledgement parked until the server's pending row expires.
                // Redispatching the same command could repeat an already-applied scene effect.
                if (ctx.Event.Status is SpacetimeDB.Status.Committed) dispatchedCommand = null;
                else SetStatus("Command acknowledgement rejected");
            };
            conn.Reducers.OnAppendExerciseEvent += (ctx, session, attempt, kind, step, structure, message, deviceTime) =>
            {
                if (!(ctx.Event.Status is SpacetimeDB.Status.Committed)) SetStatus("Event rejected; event was not retried");
            };
            conn.Reducers.OnSetAttemptResult += (ctx, attempt, practiceStatus, complete, total, mistakes, hints, summary) =>
            {
                if (attempt != resultPendingAttempt) return;
                resultPendingAttempt = null;
                if (ctx.Event.Status is SpacetimeDB.Status.Committed)
                {
                    SetStatus("Learning result committed");
                    ResultCommitted?.Invoke(attempt);
                }
                else SetStatus("Learning result rejected");
            };
        }

        void Update()
        {
            if (connection != null)
            {
                try { connection.FrameTick(); }
                catch (Exception) { CloseConnection(); Disconnected("Network processing unavailable"); }
            }
            if (connecting && Time.realtimeSinceStartup - connectStarted > 15) Disconnected("Connection timed out");
            if (attemptInFlight && Time.realtimeSinceStartup - attemptStartedAt >= AttemptTimeoutSeconds)
            {
                FailAttempt("Attempt confirmation timed out", "Shared attempt was not confirmed within 10 seconds; check the session and retry explicitly");
                return;
            }
            if (!Connected)
            {
                if (autoConnect && !stopping && !connecting && Time.realtimeSinceStartup >= reconnectAt)
                {
                    CloseConnection(); reconnectAt = Time.realtimeSinceStartup + 5; Connect();
                }
                return;
            }
            if (!subscribed) return;
            ObserveSession();
            if (!Paired) return;
            PumpSnapshot();
            PumpCommands();
        }

        void ObserveSession()
        {
            Session chosen = null;
            foreach (var member in connection.Db.MyMemberships.Iter())
            {
                if (member.Role != "headset") continue;
                if (!string.IsNullOrEmpty(preferredSessionId) && member.SessionId != preferredSessionId) continue;
                foreach (var candidate in connection.Db.MySessions.Iter())
                {
                    if (candidate.SessionId != member.SessionId || candidate.Status != "active") continue;
                    if (chosen != null && chosen.SessionId != candidate.SessionId)
                    {
                        Paired = false; SetStatus("Choose preferredSessionId: multiple active headset memberships"); return;
                    }
                    chosen = candidate;
                }
            }
            if (chosen == null)
            {
                Paired = false; ObservedState = null;
                SetStatus(joinRejected ? "Pairing rejected: no active headset membership; check invite" : "Unpaired: no active headset membership");
                return;
            }
            bool hadSession = !string.IsNullOrEmpty(SessionId);
            bool changed = SessionId != chosen.SessionId || AttemptId != chosen.CurrentAttemptId;
            SessionId = chosen.SessionId;
            AttemptId = chosen.CurrentAttemptId;
            ObservedState = connection.Db.SessionExerciseState.SessionId.Find(SessionId);
            Paired = ObservedState != null && ObservedState.AttemptId == AttemptId;
            if (changed)
            {
                dispatchedCommand = null; resolution = null; resolutionInFlight = false;
                acknowledgedGeneration = 0;
                dispatchedIds.Clear(); dispatchOrder.Clear();
                if (hadSession && !attemptInFlight) { latest = null; dirty = false; }
            }
            CaptureCommittedAttempt();
            if (Paired && attemptInFlight && attemptCommitted && AttemptId == committedAttemptId &&
                chosen.ExerciseId == requestedExercise && chosen.ExerciseVersion == requestedVersion)
            {
                attemptInFlight = false; attemptCommitted = false;
                AttemptStarted?.Invoke(AttemptId);
            }
            if (Paired && (changed || Status.StartsWith("Connected:", StringComparison.Ordinal) ||
                Status.StartsWith("Unpaired:", StringComparison.Ordinal) || Status.StartsWith("Pairing rejected:", StringComparison.Ordinal))) SetStatus("Paired");
        }

        void CaptureCommittedAttempt()
        {
            if (!attemptInFlight || !attemptCommitted || !string.IsNullOrEmpty(committedAttemptId)) return;
            // The server start reducer stamps this state with its transaction's timestamp and
            // sender. Bind that commit to one attempt rather than adopting any later matching
            // exercise. SDK v2 applies reducer DB updates before invoking its callback; delayed
            // state is still allowed, but an uncorrelated cache must fail at the deadline.
            var state = connection.Db.SessionExerciseState.SessionId.Find(SessionId);
            if (state == null || state.AttemptId == attemptBeforeStart ||
                state.ExerciseId != requestedExercise || state.ExerciseVersion != requestedVersion ||
                (DateTimeOffset)state.UpdatedAt != committedAttemptAt ||
                !(connection.Identity is Identity identity) || !identity.Equals(state.UpdatedBy)) return;
            committedAttemptId = state.AttemptId;
        }

        public bool TryGetEncounterBinding(string id, out string sessionId, out string attemptId, out string patientId, out string phase)
        {
            sessionId = attemptId = patientId = phase = "";
            if (!Connected || !Paired || string.IsNullOrEmpty(id)) return false;
            var encounter = connection.Db.SessionEncounters.EncounterId.Find(id);
            if (encounter == null) return false;
            sessionId = encounter.SessionId; attemptId = encounter.AttemptId;
            patientId = encounter.PatientId; phase = encounter.Phase;
            return true;
        }

        public bool BeginAttempt(string exerciseId, string version)
        {
            if (!Paired || attemptInFlight || string.IsNullOrWhiteSpace(exerciseId) || string.IsNullOrWhiteSpace(version)) return false;
            attemptBeforeStart = AttemptId;
            requestedExercise = exerciseId; requestedVersion = version;
            attemptInFlight = true; attemptCommitted = false;
            committedAttemptId = null;
            attemptStartedAt = Time.realtimeSinceStartup;
            latest = null; dirty = false;
            try { connection.Reducers.StartAttempt(SessionId, exerciseId, version); return true; }
            catch (Exception) { attemptInFlight = false; SetStatus("Attempt could not be sent"); return false; }
        }

        void FailAttempt(string status, string reason)
        {
            attemptInFlight = false; attemptCommitted = false;
            // A pending scene snapshot is not confirmation of this uncertain attempt.
            latest = null; dirty = false;
            SetStatus(status);
            AttemptFailed?.Invoke(reason);
        }

        public void PublishSnapshot(string phase, string stepId, uint index, uint count,
            string selectedStructureId, string highlightedStructureId, bool previewRotating,
            bool paused, bool registrationValid, string registrationReason)
        {
            latest = new Snapshot { phase = phase, stepId = stepId, index = index, count = count,
                selected = selectedStructureId, highlighted = highlightedStructureId, rotating = previewRotating,
                paused = paused, registration = registrationValid ? "valid" :
                    (phase == "Startup" || phase == "Selecting" || phase == "Confirmed" ? "unaligned" : "uncertain"), reason = registrationReason };
            generation++; dirty = true;
        }

        void PumpSnapshot()
        {
            if (!dirty || latest == null || snapshotInFlight || attemptInFlight || Time.realtimeSinceStartup < nextPublish) return;
            var snapshot = latest;
            dirty = false; snapshotInFlight = true; sentGeneration = generation; sentAttempt = AttemptId;
            nextPublish = Time.realtimeSinceStartup + Mathf.Max(0.25f, snapshotInterval);
            try
            {
                connection.Reducers.PublishExerciseState(SessionId, AttemptId, snapshot.phase, snapshot.stepId,
                    snapshot.index, snapshot.count, snapshot.selected, string.IsNullOrEmpty(snapshot.selected),
                    snapshot.highlighted, string.IsNullOrEmpty(snapshot.highlighted), snapshot.rotating, snapshot.paused,
                    snapshot.registration, snapshot.reason, "off");
            }
            catch (Exception)
            {
                snapshotInFlight = false; SetStatus("Snapshot could not be sent");
                if (resolution == "applied") { resolution = "failed"; resolutionReason = "Confirmed scene state could not be sent"; }
            }
        }

        public bool AppendEvent(string kind, string stepId, string structureId, string message, double deviceTimeMs)
        {
            if (!Paired || attemptInFlight) return false;
            // No event-id field exists in this schema. An uncertain write is never replayed.
            try { connection.Reducers.AppendExerciseEvent(SessionId, AttemptId, kind, stepId, structureId, message, deviceTimeMs); return true; }
            catch (Exception) { SetStatus("Event could not be sent; event was not retried"); return false; }
        }

        // Called by the local authored case-completed callback, never by a motion job or recap UI.
        // A true return means sent; only ResultCommitted confirms the learning result was stored.
        public bool ReportAttemptResult(uint stepsCompleted, uint stepsTotal, uint mistakes, uint hintsUsed, string summary)
        {
            if (!Paired || attemptInFlight || resultPendingAttempt != null || stepsTotal == 0 || stepsCompleted != stepsTotal) return false;
            resultPendingAttempt = AttemptId;
            try
            {
                connection.Reducers.SetAttemptResult(AttemptId, "completed", stepsCompleted, stepsTotal, mistakes, hintsUsed, summary);
                SetStatus("Learning result awaiting acknowledgement");
                return true;
            }
            catch (Exception) { resultPendingAttempt = null; SetStatus("Learning result could not be sent"); return false; }
        }

        void PumpCommands()
        {
            if (dispatchedCommand != null)
            {
                var live = connection.Db.SessionCommands.CommandId.Find(dispatchedCommand.CommandId);
                if (live == null || live.Status != "pending" || live.AttemptId != AttemptId)
                { dispatchedCommand = null; resolution = null; resolutionInFlight = false; }
                else if (resolution != null && !resolutionInFlight &&
                    (resolution != "applied" || acknowledgedGeneration >= resolutionGeneration))
                {
                    resolutionInFlight = true;
                    try { connection.Reducers.ResolveCommand(live.CommandId, resolution, resolutionReason); }
                    catch (Exception) { resolutionInFlight = false; resolution = null; SetStatus("Command acknowledgement could not be sent"); }
                }
                return;
            }
            if (attemptInFlight || ObservedState == null) return;
            foreach (var command in connection.Db.SessionCommands.Iter())
            {
                if (command.SessionId != SessionId || command.AttemptId != AttemptId || command.Status != "pending") continue;
                dispatchedCommand = command;
                if (command.ExpectedStepVersion != ObservedState.StepVersion)
                    ResolveCommand(command, "rejected", "The confirmed exercise step changed");
                else if (IsExpired(command)) ResolveCommand(command, "rejected", "Command expired");
                else if (dispatchedIds.Contains(command.CommandId))
                    ResolveCommand(command, "failed", "Command was already dispatched; its previous acknowledgement is uncertain");
                else if (CommandRequested == null) ResolveCommand(command, "unavailable", "No scene command adapter is installed");
                else
                {
                    dispatchedIds.Add(command.CommandId); dispatchOrder.Enqueue(command.CommandId);
                    if (dispatchOrder.Count > 32) dispatchedIds.Remove(dispatchOrder.Dequeue());
                    try { CommandRequested.Invoke(command); }
                    catch (Exception) { ResolveCommand(command, "failed", "Scene command adapter failed"); }
                }
                break;
            }
        }

        static bool IsExpired(Command command)
        {
            // Same wall-clock domain as the server timestamp; skew is a limitation of this local check.
            return (DateTimeOffset.UtcNow - (DateTimeOffset)command.RequestedAt).TotalSeconds >= 15;
        }

        public bool ResolveCommand(Command command, string outcome, string reason = null)
        {
            if (!Paired || command == null || dispatchedCommand == null || command.CommandId != dispatchedCommand.CommandId ||
                command.SessionId != SessionId || command.AttemptId != AttemptId || resolutionInFlight) return false;
            if (outcome != "applied" && outcome != "rejected" && outcome != "unavailable" && outcome != "failed") return false;
            if (outcome == "applied" && latest == null) { outcome = "failed"; reason = "No confirmed scene snapshot was supplied"; }
            resolution = outcome; resolutionReason = reason;
            if (outcome == "applied") { generation++; dirty = true; resolutionGeneration = generation; }
            return true;
        }

        void Disconnected(string status)
        {
            bool uncertainAttempt = attemptInFlight;
            connecting = false; subscribed = false; joinRejected = false; Paired = false; ObservedState = null;
            snapshotInFlight = false; acknowledgedGeneration = 0; dirty = latest != null;
            dispatchedCommand = null; resolution = null; resolutionInFlight = false;
            // A lost attempt acknowledgement must not create another attempt on retry.
            attemptInFlight = false; attemptCommitted = false;
            resultPendingAttempt = null;
            reconnectAt = Time.realtimeSinceStartup + 5;
            SetStatus(status);
            if (uncertainAttempt) AttemptFailed?.Invoke("Shared attempt acknowledgement lost; A: retry after reconnect");
        }

        void Close()
        {
            stopping = true;
            CloseConnection();
            Disconnected("Disconnected");
        }

        void CloseConnection()
        {
            var old = connection;
            connection = null;
            if (old != null)
            {
                if (old.IsActive) { try { subscription?.Unsubscribe(); } catch (Exception) { } }
                try { old.Disconnect(); } catch (Exception) { }
            }
            subscription = null;
        }

        string TokenPath()
        {
            using (var hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(uri + "\n" + database))).Replace("-", "");
                return Path.Combine(Application.persistentDataPath, "scalpal-session-" + key + ".token");
            }
        }

        string ReadToken() { try { return File.Exists(TokenPath()) ? File.ReadAllText(TokenPath()) : null; } catch (Exception) { return null; } }
        void SaveToken(string token)
        {
            try { Directory.CreateDirectory(Application.persistentDataPath); File.WriteAllText(TokenPath(), token); }
            catch (Exception) { SetStatus("Connected: identity persistence unavailable"); }
        }
        void SetStatus(string value)
        {
            if (Status == value) return;
            Status = value;
            Debug.Log("SCALPAL_SESSION " + value);
        }
    }
}
