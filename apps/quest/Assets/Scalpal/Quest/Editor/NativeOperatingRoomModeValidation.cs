using System;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.EncounterOffice;
using Scalpal.Handoff;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Committed scene and real mode/registration APIs, with synthetic acquisition
    // geometry and reflected setup state. This is not a Play Mode or headset test.
    public static class NativeOperatingRoomModeValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;

        [MenuItem("Scalpal/Quest/Validate Operating Room Presentation Modes")]
        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var priorTicket = HandoffRun.Current;
            var priorPreflight = HandoffRun.Preflight;
            GameObject fixture = null;
            try
            {
                SetStaticProperty(typeof(HandoffRun), "Current", null);
                SetStaticProperty(typeof(HandoffRun), "Preflight", new TheatrePreflight());
                var scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Single);
                NativeCaseSession session = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = root.GetComponentInChildren<NativeCaseSession>(true);
                    if (found) session = found;
                    root.SetActive(false);
                }
                Assert(session && session.presentation && session.bodyRegistration && session.workbench
                    && session.exercise && session.anatomy && session.coach && session.voice && session.realtime,
                    "committed scene contains the shared core and both presentation paths");
                var view = session.presentation;
                var body = session.bodyRegistration;
                Assert(view.anatomyFit == body.anatomyFit && view.patientFrame == body.patientFrame
                    && body.patientFrame == session.patientFrame, "body fit and presentation address the same anatomy and port roots");
                Assert(view.virtualRoom && view.virtualMannequin && view.headCamera && view.cameraManager
                    && body.cameraAccess && body.surfaceAccess, "real scene binds room, mannequin, compositor, camera and depth sources");
                Call(view, "Awake");
                Call(session.workbench, "Awake"); // Cache authored equipment reset poses without XR Update.
                var authoredFit = new TransformState(view.anatomyFit);
                var authoredPorts = new TransformState(view.patientFrame);
                session.realtime.autoConnect = false;
                SetProperty(session.workbench, "IsReady", true);
                SeedSharedIdentity(session);
                HandoffStartupMode(session);

                // Only a clone of the real practice geometry is active. The native rig,
                // sensors, voice and transport remain inactive throughout this fixture.
                fixture = new GameObject("SyntheticOperatingRoomModeGeometry");
                var clone = UnityEngine.Object.Instantiate(session.anatomy.gameObject, fixture.transform);
                clone.SetActive(true);
                session.anatomy = clone.GetComponent<AnatomyController>();
                session.anatomy.SetPreviewMode(false);
                session.anatomy.SetPreviewRotation(false);
                session.anatomy.RebuildIndex();
                session.exercise.anatomy = session.anatomy;

                NegativeControls(session);
                foreach (var phase in new[] { "Selecting", "Confirmed", "Recap" })
                {
                    SetProperty(session, "Phase", phase);
                    view.passthrough = true; view.Apply(); session.exercise.presentationMode = "mixed_reality";
                    Acquire(body);
                    Assert(session.RegistrationReady, "AR positive control has a fresh accepted measured fit in " + phase);
                    SeedPrepracticeState(session);
                    Assert(session.anatomy.RegistrationValid && session.anatomy.CanDisplay
                        && session.anatomy.GetComponentsInChildren<Renderer>(true).Any(item => item.enabled),
                        "positive control exposes actual imported practice renderers before the transition");
                    var shared = new IdentityState(session);
                    Assert(session.TrySelectOperatingRoomMode("virtual", out var reason), "AR to VR is supported in " + phase + ": " + reason);
                    Assert(view.CoachMode == "virtual" && session.exercise.presentationMode == "virtual", "shared exercise adopts exact VR mode");
                    Assert(view.virtualRoom.activeSelf && view.virtualMannequin.enabled && !view.cameraManager.enabled
                        && view.headCamera.backgroundColor.a == 1f, "VR turns on the real room/mannequin and opaque background without AR camera permission");
                    Assert(authoredFit.Matches(view.anatomyFit) && authoredPorts.Matches(view.patientFrame),
                        "VR restores authored world poses and local scales after measured AR fitting");
                    Assert(session.RegistrationReady && !body.Accepted && !body.CandidateValid
                        && !body.EnabledByOperator && !body.cameraAccess.enabled && !body.surfaceAccess.enabled,
                        "VR remains ready with no AR sources or accepted participant fit");
                    AssertReset(session);
                    shared.AssertPreserved(session, phase);

                    SeedPrepracticeState(session);
                    Assert(session.TrySelectOperatingRoomMode("mixed_reality", out reason), "VR to AR is supported in " + phase + ": " + reason);
                    Assert(view.CoachMode == "mixed_reality" && session.exercise.presentationMode == "mixed_reality", "shared exercise adopts exact AR mode");
                    Assert(!view.virtualRoom.activeSelf && !view.virtualMannequin.enabled && view.cameraManager.enabled
                        && view.headCamera.backgroundColor.a == 0f, "AR turns off the virtual patient/room and enables passthrough background");
                    AssertReset(session);
                    shared.AssertPreserved(session, phase);
                    Assert(!session.RegistrationReady && !body.Accepted && !body.CandidateValid,
                        "switching back to AR cannot reuse a previous accepted participant fit");
                    Observe(body); Assert(!session.RegistrationReady && !body.Accepted, "one fresh AR observation cannot reopen practice");
                    Observe(body); Assert(!session.RegistrationReady && !body.Accepted, "two fresh AR observations cannot reopen practice");
                    Observe(body); Assert(session.RegistrationReady && body.Accepted && body.CandidateValid,
                        "three fresh public calibrated-depth observations reopen AR registration");
                }
                NoOpPreservesState(session);
                HandoffPreflight(session);
                Debug.Log("SCALPAL_NATIVE_OR_MODE_VALIDATION_OK checks=" + checks
                    + " committed scene/shared-core mode APIs with synthetic editor lifecycle, preflight and acquisition-time depth; no Play Mode, hardware, participant or provider evidence");
            }
            finally
            {
                SetStaticProperty(typeof(HandoffRun), "Current", priorTicket);
                SetStaticProperty(typeof(HandoffRun), "Preflight", priorPreflight);
                if (fixture) UnityEngine.Object.DestroyImmediate(fixture);
                if (previous.Any(item => item.isLoaded && item.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void NegativeControls(NativeCaseSession session)
        {
            SetProperty(session, "Phase", "Selecting");
            foreach (var mode in new[] { null, "", "AR", "VR", "Mixed_Reality", " virtual", "virtual ", "mixed_reality_extra" })
                RefuseUnchanged(session, mode, "invalid presentation string");
            Set(session, "busy", true);
            RefuseUnchanged(session, "virtual", "busy session cannot change mode");
            Set(session, "busy", false);
            foreach (var phase in new[] { "Startup", "Practicing", "Replay" })
            {
                SetProperty(session, "Phase", phase);
                Set(session, "practicePaused", false);
                RefuseUnchanged(session, "virtual", phase + " cannot change mode");
                Set(session, "practicePaused", true);
                RefuseUnchanged(session, "virtual", phase + " cannot bypass lifecycle with a pause");
            }
            SetProperty(session, "Phase", "Confirmed");
            Set(session, "practicePaused", false);
        }

        static void RefuseUnchanged(NativeCaseSession session, string mode, string label)
        {
            var view = session.presentation;
            bool passthrough = view.passthrough;
            int generation = Get<int>(session, "generation");
            var fit = new TransformState(view.anatomyFit);
            var ports = new TransformState(view.patientFrame);
            var shared = new IdentityState(session);
            string phase = session.Phase;
            Assert(!session.TrySelectOperatingRoomMode(mode, out var reason) && !string.IsNullOrWhiteSpace(reason), label + " gives a concrete refusal");
            Assert(view.passthrough == passthrough && Get<int>(session, "generation") == generation
                && fit.Matches(view.anatomyFit) && ports.Matches(view.patientFrame), label + " leaves mode, generation and geometry unchanged");
            shared.AssertPreserved(session, phase);
        }

        static void NoOpPreservesState(NativeCaseSession session)
        {
            SetProperty(session, "Phase", "Confirmed");
            var ticket = Ticket();
            SetStaticProperty(typeof(HandoffRun), "Current", ticket);
            SetStaticProperty(typeof(HandoffRun), "Preflight", AllAvailable());
            ticket.presentationMode = "mixed_reality";
            session.presentation.passthrough = true; session.presentation.Apply();
            Acquire(session.bodyRegistration);
            ConfirmAll(ticket);
            SeedPrepracticeState(session);
            var fit = new TransformState(session.bodyRegistration.anatomyFit);
            var ports = new TransformState(session.bodyRegistration.patientFrame);
            float observation = Get<float>(session.bodyRegistration, "observationTime");
            int generation = Get<int>(session, "generation");
            var identity = new IdentityState(session);
            Assert(session.TrySelectOperatingRoomMode("mixed_reality", out var reason), "same-mode AR request is a successful no-op: " + reason);
            Assert(session.bodyRegistration.Accepted && session.bodyRegistration.CandidateValid
                && observation == Get<float>(session.bodyRegistration, "observationTime")
                && fit.Matches(session.bodyRegistration.anatomyFit) && ports.Matches(session.bodyRegistration.patientFrame),
                "same-mode request preserves measured fit, original acquisition time and registered poses");
            Assert(ticket.timeOutConfirmed && ticket.AllConfirmed && Get<int>(session, "generation") == generation,
                "same-mode request preserves Time-Out and callback generation");
            Assert(session.CoachPrepared && Get<bool>(session, "recoveringLocalCoach") && Get<bool>(session, "localCoachReady")
                && session.anatomy.RegistrationValid && session.patientFrame.gameObject.activeSelf,
                "same-mode request does not reset coach recovery or hide existing geometry");
            identity.AssertPreserved(session, "Confirmed");
            Set(session.bodyRegistration, "observationTime", Time.realtimeSinceStartup - NativeBodyRegistration.MaximumFitAge - .01f);
            Assert(!session.RegistrationReady, "a held AR fit still expires at the acquisition-time boundary");
            SetStaticProperty(typeof(HandoffRun), "Current", null);
        }

        static void HandoffStartupMode(NativeCaseSession session)
        {
            // Exercise the actual committed presentation Awake against a serialized
            // scored-office ticket. Opposite inspector defaults must not win.
            var view = session.presentation;
            bool initialMode = view.passthrough;
            foreach (var mode in new[] { "mixed_reality", "virtual" })
            {
                var ticket = Ticket(); ticket.presentationMode = mode;
                var source = ticket.sourceOffice;
                HandoffRun.BindOfficeSource(ticket, source);
                Assert(ticket.presentationMode == mode && ticket.attemptId == source.attemptId,
                    "binding the scored office source preserves learner mode and attempt: " + mode);
                ticket = JsonUtility.FromJson<HandoffTicket>(JsonUtility.ToJson(ticket));
                Assert(ticket.presentationMode == mode && ticket.schema == "scalpal.handoff.v1",
                    "shared run context round-trips the agreed mode field: " + mode);
                SetStaticProperty(typeof(HandoffRun), "Current", ticket);
                view.passthrough = mode == "virtual";
                Call(view, "Awake");
                bool ar = mode == "mixed_reality";
                Assert(view.passthrough == ar && view.CoachMode == ticket.presentationMode,
                    "real presentation startup takes ticket mode over the opposite scene default: " + mode);
                Assert(view.cameraManager.enabled == ar && view.virtualRoom.activeSelf == !ar && view.virtualMannequin.enabled == !ar
                    && view.headCamera.backgroundColor.a == (ar ? 0f : 1f),
                    "ticket startup configures actual passthrough/virtual scene bindings: " + mode);
                Assert(ticket.attemptId == source.attemptId && ticket.sharedSessionId == source.sharedSessionId && !ticket.practiceStarted,
                    "presentation startup does not create a second scored attempt or enter practice: " + mode);
            }
            SetStaticProperty(typeof(HandoffRun), "Current", null);
            view.passthrough = initialMode; view.Apply();
        }

        static void HandoffPreflight(NativeCaseSession session)
        {
            var ticket = Ticket();
            SetStaticProperty(typeof(HandoffRun), "Current", ticket);
            SetStaticProperty(typeof(HandoffRun), "Preflight", AllAvailable());
            SetProperty(session, "Phase", "Confirmed");
            session.presentation.passthrough = false; session.presentation.Apply();
            session.exercise.presentationMode = "virtual";
            ConfirmAll(ticket); SeedPrepracticeState(session);
            var identity = new IdentityState(session);
            Assert(ticket.sourceOffice != null && HandoffRun.Preflight.ArAvailable,
                "positive control is a scored-office source with synthetic all-green AR preflight");
            Assert(session.TrySelectOperatingRoomMode("mixed_reality", out var reason),
                "office-source ticket may select AR through the OR entry point: " + reason);
            Assert(ticket.presentationMode == "mixed_reality" && !ticket.timeOutConfirmed && !ticket.AllConfirmed,
                "real mode transition updates the handoff mode and requires a new Time-Out");
            AssertReset(session); identity.AssertPreserved(session, "Confirmed");
            Assert(!session.RegistrationReady, "preflight availability cannot substitute for a measured accepted body fit");

            foreach (var unavailable in new[] { "volunteerConsented", "cameraGranted", "sceneGranted", "poseServiceOk", "coachServiceOk" })
            {
                var preflight = AllAvailable();
                typeof(TheatrePreflight).GetField(unavailable).SetValue(preflight, false);
                SetStaticProperty(typeof(HandoffRun), "Preflight", preflight);
                session.presentation.passthrough = false; session.presentation.Apply();
                session.exercise.presentationMode = "virtual"; ticket.presentationMode = "virtual";
                RefuseUnchanged(session, "mixed_reality", "unavailable AR prerequisite " + unavailable);
            }
            SetStaticProperty(typeof(HandoffRun), "Preflight", new TheatrePreflight());
            session.presentation.passthrough = true; session.presentation.Apply(); ticket.presentationMode = "mixed_reality";
            ConfirmAll(ticket); SeedPrepracticeState(session);
            Assert(session.TrySelectOperatingRoomMode("virtual", out reason), "VR is available when every AR prerequisite is absent: " + reason);
            Assert(session.RegistrationReady && !session.presentation.cameraManager.enabled && ticket.presentationMode == "virtual",
                "permissionless VR uses the same ready shared core");
            AssertReset(session); identity.AssertPreserved(session, "Confirmed");
            ticket.practiceStarted = true;
            SetProperty(session, "Phase", "Practicing");
            RefuseUnchanged(session, "mixed_reality", "a started handoff attempt cannot switch presentation while awaiting replay");
            SetStaticProperty(typeof(HandoffRun), "Current", null);
        }

        static HandoffTicket Ticket() => new HandoffTicket
        {
            patientId = NativeCaseSession.PatientId, procedureId = NativeCaseSession.ProcedureId,
            encounterId = "synthetic-mode-encounter", sharedSessionId = "synthetic-mode-session", attemptId = "synthetic-mode-attempt",
            sourceOffice = new EncounterSurgeryHandoff
            {
                patientId = NativeCaseSession.PatientId, procedureId = NativeCaseSession.ProcedureId,
                encounterId = "synthetic-mode-encounter", sharedSessionId = "synthetic-mode-session", attemptId = "synthetic-mode-attempt"
            }
        };
        static TheatrePreflight AllAvailable() => new TheatrePreflight
        { volunteerConsented = true, cameraGranted = true, sceneGranted = true, poseServiceOk = true, coachServiceOk = true };
        static void ConfirmAll(HandoffTicket ticket)
        {
            ticket.timeOutConfirmed = ticket.patientConfirmed = ticket.procedureConfirmed = ticket.siteConfirmed
                = ticket.risksConfirmed = ticket.antibioticsReviewed = ticket.imagingReviewed = true;
        }
        static void SeedSharedIdentity(NativeCaseSession session)
        {
            Set(session, "sharedAttemptReady", true); Set(session, "attemptRequested", false); Set(session, "attemptFailed", false);
            Set(session, "boundSharedSession", "synthetic-mode-session"); Set(session, "boundSharedAttempt", "synthetic-mode-attempt");
            SetProperty(session, "SelectedPatientId", NativeCaseSession.PatientId); SetProperty(session, "SelectedProcedureId", NativeCaseSession.ProcedureId);
            SetProperty(session.realtime, "Paired", true); SetProperty(session.realtime, "SessionId", "synthetic-mode-session");
            SetProperty(session.realtime, "AttemptId", "synthetic-mode-attempt");
        }
        static void SeedPrepracticeState(NativeCaseSession session)
        {
            Set(session, "coachSessionId", "synthetic-mode-coach");
            Set(session, "voiceRequested", true); Set(session, "handoffVoiceAllowed", true);
            Set(session, "localCaptionAttempt", true); Set(session, "recoveringLocalCoach", true); Set(session, "localCoachReady", true);
            Set(session, "recoverCoachAt", 1234f);
            SetProperty(session, "CaptionFallbackAllowed", true);
            SetProperty(session.coach, "SessionId", "synthetic-mode-coach");
            session.anatomy.SetRegistrationValid(true); session.patientFrame.gameObject.SetActive(true);
        }
        static void AssertReset(NativeCaseSession session)
        {
            Assert(!session.anatomy.RegistrationValid && !session.anatomy.CanDisplay && !session.patientFrame.gameObject.activeSelf,
                "mode change immediately closes anatomy and port interaction visibility");
            Assert(session.anatomy.GetComponentsInChildren<Renderer>(true).All(item => !item.enabled)
                && session.anatomy.GetComponentsInChildren<Collider>(true).All(item => !item.enabled),
                "actual practice renderers and colliders are hidden before the next Update");
            Assert(!session.CoachPrepared && string.IsNullOrEmpty(session.coach.SessionId) && !session.voice.Connected,
                "old presentation coach and voice cannot survive a real mode change");
            Assert(!Get<bool>(session, "localCaptionAttempt") && !Get<bool>(session, "recoveringLocalCoach")
                && !Get<bool>(session, "localCoachReady") && Get<float>(session, "recoverCoachAt") == 0
                && !session.CaptionFallbackAllowed && !Get<bool>(session, "handoffVoiceAllowed"),
                "mode change clears old prepractice captions, coach recovery and handoff voice authority");
        }

        static void Acquire(NativeBodyRegistration body)
        { body.ResetFit(); SetProperty(body, "EnabledByOperator", true); Observe(body); Observe(body); Observe(body); }
        static void Observe(NativeBodyRegistration body)
        {
            var points = new[] { new Vector3(.167503f, 1, .521565f), new Vector3(-.167503f, 1, .521565f),
                new Vector3(.084307f, 1, 0), new Vector3(-.084307f, 1, 0) };
            var landmarks = new NativeBodyRegistration.Landmark[33];
            for (int i = 0; i < landmarks.Length; i++) landmarks[i] = new NativeBodyRegistration.Landmark
            { index = i, x = .5f, y = .5f, visibility = .9f, presence = .9f };
            int[] ids = { 11, 12, 23, 24 };
            for (int i = 0; i < ids.Length; i++)
            { landmarks[ids[i]].x = (points[i].x + 1) / 2; landmarks[ids[i]].y = (1 - points[i].z) / 2; }
            var eye = new Vector3(0, 2, 0);
            var surface = new BodySurfaceSnapshot
            {
                bottomLeft = new Ray(eye, new Vector3(-1, -1, -1)), bottomRight = new Ray(eye, new Vector3(1, -1, -1)),
                topLeft = new Ray(eye, new Vector3(-1, -1, 1)), lensForward = Vector3.down
            };
            for (int y = 0; y < BodySurfaceSnapshot.Height; y++) for (int x = 0; x < BodySurfaceSnapshot.Width; x++)
            {
                int index = y * BodySurfaceSnapshot.Width + x;
                surface.points[index] = new Vector3(2f * x / (BodySurfaceSnapshot.Width - 1) - 1, 1, 1 - 2f * y / (BodySurfaceSnapshot.Height - 1));
                surface.normals[index] = Vector3.up; surface.valid[index] = true;
            }
            body.Process(new NativeBodyRegistration.Reply
            {
                schema = "scalpal.body_pose.v1", frameId = "synthetic-mode-frame", imageWidth = 640, imageHeight = 480,
                coordinateConvention = "normalized_image_top_left", valid = true, personCount = 1, landmarks = landmarks,
                model = new NativeBodyRegistration.Model { sha256 = "59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a" }
            }, "synthetic-mode-frame", new Vector2Int(640, 480), Time.realtimeSinceStartup, surface);
        }

        readonly struct TransformState
        {
            readonly Vector3 position, scale;
            readonly Quaternion rotation;
            public TransformState(Transform transform) { position = transform.position; rotation = transform.rotation; scale = transform.localScale; }
            public bool Matches(Transform transform) => Vector3.Distance(position, transform.position) < .00001f
                && Quaternion.Angle(rotation, transform.rotation) < .001f && Vector3.Distance(scale, transform.localScale) < .00001f;
        }
        sealed class IdentityState
        {
            readonly string patient, procedure, sessionId, attemptId, boundSession, boundAttempt;
            readonly bool sharedReady, requested, failed;
            readonly UnityEngine.Object exercise, anatomy, workbench, coach, realtime;
            readonly string ticketSession, ticketAttempt;
            public IdentityState(NativeCaseSession session)
            {
                patient = session.SelectedPatientId; procedure = session.SelectedProcedureId;
                sessionId = session.realtime.SessionId; attemptId = session.realtime.AttemptId;
                boundSession = Get<string>(session, "boundSharedSession"); boundAttempt = Get<string>(session, "boundSharedAttempt");
                sharedReady = Get<bool>(session, "sharedAttemptReady"); requested = Get<bool>(session, "attemptRequested"); failed = Get<bool>(session, "attemptFailed");
                exercise = session.exercise; anatomy = session.anatomy; workbench = session.workbench; coach = session.coach; realtime = session.realtime;
                ticketSession = HandoffRun.Current?.sharedSessionId; ticketAttempt = HandoffRun.Current?.attemptId;
            }
            public void AssertPreserved(NativeCaseSession session, string phase)
            {
                Assert(session.Phase == phase && session.SelectedPatientId == patient && session.SelectedProcedureId == procedure,
                    "presentation selection preserves lifecycle, subject and reviewed procedure");
                Assert(session.realtime.SessionId == sessionId && session.realtime.AttemptId == attemptId
                    && Get<string>(session, "boundSharedSession") == boundSession && Get<string>(session, "boundSharedAttempt") == boundAttempt
                    && Get<bool>(session, "sharedAttemptReady") == sharedReady && Get<bool>(session, "attemptRequested") == requested
                    && Get<bool>(session, "attemptFailed") == failed && HandoffRun.Current?.sharedSessionId == ticketSession
                    && HandoffRun.Current?.attemptId == ticketAttempt, "presentation selection preserves the exact shared attempt without a reducer request");
                Assert(session.exercise == exercise && session.anatomy == anatomy && session.workbench == workbench
                    && session.coach == coach && session.realtime == realtime, "both modes retain the same tool/anatomy/exercise/coach/realtime core");
            }
        }
        static T Get<T>(object target, string field) => (T)(target.GetType().GetField(field, Private)
            ?? throw new InvalidOperationException("Fixture field missing: " + field)).GetValue(target);
        static void Set(object target, string field, object value) => (target.GetType().GetField(field, Private)
            ?? throw new InvalidOperationException("Fixture field missing: " + field)).SetValue(target, value);
        static void SetProperty(object target, string name, object value) => (target.GetType().GetProperty(name)?.GetSetMethod(true)
            ?? throw new InvalidOperationException("Fixture property missing: " + name)).Invoke(target, new[] { value });
        static void SetStaticProperty(Type target, string name, object value) => (target.GetProperty(name)?.GetSetMethod(true)
            ?? throw new InvalidOperationException("Fixture static property missing: " + name)).Invoke(null, new[] { value });
        static void Call(object target, string method) => (target.GetType().GetMethod(method, Private)
            ?? throw new InvalidOperationException("Fixture method missing: " + method)).Invoke(target, null);
        static void Assert(bool value, string message)
        { if (!value) throw new InvalidOperationException("Operating room mode validation failed: " + message); checks++; }
    }
}
