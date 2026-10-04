# Implementation Plan and Team Handoff

> **Scope notice:** the current product flow is the [latest experience flow](current-direction.md#latest-experience-flow): launch → explore patients → diagnosis office → full-VR surgery → required robot replay. Mixed reality with a real participant, conversational selection and the rotating preview are off the main path. Where this document disagrees, current direction wins.

> Scope update: Solana, wallets, payouts, and monetary completion rewards are removed. Nathan owns the companion website + SpacetimeDB/routing lane; Matthew continues Scalpal. This earlier proposal contains superseded reward/challenge references. Read [current direction](current-direction.md) and [Nathan's implementation plan](nathan-plan.md) first.

This is a proposed sequence for a documentation-and-folder-scaffold repo. The user requested repository context, folder structure, and a team work split; application implementation has not begun. See the [team plan](team-plan.md) for proposed ownership, component boundaries, and parallel checkpoints.

## First Align the Team

Read the [thesis](thesis.md), [demo flow](demo-flow.md), [decisions](decisions.md), and [architecture](architecture.md). Choose one exercise, one robot hand, and an intended recording/processing use. Identify component owners and resolve shared contracts before changing multiple components.

The project had two distinct spatial problems: attaching anatomy to a reclining participant and reconstructing hand movement from a head-mounted camera. Solving one does not solve the other. The first is now off the main path because surgery runs in full VR on a virtual patient. The native bottle sample proves a useful runtime baseline, not either application problem.

## Smallest Next Robotics Experiment

For the latest direction, the bounded robotics test is a short real passthrough clip of finger opening/closing and a simple wrist orientation change, followed by pretrained landmark inference on the Mac and replay on one simulated articulated robot hand.

Establish the capture route and intended data use first. Use a supported camera source, preserve frame timing and calibration where available, and distinguish raw camera footage from a composited headset recording. Initially isolate finger articulation before claiming metric arm trajectories or instrument manipulation.

Success means the replay is derived from the recorded movement, changes the expected robot joints, respects limits, preserves timing sufficiently for comparison, and explicitly handles missing/low-confidence observations. Inspect mismatches and stop on unsupported segments. A smooth animation alone is not evidence of correct reconstruction.

Record the tested model/version, clip conditions, visibility failures, processing time, robot model/version, coordinate conventions, and resulting replay limitations. Do not substitute a pretrained canned animation for a failed estimate.

## Workstreams and Outputs

| Workstream | Bounded output | Dependencies |
| --- | --- | --- |
| Native runtime | App shell, raw camera acquisition, permissions, mirroring, lifecycle recovery | Measured official sample baseline; application setup still needed |
| Body registration (off the main path) | Visible-landmark debug points, valid torso frame, quality state and recovery | Camera calibration/pose association; actual reclining-person trials |
| Content and assets | One licensed model, named structures, original exercise steps/hints/rubric, an authored diagnosis encounter per demo patient | Chosen exercise and review owner |
| Video-to-robot | Clip decoding, landmark estimation, retargeting, replay and confidence report | Permitted recording, selected robot, consistent frames/units |
| Voice | Diagnosis-office patient and attending voices plus validated scene actions and feedback | Encounter catalog, state machine, imported model |

Independent owners can investigate bounded prerequisites concurrently, but freeze shared identifiers and state semantics before integration. Avoid multiple agents rewriting the main scene or exercise manifest at once.

## Suggested Build Order

1. **Prove the risky input path.** Run the video-to-hand-to-robot test. Diagnose recognition, calibration, depth, and timing separately.
2. **Choose the shared task.** Use those results to select a movement the robot can reproduce and an educational objective the learner can understand. Author the acceptance rules and clarify what the replay can demonstrate.
3. **Build the local experience.** Add the launch screen, explore page, diagnosis office and full-VR operating room with one exercise and a structured result. Test scene transitions without depending on voice.
4. **Connect the voices.** Implement the patient and attending in the office and the Scalpal coach in the OR: supported tool calls, grounded explanations, rejection handling, interruption, and local pause/fallback. Voice responses acknowledge actual app results.
5. **Connect recording and replay.** Tie the recording segment and virtual scene state to the attempt. Show processing, replay-ready, and reconstruction-failed states separately from learning completion. Every run ends with the robot replay.
6. **Rehearse the complete session.** Run consecutive physical-headset sessions with the laptop mirror, actual recorded motion and resulting replay. Fix integration failures before adding another exercise.

## Verification Required

If MR registration is revisited (it is off the main path), test normal visibility, head movement, deliberate occlusion, and participant repositioning. Measure valid tracking time, actual overlay offset against external reference points, and pause/recovery behavior. Proposed numerical targets in earlier design notes are provisional; establish values after the task and viewpoint are fixed. Do not label a generic anatomy fit patient-specific.

For reconstruction, use held-out short clips and compare source movement with robot joints. Check confidence gaps, frame ordering, handedness, scale/frame conventions, joint limits, and the effect of headset movement. If manipulation is part of the claim, measure object/task outcome with a reset scene rather than only playback of joint targets.

For voice, test supported encounter questions, clarification, unsupported requests, cancellation, stale tool calls, and connection failure.

The final demo needs repeatable end-to-end completion on physical hardware and reported rendering/processing responsiveness. The earlier approximately 59 FPS camera input and 5 inferences/sec object model are separate metrics and do not establish application display performance.

## Later Work

Only after recording and retargeting are useful should the team attempt policy training. That work needs synchronized task observations and robot-applied actions, appropriate data rights, dataset review, and evaluation on unseen starts. A small imitation-learning experiment is a candidate; a large model trained from first principles is not required for motion mapping.

Real robots, force-sensitive surgical tasks, expert demonstrations, medical validation, funded lab contracts, and real-token incentives require separate scope and evidence. Do not make them prerequisites for a working hackathon slice.

## Handoff Format

Each component owner reports the interface/version used, files changed, checks actually run, measured outcomes, known limitations, and the next required input. Update [decisions](decisions.md) when a choice changes and [hardware baseline](hardware-baseline.md) only for actual measured results. Keep sensitive raw artifacts outside Git.
