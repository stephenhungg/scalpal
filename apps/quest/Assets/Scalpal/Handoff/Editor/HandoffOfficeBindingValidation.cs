using System;
using System.Reflection;
using Scalpal.EncounterOffice;
using UnityEngine;

namespace Scalpal.Handoff.Editor
{
    // Production snapshot/matcher/office producer with synthetic captured IDs; no reducer evidence. The office source is
    // a scored choice interview (int-...) whose plan pick was wrong: the OR still loads the case's surgery.
    public static class HandoffOfficeBindingValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        public static void Verify()
        {
            checks = 0;
            var previous = HandoffRun.Current;
            var fixture = new GameObject("HandoffOfficeSourceFixture"); fixture.SetActive(false);
            try
            {
                var state = new EncounterState { patientId = "patient-office-fixture", encounterId = "int-officebinding123", phase = "scored",
                    assessment = new EncounterAssessment { procedure = "wrong proposed procedure", differential = new[] { "fixture differential" } } };
                var score = new EncounterScore { kind = "interview", patientId = state.patientId, procedureId = "lap_appendectomy", total = 78, max = 100, grade = "Solid",
                    procedureChosenCorrectly = false, diagnosisResult = "correct",
                    rounds = new[] { new InterviewRoundResult { stage = "plan", prompt = "What is the plan?", points = 0, max = 15,
                        picked = new InterviewPickedChoice { key = "B", text = "wrong proposed procedure", grade = "wrong" }, best = new InterviewPickedChoice { key = "A", text = "Laparoscopic appendectomy" } } },
                    carryoverItems = new[] { new EncounterCarryoverItem { flagId = "latex", label = "Latex allergy", status = "found", historyTopics = new[] { "allergies" } } } };
                var office = fixture.AddComponent<NativeEncounterSession>();
                Property(office, "State", state); Property(office, "Score", score); Property(office, "AuthoredProcedureId", score.procedureId);
                Set(office, "patientId", state.patientId); Set(office, "encounterId", state.encounterId);
                Set(office, "sharedSessionId", "source-session"); Set(office, "sharedAttemptId", "source-attempt");
                Assert(office.TryPrepareHandoff(out var source, out var reason) && reason == "", "actual office producer prepares valid scored snapshot with synthetic captured IDs");
                Assert(source.sharedSessionId == "source-session" && source.attemptId == "source-attempt", "producer preserves privately captured IDs");
                Assert(!EncounterOfficeRoute.TakeSurgery(out _), "producer consumes legacy transport pointer exactly once");
                var ticket = HandoffRun.Begin(state, score, office.baseUrl);
                HandoffRun.BindOfficeSource(ticket, source);
                Assert(ticket.encounterId == "int-officebinding123" && ticket.escalated && ticket.procedureId == "lap_appendectomy" && ticket.learnerProcedure == "wrong proposed procedure",
                    "a wrong plan pick escalates (challenge and consequence) yet still routes to the case's surgery");
                Assert(HandoffRun.CanChoose("mixed_reality", new TheatrePreflight
                    { volunteerConsented = true, cameraGranted = true, sceneGranted = true, poseServiceOk = true, coachServiceOk = true }),
                    "office source permits AR when every operator preflight gate is green");
                string snapshot = JsonUtility.ToJson(ticket.sourceOffice);
                Assert(ticket.attemptId == source.attemptId && ticket.sharedSessionId == source.sharedSessionId, "initial OR attempt adopts original source IDs");
                source.assessment.procedure = "mutated proposal";
                source.assessment.differential[0] = "mutated differential";
                source.scorecard.carryoverItems[0].label = "mutated risk";
                source.scorecard.carryoverItems[0].historyTopics[0] = "mutated evidence";
                source.attemptId = "mutated source";
                Assert(JsonUtility.ToJson(ticket.sourceOffice) == snapshot, "bound original office source is a deep snapshot of assessment, risks and IDs");
                Assert(ticket.sourceOffice.assessment.procedure == "wrong proposed procedure" && ticket.sourceOffice.procedureId == "lap_appendectomy", "snapshot preserves wrong learner plan plus correct authored surgery");
                Assert(HandoffRun.SourceBindingMatches(ticket, "source-session", "source-attempt", state.patientId, "scored"), "exact scored source binding accepted");
                Assert(!HandoffRun.SourceBindingMatches(null, "source-session", "source-attempt", state.patientId, "scored")
                    && !HandoffRun.SourceBindingMatches(new HandoffTicket(), "source-session", "source-attempt", state.patientId, "scored"), "missing ticket/source rejected");
                foreach (var values in new[] {
                    new[] { "other-session", "source-attempt", state.patientId, "scored" },
                    new[] { "source-session", "other-attempt", state.patientId, "scored" },
                    new[] { "source-session", "source-attempt", "other-patient", "scored" },
                    new[] { "source-session", "source-attempt", state.patientId, "attending" },
                    new[] { "", "source-attempt", state.patientId, "scored" },
                    new[] { "source-session", "", state.patientId, "scored" },
                    new[] { "source-session", "source-attempt", "", "scored" },
                    new[] { "source-session", "source-attempt", state.patientId, "" } })
                    Assert(!HandoffRun.SourceBindingMatches(ticket, values[0], values[1], values[2], values[3]), "mismatched or missing observed source field rejected");
                ticket.practiceStarted = true; ticket.attemptId = "new-or-attempt"; ticket.ResetTimeOut();
                Assert(JsonUtility.ToJson(ticket.sourceOffice) == snapshot
                    && HandoffRun.SourceBindingMatches(ticket, "source-session", "source-attempt", state.patientId, "scored"), "new OR attempt/reset preserves immutable source encounter binding");
                Assert(!HandoffRun.SourceBindingMatches(ticket, "source-session", "new-or-attempt", state.patientId, "scored"), "current OR retry is never mistaken for original office provenance");
                foreach (Action<EncounterSurgeryHandoff> change in new Action<EncounterSurgeryHandoff>[] {
                    value => value.sharedSessionId = "", value => value.attemptId = "", value => value.patientId = "other-patient",
                    value => value.encounterId = "enc-other123", value => value.procedureId = "lap_cholecystectomy" })
                {
                    var invalid = JsonUtility.FromJson<EncounterSurgeryHandoff>(snapshot); change(invalid);
                    Throws(() => HandoffRun.BindOfficeSource(ticket, invalid), "invalid office source cannot replace canonical binding");
                    Assert(JsonUtility.ToJson(ticket.sourceOffice) == snapshot && ticket.attemptId == "new-or-attempt", "rejected source leaves original/current attempts untouched");
                }
                Throws(() => HandoffRun.BindOfficeSource(ticket, null), "null office source rejected");
                Throws(() => HandoffRun.BindOfficeSource(null, ticket.sourceOffice), "null target ticket rejected");
                Set(office, "blocking", 1);
                Assert(!office.TryPrepareHandoff(out _, out reason) && reason.Length > 0, "busy office cannot produce handoff");
                Set(office, "blocking", 0); state.phase = "interview";
                Assert(!office.TryPrepareHandoff(out _, out reason), "unscored office cannot produce handoff");
                state.phase = "scored"; Property(office, "AuthoredProcedureId", "lap_cholecystectomy");
                Assert(!office.TryPrepareHandoff(out _, out reason), "mismatched authored surgery cannot produce handoff");
                Property(office, "AuthoredProcedureId", score.procedureId);
                Set(office, "sharedSessionId", ""); Set(office, "sharedAttemptId", "");
                Assert(office.TryPrepareHandoff(out var isolated, out reason), "isolated office helper may prepare unpaired snapshot for component consumers");
                Throws(() => HandoffRun.BindOfficeSource(ticket, isolated), "canonical theatre refuses unpaired component snapshot");
                Debug.Log("SCALPAL_HANDOFF_OFFICE_BINDING_VALIDATION_OK checks=" + checks
                    + " actual office snapshot and source matcher; synthetic shared IDs, no realtime reducer or physical evidence");
            }
            finally
            {
                typeof(HandoffRun).GetProperty("Current").GetSetMethod(true).Invoke(null, new object[] { previous });
                EncounterOfficeRoute.ClearSurgery(); UnityEngine.Object.DestroyImmediate(fixture);
            }
        }
        static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Private).SetValue(instance, value);
        static void Property(object instance, string name, object value) => instance.GetType().GetProperty(name).GetSetMethod(true).Invoke(instance, new[] { value });
        static void Throws(Action action, string reason) { try { action(); } catch (ArgumentException) { Assert(true, reason); return; } throw new InvalidOperationException("Handoff office binding validation failed: " + reason); }
        static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException("Handoff office binding validation failed: " + reason); checks++; }
    }
}
