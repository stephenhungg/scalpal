# Proposed Integration Contracts

> Scope update: Solana, wallets, payouts, and monetary completion rewards have been removed. The remaining design and assignments await Nathan's plan. This earlier proposal contains superseded reward/challenge references and work orders; read [current direction](current-direction.md) first and do not implement those removed features.

These are conceptual agreements for team coordination, not implemented APIs or final schemas. Choose transport, storage, serialization, robot environment, and versioned field definitions before coding across components. Use the smallest fields necessary for the first exercise.

## Shared Identity

Use stable `sessionId`, `attemptId`, `exerciseId`, `exerciseVersion`, `challengeId`, `challengeVersion`, and `artifactId` values. An attempt belongs to one selected exercise version. Contribution acceptance and reward claims must identify the exact attempt/artifact and challenge version.

A retry creates a new attempt. Wallet control proves control of an address, not uniqueness of a person. The first slice may trust a paired client and operator; document that trust rather than claiming fraud-proof performance verification.

## Exercise Manifest

Define the anatomy asset/version, stable named structure IDs, supported learner actions, step transitions, hints, completion rules, invalid states, and feedback text. Define which movement segment is eligible for robot reconstruction and what the shared manipulation task is, if any.

Keep educational completion separate from contribution usefulness. The voice provider receives permitted explanations and relevant scene state; it does not invent a new surgery, scoring rule, or asset ID during a session.

## Frame / Recording Record

Identify the source camera and raw-versus-composited capture route, resolution/crop, intrinsics, encoding, frame IDs or sequence, timestamps and their clock domain, and any associated capture-time camera pose. Store synchronization uncertainty explicitly. A JPEG preview's nearby timestamp is not necessarily its capture timestamp.

A clip has explicit start/end and a link to its attempt. Associate relevant virtual tool/object states and events through a defined timeline because raw camera footage excludes those objects. Do not promise simultaneous left/right capture, exact frame-to-pose alignment, or metric depth before verifying them.

Raw capture and derived movement remain restricted artifacts with controlled access, not public repo files or onchain payloads. Their intended use and retention must match the selected platform/data arrangement.

## Hand Estimate

Each estimate identifies its input frame/time, model/version, handedness, landmark coordinates, coordinate frame/units, and available detection/presence confidence. Estimated hand-relative 3D coordinates are not automatically a calibrated wrist pose in Unity world space.

Handle unknown handedness, missing fingers, skipped frames, out-of-order results, and low-confidence segments. Preserve a validity mask or equivalent so filtering does not silently turn missing information into precise movement. Establish a limited interpolation rule only after measuring the footage.

## Robot Motion / Replay

Specify the robot model/version, named joint ordering, units, limits, neutral pose, world/root frame, retargeting configuration/version, desired joint targets, applied/clamped commands, and timestamps. If attached to an arm, identify the wrist-to-arm controller and inverse-kinematics behavior separately from finger retargeting.

Choose whether the first output is kinematic visualization or physics simulation and label it accordingly. Replay has a known start state and timeline. Object manipulation additionally needs a reproducible scene reset, controller dynamics, and contact validation. A robot rendered in Unity is not automatically compatible with a robotics simulator's model or joint conventions.

## Voice Action Boundary

Candidate tools include `previewExercise(id)`, `rotatePreview(enabled)`, `isolateStructure(id)`, `restoreContext()`, `confirmExercise(id)`, `highlightStructure(id)`, `requestHint()`, and `pausePractice()`.

Validate IDs, parameters, current mode, session, and step/version. Serialize conflicting actions. Return applied, loading, unavailable, or rejected outcomes. Jarvis announces action completion after an actual application response. Voice tools have no arbitrary code execution or direct treasury authority.

## Attempt Result and Contribution Result

The learning result includes authored step outcomes, mistakes, hints, assistance, and completion status. The contribution result includes clip availability, reconstruction/retargeting status, task checks, accepted/rejected/pending state, verifier identity/version, and a reason.

Use distinguishable statuses for `PracticeCompleted`, `MotionProcessing`, `ReplayReady`, `ContributionAccepted`, and payment state. Those labels are examples, not a finalized enum. A finished practice attempt can have a rejected contribution, and an accepted contribution can have a pending payment.

## Challenge and Reward

A challenge fixes its sponsor/demo label, exercise/task version, permitted contribution requirements, acceptance rule, budget, reward amount, and authorized verifier. Proposed richer onchain state includes funded challenge, participation, accepted receipt, and payout; exact program scope remains open.

The reward service binds a recipient before eligibility, reserves one unique claim under the selected rule, stores transaction identity before submission, and reconciles uncertain outcomes before any resend/replacement. A client retry must not create a second payout. Keep payment status independent of the saved results.

Public chain data should contain only the minimal challenge/receipt/claim information. An artifact digest identifies a versioned artifact; it does not prove medical correctness, reconstructed motion accuracy, or data rights. Keys and provider secrets stay outside the headset bundle and repository.

## Interface Changes

Declare schema/configuration versions and document breaking changes. Every owner should be able to inspect one synthetic session record and understand which coordinate frames, time domains, task versions, and claim identity were used. Use synthetic fixtures for integration; do not commit participant footage as test data.
