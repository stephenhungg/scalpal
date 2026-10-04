# Thesis and Product Scope

> **Scope notice:** the current product flow is the [latest experience flow](current-direction.md#latest-experience-flow): launch → explore patients → diagnosis office → full-VR surgery → required robot replay. Mixed reality with a real participant, conversational selection and the rotating preview are off the main path. Where this document disagrees, current direction wins.

> Current scope: full VR throughout, one Jarvis coach/attending plus the voice patient, live companion state/media and video-derived robot replay. Solana and monetary rewards are removed. Read the [system integration map](system-integration.md) for actual implementation status.

Updated October 3, 2026. This is the current product direction for an MHacks demo, not a validated medical product or robotics dataset business.

## Grand Thesis

**Human learning can become the engine for robot learning.**

Practicing a skill and collecting robot demonstrations are often separate activities. Scalpal proposes connecting them: AI helps a person practice a supported task in VR, and a permitted recording of that session supplies motion examples that can be mapped onto a simulated robot.

The two sides must both remain present:

1. **Human learning:** Accessible conversation, spatial anatomy visualization, structured practice, and feedback.
2. **Robot demonstrations:** Reconstruction and retargeting of a task-relevant movement sequence, with quality checks before treating it as a useful contribution.

Surgery is the initial educational setting. The patient, anatomy and tools are virtual, in a full-VR doctor's office and operating room. The anatomy represents a generic teaching model. (The earlier real-reclining-person MR setup is off the main path.)

## Immediate Demo Claim

One learner completes one guided, surgery-themed exercise. A short passthrough-camera recording is processed into estimated hand motion. A simulated robot hand replays a supported part of that movement. The companion displays confirmed learning feedback and the independently validated motion/replay status.

This demonstrates a connected interaction and contribution flow. It does not establish that the robot learned an autonomous policy, that beginners produced expert surgical data, or that users acquired clinical competence.

The first shared exercise remains to be agreed. Silas's branch implements right Shadow-hand kinematic replay; its worker and artifact interfaces still need reconciliation with Nathan. A shared manipulation subtask, such as moving a virtual instrument between defined targets while avoiding a restricted region, is a candidate. It is not a selected full surgical procedure. Anatomy questions alone do not provide the action examples required for robot manipulation; free-hand motion alone does not specify forces, contacts, or robot-feasible control.

## Long-Term Flywheel

A lab defines a task it wants demonstrated. AI guidance helps people practice it. A verifier admits useful, permitted demonstrations. Robot policies are trained and tested against new task starting conditions. Measured weaknesses motivate the next task.

Each arrow is a separate hypothesis:

- Learners benefit from the guidance and assessment.
- Recorded motion can be reconstructed accurately enough for the task.
- Retargeted motion is feasible for the selected robot and environment.
- Accepted demonstrations improve a learned policy on held-out trials.
- Sponsors want those demonstrations and will pay for them.

No sponsor has committed to buying or funding a lab dataset. No robot-learning improvement has been measured. Improvements to a robot policy do not automatically improve the educational coach.

## Who Uses It

The first proposed learner is a medical student or curious novice practicing anatomy and supported simulated decisions or movements. No real participant reclines on a table; the patient is virtual. The operator helps start and recover the demo. Judges or spectators follow the headset mirror on the laptop.

The long-term potential customer is a lab seeking useful demonstrations for a defined task. No such customer commitment is established; a hackathon prize sponsor is not automatically a dataset customer.

## The Experience

The learner browses synthetic FinchNode patients, diagnoses the chosen patient in a full-VR doctor's office through a voice conversation with the patient and a presentation to the Jarvis attending, then performs the surgery that diagnosis calls for in a full-VR operating room. Structured feedback and the required robot replay finish the session. See the [demo flow](demo-flow.md).

The headset is the immersive client; the companion website supports observers/results, with a separate composited stream and external video-processing worker. The learner should see meaningful activity and honest progress, rather than internal model names or controller jargon.

## Scope Boundaries

The initial slice supports one exercise and one simulated robot hand. It uses pretrained models and custom registration/retargeting logic. Model fine-tuning requires a measured recognition problem; training from scratch is not the starting point. Live robot teleoperation is optional later work, since the latest input direction is recording a video and processing it for replay.

The demo uses virtual tools on a virtual patient, not an actual procedure on anyone. It has no promised tissue deformation, force feedback, patient-specific anatomical accuracy, real robot operation, or autonomous surgical execution. No wallet, payment or completion-reward flow exists.

## What Success Looks Like

The full journey completes repeatedly on the physical Quest. The diagnosis leads to the matching surgery; invalid tracking blocks assessment; the coach responds to actual exercise state; the replay shows video-derived movement on the selected robot hand; and the companion reflects the same confirmed attempt with separate learning and motion quality outcomes.

Evidence must identify failures as well as successes. The immediate robotics evidence is motion reconstruction and replay. The later robot-learning evidence would be autonomous task success on unseen initial conditions, evaluated separately from replay.

See [demo flow](demo-flow.md), [decisions](decisions.md), and [hardware baseline](hardware-baseline.md) before interpreting any part as implemented.
