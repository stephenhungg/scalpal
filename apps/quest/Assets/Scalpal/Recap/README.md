# Run Ending

The canonical `ScalpalPlayerBuild` and shell build include `Launch → DiagnosisOffice → NativeSession → RunEnding`. Open `Scenes/RunEnding.unity` for component work. The panel reuses the office glass materials, world-text shader and licensed Inter font; Unity's built-in Video module is enabled.

A scene without a bound result shows **No result for this run**. It never silently imports sample scores. Explicit sample-demo selection enables the synthetic preview. A missing recording can still show the separately labeled bundled Shadow-hand replay fallback while retaining the real failed/queued state.

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

The capture owner is `Scalpal.Capture.HandCaptureRecorder` (`Assets/Scalpal/Capture`): `Begin` raises `SegmentStarted`, `EndSurgery` raises `SegmentEnded`, and `AbandonSegment` discards an attempt that ends without completion. While the clip uploads the replay is `queued` with no job and the runtime-only `captureNotice` explains it; `AttachMotionJob` then supplies the job, or `ReportCaptureFailure` fails only the replay with its reason. See [system integration](../../../../../docs/system-integration.md#native-hand-capture-producer). Missing capture is shown honestly; a fallback video is not proof of capture integration.

Replay polling backs off from three to ten seconds and stops at terminal status. Players bind by artifact identity, so new signatures do not restart the video. Error/expiry refresh preserves playback position and play/pause state. Error markers require known timestamps, matching surgery/replay `eventClock` and explicit verified clock alignment. The pausing `active_interaction` clock cannot be matched to a wall-clock clip with an assumed offset. A demo uses at most 20 seconds around the earliest aligned guardrail, otherwise an incision/ligation milestone; without aligned key facts it starts at zero. This is a contiguous highlight window, not a montage. Fixed-wrist kinematic replay does not establish metric wrist recovery or a learned robot policy.

## Reflection and observer

Trigger selects world-space controls; Editor mouse selects them. The headset POSTs the canonical run ID to `/coach/runs/:runId/recap`, fetches the returned `reactionAudioRoute`, and plays the server-rendered reaction through the existing `QuestJarvisVoice.PlayLocalSpeech` API. The server owns the spoken text and ignores client prompt overrides. This is one spoken question with on-screen reflection, not a conversational session or recorded self-assessment. An entirely offline run has no server mapping; that and provider failure retain the question on screen. Legacy standalone practice has `/coach/sessions/:sid/recap` compatibility. No voice implementation file is modified.

The panel advances through reaction, self-assessment and two independent scorecards. Feedback selects no more than two strengths and two improvements from logged facts. The companion `/recap` and `/s/:id/recap` import the same durable RunResult and resolve replay grants under the viewer's identity; automatic result publication remains unconnected. Sample scores require explicit `/recap?demo=sample` selection.

## Verification

Run Unity 6000.0.66f2 batch-mode validations with `-executeMethod`:

- `Scalpal.Recap.Editor.RecapValidation.Run` — scene/contract/state/fact checks.
- `Scalpal.Recap.Editor.RecapReviewValidation.Run` — reviewed serialization, playback identity, sample and demo regressions.
- `Scalpal.Recap.Editor.BodyGradeAdapterValidation.Run` — real packaged open-body engine grade, incomplete finish, exact points/decisions, accepted versus unknown timestamps, clock mismatch, assistance and unmeasured metrics.
- `Scalpal.Recap.Editor.RecapIntegrationValidation.Run` — real preop scorecard → canonical handoff/flag snapshot and two retry attempts → packaged case through `AnatomyExerciseBinding` → completion, retry/reset and build inclusion.

`RecapPlaybackValidation.Run` is a graphics-enabled Play Mode check; invoke without `-quit` because it exits itself. `RecapBuild.CapturePreviews` renders to `SCALPAL_RECAP_PREVIEW` (default `/tmp/scalpal-recap-preview`). These are desktop component checks, not physical headset evidence.

Also run the affected API unit/integration tests, preop route tests, and companion `npm test` plus `npm ci && npm run build`; its standard test script now includes recap contracts using a fixture generated by the actual preop scorer. Report only checks that actually ran. Real capture/manifest/job production, hints/path-economy measurements, pre-office demo-start ownership, pre-exposure/time-lapse consumers, automatic publication and physical full-session/provider verification remain open boundaries.
