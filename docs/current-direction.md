# Current Direction

Updated October 3, 2026 after the user removed the monetary/onchain component. This page takes precedence over older architecture, flow, research, and team-plan documents where they disagree.

## Scope Change

Solana, wallets, onchain challenge programs, Devnet payouts, and money for completing simulated surgeries are removed from the project. They are not required integrations, acceptance checks, or assigned work. Earlier references to these features are historical context, not instructions to implement them.

## Remaining Thesis

AI guides people through mixed-reality learning and practice. Recorded passthrough video can supply estimated hand movements that are mapped onto a simulated robot hand for replay. The longer-term hypothesis is that useful demonstrations could support robot learning. Replay does not itself demonstrate a learned autonomous policy.

The working experience is: open the Quest app → discuss an exercise with Jarvis → explore a rotating 3D anatomy preview → confirm the exercise and fit generic anatomy to a real reclining participant → practice with simulated tools → receive feedback → process/replay the recorded motion → recap or retry. The exact shared exercise and component adapters still need reconciliation; see the [current demo flow](demo-flow.md) and [system integration map](system-integration.md).

## Two Presentation Modes

The user now explicitly wants both **mixed reality with a real reclining participant and virtual organs** and **full VR with a virtual patient and operating room**. Both use one coach, tool system and authored exercise flow. Surface detection, body landmarks and validated anatomy registration are separate jobs; a table/person box does not determine organ placement. See [mode engineering](environment-modes.md) and [environment-source research](research/surgery-environments.md).

A static CC0 room/patient preview is prepared in `apps/quest/Assets/Scalpal/Environment/`. The native workbench now reuses it with OpenXR and the tool station. It does not implement the two-mode switch, organs or body registration. The real-person direction remains the MR mode rather than being replaced by VR.

## Work Status

The user previously asked to wait for Nathan's plan, then requested storage/realtime planning and explicitly assigned the updated implementation lane: **Nathan owns the companion website plus SpacetimeDB and routing; Matthew continues to own Jarvis**. See [Nathan's implementation plan](nathan-plan.md) and [data/storage design](data-and-realtime.md). The earlier blanket wait does not block this assigned lane. Team feature branches now contain actual implementations; the [integration map](system-integration.md) identifies their source snapshots and missing connections. The work-order documents describe their assigned target, not a complete deployment. The exact shared exercise, robot model, and other unresolved decisions remain open. No GitHub identity mapping for Nathan is assumed.

The companion website is a live observer view, not another voice agent. It combines shared session/coach/processing state with a separately transported composited headset video feed. The existing Mac mirror is a proposed video source to validate, not the final companion website or a verified network stream.

The repo now also contains an openable Unity editor project at `apps/quest`, with its scene, authored assets, package configuration and project settings committed, plus a standalone [instrument kit](../assets/instruments/README.md), with Blender sources, runtime models and virtual pickup/action prototypes. A standalone native full-VR workbench now configures tracked head/controllers, room art and the tool patch; the [hardware test](native-workbench.md) records deployment/tracking evidence and the user-confirmed correction to tool-motion lag. The complete surgery application and Jarvis/state integration remain unbuilt. Matthew's feature branches contain the case/coach service and a Unity relay; this instrument work does not merge them. Earlier measured native camera and bottle-overlay results remain valid infrastructure evidence. Silas's branch selects a right Shadow hand and kinematic MuJoCo replay; Nathan's branch selects web/SpacetimeDB/storage implementations. Those choices do not establish a compatible session: the job/trajectory adapter, capture route, body registration, agreed exercise/version and anatomy fit remain unresolved.
