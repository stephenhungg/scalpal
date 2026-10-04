# AR Surgery Audit — October 4, 2026

## Outcome and Source Boundary

AR surgery has a real implementation: Theatre mode selection, passthrough, automatic MediaPipe/depth torso registration, shared five-layer tissue and instruments, coach/session gates, and recap/controller-motion routes. It has **not passed a complete physical reclining-participant operation**. The current fit is generic planar teaching anatomy, not measured internal anatomy or a reconstructed abdominal surface. The highest gaps are terminal death routing, physical registration/performance, consistent scaled measurements, and AR motion coordinates.

Audit source: **`0c4d04b`** on main (backend synchronized through `12c930c`). Installed package at the last independently verified checkpoint: **`0cd8b1d`, `0.6.8-exposure`, Android code 19**, SHA256 `2a133c069d1b945dd4ca7e95c7e4c87399f9f848425a3daaa8dbe5bd35b16ee6`. `0c4d04b` changes the coach opening to the Time-Out-and-begin message; it is not part of that code 19 evidence. Three bounded read-only audits covered perception/coordinates, presentation/handoff, and backend/replay, followed by the root's tests and documentation reconciliation. Source findings below are inspected risks, not reproduced wearer failures unless explicitly stated.

The real volunteer, the selected fictional chart patient, and the generic model are separate things. Virtual cutting changes the model and simulated condition; it never cuts the volunteer or changes their measured vital signs. Existing AR selection silently checks OS grants and has no additional consent checkbox or permission dialog. Optional motion export remains a separate recording choice.

## What Exists

| Layer | Actual implementation | Boundary still unverified or incomplete |
| --- | --- | --- |
| Office → Theatre | Office binds scored encounter/session/attempt into `HandoffTicket`. Theatre offers `mixed_reality` and `virtual`; AR defaults only with existing camera/spatial grants and pose :8790 / coach :8787 health. | Health means HTTP success; it does not prove camera frames, native depth support, pinned model identity or compositor readiness. |
| Presentation | Ticket overrides serialized OR default. AR enables transparent-camera passthrough, hides authored room/mannequin/colliders; VR restores authored transforms. | Real-room tool placement and hand/environment occlusion are unfinished. |
| Camera and pose | MRUK85 PCA caches acquisition-time calibrated lens rays/pose; actual texture dimensions and asynchronous readback are used. Local pinned MediaPipe Pose Landmarker Lite v1 runs CPU IMAGE mode with up to two returned poses; frame/dimensions/model are checked. | No real reclining-person detection accuracy, image orientation or full latency measurement. |
| Metric registration | Acquisition-time native 17×13 raycast grid; eight distributed chest/hip flank probes fit an anterior plane. Shoulders 11/12 and hips 23/24 intersect that plane. MediaPipe z is not used as metric depth. | No person/table segmentation, local abdomen reconstruction, measured ASIS/navel, internal anatomy, or exact image/depth sensor synchronization. |
| Stability and recovery | Three stable observations acquire automatically. Two ordinary misses can hold the unchanged fit, at most 2 s after its last successful image acquisition. Third failure, expiry, ambiguity/malformed data, major motion or XR loss invalidates it. | Physical depth coverage, stable lock, movement detection, recovery and registration accuracy are unmeasured. |
| Shared surgery | Registered wound frame drives layered skin/fat/fascia/muscle/peritoneum volumes, blade cuts, grasp/retraction, tenting, bleeding, persistent wound visuals, checklist and common event/scoring core. | Material parameters are uncalibrated; loaded tissue already exceeds its 30 Hz Editor fixture budget. Scaled measurement units disagree. |
| Gate | Common identity/XR/focus/accepted-fit gate suppresses patient anatomy, scored contacts and simulation. Transient grip/blade histories clear across frame changes. | Backend physiology clock and robot sample inclusion do not share all of these pause gates. |
| Coach and vitals | Shared coach creation, automatic spoken Time-Out, briefing, monitor and condition routes exist. AR requests Presage HR/RR; chart BP and case identity remain. | Fresh baseline/provenance is not confirmed before practice; stale vitals can be labeled measured. Remote monitor death and locally accepted surgery can disagree. |
| Ending and replay | Completion grade, RunEnding and controller-only upload/worker/result/video routes exist. Captions-only canonical handoff can reach recap. | Death does not route to failed recap; AR capture frame is wrong; upload retries and learner-result matching are incomplete. Current robot task is `mark_incision`, not every surgery step. |

