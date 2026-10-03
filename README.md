# scalpal

**AI-guided mixed-reality practice that can become demonstrations for robots.**

Scalpal is an MHacks project for Meta Quest 3S. A learner talks to a voice coach, explores a rotating 3D anatomy model, and practices a supported surgery-themed exercise with virtual tools and generic anatomy aligned to a real reclining participant. A recording of the passthrough camera supplies input for estimating human hand motion, retargeting it to a simulated robot hand, and replaying the demonstration.

The long-term thesis is that human learning can supply useful robot demonstrations. The proposed demo proves a smaller chain: **one guided exercise → feedback → one video-derived movement sequence → one simulated robot replay.** Replay is not autonomous robot learning.

**Latest direction: Solana and monetary completion rewards are removed. Nathan owns the companion website + SpacetimeDB/routing lane; Matthew continues to own Jarvis.** Read [current direction](docs/current-direction.md) and [Nathan's implementation plan](docs/nathan-plan.md) first. They supersede older payout work and the prior blanket wait instruction for this assigned lane.

The backend design uses [SpacetimeDB for shared state plus private file storage for video](docs/data-and-realtime.md), with a separate live-video transport to the companion website. Silas owns motion processing. These are documented implementation tasks, not deployed services or a working stream.

## Start Here

This repository contains documentation and an openable Unity instrument workbench, not a complete Scalpal application. Open `apps/quest` in Unity 6000.0.66f2; see [project setup](apps/quest/README.md). It is a portable handoff for teammates and their agents. Read these in order:

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

As of October 3, 2026, native camera acquisition, Unity sample deployment, desktop mirroring, and an immersive bottle-detection overlay have been demonstrated on the headset. Scalpal torso registration, anatomy assets, voice tools, video recording/reconstruction, and robot retargeting are not implemented here.

The conversational selection experience was accepted during product exploration. Robotics and recording were subsequent additions; the onchain reward idea was subsequently removed. Older docs preserve the earlier proposal with explicit scope notices. No expanded specification or application acceptance checks have been completed.

## Collaboration

Follow [AGENTS.md](AGENTS.md) and the [team plan](docs/team-plan.md). The Quest folder is an initialized standalone Unity editor project; the other major component folders still contain onboarding scaffolds. Agree on one supported exercise, one robot-hand model, and component contracts before overlapping implementation. Work on one measurable technical question at a time. Update the docs when a decision or measured result changes; distinguish a proposal from a verified result.

Keep credentials, participant footage, device identifiers, and raw datasets out of Git. This is an illustrative simulator, not clinical guidance or evidence of surgical competence. Actual recording, processing, sharing, and training rights must be established for each data source and use.

## Instrument Prototype

The [Unity project](apps/quest/README.md) includes committed scenes, assets, package configuration and project settings. The [instrument kit](assets/instruments/README.md) contains fourteen catalog tools plus a scalpel: Blender sources, previews, optimized runtime FBX models, Unity pickup/action prefabs and an authored practice sandbox. It is a standalone component, not the complete application described above. Matthew's current feature branch also contains case/coach code and a Unity relay; those are not merged into main by this asset work. Read [runtime integration](docs/instrument-runtime.md) before connecting actions to scoring or Jarvis.

Our earlier physical camera experiment is preserved as [pinned upstream source plus local changes](experiments/quest-camera-baseline/README.md). Unity caches, captured footage and build outputs are excluded; all authored instrument environment source is committed.

## Two Demo Modes

The latest direction adds a full-VR virtual patient/operating room alongside the real-person mixed-reality overlay. Both should share organs, tools and the coach/exercise core. [Mode engineering](docs/environment-modes.md) separates surface perception from body registration. [Reusable environment art](assets/environments/README.md) now includes a static CC0 operating-room/patient Unity preview and editable Blender scene; native XR, anatomy bindings and mode switching remain pending.
