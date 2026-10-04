using System;
using System.Reflection;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Realtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic editor boundary regressions using the committed native scene and real case/assets.
    // Inactive scene roots prevent hardware, HTTP, microphone and shared-state connections.
    public static class NativeSessionBoundaryValidation
    {
        const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;

        [MenuItem("Scalpal/Quest/Validate Native Session Boundaries")]
        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            GameObject scoringRoot = null;
            QuestSessionBridge bridge = null;
            Action<string> failed = null;
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
                Assert(session && session.workbench && session.anatomy && session.preview && session.voice && session.realtime,
                    "real native scene has its required session bindings");
                bridge = session.realtime;
                bridge.autoConnect = false;
                Call(session.workbench, "Awake"); // Cache real tool/reset poses without XR Update.
                var bundleAsset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
                Assert(bundleAsset, "real packaged case bundle exists");
                var bundle = JsonUtility.FromJson<ScalpalBundle>(bundleAsset.text);
                SurgicalCase candidate = null;
                foreach (var item in bundle.cases)
                    if (item.patientId == NativeCaseSession.PatientId && item.procedureId == NativeCaseSession.ProcedureId) candidate = item;
                Assert(candidate != null, "actual adult appendectomy fixture exists");

                scoringRoot = new GameObject("NativeSessionBoundaryScoringFixture");
                var anatomyObject = UnityEngine.Object.Instantiate(session.anatomy.gameObject, scoringRoot.transform);
                anatomyObject.name = "RealExerciseAnatomy_SyntheticScoring";
                anatomyObject.SetActive(true);
                var anatomy = anatomyObject.GetComponent<AnatomyController>();
                anatomy.SetPreviewMode(false);
                anatomy.SetPreviewRotation(false);
                anatomy.RebuildIndex();
                var exercise = scoringRoot.AddComponent<AnatomyExerciseBinding>();
                exercise.anatomy = anatomy;
                // Isolate attempt ownership from HTTP coach synchronization. The actual case engine
                // and imported target geometry still supply the positive/negative scoring controls.
                exercise.requireCoachSynchronization = false;
                session.exercise = exercise;
                session.anatomy = anatomy;
                var input = scoringRoot.AddComponent<NativeProcedureInput>();
                Set(session, "input", input);
                Set(session, "candidate", candidate);
                // This inactive fixture bypasses LoadCase, which normally adopts the service identity.
                SetProperty(session, "SelectedPatientId", candidate.patientId);
                SetProperty(session, "SelectedProcedureId", candidate.procedureId);
                Set(session, "previewScale", session.preview.transform.parent.localScale);
                Set(session, "nextUi", float.PositiveInfinity);
                Set(session, "nextContext", float.PositiveInfinity);
                SetProperty(session.workbench, "IsReady", false); // Never query hardware buttons.

                ExternalIdentityChange(session, bundle, candidate, false);
                ExternalIdentityChange(session, bundle, candidate, true);

                failed = reason => Call(session, "AttemptFailed", reason);
                bridge.AttemptFailed += failed;
                InterruptedAttempt(session, candidate);
                Debug.Log("SCALPAL_NATIVE_SESSION_BOUNDARY_VALIDATION_OK checks=" + checks
                    + " synthetic editor identities/callbacks with real scene/case/assets; no HTTP, headset or reducer commit validation");
            }
            finally
            {
                if (bridge && failed != null) bridge.AttemptFailed -= failed;
                if (scoringRoot) UnityEngine.Object.DestroyImmediate(scoringRoot);
                if (previous.Any(item => item.isLoaded && item.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void ExternalIdentityChange(NativeCaseSession session, ScalpalBundle bundle, SurgicalCase candidate, bool changeSession)
        {
            Assert(session.exercise.SelectCase(bundle, candidate.caseId, true, out var reason), "prepare actual case: " + reason);
            session.anatomy.SetRegistrationValid(true);
            SetProperty(session, "Phase", "Practicing");
            Set(session, "practicePaused", false);
            Set(session, "sharedAttemptReady", true);
            Set(session, "attemptFailed", false);
            Set(session, "attemptRequested", false);
            Set(session, "boundSharedSession", "synthetic-session-a");
            Set(session, "boundSharedAttempt", "synthetic-attempt-a");
            SetProperty(session.realtime, "Paired", true);
            SetProperty(session.realtime, "SessionId", "synthetic-session-a");
            SetProperty(session.realtime, "AttemptId", "synthetic-attempt-a");
            SetProperty(session.voice, "Status", "connected");
            SetProperty(session.voice, "CoachSessionId", "coach-synthetic");
            Set(session, "coachSessionId", "coach-synthetic");
            session.preview.gameObject.SetActive(false);

            Assert(session.exercise.CanScore && session.Practicing && session.voice.Connected,
                "positive control: local case can score with a matched shared identity");
            Assert(session.exercise.Submit(CaseEvent.PlacePort("umbilical"), out var accepted, out reason) && accepted.advanced,
                "positive control: real authored umbilical placement advances");
            Assert(session.exercise.Current != null && session.exercise.Current.id == "working_ports",
                "positive control: ongoing local progression exists before replacement");
            bool visible = false;
            foreach (var renderer in session.anatomy.GetComponentsInChildren<Renderer>(true)) visible |= renderer.enabled;
            Assert(visible, "positive control: registered practice geometry is displayed");
            Call(session, "Publish");
            long beforeGeneration = Get<long>(session.realtime, "generation");
            object priorSnapshot = Get<object>(session.realtime, "latest");
            Assert(priorSnapshot != null, "positive control: matching attempt may publish a snapshot");

            SetProperty(session.realtime, changeSession ? "SessionId" : "AttemptId",
                changeSession ? "synthetic-session-b" : "synthetic-attempt-b");
            Call(session, "Update");
            Assert(!session.Practicing && session.Phase == "Selecting", "external identity replacement abandons practice");
            Assert(session.exercise.Current == null && !session.exercise.CanScore && session.exercise.SelectedCase == null,
                "external identity replacement destroys the old case runner");
            Assert(!session.exercise.Submit(CaseEvent.PlacePort("left_lower"), out _, out _),
                "old attempt cannot accept its next authored action");
            Assert(!session.anatomy.RegistrationValid && !session.anatomy.CanDisplay,
                "external identity replacement invalidates authored anatomy visibility");
            foreach (var renderer in session.anatomy.GetComponentsInChildren<Renderer>(true))
                Assert(!renderer.enabled, "old practice renderer is hidden");
            foreach (var collider in session.anatomy.GetComponentsInChildren<Collider>(true))
                Assert(!collider.enabled, "old practice collider cannot receive tool contacts");
            Assert(!session.voice.Connected && session.voice.CoachSessionId == "" && Get<string>(session, "coachSessionId") == "",
                "external identity replacement disconnects the previous coach conversation");
            Call(session, "Publish");
            Assert(Get<long>(session.realtime, "generation") == beforeGeneration
                && ReferenceEquals(Get<object>(session.realtime, "latest"), priorSnapshot),
                "old local progress cannot publish a new snapshot under the replacement identity");
            Assert(!session.realtime.AttemptPending && !Get<bool>(session, "attemptRequested"),
                "identity replacement waits for explicit retry instead of silently creating an attempt");
        }

        static void InterruptedAttempt(NativeCaseSession session, SurgicalCase candidate)
        {
            SetProperty(session, "Phase", "Confirmed");
            Set(session, "candidate", candidate);
            Set(session, "sharedAttemptReady", false);
            Set(session, "attemptFailed", false);
            Set(session, "attemptRequested", true);
            Set(session.realtime, "attemptInFlight", true);
            Set(session.realtime, "attemptCommitted", false);
            Call(session.realtime, "Disconnected", "Synthetic lost acknowledgement");
            Assert(!session.realtime.AttemptPending && !Get<bool>(session, "attemptRequested"),
                "interrupted attempt callback clears both transport and UI request latches");
            Assert(!Get<bool>(session, "sharedAttemptReady") && session.Phase == "Confirmed",
                "uncertain attempt never becomes a confirmed local attempt");
            Assert(!session.ConfirmAction(), "confirmation cannot begin practice after an uncertain attempt acknowledgement");

            // Simulate subscription recovery, without constructing a socket or sending a reducer.
            SetProperty(session.realtime, "Paired", true);
            SetProperty(session.realtime, "SessionId", "synthetic-session-a");
            SetProperty(session.realtime, "AttemptId", "synthetic-attempt-after-reconnect");
            Call(session, "Update");
            Assert(!session.realtime.AttemptPending && !Get<bool>(session, "attemptRequested"),
                "reconnection alone cannot duplicate the uncertain attempt request");
            Set(session.realtime, "requestedExercise", "sentinel-no-retry");
            Set(session.realtime, "requestedVersion", "sentinel-no-retry");
            Set(session, "busy", false);
            session.Retry();
            Assert(session.Phase == "Selecting" && !Get<bool>(session, "sharedAttemptReady"),
                "explicit retry returns to reviewed selection without inheriting the uncertain attempt");
            Assert(Get<string>(session.realtime, "requestedExercise") == NativeCaseSession.ProcedureId
                && Get<string>(session.realtime, "requestedVersion") == NativeCaseSession.ContentVersion
                && Get<string>(session.realtime, "attemptBeforeStart") == "synthetic-attempt-after-reconnect",
                "explicit retry reaches a fresh appendectomy attempt request using the recovered shared identity");
            // The fixture deliberately has no connection: the send fails immediately rather than
            // pretending a new shared attempt was committed. A real reducer round trip is separate.
            Assert(!session.realtime.AttemptPending && !Get<bool>(session, "sharedAttemptReady"),
                "absent fixture transport cannot falsely commit the requested fresh attempt");
        }

        static void Set(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, PrivateInstance);
            if (info == null) throw new InvalidOperationException("Fixture field not found: " + field);
            info.SetValue(target, value);
        }
        static T Get<T>(object target, string field)
        {
            var info = target.GetType().GetField(field, PrivateInstance);
            if (info == null) throw new InvalidOperationException("Fixture field not found: " + field);
            return (T)info.GetValue(target);
        }
        static void SetProperty(object target, string name, object value)
        {
            var setter = target.GetType().GetProperty(name)?.GetSetMethod(true);
            if (setter == null) throw new InvalidOperationException("Fixture setter not found: " + name);
            setter.Invoke(target, new[] { value });
        }
        static void Call(object target, string name, params object[] arguments)
        {
            var method = target.GetType().GetMethod(name, PrivateInstance);
            if (method == null) throw new InvalidOperationException("Fixture method not found: " + name);
            try { method.Invoke(target, arguments); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("Fixture call failed: " + name, error.InnerException); }
        }
        static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Native session boundary validation failed: " + message);
            checks++;
        }
    }
}
