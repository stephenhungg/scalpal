using System;
using System.Reflection;
using System.IO;
using Scalpal.Realtime;
using SpacetimeDB.Types;
using UnityEngine;

static class BridgeCheck
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int checks, failures;
    static void Check(string name, bool condition)
    { checks++; Console.WriteLine((condition ? "PASS " : "FAIL ") + name); if (!condition) failures++; }
    static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
    static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
    sealed class Fixture
    {
        public readonly QuestSessionBridge Bridge = new QuestSessionBridge { autoConnect = false };
        public DbConnection Connection;
        public Session Session;
        public ExerciseState State;
        public Fixture() { Connect(); }
        public void Connect()
        {
            Connection = new DbConnection();
            Session = new Session { SessionId = "fixture", CurrentAttemptId = "fixture-a1", ExerciseId = "lap_appendectomy", ExerciseVersion = "0.1.0" };
            State = new ExerciseState { SessionId = "fixture", AttemptId = "fixture-a1", StepVersion = 1 };
            Connection.Db.MySessions.Rows.Add(Session);
            Connection.Db.MyMemberships.Rows.Add(new Membership { SessionId = "fixture", Role = "headset" });
            Connection.Db.SessionExerciseState.Rows.Add(State);
            Set(Bridge, "connection", Connection); Set(Bridge, "subscribed", true);
            Call(Bridge, "HookReducers", Connection);
            Tick();
        }
        public void Tick() { Time.realtimeSinceStartup += 0.6f; Call(Bridge, "Update"); }
        public void ObserveAttempt(string id, DateTimeOffset? timestamp = null, string sender = "headset")
        {
            Session.CurrentAttemptId = State.AttemptId = id;
            State.ExerciseId = Session.ExerciseId; State.ExerciseVersion = Session.ExerciseVersion;
            State.UpdatedAt = timestamp ?? Connection.Reducers.AttemptTimestamp;
            State.UpdatedBy = new SpacetimeDB.Identity(sender);
        }
        public void Snapshot(bool paused = false, bool registered = true)
        { Bridge.PublishSnapshot("Practicing", "inspect", 3, 10, "appendix", "appendix", false, paused, registered, "fixture authored geometry"); }
        public Command Command(string id = "c1")
        {
            var c = new Command { CommandId = id, SessionId = Bridge.SessionId, AttemptId = Bridge.AttemptId, ExpectedStepVersion = State.StepVersion };
            Connection.Db.SessionCommands.Rows.Add(c); return c;
        }
    }
    static void AcknowledgementOrdering()
    {
        var f = new Fixture(); int applied = 0;
        f.Bridge.CommandRequested += command => { applied++; f.Snapshot(); f.Bridge.ResolveCommand(command, "applied"); };
        f.Command(); f.Tick(); f.Tick();
        Check("scene command executes once", applied == 1);
        Check("applied acknowledgement waits for confirmed snapshot commit", f.Connection.Reducers.Resolutions.Count == 0);
        Check("command supplies actual scene snapshot before acknowledgement", f.Connection.Reducers.Snapshots.Count == 1 && f.Connection.Reducers.Snapshots[0].Highlighted == "appendix");
        f.Tick(); Check("repeated frame does not resend inflight snapshot or scene action", applied == 1 && f.Connection.Reducers.Snapshots.Count == 1);
        f.Connection.Reducers.AckSnapshot(true); f.Tick();
        Check("successful snapshot commit releases applied acknowledgement", f.Connection.Reducers.Resolutions.Count == 1 && f.Connection.Reducers.Resolutions[0].Outcome == "applied");
        f.Tick(); Check("acknowledgement remains single while inflight", f.Connection.Reducers.Resolutions.Count == 1);
        f.Connection.Reducers.AckResolution(false); f.Tick();
        Check("rejected acknowledgement never repeats already executed scene action", applied == 1 && f.Connection.Reducers.Resolutions.Count == 1);

        var rejected = new Fixture();
        rejected.Bridge.CommandRequested += command => { rejected.Snapshot(); rejected.Bridge.ResolveCommand(command, "applied"); };
        rejected.Command(); rejected.Tick(); rejected.Tick(); rejected.Connection.Reducers.AckSnapshot(false); rejected.Tick();
        Check("snapshot rejection downgrades applied result to failed", rejected.Connection.Reducers.Resolutions.Count == 1 && rejected.Connection.Reducers.Resolutions[0].Outcome == "failed");
        var missing = new Fixture();
        missing.Bridge.CommandRequested += command => missing.Bridge.ResolveCommand(command, "applied");
        missing.Command(); missing.Tick(); missing.Tick();
        Check("scene cannot claim applied without any authored snapshot", missing.Connection.Reducers.Resolutions[0].Outcome == "failed");
    }
    static void CommandBoundaries()
    {
        var stale = new Fixture(); int staleCalls = 0;
        stale.Bridge.CommandRequested += command => staleCalls++;
        stale.Command().ExpectedStepVersion = 0; stale.Tick(); stale.Tick();
        Check("stale expected-step command is rejected before scene execution", staleCalls == 0 && stale.Connection.Reducers.Resolutions[0].Outcome == "rejected");
        var expired = new Fixture(); int expiredCalls = 0;
        expired.Bridge.CommandRequested += command => expiredCalls++;
        expired.Command().RequestedAt = DateTimeOffset.UtcNow.AddSeconds(-60); expired.Tick(); expired.Tick();
        Check("expired command is rejected before scene execution", expiredCalls == 0 && expired.Connection.Reducers.Resolutions[0].Outcome == "rejected");
        var wrong = new Fixture(); int wrongCalls = 0;
        wrong.Bridge.CommandRequested += command => wrongCalls++;
        wrong.Command("old-attempt").AttemptId = "old";
        wrong.Command("other-session").SessionId = "other"; wrong.Tick(); wrong.Tick();
        Check("foreign-session and old-attempt commands are ignored", wrongCalls == 0 && wrong.Connection.Reducers.Resolutions.Count == 0);
        var noAdapter = new Fixture(); noAdapter.Command(); noAdapter.Tick(); noAdapter.Tick();
        Check("absent scene adapter acknowledges unavailable", noAdapter.Connection.Reducers.Resolutions[0].Outcome == "unavailable");
        var throwing = new Fixture(); throwing.Bridge.CommandRequested += command => { throw new InvalidOperationException("fixture"); };
        throwing.Command(); throwing.Tick(); throwing.Tick();
        Check("throwing scene adapter acknowledges failed", throwing.Connection.Reducers.Resolutions[0].Outcome == "failed");
        var paused = new Fixture(); paused.Snapshot(true, true); paused.Tick();
        var pause = paused.Connection.Reducers.Snapshots[0];
        Check("user pause and valid physical registration stay independent", pause.Paused && pause.Registration == "valid");
        Check("native bridge never enables recording in snapshots", pause.Recording == "off");
    }
    static void ReconnectAndReset()
    {
        var f = new Fixture(); int calls = 0;
        f.Bridge.CommandRequested += command => calls++;
        f.Command(); f.Tick();
        Call(f.Bridge, "Disconnected", "fixture connection loss");
        Check("disconnect clears paired state", !f.Bridge.Paired);
        f.Connect(); f.Command(); f.Tick(); f.Tick();
        Check("same pending command after reconnect cannot repeat scene side effect", calls == 1 && f.Connection.Reducers.Resolutions.Count == 1 && f.Connection.Reducers.Resolutions[0].Outcome == "failed");
        var eventFixture = new Fixture(); var previous = eventFixture.Connection;
        Check("authored event is sent once", eventFixture.Bridge.AppendEvent("touch", "inspect", "appendix", "fixture", 10) && previous.Reducers.Events == 1);
        previous.Reducers.RejectEvent(); eventFixture.Tick();
        Call(eventFixture.Bridge, "Disconnected", "fixture"); eventFixture.Connect(); eventFixture.Tick();
        Check("rejected or uncertain authored events are never retried on reconnect", previous.Reducers.Events == 1 && eventFixture.Connection.Reducers.Events == 0);
        var coalesced = new Fixture(); Call(coalesced.Bridge, "Disconnected", "fixture");
        coalesced.Bridge.PublishSnapshot("Practicing", "old", 1, 10, null, null, false, true, false, "first");
        coalesced.Bridge.PublishSnapshot("Practicing", "latest", 2, 10, null, null, false, false, true, "second");
        coalesced.Connect(); coalesced.Tick();
        Check("offline snapshots retain only the latest state", coalesced.Connection.Reducers.Snapshots.Count == 1 && coalesced.Connection.Reducers.Snapshots[0].Step == "latest");

        var attempt = new Fixture(); int started = 0, failed = 0;
        attempt.Bridge.AttemptStarted += id => started++;
        attempt.Bridge.AttemptFailed += reason => failed++;
        Check("attempt request is sent once", attempt.Bridge.BeginAttempt("lap_appendectomy", "0.1.0") && !attempt.Bridge.BeginAttempt("lap_appendectomy", "0.1.0") && attempt.Connection.Reducers.Attempts.Count == 1);
        Check("attempt is not claimed before acknowledgement", started == 0 && attempt.Bridge.AttemptPending);
        attempt.Connection.Reducers.AckAttempt(true); attempt.Tick();
        Check("attempt commit alone waits for observed new attempt row", started == 0 && attempt.Bridge.AttemptPending);
        attempt.ObserveAttempt("fixture-a2"); attempt.Tick();
        Check("matching observed attempt after commit confirms exactly once", started == 1 && !attempt.Bridge.AttemptPending);
        attempt.Tick(); Check("repeated subscription frame cannot duplicate attempt confirmation", started == 1);
        attempt.Bridge.BeginAttempt("lap_appendectomy", "0.1.0"); Call(attempt.Bridge, "Disconnected", "fixture");
        Check("lost attempt acknowledgement surfaces failure and clears pending latch", failed == 1 && !attempt.Bridge.AttemptPending);
        attempt.Connect(); attempt.Tick();
        Check("bridge reconnect never silently creates a replacement attempt", attempt.Connection.Reducers.Attempts.Count == 0);

        var stop = new Fixture(); var socket = stop.Connection; var subscription = new SubscriptionHandle();
        Set(stop.Bridge, "subscription", subscription); Call(stop.Bridge, "OnDisable");
        Check("disable unsubscribes and disconnects its owned transport", subscription.UnsubscribeCount == 1 && socket.DisconnectCount == 1 && !stop.Bridge.Paired);
    }
    static void Results()
    {
        var f = new Fixture(); int committed = 0; f.Bridge.ResultCommitted += id => committed++;
        Check("incomplete or empty attempt result is refused", !f.Bridge.ReportAttemptResult(9, 10, 0, 0, "fixture") && !f.Bridge.ReportAttemptResult(0, 0, 0, 0, "fixture"));
        Check("completed authored result sends once without premature claim", f.Bridge.ReportAttemptResult(10, 10, 1, 0, "fixture") && !f.Bridge.ReportAttemptResult(10, 10, 1, 0, "fixture") && committed == 0 && f.Connection.Reducers.Results == 1);
        f.Connection.Reducers.AckResult(false);
        Check("rejected result never fires completion acknowledgement", committed == 0);
        f.Bridge.ReportAttemptResult(10, 10, 1, 0, "fixture"); f.Connection.Reducers.AckResult(true);
        Check("committed result callback fires only after actual reducer callback", committed == 1);
    }

    static DbConnection ConnectThroughCallback(QuestSessionBridge bridge, string role = "headset", string status = "active")
    {
        bridge.joinCode = "REVOKED";
        bridge.Reconnect();
        var conn = DbConnection.LastBuilt;
        conn.Reducers.JoinCommitted = false;
        conn.Db.MySessions.Rows.Add(new Session { SessionId = "fixture", CurrentAttemptId = "fixture-a1", Status = status,
            ExerciseId = "lap_appendectomy", ExerciseVersion = "0.1.0" });
        if (role != null) conn.Db.MyMemberships.Rows.Add(new Membership { SessionId = "fixture", Role = role });
        conn.Db.SessionExerciseState.Rows.Add(new ExerciseState { SessionId = "fixture", AttemptId = "fixture-a1" });
        conn.FireConnected();
        return conn;
    }

    static void RotatedInviteMembership()
    {
        var bridge = new QuestSessionBridge { autoConnect = false };
        var conn = ConnectThroughCallback(bridge);
        Check("real connect callback subscribes even when revoked invite join fails", conn.Subscriptions.Count == 1 && conn.Reducers.Joins == 1);
        Check("membership subscription includes all authoritative views", conn.SubscriptionQueries.Count == 1 &&
            Array.IndexOf(conn.SubscriptionQueries[0], "SELECT * FROM my_memberships") >= 0 &&
            Array.IndexOf(conn.SubscriptionQueries[0], "SELECT * FROM my_sessions") >= 0 &&
            Array.IndexOf(conn.SubscriptionQueries[0], "SELECT * FROM session_exercise_state") >= 0);
        Call(bridge, "Update");
        Check("cache rows cannot authorize pairing before subscription applied", !bridge.Paired && !bridge.Status.StartsWith("Pairing rejected:"));
        if (conn.Subscriptions.Count != 0) conn.Subscriptions[0].Apply();
        Call(bridge, "Update");
        Check("revoked invite preserves existing active headset membership pairing", bridge.Paired && bridge.Status == "Paired");
        conn.Reducers.JoinSession("REVOKED", "Quest");
        Call(bridge, "Update");
        Check("join response never creates another subscription or unpairs a member", bridge.Paired && conn.Subscriptions.Count == 1);

        var reconnect = ConnectThroughCallback(bridge);
        if (conn.Subscriptions.Count != 0) conn.Subscriptions[0].Apply();
        Call(bridge, "Update");
        Check("late subscription from old connection cannot authorize reconnect", !bridge.Paired);
        if (reconnect.Subscriptions.Count != 0) reconnect.Subscriptions[0].Apply();
        Call(bridge, "Update");
        Check("same bridge reconnect pairs from persisted membership despite rotated invite", bridge.Paired && bridge.Status == "Paired");
        if (conn.Subscriptions.Count != 0) conn.Subscriptions[0].Fail();
        Check("obsolete subscription error cannot revoke current connection pairing", bridge.Paired);

        foreach (string role in new[] { null, "observer" })
        {
            var denied = new QuestSessionBridge { autoConnect = false };
            var deniedConn = ConnectThroughCallback(denied, role);
            if (deniedConn.Subscriptions.Count != 0) deniedConn.Subscriptions[0].Apply();
            Call(denied, "Update");
            Check("revoked invite cannot grant missing or observer-only headset membership: " + (role ?? "none"),
                !denied.Paired && denied.Status.StartsWith("Pairing rejected:"));
            Call(denied, "OnDisable");
        }
        var ended = new QuestSessionBridge { autoConnect = false };
        var endedConn = ConnectThroughCallback(ended, "headset", "ended");
        if (endedConn.Subscriptions.Count != 0) endedConn.Subscriptions[0].Apply();
        Call(ended, "Update");
        Check("existing headset membership cannot reactivate an ended session", !ended.Paired);
        Call(ended, "OnDisable"); Call(bridge, "OnDisable");
    }

    static void AttemptDeadlines()
    {
        var unacked = new Fixture(); int failures = 0, started = 0;
        unacked.Bridge.AttemptFailed += reason => { failures++; Check("timeout exposes confirmation failure", reason.Contains("10 seconds")); };
        unacked.Bridge.AttemptStarted += id => started++;
        unacked.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        unacked.Snapshot();
        Time.realtimeSinceStartup += 9;
        Call(unacked.Bridge, "Update");
        Check("pending attempt survives before bounded deadline", unacked.Bridge.AttemptPending && failures == 0);
        Time.realtimeSinceStartup += 1.1f;
        Call(unacked.Bridge, "Update");
        Check("missing reducer acknowledgement clears pending attempt after deadline", !unacked.Bridge.AttemptPending && failures == 1 && started == 0);
        unacked.Tick();
        Check("timeout fires once and never publishes pending scene state", failures == 1 && unacked.Connection.Reducers.Snapshots.Count == 0);
        unacked.Connection.Reducers.AckAttempt(true);
        unacked.ObserveAttempt("late-a2");
        unacked.Tick();
        Check("late acknowledgement and row cannot revive timed-out request", started == 0 && !unacked.Bridge.AttemptPending);
        Check("timeout permits explicit subsequent attempt request", unacked.Bridge.BeginAttempt("lap_appendectomy", "0.1.0") && unacked.Connection.Reducers.Attempts.Count == 2);

        var mismatch = new Fixture(); int mismatchFailed = 0, mismatchStarted = 0;
        mismatch.Bridge.AttemptFailed += reason => mismatchFailed++;
        mismatch.Bridge.AttemptStarted += id => mismatchStarted++;
        mismatch.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        mismatch.Connection.Reducers.AckAttempt(true);
        mismatch.Session.ExerciseId = "lap_cholecystectomy";
        mismatch.ObserveAttempt("foreign-a2");
        mismatch.Tick();
        Check("committed request never accepts unrelated exercise attempt", mismatchStarted == 0 && mismatch.Bridge.AttemptPending);
        Time.realtimeSinceStartup += 10.1f; mismatch.Tick();
        Check("committed but mismatched observation fails instead of wedging", mismatchFailed == 1 && mismatchStarted == 0 && !mismatch.Bridge.AttemptPending);

        var missingState = new Fixture(); int missingStarted = 0;
        missingState.Bridge.AttemptStarted += id => missingStarted++;
        missingState.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        missingState.Connection.Reducers.AckAttempt(true);
        missingState.Session.CurrentAttemptId = "new-a2";
        missingState.Tick();
        Check("new session attempt without matching exercise-state row is not confirmed", missingStarted == 0 && missingState.Bridge.AttemptPending && !missingState.Bridge.Paired);
        missingState.ObserveAttempt("new-a2"); missingState.Tick();
        Check("matching state arriving before deadline confirms request", missingStarted == 1 && !missingState.Bridge.AttemptPending);

        var foreign = new Fixture(); int foreignStarted = 0;
        foreign.Bridge.AttemptStarted += id => foreignStarted++;
        foreign.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        foreign.Connection.Reducers.AckForeignAttempt("fixture", "lap_appendectomy", "0.1.0", true);
        foreign.ObserveAttempt("operator-a2", DateTimeOffset.UtcNow, "operator"); foreign.Tick();
        Check("operator reducer event cannot acknowledge this headset request", foreignStarted == 0 && foreign.Bridge.AttemptPending);

        var superseded = new Fixture(); int supersededStarted = 0, supersededFailed = 0;
        superseded.Bridge.AttemptStarted += id => supersededStarted++;
        superseded.Bridge.AttemptFailed += reason => supersededFailed++;
        superseded.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        var commitAt = DateTimeOffset.UtcNow;
        superseded.ObserveAttempt("own-a2", commitAt);
        superseded.Connection.Reducers.AckAttempt(true, commitAt);
        superseded.ObserveAttempt("operator-a3", commitAt.AddMilliseconds(1), "operator");
        superseded.Tick();
        Check("same-exercise operator attempt cannot replace captured headset commit", supersededStarted == 0 && superseded.Bridge.AttemptPending);
        Time.realtimeSinceStartup += 10.1f; superseded.Tick();
        Check("superseded headset commit fails at deadline without adopting operator attempt", supersededStarted == 0 && supersededFailed == 1 && !superseded.Bridge.AttemptPending);

        var uncorrelated = new Fixture(); int uncorrelatedStarted = 0;
        uncorrelated.Bridge.AttemptStarted += id => uncorrelatedStarted++;
        uncorrelated.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        uncorrelated.Connection.Reducers.AckAttempt(true);
        uncorrelated.ObserveAttempt("same-exercise-other", uncorrelated.Connection.Reducers.AttemptTimestamp.AddMilliseconds(1));
        uncorrelated.Tick();
        Check("same sender and exercise cannot confirm a different transaction timestamp", uncorrelatedStarted == 0 && uncorrelated.Bridge.AttemptPending);

        var noSubscription = new Fixture(); int noSubscriptionFailed = 0;
        noSubscription.Bridge.AttemptFailed += reason => noSubscriptionFailed++;
        noSubscription.Bridge.BeginAttempt("lap_appendectomy", "0.1.0");
        Set(noSubscription.Bridge, "subscribed", false);
        Time.realtimeSinceStartup += 10.1f; noSubscription.Tick();
        Check("observation unavailable still expires attempt instead of bypassing timeout", noSubscriptionFailed == 1 && !noSubscription.Bridge.AttemptPending);
    }
    static void EncounterBinding()
    {
        var unavailable = new QuestSessionBridge { autoConnect = false };
        Check("unavailable bridge cannot invent encounter provenance", !unavailable.TryGetEncounterBinding("enc-fixture123", out var sid, out var aid, out var patient, out var phase)
            && sid == "" && aid == "" && patient == "" && phase == "");
        var f = new Fixture();
        Check("paired bridge rejects a missing encounter", !f.Bridge.TryGetEncounterBinding("enc-fixture123", out sid, out aid, out patient, out phase));
        f.Connection.Db.SessionEncounters.Rows.Add(new Encounter { EncounterId = "enc-fixture123", SessionId = "fixture", AttemptId = "fixture-a1", PatientId = "u_fixture_patient", Phase = "scored" });
        Check("exact encounter row supplies recorded shared attempt and patient", f.Bridge.TryGetEncounterBinding("enc-fixture123", out sid, out aid, out patient, out phase)
            && sid == "fixture" && aid == "fixture-a1" && patient == "u_fixture_patient" && phase == "scored");
        Check("encounter lookup cannot use another ID", !f.Bridge.TryGetEncounterBinding("enc-other123", out sid, out aid, out patient, out phase)
            && sid == "" && aid == "" && patient == "" && phase == "");
        f.Session.CurrentAttemptId = f.State.AttemptId = "fixture-a2"; f.Tick();
        Check("recorded encounter provenance does not silently move to a newer attempt", f.Bridge.TryGetEncounterBinding("enc-fixture123", out sid, out aid, out patient, out phase)
            && aid == "fixture-a1" && aid != f.Bridge.AttemptId && f.Connection.Reducers.Attempts.Count == 0);
        f.Connection.IsActive = false;
        Check("disconnected cache cannot authorize an office handoff", !f.Bridge.TryGetEncounterBinding("enc-fixture123", out sid, out aid, out patient, out phase)
            && sid == "" && aid == "" && patient == "" && phase == "");
    }

    public static int Main()
    {
        // Connection lifecycle tests persist only a disposable fixture token, never an app token.
        string tokenDirectory = Path.Combine(Path.GetTempPath(), "scalpal-bridge-check-" + Guid.NewGuid().ToString("N"));
        Application.persistentDataPath = tokenDirectory;
        try { AcknowledgementOrdering(); CommandBoundaries(); ReconnectAndReset(); Results(); EncounterBinding(); RotatedInviteMembership(); AttemptDeadlines(); }
        finally { if (Directory.Exists(tokenDirectory)) Directory.Delete(tokenDirectory, true); }
        Console.WriteLine("SCALPAL_NATIVE_BRIDGE_CHECK checks=" + checks + " passed=" + (checks - failures) + " failed=" + failures + "; production bridge with deterministic transport/cache doubles, no Unity or live reducer validation");
        return failures == 0 ? 0 : 1;
    }
}
