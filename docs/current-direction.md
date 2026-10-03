# Current Direction

Updated October 3, 2026 after the user removed the monetary/onchain component. This page takes precedence over older architecture, flow, research, and team-plan documents where they disagree.

## First Demo

The first case is **lap_appendectomy**. The atlas now includes `cecum` and `terminal_ileum`, followed by the requested gallbladder and colectomy targets. See [anatomy integration](anatomy-integration.md) for implemented wiring and the remaining headset/service boundaries. The adult synthetic demo fixture is `patient-demo-multi-source`. This selects the educational case, not a robot model or validated hardware flow.

## Scope Change

Solana, wallets, onchain challenge programs, Devnet payouts, and money for completing simulated surgeries are removed from the project. They are not required integrations, acceptance checks, or assigned work. Earlier references to these features are historical context, not instructions to implement them.

## Remaining Thesis

AI guides people through mixed-reality learning and practice. Recorded passthrough video can supply estimated hand movements that are mapped onto a simulated robot hand for replay. The longer-term hypothesis is that useful demonstrations could support robot learning. Replay does not itself demonstrate a learned autonomous policy.

The working experience is: open the Quest app → discuss an exercise with Jarvis → explore a rotating 3D anatomy preview → confirm the exercise and fit generic anatomy to a real reclining participant → practice with simulated tools → receive feedback → process/replay the recorded motion → recap or retry. The exact final flow and task remain subject to Nathan's plan.

## Work Status

The user previously asked to wait for Nathan's plan, then requested storage/realtime planning and explicitly assigned the updated implementation lane: **Nathan owns the companion website plus SpacetimeDB and routing; Matthew continues to own Jarvis**. See [Nathan's implementation plan](nathan-plan.md) and [data/storage design](data-and-realtime.md). The earlier blanket wait does not block this assigned lane. The latest request updates the work order; no application implementation is performed by this documentation change. The exact shared exercise, robot model, and other unresolved decisions remain open. No GitHub identity mapping for Nathan is assumed.

The companion website is a live observer view, not another voice agent. It combines shared session/coach/processing state with a separately transported composited headset video feed. The existing Mac mirror is a proposed video source to validate, not the final companion website or a verified network stream.

The repo now contains anatomy assets, Unity anatomy/exercise code, and preop service components alongside the planning documents. See the [anatomy asset guide](../assets/anatomy/README.md) for the committed Blender workspace, source models, Unity exports, and teammate setup. This is not evidence of a working end-to-end application or validated Quest anatomy rendering. Earlier measured native camera and bottle-overlay results remain valid infrastructure evidence. Recording/reconstruction rights, torso registration, robot hand and educational content validation still require resolution.