Key entry consumers: `EncounterOffice/Runtime/NativeEncounterSession.cs:251–267`, `Handoff/Runtime/HandoffRun.cs:9–21,80–86`, `HandoffFlow.cs:203–279`, `Quest/Runtime/NativePresentation.cs:37–79`. Paths here and below are under `apps/quest/Assets/Scalpal/` unless qualified.

## Concrete Findings and Work Order

### 1. Finish Terminal and Condition Authority — High

`NativePatientMonitor.cs:33–35,116` can display death/flatline, but `NativeCaseSession.cs:581–592` enters recap only on completed exercise. `Recap/Runtime/RecapSessionIntegration.cs:99–106` also requires completion. `Exercises/Coach/CoachRelay.cs:430–436` treats a `patient_died` refusal as delivered, rather than stopping native practice. The target in [operation flow](operation-flow.md) says death ends the case; the native terminal route is missing.

Fresh shared condition is exposed by `services/preop/src/coach.ts:976–986`, while accepted actions/death alerts still depend on local condition at `:434,700–713`. Shared `patientTick` advances while the session remains active (`services/realtime/src/index.ts:2320–2331`), independent of the headset's registration pause. Dropped start/control/end sends are cached before acknowledgment and have no replay (`coach-routes.ts:530–560`, `realtime-bridge.ts:233–238`). Reducer reports remain session-only; an already delayed prior-attempt update can affect a new attempt (`index.ts:2146–2148,2242–2245,2298–2301`; companion `src/data/live.tsx:129`).

**Owners:** Quest session/Surgery/Recap for failed ending; preop/realtime/companion for authority and transport. **Required decision:** explicitly choose whether technical fit/tracking interruptions freeze physiological time; do not silently change that policy. Both displayed outcome and accepted actions must use the same matched condition.

### 2. Establish Physical Fit and Wound Placement — High Acceptance Risk

`NativeBodyRegistration.cs:179–204` requires all 221 raycasts to finish inside an 8 ms acquisition budget, otherwise discards the observation. This allows roughly 36µs per query including managed overhead. Tests provide synthetic grids; no hardware timing proves the complete grid can finish. During a wearer test inspect `SCALPAL_NATIVE_BODY_DEPTH snapshotComplete/hits/sampleMs` before blaming MediaPipe.

The fit uses an approximate anterior flank plane (`BodySurfaceSnapshot.cs:55–98`, `NativeBodyRegistration.cs:359–383`). `Surgery/Runtime/OpenSurgerySession.cs:126–140` derives McBurney from authored ASIS/umbilicus proxies and disables VR skin projection in AR. Authored ASIS y=-15 mm places the wound centre10 mm×fitScale below that plane. Neither that offset nor the plane is a measurement of the local participant's incision site. `BodyRegistrationMath.cs:81` allows up to 90 mm source-correspondence residual; this is a rejection bound, not demonstrated anatomical accuracy.

Replies allow1.25 s but acquisition freshness expires after 2 s. Serialized approximately 1.1 s observations can each be individually admissible yet reset stable acquisition when the preceding capture becomes approximately 2.2 s old (`NativeBodyRegistration.cs:29–31,104,107–108,328–399`). Existing600 ms fixtures do not establish the near-limit pipeline.

**Owner:** Quest registration/tissue. First instrument/test the real pipeline; then add a local wound-surface adjustment and visible alignment check if measured fit is insufficient. A person mask can reject foreground, but does not recover ASIS/navel/internal organs. Retain generic teaching labels. Do not tune ages or depth budgets blindly.

