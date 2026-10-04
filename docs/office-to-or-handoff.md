# Scalpal: Diagnosis Office to Operating Room Handoff (UX spec)

Status: research-backed recommendation, October 3, 2026. Nothing here is built behavior. Repo read at `scalpal-flow-docs` (origin/main `1a5339f`). No repo files were edited.

**Direction this spec follows (overrides older docs where they disagree):**
- Explore hub and diagnosis office are full VR.
- The operating room offers **two modes sharing one tool/anatomy/exercise/coach core**:
  - **AR**: passthrough, a real person reclining, MediaPipe pose plus depth registration, and virtual organs on their body.
  - **VR**: a virtual patient in a virtual OR.
- The mode is chosen **at the handoff**.
- Robot-replay capture records the **raw passthrough camera of the learner's hands in both modes**.

Legend:
- **[S-n]** is a sourced claim; see section 1 and the source list at the end.
- **[R]** is our recommendation.
- **[Repo]** is a fact read from this repository, with the file named.
- *Derived* numbers are our sizing, to be checked on a Quest 3S.

---

## 0. One-paragraph summary

[R] Treat the handoff as a short **"To theatre" pre-op stage**. It is narrated as the WHO **Time-Out** and an INACSL-style **prebrief**, so the learner experiences the mode switch as clinical procedure, not a loading screen.

1. The attending (Jarvis) closes the scorecard. On the wrong-plan branch, he shows the consequence card in VR first.
2. A **Theatre card** offers **Volunteer patient (AR)** or **Virtual OR (VR)**. AR is the default only when the operator's preflight is all green: volunteer ready, consent given, permissions granted, pose service healthy.
3. Fade to black, then a world-locked title card.
4. The scene branches:
   - **VR**: fade in to the virtual OR.
   - **AR**: a deliberate 1 s fade from black to passthrough, with the title card held as a stable anchor. Then guided participant positioning, a 3-stage registration progress display, and a fit confirmation.
5. Both paths converge on the same **Time-Out card**, where coach Jarvis connects (the third voice role). The learner confirms patient, procedure, site and the office-found risks.
6. "Recording hands" begins, then step 1.

Office scoring and VR scene loading run in parallel with the operator settling the volunteer, so the AR path adds about 30–45 s, not minutes.

---

## 1. Findings

### 1.1 Meta guidance: VR and passthrough transitions, permissions, scene

- [S1] Meta says abrupt switches between virtual and physical worlds "can be an overwhelming and uncomfortable experience". It recommends "smooth blending when transitioning between the two" by controlling passthrough opacity, not an on/off cut, and suggests "an easily accessible toggle-switch UI" for frequent switching.
- [S2] Meta asks apps to "Clearly signal reality transitions… separate them and clearly mark the transition." The exception is a 1:1 match between virtual and physical, where an opacity fade "can be useful". Organs registered to the real body are that 1:1 case. Overlapping incongruent realities "can cause users to feel confused and unsafe". The World Beyond is Meta's exemplar of an animated transition.
- [S3] Meta's MR Motifs "Passthrough Transitioning" recipe fades a sphere's `_InvertedAlpha` while toggling the passthrough layer, with `OVRManager.eyeFovPremultipliedAlphaModeEnabled = false` for a smoother fade. The Core SDK `SelectivePassthrough` shader "enables you to selectively reveal Passthrough" (passthrough windows).
- [S4] Meta says the "user experience is typically better if loading screens show Passthrough in the background." Its contextual-passthrough splash fades in over the real world.
- [S5] Apps must declare and request `com.oculus.permission.USE_SCENE` at runtime. Without it, MRUK scene load fails with `NoScenePermission`. Zero anchors can also mean the user never ran Space Setup. **Unity OpenXR Meta 2.0 no longer auto-requests it** [S6].
- [S7] Head-locked content in MR "can tire them quickly and reduce usability".
- [S8] Passthrough Camera API: request permissions "from one single place"; do not mix `OVRPermissionsRequester` or OVRManager startup requests. Camera textures are valid only after `HEADSET_CAMERA` is granted.
- [R] Implications:
  - **Request camera and scene permissions in one operator preflight**, never at the dramatic handoff moment.
  - Use a **1.0 s opacity crossfade** from black to passthrough for AR, which is allowed because the overlay will be 1:1 with the body.
  - Keep a **world-locked** title card as the stable element across the switch.
  - The repo's MRUK environment raycast needs no Space Setup room scan (`docs/body-registration.md`), so **do not send the learner to Space Setup**. (filled in below from the research pass)

### 1.2 Comfort and orientation when the world switches

- [S-repo-1] Blinks and fades with spatial sound reduce disorientation at camera discontinuities, and an independent visual background gives a stable reference. Source: Meta, *Locomotion comfort & usability*, https://developers.meta.com/horizon/design/locomotion-comfort-usability/ (already cited in `docs/research/ux-vr-shell.md` §3).
- [S-repo-2] For hitch-free loads, fade at the compositor level (`OVRManager.SetColorScaleAndOffset` / OVROverlay) and load async with activation held. World-locked compositor layers are timewarped; head-locked layers judder. Source: https://developers.meta.com/horizon/documentation/unity/unity-ovroverlay/ (from `ux-vr-shell.md`).
- [S-repo-3] Portals beat cuts on presence but disorient more. Advance cues of an impending change help re-orientation. Source: Husung & Langbehn, MUC 2019, https://www.edit.fis.uni-hamburg.de/ws/files/12484141/MUC_Portals_Paper.pdf (from `ux-vr-shell.md`).

