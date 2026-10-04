# scalpal

**AI-guided VR surgical practice that can become demonstrations for robots.**

> **Scope notice:** the current product flow is the [latest experience flow](docs/current-direction.md#latest-experience-flow): launch → explore patients → diagnosis office → full-VR surgery → required robot replay. Mixed reality with a real participant, conversational selection and the rotating preview are off the main path. Where an older document disagrees, current direction wins.

Scalpal is an MHacks project for Meta Quest 3S. The learner launches the app, browses a large explore page of synthetic FinchNode patients, picks a case, diagnoses the patient in a full-VR doctor's office through a voice back-and-forth, then performs the surgery that patient needs in a full-VR operating room. A recording of the passthrough camera during the surgery supplies input for estimating human hand motion, retargeting it to a simulated robot hand, and replaying the demonstration. See the [latest experience flow](docs/current-direction.md#latest-experience-flow).

The long-term thesis is that human learning can supply useful robot demonstrations. The proposed demo proves a smaller chain: **one guided exercise → feedback → one video-derived movement sequence → one simulated robot replay.** Replay is not autonomous robot learning.

**Latest direction: Solana and monetary completion rewards are removed. Nathan owns the companion website + SpacetimeDB/routing lane; Matthew continues to own Jarvis.** Read [current direction](docs/current-direction.md) and the [system integration map](docs/system-integration.md) first. They supersede older payout work and the prior blanket wait instruction for this assigned lane.

The backend uses [SpacetimeDB for shared state plus private file storage for video](docs/data-and-realtime.md). Main now includes the native session, full anatomy sources, Jarvis service, companion/realtime/gateway and video-motion processor. Physical end-to-end verification is pending. Live headset video and motion processing still need capture/worker adapters. The integration map records exact source snapshots and routing gaps.

## Start Here

Main contains documentation, the complete source atlas and team services, a native Quest tool workbench and an assembled single-case full-VR session. Open `apps/quest` in Unity 6000.0.66f2; see [project setup](apps/quest/README.md). Start with the [system integration map](docs/system-integration.md): actual contents, routes, shared scene bindings and shipping checks. Then read these:

1. [Thesis and scope](docs/thesis.md): What we are building, for whom, and what the demo must establish.
2. [End-to-end experience](docs/demo-flow.md): Current target journey (explore, diagnosis office, full-VR surgery), with live observing and the required video-derived replay.
3. [Decisions and open questions](docs/decisions.md): Current user direction, superseded ideas, and choices still needed.
4. [Architecture](docs/architecture.md): Proposed components, responsibilities, and failure handling.
5. [Hardware baseline](docs/hardware-baseline.md): What was actually demonstrated on the physical Quest 3S.
6. [Implementation plan](docs/implementation-plan.md): Bounded workstreams, ordering, and evidence required to proceed.
7. [Integration contracts](docs/integration-contracts.md): Proposed shared identifiers and records so parallel components can connect.
8. [Research](docs/research/README.md) and [sponsor alignment](docs/sponsors.md): Primary sources and conditional event integrations.
9. [Folder structure and team split](docs/team-plan.md): Stephen, Matthew, Silas, and Nathan's lanes, parallel checkpoints, and copyable agent briefs.
10. [Data storage and realtime](docs/data-and-realtime.md): Latest proposed storage split, Nathan's backend lane, and the distinction between session sync and video processing.
11. [Nathan's implementation plan](docs/nathan-plan.md): Assigned companion website, SpacetimeDB, live-video, storage, and integration milestones.

The new [native appendectomy session](docs/native-session.md) assembles the working Quest rig, selected anatomy, authored case, coach and real session adapter. Start with `Assets/Scalpal/Quest/Scenes/NativeSession.unity`; use the component workbench for isolated tool checks. Verification and unconnected media/MR/robot interfaces are listed in that document.

## Status

As of October 3, 2026, native camera acquisition, Unity sample deployment, desktop mirroring, and an immersive bottle-detection overlay have been demonstrated on the headset. The native workbench combines the shared tools, tracked head/controllers and operating-room/patient art. USB installation, XR tracking and held-tool telemetry were exercised on Quest; the user reported tool-motion lag, then confirmed the corrected build keeps up with hand movement. See [native workbench evidence](docs/native-workbench.md). Teammate branches contain anatomy, case/coach, realtime/companion and motion implementations. Native torso registration, capture and their complete integration have not been demonstrated; see the audited map.

The conversational selection experience was accepted during earlier product exploration and has since been replaced by the explore → diagnosis office → full-VR operating room flow. Robotics and recording were subsequent additions; the onchain reward idea was subsequently removed. The architecture, demo flow and integration contracts now reflect the current scope; older research and planning notes preserve historical proposals with scope notices. No expanded specification or application acceptance checks have been completed.

## Collaboration

Follow [AGENTS.md](AGENTS.md) and the [team plan](docs/team-plan.md). The Quest folder includes the standalone native workbench; the preop, realtime, gateway and companion implementations are now included. Reconcile the actual feature-branch contracts and select one shared authored exercise before overlapping integration. Work on one measurable technical question at a time. Update the integration map when routes, contracts or verification change; distinguish a proposal from a verified result.

Keep credentials, participant footage, device identifiers, and raw datasets out of Git. This is an illustrative simulator, not clinical guidance or evidence of surgical competence. Actual recording, processing, sharing, and training rights must be established for each data source and use.

## Instrument Prototype

The [Unity project](apps/quest/README.md) includes committed scenes, assets, package configuration and project settings. The [instrument kit](assets/instruments/README.md) contains fourteen catalog tools plus a scalpel: Blender sources, previews, optimized runtime FBX models, Unity pickup/action prefabs and an authored practice sandbox. The native session connects the selected case/coach and anatomy bindings. Read [runtime integration](docs/instrument-runtime.md) before adding action or scoring adapters.

Our earlier physical camera experiment is preserved as [pinned upstream source plus local changes](experiments/quest-camera-baseline/README.md). Unity caches, captured footage and build outputs are excluded; all authored instrument environment source is committed. Run the [repeatable session gate](scripts/README.md) after changes to the affected boundaries.

## Two Demo Modes (Superseded)

The main flow now runs in full VR only; the real-person mixed-reality overlay stays in the repository off the main path. The earlier direction added a full-VR virtual patient/operating room alongside that overlay, both sharing organs, tools and the coach/exercise core. [Mode engineering](docs/environment-modes.md) separates surface perception from body registration. [Reusable environment art](assets/environments/README.md) supplies the room/patient. Selected anatomy bindings are assembled in the native session; MR registration and mode switching remain pending. The [comprehensive anatomy research](docs/research/comprehensive-anatomy.md) describes how a reusable library can support additional cases; [TAPNet research](docs/research/tapnet.md) evaluates video point tracking.
