// Deterministic transport/cache doubles. Compile the unchanged production bridge against
// these types to control reducer acknowledgement ordering without Unity or a live session.
using System;
using System.Collections.Generic;
using System.IO;

namespace UnityEngine
{
    public class MonoBehaviour { }
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }
    public static class Time { public static float realtimeSinceStartup; }
    public static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
    public static class Application { public static string persistentDataPath = Path.Combine(Path.GetTempPath(), "scalpal-bridge-fixture-unused"); }
    public static class Debug { public static void Log(object value) { } }
}
namespace SpacetimeDB
{
    public enum Compression { None }
    public readonly struct Identity : IEquatable<Identity>
    {
        readonly string value;
        public Identity(string value) { this.value = value; }
        public bool Equals(Identity other) => value == other.value;
        public override bool Equals(object other) => other is Identity identity && Equals(identity);
        public override int GetHashCode() => value == null ? 0 : value.GetHashCode();
    }
    public abstract class Status { public sealed class Committed : Status { } public sealed class Failed : Status { } }
}
namespace SpacetimeDB.Types
{
    public sealed class ReducerEventContext
    {
        public EventData Event = new EventData();
        public sealed class EventData
        {
            public SpacetimeDB.Status Status;
            public SpacetimeDB.Identity CallerIdentity = new SpacetimeDB.Identity("headset");
            public DateTimeOffset Timestamp;
        }
        public static ReducerEventContext Result(bool committed) => new ReducerEventContext {
            Event = new EventData { Status = committed ? (SpacetimeDB.Status)new SpacetimeDB.Status.Committed() : new SpacetimeDB.Status.Failed() } };
    }
    public sealed class Session
    { public string SessionId, CurrentAttemptId, ExerciseId, ExerciseVersion, Status = "active"; }
    public sealed class Membership { public string Role, SessionId; }
    public sealed class ExerciseState
    {
        public string SessionId, AttemptId, ExerciseId = "lap_appendectomy", ExerciseVersion = "0.1.0";
        public ulong StepVersion = 1;
        public DateTimeOffset UpdatedAt;
        public SpacetimeDB.Identity UpdatedBy = new SpacetimeDB.Identity("headset");
    }
    public sealed class Command
    { public string CommandId, SessionId, AttemptId, Status = "pending"; public ulong ExpectedStepVersion = 1; public DateTimeOffset RequestedAt = DateTimeOffset.UtcNow; }
    public sealed class Index<T>
    {
        readonly Func<string, T> find;
        public Index(Func<string, T> find) { this.find = find; }
        public T Find(string value) => find(value);
    }
    public sealed class Table<T> where T : class
    {
        public readonly List<T> Rows = new List<T>();
        public Index<T> SessionId, CommandId;
        public IEnumerable<T> Iter() => Rows;
        public Table(Func<T, string> key)
        { SessionId = CommandId = new Index<T>(value => Rows.Find(row => key(row) == value)); }
    }
    public sealed class Cache
    {
        public readonly Table<Session> MySessions = new Table<Session>(row => row.SessionId);
        public readonly Table<Membership> MyMemberships = new Table<Membership>(row => row.SessionId);
        public readonly Table<ExerciseState> SessionExerciseState = new Table<ExerciseState>(row => row.SessionId);
        public readonly Table<Command> SessionCommands = new Table<Command>(row => row.CommandId);
    }
    public sealed class SubscriptionHandle
    {
        public int UnsubscribeCount;
        public Action<object> Applied;
        public Action<object, Exception> Error;
        public void Unsubscribe() { UnsubscribeCount++; }
        public void Apply() => Applied?.Invoke(null);
        public void Fail() => Error?.Invoke(null, new Exception("fixture subscription failure"));
    }
    public sealed class SubscribeBuilder
    {
        Action<object> applied;
        Action<object, Exception> error;
        readonly DbConnection connection;
        public SubscribeBuilder(DbConnection connection) { this.connection = connection; }
        public SubscribeBuilder OnApplied(Action<object> callback) { applied = callback; return this; }
        public SubscribeBuilder OnError(Action<object, Exception> callback) { error = callback; return this; }
        public SubscriptionHandle Subscribe(string[] queries)
        {
            var handle = new SubscriptionHandle { Applied = applied, Error = error };
            connection.Subscriptions.Add(handle); connection.SubscriptionQueries.Add(queries);
            return handle;
        }
    }
    public sealed class DbConnection
    {
        public bool IsActive = true;
        public SpacetimeDB.Identity? Identity = new SpacetimeDB.Identity("headset");
        public int DisconnectCount, FrameTickCount;
        public readonly Cache Db = new Cache();
        public readonly RemoteReducers Reducers = new RemoteReducers();
        public static DbConnection LastBuilt;
        public readonly List<SubscriptionHandle> Subscriptions = new List<SubscriptionHandle>();
        public readonly List<string[]> SubscriptionQueries = new List<string[]>();
        Action<DbConnection, object, string> connected;
        public void FireConnected() => connected?.Invoke(this, null, "fixture-token");
        public void FrameTick() { FrameTickCount++; }
        public void Disconnect() { DisconnectCount++; IsActive = false; }
        public SubscribeBuilder SubscriptionBuilder() => new SubscribeBuilder(this);
        public static ConnectionBuilder Builder() => new ConnectionBuilder();
        public sealed class ConnectionBuilder
        {
            Action<DbConnection, object, string> connected;
            public ConnectionBuilder WithUri(string value) => this;
            public ConnectionBuilder WithDatabaseName(string value) => this;
            public ConnectionBuilder WithToken(string value) => this;
            public ConnectionBuilder WithCompression(SpacetimeDB.Compression value) => this;
            public ConnectionBuilder OnConnect(Action<DbConnection, object, string> callback) { connected = callback; return this; }
            public ConnectionBuilder OnConnectError(Action<Exception> callback) => this;
            public ConnectionBuilder OnDisconnect(Action<DbConnection, Exception> callback) => this;
            public DbConnection Build() { return LastBuilt = new DbConnection { connected = connected }; }
        }
    }
    public sealed class RemoteReducers
    {
        public delegate void JoinHandler(ReducerEventContext ctx, string code, string name);
        public delegate void AttemptHandler(ReducerEventContext ctx, string session, string exercise, string version);
        public delegate void SnapshotHandler(ReducerEventContext ctx, string session, string attempt, string mode, string step,
            uint index, uint count, string selected, bool clearSelected, string highlighted, bool clearHighlighted,
            bool rotating, bool paused, string registration, string reason, string recording);
        public delegate void ResolutionHandler(ReducerEventContext ctx, string id, string outcome, string reason);
        public delegate void EventHandler(ReducerEventContext ctx, string session, string attempt, string kind, string step, string structure, string message, double deviceTime);
        public delegate void ResultHandler(ReducerEventContext ctx, string attempt, string status, uint complete, uint total, uint mistakes, uint hints, string summary);
        public event JoinHandler OnJoinSession;
        public event AttemptHandler OnStartAttempt;
        public event SnapshotHandler OnPublishExerciseState;
        public event ResolutionHandler OnResolveCommand;
        public event EventHandler OnAppendExerciseEvent;
        public event ResultHandler OnSetAttemptResult;

