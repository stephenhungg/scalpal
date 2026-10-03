using System.Text;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Engine;
using UnityEngine.Networking;

int checks = 0;
void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
UnityWebRequest Find(string method, string tail) => Scheduler.Requests.Last(r => r.method == method && r.url.EndsWith(tail));
string Body(UnityWebRequest r) => Encoding.UTF8.GetString(r.uploadHandler.data);
void Init(CoachRelay relay, string sid = "session-a")
{
    relay.UseSession(sid, "patient-a", "lap_appendectomy");
    Scheduler.Pump();
    Find("POST", sid + "/events").Complete("{\"results\":[{\"accepted\":true}]}");
    Scheduler.Pump();
}

Scheduler.Reset();
var relay = new CoachRelay();
relay.Forward(CaseEvent.Touch("cecum", "grasper"));
relay.Focus("old-focus");
relay.Tracking(true);
Init(relay);
Check(relay.IsSynchronized && relay.Connected, "acknowledged initial state enables forwarding");
Check(Body(Find("POST", "session-a/events")).Contains("\"valid\":true"), "current registration synchronized to session");
Check(!Body(Find("POST", "session-a/events")).Contains("cecum"), "offline touch not replayed into session");
int polls = Scheduler.Requests.Count(r => r.url.EndsWith("/commands"));
relay.UseSession("session-a", "patient-a", "lap_appendectomy");
Scheduler.Pump();
Check(Scheduler.Requests.Count(r => r.url.EndsWith("/commands")) == polls, "same session does not duplicate polling");
relay.Forward(CaseEvent.Touch("cecum", "grasper"));
Scheduler.Pump();
var oldPost = Find("POST", "session-a/events");
relay.UseSession("session-b", "patient-b", "lap_appendectomy");
Scheduler.Pump();
oldPost.Complete("", false);
Scheduler.Pump();
Check(relay.SyncFailureReason == "", "stale failed event cannot poison replacement session");
Check(!Body(Find("POST", "session-b/events")).Contains("cecum"), "queued progress never crosses sessions");
Find("POST", "session-b/events").Complete("{\"results\":[{\"accepted\":true}]}");
Scheduler.Pump();
Check(relay.IsSynchronized, "new session synchronizes independently");
relay.Forward(CaseEvent.Touch("appendix", "grasper"));
Scheduler.Pump();
var failedPost = Find("POST", "session-b/events");
failedPost.Complete("", false);
Scheduler.Pump();
Check(!relay.IsSynchronized && relay.SyncFailureReason.Length > 0, "failed POST disables synchronization explicitly");
int posts = Scheduler.Requests.Count(r => r.method == "POST");
relay.Forward(CaseEvent.Touch("appendix", "grasper"));
Scheduler.Pump(true);
Check(Scheduler.Requests.Count(r => r.method == "POST") == posts, "non-idempotent failed touch is never retried");

Scheduler.Reset();
relay = new CoachRelay();
Init(relay);
var firstPoll = Find("GET", "session-a/commands");
int commands = 0;
relay.CommandRequested += c => { commands++; relay.Ack(c.commandId, true); };
relay.UseSession("session-b", "patient-b", "lap_appendectomy");
Scheduler.Pump();
firstPoll.Complete("{\"commands\":[{\"commandId\":\"stale\",\"action\":\"highlight\",\"targetId\":\"cecum\"}]}");
Scheduler.Pump();
Check(commands == 0, "stale poll cannot emit into replacement anatomy session");
Find("POST", "session-b/events").Complete("{\"results\":[{\"accepted\":true}]}");
Find("GET", "session-b/commands").Complete("{\"commands\":[{\"commandId\":\"new\",\"action\":\"highlight\",\"targetId\":\"cecum\"}]}");
Scheduler.Pump(); Scheduler.Pump();
Check(commands == 1, "new session command delivered");
Check(Find("POST", "session-b/commands/new/ack") != null, "ack bound to correct session");
posts = Scheduler.Requests.Count;
relay.Ack("stale", true); Scheduler.Pump();
Check(Scheduler.Requests.Count == posts, "unoffered stale ack rejected");

Scheduler.Reset();
relay = new CoachRelay();
relay.AdoptCurrentSession("patient-a", "lap_appendectomy"); Scheduler.Pump();
var oldLookup = Scheduler.Requests.Last();
relay.AdoptCurrentSession("patient-b", "lap_appendectomy"); Scheduler.Pump();
var newLookup = Scheduler.Requests.Last();
oldLookup.Complete("{\"sessionId\":\"old\",\"patientId\":\"patient-a\",\"procedureId\":\"lap_appendectomy\"}"); Scheduler.Pump();
Check(!relay.Connected, "out-of-order adoption ignored");
newLookup.Complete("{\"sessionId\":\"new\",\"patientId\":\"patient-b\",\"procedureId\":\"lap_cholecystectomy\"}"); Scheduler.Pump();
Check(!relay.Connected && relay.SyncFailureReason.Length > 0, "wrong procedure adoption fails explicitly");

Scheduler.Reset();
relay = new CoachRelay();
relay.AdoptCurrentSession("patient-a", "lap_appendectomy"); Scheduler.Pump();
Scheduler.Requests.Last().Complete("{\"sessionId\":\"fresh\",\"patientId\":\"patient-a\",\"procedureId\":\"lap_appendectomy\"}"); Scheduler.Pump();
Find("GET", "/fresh").Complete("{\"snapshot\":{\"sessionId\":\"fresh\",\"patientId\":\"patient-a\",\"procedureId\":\"lap_appendectomy\",\"version\":1,\"stepNumber\":1,\"completedCount\":0,\"status\":\"active\",\"step\":{\"id\":\"access_umbilical\"}}}"); Scheduler.Pump();
Check(!relay.Connected, "partial first-step or prior activity rejected during new attempt adoption");
relay.AdoptCurrentSession("patient-a", "lap_appendectomy"); Scheduler.Pump();
Scheduler.Requests.Last().Complete("{\"sessionId\":\"fresh2\",\"patientId\":\"patient-a\",\"procedureId\":\"lap_appendectomy\"}"); Scheduler.Pump();
Find("GET", "/fresh2").Complete("{\"snapshot\":{\"sessionId\":\"fresh2\",\"patientId\":\"patient-a\",\"procedureId\":\"lap_appendectomy\",\"version\":0,\"stepNumber\":1,\"completedCount\":0,\"status\":\"active\",\"step\":{\"id\":\"access_umbilical\"}}}"); Scheduler.Pump(); Scheduler.Pump();
Check(relay.Connected && relay.SessionInitialStepId == "access_umbilical", "fresh session adoption records first step");
Find("POST", "fresh2/events").Complete("{\"results\":[{\"accepted\":false,\"reason\":\"tracking_invalid\"}]}"); Scheduler.Pump();
Check(!relay.IsSynchronized, "HTTP success with rejected event does not mean synchronized");
Console.WriteLine($"PASS: {checks} coach relay lifecycle checks (controlled network/coroutine doubles).");
