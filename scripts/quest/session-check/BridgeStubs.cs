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
    public abstract class Status { public sealed class Committed : Status { } public sealed class Failed : Status { } }
}
namespace SpacetimeDB.Types
{
    public sealed class ReducerEventContext
    {
        public EventData Event = new EventData();
        public sealed class EventData { public SpacetimeDB.Status Status; }
        public static ReducerEventContext Result(bool committed) => new ReducerEventContext {
            Event = new EventData { Status = committed ? (SpacetimeDB.Status)new SpacetimeDB.Status.Committed() : new SpacetimeDB.Status.Failed() } };
    }
    public sealed class Session
    { public string SessionId, CurrentAttemptId, ExerciseId, ExerciseVersion, Status = "active"; }
    public sealed class Membership { public string Role, SessionId; }
    public sealed class ExerciseState
    { public string SessionId, AttemptId; public ulong StepVersion = 1; }
    public sealed class Encounter
    { public string EncounterId, SessionId, AttemptId, PatientId, Phase; }
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
        public Index<T> SessionId, CommandId, EncounterId;
        public IEnumerable<T> Iter() => Rows;
        public Table(Func<T, string> key)
        { SessionId = CommandId = EncounterId = new Index<T>(value => Rows.Find(row => key(row) == value)); }
    }
    public sealed class Cache
    {
        public readonly Table<Session> MySessions = new Table<Session>(row => row.SessionId);
        public readonly Table<Membership> MyMemberships = new Table<Membership>(row => row.SessionId);
        public readonly Table<ExerciseState> SessionExerciseState = new Table<ExerciseState>(row => row.SessionId);
        public readonly Table<Command> SessionCommands = new Table<Command>(row => row.CommandId);
        public readonly Table<Encounter> SessionEncounters = new Table<Encounter>(row => row.EncounterId);
    }
    public sealed class SubscriptionHandle { public int UnsubscribeCount; public void Unsubscribe() { UnsubscribeCount++; } }
    public sealed class SubscribeBuilder
    {
        Action<object> applied;
        public SubscribeBuilder OnApplied(Action<object> callback) { applied = callback; return this; }
        public SubscribeBuilder OnError(Action<object, Exception> callback) => this;
        public SubscriptionHandle Subscribe(string[] queries) { applied?.Invoke(null); return new SubscriptionHandle(); }
    }
    public sealed class DbConnection
    {
        public bool IsActive = true;
        public int DisconnectCount, FrameTickCount;
        public readonly Cache Db = new Cache();
        public readonly RemoteReducers Reducers = new RemoteReducers();
        public void FrameTick() { FrameTickCount++; }
        public void Disconnect() { DisconnectCount++; IsActive = false; }
        public SubscribeBuilder SubscriptionBuilder() => new SubscribeBuilder();
        public static ConnectionBuilder Builder() => new ConnectionBuilder();
        public sealed class ConnectionBuilder
        {
            public ConnectionBuilder WithUri(string value) => this;
            public ConnectionBuilder WithDatabaseName(string value) => this;
            public ConnectionBuilder WithToken(string value) => this;
            public ConnectionBuilder WithCompression(SpacetimeDB.Compression value) => this;
            public ConnectionBuilder OnConnect(Action<DbConnection, object, string> callback) => this;
            public ConnectionBuilder OnConnectError(Action<Exception> callback) => this;
            public ConnectionBuilder OnDisconnect(Action<DbConnection, Exception> callback) => this;
            public DbConnection Build() => new DbConnection();
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
        string resultAttempt;
        uint resultComplete, resultTotal, resultMistakes, resultHints;
        string resultSummary, resultStatus;
        public void JoinSession(string code, string name) { OnJoinSession?.Invoke(ReducerEventContext.Result(true), code, name); }
        public void StartAttempt(string session, string exercise, string version)
        { Attempts.Add(new AttemptCall { Session = session, Exercise = exercise, Version = version }); }
        public void AckAttempt(bool committed)
        { var c = Attempts[Attempts.Count - 1]; OnStartAttempt?.Invoke(ReducerEventContext.Result(committed), c.Session, c.Exercise, c.Version); }
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