### 3. Make AR Measurements Consistent — Medium

Stroke samples are transformed into the scaled wound's local coordinates (`OpenBodyInteraction.cs:298–312`), then treated as millimetres without restoring scale (`OpenSurgeryMeasurements.cs:14–17,37–53`; `OpenBodyInteraction.cs:380–390`). Membrane lift, muscle opening, base stations and speed use world measurements (`NativeVolumeSimulation.cs:133–135,174–178`; `SurgeryTissueTarget.cs:38–44`). At scale 1.2 a physical 60 mm stroke reports 50 mm while a physical 15 mm tent reports15 mm; the authored 60 mm guide renders 72 mm wide. This is a numerical source-derived inconsistency.

**Owners:** Quest tissue + Surgery measurement contract. Agree whether event fields are physical measurements or explicitly normalized teaching units. Add actual-scene stroke→body-state tests at0.8/1/1.2 scale; current actual-wall fixtures rotate/translate without changing scale. Do not silently shift milestone thresholds.

### 4. Complete AR Layout, Diagnostics and Occlusion — Medium

The visible AR stand copies the authored VR stand pose once (`OpenSurgerySession.cs:240–285`). It does not move with the fitted participant, check real-room clearance, or offer placement. It is not hidden when VR restores its stand. Tools can therefore stay elsewhere in the real room or show a duplicate tray after fallback.

Registration UI uses generic hints and omits `registration.Status` (`HandoffFlow.cs:269–279`), hiding specific unsupported-depth, missing-frame, clock and reference-space failures. Health refresh runs only while the office exists and accepts any HTTP success (`:165–166,425–447`). Keep silent capability checks, but distinguish them from actual OR acquisition readiness.

Environment raycasting is used for fitting; the scene/tissue shaders have no real-environment depth occlusion integration. Real hands/blankets do not occlude virtual organs or stop scene pointer rays. Intentionally visible internal anatomy also needs a selective occlusion design; blindly occluding every organ under the real body would remove the teaching view. The real person's skin is not a scene collider. The VR skin-aperture pointer tests cannot prove AR interaction visibility.

Coarse off-field damage uses fixed authored region bounds in the registered torso frame (`OpenBodyInteraction.cs:763–786`), not measured real head/limb surfaces. These bounds may differ from the volunteer's actual pose; they can report an injury in the approximate model volume. Persistent whole-body skin wound painting is deliberately VR-only (`PatientIncisions.cs:50–55,88–99`), because AR hides the virtual patient skin. The AR incision field has wound visuals, but arbitrary real-person skin wounds/bleeds elsewhere are not implemented. Define and communicate the AR field scope rather than claiming a measured whole-body sandbox.

**Owners:** Quest presentation/Surgery for tray; handoff for diagnostic UI. Add reversible patient-relative tray placement, freeze it during practice, and synchronize tool rest poses. Add selective hand/foreground occlusion only after fit/performance acceptance. The head-relative upper-left checklist is intentional; the monitor follows the patient frame.

### 5. Correct Baseline Freshness and Identity — High Provenance Risk

`NativeCaseSession.cs:455–461` starts practice first, requests `/vitals/baseline` and ignores its outcome. This is an optional baseline request with chart/authored fallback, not verified measured Time-Out. `services/preop/src/coach-routes.ts:579–581` replaces only HR/RR; the result mixes a selected chart with whoever the separate Presage camera sees. There is no binding to the fitted volunteer.

A concrete service freshness defect exists: `services/vitals/src/server.mjs:74–76` prunes recent samples only on another stable update; capture stop retains the array (`:101–112`); `/baseline/capture` reuses it (`:152–156`). `physiology.mjs:95–108` has no timestamp filter. Old samples can remain `source: measured` after live capture ends.

**Owners:** vitals/preop/handoff. Reject expired samples on capture and stop; surface measurement time/source and actual success or fallback. Keep fictional chart identity separate from volunteer measurement identity. Missing Presage must be clearly labeled, not disguised as measured.

### 6. Make AR Replay Coordinates and Delivery Correct — High End-to-End Gap