        public sealed class SnapshotCall
        {
            public string Session, Attempt, Mode, Step, Selected, Highlighted, Registration, Reason, Recording;
            public uint Index, Count;
            public bool ClearSelected, ClearHighlighted, Rotating, Paused;
        }
        public sealed class ResolutionCall { public string Id, Outcome, Reason; }
        public sealed class AttemptCall { public string Session, Exercise, Version; }
        public readonly List<SnapshotCall> Snapshots = new List<SnapshotCall>();
        public readonly List<ResolutionCall> Resolutions = new List<ResolutionCall>();
        public readonly List<AttemptCall> Attempts = new List<AttemptCall>();
        public int Events, Results;
        public int Joins;
        public bool JoinCommitted = true;
        public DateTimeOffset AttemptTimestamp;
        string resultAttempt;
        uint resultComplete, resultTotal, resultMistakes, resultHints;
        string resultSummary, resultStatus;
        public void JoinSession(string code, string name) { Joins++; OnJoinSession?.Invoke(ReducerEventContext.Result(JoinCommitted), code, name); }
        public void StartAttempt(string session, string exercise, string version)
        { Attempts.Add(new AttemptCall { Session = session, Exercise = exercise, Version = version }); }
        public void AckAttempt(bool committed, DateTimeOffset? timestamp = null)
        {
            var c = Attempts[Attempts.Count - 1]; var ctx = ReducerEventContext.Result(committed);
            AttemptTimestamp = ctx.Event.Timestamp = timestamp ?? DateTimeOffset.UtcNow;
            OnStartAttempt?.Invoke(ctx, c.Session, c.Exercise, c.Version);
        }
        public void AckForeignAttempt(string session, string exercise, string version, bool committed)
        {
            var ctx = ReducerEventContext.Result(committed);
            ctx.Event.CallerIdentity = new SpacetimeDB.Identity("operator");
            OnStartAttempt?.Invoke(ctx, session, exercise, version);
        }
        public void PublishExerciseState(string session, string attempt, string mode, string step, uint index, uint count,
            string selected, bool clearSelected, string highlighted, bool clearHighlighted, bool rotating, bool paused,
            string registration, string reason, string recording)
        { Snapshots.Add(new SnapshotCall { Session = session, Attempt = attempt, Mode = mode, Step = step, Index = index, Count = count,
            Selected = selected, ClearSelected = clearSelected, Highlighted = highlighted, ClearHighlighted = clearHighlighted,
            Rotating = rotating, Paused = paused, Registration = registration, Reason = reason, Recording = recording }); }
        public void AckSnapshot(bool committed)
        { var c = Snapshots[Snapshots.Count - 1]; OnPublishExerciseState?.Invoke(ReducerEventContext.Result(committed), c.Session, c.Attempt,
            c.Mode, c.Step, c.Index, c.Count, c.Selected, c.ClearSelected, c.Highlighted, c.ClearHighlighted,
            c.Rotating, c.Paused, c.Registration, c.Reason, c.Recording); }
        public void ResolveCommand(string id, string outcome, string reason)
        { Resolutions.Add(new ResolutionCall { Id = id, Outcome = outcome, Reason = reason }); }
        public void AckResolution(bool committed)
        { var c = Resolutions[Resolutions.Count - 1]; OnResolveCommand?.Invoke(ReducerEventContext.Result(committed), c.Id, c.Outcome, c.Reason); }
        public void AppendExerciseEvent(string session, string attempt, string kind, string step, string structure, string message, double deviceTime)
        { Events++; }
        public void RejectEvent()
        { OnAppendExerciseEvent?.Invoke(ReducerEventContext.Result(false), "fixture", "fixture-a1", "touch", "inspect", "appendix", "", 0); }
        public void SetAttemptResult(string attempt, string status, uint complete, uint total, uint mistakes, uint hints, string summary)
        { Results++; resultAttempt = attempt; resultStatus = status; resultComplete = complete; resultTotal = total; resultMistakes = mistakes; resultHints = hints; resultSummary = summary; }
        public void AckResult(bool committed)
        { OnSetAttemptResult?.Invoke(ReducerEventContext.Result(committed), resultAttempt, resultStatus, resultComplete, resultTotal, resultMistakes, resultHints, resultSummary); }
    }
}
