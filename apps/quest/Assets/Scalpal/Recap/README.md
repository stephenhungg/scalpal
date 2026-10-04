# Run Ending

The canonical `ScalpalPlayerBuild` and shell build include `Launch → DiagnosisOffice → NativeSession → RunEnding`. Open `Scenes/RunEnding.unity` for component work. The panel reuses the office glass materials, world-text shader and licensed Inter font; Unity's built-in Video module is enabled.

The screen shows only the essentials: **Diagnosis** (score, or Skipped / Unavailable), **Surgery** (score or Unavailable, "· assisted" when the run was demo-assisted), the **robot replay** video with one result line ("Robot · Mark McBurney incision · Success · 6 mm"), one provenance line only when the robot learned from synthetic demos ("Learned from 40 demos (synthetic)"), and the two buttons *Choose another patient* / *Retry surgery*. While the robot trains: one line, "Robot learning…"; on a timeout (150 s), an `unavailable` reply, a malformed reply or a player error: "Robot replay unavailable". A scene without a bound result shows **No result for this run** and never imports sample scores.

The robot result is `GET {coach}/coach/sessions/:coachSessionId/robot-result`, polled every 3 s. The video streams from the coach origin plus the coach-relative `videoUrl` (`/robot/replays/<file>.mp4` only). The demo it learned from is the learner's own controller motion for `mark_incision` (`Robotics/Runtime/ControllerMotionCapture.cs`, posted by `NativeCaseSession.RobotDemo.cs`; see `docs/robot-hand.md`).

## Native integration

`NativeCaseSession.BeginReviewedPractice` calls `RecapSessionIntegration.Begin` with the actual `HandoffRun.Current` ticket, verified case and confirmed shared attempt. Diagnosis comes from that ticket and its original `sourceOffice`; `runId` preserves the canonical handoff UUID. Caption-only offline practice remains supported without inventing a coach session. Initial coach creation and ordered recovery send the canonical run ID for server-side session mapping. Accepted case completion closes `RecapRunContext.EndSurgery` once, waits up to five seconds for shared-result acknowledgement, then opens this scene. `BodyGradeAdapter` consumes the actual finalized open-body `exercise.Grade`; its exact points are labeled illustrative and uncalibrated. Explicit early finish retains incomplete goals. Native summaries use source milestone counts and completion state. Legacy runs without a grader stay `surgery.available=false`; legacy completion counters are not a grade.

Retry reloads the OR, resetting its tool/tissue/body/registration/coach instances. It requires a server-confirmed different attempt in the same session, retains the original scored encounter provenance and diagnosis, and clears old grade/job state. Choose another patient uses the shell transition to open explore. Component-only native build entry points remain isolated; use the shell build for the full scene chain.

`RecapRunContext` retains configured gateway/voice endpoints and the nonserialized authorized client identity token. Native `session-config.json` supports `gatewayBaseUrl` alongside `coachBaseUrl`; authenticated replay requires HTTPS away from loopback. No worker credentials are exposed. The independent ending scene has its own XR rig; disable it if a future shell retains a shared rig.

## Contract and capture boundary

See the [integration contract](../../../../../docs/system-integration.md#run-ending-contract--recap-lane-october-3) for complete fields and source snapshots.

- `diagnosisAvailable:false` means unavailable even when `JsonUtility` serializes a zeroed nested scorecard. Available diagnosis uses the real preop `Scorecard`, including `carryoverItems`; no invented risk arrays are required.
- `surgery` copies the actual producer’s earned/available points (currently out of 80), rubric, completion reason, missing milestones, blood loss and aggregate decision correctness. The adapter does not grade actions. Hints and path economy remain explicitly unavailable; recorded decision choices carry `correctnessAvailable:false`.
- Timed facts include `timeKnown`; the open-body clock is `active_interaction`, derived from accepted action milliseconds and paused when interaction is not ready. Live callbacks preserve accepted times. Late snapshot-only milestones/guardrails have unknown times.
- `ExportResultJson()` includes durable session/attempt/job/run/artifact identities. Signed video URLs and expiry are runtime-only and never exported; the companion resolves fresh authorized grants on view.
- Source is learner/rehearsal/sample/unknown. The gateway requires a matching `scalpal.capture-provenance.v1` manifest registered as an available `capture_manifest` in the motion job's `extraArtifactIds`. Unknown provenance cannot become a ready learner replay.
- `HandoffRun.Begin` freezes the operator demo flag at the **scored-office-to-theatre handoff**; OR `Begin` copies it into detailed result flags and `EndSurgery` records `demoAssisted`. Legacy standalone runs freeze at OR start. There is no recap toggle that retroactively changes a grade. Native premark assistance reads the frozen flag, while manual `assisted_premark` records also label the grade assisted. Earlier patient-selection/office-chip flag ownership and the `preExpose`/`timeLapseNonKeySteps` consumers remain unimplemented.

The passthrough hand-clip recorder (`Scalpal.Capture.HandCaptureRecorder`) is retired: nothing subscribes to `SegmentStarted`/`SegmentEnded`, so `EndSurgery` records the RunResult `replay` as failed ("Capture adapter is not connected"). The `replay`/`AttachMotionJob` contract fields remain for the companion and gateway; the headset recap no longer shows them.

## Verification

Run Unity 6000.0.66f2 batch-mode validations with `-executeMethod`:

- `Scalpal.Recap.Editor.RecapValidation.Run` — contract/state/fact checks, then `RecapScreenValidation`: every visible text on the real scene in each robot state (pending, ready synthetic/headset/mixed, unavailable, malformed, timeout, player error, no result) must be exactly the allowed list.
- `Scalpal.Recap.Editor.RecapReviewValidation.Run` — reviewed serialization and demo-flag regressions.
- `Scalpal.Recap.Editor.BodyGradeAdapterValidation.Run` — real packaged open-body engine grade, incomplete finish, exact points/decisions, accepted versus unknown timestamps, clock mismatch, assistance and unmeasured metrics.
- `Scalpal.Recap.Editor.RecapIntegrationValidation.Run` — real preop scorecard → canonical handoff/flag snapshot and two retry attempts → packaged case through `AnatomyExerciseBinding` → completion, retry/reset and build inclusion.

`RecapBuild.CapturePreviews` renders to `SCALPAL_RECAP_PREVIEW` (default `/tmp/scalpal-recap-preview`). These are desktop component checks, not physical headset evidence.

Also run the affected API unit/integration tests, preop route tests, and companion `npm test` plus `npm ci && npm run build`; its standard test script now includes recap contracts using a fixture generated by the actual preop scorer. Report only checks that actually ran. Real capture/manifest/job production, hints/path-economy measurements, pre-office demo-start ownership, pre-exposure/time-lapse consumers, automatic publication and physical full-session/provider verification remain open boundaries.
