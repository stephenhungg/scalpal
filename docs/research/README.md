# Research and Evidence

> Scope update: Solana, wallets, payouts, and monetary completion rewards are removed. Nathan owns the companion website + SpacetimeDB/routing lane; Matthew continues Scalpal. This earlier proposal contains superseded reward/challenge references. Read [current direction](../current-direction.md) and [Nathan's implementation plan](../nathan-plan.md) first.

Research snapshot: October 3, 2026. This page consolidates the sources relevant to Scalpal's current direction. It distinguishes documented capabilities, prior hardware observations, proposed integrations, and unanswered questions. It is not a clinical operating manual or a claim that the product has been implemented.

Start with the [repository overview](../../README.md) for the thesis and product scope. See [sponsor alignment](../sponsors.md) for event eligibility. Sources below support decisions; they do not automatically validate Scalpal's educational effectiveness or robotics results.

Latest research: [VR shell and navigation UX](ux-vr-shell.md) and [medical simulation, voice and debrief UX](ux-medical-sim.md), summarized in [experience UX](../experience-ux.md). Earlier: [TAPNet family and video tracking](tapnet.md) and [reusable comprehensive anatomy and case coverage](comprehensive-anatomy.md). These are researched directions, not newly implemented tracking or tissue physics.

## Current Research Position

Scalpal connects two goals: AI-guided learning in mixed reality, and useful human demonstrations for robot learning. The first visible robotics result is **video-derived robot-hand replay**, not an autonomous learned policy.

The current input proposal is **Quest passthrough video**, processed on the Mac first. It replaces the earlier proposal to export Meta SDK hand-joint data. It does not eliminate camera-data permissions, calibration, occlusion, or reconstruction challenges.

The physical subject is a reclining real person. The anatomy overlay is a generic teaching model registered to their torso, not an estimate of their actual internal organs. Tools and interactions are simulated. A rotating anatomy preview belongs to the conversational selection phase; the practice overlay follows the participant rather than spinning.

One bounded exercise should connect the educational and robotics components. The current native integration selects an authored appendectomy rehearsal; Silas's source selects a right Shadow hand. Their recording/worker/replay connection remains absent. The broader anatomy/case library is a reusable content direction, not a reason to ship unverified procedures.

## Surgical Education: What We Can Reasonably Teach

Surgical development includes anatomy, procedural reasoning, physical manipulation, teamwork, and supervised clinical judgment. These require different evidence and assessments.

| Educational objective | Observable result in an initial simulator | Remaining limitation |
|---|---|---|
| Anatomy and orientation | Correct identification of named structures and relationships | Generic anatomy is not patient-specific anatomy |
| Procedural decisions | Appropriate sequence, instrument choice, and recognition of uncertainty | Requires an authored, reviewed scenario and rubric |
| Manual coordination | Task completion, motion accuracy, errors, and retries | Virtual motion alone does not establish tissue handling or force control |
| Communication | Responses to scripted prompts or coordination tasks | Team training would add scope |
| Clinical competence | Not established by this demo | Requires broader supervised assessment and clinical evidence |

The [ACS/APDS Surgery Resident Skills Curriculum](https://www.facs.org/for-medical-professionals/education/programs/acsapds-surgery-resident-skills-curriculum/) separates basic skills, advanced procedures, and team scenarios. The [FLS FAQ](https://www.flsprogram.org/about-fls/faq/) describes cognitive learning, physical practice, and proctored assessment; its manual tasks consider accuracy as well as efficiency. The [American Board of Surgery EPA guidance](https://www.absurgery.org/get-certified/epas/) concerns repeated workplace observations and supervision. These support narrow exercises and explicit feedback rather than treating a simulator score as a qualification.

The [SAGES safe-cholecystectomy guideline](https://www.sages.org/publications/guidelines/safe-cholecystectomy-multi-society-practice-guideline/) provides a possible source for an anatomy-and-uncertainty lesson. It contains recommendations with differing evidence certainty. A team-authored scenario should distinguish sourced guidance from an invented exercise rule. Review content and anatomy with a qualified surgical educator before educational deployment, and check reuse permissions rather than copying a curriculum into an AI knowledge base.

### What Existing Simulation Evidence Shows

| Primary study | Relevant evidence | Generalization limit |
|---|---|---|
| [Mixed-reality feasibility trial](https://pmc.ncbi.nlm.nih.gov/articles/PMC9985210/) | Guidance for 36 novice medical students around a synthetic physical model improved performance on a defined task compared with video instruction | The physical model supplied practice; this does not validate our torso-overlay product |
| [Immersive VR trial](https://pubmed.ncbi.nlm.nih.gov/33369660/) | Procedure-specific preparation for 18 senior orthopedic residents improved subsequent cadaveric assessment | Small, experienced cohort and a particular procedure |
| [Haptic bone-drilling trial](https://pmc.ncbi.nlm.nih.gov/articles/PMC9661648/) | Haptic training for 31 junior doctors improved technical outcomes and reduced overpenetration | Task-specific synthetic-bone results; a headset without haptics differs |

Commercial examples include [Osso Academy](https://www.ossovr.com/osso-academy) and [Fundamental XR](https://www.fundamentalxr.com/). Their product pages establish available offerings, not independent evidence that Scalpal works.

## Quest Camera Access and Spatial CV

The [Meta Passthrough Camera API overview](https://developers.meta.com/vr/documentation/unity/unity-pca-overview/) explicitly supports Quest 3 and Quest 3S raw forward-facing camera access. Raw camera access and the passthrough display are separate capabilities. Camera coverage is smaller than the entire composited view.

The earlier browser attempt drew boxes on a flat camera projection but failed to keep them correctly placed in immersive VR. A detector's image coordinates are not automatically world coordinates or either eye's projection. The engineering path is:

```text
Model coordinates
  -> undo image resize/crop/letterbox/rotation
  -> calibrated camera ray for the source frame
  -> valid depth or measured geometric constraint
  -> world position
  -> stereo rendering by the XR runtime
```

Use the [Unity camera guide](https://developers.meta.com/vr/documentation/unity/unity-pca-documentation/) and [CameraToWorld sample baseline](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples/tree/f9c382190907e20232a74c5ecf6513df47bc13b8). Preserve source-frame identity, timestamp, image dimensions, preprocessing transform, and capture pose together. Do not substitute the latest head pose for an older frame or subtract timestamps from incompatible clock domains.

### Previous Hardware Baseline

Physical testing before this repository established a useful foundation, with these limits:

| Observation | Meaning |
|---|---|
| Official Unity samples built, installed, and launched on Quest 3S | Native deployment and camera permission flow were demonstrated |
| Native capture observed 4,455 distinct frames over 75.26 seconds, approximately 59.19 FPS | Camera freshness was measured; this is not rendering or inference FPS |
| Physical bottle label/rectangle appeared in both eyes; wearer reported attachment during slow head turns | Practical object-overlay behavior was demonstrated; quantitative alignment error was not measured |
| YOLO CPU path: median 4.94 completed inferences/second and 208.39 ms sampled processing duration | This specific detector ran locally; processing duration excludes full sensor-to-display latency |
| GPUCompute path: median 2.96 inferences/second and 320.39 ms sampled processing duration | GPU was slower in this tested path; no universal CPU/GPU conclusion follows |

These results concern an object-detection sample. They do **not** prove torso registration, hand reconstruction, precise moving-tool tracking, anatomical accuracy, or robot replay. The sample's 80 COCO classes do not constitute a surgical-instrument detector. The sample approximates a camera-facing rectangle using a center depth hit; it does not recover a full 3D object or guarantee persistent identity. Raw test footage and device identifiers are intentionally excluded from this repository.

Reproducible baseline versions: Unity `6000.0.66f2`, sample commit `f9c382190907e20232a74c5ecf6513df47bc13b8`, MRUK `85.0.0`, Unity Inference Engine `2.2.1`, and OpenXR `1.15.1`. See the pinned [editor version](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples/blob/f9c382190907e20232a74c5ecf6513df47bc13b8/ProjectSettings/ProjectVersion.txt) and [package manifest](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples/blob/f9c382190907e20232a74c5ecf6513df47bc13b8/Packages/manifest.json). These identify the tested sample, not an instruction to install every dependency into this documentation repository.

### Registering Anatomy to a Real Person

[MediaPipe Pose Landmarker](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker?hl=en) is a pretrained candidate for visible shoulder/hip landmarks. Detection and registration are distinct: correct pixels can still produce a drifting overlay if camera geometry or timing is wrong. Its hip-centered coordinates do not automatically become the Quest world coordinate frame.

A candidate path is visible landmarks plus calibrated rays and verified surface-depth observations, forming a torso coordinate frame. [Meta environment raycasting](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-environment-raycast/) can supply surface information; it cannot identify hidden organs. Reject depth hits on the table, clothing occlusion, or the operator's hands when they are inconsistent with the intended surface.

The [BlazePose GHUM model card](https://storage.googleapis.com/mediapipe-assets/Model%20Card%20BlazePose%20GHUM%203D.pdf) describes limitations relevant to reclining participants and metric depth. [Android live-stream support](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker/android) is documented, but Unity texture integration and Quest performance remain untested. Evaluate real reclining footage before committing to the model. Fine-tuning is a later response to measured recognition failures; training from scratch is not the initial plan. A participant-attached marker is a contingency, not the chosen default. A table marker does not track participant movement.

## Passthrough Video to Robot-Hand Replay

The first experiment is:

```text
Short passthrough recording
  -> decoded video frames on Mac
  -> estimated human hand landmarks and confidence
  -> calibrated wrist/finger motion representation
  -> robot-specific constrained retargeting
  -> applied robot joint targets
  -> simulated replay
```

[MediaPipe Hand Landmarker](https://developers.google.com/edge/mediapipe/solutions/vision/hand_landmarker) accepts video input and estimates 21 hand landmarks and handedness. This makes it a concrete pretrained starting point, not a proven reconstruction pipeline for our footage. Headset motion, hand occlusion, motion blur, left/right identity, depth scale, and wrist translation need testing. A monocular RGB clip should not be described as exact metric motion capture.

[dex-retargeting](https://github.com/dexsuite/dex-retargeting) translates human hand motion into robot-hand motion through robot-specific optimization. No new neural network is required to attempt this mapping. Select one articulated hand model, its kinematics, joint limits, and simulator integration. Match joints by name because URDF parsers, simulators, and drivers can use different ordering. A wrist-and-pinch gripper is a smaller alternative, but substituting it for an articulated robot hand changes the demo scope.

If the robot hand is attached to an arm, arm inverse kinematics is an additional stage. [Robosuite controller documentation](https://robosuite.ai/docs/modules/controllers.html) explains how action spaces and controller conventions vary; explicitly define units, frames, limits, and reset state rather than treating human angles as robot commands.

### Recording the Right Artifact

Raw camera recording excludes virtual organs, tools, and UI. A composited headset-view recording and raw Passthrough Camera API frames are different artifacts. Save relevant simulator state alongside the chosen recording, with synchronized timestamps, calibration metadata where available, initial scene state, task/version identifiers, tracking-loss events, and the robot commands actually applied after mapping and limits.

Replaying joint targets from the same initial state demonstrates motion transfer. It does not guarantee successful contacts under different object placements. The first check should be one short, well-lit movement sequence with visible fingers, followed by a replay comparison. No recorder, hand estimator, retargeter, or autonomous policy has been implemented or validated for Scalpal yet.

### Later Robot Learning

VR demonstration collection and imitation learning have precedent in [Zhang et al.](https://arxiv.org/abs/1710.04615) and [AR-assisted imitation learning](https://arxiv.org/abs/2403.13910). Those studies do not establish that free-hand surgical footage is already usable robot supervision.

[robomimic's dataset overview](https://robomimic.github.io/docs/datasets/overview.html) shows why actions, observations, environment metadata, outcomes, and synchronized steps matter. Its [dataset descriptions](https://robomimic.github.io/docs/datasets/robomimic_v0.1.html) distinguish operator proficiency and successful/unsuccessful demonstrations. Beginner motion is not automatically expert behavior.

An eventual small behavior-cloning experiment could use simulator observations and applied robot actions for one shared task. Evaluate autonomous rollouts on held-out sessions and new starting configurations. Keep adjacent frames from the same recording together when splitting data. Report task success and failures; replay, teleoperation, and inverse kinematics are not learned autonomy. Policy training is outside the current replay milestone.

## Data Use and Recording Rights

The current capture proposal uses camera images rather than Meta SDK hand poses. Therefore the SDK hand-pose-only restriction is not the direct rule for the proposed input. However, the [Meta camera overview](https://developers.meta.com/vr/documentation/unity/unity-pca-overview/) identifies camera images as Device User Data under the [Developer Data Use Policy](https://developers.meta.com/vr/policy/data-use/), which restricts permitted purposes and sharing, including selling or licensing covered User Data.

In-app CV, user-view recording, off-device processing, research export, and licensing demonstrations to a robotics lab must not be assumed to have identical permission. Participation consent matters, but does not automatically override platform terms. Verify the exact capture, processing, retention, and sharing route before committing to export or dataset licensing. Keep recording controls and participant consent explicit. Do not put images or detailed movement records onchain.

The [Meta hand-tracking overview](https://developers.meta.com/vr/documentation/unity/unity-handtracking-overview/) remains relevant only if the implementation later returns to SDK hand-joint data; it limits that data to enabling hand tracking within the app. Do not silently switch back to that source while preserving a claim of exportable robotics data.

## Voice Agent and Anatomy Assets

[ElevenLabs client tools](https://elevenlabs.io/docs/eleven-agents/customization/tools/client-tools) provide a documented path for a conversational agent to request app actions. Candidate actions include selecting a supported exercise, rotating/isolation of a named anatomy structure, pausing, requesting a hint, and explaining a result. Unity owns the actual scene; authored rules own progression and scoring; a verifier owns reward authorization.

Use stable exercise and organ identifiers, validate allowed actions against the current step, and return actual tool results to the agent. An agent should not announce a successful visual change before the app confirms it. Unsupported procedures should lead to an explicit supported alternative. Medical content requires review separately from the voice platform integration.

Prepare models in Blender and export portable assets for Unity. The [Unity model-import guide](https://docs.unity3d.com/Manual/HOWTO-ImportObjectsFrom3DApps.html) describes this workflow. Runtime rotation supplies the selection animation; a pre-rendered video is unnecessary. Keep target anatomy separately addressable and simplify the rendering budget.

[Z-Anatomy](https://github.com/Z-Anatomy/Models-of-human-anatomy/blob/master/Readme.md) is a possible asset source. Its README declares CC BY-SA 4.0 and also lists separately credited components with other terms, including noncommercial terms. Check each selected component and retain attribution. Geometry does not supply tissue mechanics, bleeding, dissection behavior, or a clinical rubric.

## Solana: Funded Challenges and Verifiable Rewards

The proposed onchain role is visible challenge funding, accepted-contribution receipts, and a single authorized payout. A sponsor and task rubric must exist even if the MHacks sponsor is a clearly labeled demo entity. No robotics lab has committed funding or validated demand for these recordings.

Relevant building blocks include [program-derived addresses](https://solana.com/docs/core/pda), [cross-program invocation](https://solana.com/docs/core/cpi), and the [Memo program](https://www.solana-program.com/docs/memo). A custom challenge program is a proposal, not deployed code. A hash identifies an artifact; it does not prove clinical correctness, dataset quality, or whether a robot learned.

Use [Devnet](https://solana.com/docs/references/clusters) for the demo; tokens have no real monetary value and the ledger can reset. [MLH Solana guidance](https://www.mlh.com/partners/solana) recommends Devnet. Use a companion Mac/web wallet as the working plan; [Unity Mobile Wallet Adapter instructions](https://github.com/magicblock-labs/Solana.Unity-SDK/blob/main/docs/mwa-quick-start.md) target Android phones/Seeker and do not establish Quest compatibility. See [Solana web integration](https://solana.com/docs/frontend/nextjs-solana).

An offchain verifier accepts a contribution against a versioned rubric, reserves a unique claim, and authorizes payment. Persist transaction signatures and reconcile uncertain results before retrying. Wallet uniqueness does not prove one person per contribution. Keep video and detailed records offchain; a receipt can carry a pseudonymous identifier or digest. No custom token is necessary for test SOL rewards.

## Questions Requiring Evidence

1. Which single exercise offers a worthwhile anatomy/decision lesson and a feasible robot movement replay?
2. Can our actual passthrough footage recover stable hand motion with adequate visibility and scale?
3. Which articulated robot hand and simulator run reliably on the available Mac?
4. Does markerless registration remain stable on a reclining participant during breathing and occlusion?
5. Which recording, off-device processing, and sharing purposes are permitted for the selected capture route?
6. Who reviews the exercise and what objective checks determine learning feedback and contribution acceptance?
7. If later robot training is attempted, does it improve autonomous success on new starts rather than merely replaying the recording?
8. Would a real sponsor fund this particular demonstration task? The hackathon prize list does not answer that economic question.

The source-backed implementation candidates are useful starting points. They remain candidates until exercised end to end on the actual headset, Mac, participant setup, and selected simulator.
