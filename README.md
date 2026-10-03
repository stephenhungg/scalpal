# scalpal

**AI-guided mixed-reality practice that can become demonstrations for robots.**

Scalpal is an MHacks project for Meta Quest 3S. A learner talks to a voice coach, explores a rotating 3D anatomy model, and practices a supported surgery-themed exercise with virtual tools and generic anatomy aligned to a real reclining participant. A recording of the passthrough camera supplies input for estimating human hand motion, retargeting it to a simulated robot hand, and replaying the demonstration. Sponsored challenges and Solana Devnet rewards connect the learning experience to contribution incentives.

The long-term thesis is that human learning can supply useful robot demonstrations. The immediate demo proves a smaller chain: **one guided exercise → one video-derived movement sequence → one simulated robot replay → one accepted contribution and Devnet reward.** Replay is not autonomous robot learning.

## Start Here

This repository currently contains documentation, not an implemented Scalpal application. It is a portable handoff for teammates and their agents. Read these in order:

1. [Thesis and scope](docs/thesis.md): What we are building, for whom, and what the demo must establish.
2. [End-to-end experience](docs/demo-flow.md): The proposed journey from opening the app through replay and reward.
3. [Decisions and open questions](docs/decisions.md): Current user direction, superseded ideas, and choices still needed.
4. [Architecture](docs/architecture.md): Proposed components, responsibilities, and failure handling.
5. [Hardware baseline](docs/hardware-baseline.md): What was actually demonstrated on the physical Quest 3S.
6. [Implementation plan](docs/implementation-plan.md): Bounded workstreams, ordering, and evidence required to proceed.
7. [Integration contracts](docs/integration-contracts.md): Proposed shared identifiers and records so parallel components can connect.
8. [Research](docs/research/README.md) and [sponsor alignment](docs/sponsors.md): Primary sources and conditional event integrations.

## Status

As of October 3, 2026, native camera acquisition, Unity sample deployment, desktop mirroring, and an immersive bottle-detection overlay have been demonstrated on the headset. Scalpal torso registration, anatomy assets, voice tools, video recording/reconstruction, robot retargeting, challenge verification, and Solana payout integration are not implemented here.

The conversational selection experience was accepted during product exploration. Robotics, recording, and richer onchain challenges were subsequent additions. These docs consolidate that direction; they are not a claim that an expanded specification has received engineering approval or that the application passes its acceptance checks.

## Collaboration

Follow [AGENTS.md](AGENTS.md). Agree on one supported exercise, one robot-hand model, and component contracts before overlapping implementation. Work on one measurable technical question at a time. Update the docs when a decision or measured result changes; distinguish a proposal from a verified result.

Keep credentials, participant footage, device identifiers, and raw datasets out of Git. This is an illustrative simulator, not clinical guidance or evidence of surgical competence. Actual recording, processing, sharing, and training rights must be established for each data source and use.
