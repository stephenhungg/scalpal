# scalpal

**AI-guided mixed-reality practice that can become demonstrations for robots.**

Scalpal is an MHacks project for Meta Quest 3S. A learner talks to a voice coach, explores a rotating 3D anatomy model, and practices a supported surgery-themed exercise with virtual tools and generic anatomy aligned to a real reclining participant. A recording of the passthrough camera supplies input for estimating human hand motion, retargeting it to a simulated robot hand, and replaying the demonstration.

The long-term thesis is that human learning can supply useful robot demonstrations. The proposed demo proves a smaller chain: **one guided exercise → feedback → one video-derived movement sequence → one simulated robot replay.** Replay is not autonomous robot learning.

**Latest direction: Solana and monetary completion rewards are removed. Nathan owns the companion website + SpacetimeDB/routing lane; Matthew continues to own Jarvis.** Read [current direction](docs/current-direction.md) and [Nathan's implementation plan](docs/nathan-plan.md) first. They supersede older payout work and the prior blanket wait instruction for this assigned lane.

The backend design uses [SpacetimeDB for shared state plus private file storage for video](docs/data-and-realtime.md), with a separate live-video transport to the companion website. Silas owns motion processing. These are documented implementation tasks, not deployed services or a working stream.

## Start Here

The repository contains implementation components and team handoff documentation, but not a validated end-to-end Scalpal application. Read these in order:

1. [Thesis and scope](docs/thesis.md): What we are building, for whom, and what the demo must establish.
2. [End-to-end experience](docs/demo-flow.md): Earlier proposed journey; its reward phase has been removed and awaits reconciliation with Nathan's plan.
3. [Decisions and open questions](docs/decisions.md): Current user direction, superseded ideas, and choices still needed.
4. [Architecture](docs/architecture.md): Proposed components, responsibilities, and failure handling.
5. [Hardware baseline](docs/hardware-baseline.md): What was actually demonstrated on the physical Quest 3S.
6. [Implementation plan](docs/implementation-plan.md): Bounded workstreams, ordering, and evidence required to proceed.
7. [Integration contracts](docs/integration-contracts.md): Proposed shared identifiers and records so parallel components can connect.
8. [Research](docs/research/README.md) and [sponsor alignment](docs/sponsors.md): Primary sources and conditional event integrations.
9. [Folder structure and team split](docs/team-plan.md): Stephen, Matthew, Silas, and Nathan's lanes, parallel checkpoints, and copyable agent briefs.
10. [Data storage and realtime](docs/data-and-realtime.md): Latest proposed storage split, Nathan's backend lane, and the distinction between session sync and video processing.
11. [Nathan's implementation plan](docs/nathan-plan.md): Assigned companion website, SpacetimeDB, live-video, storage, and integration milestones.

## Status

As of October 3, 2026, native camera acquisition, Unity sample deployment, desktop mirroring, and an immersive bottle-detection overlay have been demonstrated on the headset. The repository now includes anatomy assets, Unity anatomy controls and exercise code, and the preop service. Scalpal torso registration and the integrated experience are not headset-validated; video recording/reconstruction and robot retargeting remain separate workstreams.

## Anatomy and Blender Files

**[Open the anatomy asset guide](assets/anatomy/README.md)** for teammate setup, the editable Blender workspace, original full-resolution models, Unity exports, and preview. On branch `codex/anatomy-atlas`, run `git lfs install` and `git lfs pull` to download the original models and `.blend` workspace. Runtime FBXs and Unity `.meta` files are also committed. The Blender workspace has 4,020 prepared parts in four scenes; the 11 original FBX/GLB files are included separately. Geometry checks passed; actual Unity import and Quest performance remain unverified.

![Prepared full-body anatomy, rendered in Blender](assets/anatomy/preview.png)

The conversational selection experience was accepted during product exploration. Robotics and recording were subsequent additions; the onchain reward idea was subsequently removed. Older docs preserve the earlier proposal with explicit scope notices. No expanded specification or application acceptance checks have been completed.

## Collaboration

Follow [AGENTS.md](AGENTS.md) and the [team plan](docs/team-plan.md). Major component folders contain onboarding READMEs; inspect their current code as some older documents still describe scaffolds. Agree on component contracts before overlapping implementation. Work on one measurable technical question at a time. Update the docs when a decision or measured result changes; distinguish a proposal from a verified result.

Keep credentials, participant footage, device identifiers, and raw datasets out of Git. This is an illustrative simulator, not clinical guidance or evidence of surgical competence. Actual recording, processing, sharing, and training rights must be established for each data source and use.
