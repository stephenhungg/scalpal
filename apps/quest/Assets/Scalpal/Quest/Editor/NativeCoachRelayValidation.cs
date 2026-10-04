using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // The scene check uses real binding/geometry with a synthetic paired relay and held queue.
    // A separate Mono/Hono harness runs actual relay coroutines against an isolated synthetic service.
    public static class NativeCoachRelayValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;

        [MenuItem("Scalpal/Quest/Validate Native Coach Relay")]
        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            GameObject fixture = null;
            try
            {
                var scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Single);
                NativeCaseSession session = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = root.GetComponentInChildren<NativeCaseSession>(true);
                    if (found) session = found;
                    root.SetActive(false);
                }
                Assert(session && session.exercise && session.anatomy && session.coach, "actual native scene contains required bindings");
                Assert(session.exercise.requireCoachSynchronization && session.exercise.presentationMode == "mixed_reality",
                    "authored native OR defaults to exact mixed-reality coach pairing");
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
                var bundle = JsonUtility.FromJson<ScalpalBundle>(asset.text);
                var candidate = bundle.cases.Single(item => item.patientId == NativeCaseSession.PatientId && item.procedureId == NativeCaseSession.ProcedureId);

                fixture = new GameObject("NativeCoachBindingFixture");
                var anatomyObject = UnityEngine.Object.Instantiate(session.anatomy.gameObject, fixture.transform);
                anatomyObject.SetActive(true);
                var anatomy = anatomyObject.GetComponent<AnatomyController>();
                anatomy.RebuildIndex(); anatomy.SetPreviewMode(false); anatomy.SetPreviewRotation(false);
                var binding = fixture.AddComponent<AnatomyExerciseBinding>();
                binding.anatomy = anatomy;
                Assert(binding.SelectCase(bundle, candidate.caseId, true, out var reason), "actual native geometry validates real packaged case: " + reason);
                var coach = fixture.AddComponent<CoachRelay>();
                // Prevent StartCoroutine during this scene check. Paired identity is synthetic;
                // actual adoption/request delivery is tested by the independent production-source harness.
                Set(coach, "sending", true);
                Property(coach, "SessionId", "synthetic-coach-fixture");
                Property(coach, "SessionPatientId", candidate.patientId);
                Property(coach, "SessionProcedureId", candidate.procedureId);
                Property(coach, "SessionCaseId", candidate.caseId);
                Property(coach, "SessionMode", "virtual");
                Property(coach, "SessionInitialStepId", candidate.procedure.firstStep);
                Property(coach, "IsSynchronized", true);
                binding.coach = coach; binding.requireCoachSynchronization = true; binding.presentationMode = "virtual";
                Set(binding, "liveAttempt", true); Set(binding, "selectedCoach", coach);
                Set(binding, "selectedPresentationMode", "virtual"); Set(binding, "boundSessionId", coach.SessionId);
                var queue = Get<Queue<CoachEventDto>>(coach, "pending");
                anatomy.SetRegistrationValid(true);
                coach.Tracking(true);
                Assert(queue.Count == 1 && queue.Peek().type == "tracking" && queue.Peek().valid, "registration restore precedes any scored action in the queue");

                // A real binding must reject scoring while registration is lost, and preserve the
                // restored tracking-before-action order without mutating the already queued IDs.
                anatomy.SetRegistrationValid(false); coach.Tracking(false);
                int pausedCount = queue.Count;
                var initial = binding.Current.id;
                Assert(!binding.Submit(CaseEvent.PlacePort("umbilical"), out _, out _) && queue.Count == pausedCount && binding.Current.id == initial,
                    "registration pause cannot advance local scoring or enqueue an ignored server event");
                coach.Tracking(true); anatomy.SetRegistrationValid(true);
                Assert(binding.CanScore, "matched coach and restored registration permit intentional scoring");
                foreach (var mismatch in new[] { "SessionId", "SessionCaseId", "SessionMode", "SessionInitialStepId" })
                {
                    var property = coach.GetType().GetProperty(mismatch);
                    var original = property.GetValue(coach);
                    Property(coach, mismatch, mismatch == "SessionMode" ? "mixed_reality" : "synthetic-mismatch");
                    int count = queue.Count;
                    Assert(!binding.Submit(CaseEvent.PlacePort("umbilical"), out _, out _) && queue.Count == count && binding.Current.id == initial,
                        "paired " + mismatch + " replacement blocks actual binding scoring without forwarding");
                    Property(coach, mismatch, original);
                }
                Property(coach, "IsSynchronized", false);
                Assert(!binding.Submit(CaseEvent.PlacePort("umbilical"), out _, out _) && binding.Current.id == initial,
                    "uncertain event acknowledgement blocks the actual local scoring boundary");
                Property(coach, "IsSynchronized", true);
                var ids = new HashSet<string>();
                var committed = new List<CoachEventDto>();
                var originalIds = new List<string>();
                var originalSteps = new List<string>();
                int handled = 0, advanced = 0;
                binding.EventHandled += (_, result) => { handled++; if (result.advanced) advanced++; };
                while (!binding.Completed)
                {
                    var step = binding.Current;
                    foreach (var action in CaseRunner.PerfectEvents(step).ToArray())
                    {
                        string before = binding.Current.id;
                        int count = queue.Count;
                        Assert(binding.Submit(action, out var result, out reason), "actual binding accepts authored action: " + reason);
                        var dto = queue.Last();
                        Assert(queue.Count == count + 1 && dto.stepId == before && result.stepId == before,
                            "actual binding forwards exactly once with the step BEFORE synchronous callbacks advance it");
                        Assert(ids.Add(dto.eventId) && !string.IsNullOrEmpty(dto.eventId), "every intentional scored action receives its own nonempty retry identity");
                        committed.Add(dto);
                        originalIds.Add(dto.eventId); originalSteps.Add(before);
                        var after = queue.ToArray();
                        Assert(after[0].type == "tracking" && after[0].valid && after[1].type == "tracking" && !after[1].valid && after[2].valid,
                            "scoring queue preserves registration transition order");
                    }
                }
                Assert(handled == committed.Count && advanced == candidate.procedure.steps.Length, "one local event callback per forwarded action completes every authored step");
                Assert(!binding.Submit(CaseEvent.Confirm(), out _, out _) && queue.Count == committed.Count + 3,
                    "completed binding cannot publish an extra score event");
                Assert(committed.Select(dto => dto.eventId).SequenceEqual(originalIds) && committed.Select(dto => dto.stepId).SequenceEqual(originalSteps),
                    "held DTO IDs survive later local progression without rewritten step metadata");
                binding.StopAttempt();
                Assert(!binding.CanScore && !coach.Connected && queue.Count == 0, "abandoning the actual binding clears old pending scoring and coach ownership");
                UnityEngine.Debug.Log("SCALPAL_NATIVE_COACH_BINDING_VALIDATION_OK checks=" + checks + " actions=" + handled
                    + " actual native scene/case/geometry and binding; synthetic paired metadata/held transport queue");
            }
            finally
            {
                if (fixture) UnityEngine.Object.DestroyImmediate(fixture);
                if (previous.Any(item => item.isLoaded && item.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void Property(object target, string name, object value) { target.GetType().GetProperty(name).GetSetMethod(true).Invoke(target, new[] { value }); }
        static void Set(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
        static T Get<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
        static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException("Native coach binding validation failed: " + reason); checks++; }
    }
}