`Robotics/Editor/ControllerMotionSetup.cs:31–37` binds fixed `PatientRoot`. `Robotics/Runtime/ControllerMotionCapture.cs:254–264` converts controller poses into that frame, although registration moves/scales `patientFrame`. `DemoBody():205` exports no registration transform/fit identity/tool-tip offset. Worker `services/motion/scalpal_motion/mark/demos.py:140,158,178` assumes the authored VR origin/landmarks. AR incision coordinates therefore do not establish a matching robot task. Practice-only step stamping also precedes separate fit/sync gating (`NativeCaseSession.RobotDemo.cs:28–30`; `NativeCaseSession.cs:243–247`), allowing invalid-fit samples into the buffer.

`Robotics/Runtime/ControllerMotionCapture.cs:213,224–226` marks posted/clears frames before HTTP acceptance; upload failure loses retry data. Recap can stop on the first ready global baseline before a slower learner POST (`services/preop/src/robot-routes.ts:211–222`; `RecapController.cs:72,83–89`); no accepted demo ID correlates the result. Shared grade commit does not wait for upload (`NativeCaseSession.cs:554–560`). Captions-only handoff can recap, but no coach ID means robot lookup is unavailable.

**Owners:** robotics/motion/Recap, with registration coordinate contract from Quest. Use an explicit fitted-patient frame plus provenance, ignore invalid-fit intervals, retain data until acknowledgment, and match the learner result by demo/run/attempt. Label baseline replay honestly. No camera footage is needed or intended.

## Proposed Coherent Learner Flow

This is the desired acceptance route, not a claim that every step works physically today.

1. Choose a chart case, interview its fictional patient in VR, review the scorecard, then select **Volunteer patient (AR)** or **Virtual OR (VR)** at Theatre. Show silent capability checks and concrete unavailable reasons; no extra checkbox or OS prompt.
2. AR reveals the real room. Automatically acquire camera/depth/pose; show camera, depth, landmarks and latency/status separately. Require three stable observations. Keep scored anatomy/effects off before acceptance.
3. Show labeled shoulders/hips and the right lower abdominal field. Learner checks both eyes and selects **Looks right**. Keep “generic teaching anatomy” visible. Provide realign/VR fallback if placement is wrong.
4. Place the virtual instrument tray within reach and clear of the participant. Tray and tool rest poses remain stable during practice.
5. Play the surgery briefing and automatic spoken Time-Out. Show selected case identity separately from volunteer fit, and fresh measured HR/RR or explicit chart/demo fallback. Current code's order is **fit → briefing → automatic Time-Out**; prose that reverses those is aspirational.
6. Perform the open-appendectomy steps with one shared tool/tissue/event/checklist/coach core. Virtual injuries/wounds/blood persist until intervention or an explicit retry. Show simulated vital changes separately from measured baseline. Controller lasers remain available.
7. Brief misses may hold the unchanged fit within its budget. Sustained/unsafe loss hides the patient overlay and stops actions. Explain why; require explicit recovery/realignment, or VR with a new attempt after practice has started. Do not carry a stale grasp/cut sweep into recovery.
8. Success **or death** produces exactly one terminal result and freezes further scored actions. Keep partial mistakes/blood loss and explain the outcome. Death routing is still to build.
9. Submit the valid controller demonstration durably, show recap, then the matched robot result. Current robot task is incision marking; a fallback is labeled baseline/unavailable. Choose another patient without carrying old fit/condition/result state.

## Verification and Acceptance Matrix

This audit re-ran the registration service gate: **33 passed**, real blank-image CPU inference and HTTP protocol plus synthetic positive/multiple-person fixtures. It did not send participant pixels. The complete native gate passed after repairing the outdated shared-command transport doubles: **85 production-bridge fixture checks**, **111 registration checks**, both-mode scene/mechanics checks and terminal `SCALPAL_NATIVE_SESSION_VERIFY_OK`. The first gate failed its standalone transport compilation because the doubles lacked the newer generated request types; its Editor validation independently passed. The repaired rerun passed the entire suite. A wrong-expected-step-version mutation fails the new payload check; production source was untouched. These are synthetic/Editor checks, not a physical playthrough. Details are in the [integration checkpoint](system-integration.md). Q7 previously passed 111 synthetic registration assertions, both-mode Editor/Play Mode routes and a unified ARM64 build. The physical evidence is installation/hash/config verification and Launch XR/head/focus startup only.

