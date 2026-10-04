#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Scalpal.Instruments;
using Scalpal.Quest;
using Scalpal.Realtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Scalpal.EncounterOffice.Editor
{
    // Real scenes/coordinators and local HTTP/reducers. Only tracked poses are synthetic.
    public sealed class EncounterOfficePlayModeDriver : MonoBehaviour
    {
        sealed class Input : IXRInputSource
        {
            public bool DisplayRunning => true;
            public bool FloorTracking => true;
            public bool HasFocus => true;
            public bool TryPose(XRNode node, out Pose pose)
            { pose = new Pose(new Vector3(node == XRNode.Head ? 0 : node == XRNode.LeftHand ? -.25f : .25f, node == XRNode.Head ? 1.6f : 1, node == XRNode.Head ? 0 : -.3f), Quaternion.identity); return true; }
            public float Grip(XRNode node) => 0;
            public float Trigger(XRNode node) => 0;
            public bool Button(XRNode node, XRInputButton button) => false;
        }
        [Serializable] sealed class FixtureState
        {
            public string sessionId, initialAttemptId, currentAttemptId, encounterId, encounterAttemptId, encounterPatientId, encounterPhase;
            public string coachPatientId, coachEncounterId, coachMode, coachSessionId, coachProcedureId;
            public int initialAttemptCount, attemptCount, attemptRows, encounterRows, encounterCreates, coachCreateCount;
            public bool coachCarryover, coachContainsWrongProposal, coachContainsPatient;
            public string[] routes;
        }
        IXRInputSource previous;
        NativeEncounterSession office;
        NativeCaseSession surgery;
        NativeCaseSession.DevelopmentConfig config;
        FixtureState fixture;
        int checks;
        string runtimeException, encounterId, sharedSessionId, attemptId, procedureId, assessmentJson, scoreJson;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            previous = XRInput.Source; XRInput.Source = new Input();
            Application.logMessageReceived += Log;
            config = JsonUtility.FromJson<NativeCaseSession.DevelopmentConfig>(File.ReadAllText(Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG")));
            using (var hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(config.uri + "\n" + config.database))).Replace("-", "");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG")), "native-token-path.txt"),
                    Path.Combine(Application.persistentDataPath, "scalpal-session-" + key + ".token"));
            }
        }
        void Start() => StartCoroutine(Guard(Exercise()));
        void Log(string message, string stack, LogType type)
        { if (type == LogType.Exception && stack.Contains("Scalpal")) runtimeException = message; }
        void Check(bool condition, string detail)
        { checks++; if (!condition) throw new InvalidOperationException("Office Play Mode failed: " + detail); }
        IEnumerator Wait(Func<bool> condition, string detail, float seconds = 20)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (runtimeException != null) throw new InvalidOperationException(runtimeException);
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("Office Play Mode timeout: " + detail + " office=" + (office ? office.Status : "unloaded")
                        + " OR=" + (surgery ? surgery.Phase + " / " + surgery.Message : "unloaded"));
                yield return null;
            }
            Check(true, detail);
        }
        IEnumerator Frames(int count = 4) { for (int i = 0; i < count; i++) yield return null; }
        IEnumerator ReadFixture()
        {
            using (var request = UnityWebRequest.Get(config.coachBaseUrl + "/fixture/state"))
            {
                request.timeout = 8; yield return request.SendWebRequest();
                Check(request.result == UnityWebRequest.Result.Success, "read actual fixture reducer/captured HTTP state");
                fixture = JsonUtility.FromJson<FixtureState>(request.downloadHandler.text);
                Check(fixture != null, "fixture state parses");
            }
        }
        IEnumerator Exercise()
        {
            office = UnityEngine.Object.FindFirstObjectByType<NativeEncounterSession>();
            Check(office && office.enabled && office.realtime && office.voice, "real office scene has its enabled session, shared bridge and voice bindings");
            yield return Wait(() => office.Patients.Length > 0 && !office.Busy && office.realtime.Paired,
                "real office Start loads explore patients and pairs the isolated headset identity");
            var jonah = office.Patients.Single(item => item.patientId == "patient-demo-sparse");
            Check(EncounterOfficeRoute.CanEnter(jonah), "explore producer supplies selectable canonical Jonah subject");
            Check(!office.voice.Connected, "no patient provider connection starts automatically");
            yield return ReadFixture();
            Check(fixture.attemptCount == fixture.initialAttemptCount && fixture.encounterCreates == 0, "office list does not silently create an attempt or encounter");
            office.StartPatient(jonah.patientId);
            yield return Wait(() => office.State?.phase == "interview" && !office.Busy,
                "real selected-patient path confirms new shared attempt then creates authoritative HTTP encounter");
            encounterId = office.State.encounterId; sharedSessionId = office.realtime.SessionId; attemptId = office.realtime.AttemptId;
            procedureId = office.AuthoredProcedureId;
            Check(office.State.patientId == jonah.patientId && !string.IsNullOrEmpty(encounterId) && !string.IsNullOrEmpty(attemptId), "selected canonical patient and encounter identity match");
            yield return Wait(() => office.realtime.TryGetEncounterBinding(encounterId, out var session, out var attempt, out var patient, out var phase)
                && session == sharedSessionId && attempt == attemptId && patient == jonah.patientId && phase == "interview", "actual reducer encounter row binds the exact office attempt");
            office.Ask("allergies");
            yield return Wait(() => !office.Busy && office.State.historyAsked.Any(item => item.id == "allergies"), "real interview request elicits an authored fact");
            office.SeeAttending();
            yield return Wait(() => !office.Busy && office.State.phase == "attending" && office.Role == "attending", "real attending endpoint switches the office role");
            office.Draft = new EncounterAssessment { diagnosis = "kidney stone", differential = new[] { "appendicitis" }, procedure = "ureteroscopy", urgency = "elective" };
            office.SubmitAssessment();
            yield return Wait(() => !office.Busy && office.SurgeryReady && office.State.phase == "scored", "real assessment and score GET complete before handoff");
            Check(!office.Score.procedureChosenCorrectly && office.Score.procedureId == procedureId && office.State.assessment.procedure == "ureteroscopy",
                "wrong learner plan retains authored surgery and exact committed proposal");
            Check(office.Score.carryoverItems.Length > 0, "authoritative score contains structured surgery carryover");
            assessmentJson = JsonUtility.ToJson(office.State.assessment); scoreJson = JsonUtility.ToJson(office.Score);
            yield return Wait(() => office.realtime.TryGetEncounterBinding(encounterId, out var session, out var attempt, out var patient, out var phase)
                && session == sharedSessionId && attempt == attemptId && patient == jonah.patientId && phase == "scored", "actual scored reducer row is committed before scene transition");
            office.ContinueToSurgery();
            yield return Wait(() => SceneManager.GetActiveScene().name == EncounterOfficeRoute.SurgeryScene,
                "production ContinueToSurgery loads actual OR scene");
            surgery = UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Check(surgery && surgery.enabled && surgery.OfficeHandoff != null, "real OR Awake consumes the office ticket");
            Check(surgery.SelectedPatientId == jonah.patientId && surgery.SelectedProcedureId == procedureId
                && surgery.OfficeHandoff.encounterId == encounterId && surgery.OfficeHandoff.sharedSessionId == sharedSessionId
                && surgery.OfficeHandoff.attemptId == attemptId, "OR preserves patient, encounter, procedure and exact shared attempt");
            Check(JsonUtility.ToJson(surgery.OfficeHandoff.assessment) == assessmentJson && JsonUtility.ToJson(surgery.OfficeHandoff.scorecard) == scoreJson,
                "actual scene handoff retains the complete committed assessment and score");
            Check(!EncounterOfficeRoute.TakeSurgery(out _), "OR consumes the handoff only once");
            yield return Wait(() => surgery.Phase == "Selecting" && surgery.workbench.IsReady && surgery.realtime.Paired && surgery.RegistrationReady,
                "real OR Start loads selected case and actual Update restores shared pairing with synthetic XR poses");
            Check(surgery.PresentationMode == "virtual" && !surgery.presentation.passthrough && !surgery.TryChangePresentation(true), "office OR remains full VR");
            Check(surgery.realtime.SessionId == sharedSessionId && surgery.realtime.AttemptId == attemptId && !surgery.realtime.AttemptPending,
                "OR adopts the existing attempt without a replacement request");
            yield return Frames(8);
            Check(surgery.ConfirmAction() && surgery.Phase == "Confirmed", "actual OR coordinator accepts first case review");
            Check(surgery.ConfirmAction(), "actual OR coordinator accepts second practice confirmation");
            yield return Wait(() => surgery.Practicing && surgery.exercise.CanScore && surgery.exercise.CoachMatches && surgery.coach.Connected,
                "actual coach creation and relay synchronize the selected case for practice", 30);
            Check(!surgery.voice.Connected, "no OR provider conversation or headset microphone starts");
            yield return Frames(20); yield return ReadFixture();
            Check(fixture.encounterCreates == 1 && fixture.encounterRows == 1 && fixture.encounterId == encounterId
                && fixture.encounterPatientId == jonah.patientId && fixture.encounterAttemptId == attemptId && fixture.encounterPhase == "scored",
                "one actual encounter row remains bound to the same scored patient attempt");
            Check(fixture.currentAttemptId == attemptId && fixture.attemptCount == fixture.initialAttemptCount + 1
                && fixture.attemptRows == fixture.attemptCount, "only office selection increments the actual attempt ordinal");
            Check(fixture.coachCreateCount == 1 && fixture.coachPatientId == jonah.patientId && fixture.coachEncounterId == encounterId
                && fixture.coachMode == "virtual" && fixture.coachProcedureId == procedureId && fixture.coachSessionId == surgery.coach.SessionId,
                "captured production coach POST carries exact patient, encounter and virtual mode once");
            Check(fixture.coachCarryover && fixture.coachContainsWrongProposal && fixture.coachContainsPatient,
                "actual coach prompt contains this patient's office result and wrong learner proposal");
            var routes = fixture.routes;
            int coachIndex = Array.IndexOf(routes, "POST /coach/sessions");
            Check(coachIndex > 0 && routes.Take(coachIndex).Count(route => route == "GET /encounters/" + encounterId + "/score") >= 2
                && routes.Take(coachIndex).Contains("GET /encounters/" + encounterId)
                && routes.Take(coachIndex).Count(route => route == "GET /patients/" + jonah.patientId + "/case") >= 3,
                "actual OR rechecks live case, encounter and score before coach POST");
            Check(!routes.Any(route => route.Contains("/jarvis/connection")), "fixture never requests provider voice credentials");
            Debug.Log("SCALPAL_OFFICE_PLAYMODE_OK checks=" + checks + " realOfficeStart=true realORStart=true realSceneTransition=true liveLocalDb=true realEncounterHttp=true realCoachHttp=true sameAttempt=true syntheticXR=true headsetValidated=false providerVoiceValidated=false completeDemoFlow=false");
        }
        IEnumerator Guard(IEnumerator work)
        {
            var stack = new Stack<IEnumerator>(); stack.Push(work); bool failed = false;
            while (stack.Count > 0)
            {
                bool moved = false; object current = null;
                try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
                catch (Exception error) { Debug.LogError("SCALPAL_OFFICE_PLAYMODE_FAILED " + error.Message); failed = true; break; }
                if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current;
            }
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Cleanup(); SessionState.SetBool("Scalpal.OfficePlayMode.Passed", !failed); EditorApplication.ExitPlaymode();
        }
        void Cleanup()
        {
            foreach (var bridge in UnityEngine.Object.FindObjectsByType<QuestSessionBridge>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            { bridge.autoConnect = false; bridge.enabled = false; }
            if (office && office.voice) office.voice.Disconnect();
            if (surgery && surgery.voice) surgery.voice.Disconnect();
            if (config != null && config.database.StartsWith("scalpal-test-office-", StringComparison.Ordinal))
            {
                using (var hash = SHA256.Create())
                {
                    string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(config.uri + "\n" + config.database))).Replace("-", "");
                    string path = Path.Combine(Application.persistentDataPath, "scalpal-session-" + key + ".token");
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }
        void OnDestroy() { Application.logMessageReceived -= Log; XRInput.Source = previous; }
    }
}
#endif
