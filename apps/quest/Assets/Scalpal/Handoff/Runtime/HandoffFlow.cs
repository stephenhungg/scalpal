using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Scalpal.EncounterOffice;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using Scalpal.Shell;
using Scalpal.Voice;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Scalpal.Handoff
{
    // A UI/lifecycle adapter only. NativeCaseSession and its CaseRunner remain the sole scored path.
    [DefaultExecutionOrder(150)]
    public sealed class HandoffFlow : MonoBehaviour
    {
        HandoffCard card;
        NativeEncounterSession office;
        NativeCaseSession surgery;
        string phase = "office", rendered = "", failure = "";
        float entered, nextRefresh, nextCoachRetry, nextHealth, nextImport, lossStarted = -1;
        int realigns;
        bool shellPaused, loading, healthBusy, focused = true, suspended, fitConfirmed, coachTried, menuDown, briefingDone;
        Scalpal.Briefing.BriefingDirector briefing;
        const string DefaultPoseEndpoint = "http://localhost:8790";
        HandoffTicket Ticket => HandoffRun.Current;
        string ReturnLabel
        {
            get
            {
                for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                    if (SceneUtility.GetScenePathByBuildIndex(i).Contains("/Shell/")) return "Back to explore";
                return "Back to patient picker";
            }
        }
        public static string ExpectedRole(string phase) => phase == "score" || phase == "challenge" || phase == "consequence" || phase == "theatre" ? "attending" : phase == "timeout" || phase == "practice" ? "coach" : "none";
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindFirstObjectByType<HandoffFlow>()) return;
            var go = new GameObject("ScalpalRunHandoff"); DontDestroyOnLoad(go); go.AddComponent<HandoffFlow>();
        }
        void Awake()
        {
            SceneManager.sceneLoaded += Loaded;
            var prefab = Resources.Load<HandoffCard>("HandoffCard");
            if (prefab) { card = Instantiate(prefab, transform); card.Hide(); }
            BindScene();
        }
        void Loaded(Scene scene, LoadSceneMode mode) => BindScene();
        void BindScene()
        {
            office = FindFirstObjectByType<NativeEncounterSession>(); surgery = FindFirstObjectByType<NativeCaseSession>();
            if (!office && !surgery && FindFirstObjectByType<Scalpal.Recap.RecapController>())
            { loading = false; phase = "ending"; if (card) card.Hide(); return; }
            if (!office && !surgery) { HandoffRun.EndRun(); ResetRunState(); loading = false; phase = "office"; if (card) card.Hide(); }
            if (surgery && Ticket != null && !loading) SetPhase("register");
        }
        void OnDestroy() { SceneManager.sceneLoaded -= Loaded; }
        void SetPhase(string value)
        {
            phase = value; entered = Time.unscaledTime; rendered = "";
            if (value != "practice") lossStarted = -1; // The 15/45 s fit-loss ladder is per practice interval.
            if (briefing && value != "briefing") briefing.gameObject.SetActive(false); // Paused/realigning: resumes on re-entry.
            if (surgery) surgery.SetHandoffVoiceAllowed(ExpectedRole(value) == "coach");
            if (ExpectedRole(value) == "none") DisconnectAll();
        }
        // Fit check, briefing and Time-Out happen before anyone scrubs in: the instrument table and every tool stay out of
        // view (draw only; colliders and rest poses are untouched) until practice begins.
        public bool PreparingTheatre => surgery && (phase == "register" || phase == "briefing" || phase == "timeout" || phase == "transition");
        bool toolsHidden;
        void ShowSurgeryTools(bool show)
        {
            if (!surgery || !surgery.workbench || toolsHidden == !show) return;
            toolsHidden = !show;
            var roots = new List<GameObject>();
            foreach (var tool in surgery.workbench.tools ?? Array.Empty<Scalpal.Instruments.InstrumentBehaviour>()) if (tool) roots.Add(tool.gameObject);
            foreach (var root in surgery.gameObject.scene.GetRootGameObjects()) if (root.name == "Workbench") roots.Add(root);
            foreach (var root in roots) foreach (var renderer in root.GetComponentsInChildren<Renderer>(true)) renderer.forceRenderingOff = !show;
        }
        static void DisconnectAll()
        {
            foreach (var voice in FindObjectsByType<QuestJarvisVoice>(FindObjectsSortMode.None)) voice.Disconnect();
        }
        public static bool OpenFromOffice(NativeEncounterSession session)
        {
            var flow = FindFirstObjectByType<HandoffFlow>();
            return flow && flow.ImportOffice(session);
        }
        // Skip to surgery (explore, office pause, or a patient with no authored interview): straight to the Theatre card.
        public static bool OpenSkipped(NativeEncounterSession session)
        {
            var flow = FindFirstObjectByType<HandoffFlow>();
            return flow && flow.ImportSkipped(session);
        }
        bool ImportSkipped(NativeEncounterSession session)
        {
            if (!session || !EncounterContract.IsSkipped(session.Score)) { failure = "Skip unavailable"; return false; }
            try
            {
                var context = session.Score;
                var ticket = HandoffRun.BeginSkipped(context, session.baseUrl);
                HandoffRun.BindOfficeSource(ticket, new EncounterSurgeryHandoff { patientId = context.patientId, encounterId = "", procedureId = context.procedureId,
                    procedureTitle = context.procedureTitle, serviceUrl = session.baseUrl, sharedSessionId = session.SharedSessionId, attemptId = session.SharedAttemptId,
                    assessment = new EncounterAssessment(), scorecard = context });
                office = session; ResetRunState(); failure = ""; SetPhase("theatre"); return true;
            }
            catch (ArgumentException exception) { HandoffRun.Clear(); failure = exception.Message; return false; }
        }
        bool ImportOffice(NativeEncounterSession session)
        {
            if (!session) { failure = "Office unavailable"; return false; }
            if (!session.TryPrepareHandoff(out var source, out var reason))
            { failure = reason; return false; }
            if (Ticket?.encounterId == source.encounterId) { failure = ""; SetPhase("score"); return true; }
            try
            {
                var ticket = HandoffRun.Begin(session.State, session.Score, session.baseUrl);
                HandoffRun.BindOfficeSource(ticket, source); office = session; ResetRunState(); SetPhase("score"); return true;
            }
            catch (ArgumentException exception) { HandoffRun.Clear(); failure = exception.Message; return false; }
        }
        void Update()
        {
            if (!card) return;
            // The global shell owns its menu. Keep surgery stopped even though its HTTP/voice
            // coroutines use realtime clocks; restore our explicit fit/resume gate afterwards.
            if (ShellPause.Instance && ShellPause.Instance.IsPaused)
            {
                if (!shellPaused) { shellPaused = true; if (surgery) surgery.PauseHandoffPractice(); DisconnectAll(); card.Hide(); rendered = ""; }
                return;
            }
            if (shellPaused)
            {
                shellPaused = false; rendered = "";
                if (surgery && Ticket != null) ResumeGate();
            }
            ShowSurgeryTools(!PreparingTheatre);
            if (loading || !focused || ShellTransition.Busy) return;
            var left = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.LeftHand);
            left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.menuButton, out bool menu);
            if (!ShellPause.Instance && menu && !menuDown)
            {
                if (surgery && Ticket?.practiceStarted == true) { surgery.PauseHandoffPractice(); SetPhase("paused"); }
                else if (office) SetPhase("setup");
            }
            menuDown = menu;
            // Choosing another patient in the office starts a new run; the previous patient's ticket must not reach the OR.
            if (office && Ticket != null && office.State?.encounterId != Ticket.encounterId) OfficePatientChanged();
            if (office && office.Score != null && office.State?.phase == "scored" && (Ticket == null || Ticket.encounterId != office.State.encounterId))
            {
                bool imported = false;
                if (Time.unscaledTime >= nextImport) { nextImport = Time.unscaledTime + 1; imported = ImportOffice(office); }
                if (!imported)
                {
                    Show("Waiting for scored office attempt", failure, new[] { "Refresh scorecard", ReturnLabel }, i => { if (i == 0) office.RefreshState(); else BackToExplore(); });
                    return;
                }
            }
            if (office && Time.unscaledTime >= nextHealth && !healthBusy)
            { nextHealth = Time.unscaledTime + 10; StartCoroutine(CheckPreflight()); }
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .25f;
            if (phase == "setup") { SetupCard(); return; }
            if (phase == "office") return;
            if (Ticket == null) return;
            if (phase == "score" && Ticket.scorecard.kind == "interview")
                // One compact card after the interview: score, grade, key feedback, missed rounds, and one button.
                Show("Interview score · " + Ticket.scorecard.total + "/" + Ticket.scorecard.max + " · " + Ticket.scorecard.grade, EncounterContract.CompactScore(Ticket.scorecard),
                    new[] { "To theatre" }, _ => SetPhase(!Ticket.escalated ? "theatre" : !Ticket.challengeSeen ? "challenge" : !Ticket.consequenceSeen ? "consequence" : "theatre"));
            else if (phase == "score")
                Show("Clinical reasoning · " + Ticket.scorecard.total + "/" + Ticket.scorecard.max + " · " + Ticket.scorecard.grade,
                    Ticket.scorecard.spoken + "\n\n" + RiskText(Ticket.scorecard), new[] { "To theatre", "Theatre setup (operator)" }, i => { if (i == 1) SetPhase("setup"); else SetPhase(!Ticket.escalated ? "theatre" : !Ticket.challengeSeen ? "challenge" : !Ticket.consequenceSeen ? "consequence" : "theatre"); });
            else if (phase == "challenge")
                Show("One challenge", "You proposed: " + (office?.State?.assessment?.procedure ?? (string.IsNullOrEmpty(Ticket.learnerProcedure) ? Ticket.scorecard.diagnosisGiven : Ticket.learnerProcedure)) +
                    "\nThe findings support " + (string.IsNullOrEmpty(Ticket.scorecard.diagnosisExpected) ? EncounterContract.Best(Ticket.scorecard, "diagnosis") : Ticket.scorecard.diagnosisExpected) + ".\nWhat finding would change your plan? Reflect, then acknowledge the correct procedure. Your original reasoning score is kept.",
                    new[] { "I would choose " + Ticket.procedureTitle }, _ => { Ticket.challengeSeen = true; SetPhase("consequence"); });
            else if (phase == "consequence")
            {
                float wait = (Ticket.demoMode ? 6 : 8) - (Time.unscaledTime - entered);
                string text = Ticket.procedureId == "lap_appendectomy" ? "6 hours later · teaching scenario\nHR 118 · T 38.9 °C · BP 98/60\nPerforated appendix. The surgical team takes the case." : "Case escalated · teaching scenario\nDelayed treatment risks deterioration. The surgical team takes the case.";
                Show("Case escalated", text + "\nYou will scrub in for " + Ticket.procedureTitle + ".", new[] { wait > 0 ? "Continue in " + Mathf.CeilToInt(wait) + " s" : "Continue" }, _ => { Ticket.consequenceSeen = true; SetPhase("theatre"); }, new[] { wait <= 0 });
            }
            else if (phase == "theatre") Theatre();
            else if (phase == "register") Registration();
            else if (phase == "briefing") Briefing();
            else if (phase == "timeout") TimeOut();
            else if (phase == "practice") Practice();
            else if (phase == "paused") Show("Paused", "Press to continue. In AR, alignment must be checked again before scoring resumes.", new[] { "Resume", "Virtual OR (new attempt)", Ticket.presentationMode == "mixed_reality" ? "End AR" : ReturnLabel }, i =>
            {
                if (i == 2) { if (Ticket.presentationMode == "mixed_reality") EndAR(); else BackToExplore(); } else if (i == 1) SwitchToVirtual();
                else if (surgery && surgery.RegistrationReady) { surgery.ResumeHandoffPractice(); SetPhase("practice"); surgery.ReconnectTimeOutVoice(); card.Hide(); }
                else { fitConfirmed = false; SetPhase("register"); }
            }, new[] { true, Ticket.presentationMode != "virtual", true });
            else if (phase == "stopped") Show("Volunteer stopped · Practice paused", "The volunteer can get up. Continue in the virtual OR with a new attempt; your office score is kept.", new[] { "Virtual OR (new attempt)", ReturnLabel }, i => { if (i == 0) SwitchToVirtual(); else BackToExplore(); });
            else if (phase == "recap") Show("Practice complete", surgery.Message + "\n" + EncounterContract.ReasoningLine(Ticket.scorecard) +
                "\n" + Scalpal.Capture.HandCaptureRecorder.StatusLine(), new[] { "Retry surgery", ReturnLabel }, i => { if (i == 1) BackToExplore(); else { surgery.Retry(); fitConfirmed = false; SetPhase("register"); } });
        }
        void Theatre()
        {
            if (!HandoffRun.Supported(Ticket.procedureId))
            {
                Show("Surgery coming soon", "Surgery content for " + Ticket.procedureTitle + " is not built yet.\n" + EncounterContract.ReasoningLine(Ticket.scorecard),
                    new[] { ReturnLabel }, _ => BackToExplore()); return;
            }
            var preflight = HandoffRun.Preflight;
            var summary = Ticket.scorecard.patientName + "\n" + Ticket.procedureTitle + " · " + Ticket.scorecard.urgency + "\n" +
                (preflight.ArAvailable ? "Permissions and services ready. AR recommended." : "AR unavailable: " + preflight.UnavailableReason + ". Virtual OR recommended.") +
                "\nVirtual organs are generic teaching anatomy." + (failure.Length > 0 ? "\n" + failure : "");
            Show("To theatre", summary, new[] { "Volunteer patient (AR)", "Virtual OR (VR)", "Theatre setup (operator)" }, i =>
            { if (i == 2) SetPhase("setup"); else ChooseMode(i == 0 ? "mixed_reality" : "virtual"); }, new[] { preflight.ArAvailable, true, true });
        }
        public void ChooseMode(string mode)
        {
            if (loading || Ticket == null || !HandoffRun.CanChoose(mode, HandoffRun.Preflight)) return;
            if (!Application.CanStreamedLevelBeLoaded("NativeSession"))
            { Show("Combined player required", "This standalone office build does not contain the operating room. Install the unified Scalpal player.", new[] { "Back to scorecard" }, _ => SetPhase("score")); return; }
            Ticket.presentationMode = mode; Ticket.modeChosenBy = "learner";
            ResetRunState();
            StartCoroutine(Transition());
        }
        IEnumerator Transition()
        {
            var ticket = Ticket;
            loading = true; SetPhase("transition");
            if (office) office.StopVoice();
            card.Hide();
            if (!ShellView.Configured) ShellView.Configure(Scalpal.Brand.ScalpalBrand.Active);
            // Use the shell's single transition owner so its pause/back controls cannot race OR loading.
            yield return ShellTransition.Ensure().Load("NativeSession", "Pre-op · " + ticket.scorecard.patientName + "\n" + ticket.procedureTitle, revealSeconds: ticket.presentationMode == "mixed_reality" ? 1f : .5f);
            if (!ReferenceEquals(ticket, Ticket)) { loading = false; yield break; }
            BindScene(); loading = false;
            if (!surgery) { failure = ShellTransition.LastError ?? "Operating room unavailable"; SetPhase("theatre"); yield break; }
            card.Recenter(); fitConfirmed = false; realigns = 0; SetPhase("register");
        }
        void Registration()
        {
            if (!surgery) return;
            if (surgery.AttemptNeedsRetry)
            { Show("Shared attempt interrupted", surgery.Message, new[] { "Start matching attempt", ReturnLabel }, i => { if (i == 0) surgery.Retry(); else BackToExplore(); }); return; }
            if (!surgery.HandoffVerified)
            {
                // Loading the case and confirming the office result is the normal first second in the OR, not a failure.
                if (surgery.Busy) { Show("Preparing the operating room", "Loading " + Ticket.procedureTitle + ".", new[] { ReturnLabel }, _ => BackToExplore()); return; }
                Show("Case unavailable", surgery.Message, new[] { "Try again", ReturnLabel }, i => { if (i == 0) surgery.Retry(); else BackToExplore(); }); return;
            }
            // Re-entering registration mid-practice must return to the paused card, never to Time-Out.
            if (Ticket.presentationMode == "virtual") { fitConfirmed = true; SetPhase(AfterFit(Ticket.practiceStarted, briefingDone)); return; }
            var registration = surgery.bodyRegistration;
            if (!registration) { Show("Body detection unavailable", "Continue in the virtual OR.", new[] { "Virtual OR" }, _ => SwitchToVirtual()); return; }
#if UNITY_ANDROID && !UNITY_EDITOR
            HandoffRun.Preflight.cameraGranted = Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA");
            HandoffRun.Preflight.sceneGranted = Permission.HasUserAuthorizedPermission("com.oculus.permission.USE_SCENE");
#endif
            if (!HandoffRun.Preflight.cameraGranted || !HandoffRun.Preflight.sceneGranted)
            { Show("Camera access is off", "Continue in the virtual OR. Camera and spatial permissions can be restored in Quest settings.", new[] { "Virtual OR", ReturnLabel }, i => { if (i == 0) SwitchToVirtual(); else BackToExplore(); }); return; }
            registration.BeginPreflightedDetection();
            float elapsed = Time.unscaledTime - entered;
            bool valid = registration.Accepted && registration.CandidateValid;
            if (valid)
            {
                Show("Fit: Good · Check alignment", "Do the organs sit inside the torso? Lean gently left and right to check stability.\nGeneric teaching anatomy, not this person's organs.",
                    new[] { "Looks right", "Realign", "Virtual OR" }, i => { if (i == 0) { fitConfirmed = true; SetPhase(AfterFit(Ticket.practiceStarted, briefingDone)); } else if (i == 1) Realign(); else SwitchToVirtual(); }); return;
            }
            string dots = ""; string[] names = { "Left shoulder", "Right shoulder", "Left hip", "Right hip" };
            for (int i = 0; i < 4; i++) dots += (registration.VisibleLandmarks[i] ? "[seen] " : "[waiting] ") + names[i] + "  ";
            string hint = registration.PersonCount > 1 ? "Two people in view. Ask others to step out of frame." : elapsed > 10 && registration.PersonCount == 0 ? "Can't see a person. More light, or step back so shoulders to hips are visible." : "Look at the patient's chest and belly. Keep shoulders and hips visible.";
            if (registration.PersonCount == 1)
                for (int i = 0; i < 4; i++) if (!registration.VisibleLandmarks[i]) { hint = "Can't see " + names[i].ToLowerInvariant() + ". Move the arm or blanket off it."; break; }
            if (registration.PersonCount == 1 && !registration.SurfaceMeasured && elapsed >= 8) hint = "Hold still. Measuring the torso surface.";
            if (registration.SurfaceMeasured && !valid) hint = "Hold still while the fit settles.";
            bool offer = elapsed >= (Ticket.demoMode ? 20 : 30) || realigns >= 2 || !HandoffRun.Preflight.ArAvailable;
            Show(offer ? "Switch to the virtual OR?" : "You're in the real room now", "Stay where you are. Check the space around you.\nPatient face-up, arms at sides. Stand at their right side about an arm's length away.\n1 Person found: " + registration.PersonCount + "\n" + dots +
                "\n2 Measuring torso surface: " + (registration.SurfaceMeasured ? "Ready" : "Waiting") + "\n3 Holding still: " + registration.StableObservations + "/3\nFit: Check alignment\n" + hint,
                new[] { "Keep trying", "Virtual OR", "End AR" }, i => { if (i == 0) entered = Time.unscaledTime; else if (i == 1) SwitchToVirtual(); else EndAR(); });
        }
        /// <summary>After a confirmed fit: a started attempt resumes paused; otherwise the briefing once per OR entry, then the Time-Out.</summary>
        public static string AfterFit(bool practiceStarted, bool briefingDone) => practiceStarted ? "paused" : briefingDone ? "timeout" : "briefing";
        // Pre-surgery briefing (skippable). It never needs the network; finishing or skipping both lead to the Time-Out.
        void Briefing()
        {
            if (Ticket.practiceStarted) { SetPhase("paused"); return; }
            if (briefingDone) { SetPhase("timeout"); return; }
            card.Hide(); rendered = "";
            if (!briefing) briefing = Scalpal.Briefing.BriefingDirector.Begin(card.viewer ? card.viewer.transform : Camera.main ? Camera.main.transform : null);
            else if (!briefing.Finished && !briefing.gameObject.activeSelf) briefing.gameObject.SetActive(true);
            if (!briefing.Finished) return;
            briefingDone = true; EndBriefing(); SetPhase("timeout");
        }
        void EndBriefing()
        {
            if (briefing) { if (Application.isPlaying) Destroy(briefing.gameObject); else DestroyImmediate(briefing.gameObject); }
            briefing = null;
        }
        void Realign() { realigns++; fitConfirmed = false; surgery.bodyRegistration.ResetFit(); SetPhase("register"); }
        void SwitchToVirtual()
        {
            if (Ticket?.presentationMode == "virtual" || !surgery) return;
            bool started = Ticket.practiceStarted;
            // The OR core refuses mode changes mid-practice; an explicit new attempt comes first (office score kept).
            if (Ticket.practiceStarted) { surgery.Retry(); if (Ticket.practiceStarted) return; }
            if (!surgery.TryChangePresentation(false)) return;
            Ticket.modeChosenBy = "fallback"; fitConfirmed = true; coachTried = false; nextCoachRetry = 0; SetPhase(started || briefingDone ? "timeout" : "briefing");
        }
        public void EndAR()
        {
            if (!surgery) return;
            surgery.PauseHandoffPractice(); surgery.bodyRegistration.StopTracking(); HandoffRun.Preflight.volunteerConsented = false; SetPhase("stopped");
        }
        void TimeOut()
        {
            // Time-Out precedes practice only; a started attempt resumes from the paused card, without coach reconnects.
            if (Ticket.practiceStarted) { SetPhase("paused"); return; }
            if (!surgery || !surgery.HandoffVerified || surgery.AttemptNeedsRetry) { SetPhase("register"); return; }
            if (!surgery.RegistrationReady || !fitConfirmed) { fitConfirmed = false; SetPhase("register"); return; }
            // The OR opens its own shared-session connection on entry; joining is not a connection error.
            if (!surgery.realtime.Paired && surgery.realtime.Joining)
            { Show("Preparing the operating room", "Joining the shared session.", new[] { ReturnLabel }, _ => BackToExplore()); return; }
            if (!surgery.realtime.Paired)
            { Show("Shared headset session unavailable", surgery.realtime.Status + "\nPair the headset with the companion before beginning this attempt.", new[] { "Retry shared connection", ReturnLabel }, i => { if (i == 0) surgery.realtime.Reconnect(); else BackToExplore(); }); return; }
            surgery.SetHandoffVoiceAllowed(true);
            if (surgery.CoachPrepared && Time.unscaledTime >= nextCoachRetry) { nextCoachRetry = Time.unscaledTime + 10; surgery.ReconnectTimeOutVoice(); }
            if (!surgery.CoachPrepared && Time.unscaledTime >= nextCoachRetry)
            { nextCoachRetry = Time.unscaledTime + 10; coachTried |= surgery.PrepareTimeOut(); }
            string voice = surgery.voice.Status == "error" ? "Voice unavailable. Continue with captions." : surgery.CoachPrepared ? "Jarvis: Scrubbed in with you. Confirm the patient, procedure and site. Hold Y to talk to me." : "Connecting Jarvis. Captions and authored local scoring are available if the coach is offline.";
            string body = "Patient: " + Ticket.scorecard.patientName + "\nProcedure: " + Ticket.procedureTitle + "\nSite: " + Ticket.scorecard.site + "\nUrgency: " + Ticket.verifiedCase.urgency + "\n" + RiskText(Ticket.scorecard) +
                "\n" + voice + "\nMistakes are expected; this is practice.\n" + Scalpal.Capture.HandCaptureRecorder.PlannedNotice(Ticket, HandoffRun.Preflight);
            var risks = ReviewRisks(Ticket.scorecard);
            if (Ticket.patientConfirmed && Ticket.procedureConfirmed && Ticket.siteConfirmed && !Ticket.risksConfirmed && risks.Length > 0)
            {
                int index = Mathf.Clamp(Ticket.riskReviewIndex, 0, risks.Length - 1); var risk = risks[index];
                Show("TIME-OUT · Risk " + (index + 1) + " of " + risks.Length, "Patient-specific concern: " + risk.label + " (" + StatusText(risk.status) + ")\n" + risk.detail +
                    "\nPlan for it only if your surgical plan addresses this risk.\n" + body, new[] { "Plan for: " + risk.label, "Not addressed", "Virtual OR", ReturnLabel }, i =>
                    {
                        if (i == 2) { SwitchToVirtual(); return; } if (i == 3) { BackToExplore(); return; }
                        ReviewRisk(index, i == 0);
                    }, new[] { true, true, Ticket.presentationMode != "virtual", true });
            }
            else if (Ticket.AllConfirmed)
                Show("TIME-OUT · Ready", body + "\n" + failure, new[] { "Begin practice", "Change to virtual OR", ReturnLabel }, i => { if (i == 0) StartCoroutine(ConfirmTimeOut()); else if (i == 1) SwitchToVirtual(); else BackToExplore(); }, new[] { !surgery.Busy && (surgery.CoachPrepared || coachTried && surgery.CaptionFallbackAllowed), Ticket.presentationMode != "virtual", true });
            else
            {
                string label = !Ticket.patientConfirmed ? "Confirm patient" : !Ticket.procedureConfirmed ? "Confirm procedure" : !Ticket.siteConfirmed ? "Confirm site" : !Ticket.risksConfirmed ? "Acknowledge found and missed risks" : !Ticket.antibioticsReviewed ? "Review antibiotic prophylaxis (simulation)" : "Review imaging (simulation)";
                Show("TIME-OUT", body, new[] { label, "Virtual OR", ReturnLabel }, i =>
                {
                    if (i == 1) { SwitchToVirtual(); return; } if (i == 2) { BackToExplore(); return; }
                    if (!Ticket.patientConfirmed) Ticket.patientConfirmed = true; else if (!Ticket.procedureConfirmed) Ticket.procedureConfirmed = true;
                    else if (!Ticket.siteConfirmed) Ticket.siteConfirmed = true; else if (!Ticket.risksConfirmed) Ticket.risksConfirmed = true;
                    else if (!Ticket.antibioticsReviewed) Ticket.antibioticsReviewed = true; else Ticket.imagingReviewed = true;
                }, new[] { true, Ticket.presentationMode != "virtual", true });
            }
        }
        void ReviewRisk(int index, bool confirmed)
        {
            var risks = ReviewRisks(Ticket.scorecard);
            if (Ticket.risksConfirmed || index != Ticket.riskReviewIndex || index >= risks.Length) return; // stale or repeated click
            if (confirmed && !Ticket.confirmedRiskTypes.Contains(risks[index].type)) Ticket.confirmedRiskTypes.Add(risks[index].type);
            Ticket.riskReviewIndex++;
            if (Ticket.riskReviewIndex >= risks.Length) Ticket.risksConfirmed = true;
        }
        public static EncounterCarryoverItem[] ReviewRisks(EncounterScore score) => (score?.carryoverItems ?? Array.Empty<EncounterCarryoverItem>())
            .Where(item => item != null && !string.IsNullOrEmpty(item.type)).GroupBy(item => item.type).Select(group => group.First()).ToArray();
        IEnumerator ConfirmTimeOut()
        {
            // A started attempt must never be re-selected; Begin practice is only for the first start of this attempt.
            if (loading || Ticket == null || !Ticket.AllConfirmed || Ticket.practiceStarted) yield break;
            var ticket = Ticket; var consumer = surgery; string attempt = Ticket.attemptId;
            loading = true; failure = "";
            // Only risks the learner individually planned for count as caught; skipped ones are reported missed.
            var selected = Ticket.confirmedRiskTypes.Distinct().ToArray();
            string json = null;
            yield return Http(Ticket.serviceUrl + "/patients/" + Uri.EscapeDataString(Ticket.patientId) + "/preop-check", JsonUtility.ToJson(new PreopCheckRequest { selected = selected, scope = "surgical" }), value => json = value);
            if (!ReferenceEquals(ticket, Ticket) || !consumer || consumer != surgery) { loading = false; yield break; }
            loading = false;
            if (!focused || ShellTransition.Busy || (ShellPause.Instance && ShellPause.Instance.IsPaused)
                || phase != "timeout" || ticket.attemptId != attempt) yield break;
            PreopCheckResult result = null;
            try { if (json != null) result = JsonUtility.FromJson<PreopCheckResult>(json); } catch (ArgumentException) { }
            if (result != null && (result.patientId != Ticket.patientId || result.caseId != Ticket.verifiedCase.caseId))
            { failure = "Time-Out response identity mismatch; practice blocked."; loading = false; yield break; }
            Ticket.preopResult = result; // Offline is explicit; this review does not change the diagnosis score.
            if (surgery.FinishTimeOut(surgery.CaptionFallbackAllowed)) { SetPhase("practice"); card.Hide(); }
            else failure = "Waiting for shared attempt, tracking, or coach request to settle. Try again.";
            loading = false;
        }
        void Practice()
        {
            if (!surgery) return;
            if (!Ticket.practiceStarted) { fitConfirmed = false; SetPhase("register"); return; }
            if (surgery.Phase == "Recap") { SetPhase("recap"); return; }
            if (Ticket.presentationMode == "mixed_reality" && !surgery.RegistrationReady)
            {
                if (lossStarted < 0) lossStarted = Time.unscaledTime;
                float lost = Time.unscaledTime - lossStarted;
                Show("Patient moved · Practice paused", "Anatomy and scoring are hidden until a stable fit returns. Hold still.\n" + (lost >= 45 ? "Continue in VR with a new attempt; office score kept." : "Waiting for three stable observations."),
                    new[] { "Realign", "Virtual OR (new attempt)", "End AR" }, i => { if (i == 0) Realign(); else if (i == 1) SwitchToVirtual(); else EndAR(); }, new[] { lost >= 15, lost >= 45, true });
            }
            else { lossStarted = -1; card.Hide(); rendered = ""; }
        }
        public static string RiskText(EncounterScore score)
        {
            var text = new StringBuilder("Anticipated risks\n");
            foreach (var item in score.carryoverItems ?? Array.Empty<EncounterCarryoverItem>())
                text.AppendLine((item.status == "found" ? "Found · " : item.status == "missed" ? "Missed · " : "Chart · ") + item.label + " (" + StatusText(item.status) + ")");
            if (score.carryoverItems == null || score.carryoverItems.Length == 0) text.Append("No chart risk chips returned.");
            return text.ToString();
        }
        static string StatusText(string status) => status == "found" ? "you asked" : status == "missed" ? "you missed this" : "chart context";
        void SetupCard()
        {
            var p = HandoffRun.Preflight;
            var labels = new List<string> { "Back to theatre",
                p.learnerCaptureConsented ? "Withdraw hand-recording consent" : "Learner agreed to hand recording" };
            string recording = "\nRecording is optional and is not required to use AR.";
            if (p.learnerCaptureConsented)
            {
                recording += "\nHand recording enabled. Participant recording permission: " + p.volunteerConsented +
                    " (required only to record an AR clip with the participant in frame).";
                labels.Add(p.volunteerConsented ? "Withdraw participant recording permission" : "Participant recording agreed");
            }
            Show("Theatre setup · Operator", "Camera permission: " + p.cameraGranted + " · Spatial permission: " + p.sceneGranted +
                "\nBody detection: " + (p.poseServiceOk ? "Online" : "Offline") + " · Coach: " + (p.coachServiceOk ? "Online" : "Offline") +
                "\nAR is available when permissions and services are ready. Detection images are not saved." + recording,
                labels.ToArray(), i =>
                {
                    if (i == 1)
                    {
                        p.learnerCaptureConsented = !p.learnerCaptureConsented;
                        if (!p.learnerCaptureConsented) p.volunteerConsented = false;
                    }
                    else if (i == 2 && p.learnerCaptureConsented) p.volunteerConsented = !p.volunteerConsented;
                    else if (i == 0)
                    {
                        if (Ticket == null) { SetPhase("office"); card.Hide(); }
                        else SetPhase(Ticket.escalated && !Ticket.consequenceSeen ? "score" : "theatre");
                    }
                });
        }
        IEnumerator CheckPreflight()
        {
            healthBusy = true;
            var p = HandoffRun.Preflight;
#if UNITY_ANDROID && !UNITY_EDITOR
            p.cameraGranted = Permission.HasUserAuthorizedPermission("horizonos.permission.HEADSET_CAMERA");
            p.sceneGranted = Permission.HasUserAuthorizedPermission("com.oculus.permission.USE_SCENE");
#endif
            // Measure into locals and publish once, so the Theatre AR button does not flicker off during each check.
            bool pose = false, coach = false;
            string poseEndpoint = surgery && surgery.bodyRegistration ? surgery.bodyRegistration.endpoint : DefaultPoseEndpoint;
            yield return Http(poseEndpoint.TrimEnd('/') + "/health", null, _ => pose = true);
            yield return Http((office ? office.baseUrl : Ticket?.serviceUrl ?? "http://localhost:8787") + "/health", null, _ => coach = true);
            p.poseServiceOk = pose; p.coachServiceOk = coach;
            healthBusy = false;
        }
        static IEnumerator Http(string url, string body, Action<string> result)
        {
            using (var request = new UnityWebRequest(url, body == null ? "GET" : "POST"))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 5;
                if (body != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)); request.SetRequestHeader("Content-Type", "application/json"); }
                yield return request.SendWebRequest(); if (request.result == UnityWebRequest.Result.Success) result(request.downloadHandler.text);
            }
        }
        void Show(string title, string body, string[] labels, Action<int> action, bool[] enabled = null)
        {
            string key = title + body + string.Join("|", labels) + (enabled == null ? "" : string.Join("|", enabled));
            if (key == rendered) return; rendered = key; card.Show(title, body, labels, action, enabled);
        }
        public void BackToExplore()
        {
            DisconnectAll(); HandoffRun.EndRun(); ResetRunState(); loading = false; card.Hide(); rendered = ""; phase = "office";
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var path = SceneUtility.GetScenePathByBuildIndex(i);
                if (path.Contains("/Shell/")) { ShellPause.ReturningToExplore = true; SceneManager.LoadScene(i); return; }
            }
            SceneManager.LoadScene("DiagnosisOffice"); // Explicit temporary fallback until Shell lands.
        }
        void OfficePatientChanged()
        {
            HandoffRun.EndRun(); ResetRunState(); phase = "office"; rendered = ""; if (card) card.Hide();
        }
        void ResetRunState()
        {
            failure = ""; lossStarted = -1; coachTried = false; nextCoachRetry = 0; realigns = 0; fitConfirmed = false; nextImport = 0;
            briefingDone = false; EndBriefing();
        }
        // Return from a headset/shell interruption. Practice only resumes through the paused card.
        void ResumeGate()
        {
            rendered = "";
            if (phase == "stopped" || phase == "recap" || phase == "transition" || phase == "ending") return;
            fitConfirmed = false;
            SetPhase(Ticket.practiceStarted || phase == "practice" || phase == "paused" ? "paused" : "register");
        }
        // Quest resume delivers both OnApplicationPause(false) and OnApplicationFocus(true); act once per edge.
        void Lifecycle(bool suspend)
        {
            if (suspend == suspended) return;
            suspended = suspend; focused = !suspend;
            if (!surgery || Ticket == null) return;
            if (suspend) surgery.PauseHandoffPractice();
            else ResumeGate();
        }
        void OnApplicationFocus(bool value) => Lifecycle(!value);
        void OnApplicationPause(bool pause) => Lifecycle(pause);
    }
}
