using System;
using System.Linq;
using System.IO;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Quest;
using Scalpal.Handoff;
using Scalpal.Handoff.Editor;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    public static class RecapIntegrationValidation
    {
        static int checks;
        static void Check(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException("Recap integration: " + message);
        }
        [MenuItem("Scalpal/Recap/Verify Session Integration")]
        public static void Run()
        {
            checks = 0;
            Check(typeof(NativeCaseSession).GetMethod("BeginRecapRun", BindingFlags.NonPublic | BindingFlags.Instance) != null,
                "native practice must begin the real recap context");
            Check(typeof(NativeCaseSession).GetMethod("CompleteRecapRun", BindingFlags.NonPublic | BindingFlags.Instance) != null,
                "native completion must close the segment and open RunEnding");
            Check(EditorBuildSettings.scenes.Any(s => s.enabled && s.path == "Assets/Scalpal/Recap/Scenes/RunEnding.unity"),
                "RunEnding is included in the single-player build");
            Check(ScalpalPlayerBuild.PlayerScenes().Last().path == "Assets/Scalpal/Recap/Scenes/RunEnding.unity", "canonical unified builder retains RunEnding last");
            VerifyRealProducers();
            VerifyCanonicalHandoff();
            Debug.Log("SCALPAL_RECAP_INTEGRATION_OK checks=" + checks + " physicalHeadset=false liveService=false");
        }
        static void VerifyCanonicalHandoff()
        {
            var score = JsonUtility.FromJson<EncounterScore>(File.ReadAllText("../companion/tests/fixtures/preop-scorecard.json"));
            var bundle = JsonUtility.FromJson<ScalpalBundle>(Resources.Load<TextAsset>("scalpal_bundle").text);
            var selected = bundle.cases.First(c => c.patientId == score.patientId && c.procedureId == score.procedureId);
            var state = new EncounterState { patientId = score.patientId, encounterId = "enc-canonicalrecap", phase = "scored",
                assessment = new EncounterAssessment { procedure = score.procedureId } };
            Check(EncounterOfficeRoute.PrepareSurgery(state, score, selected.procedureId, "http://localhost:8787", out _, "canonical-session", "office-attempt"), "canonical source prepared by office");
            EncounterOfficeRoute.TakeSurgery(out var office);
            HandoffRun.Preflight.demoMode = true;
            var ticket = HandoffRun.Begin(state, score, office.serviceUrl);
            HandoffRun.BindOfficeSource(ticket, office);
            HandoffRun.Preflight.demoMode = false;
            Check(ticket.demoMode && JsonUtility.FromJson<HandoffTicket>(JsonUtility.ToJson(ticket)).demoMode, "canonical run freezes and serializes producer demo flag");
            var integration = RecapSessionIntegration.Ensure();
            var context = integration.GetComponent<RecapRunContext>();
            var nativeRoot = new GameObject("Canonical coach request fixture");
            try
            {
                var native = nativeRoot.AddComponent<NativeCaseSession>();
                Check(native.CoachRequest().runId == ticket.runId && native.CoachRequest().encounterId == ticket.encounterId,
                    "actual native initial/recovery coach request carries canonical run identity");
                integration.Begin(ticket, selected, ticket.sharedSessionId, ticket.attemptId, "", office.serviceUrl, "");
                Check(context.result.runId == ticket.runId && context.result.diagnosis.total == score.total && context.result.demo.enabled,
                    "caption-only native attempt keeps canonical run and frozen flags without inventing coach ID");
                Check(context.EndSurgery(ticket.attemptId, null), "caption-only ending remains available with missing grader");
                ticket.practiceStarted = ticket.timeOutConfirmed = true;
                Check(integration.PrepareRetry(out _), "canonical completed run can retry");
                Check(!ticket.practiceStarted && !ticket.timeOutConfirmed && ticket.attemptId == "" && ticket.sourceOffice.attemptId == "office-attempt",
                    "retry clears practice/time-out/current attempt while preserving original scored provenance");
                Check(RecapSessionIntegration.TakeRetry(out _, out var session, out var previous, out var original)
                    && previous == "office-attempt" && original == "office-attempt", "retry carries old IDs for fresh-attempt comparison");
                ticket.attemptId = "surgery-retry";
                Check(RecapSessionIntegration.IsFreshRetry(session, previous, ticket.sharedSessionId, ticket.attemptId), "confirmed retry id differs from original");
                integration.Begin(ticket, selected, ticket.sharedSessionId, ticket.attemptId, "coach-recovered", office.serviceUrl, "");
                Check(context.result.attemptId == "surgery-retry" && context.result.runId == ticket.runId && !context.SegmentClosed
                    && !context.result.surgery.available && context.result.surgery.demoAssisted && context.result.replay.jobId == "",
                    "new surgical attempt preserves canonical run/assistance but clears result and motion job");
                context.EndSurgery(ticket.attemptId, null);
                Check(integration.PrepareRetry(out _) && RecapSessionIntegration.TakeRetry(out _, out _, out previous, out original)
                    && previous == "surgery-retry" && original == "office-attempt" && ticket.sourceOffice.scorecard.total == score.total,
                    "second retry still preserves original scored encounter and diagnosis");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(nativeRoot);
                UnityEngine.Object.DestroyImmediate(context.gameObject);
                HandoffRun.Clear(); HandoffRun.Preflight.demoMode = false; EncounterOfficeRoute.ClearSurgery();
            }
        }
        static void VerifyRealProducers()
        {
            // Generated by the real preop EncounterService; no invented risksFound/risksMissed.
            var score = JsonUtility.FromJson<EncounterScore>(File.ReadAllText("../companion/tests/fixtures/preop-scorecard.json"));
            var bundle = JsonUtility.FromJson<ScalpalBundle>(Resources.Load<TextAsset>("scalpal_bundle").text);
            var selected = bundle.cases.First(c => c.patientId == score.patientId && c.procedureId == score.procedureId);
            var state = new EncounterState { patientId = score.patientId, encounterId = "enc-recapintegration", phase = "scored",
                assessment = new EncounterAssessment { diagnosis = score.diagnosisGiven ?? "", procedure = score.procedureId } };
            Check(EncounterOfficeRoute.PrepareSurgery(state, score, selected.procedureId, "http://localhost:8787", out var reason,
                "session-integration", "attempt-integration"), "actual office producer accepts scored case: " + reason);
            Check(EncounterOfficeRoute.TakeSurgery(out var handoff), "actual scored office handoff consumed");
            var integration = RecapSessionIntegration.Ensure();
            var context = integration.GetComponent<RecapRunContext>();
            var anatomyRoot = new GameObject("Recap boundary fixture");
            try
            {
                integration.Begin(handoff, selected, handoff.sharedSessionId, handoff.attemptId, "coach-integration", handoff.serviceUrl, "");
                Check(context.result.diagnosisAvailable && context.result.diagnosis.total == score.total,
                    "real office score reaches recap unchanged");
                Check(context.result.runId == "coach-integration" && !context.result.surgery.available,
                    "server coach identity retained and absent aggregate grade stays unavailable");
                bool rejected = false;
                try { RecapSessionIntegration.FromSession(handoff, selected, handoff.sharedSessionId, "stale-attempt", "coach", new DemoFlags()); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "mismatched office and OR attempt rejected");
                var anatomy = anatomyRoot.AddComponent<AnatomyController>();
                foreach (var structure in selected.anatomy)
                {
                    var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    primitive.transform.SetParent(anatomyRoot.transform, false);
                    var part = primitive.AddComponent<AnatomyPart>(); part.stableId = structure.id; part.system = structure.system;
                }
                var exercise = anatomyRoot.AddComponent<AnatomyExerciseBinding>(); exercise.anatomy = anatomy;
                Check(exercise.SelectCase(bundle, selected.caseId, true, out reason), "real case selected through anatomy boundary: " + reason);
                anatomy.SetRegistrationValid(true);
                Check(!integration.Complete(exercise, handoff.attemptId), "incomplete real exercise cannot open recap");
                Check(!integration.PrepareRetry(out _), "unfinished run cannot schedule recap retry");
                int endings = 0;
                exercise.EventHandled += (action, result) => { if (result.completed && integration.Complete(exercise, handoff.attemptId)) endings++; };
                foreach (var step in selected.procedure.steps)
                    foreach (var action in CaseRunner.PerfectEvents(step))
                        Check(exercise.Submit(action, out _, out reason), "authored action accepted at real boundary: " + reason);
                Check(exercise.Completed && endings == 1 && context.SegmentClosed, "actual exercise completion closes recap exactly once");
                Check(!integration.Complete(exercise, handoff.attemptId) && !integration.Complete(exercise, "stale-attempt"), "duplicate and stale completion rejected");
                Check(context.result.replay.status == "failed" && !context.result.surgery.available,
                    "missing capture and aggregate grader remain honest");
                Check(!RecapSessionIntegration.IsFreshRetry(handoff.sharedSessionId, handoff.attemptId, handoff.sharedSessionId, handoff.attemptId), "same attempt cannot retry");
                Check(!RecapSessionIntegration.IsFreshRetry(handoff.sharedSessionId, handoff.attemptId, "other-session", "new-attempt"), "cross-session retry rejected");
                Check(RecapSessionIntegration.IsFreshRetry(handoff.sharedSessionId, handoff.attemptId, handoff.sharedSessionId, "new-attempt"), "new confirmed attempt allowed");
                Check(integration.PrepareRetry(out reason), "closed run can schedule retry: " + reason);
                Check(RecapSessionIntegration.TakeRetry(out var retryOffice, out var retrySession, out var previousAttempt, out var encounterAttempt)
                    && retrySession == handoff.sharedSessionId && previousAttempt == handoff.attemptId && encounterAttempt == handoff.attemptId
                    && retryOffice.scorecard.total == score.total, "retry snapshot preserves real diagnosis and original attempt provenance");
                Check(!RecapSessionIntegration.TakeRetry(out _, out _, out _, out _), "retry snapshot consumed exactly once");
                exercise.StopAttempt();
                Check(!exercise.Completed && !exercise.CanScore && exercise.Body == null, "old exercise reset invalidates scoring/body");
                handoff.attemptId = "new-attempt";
                integration.Begin(handoff, selected, handoff.sharedSessionId, handoff.attemptId, "coach-retry", handoff.serviceUrl, "");
                Check(!context.SegmentClosed && !context.result.surgery.available && string.IsNullOrEmpty(context.result.replay.jobId)
                    && context.result.diagnosis.total == score.total, "new attempt clears grade/capture while preserving diagnosis");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(anatomyRoot);
                UnityEngine.Object.DestroyImmediate(context.gameObject);
                EncounterOfficeRoute.ClearSurgery();
            }
        }
    }
}
