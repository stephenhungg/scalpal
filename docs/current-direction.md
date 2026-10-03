# Current Direction

Updated October 3, 2026 after the user removed the monetary/onchain component. This page takes precedence over older architecture, flow, research, and team-plan documents where they disagree.

## Scope Change

Solana, wallets, onchain challenge programs, Devnet payouts, and money for completing simulated surgeries are removed from the project. They are not required integrations, acceptance checks, or assigned work. Earlier references to these features are historical context, not instructions to implement them.

## Remaining Thesis

AI guides people through mixed-reality learning and practice. Recorded passthrough video can supply estimated hand movements that are mapped onto a simulated robot hand for replay. The longer-term hypothesis is that useful demonstrations could support robot learning. Replay does not itself demonstrate a learned autonomous policy.

The working experience is: open the Quest app → discuss an exercise with Jarvis → explore a rotating 3D anatomy preview → confirm the exercise and fit generic anatomy to a real reclining participant → practice with simulated tools → receive feedback → process/replay the recorded motion → recap or retry. The exact final flow and task remain subject to Nathan's plan.

## Work Status

The user previously asked to wait for Nathan's plan, then requested storage/realtime planning and explicitly assigned the updated implementation lane: **Nathan owns the companion website plus SpacetimeDB and routing; Matthew continues to own Jarvis**. See [Nathan's implementation plan](nathan-plan.md) and [data/storage design](data-and-realtime.md). The earlier blanket wait does not block this assigned lane. The latest request updates the work order; no application implementation is performed by this documentation change. The exact shared exercise, robot model, and other unresolved decisions remain open. No GitHub identity mapping for Nathan is assumed.

The companion website is a live observer view, not another voice agent. It combines shared session/coach/processing state with a separately transported composited headset video feed. The existing Mac mirror is a proposed video source to validate, not the final companion website or a verified network stream.

The repo now also contains an openable Unity editor project at `apps/quest`, with its scene, authored assets, package configuration and project settings committed, plus a standalone [instrument kit](../assets/instruments/README.md), with Blender sources, runtime models and virtual pickup/action prototypes. The complete native Quest application and Jarvis/state integration remain unbuilt on main. Matthew's feature branches contain the case/coach service and a Unity relay; this instrument work does not merge them. Earlier measured native camera and bottle-overlay results remain valid infrastructure evidence. Recording/reconstruction rights, torso registration, the selected exercise, robot hand, and educational content still require resolution.
