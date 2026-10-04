#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Scalpal.Handoff;
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
    // Real scenes/coordinators and local HTTP/reducers. Tracked poses and card selections are synthetic; provider voice is unavailable.
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
            public int initialAttemptCount, attemptCount, attemptRows, encounterRows, encounterCreates, coachCreateCount, providerUnavailableCount, providerFetchAttempts;
            public bool coachCarryover, coachContainsWrongProposal, coachContainsPatient;
            public string[] routes;
        }
        [Serializable] sealed class AuthoredChoice { public string key, grade; }
        [Serializable] sealed class AuthoredRound { public string id, stage; public AuthoredChoice[] choices; }
        [Serializable] sealed class AuthoredInterview { public AuthoredRound[] rounds; }
        IXRInputSource previous;
        NativeEncounterSession office;
        NativeCaseSession surgery;
        HandoffFlow flow;
        HandoffCard card;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        T Read<T>(object value, string field) => (T)value.GetType().GetField(field, Private).GetValue(value);
        string FlowPhase => flow ? Read<string>(flow, "phase") : "missing";
        IEnumerator SelectCard(string title, string label, int index = 0)
        {
            yield return Wait(() => card && card.Visible && Read<string>(card, "heading").StartsWith(title, StringComparison.Ordinal)
                && Read<string[]>(card, "actions").Length > index && Read<string[]>(card, "actions")[index] == label
                && (Read<bool[]>(card, "available") == null || Read<bool[]>(card, "available")[index]),
                "actual enabled handoff card: " + title + " / " + label);
            var callback = Read<Action<int>>(card, "selected");
            Check(callback != null, "card has its production selection callback");
            callback(index);
            yield return Frames();
        }
        NativeCaseSession.DevelopmentConfig config;
        FixtureState fixture;
        int checks;
        string runtimeException, encounterId, sharedSessionId, attemptId, procedureId, assessmentJson, scoreJson;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            previous = XRInput.Source; XRInput.Source = new Input();
            Application.logMessageReceived += Log;
            SceneManager.sceneLoaded += DisableFixtureMicrophone;
            config = JsonUtility.FromJson<NativeCaseSession.DevelopmentConfig>(File.ReadAllText(Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG")));
            using (var hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(config.uri + "\n" + config.database))).Replace("-", "");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("SCALPAL_PLAYMODE_CONFIG")), "native-token-path.txt"),
                    Path.Combine(Application.persistentDataPath, "scalpal-session-" + key + ".token"));
            }
        }
        void DisableFixtureMicrophone(Scene scene, LoadSceneMode mode)
        {
            // Connect checks isActiveAndEnabled before requesting macOS mic permission. Only this
            // synthetic fixture disables transport; real coach HTTP and relay remain enabled.
            if (scene.name != EncounterOfficeRoute.SurgeryScene) return;
            var native = UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            if (native && native.voice) native.voice.enabled = false;
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
            Check(!office.voice.Connected, "no patient provider connection starts before a patient is chosen");
            // The office auto-connects the patient voice when an encounter starts. Like the surgery Time-Out, this
            // synthetic fixture disables the transport so that attempt fails before mic permission or provider HTTP.
            office.voice.enabled = false;
            // Headless Unity reports OS focus=false. Supply the same explicit synthetic
            // focus input as the XR source; keep the actual Start/request/voice path.
            office.SendMessage("OnApplicationFocus", true, SendMessageOptions.RequireReceiver);
            Check(Read<bool>(office, "focused"), "synthetic focused office input reaches the production focus callback");
            yield return ReadFixture();
            Check(fixture.attemptCount == fixture.initialAttemptCount && fixture.encounterCreates == 0, "office list does not silently create an attempt or encounter");
            office.StartPatient(jonah.patientId);
            yield return Wait(() => office.State?.phase == "interview" && !office.Busy,
                "real selected-patient path confirms new shared attempt and creates the HTTP interview");
            Check((office.voice.Status == "error" || office.voice.Status == "offline") && office.voice.LastError.Length > 0 && office.patient.Patient && office.patient.Patient.activeSelf,
                "interview start seats the patient and attempts the patient voice automatically (disabled fixture transport fails safely)");
            // Provider failure now falls back to a cached greeting. The disabled voice
            // component cannot drain audio in Update, so explicitly finish only that
            // fixture playback; the production office Update must release the round.
            office.voice.InterruptPlayback();
            yield return Wait(() => office.RoundVisible, "production office Update releases round 1 after fixture greeting playback finishes");
            encounterId = office.State.encounterId; sharedSessionId = office.realtime.SessionId; attemptId = office.realtime.AttemptId;
            procedureId = office.AuthoredProcedureId;
            Check(office.State.patientId == jonah.patientId && encounterId.StartsWith("int-", StringComparison.Ordinal) && !string.IsNullOrEmpty(attemptId), "selected canonical patient and interview identity match");
            yield return Wait(() => office.realtime.TryGetEncounterBinding(encounterId, out var session, out var attempt, out var patient, out var phase)
                && session == sharedSessionId && attempt == attemptId && patient == jonah.patientId && phase == "interview", "actual reducer encounter row binds the exact office interview attempt");
            // Authored answers for this fixture: every round correct except the plan, so the OR must still load the case's surgery.
            var authored = JsonUtility.FromJson<AuthoredInterview>(File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "../../../services/preop/content/patients/" + jonah.patientId + "/interview.json"))));
            string wrongPlan = "";
            while (office.State.phase == "interview")
            {
                var round = office.Round; int number = round.number;
                var spec = authored.rounds.Single(item => item.id == round.roundId);
                string key = spec.stage == "plan" ? spec.choices.First(choice => choice.grade == "wrong").key : spec.choices.Single(choice => choice.grade == "correct").key;
                if (spec.stage == "plan") wrongPlan = round.choices.Single(choice => choice.key == key).text;
                Check(office.voice.MicrophoneMuted && !office.TalkHeld, "the microphone stays muted through the interview");
                office.Choose(key);
                yield return Wait(() => !office.Busy && (office.SurgeryReady || office.RoundVisible && office.Round.number == number + 1), "real answer route advances from round " + number);
            }
            Check(office.SurgeryReady && office.State.phase == "scored" && office.Score.kind == "interview", "real interview scores after the last round");
            Check(!office.Score.procedureChosenCorrectly && office.Score.diagnosisResult == "correct" && office.Score.procedureId == procedureId && office.State.assessment.procedure == wrongPlan,
                "wrong plan pick retains the case's surgery and the exact picked plan");
            Check(office.Score.carryoverItems != null, "interview score carries structured surgery carryover");
            assessmentJson = JsonUtility.ToJson(office.State.assessment); scoreJson = JsonUtility.ToJson(office.Score);
            yield return ReadFixture();
            int officeRouteCount = fixture.routes.Length;
            yield return Wait(() => office.realtime.TryGetEncounterBinding(encounterId, out var session, out var attempt, out var patient, out var phase)
                && session == sharedSessionId && attempt == attemptId && patient == jonah.patientId && phase == "scored", "actual scored reducer row is committed before scene transition");
            office.ContinueToSurgery();
            flow = UnityEngine.Object.FindFirstObjectByType<HandoffFlow>();
            Check(flow && HandoffRun.Current != null && HandoffRun.Current.sourceOffice != null, "ContinueToSurgery opens canonical scored office handoff");
            card = Read<HandoffCard>(flow, "card");
            var ticket = HandoffRun.Current;
            Check(ticket.escalated && HandoffRun.Supported(ticket.procedureId) && ticket.procedureId == procedureId,
                "wrong plan routes to supported authored appendectomy");
            Check(ticket.sourceOffice.sharedSessionId == sharedSessionId && ticket.sourceOffice.attemptId == attemptId,
                "immutable source captures actual office reducer binding");
            yield return SelectCard("Interview score", "To theatre");
            Check(FlowPhase == "challenge", "wrong plan gets one challenge before theatre");
            yield return SelectCard("One challenge", "I would choose " + ticket.procedureTitle);
            Check(ticket.challengeSeen && FlowPhase == "consequence", "challenge acknowledgement reveals consequence");
            yield return SelectCard("Case escalated", "Continue");
            Check(ticket.consequenceSeen && FlowPhase == "theatre", "actual consequence delay completes before theatre");
            yield return Wait(() => card.Visible && Read<string>(card, "heading") == "To theatre", "theatre card renders");
            Check(Read<string[]>(card, "actions")[0] == "Volunteer patient (AR)" && !Read<bool[]>(card, "available")[0]
                && HandoffRun.CanChoose("mixed_reality", new TheatrePreflight { volunteerConsented = true, cameraGranted = true,
                    sceneGranted = true, poseServiceOk = true, coachServiceOk = true }),
                "office offers AR with green preflight and disables it with the current red preflight");
            yield return SelectCard("To theatre", "Virtual OR (VR)", 1);
            yield return Wait(() => SceneManager.GetActiveScene().name == EncounterOfficeRoute.SurgeryScene,
                "actual theatre callback loads OR through canonical transition");
            surgery = UnityEngine.Object.FindFirstObjectByType<NativeCaseSession>();
            Check(surgery && surgery.enabled && surgery.HasHandoff && surgery.OfficeHandoff == null,
                "real OR Awake uses canonical ticket rather than duplicate standalone route");
            Check(ReferenceEquals(ticket, HandoffRun.Current) && surgery.SelectedPatientId == jonah.patientId && surgery.SelectedProcedureId == procedureId
                && ticket.encounterId == encounterId && ticket.sourceOffice.sharedSessionId == sharedSessionId
                && ticket.sourceOffice.attemptId == attemptId, "OR preserves patient, encounter, procedure and exact shared source attempt");
            Check(JsonUtility.ToJson(ticket.sourceOffice.assessment) == assessmentJson && JsonUtility.ToJson(ticket.sourceOffice.scorecard) == scoreJson,
                "actual scene handoff retains the complete committed assessment and score");
            Check(!EncounterOfficeRoute.TakeSurgery(out _), "canonical import consumes the legacy producer snapshot once");
            yield return Wait(() => surgery.HandoffVerified && surgery.workbench.IsReady && surgery.realtime.Paired && surgery.RegistrationReady,
                "real OR Start revalidates office source and loads case with synthetic XR poses");
            Check(surgery.PresentationMode == "virtual" && !surgery.TryChangePresentation(true), "chosen virtual OR core refuses AR while operator preflight is red");
            Check(surgery.realtime.SessionId == sharedSessionId && surgery.realtime.AttemptId == attemptId && !surgery.realtime.AttemptPending,
                "OR adopts the existing attempt without a replacement request");
            // The pre-surgery briefing sits between the fit and the Time-Out; this gate skips it like a learner can.
            yield return Wait(() => FlowPhase == "briefing" || FlowPhase == "timeout", "fit leads to the briefing or Time-Out");
            if (FlowPhase == "briefing")
            {
                yield return Wait(() => UnityEngine.Object.FindFirstObjectByType<Scalpal.Briefing.BriefingDirector>() != null, "briefing spawns in the OR");
                UnityEngine.Object.FindFirstObjectByType<Scalpal.Briefing.BriefingDirector>().Skip();
            }
            yield return Wait(() => surgery.CoachPrepared && !surgery.Busy && FlowPhase == "timeout",
                "actual Time-Out creates coach before practice", 30);
            Check(!surgery.Practicing && !surgery.exercise.CanScore && !ticket.AllConfirmed, "practice remains gated before six Time-Out confirmations");
            foreach (var label in new[] { "Confirm patient", "Confirm procedure", "Confirm site" }) yield return SelectCard("TIME-OUT", label);
            // Each office risk is an individual decision; plan for the first and leave the rest unaddressed so the
            // real preop-check must report them missed (a review that always passes would prove nothing).
            var risks = HandoffFlow.ReviewRisks(ticket.scorecard);
            Check(risks.Length > 0, "the scored patient carries at least one typed office risk into Time-Out");
            string planned = risks.Length > 1 ? risks[0].type : null;
            var skipped = risks.Where(risk => risk.type != planned).Select(risk => risk.type).ToArray();
            for (int i = 0; i < risks.Length; i++)
            {
                if (risks[i].type == planned) yield return SelectCard("TIME-OUT · Risk " + (i + 1) + " of", "Plan for: " + risks[i].label, 0);
                else yield return SelectCard("TIME-OUT · Risk " + (i + 1) + " of", "Not addressed", 1);
            }
            Check(ticket.risksConfirmed && ticket.confirmedRiskTypes.SequenceEqual(planned == null ? new string[0] : new[] { planned }),
                "only the risk the learner planned for is recorded as confirmed");
            foreach (var label in new[] { "Review antibiotic prophylaxis (simulation)", "Review imaging (simulation)" }) yield return SelectCard("TIME-OUT", label);
            Check(ticket.AllConfirmed && !ticket.practiceStarted, "actual Time-Out card callbacks complete review without starting scoring");
            yield return SelectCard("TIME-OUT · Ready", "Begin practice");
            yield return Wait(() => surgery.Practicing && surgery.exercise.CanScore && surgery.exercise.CoachMatches && surgery.coach.Connected,
                "actual ConfirmTimeOut POST and coach relay synchronize practice", 30);
            Check(ticket.timeOutConfirmed && ticket.preopResult != null && ticket.preopResult.patientId == jonah.patientId,
                "actual preop-check response belongs to the reviewed patient");
            Check(!ticket.preopResult.passed && skipped.All(type => ticket.preopResult.missed.Any(item => item.type == type))
                && (planned == null || ticket.preopResult.caught.Any(item => item.type == planned)),
                "actual preop-check scores the skipped office risk as missed and the planned one as caught");
            yield return Wait(() => surgery.voice.Status == "error", "disabled fixture voice transport fails Connect safely before microphone permission");
            Check(!surgery.voice.Connected, "simulated unavailable provider never opens conversation or headset microphone");
            yield return Frames(20); yield return ReadFixture();
            Check(fixture.encounterCreates == 1 && fixture.encounterRows == 1 && fixture.encounterId == encounterId
                && fixture.encounterPatientId == jonah.patientId && fixture.encounterAttemptId == attemptId && fixture.encounterPhase == "scored",
                "one actual interview row remains bound to the same scored patient attempt");
            Check(fixture.currentAttemptId == attemptId && fixture.attemptCount == fixture.initialAttemptCount + 1
                && fixture.attemptRows == fixture.attemptCount, "only office selection increments the actual attempt ordinal");
            Check(fixture.coachCreateCount == 1 && fixture.coachPatientId == jonah.patientId && fixture.coachEncounterId == encounterId
                && fixture.coachMode == "virtual" && fixture.coachProcedureId == procedureId && fixture.coachSessionId == surgery.coach.SessionId,
                "captured production coach POST carries exact patient, encounter and virtual mode once");
            Check(fixture.coachCarryover && fixture.coachContainsWrongProposal && fixture.coachContainsPatient,
                "actual coach prompt contains this patient's interview result and the wrong plan pick");
            var routes = fixture.routes;
            int coachIndex = Array.IndexOf(routes, "POST /coach/sessions");
            var orVerificationRoutes = routes.Skip(officeRouteCount).Take(Math.Max(0, coachIndex - officeRouteCount)).ToArray();
            Check(coachIndex > officeRouteCount && orVerificationRoutes.Contains("GET /interviews/" + encounterId + "/score")
                && orVerificationRoutes.Contains("GET /interviews/" + encounterId)
                && orVerificationRoutes.Contains("GET /patients/" + jonah.patientId + "/case"),
                "actual OR rechecks live case, encounter and score before coach POST");
            Check(routes.Contains("POST /patients/" + jonah.patientId + "/preop-check"), "actual Time-Out submits the structured risk review");
            Check(!surgery.voice.enabled && fixture.providerUnavailableCount == 0 && fixture.providerFetchAttempts == 0
                && !routes.Any(route => route.Contains("/jarvis/connection") || route.Contains("/attending") || route.StartsWith("POST /encounters", StringComparison.Ordinal)),
                "fixture-only disabled transport blocks automatic Time-Out voice before mic permission or provider credentials");
            Debug.Log("SCALPAL_OFFICE_PLAYMODE_OK checks=" + checks + " realOfficeStart=true realORStart=true realSceneTransition=true liveLocalDb=true realInterviewHttp=true realCoachHttp=true sameAttempt=true canonicalHandoff=true wrongPlan=true timeOut=true timeOutRiskReviewCanFail=true syntheticCardSelections=true voiceTransportDisabled=true syntheticXR=true headsetValidated=false providerVoiceValidated=false completeDemoFlow=false");
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
        void OnDestroy() { SceneManager.sceneLoaded -= DisableFixtureMicrophone; Application.logMessageReceived -= Log; XRInput.Source = previous; }
    }
}
#endif
