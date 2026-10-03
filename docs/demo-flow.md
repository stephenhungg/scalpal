# End-to-End Demo Flow

Updated October 3, 2026. This is the target user journey, not a completed application. The [system integration map](system-integration.md) records actual components, routes and missing adapters. There are no wallets, payouts or onchain stages.

## Before the Learner Arrives

The operator selects one supported authored case/version, confirms service connectivity, pairs the headset and authorized companion to the same session, and loads the required anatomy/tool assets. Use the actual native XR rig; the shipped static preview is not the runtime. Configure recording source and intended storage/processing before capture.

For MR, the real participant reclines on the accepted table. The virtual activity affects only simulated anatomy/tools. For full VR, the authored room and mannequin supply the scene. Both use the same case, coach and action gates; an imported model still needs a validated anatomy fit.

## 1. Start and Choose the Presentation

The learner opens Scalpal and chooses real-person MR or full VR. The operator opens the companion and explicitly shares the composited headset mirror. Live-media connection status and shared-state status are separate. Starting does not automatically record or submit motion.

## 2. Discuss a Learning Goal

The learner asks Jarvis what to practice. Jarvis suggests the supported authored exercise and explains its objective. Unsupported requests get a supported alternative; the agent does not generate arbitrary new surgical rules. The selected case/version is the same in Unity, the coach and shared state.

## 3. Explore the Selection Preview

A rotating 3D anatomy model appears during selection. Supported commands can highlight/isolate a structure and control the preview only after their scene handlers exist. Jarvis reports actual applied or rejected outcomes. The selection model is independent of the practice root; preview visibility does not authorize scoring.

## 4. Confirm and Fit

Confirmation fixes the case/version for this attempt. In MR, the application proposes and validates a fit of generic teaching anatomy to the real reclining participant; the operator checks it. Uncertain landmarks/depth leave the fit invalid. An explicitly labeled assisted fit is a possible fallback, not automatic CV evidence.

In full VR, validate the authored anatomy-to-mannequin fit and XR tracking. Generic anatomy is not patient-specific. Only a valid practice assembly enables tool effects and scoring.

## 5. Practice and Record the Defined Segment

Jarvis guides the current authored step. Tracked virtual tools and supported UI actions pass through one local dispatcher; one deliberate action produces one accepted transition. Wrong-tool/contact feedback follows the authored rubric. The companion shows confirmed state and actual composited video.

Recording starts explicitly for a defined segment. The motion input remains raw passthrough video, accompanied by capture/calibration/clock metadata and a separate virtual scene timeline. Raw images do not contain virtual tools/organs. Full-VR rendered footage cannot be silently substituted as observed raw input.

Invalid body/scene fit hides misleading practice anatomy and blocks effects and scoring, including UI actions. Controller tracking loss releases tools. Voice/network failure preserves local pause and the attempt; recovery resynchronizes confirmed state and retryable outcomes.

## 6. Review the Learning Result

The learner sees authored completed steps, mistakes and hints. A retry starts a new attempt and resets targets/held tools. Learning completion and motion quality remain separate: a completed lesson may have missing or unusable footage. Feedback does not claim clinical competence.

## 7. Process the Actual Clip

The gateway verifies the private input artifact, binds its capture manifest/timeline to the same attempt, and queues a deduplicated job. The real worker claims a run, processes the clip, heartbeats, and uploads compatible private outputs. Current Nathan/Silas protocol and trajectory differences require an adapter before this flow works.

Show queued/running/failed/replay-ready honestly. The synthetic example worker is a routing fixture, not a replacement for user-derived reconstruction. Late, expired or obsolete worker outcomes cannot overwrite a newer run or attempt.

## 8. Watch the Robot Replay

The companion plays the actual derived robot video with a compatible trajectory and validity labels. Silas currently implements right Shadow-hand kinematic MuJoCo replay; Nathan's viewer shows video and joint charts/bars. This does not establish robot contact physics, metric world wrist motion or autonomous learning.

Missing observations remain explicit. A failed reconstruction leaves the learning feedback intact and gives a useful reason. A future interactive 3D robot viewer is separate from current video playback.

## 9. End or Repeat

The recap shows learning feedback and independent motion/replay status. Stop capture, release resources/held targets and follow the selected artifact retention/deletion arrangement. Switching presentation invalidates the old fit and rebinds the shared core before another action.

## Judge-Facing Proof

Rehearse one actual physical-headset journey with a website observer and a real permitted input clip producing the displayed replay. Repeat reset/retry, tracking loss and reconnect. Label asynchronous processing and component-only evidence clearly. The [shipping checks](system-integration.md#shipping-checks) define what must pass; future robot-policy training remains a hypothesis.
