# Thesis and Product Scope

> Scope update: Solana, wallets, payouts, and monetary completion rewards have been removed. The remaining design and assignments await Nathan's plan. This earlier proposal contains superseded reward/challenge references and work orders; read [current direction](current-direction.md) first and do not implement those removed features.

Updated October 3, 2026. This is the current product direction for an MHacks demo, not a validated medical product or robotics dataset business.

## Grand Thesis

**Human learning can become the engine for robot learning.**

Practicing a skill and collecting robot demonstrations are often separate activities. Scalpal proposes connecting them: AI helps a person practice a supported task in mixed reality, and a permitted recording of that session supplies motion examples that can be mapped onto a simulated robot.

The two sides must both remain present:

1. **Human learning:** Accessible conversation, spatial anatomy visualization, structured practice, and feedback.
2. **Robot demonstrations:** Reconstruction and retargeting of a task-relevant movement sequence, with quality checks before treating it as a useful contribution.

Surgery is the initial educational setting. A real reclining person gives the scene a physical reference, while anatomy and tools are virtual. The anatomy represents a generic teaching model. Surface video does not reveal that participant's actual internal organs.

## Immediate Demo Claim

One learner completes one guided, surgery-themed exercise. A short passthrough-camera recording is processed into estimated hand motion. A simulated robot hand replays a supported part of that movement. A verifier accepts a qualifying contribution, and a sponsor-funded Solana Devnet challenge issues a reward receipt.

This demonstrates a connected interaction and contribution flow. It does not establish that the robot learned an autonomous policy, that beginners produced expert surgical data, or that users acquired clinical competence.

The exact exercise and robot model are still open. A shared manipulation subtask, such as moving a virtual instrument between defined targets while avoiding a restricted region, is a candidate. It is not a selected full surgical procedure. Anatomy questions alone do not provide the action examples required for robot manipulation; free-hand motion alone does not specify forces, contacts, or robot-feasible control.

## Long-Term Flywheel

A sponsor requests a defined task and funds a challenge. AI guidance helps people practice it. A verifier admits useful, permitted demonstrations. Robot policies are trained and tested against new task starting conditions. Measured weaknesses motivate the next challenge.

Each arrow is a separate hypothesis:

- Learners benefit from the guidance and assessment.
- Recorded motion can be reconstructed accurately enough for the task.
- Retargeted motion is feasible for the selected robot and environment.
- Accepted demonstrations improve a learned policy on held-out trials.
- Sponsors want those demonstrations and will pay for them.

No sponsor has committed to buying or funding a lab dataset. No robot-learning improvement has been measured. Improvements to a robot policy do not automatically improve the educational coach.

## Who Uses It

The first proposed learner is a medical student or curious novice practicing anatomy and supported simulated decisions or movements. The first physical participant is a consenting teammate reclining on a table. The operator helps fit and recover the demo. Judges or spectators follow the headset mirror on the laptop.

The proposed sponsor role is a lab or organization funding a specific contribution challenge. In the demo, label an invented sponsor as fictional or a demo sponsor. A hackathon prize sponsor is not automatically a customer or a funder of our challenges.

## The Experience

Conversation replaces a swipe-based surgery catalog. During selection, Jarvis can present and manipulate a rotating 3D anatomy model so the learner understands the target before committing. Practice begins only after the chosen model is fitted to the participant and alignment is accepted. Structured feedback, robot replay, and reward status finish the session.

The headset is the immersive client; the laptop supports observers, wallet pairing, and the proposed first offline video-processing experiment. The learner should see meaningful activity and honest progress, rather than internal model names or controller jargon.

## Scope Boundaries

The initial slice supports one exercise and one simulated robot hand. It uses pretrained models and custom registration/retargeting logic. Model fine-tuning requires a measured recognition problem; training from scratch is not the starting point. Live robot teleoperation is optional later work, since the latest input direction is recording a video and processing it for replay.

The demo uses virtual tools, not an actual procedure on the participant. It has no promised tissue deformation, force feedback, patient-specific anatomical accuracy, real robot operation, or autonomous surgical execution. Rewards use Devnet test tokens, not income. No custom token is required.

## What Success Looks Like

The full journey completes repeatedly on the physical Quest. The preview corresponds to the chosen exercise; the anatomy stays acceptably fitted when tracking is valid; uncertainty blocks assessment; the coach responds to actual exercise state; the replay shows video-derived movement on the selected robot hand; and an accepted contribution receives one traceable reward without duplicate payment.

Evidence must identify failures as well as successes. The immediate robotics evidence is motion reconstruction and replay. The later robot-learning evidence would be autonomous task success on unseen initial conditions, evaluated separately from replay.

See [demo flow](demo-flow.md), [decisions](decisions.md), and [hardware baseline](hardware-baseline.md) before interpreting any part as implemented.
