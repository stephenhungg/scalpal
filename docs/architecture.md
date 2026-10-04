# Architecture

> **Scope notice:** the current product flow is the [latest experience flow](current-direction.md#latest-experience-flow): launch → explore patients → diagnosis office → full-VR surgery → required robot replay. Mixed reality with a real participant, conversational selection and the rotating preview are off the main path. Where this document disagrees, current direction wins.

Updated October 3, 2026. Use the [system integration map](system-integration.md) for audited commits, actual routes, missing adapters and shipping checks. This document describes the current target architecture; it does not claim the components are connected. Earlier wallet/challenge architecture is superseded and remains recoverable in Git history.

## Shared Native Runtime

Scalpal has one Unity application. The main flow is full VR around an authored virtual patient/table; the earlier mixed-reality presentation over a real reclining participant remains in the repository off the main path. Both presentations use the same anatomy identities, tools, authored exercise rules, Scalpal and session flow. A mode changes the background and patient-fit source. It does not instantiate another coach, scorer or storage system.

| Layer | Owns | Runs where |
| --- | --- | --- |
| Device foundation | Native XR rig, controller tracking, passthrough, camera permission/acquisition | Quest / Stephen |
| Registration and presentation | Accepted real surface/body fit or authored virtual fit; validity and recovery; anatomy-to-torso conversion | Quest / Stephen |
| Simulation and experience | Explore page, diagnosis office, anatomy controls, virtual tools/effects, one authored case and feedback | Quest / Stephen + Matthew |
| Coach | Reviewed case context, diagnosis-office patient and attending voices, guidance, bounded application requests | Matthew's service/browser voice client + Unity action binding |
| Shared coordination | Membership, confirmed session/exercise state, commands/outcomes, artifacts and job progress | Nathan's SpacetimeDB module and client adapters |
| Media and artifacts | Composited spectator stream; separately private raw clips, manifests and derived output files | WebRTC publisher/viewer + Nathan's gateway/storage |
| Motion and replay | Decode permitted video, estimate hand motion, robot-specific retargeting, replay and validity signals | Silas's external worker; companion playback |

Main currently contains tool/runtime and room/patient components. Team feature branches contain the case/coach, anatomy, backend and processor. A shared bootstrap, real XR configuration, body registration and cross-component adapters still need integration. See the map rather than assuming a folder or generated SDK binding is a connected application.

## Authority and Actions

Keep rendering, tracking, physics and immediate validity gates local. A network round trip must not decide whether the next anatomy frame can render or a dropped controller remains held.

The first proposed integration keeps Unity's authored runner responsible for accepted local steps and publishes its confirmed state to SpacetimeDB. Scalpal requests allowed actions and consumes confirmed context; Nathan routes and persists coordination rather than introducing another scoring engine. Matthew currently runs local and server engines, so this authority choice still needs to be implemented consistently. See the concrete divergence risks in the map.

One action follows: tracked input or bounded voice request → validate session/attempt, scene, registration, target and step → apply/reject → publish actual outcome → acknowledge → update coach/observer. Raw contact for focus, an applied visual effect and a scored transition are separate events. Use one scored adapter with retry identity and deduplication.

Registration invalidity must reach anatomy visibility/colliders, authored effects, controller/contact dispatch, UI scoring and coach state. Preview anatomy can rotate before fitting, but that bypass cannot authorize practice. Mode changes release tools, invalidate/rebind the fit and restore authored target state before another action.

## Data and Media

SpacetimeDB holds small shared records. Private file/object storage holds clips, calibration/capture manifests, virtual scene timelines, inferred tracks and robot trajectories/videos. The gateway handles scoped access and external processing; the external worker performs video inference. [Storage design](data-and-realtime.md) explains the split and [contracts](integration-contracts.md) defines the required boundaries.

Live observer video travels over WebRTC from the selected composited mirror window. It includes virtual anatomy/tools. Raw passthrough does not contain virtual objects and remains the user's requested reconstruction input. A full-VR rendered recording is another source; do not relabel it as observed raw input. Store source identity and the applied virtual timeline explicitly.

The learning result and motion-processing result remain separate. A completed exercise may produce unusable footage. A ready artifact does not prove parseable replay or accurate reconstruction. There are no wallets, payouts or onchain operations.

## Coordinates and Time

Use meters for Unity world and the torso frame: origin at skin umbilicus, +Z toward head, +Y out of abdomen, +X participant left. Anatomy source coordinates require an explicit validated conversion. A detected person rectangle or table hit does not determine that transform or reveal internal organs.

Image pixels, camera rays, estimated model coordinates, Unity world and robot joints are distinct spaces. Preserve intrinsics/crop, observation identity, capture-time pose availability and uncertainty. Hand-centered model meters do not establish metric wrist translation in Quest world; robot joints need named ordering, radians/other declared units and limits.

Camera time, Unity monotonic time, decoded video PTS and backend wall time are distinct clocks. Record their measured relationship and uncertainty; do not subtract unrelated timestamps to invent latency. Preserve missing observations and avoid interpolation that invents long unseen movement. Kinematic robot replay is not autonomous policy learning or validated contact simulation.

## Success

One explicitly paired attempt completes explore → diagnosis office → full-VR authored practice/feedback → permitted recording → real processing → compatible replay, while an observer sees actual composited media and confirmed state. Repeat it, including tracking loss, reset, reconnect, failed processing and stale commands/results. The [shipping checks](system-integration.md#shipping-checks) define the concrete evidence required before calling it integrated.
