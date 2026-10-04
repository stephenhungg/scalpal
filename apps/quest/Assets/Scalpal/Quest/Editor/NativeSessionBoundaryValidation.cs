using System;
using System.Reflection;
using System.Linq;
using System.IO;
using SpacetimeDB.Types;
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
                BridgePairingAndDeadline();
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
            // Jarvis must not repeat himself: his own lines (and clips the simulator played in his voice) are remembered,
            // and a reconnect carries them instead of a second greeting.
            session.voice.ForgetSaid();
            session.voice.RememberSaid("Scrubbed in with you."); session.voice.RememberSaid("Scrubbed in with you."); session.voice.RememberSaid("Stop. Bleeding from the appendicular artery.");
            for (int i = 0; i < 12; i++) session.voice.RememberSaid("line " + i);
            Assert(session.voice.RecentLines.Count == 8 && session.voice.RecentLines[7] == "line 11", "Scalpal keeps his most recent lines, without consecutive duplicates");
            string resumed = NativeCaseSession.ReconnectContext("STATE", session.voice.RecentLines);
            Assert(resumed.StartsWith("STATE") && resumed.Contains("line 11") && resumed.Contains("Do not greet again"), "a reconnect tells Scalpal what he already said");
            Assert(NativeCaseSession.ReconnectContext("STATE", new string[0]) == "STATE", "a first connect carries only the state");
            session.voice.ForgetSaid();
            // One voice at a time: while a clip plays the agent's buffered reply is held, then plays in order.
            var buffer = (System.Collections.Generic.Queue<float>)typeof(Scalpal.Voice.QuestJarvisVoice).GetField("outputSamples", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session.voice);
            var read = typeof(Scalpal.Voice.QuestJarvisVoice).GetMethod("ReadAudio", BindingFlags.Instance | BindingFlags.NonPublic);
            buffer.Clear(); for (int i = 0; i < 8; i++) buffer.Enqueue(.5f);
            var frame = new float[4];
            session.voice.HoldOutput = true; read.Invoke(session.voice, new object[] { frame });
            Assert(frame.All(sample => sample == 0) && buffer.Count == 8, "a held agent reply stays silent and keeps its place while a clip plays");
            session.voice.HoldOutput = false; read.Invoke(session.voice, new object[] { frame });
            Assert(frame.All(sample => sample == .5f) && buffer.Count == 4, "the agent reply plays after the clip, in order");
            buffer.Clear();
            // Push to talk: Jarvis must hear silence unless the learner holds Y, so table talk never triggers replies.
            session.PushToTalk(false);
            Assert(session.voice.MicrophoneMuted && Scalpal.Voice.QuestJarvisVoice.EncodeMicrophonePcm(new[] { .5f, -.5f }, 1, session.voice.MicrophoneMuted).All(b => b == 0),
                "Scalpal receives only silence while push-to-talk is released");
            session.PushToTalk(true);
            Assert(!session.voice.MicrophoneMuted, "holding push-to-talk opens the microphone to Scalpal");
            session.PushToTalk(false);
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

        static void BridgePairingAndDeadline()
        {
            // These call the production selection/deadline transitions with real generated
            // row types. They do not construct a socket or alter the persisted demo identity.
            var root = new GameObject("BridgePairingAndDeadlineFixture");
            root.SetActive(false);
            var fixture = root.AddComponent<QuestSessionBridge>();
            fixture.autoConnect = false;
            string directory = Path.Combine(Path.GetTempPath(), "scalpal-pairing-validation-" + Guid.NewGuid().ToString("N"));
            try
            {
                var a = new Session { SessionId = "session-a", Status = "active", CurrentAttemptId = "attempt-a" };
                var b = new Session { SessionId = "session-b", Status = "active", CurrentAttemptId = "attempt-b" };
                var members = new[] { new Membership { SessionId = a.SessionId, Role = "headset" },
                    new Membership { SessionId = b.SessionId, Role = "headset" } };
                var sessions = new[] { a, b };
                Assert(SelectPairing(new[] { members[0] }, sessions, "", "", false, false, out _) == null,
                    "cached old membership cannot pair before configured invite resolves");
                Assert(SelectPairing(new[] { members[0] }, sessions, "", "", true, true, out var rejected) == null
                    && rejected.StartsWith("Pairing rejected:", StringComparison.Ordinal),
                    "rejected new invite cannot silently adopt the only older headset membership");
                Assert(SelectPairing(new[] { members[0] }, sessions, b.SessionId, a.SessionId, true, true, out rejected) == null
                    && rejected.StartsWith("Pairing rejected:", StringComparison.Ordinal),
                    "explicit configured session rejection cannot fall back to another persisted membership");
                Assert(SelectPairing(new[] { members[0] }, sessions, "", b.SessionId, true, true, out _) == null,
                    "same-invite persisted session must actually have an active headset membership");
                Assert(SelectPairing(members, sessions, "", a.SessionId, true, true, out _) == a,
                    "rotated invite may reconnect exactly its known persisted session among several memberships");
                Assert(SelectPairing(members, sessions, b.SessionId, a.SessionId, true, true, out _) == b,
                    "explicit configured session takes priority over invite history");
                Assert(SelectPairing(members, sessions, "", "", true, false, out var multiple) == null
                    && multiple.StartsWith("Choose preferredSessionId:", StringComparison.Ordinal),
                    "successful join still refuses ambiguous headset memberships");
                Assert(SelectPairing(members, sessions, b.SessionId, "", true, false, out _) == b,
                    "positive control: a successful join pairs the configured active headset session");
                a.Status = "ended";
                Assert(SelectPairing(members, sessions, "", a.SessionId, true, true, out _) == null,
                    "persisted membership cannot resurrect an ended session");
                a.Status = "active";
                members[0].Role = "viewer";
                Assert(SelectPairing(members, sessions, "", a.SessionId, true, true, out _) == null,
                    "viewer membership cannot satisfy headset reconnect");

                string path = Path.Combine(directory, "identity.pairing");
                string hashA = (string)CallBridgeStatic("InviteHash", new object[] { "A-CODE" });
                string hashB = (string)CallBridgeStatic("InviteHash", new object[] { "B-CODE" });
                Assert(hashA != hashB && hashA == (string)CallBridgeStatic("InviteHash", new object[] { " a-code " }),
                    "invite persistence normalizes case and whitespace but distinguishes newly configured invites");
                CallBridgeStatic("SavePersistedSession", new object[] { path, hashA, a.SessionId });
                Assert((string)CallBridgeStatic("ReadPersistedSession", new object[] { path, hashA }) == a.SessionId,
                    "real temporary pairing file round-trips the same invite's session");
                Assert((string)CallBridgeStatic("ReadPersistedSession", new object[] { path, hashB }) == "",
                    "changing the configured invite prevents reuse of the older session file");
                Assert(!File.ReadAllText(path).Contains("A-CODE"), "pairing file contains no plaintext invite");
                File.WriteAllText(path, "malformed-pairing");
                Assert((string)CallBridgeStatic("ReadPersistedSession", new object[] { path, hashA }) == "",
                    "invalid persistence fails closed instead of adopting an arbitrary membership");
                File.Delete(path);
                Assert((string)CallBridgeStatic("ReadPersistedSession", new object[] { path, hashA }) == "",
                    "legacy token without a pairing file requires an explicit configured session on rejection");
                fixture.uri = "ws://fixture-a"; fixture.database = "fixture-db-a";
                string endpointA = (string)CallResult(fixture, "TokenPath");
                fixture.uri = "ws://fixture-b";
                Assert(endpointA != (string)CallResult(fixture, "TokenPath"), "pairing file is scoped to its configured URI");
                fixture.uri = "ws://fixture-a"; fixture.database = "fixture-db-b";
                Assert(endpointA != (string)CallResult(fixture, "TokenPath"), "pairing file is scoped to its configured database");

                int started = 0, failed = 0;
                string deliveredAttempt = "", failure = "";
                fixture.AttemptStarted += id => { started++; deliveredAttempt = id; };
                fixture.AttemptFailed += reason => { failed++; failure = reason; };
                b.ExerciseId = "open_appendectomy"; b.ExerciseVersion = "fixture-version";
                SetProperty(fixture, "Paired", true);
                SetProperty(fixture, "AttemptId", b.CurrentAttemptId);
                Set(fixture, "requestedExercise", b.ExerciseId);
                Set(fixture, "requestedVersion", b.ExerciseVersion);
                Set(fixture, "committedAttemptId", b.CurrentAttemptId);
                Set(fixture, "attemptStartedAt", 100f);
                Set(fixture, "attemptInFlight", true);
                Set(fixture, "attemptCommitted", true);
                Call(fixture, "EvaluateAttempt", b, 110.5f);
                Assert(!fixture.AttemptPending && started == 1 && failed == 0 && deliveredAttempt == b.CurrentAttemptId,
                    "a correlated confirmation wins over an expired deadline in the same state transition");
                Call(fixture, "EvaluateAttempt", null, 111f);
                Assert(started == 1 && failed == 0, "later deadline checks cannot fail or redeliver an already confirmed attempt");

                Set(fixture, "attemptInFlight", true); Set(fixture, "attemptCommitted", false);
                Call(fixture, "EvaluateAttempt", b, 109.5f);
                Assert(fixture.AttemptPending && failed == 0, "unconfirmed request remains pending before its deadline");
                Call(fixture, "EvaluateAttempt", b, 110f);
                Assert(!fixture.AttemptPending && failed == 1 && started == 1,
                    "negative control: matching cached state without a reducer commit must time out");
                Set(fixture, "attemptInFlight", true); Set(fixture, "attemptCommitted", true);
                Set(fixture, "committedAttemptId", "different-attempt");
                Call(fixture, "EvaluateAttempt", b, 110f);
                Assert(!fixture.AttemptPending && failed == 2 && started == 1,
                    "negative control: another attempt's commit cannot satisfy this request at the deadline");
                Set(fixture, "attemptInFlight", true);
                Call(fixture, "Disconnected", "Synthetic acknowledgement loss");
                Assert(failed == 3 && failure.Contains("Left menu: retry") && !failure.Contains("A: retry"),
                    "lost acknowledgement points to the actual retry control rather than tool reset");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static Session SelectPairing(Membership[] memberships, Session[] sessions, string preferred, string persisted,
            bool resolved, bool rejected, out string status)
        {
            object[] arguments = { memberships, sessions, preferred, persisted, resolved, rejected, null };
            var result = (Session)CallBridgeStatic("SelectSession", arguments);
            status = (string)arguments[6];
            return result;
        }
        static object CallBridgeStatic(string name, object[] arguments)
        {
            var method = typeof(QuestSessionBridge).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("Bridge fixture static method not found: " + name);
            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException error) { throw new InvalidOperationException("Bridge fixture call failed: " + name, error.InnerException); }
        }
        static object CallResult(object target, string name)
        {
            var method = target.GetType().GetMethod(name, PrivateInstance);
            if (method == null) throw new InvalidOperationException("Fixture method not found: " + name);
            return method.Invoke(target, null);
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