Read-only readiness snapshot during this audit: host pose :8790 `/health` returned 200, `status=ready`, `schema=scalpal.body_pose.v1`; coach :8787 `/health` returned 200. ADB port 5037 reported **zero authorized devices** at that moment. This does not contradict earlier connection, establish current OS grants/loopbacks, or prove OR readiness. No participant acquisition, microphone/provider call, physical surgery, permission request, motion export, replay or database reset occurred.

| Acceptance check | Required evidence / meaningful failure control | Status |
| --- | --- | --- |
| Gate/no prompts | Existing grants + correct pose/coach model; missing each grant/service keeps AR disabled with reason. No camera starts in office. | Source/synthetic coverage; physical selection pending. |
| Real auto-fit | Completed native grid, useful flank hits, measured image-to-result latency, correct labeled markers in both eyes and right-sided local wound placement. Test rotated/reclining viewpoints, partial framing and different torso proportions. | Physical pending. |
| Registration failure | Cover a shoulder, foreground hands, two people, stale request, move volunteer, recenter, Meta button, focus loss. Verify bounded hold, hidden anatomy, no scored events and stable reacquisition. | Many synthetic controls; physical pending. |
| Scale and coupling | Actual wound/contact scene at scales 0.8/1/1.2; independently measured stroke, split and tent distances. Mutation dropping scale conversion must fail. | Actual-scene scale boundary missing. |
| Layout/appearance | Tray reachable/clear, no duplicate after mode fallback, persistent wounds/blood, both-eye occlusion/lasers/readable monitor/skin-depth view. | Synthetic visual checks only; physical pending. |
| Quest budget | Measure native grid cost, capture latency, tissue step/frame time and stereo render cost with actual surgery active. Record device and workload, not Editor timing. | Physical pending; fresh loaded Editor wall p95 42.164 ms / max 327.394 ms over 2,871 samples; exceeds 30 Hz fixture target (Q7 previously 39.469 ms). |
| Vitals provenance | Expired samples/capture stop/wrong volunteer/service failure must not become fresh measured baseline. Show source/time and chart fallback explicitly. | Missing freshness/binding regressions; physical Presage pending. |
| Pause/terminal authority | Remote/local death disagreement, active bleed during technical pause, lost updates/reconnect, delayed prior-attempt report. Death freezes actions and submits one failed recap; foreign death does nothing. | Missing/partial routes and regressions. |
| Replay coordinates | Same marker-tip path under translated/rotated/scaled AR frame; exclude invalid-fit intervals. | Missing producer/worker contract. |
| Replay delivery | Failed POST retains retry data; slow learner upload with ready baseline still yields matched learner result or honest failure. | Missing route/acceptance regression. |
| Complete physical run | Real Theatre→fit→briefing/Time-Out→all operative steps→success/death→recap→actual learner replay. Distinguish live voice, measured baseline, fallback and simulated robot. | Not performed. |

## Platform Evidence

Meta's [PCA Unity guide](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/) supplies calibrated camera access infrastructure; it does not establish participant fit accuracy. Google's [Pose Landmarker guide](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker) describes estimated33 body landmarks and optional pose segmentation. Our service uses shoulder/hip image landmarks, not an internal-organ detector; it does not request a segmentation mask. Existing uniform-plane placement remains an application inference.

See [body registration](body-registration.md), [mode engineering](environment-modes.md), [operation flow](operation-flow.md), [surgery state](surgery-state.md) and the latest [integration evidence](system-integration.md). Earlier manual-consent/manual-Time-Out/passthrough-replay prose is historical, not missing work to restore.
