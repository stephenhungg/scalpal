# Repository Structure and Four-Person Work Split

> **Scope notice:** the current product flow is the [latest experience flow](current-direction.md#latest-experience-flow): launch → explore patients → diagnosis office → full-VR surgery → required robot replay. Mixed reality with a real participant, conversational selection and the rotating preview are off the main path. Where this document disagrees, current direction wins.

> Implementation update: team feature branches now contain component code. Read the [system integration map](system-integration.md) for audited commits, actual routes, missing adapters and verification. The plan below describes intended responsibilities, not proof of a connected deployment.

> Current scope: Solana and monetary completion rewards are removed. Nathan now owns the companion website + SpacetimeDB/routing lane, and Matthew continues his Scalpal work. Read [current direction](current-direction.md) and [Nathan's implementation plan](nathan-plan.md) before older design notes.

Updated October 3, 2026. This is an ownership plan and folder scaffold, not a running application. Nathan's companion/realtime lane is the latest explicit assignment. Stephen's hardware/integration, Matthew's broader anatomy-experience, and Silas's robotics responsibilities retain the earlier split; Matthew's current Scalpal work is confirmed by the user. Nathan's GitHub identity is not assumed from the earlier `nakim12` entry.

## First Shared Outcome

Build one connected session: choose a FinchNode patient on the explore page, diagnose them in the full-VR office with the voice patient and the Scalpal attending, perform the matching surgery on a virtual patient in the full-VR operating room, receive feedback, and process a short passthrough recording into a simulated articulated robot-hand replay. Nathan's companion website shows the session live and follows shared state and processing results.

Keep two paths distinct while integrating them: the educational result and the quality of the motion contribution. A completed lesson can produce an unusable clip. Robot replay is the immediate goal; policy training is later.

## Folder Layout

The major component folders below exist with onboarding READMEs. Indented implementation folders are the intended layout once their owner starts the component; these scaffolds are not initialized applications or deployed services.

```text
scalpal/
├── README.md
├── AGENTS.md
├── docs/                         # Product, evidence, architecture, team plan
├── apps/
│   ├── quest/                    # Native Unity application
│   │   ├── Assets/Scalpal/
│   │   │   ├── Runtime/          # Startup, session state, final composition
│   │   │   ├── Capture/          # Camera acquisition and recording
│   │   │   ├── Registration/     # Body inference and torso alignment
│   │   │   ├── Experience/       # Anatomy preview, practice, feedback UI
│   │   │   ├── Voice/            # Validated Scalpal client actions
│   │   │   ├── Exercises/        # One authored exercise and scene bindings
│   │   │   ├── Prefabs/          # Independently owned reusable scene objects
│   │   │   └── Scenes/           # Bootstrap plus independently edited scenes
│   │   ├── Packages/             # Unity package manifest and lock
│   │   └── ProjectSettings/      # Pinned editor/platform configuration
│   └── companion/                # Live observer website, shared state, results/replay
├── services/
│   ├── realtime/                 # SpacetimeDB shared state and media setup/status
│   ├── api/                      # Authorized storage, voice auth, worker routing
│   └── motion/                   # Offline video → hand estimates → robot replay
│       ├── ingest/               # Decode clips and read capture metadata
│       ├── perception/           # Pretrained hand inference and validity handling
│       ├── retarget/             # Robot-specific joint mapping and limits
│       └── replay/               # Simulation adapter and replay artifacts
├── packages/
│   └── contracts/                # Language-neutral versioned schemas and fixtures
├── assets/
│   ├── anatomy/                  # Blender source, exports, attribution
│   └── robots/                   # Chosen robot description/assets and licenses
└── scripts/                      # Shared build/run/demo helpers when needed
```

Choose the actual motion module layout, web stack, and SpacetimeDB module language when initializing those components. The conceptual subfolders are ownership boundaries, not a requirement to make a package for every step. Import runtime anatomy into Unity with stable `.meta` files; keep its source and attribution in `assets/anatomy/`. Do not commit generated caches, binaries, participant footage, or secret-bearing artifacts.
## Ownership

| Person | Owns | Primary paths | First tangible result |
| --- | --- | --- | --- |
| Stephen (`stephenhungg`) | Quest runtime, capture, body registration, final integration | `apps/quest/` runtime/capture/registration, bootstrap scene, Unity settings; `scripts/` | App runs on Quest; explicit short recording with documented metadata; separately, torso debug points and honest valid/uncertain state |
| Matthew (`MatthewKim323`) | Anatomy experience, exercise content, Scalpal behavior/client | Quest experience/voice/exercise code and prefabs; `assets/anatomy/` | Authored diagnosis encounters plus validated voice actions and an authored practice/result flow |
| Silas (`silaswu4`) | Video inference, robot retargeting, simulation/replay; main project website and the companion's visual design | `services/motion/`, `assets/robots/`; companion styling (`apps/companion/src/styles.css`, component/page markup) | A supplied clip produces estimates and a derived replay on one articulated robot hand, with invalid segments reported |
| Nathan (GitHub identity not confirmed) | Companion website, SpacetimeDB, storage/worker/provider routing | `apps/companion/`, `services/realtime/`, `services/api/` | Two authorized clients share session state; a separate live feed shows the composited headset view; artifact/job results reach the viewer |

Stephen is the final integrator, not the author of everyone's component. Matthew owns the Scalpal agent, its organ/state context, and voice semantics. Nathan supplies shared-state routing and service-side credential/session support where Matthew needs it; he does not create a second agent. Silas supplies reconstruction/retargeting outputs and quality signals. Nathan exposes their real status/results rather than inventing successful processing. No person can silently redefine another lane's success criteria. Silas restyles the companion to match the main website he is building; Nathan keeps the companion's data, media and storage plumbing (`apps/companion/src/lib/`, `src/data/`, generated bindings), and the two coordinate before editing the same component. Restyling keeps the honesty labels (replay is not a learned policy, synthetic data is marked, video and database status stay separate).

## Shared Files and Unity Rules

Stephen is the merge owner for `packages/contracts/`, the top-level `README.md`, `AGENTS.md`, the main architecture/decisions documents, Unity `ProjectSettings/`, package manifests/locks, and the bootstrap scene. Other owners propose changes in their branch and coordinate contract changes before merging.

For the Unity project:

- Stephen initializes it once, pins the tested editor and required packages, and establishes the entry scene. Do not create competing Unity projects inside `apps/quest/`.
- Stephen owns runtime, capture, registration, and the bootstrap scene. Matthew owns the experience, voice client, exercise assets, and separate preview/practice prefabs or scenes.
- Agree which prefab/scene belongs to which person. Stephen and Matthew should not simultaneously edit the same scene YAML or prefab.
- Commit Unity `.meta` files with their assets and keep GUIDs stable. Introduce assembly boundaries only where needed, and avoid circular dependencies between runtime and experience.
- Stephen exposes current mode, registration quality, recording state, and validated session events through an agreed interface. Matthew consumes those signals instead of reading capture internals.
- Neither the companion nor the voice bridge bypasses the headset's registration validity or authored exercise transitions.

Work directly on `main`: pull before starting work, make small focused commits, push to `main`, and never force-push (see [AGENTS.md](../AGENTS.md)). Preserve teammates' existing branches rather than requiring a rename. Changes to contracts and global Unity files need coordination with the merge owner. Do not rely on CODEOWNERS enforcement unless it is separately configured.

## Interfaces to Agree Before Coding Across Lanes

| Interface | Producer → consumer | Minimum agreement |
| --- | --- | --- |
| Exercise and voice context | Matthew/Quest → Nathan's state backend | Exercise ID/version, named anatomy IDs, allowed actions, current step and relevant guidance/status |
| Capture artifact | Stephen → Silas/Nathan | Raw clip/attempt ID, format, timing/clock domain, calibration/pose metadata, virtual scene timeline, protected access reference |
| Reconstruction/replay result | Silas → Nathan/companion/Quest | Input artifact/run identity, versions, processing status, validity gaps, agreed replay format and quality outcomes |
| Voice service session | Nathan → Matthew, if needed | Scoped authorization/configuration and connection/error behavior; agent implementation remains Matthew's |
| Learning and motion status | Quest/Silas → Nathan | Separate learning result and technical motion quality; no payment or challenge eligibility |
| Live spectator media | Stephen's composited mirror → Nathan's publisher/viewer | Explicit source sharing, session-scoped media setup, independent stream status; frames use separate media transport |

Start with a small language-neutral contract and synthetic example for each actual boundary. Do not make four independent versions of the same session record. See [integration contracts](integration-contracts.md) for the unresolved field/time/frame details. APIs and transports are not implemented or finalized yet.

## Parallel Checkpoints

### Checkpoint 1: Independently Testable Components

- **Stephen:** Native app/camera path and a permitted short capture. Run torso registration as a separate test; distinguish camera, landmark, depth, and pose failures.
- **Matthew:** Preview and one exercise on a temporary unregistered torso root. Use a local test dispatcher for app actions before cloud voice; label alignment as a placeholder.
- **Silas:** Retarget known synthetic motion to isolate robot-model/simulator errors, then run pretrained inference on the real supplied clip. Synthetic animation is a test, not the final user-derived replay.
- **Nathan:** Show synthetic state on two authorized clients, then validate the live mirror-to-browser media path. Use a nonpersonal file and synthetic worker result to test storage/status without claiming the CV pipeline is complete.

Everyone can move without blocking on the headset continuously. Exchange a real captured clip through a restricted artifact route once available; keep it out of Git. Do not distribute platform data beyond the agreed permitted use. If that capture route remains unresolved, Silas can test mapping with synthetic or appropriately licensed input without claiming the complete recording pipeline works.

### Checkpoint 2: Connect the Boundaries

Matthew's preview and practice consume Stephen's runtime/registration state. Stephen's raw capture artifact goes to Silas through Nathan's storage/job route. Silas's real output becomes visible through Nathan's service and companion website. Matthew's existing Scalpal consumes current context and requests supported actions, with Unity validating and acknowledging them. Nathan's live-video source uses the composited mirror independently of raw reconstruction input.

Use one fixed exercise, artifact, processor configuration, and robot version for the first integrated session. Agree a replay format the companion can display; a headset replay panel is additional scope, not a requirement to build two renderers. Nathan's live companion view is now required by his work order, not satisfied by a metadata-only dashboard.

### Checkpoint 3: Rehearse the Real Journey

Run a full physical-headset session: launch → explore → diagnosis office → full-VR surgery/capture → feedback → video-derived replay, while an authorized website viewer watches the session. Check voice/camera failure, invalid tracking, media interruption, failed reconstruction, reconnect, and obsolete worker results without corrupting the current attempt.

Complete consecutive sessions before adding more procedures. Keep immediate replay evidence separate from later robot-training claims. See [implementation plan](implementation-plan.md) for verification scope and [hardware baseline](hardware-baseline.md) for measured starting conditions.

## Copyable Agent Work Orders

These are task briefs for teammates to give their own agents. They are not messages sent to teammates or authorization to spend money, deploy publicly, record people, or start every feature at once.

### Stephen: Quest Foundation

Read `AGENTS.md`, current direction, the hardware baseline, team plan, and integration contracts. Work within Quest runtime/capture/registration and the assigned bootstrap/settings files. Establish the native app and a short raw passthrough capture with documented metadata, and the launch → explore → office → OR scene flow. Torso registration is off the main path. Coordinate the capture manifest with Silas, session events and the composited mirror source with Nathan, and experience state with Matthew. Preserve honest uncertainty and verify on the physical headset. Do not take over Scalpal or the robot pipeline.

### Matthew: Experience and Coach

Continue Matthew's existing Scalpal work with context about organs and exercise state. Read current direction and coordinate supported action requests/results with Stephen and Nathan. The current split covers authored diagnosis encounters (patient and attending) and the authored practice/result flow; the rotating selection preview is superseded; agree those assets/tasks with the team rather than duplicating an existing implementation. Nathan supplies backend/companion integration and scoped provider support where needed. Do not change capture/registration internals, bootstrap scenes, or global Unity configuration without coordination.

### Silas: Video to Robot

Read current direction, architecture/research, and the team plan. Work within `services/motion/` and robot assets. Select one articulated hand/simulator, verify retargeting using known input, then process a permitted real passthrough clip through pretrained inference and constrained replay. Coordinate timing/calibration with Stephen and job/run identity, output format, and result manifest with Nathan. Report missing segments and distinguish finger motion from metric wrist/arm motion. Do not train a policy or substitute a canned animation for the real reconstruction.

### Nathan: Companion and SpacetimeDB

Follow [Nathan's implementation plan](nathan-plan.md). Work within the companion, realtime module, and thin API gateway. Build session-scoped shared state, actual live composited headset viewing, private artifact routing, and integration of Matthew's agent and Silas's processor. Keep rendering/inference with their owners and keep video outside database row replication. No wallets, rewards, or second voice agent. Supply setup instructions, tested client bindings, and honest connection/processing/media statuses.

## What Still Needs a Team Decision

The exact shared exercise, licensed anatomy model, robot hand/simulator, capture rights/synchronization, provider and media/storage deployment, and replay format still need agreement. SpacetimeDB and Nathan's companion/backend ownership are now part of his assigned lane; payment infrastructure is removed. The submission working deadline is noon Eastern on October 4; coordinate internal integration milestones against [event constraints](sponsors.md#event-constraints-for-the-team).