- [S9] Meta lists brief transitions to black ("blinks") as a comfort aid and notes that cut-style relocation "can sometimes lead to disorientation".
- [S10] Meta: "Users experience less discomfort when they can predict camera movements." Independent visual backgrounds "create a stable environment that responds to head movement". "Environmental sound effects can help reduce disorientation during blink effects or other occlusions."
- [R] Implications:
  - Never move the rig.
  - Announce the switch one beat ahead with the Theatre card (supports [S-repo-3]).
  - Play a soft door/whoosh cue under the black.
  - Hold the world-locked title card through both fades.

### 1.3 Apps with good VR to MR transitions

- [S11] **First Encounters** (Meta's MR showcase): creatures "burst through players' walls to reveal a new, virtual world". This is a progressive reveal anchored to the real room.
- [S3] **Cubism** loads "with a contextual Passthrough splash screen and continue[s] to play… in Passthrough without interruption". **Wooorld** is cited for MR-to-VR switching.
- [S12] The Quest system offers an opt-in double-tap on the headset side to toggle passthrough, showing that users accept explicit, user-initiated switching.
- [S13] Eleven Table Tennis offers MR and "portal" modes. Source is social posts only; weak.
- Not verified: Demeo, Spatial Ops and Gorilla Tag MR transition details.
- [R] Scalpal's best fit is the "Cubism" pattern: a calm, user-initiated fade, then stay in MR. The surgical setting should feel clinical, not spectacular. A "First Encounters" reveal is optional polish only for the VR-to-VR path.

### 1.4 Surgical Time-Out and pre-op briefing as the narrative bridge

- [S14] The WHO Surgical Safety Checklist has three phases:
  - **Sign In**, before induction: identity, site, procedure and consent; site marked; anaesthesia check; pulse oximeter; known allergy; airway risk; more than 500 ml blood loss risk.
  - **Time Out**, "before skin incision (with nurse, anaesthetist and surgeon)": team introductions by name and role; to the surgeon, "What are the critical or non-routine steps? How long will the case take? What is the anticipated blood loss?"; to the anaesthetist, "Are there any patient-specific concerns?"; to nursing, sterility and equipment.
  - **Sign Out**: procedure name, counts, specimen labels, "key concerns for recovery".
- [S15] Partly verified from the standard 2009 form and a hospital implementation: Time Out also confirms patient name, procedure and incision site, "antibiotic prophylaxis… within the last 60 minutes", and "Is essential imaging displayed?"
- [S16] In the 8-hospital checklist trial (Haynes et al., NEJM 2009), deaths fell from 1.5% to 0.8% and complications from 11.0% to 7.0%.
- [R] The Time-Out is the ideal **in-fiction bridge**:
  - It happens exactly at this transition.
  - It is a confirmation ritual, which suits a moment the learner must stand still while registration settles.
  - It is where "patient-specific concerns" (the office-found risks) are spoken aloud.
  - Sign Out maps naturally onto the recap later.

### 1.5 Simulation prebriefing (INACSL)

- [S-repo-4] The INACSL standards require debriefing to be preceded by a prebrief. The prebrief sets expectations, roles and psychological safety, and orients the learner to the modality. Source: https://lewis.gsu.edu/files/2024/06/Healthcare-Simulation-Standards-of-Best-Practice.pdf (from `docs/research/ux-medical-sim.md`).

- [S17] INACSL **Prebriefing: Preparation and Briefing** requires the briefing to "Set the tone with expectations, logistics, and roles", run a "structured orientation" to the environment and modality, and "Create a psychologically safe learning environment".
- [S18] INACSL **Debriefing** (2025) allows debriefing facilitated by a "technology-supported system", which supports the in-app recap.
- [R] The handoff doubles as the OR prebrief:
  - The Theatre card states the modality (real volunteer vs virtual).
  - The roles are learner as surgeon, Jarvis as coach and the volunteer as patient body.
  - The logistics are hands-only recording.
  - The psychological safety line goes on the Time-Out card: "Mistakes are expected; this is practice." 

### 1.6 Guiding subject positioning and confirming registration in AR

- [S19] Apple's `ARCoachingOverlayView` is "a standardized onboarding routine". It displays "during session initialization and in limited tracking situations" and hides once "the goal has been met". This supports goal-based, auto-dismissing coaching that **reappears on tracking loss**.
- [S20] MediaPipe Pose returns per-landmark `visibility` ("likelihood of the landmark being visible (present and not occluded)") and `presence`. Its detection, presence and tracking thresholds default to 0.5. These map directly onto a per-landmark progress display (4 dots).
- [S21] Novarad VisAR (FDA-cleared HoloLens 2 navigation) uses automatic fiducial registration with setup "less than 2 minutes". [S22] Medivis offers multiple registration modes, including landmark, trace and surface, with automatic verification against the scan. These show the clinical pattern: **an automatic fit, a verification step, and an alternative registration path.** Their on-screen accuracy indicators were not verified.
- [R] Implications:
  - Use a staged progress display: person, then surface, then stability.
  - Show one hint at a time, tied to the failing signal.
  - Use a qualitative quality chip ("Good" / "Check alignment"), not a millimetre number. The repo's 90 mm residual bound is an engineering limit, not demonstrated accuracy.
  - Require an explicit learner confirmation.
  - Offer a fallback (VR) instead of manual controller registration, which the repo removed.

### 1.7 Passthrough Camera API, for capture in both modes

- [S23] PCA requires a Quest 3/3S on Horizon OS v74 or later, with `horizonos.permission.HEADSET_CAMERA` (or `android.permission.CAMERA`) **and the passthrough feature enabled**.
  - Specs: 20–40 ms capture latency, 60 Hz, up to 1280×1280, YUV420, about 45 MB and about 1–2% GPU per camera.
  - The image FOV is narrower than what the user sees.
  - Camera frames are "Device User Data" under Meta's data-use policy.
- [S8] v83 added resolutions, but "do not simply select the largest". [S24] PCA apps have been publishable since v76.
- [R] Implications for capture in **both** modes:
  - In VR, keep the passthrough feature enabled and the camera running while rendering opaque VR. The passthrough layer stays hidden or at opacity 0. Whether PCA frames flow with no passthrough layer composited is **not confirmed by Meta docs, so test it first on the headset** (§5.2 #8).
  - The narrow camera FOV means the learner's hands must stay roughly in front of the face. Add a "hands out of camera view" hint during the case.

### 1.8 What the repo already provides, and the gaps

| Area | Repo fact |
| --- | --- |
| Office identity | `NativeEncounterSession.cs` holds `encounterId`, `patientId`, `State`, `Score`, `Role`. It starts with `POST /encounters`, moves to the attending with `POST /encounters/:id/attending`, and adopts `GET /encounters/:id/score`. Its picker is still Priya/Jonah ("Choose Priya or Jonah to begin."). |
| Unity score DTO | `EncounterData.cs` `EncounterScore` deserializes only `total, max, grade, spoken, diagnosisGiven, diagnosisExpected, feedback`. It **drops** `procedureId`, `procedureTitle`, `procedureChosenCorrectly`, `diagnosisResult`, `criticalMissed/criticalFound`, `sections` and the differential fields, which the server sends (`services/preop/src/encounter.ts` `Scorecard`). |
| Server scorecard | `Scorecard.procedureId` is "always the surgery this patient needs, whatever the learner proposed". `procedureChosenCorrectly` flags the wrong-plan branch. `carryover()` returns a **prose string** built from score, diagnosis, wrong procedure and critical misses. `POST /coach/sessions {patientId, mode, encounterId}` injects that string into the coach system prompt, only when the encounter is `scored` and the patient matches (`coach-routes.ts`). |
| Challenge and revise | `record_assessment` scores immediately and freezes the encounter (`docs/diagnosis-office.md` UX table). No challenge or revision phase exists yet. |
| OR identity | `NativeCaseSession.cs` hardcodes `PatientId = "patient-demo-multi-source"` and `ProcedureId = "lap_appendectomy"`. Its `CreateRequest` sends only `patientId` and `mode`, **no `encounterId`**, so office carryover never reaches the OR coach today. |
| OR case load | `GET /patients/:id/case` returns `SurgicalCase` with `urgency`, `bodyScale`, `procedureId`, `procedure.steps`, `considerations`, `checklistOptions` and `brief`. `POST /patients/:id/preop-check {selected}` scores a risk checklist. A Unity consumer exists in `Exercises/Preop/ScalpalPreopService.cs` (`PreopChecked`). |
| Mode switch | `NativePresentation.passthrough` (default `true`) maps to `CoachMode` `"mixed_reality"` or `"virtual"`. `NativeCaseSession.TryChangePresentation` is allowed only in `Selecting`/`Recap` and calls `Retry()`, so **a mode switch starts a new attempt**. `RegistrationReady` gates practice in AR on `bodyRegistration.Accepted && CandidateValid`. |
| Registration | `NativeBodyRegistration.cs` is opt-in by the operator (left stick). It requests `horizonos.permission.HEADSET_CAMERA` and `com.oculus.permission.USE_SCENE` and uses MRUK85 `PassthroughCameraAccess` (left camera, 640×480 requested) plus `EnvironmentRaycastManager`. It posts JPEG to `services/registration` `POST /pose` on localhost:8790 (`GET /health` exists, `server.py`). Shoulders 11/12 and hips 23/24 drive the fit, which accepts after 3 stable observations. The fit is invalidated by: no pose or more than one pose, frames older than 750 ms, movement over 35 mm / 5° / 0.05 scale, and others. `Status` strings are developer-facing ("Left stick: enable local body detection"). |
| Builds | The office builds as a **separate APK** (`com.scalpal.encounteroffice`, only `DiagnosisOffice.unity`). `EditorBuildSettings` lists only `NativeSession.unity`. **A same-run handoff is impossible until both scenes ship in one player.** |
| Voice | `QuestJarvisVoice.cs` supports roles `patient`/`attending` via `ConfigureEncounterConversation` and `ConnectEncounter`, and the coach via `Connect(coachSessionId)`. `Disconnect()` invalidates pending tools by generation. One connection exists at a time. |
| Capture | There is no native raw-clip recorder (`docs/system-integration.md`). "Recording raw camera video while the app renders full VR" is listed as unverified. |
| Procedures | Only the appendectomy has an OR scene. Cholecystectomy (4 patients) and sigmoid colectomy (1) have none (`system-integration.md`). |

---

## 2. Recommended handoff sequence

### 2.1 Where and when the mode is chosen, and the default [R]

Two touchpoints:

1. **Operator preflight (before the learner puts the headset on, or from the companion and pause menu).** One "Theatre setup" panel with four checks:
   - Volunteer present and consented.
   - Camera and spatial permissions granted. Request them here, at app launch, never mid-story; see §1.1.
   - `GET :8790/health` OK.
   - Coach `:8787/health` OK.

   The result is a stored `arAvailable` flag plus a preferred mode.
2. **The Theatre card at the handoff (learner-facing).** Two large buttons:
   - **Volunteer patient (AR)**
   - **Virtual OR (VR)**

   The default (pre-highlighted, labelled "Recommended") is **AR if and only if `arAvailable` is true**, otherwise VR. When AR is unavailable it is still shown, disabled, with the reason ("No volunteer checked in", "Body detection offline"). The operator can override from the companion until the fade starts.

Why at the handoff, not on the explore page:
- The decision depends on the physical room, which can change during the 5–15 min office.
- The diagnosis is mode-independent.
- Choosing at the doorway makes the switch an announced, user-initiated event, matching Meta's "user-initiated transition" guidance (§1.1).

Why AR is the default only when green:
- A failed registration is the most likely live-demo failure.
- Defaulting to a path that will fail costs more time than one extra button press.

### 2.2 Beat sheet (full run)

Times are *derived* targets. "L" is the learner in the headset, "O" is the operator, "P" is the reclining participant (volunteer).

| # | Beat | Mode | Time | On screen (exact copy) | Voice (exact copy) | L does | O does | P does |
|---|---|---|---|---|---|---|---|---|
| H0 | Scorecard | VR office | 20–30 s | Scorecard panel: "Clinical reasoning 82/100 · B". Lists found and missed. Footer: "Next: To theatre" | Attending Jarvis speaks `scorecard.spoken` | Reads, then presses **To theatre** | On cue "Attending phase started" (companion), walks P to the table, explains, gets verbal consent | Lies face-up, arms at sides, fitted top, shoulders to hips visible |
| H0b | *Wrong-plan branch only* (`procedureChosenCorrectly=false` or `diagnosisResult` not `correct`) | VR office | 8–10 s | Consequence card, full-width, dim room: "6 hours later". Vitals tick: "HR 118 · T 38.9 °C · BP 98/60". Then: "Perforated appendix. The surgical team takes the case." Then: "You'll scrub in for the laparoscopic appendectomy." Tag: "Case escalated" | Attending: "The patient needs a laparoscopic appendectomy. You'll scrub in with me." | Reads, presses **Continue** (no skip on the first viewing) | – | – |
| H1 | Theatre card (mode choice) | VR office | 5–10 s | Title: "To theatre · Priya Ramaswamy, 40". Subline: "Laparoscopic appendectomy · Urgent". Buttons: **Volunteer patient (AR)** with "Recommended" chip and "Virtual organs on a real person lying down", and **Virtual OR (VR)** with "Virtual patient and operating room". AR-disabled reason line if needed | Attending: "Your choice: operate on our volunteer, or in the virtual theatre." | Points and selects | Can override on the companion | Lies still |
| H2 | Fade out and load | Black | 0.4 s fade + load | World-locked compositor title card: "Pre-op · Priya Ramaswamy, 40 · Laparoscopic appendectomy · Urgent". Phase stepper: Explore ✓ Office ✓ **OR** Replay Recap | Attending connection **closes before the fade** (no voice in transit). A soft "door" sound cue | Waits | – | – |
| **VR path** | | | | | | | | |
| V1 | Fade in to the virtual OR | VR | 0.5 s | Virtual OR, patient on the table facing L at same floor height and yaw. Title card persists 1.5 s, then lazy-fades | – | Looks at the patient | – | (P can get up if O decides) |
| → T1 | | | | | | | | |
| **AR path** | | | | | | | | |
| A1 | Reveal passthrough | Black → passthrough | 1.0 s crossfade | Title card stays world-locked over passthrough as the anchor. Then: "You're in the real room now. Your patient is on the table." Safety line: "Stay where you are. Check the space around you." | Pre-recorded system voice (not an agent): "You're back in the real room." | Looks around | Stands at P's head or feet, out of the torso frame | Still |
| A2 | Positioning guide | AR | 5–20 s | Floor-level ghost silhouette panel beside the table (not on P): "Patient lying face-up · arms at sides · shoulders and hips uncovered or in a fitted top". Learner prompt: "Stand at the patient's right side, about an arm's length away, and look at their chest and belly." | – | Walks to the right side, looks at the torso | Adjusts P's arms. Moves bystanders out of frame | Arms at sides, still |
| A3 | Detection (consent gate) | AR | 2–15 s | If the operator has not already opted in: "Operator: confirm the volunteer agreed. Press left stick." Then a 3-stage progress strip: **① Person found** (4 dots for L/R shoulder, L/R hip; each fills as its landmark passes visibility) → **② Measuring torso surface** → **③ Holding still 1/3 · 2/3 · 3/3**. Live hint line, one at a time (see §4) | – | Holds gaze on the torso | Presses left stick (or companion "Volunteer agreed") | Holds still, breathes normally |
| A4 | Fit preview and confirm | AR | 3–8 s | Four cyan markers at shoulders and hips, then the organ overview fades in at 0.5 s. Quality chip: "Fit: Good" (green check) or "Fit: Check alignment" (amber triangle), never colour-only. Prompt: "Do the organs sit inside the torso? **B: Looks right** · **A: Realign**". Footer: "Generic teaching anatomy, not this person's organs." | – | Leans left and right to check stability; presses B | Confirms P is comfortable | Still |
| → T1 | | | | | | | | |
| **Shared** | | | | | | | | |
| T1 | Coach connects + Time-Out | Both | 20–40 s | Time-Out card (whiteboard on the OR wall in VR; world-locked panel beside the torso in AR): "TIME-OUT". Rows to confirm: **Patient**: Priya Ramaswamy, 40 · **Procedure**: Laparoscopic appendectomy · **Site**: Right lower quadrant · **Anticipated risks**: chips from office findings (e.g. "Penicillin allergy (you asked)", "On apixaban (you missed this)") · **Antibiotic prophylaxis given?** · **Imaging displayed?** Each row: tap or say "Confirmed" | Coach Jarvis, a new connection with the same voice and the coach role: "Scrubbed in with you. Time-out: confirm patient, procedure and site." After the last row: "Good. Let's begin." | Confirms rows by voice or ray | (AR) checks P is ready | Still |
| T2 | Prep / Recording | Both | 2 s | "Recording hands" red dot indicator, persistent until the case ends. Small print: "Camera records your hands for the robot replay. Nothing else is saved." (AR adds: "The volunteer's image is not stored.") | – | – | – | – |
| T3 | OR onboarding (first run only) → step 1 | Both | 20–90 s | Existing OR onboarding, then step 1 on the step list | Coach step line, under 12 words | Practises | – | Still |

Totals (derived):
- **VR path**: about 40–60 s from **To theatre** to step 1, excluding onboarding.
- **AR path**: about 60–110 s.
- **Judge fast path**: about 25 s (VR) or about 40 s (AR); see §2.4.

### 2.3 Voice role continuity [R]

| Role | Lives in | Connects | Disconnects |
| --- | --- | --- | --- |
| Patient | Office interview | `ConnectEncounter` after `POST /encounters` | At `POST /encounters/:id/attending`. Already implemented: pending replies are invalidated. |
| Attending Jarvis | Office presentation, scorecard, consequence, Theatre card | After `/attending` | **When the learner presses a mode button on the Theatre card**, before the fade. `voice.Disconnect()` bumps the generation so stale tool calls die. |
| (no agent) | Fade, passthrough reveal, positioning, registration | – | System lines are pre-recorded clips or on-screen only. Agents must not narrate a state machine they do not control. Keeps latency and failure out of the critical path. |
| Coach Jarvis | OR, from Time-Out | `POST /coach/sessions {patientId, mode, encounterId}`, then `voice.Connect(sid)` once T1 opens (after `RegistrationReady` in AR) | Recap |

Continuity cue:
- Attending and coach use the **same Jarvis voice and caption colour**, so it reads as one person scrubbing in.
- The coach's first line references the office ("Scrubbed in with you"), and the server already supplies the office context via `carryover()`.
- The patient never speaks in the OR. In AR the real volunteer is the "patient". [R] Never put an AI patient voice on a real person.

### 2.4 Judge or demo fast path [R]

The operator sets **Demo mode** and preflight before handing over the headset:
- The volunteer is lying and opted in, and permissions are already granted.
- With Demo mode, the Theatre card auto-selects the default after a 3 s countdown ("Starting in AR in 3…"), which the learner can override.
- The consequence card shows for 6 s.
- The Time-Out collapses to one combined confirm ("Confirm all: patient, procedure, site"), but risk chips still show.
- AR registration is already warm. Registration can run **while the learner is still on the Theatre card** only if the camera is already opted in. Today `TryChangePresentation` restarts acquisition, so keep acquisition alive across the scene change.
- Hard fallback: if AR has not accepted a fit within **20 s** in demo mode, auto-offer VR (§4).

Operator script (verbatim cue card):
> "This is [Name]. They'll lie here face-up while you operate on them in mixed reality. Nothing touches them; the tools are virtual. [To volunteer:] The headset camera finds your shoulders and hips, and images aren't saved. Say 'stop' anytime."

### 2.5 Safety specifics for AR on a real person [R]

- Laparoscopic instruments have long shafts. The virtual instrument length should let the tip reach organ depth while the learner's controller stays at least 15 cm above the torso surface (*derived*).
- Add a proximity guard: if a controller comes within 8 cm of the fitted anterior plane, give a haptic buzz and the caption "Hands up, tools only" (*derived*). This uses the plane `NativeBodyRegistration` already measures.
- The volunteer may stop at any time. "Stop" from the volunteer, or the operator pressing **End AR**, triggers a pause, then the mode-switch offer (§4).

---

## 3. State and data contract

### 3.1 `HandoffTicket` (Unity in-memory, mirrored to SpacetimeDB) [R]

```jsonc
{
  "schema": "scalpal.handoff.v1",
  "runId": "run-…",                    // one learner journey; [R] new
  "sharedSessionId": "…", "attemptId": "…", // QuestSessionBridge; attempt created at T1, not at office
  "patientId": "patient-demo-multi-source", "scenarioId": "…",
  "encounterId": "enc-…",
  "encounterPhase": "scored",
  "procedureId": "lap_appendectomy",    // = scorecard.procedureId (always the correct surgery)
  "procedureTitle": "Laparoscopic appendectomy",
  "learnerProcedure": "…",              // assessment.procedure (raw words)
  "procedureChosenCorrectly": false,
  "diagnosisResult": "incorrect",
  "escalated": true,                    // derived: !procedureChosenCorrectly || diagnosisResult != "correct"
  "revisedAfterPrompt": false,          // reserved; server has no revise phase yet
  "urgency": "urgent",                  // SurgicalCase.urgency (authoritative), learner's urgency kept in assessment
  "scorecard": { "total": 82, "max": 100, "grade": "B", "criticalMissed": ["medications"], "criticalFound": ["…"] },
  "risksFound":  [{ "id": "anticoagulant", "label": "On apixaban", "source": "history:medications" }],
  "risksMissed": [{ "id": "allergy", "label": "Penicillin allergy" }],
  "presentationMode": "mixed_reality" | "virtual",
  "modeChosenBy": "learner" | "operator" | "auto_demo" | "fallback",
  "arPreflight": { "volunteerConsented": true, "cameraGranted": true, "sceneGranted": true, "poseServiceOk": true },
  "contentVersion": "0.1.0",
  "issuedAt": "2026-10-03T…Z"
}
```

Rules:
- **The server is authoritative.** The ticket is only a pointer plus cached display data. The OR re-fetches and verifies:
  - `GET /encounters/:id`: phase must be `scored` and `patientId` must match.
  - `GET /encounters/:id/score`: `procedureId` must match.
  - `GET /patients/:id/case`: `procedureId` and `patientId` must match, and `brief.synthetic` must be true.
- On any mismatch, refuse with a message. Do not guess. This mirrors the existing identity checks in `NativeEncounterSession` and `NativeCaseSession`.

### 3.2 Producers and consumers

| Field | Producer (exists) | Consumer | Gap |
| --- | --- | --- | --- |
| patientId, encounterId | `NativeEncounterSession` (`StartPatient`, `POST /encounters`) | `NativeCaseSession` | OR uses `const PatientId`. **Make it an instance field set from the ticket.** The office picker must accept the explore subject ID. |
| procedureId/title, procedureChosenCorrectly, diagnosisResult, criticalMissed/Found | Server `Scorecard` (`encounter.ts`), via `GET /encounters/:id/score` | Theatre and consequence card; Time-Out | Unity `EncounterScore` **drops these fields**. Add them to `EncounterData.cs`. OR `const ProcedureId` must come from the scorecard, and the OR must reject procedures with no scene (cholecystectomy, colectomy) with an explicit message. |
| urgency, bodyScale, considerations, checklistOptions | `GET /patients/:id/case` (`app.ts`, `SurgicalCase`) | Title card, Time-Out rows, VR mannequin scale | `bodyScale` applies **only in VR**. In AR the fit scale wins and `bodyScale` is ignored. |
| risksFound / risksMissed | Not produced. `carryover()` is prose only. | Time-Out risk chips, pinned step considerations | [R] Add a structured `carryoverItems` to `GET /encounters/:id/score`: the intersection of `case.considerations`/`brief.flags` with `state.historyAsked` and tests. Keep the prose for the prompt. |
| Office context into coach | `POST /coach/sessions {encounterId}` → `carryover()` → `systemPrompt` (`coach-routes.ts`) | `QuestJarvisVoice.ConfigureConversation` | `NativeCaseSession.CreateRequest` has no `encounterId`. **Add it.** |
| presentationMode | Theatre card choice → `NativePresentation.passthrough` | `POST /coach/sessions {mode}`; `RegistrationReady` gate; Spacetime snapshot | `NativePresentation.passthrough` defaults to `true`. It must be **set from the ticket before `Apply()`**. |
| Time-Out answers | Time-Out card → `POST /patients/:id/preop-check {selected}` | `ScalpalPreopService.PreopChecked` → recap | Wiring into `NativeCaseSession` is absent. |
| Shared session/attempt | `QuestSessionBridge`; Spacetime `StartEncounter`/`SetEncounterPhase`/`SetEncounterResult` reducers (generated in `Realtime/Generated`) | Companion observer | Bind encounter and attempt to the same `sharedSessionId`. Add a `handoff` phase row so the operator sees "Theatre: AR, registering 2/3". |
| Scene transport | – | – | Office and OR are **separate APKs**. [R] Put both scenes in one player: an additive `DiagnosisOffice` and `NativeSession` behind a persistent `RunContext` (DontDestroyOnLoad) holding the ticket. Reload the ticket from the server by `encounterId` if the app restarts. |
| Capture segment | – | Gateway → worker | No native recorder. Start at T2, stop at case end. The manifest records `presentationMode`, `attemptId` and the PCA intrinsics and timestamps. |

### 3.3 Capture in both modes [R + sourced]

- Both modes record the **same raw stream**: the PCA camera frames of the learner's hands, never rendered VR frames. This is already a repo rule in `demo-flow.md`.
- In AR the volunteer's torso will be in frame. [R] The manifest flags `subjectInFrame: true`, and the clip consent covers the volunteer. Today the repo says participant images are not stored. **This is a conflict to surface:** robot replay from AR footage will contain the volunteer.
  - Option (a): consent covers the volunteer.
  - Option (b): mask the torso region with the registration silhouette before upload.
  - Stephen decides.
- Camera contention: the registration loop uses the left PCA camera at 640×480. The recorder should share that stream or use the right camera. See §1.7 for API limits.

---

## 4. Failure and recovery

Principle [R]:
- The attempt and the diagnosis score survive every failure.
- A mode fallback **before T1** keeps the same attempt. This needs a change: `TryChangePresentation` currently calls `Retry()`; allow a pre-practice switch without a new attempt, or create the attempt only at T1.
- A switch **after practice starts** creates a new OR attempt but keeps the run and the encounter.

| Failure | Detect | Learner copy | Operator copy (companion) | Recovery |
| --- | --- | --- | --- | --- |
| Pose service down (`:8790/health` fails) | Preflight; re-check at H1 | AR button disabled: "Body detection offline" | "Start `scalpal-registration serve`; check USB reverse 8790" | VR default. AR re-enables when health returns. |
| Permission denied or pending | `NativeBodyRegistration.Status` "permission denied" | "Camera access is off. Using the virtual OR." | "Grant camera and spatial permission in Settings > Apps" | Auto-offer VR. Never re-prompt mid-story. |
| No person detected | `personCount==0` for >5 s | 5 s: "Look at the patient's chest and belly." 10 s: "Can't see a person. More light, or step back so shoulders to hips are in view." | 10 s: "Detection not finding the volunteer" | 20 s (demo) or 30 s (full): card "Switch to the virtual OR? **A: Keep trying** · **B: Virtual OR**" |
| More than one person | `personCount>1` | "Two people in view. Ask others to step out of frame." | "Bystander in frame" | Auto-retry |
| Landmarks partly hidden | A landmark dot stays unfilled | "Can't see the left hip. Move the arm or blanket off it." | – | Auto |
| Surface depth failed | `Status` "measuring torso" for >8 s | "Hold still. Measuring the torso surface." | "Table/torso depth not resolving; check lighting/clutter" | Same timeout ladder as no-person |
| Fit looks wrong (learner presses A: Realign) | Learner | "Realigning. Patient, please hold still." | – | `ResetFit()` then 3 new observations. After 2 realigns, offer VR. |
| Registration lost mid-surgery (move over 35 mm / 5°, occlusion, or stale over 750 ms) | `Accepted` false (existing) | Organs dim to 30% ghost, scoring and the step timer pause, held tools stay. Banner: "Patient moved. Holding until they're still." | "Fit lost: volunteer moved" | Auto-reacquire 3 frames (existing). At 15 s, "A: Realign". At 45 s, "Continue in the virtual OR? (new attempt, office score kept)" |
| Volunteer says stop / operator **End AR** | Operator button | "Pausing. The volunteer is getting up." | – | Pause, then offer the VR continuation |
| Preop/coach `:8787` down | HTTP failure at the T1 coach-session create | "Coach offline. Captions only; your work is still scored." | "Coach service down" | Run steps from the cached case and `Resources/scalpal_bundle.json`. Retry the coach every 10 s and connect voice when back. If the encounter is unrecoverable, the OR loads by `patientId` only and the ticket is marked `carryover: unavailable`. |
| Voice provider down | `QuestJarvisVoice.LastError` | Captions plus pre-recorded step lines | "Voice unavailable" | Non-blocking |
| Unsupported procedure (no OR scene) | `procedureId` not in the OR catalog | Theatre card: "Surgery content for cholecystectomy isn't built yet." Shows scorecard and recap only. | – | [R] Explore marks such patients "Office only" until the scene exists |
| Headset removed or system menu | Focus loss | "Paused. Press to continue." | – | No auto-resume. In AR, re-run fit validation on return. |
| Capture start fails | Recorder error | "Recording hands" shows amber: "Recording unavailable. Replay will be skipped." | Error detail | Continue the case. Recap shows "Replay unavailable: capture failed." |

---

## 5. Acceptance checks

### 5.1 Editor (synthetic, repeatable)

1. **Ticket round-trip.** Run a scored encounter in the fixture service, press To theatre, and open the OR scene. Verify:
   - `NativeCaseSession` uses ticket `patientId`/`procedureId`, not the consts.
   - `POST /coach/sessions` body contains `encounterId`.
   - The returned `systemPrompt` contains the `carryover()` text.
2. **Identity rejection.** Tamper with the ticket (other patient, wrong procedure, encounter not scored) and assert a visible refusal with no OR load.
3. **Wrong-plan branch.** A fixture assessment with the wrong procedure gives `procedureChosenCorrectly=false`. Verify:
   - The consequence card shows.
   - The OR loads `scorecard.procedureId`.
   - The ticket has `escalated=true`.
4. **DTO completeness.** `EncounterScore` parses `procedureId`, `procedureChosenCorrectly`, `diagnosisResult` and `criticalMissed` from a real service reply.
5. **Voice order.** The attending is disconnected before the fade starts. No agent is connected between H2 and T1. The coach connects only after T1 opens. A tool call from the old generation is ignored.
6. **Mode default logic.** For all 16 combinations of the four preflight booleans, AR is the default only when all are true. Otherwise AR is disabled with the correct reason string.
7. **VR path.** Choosing VR sets `passthrough=false` before `Apply()`, the mannequin is visible, `RegistrationReady` is true without a fit, and the coach `mode="virtual"`.
8. **AR path with the synthetic pose fixture.** The progress strip moves ① → ② → ③ 1/3–3/3 on the existing synthetic body checks. B is ignored until `Accepted`.
9. **Fallback keeps the attempt.** Before T1, AR fails, the learner picks VR, and `attemptId` is unchanged (after the `TryChangePresentation` change). After practice starts, a switch creates a new attempt while `encounterId` and the scorecard are unchanged.
10. **Failure injection.** Kill 8790 and 8787 in turn. Each produces the §4 copy within the stated timeouts, and no exception or stuck "busy" state remains.
11. **Fade timing.** Scene activation happens only after alpha reaches 1. Fade-out is 0.3–0.5 s, VR fade-in 0.5 s, AR reveal 1.0 s ±0.1. Use a play-mode test that samples the fade value.
12. **Time-Out.** Risk chips equal the intersection of case considerations and office history. Confirmations post `preop-check` and the result reaches the recap.

### 5.2 Headset (Quest 3S, physical)

1. A single APK contains the office and the OR. One run goes from explore to the office to the Theatre card to both modes without relaunching.
2. Permission prompts appear only at preflight or launch, never during H1–T1.
3. **VR path.** There is no visible hitch or black frame after the fade-in, the patient is in front at the same yaw, and frame time stays within budget across the load.
4. **AR path with a real reclining volunteer in normal room light.** Measure:
   - Time from "To theatre" to the fit being accepted is 45 s or less (full) and 25 s or less (demo, pre-warmed).
   - The title card stays stable through the passthrough reveal.
   - No one reports discomfort at the switch; ask both the learner and a second wearer.
5. **Registration check.** Markers stay on the shoulders and hips while the learner walks around the table. When the volunteer shifts, the overlay pauses and then reacquires.
6. **Fallback drill.** Cover the volunteer with a blanket. The no-person ladder fires, VR is offered, and the case continues in VR with the office score intact.
7. **Voice.** A judge hears patient, then attending, then silence, then coach, never overlapping. The coach's first line references the office finding.
8. **Capture.** In each mode a raw clip records the learner's hands with no virtual content (verify by inspecting frames). The manifest records the mode. In VR, confirm that PCA capture works while passthrough is not displayed; this is currently unverified.
9. **Companion.** The operator sees handoff phase, mode, registration stage and fallback events in real time.
10. **Proximity guard.** With a controller held less than 8 cm from the torso plane, the haptic and caption fire.

---

## 6. Open decisions to surface

1. Robot-replay footage in AR contains the volunteer, which conflicts with "participant images are not stored". Decide between consent-covered capture and torso masking.
2. Whether a mode fallback after practice starts should preserve OR progress. The recommendation is a new attempt; the simpler and safer path is to keep scoring honest.
3. The challenge-and-revise step needs a new server phase. Until it exists, the handoff uses scorecard fields only and `revisedAfterPrompt` stays false.
4. Which OR procedures ship. The explore page should mark non-appendectomy patients "Office only" until their scenes exist.

---

## Sources

- [S1][S2] Meta, Passthrough design: https://developers.meta.com/horizon/design/mr-design-passthrough/
- [S3] Meta, MR Motifs: Passthrough Transitioning: https://developers.meta.com/vr/blog/mixed-reality-motifs-passthrough-transitioning-meta-quest-horizon
- [S4] Meta, Passthrough starter sample (contextual passthrough): https://developers.meta.com/horizon/documentation/unity/unity-sample-starter-passthrough/
- [S5] Meta, Spatial data permission: https://developers.meta.com/horizon/documentation/unity/unity-spatial-data-perm/
- [S6] Unity OpenXR Meta 2.0 scene setup: https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@2.0/manual/get-started/scene-setup.html
- [S7] Meta, MR design guidelines: https://developers.meta.com/horizon/design/mr-design-guideline/
- [S8] Meta, PCA documentation: https://developers.meta.com/horizon/documentation/unity/unity-pca-documentation/
- [S9] Meta, Locomotion user preferences: https://developers.meta.com/horizon/design/locomotion-user-preferences/
- [S10] Meta, Locomotion comfort & usability: https://developers.meta.com/horizon/resources/locomotion-comfort-usability/
- [S11] First Encounters success story: https://developers.meta.com/horizon/discover/success-stories/first-encounters/
- [S12] Meta Quest help, double-tap passthrough: https://www.meta.com/help/quest/617966963105359/
- [S13] Eleven Table Tennis post (weak source): https://www.facebook.com/ElevenVR/posts/have-you-tried-mixed-reality-and-portal-modethanks-xoxobluff-mixedreality-eleven/1552221566469207/
- [S14] WHO Surgical Safety Checklist: https://www.who.int/docs/default-source/patient-safety/9789241598590-eng-checklist.pdf
- [S15] Checklist implementation (Time Out items): https://pmc.ncbi.nlm.nih.gov/articles/PMC3489074/
- [S16] Haynes et al., NEJM 2009: https://www.nejm.org/doi/full/10.1056/NEJMsa0810119
- [S17] INACSL Prebriefing (summary): https://www.healthysimulation.com/healthcare-simulation-standards-of-best-practice/ ; 2025 standard: https://www.nursingsimulation.org/article/S1876-1399(25)00094-5/fulltext
- [S18] INACSL Debriefing 2025: https://www.nursingsimulation.org/article/S1876-1399(25)00092-1/fulltext
- [S19] Apple ARCoachingOverlayView: https://developer.apple.com/documentation/arkit/arcoachingoverlayview
- [S20] MediaPipe Pose Landmarker: https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker
- [S21] Novarad VisAR FDA clearance: https://blog.novarad.net/en/newsfeed/first-fully-immersive-3d-augmented-reality-surgical-navigation-system-achieves-fda-approval-for-precision-spine-surgery
- [S22] Medivis automatic registration: https://www.medivis.com/automatic-registration
- [S23] Meta, PCA overview: https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
- [S24] Meta, PCA public release blog: https://developers.meta.com/horizon/blog/new-era-mixed-reality-passthrough-camera-api-machine-learning-computer-vision/
- [S-repo-1..4] Previously cited in `docs/research/ux-vr-shell.md` and `docs/research/ux-medical-sim.md` (URLs inline above).

Unverified:
- The exact HEADSET_CAMERA prompt wording.
- A `horizonos.permission.USE_SCENE` variant name (the repo uses `com.oculus.permission.USE_SCENE`).
- Whether PCA frames flow while only opaque VR is composited.
- Demeo, Spatial Ops and Gorilla Tag MR transition details.
- Vendor on-screen accuracy indicators (Medivis, Augmedics).
