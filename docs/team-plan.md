# Repository Structure and Four-Person Work Split

Updated October 3, 2026. This is a proposed ownership plan and a folder scaffold, not a running application. Assignments assume Stephen is the hardware/integration owner because the Quest is connected to his machine. Teammate specialties are not known; swap whole ownership lanes if another arrangement fits better.

## First Shared Outcome

Build one connected session: choose one supported exercise with Jarvis and a 3D preview, practice with generic anatomy fitted to a real participant, process a short passthrough recording into a simulated articulated robot-hand replay, then issue one accepted-contribution Devnet reward.

Keep two paths distinct while integrating them: the educational result and the quality of the motion contribution. A completed lesson can produce an unusable clip. Robot replay is the immediate goal; policy training is later.

## Folder Layout

The major component folders below exist with onboarding READMEs. Indented implementation folders are the intended layout once their owner starts the component; no Unity, web, Python, or Solana project has been initialized yet.

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
│   │   │   ├── Voice/            # Validated Jarvis client actions
│   │   │   ├── Exercises/        # One authored exercise and scene bindings
│   │   │   ├── Prefabs/          # Independently owned reusable scene objects
│   │   │   └── Scenes/           # Bootstrap plus independently edited scenes
│   │   ├── Packages/             # Unity package manifest and lock
│   │   └── ProjectSettings/      # Pinned editor/platform configuration
│   └── companion/                # Browser wallet pairing, processing/replay, receipts
├── services/
│   ├── api/                      # Sessions, artifacts, verifier, voice auth, payouts
│   └── motion/                   # Offline video → hand estimates → robot replay
│       ├── ingest/               # Decode clips and read capture metadata
│       ├── perception/           # Pretrained hand inference and validity handling
│       ├── retarget/             # Robot-specific joint mapping and limits
│       └── replay/               # Simulation adapter and replay artifacts
├── packages/
│   └── contracts/                # Language-neutral versioned schemas and fixtures
├── programs/
│   └── challenges/               # Optional custom Solana challenge program
├── assets/
│   ├── anatomy/                  # Blender source, exports, attribution
│   └── robots/                   # Chosen robot description/assets and licenses
└── scripts/                      # Shared build/run/demo helpers when needed
```

Choose the actual Python module layout, web stack, and onchain framework when initializing those components. The conceptual subfolders are ownership boundaries, not a requirement to make a package for every step. Import runtime anatomy into Unity with stable `.meta` files; keep its source and attribution in `assets/anatomy/`. Do not commit generated caches, binaries, participant footage, or secret-bearing artifacts.

## Ownership

| Person | Owns | Primary paths | First tangible result |
| --- | --- | --- | --- |
| Stephen (`stephenhungg`) | Quest runtime, capture, body registration, final integration | `apps/quest/` runtime/capture/registration, bootstrap scene, Unity settings; `scripts/` | App runs on Quest; explicit short recording with documented metadata; separately, torso debug points and honest valid/uncertain state |
| Matthew (`MatthewKim323`) | Anatomy experience, exercise content, Jarvis behavior/client | Quest experience/voice/exercise code and prefabs; `assets/anatomy/` | One 3D selection preview plus validated voice actions and an authored practice/result flow |
| Silas (`silaswu4`) | Video inference, robot retargeting, simulation/replay | `services/motion/`, `assets/robots/` | A supplied clip produces estimates and a derived replay on one articulated robot hand, with invalid segments reported |
| Nakim (`nakim12`) | Companion app, API, challenge acceptance and Solana | `apps/companion/`, `services/api/`, `programs/challenges/` | A synthetic session pairs with a wallet, processes a contribution status, and gets one confirmed Devnet reward without duplication |

Stephen is the final integrator, not the author of everyone's component. Matthew owns the visual interaction and voice semantics; Nakim provides the service-side voice credential/session endpoint so provider keys are not shipped in the client. Silas defines reconstruction/retargeting quality signals; Nakim implements verifier decisions using the agreed checks. No person can silently redefine another lane's success criteria.

## Shared Files and Unity Rules

Stephen is the merge owner for `packages/contracts/`, the top-level `README.md`, `AGENTS.md`, the main architecture/decisions documents, Unity `ProjectSettings/`, package manifests/locks, and the bootstrap scene. Other owners propose changes in their branch and coordinate contract changes before merging.

For the Unity project:

- Stephen initializes it once, pins the tested editor and required packages, and establishes the entry scene. Do not create competing Unity projects inside `apps/quest/`.
- Stephen owns runtime, capture, registration, and the bootstrap scene. Matthew owns the experience, voice client, exercise assets, and separate preview/practice prefabs or scenes.
- Agree which prefab/scene belongs to which person. Stephen and Matthew should not simultaneously edit the same scene YAML or prefab.
- Commit Unity `.meta` files with their assets and keep GUIDs stable. Introduce assembly boundaries only where needed, and avoid circular dependencies between runtime and experience.
- Stephen exposes current mode, registration quality, recording state, and validated session events through an agreed interface. Matthew consumes those signals instead of reading capture internals.
- Neither the anatomy experience nor the voice bridge writes directly into registration transforms or payment state.

Use lightweight branch names such as `stephen/quest-runtime`, `matthew/experience`, `silas/motion-replay`, and `nakim/challenges`. Open focused pull requests into `main`; keep independent feature work off the shared branch. Changes to contracts and global Unity files need coordination with the merge owner. Do not rely on CODEOWNERS enforcement unless it is separately configured.

## Interfaces to Agree Before Coding Across Lanes

| Interface | Producer → consumer | Minimum agreement |
| --- | --- | --- |
| Exercise and voice catalog | Matthew → Stephen/Nakim | One exercise ID/version, named anatomy IDs, allowed actions, steps/hints, result rubric |
| Capture artifact | Stephen → Silas/Nakim | Clip/attempt ID, raw-versus-composited source, format, frame timing/clock domain, available calibration/pose metadata, virtual scene timeline, access reference |
| Reconstruction/replay result | Silas → Nakim/companion/Quest | Input artifact ID, configuration/model versions, processing status, validity gaps, named robot joints/units, replay artifact and quality outcomes |
| Voice service session | Nakim → Matthew | Scoped client authorization/configuration, connection/error behavior; tool payloads follow the agreed exercise contract |
| Attempt and contribution | Quest/Silas → Nakim | Separate learning result and technical contribution checks, fixed exercise/challenge versions, explicit acceptance/rejection reason |
| Reward receipt | Nakim → companion/Quest | Unique claim identity, recipient, Devnet status/signature/explorer reference; duplicate request returns the existing result |

Start with a small language-neutral contract and synthetic example for each actual boundary. Do not make four independent versions of the same session record. See [integration contracts](integration-contracts.md) for the unresolved field/time/frame details. APIs and transports are not implemented or finalized yet.

## Parallel Checkpoints

### Checkpoint 1: Independently Testable Components

- **Stephen:** Native app/camera path and a permitted short capture. Run torso registration as a separate test; distinguish camera, landmark, depth, and pose failures.
- **Matthew:** Preview and one exercise on a temporary unregistered torso root. Use a local test dispatcher for app actions before cloud voice; label alignment as a placeholder.
- **Silas:** Retarget known synthetic motion to isolate robot-model/simulator errors, then run pretrained inference on the real supplied clip. Synthetic animation is a test, not the final user-derived replay.
- **Nakim:** Pair a synthetic session with a wallet and exercise the accepted/rejected/pending and idempotent Devnet claim paths. Synthetic acceptance is not the final verifier.

Everyone can move without blocking on the headset continuously. Exchange a real captured clip through a restricted artifact route once available; keep it out of Git. Do not distribute platform data beyond the agreed permitted use. If that capture route remains unresolved, Silas can test mapping with synthetic or appropriately licensed input without claiming the complete recording pipeline works.

### Checkpoint 2: Connect the Boundaries

Matthew's preview and practice consume Stephen's runtime/registration state. Stephen's capture artifact goes to Silas. Silas's processing/replay result goes to Nakim's service and companion view. Matthew's Jarvis tools use Nakim's scoped service session, with Unity validating every action. Nakim's verifier consumes actual attempt and contribution results rather than synthetic acceptance.

Use one fixed exercise, artifact, robot, and challenge version for the first integrated session. Decide whether replay appears on the companion laptop, a headset panel, or both after the robot renderer is chosen; do not build two renderers by default.

### Checkpoint 3: Rehearse the Real Journey

Run a full physical-headset session: conversation/preview → confirmed fit → practice/capture → feedback → video-derived replay → accepted contribution → one Devnet receipt. Check voice/camera failure, invalid tracking, failed reconstruction, rejected contribution, and uncertain transaction outcomes without losing the session or duplicating payment.

Complete consecutive sessions before adding more procedures. Keep immediate replay evidence separate from later robot-training claims. See [implementation plan](implementation-plan.md) for verification scope and [hardware baseline](hardware-baseline.md) for measured starting conditions.

## Copyable Agent Work Orders

These are task briefs for teammates to give their own agents. They are not messages sent to teammates or authorization to spend money, deploy publicly, record people, or start every feature at once.

### Stephen: Quest Foundation

Read `AGENTS.md`, the architecture, hardware baseline, team plan, and integration contracts. Work within Quest runtime/capture/registration and the assigned shared bootstrap/settings files. Establish the native app and one explicit short passthrough capture with documented metadata, then test torso registration separately. Coordinate the capture manifest with Silas and session events with Matthew. Preserve honest uncertainty and verify behavior on the physical headset. Do not implement the voice provider, robot pipeline, or payout service.

### Matthew: Experience and Coach

Read the thesis, demo flow, decisions, architecture, and team plan. Work within the assigned Quest experience/voice/exercise folders and anatomy source assets. Propose one supported exercise and addressable anatomy model, implement its rotating selection preview and authored step/result flow, then wire a validated Jarvis action bridge. Start with local dispatch and the runtime contract; coordinate scoped provider authentication with Nakim. Do not change capture/registration internals, bootstrap scenes, global Unity configuration, or reward eligibility without coordination.

### Silas: Video to Robot

Read the thesis, architecture, research, integration contracts, and team plan. Work within `services/motion/` and robot assets. Select a single articulated robot-hand model/simulator compatible with the available machine, verify retargeting using known input, then process a permitted real passthrough clip through pretrained hand inference and constrained replay. Coordinate timing/calibration with Stephen and the result manifest with Nakim. Report missing segments and distinguish reconstructed finger motion from metric wrist/arm motion. Do not train a policy or substitute a canned animation for the real reconstruction.

### Nakim: Services and Rewards

Read the thesis, demo flow, architecture, integration contracts, sponsors, and team plan. Work within the companion app, API service, and optional challenge program. Implement session/wallet pairing, motion job/result integration, scoped voice-provider authorization, separate learning/contribution states, and a verifier-authorized unique Devnet reward. Coordinate quality checks with Silas and the exercise rubric with Matthew. Keep secrets server-side and test duplicate/uncertain submissions. Do not treat synthetic completion as real acceptance or require a custom onchain program before the smallest reward loop works.

## What Still Needs a Team Decision

The exact shared exercise, licensed anatomy model, robot hand/simulator, capture rights and synchronization, provider/backend stack, contribution checks, and custom Solana program scope remain open. Select them at the boundary where evidence is needed; the folder scaffold does not choose them by implication. The submission working deadline is noon Eastern on October 4; establish internal integration milestones against [event constraints](sponsors.md#event-constraints-for-the-team).
