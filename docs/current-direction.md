# Current Direction

Updated October 3, 2026 after the user removed the monetary/onchain component. This page takes precedence over older architecture, flow, research, and team-plan documents where they disagree.

## Scope Change

Solana, wallets, onchain challenge programs, Devnet payouts, and money for completing simulated surgeries are removed from the project. They are not required integrations, acceptance checks, or assigned work. Earlier references to these features are historical context, not instructions to implement them.

## Remaining Thesis

AI guides people through mixed-reality learning and practice. Recorded passthrough video can supply estimated hand movements that are mapped onto a simulated robot hand for replay. The longer-term hypothesis is that useful demonstrations could support robot learning. Replay does not itself demonstrate a learned autonomous policy.

The working experience is: open the Quest app → discuss an exercise with Jarvis → explore a rotating 3D anatomy preview → confirm the exercise and fit generic anatomy to a real reclining participant → practice with simulated tools → receive feedback → process/replay the recorded motion → recap or retry. The exact shared exercise and component adapters still need reconciliation; see the [current demo flow](demo-flow.md) and [system integration map](system-integration.md).

## Two Presentation Modes

The user now explicitly wants both **mixed reality with a real reclining participant and virtual organs** and **full VR with a virtual patient and operating room**. Both use one coach, tool system and authored exercise flow. Surface detection, body landmarks and validated anatomy registration are separate jobs; a table/person box does not determine organ placement. See [mode engineering](environment-modes.md) and [environment-source research](research/surgery-environments.md).

A static CC0 room/patient preview is prepared in `apps/quest/Assets/Scalpal/Environment/`. The native workbench reuses it with OpenXR and the tool station; the [native session](native-session.md) adds nine selected anatomy meshes and an authored full-VR appendectomy rehearsal. A two-mode switch and real-person registration remain absent. The real-person direction remains the MR mode.

## Work Status

The user previously asked to wait for Nathan's plan, then requested storage/realtime planning and explicitly assigned the updated implementation lane: **Nathan owns the companion website plus SpacetimeDB and routing; Matthew continues to own Jarvis**. See [Nathan's implementation plan](nathan-plan.md) and [data/storage design](data-and-realtime.md). The earlier blanket wait does not block this assigned lane. The active team implementations are consolidated on main; the [integration map](system-integration.md) identifies their source snapshots and missing connections. The work-order documents describe their assigned target, not a complete deployment. The exact shared exercise, robot model, and other unresolved decisions remain open. No GitHub identity mapping for Nathan is assumed.

The companion website is a live observer view, not another voice agent. It combines shared session/coach/processing state with a separately transported composited headset video feed. The existing Mac mirror is a proposed video source to validate, not the final companion website or a verified network stream.

The repo contains an openable Unity project at `apps/quest`, committed scenes/assets/packages/settings and the [instrument kit](../assets/instruments/README.md). The [hardware test](native-workbench.md) records deployment/tracking evidence and the user-confirmed tool-motion correction. Main now assembles Matthew's case/coach/anatomy, Nathan's shared-state adapter and a native transport for Matthew's existing Jarvis agent. Local service exchanges and synthetic Unity checks are verified; the full physical session and native voice remain unverified. Earlier camera/bottle results concern a separate experiment. Silas's merged processor supports right or left Shadow hands and kinematic MuJoCo replay. Video capture and real-person anatomy fit remain unresolved; the gateway worker/trajectory adapter is now implemented, with physical clip validation still required; see the [integration map](system-integration.md).

## Reusable Anatomy Direction

The user wants broad organ/vessel coverage supporting a growing network of surgery cases. Build on the existing atlas rather than generating a separate body for each case. The [anatomy research](research/comprehensive-anatomy.md) proposes stable identities, verified anatomical relationships, explicit interaction capabilities, regional loading and per-case coverage gates. The manifest names 4,031 parts, but catalog coverage is not a complete runtime or clinically reviewed simulator. The native scene currently uses nine meshes; no general tissue/vascular behavior graph exists. First complete the physical single-case slice, then expand reusable regional content and behaviors. [TAPNet research](research/tapnet.md) covers an optional visible-point tracking experiment for recorded video, not hidden-organ detection.
