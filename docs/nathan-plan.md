# Nathan's Implementation Plan: Companion Website and SpacetimeDB

> Implementation update: team feature branches now contain component code. Read the [system integration map](system-integration.md) for audited commits, actual routes, missing adapters and verification. The plan below describes intended responsibilities, not proof of a connected deployment.

Assigned scope: October 3, 2026. The user requested this implementation work order after removing Solana and confirming that the companion website should show the headset session live. This document updates the plan; no application, stream, backend, bucket, or public deployment is implemented by this documentation change.

## Outcome

Build a browser companion where an authorized viewer can watch the simulated surgery session, see its current exercise/organ/step and coach context, follow recording/processing status, and access the completed robot replay. SpacetimeDB is the core shared-state backend. Private file storage holds recordings and derived artifacts. A separate media connection carries live video.

Matthew is already working on **Scalpal, the voice agent with organ and exercise-state context**. Nathan does not build another agent or take over its reasoning/content. Nathan supplies the state, service authorization where needed, command routing, and viewer interface that connect Matthew's agent to the rest of the system.

Nathan's GitHub identity has not been established in the conversation; do not assume it from the old `nakim12` assignment.

## Owned Paths

| Path | Nathan's responsibility |
| --- | --- |
| `apps/companion/` | Browser publisher/viewer, live session panels, processing/results UI |
| `services/realtime/` | SpacetimeDB tables, views, reducers, subscriptions, session roles |
| `services/api/` | Thin provider/storage/worker gateway and supporting media setup where needed |
| `packages/contracts/` | Propose session/command/artifact/job/media contracts with other owners; coordinate shared changes |

Stephen owns Quest capture, registration, rendering, and integration of the Unity state adapter. Matthew owns Scalpal and its supported actions/explanations. Silas owns hand inference, robot retargeting, and the replay output. Nathan coordinates with them rather than modifying their algorithms or shared Unity scenes independently.

## Build 1: Shared State and Companion Shell

Initialize the smallest companion application and SpacetimeDB module that can show a single session. Choose a supported module/client stack and pin its versions. Do not add a second primary state database or a separate backend for every participant.

Define only the records needed by the actual flow:

- Session and membership/roles, with one stable session/attempt identity.
- Exercise version, current mode/step, selected organ, pause state, coarse registration readiness.
- Authored interaction/feedback events and coach-visible context.
- Required action requests and applied/rejected acknowledgements.
- Artifact metadata, motion jobs/results, and replay availability.
- Session-scoped media signaling/status if using the proposed WebRTC route.

Use private records and identity-aware views; authorize mutations by role/session. Spectators can view their authorized session but cannot change the exercise or mark jobs complete. Stephen's runtime publishes observed exercise state; Matthew's agent requests allowed actions; Unity validates and acknowledges them. Do not announce a successful highlight merely because a request entered the database.

The companion UI initially shows connection/source status, exercise and step, selected organ, relevant Scalpal text/status when Matthew exposes it, recording/job progress, and a results area. It is an observer interface, not a second surgery simulator or a second voice agent.

Provide generated C# bindings and a minimal connection contract for Stephen. The Unity SDK requires connection advancement through its manager or `FrameTick`; verify actual Quest Android/IL2CPP behavior and endpoint reachability before assuming integration works. [SpacetimeDB C# SDK](https://spacetimedb.com/docs/clients/c-sharp/).

## Build 2: Live Headset View in the Website

The required viewer image should show what the wearer sees, including virtual anatomy/tools. Raw passthrough footage excludes those overlays and is a different input for Silas. Keep these two media products separate. [Meta camera overview](https://developers.meta.com/vr/documentation/unity/unity-pca-overview/).

Recommended first route to validate:

```text
Quest composited view
  → existing mirror window on Stephen's Mac
  → companion publisher captures that window
  → WebRTC media connection
  → companion viewer on a second browser/device
```

The publisher has an explicit Start sharing action. The operator selects the Quest mirror window through the browser's screen-sharing prompt. `getDisplayMedia()` requires a supporting browser, secure context, user activation, and user permission. Permission denial or a stopped track must produce a clear source-off state. Start with video only; do not silently capture microphone/system audio or the whole desktop. [Browser screen capture](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getDisplayMedia).

A WebRTC peer connection transports the media. Session-scoped signaling exchanges connection setup messages; SpacetimeDB can coordinate that small signaling state, but video frames do not become database rows. Verify the chosen browser/network route; remote-network connectivity may need TURN infrastructure. Limit the first demo to one publisher and one viewer, and do not promise arbitrary viewer scaling. [WebRTC signaling](https://developer.mozilla.org/en-US/docs/Web/API/WebRTC_API/Signaling_and_video_calling).

The mirrored-window route is a proposal to test, not an already functioning stream. Stephen supplies the actual mirror and confirms that it includes the desired scene. If capture/encoding is unreliable, coordinate a different composited capture route rather than substituting raw RGB or pretending a prerecorded clip is live. A metadata dashboard alone does not satisfy this live-view milestone.

Display video-source status separately from database connection status. The step panel may update while the video disconnects, and vice versa. Do not claim frame-accurate video/state synchronization without measuring timestamps and transport delay. Playback and observer video must not affect the local headset rendering loop.

## Build 3: Recording and Artifact Routing

Choose a restricted local file store for the first integrated test or a private object bucket for permitted cloud transfer. R2 is a researched candidate, not a provisioned account. Provide authorized upload/download with stable artifact IDs/keys, content metadata, availability confirmation, and agreed retention/deletion behavior. Temporary URLs should be minted when needed, not treated as permanent artifact identities.

Stephen produces the raw camera clip plus timing/calibration/virtual-scene metadata required for reconstruction. Store it independently of the live composited stream. A live spectator stream is not automatically recorded or suitable as robot input. Keep clips, full motion sequences, and replay files out of Git and out of broadly subscribed database tables.

Establish the exact permitted capture/processing/sharing arrangement before participant footage is transferred. Use nonpersonal test files for the initial routing check. No viewer needs blanket access to every recorded session or the storage credentials.

## Build 4: Connect Silas's Processor and Matthew's Agent

Expose a small worker contract: input artifact/attempt/configuration identity, current run/version, authorized access references, and output artifact/quality/error result. A reducer creates or returns a deduplicated job and atomically claims the current lease/run. The gateway dispatches it to Silas's external processor. Only the active authorized run can report completion; stale results cannot overwrite a retry or a new attempt. Dispatch/computation may repeat, so do not promise exactly-once processing.

Run inference outside SpacetimeDB. Reducers cannot perform network/filesystem I/O; the thin gateway handles worker/provider/storage integration while the database owns the shared state. Procedures are another documented external-I/O mechanism if the chosen module needs them; do not add both routes without a requirement. [Reducers](https://spacetimedb.com/docs/functions/reducers/), [procedures](https://spacetimedb.com/docs/functions/procedures/).

Coordinate Scalpal integration with Matthew's current code rather than replacing it. Agree its exercise context payload, stable organ IDs, allowlisted action requests, outcome acknowledgements, and provider authorization needs. Show only the guidance/status he actually emits. Do not duplicate his model prompts, medical content, voice session, or scoring rules in the companion.

Silas supplies a replay manifest/output format that the companion can actually display. Agree whether it is joint animation data, a rendered video, or another supported artifact before implementing the viewer. Finished motion processing is distinct from learning completion; invalid clips show a useful failure without erasing feedback.

## Milestones and Done Checks

| Milestone | Required evidence |
| --- | --- |
| Shared state | A synthetic state change appears in both authorized clients; reconnect recovers the correct session/version; an unrelated identity cannot read/change it |
| Actual Quest integration | Stephen's Unity client connects on the physical headset and sends a real supported state event visible on the web; local rendering remains responsive |
| Live website | A second browser/device watches a changing composited Quest view with actual overlays; source stop/restart and denied capture are handled; record measured video delay and tested network/browser |
| Storage | An authorized nonpersonal test artifact uploads, is verified available, and downloads; failure/retry does not create conflicting references; access is session-scoped |
| Motion integration | An actual Silas output becomes replay-ready in the web UI; failed processing remains visible; duplicate requests and obsolete completion do not corrupt the current result |
| Scalpal integration | Matthew's current context/action contract works through the shared state; a real applied/rejected action is reflected accurately; no second agent is introduced |
| Rehearsal | Selection → practice → feedback → processing → replay works with live viewing and reconnect/failure handling; no payments or wallet flow |

Test the media path early alongside the small state proof. It is an independent technical risk; a successful subscription does not establish live video. Coordinate real participant recordings only after the relevant capture permissions and integration arrangement are resolved.

## Deliver to the Team

Provide setup/run instructions, pinned versions, endpoint and network requirements, a secret-free configuration example, generated bindings or generation commands, shared schemas, and one synthetic session fixture. Report what was tested in a browser versus on Quest, the actual storage/stream setup, known latency/compatibility limits, and any remaining external account requirement.

Open focused changes in a branch such as `nathan/companion-realtime`. Coordinate shared contracts with Stephen before merge and leave other owners' scene/agent/processor code alone. No outreach, public deployment, paid resource provisioning, screen capture, or participant recording has been performed by this plan update.

See [data/storage design](data-and-realtime.md), [current direction](current-direction.md), and [team ownership](team-plan.md) for surrounding context. This work order supersedes the older payout lane and the suggestion that the companion might remain only a local mirror.

## Status (October 3, 2026, evening)

Implemented on branch `nathan/companion-realtime`. Everything below was tested locally against SpacetimeDB 2.10.2 and headless Chromium, **not yet on the Quest or a hosted deployment**.

| Milestone | State | Evidence |
| --- | --- | --- |
| Shared state | Built | `services/realtime/` module. 23 gateway/module integration tests pass: propagation to every member, reconnect, outsider isolation, role checks |
| Actual Quest integration | Bindings ready, not tested | C# bindings in `services/realtime/bindings/csharp/`; Stephen to integrate and verify IL2CPP behavior and reachability |
| Live website | Built, locally verified | WebRTC publisher/viewer with SpacetimeDB signaling. Separate browser contexts reached video connected at 1280×720 and about 30 fps; source stop and denial states shown. Real mirror window, TURN across networks, and delay are unmeasured |
| Storage | Built | Signed-URL grants (local disk or S3/R2), size/SHA-256 verification, tamper rejection, retry |
| Motion integration | Built against a synthetic worker | Worker HTTP API with leases. Duplicate and stale-run protection tested. Silas's real output not yet connected |
| Scalpal integration | Contract and credential path built | Command allowlist, applied/rejected acknowledgements, transcript and status, ElevenLabs voice grants. Matthew's agent not yet connected |
| Rehearsal | Synthetic only | The synthetic headset page runs selection → practice → review → processing → replay |

Contracts proposed for agreement: [realtime-v1](../packages/contracts/realtime-v1.md), [worker API](../packages/contracts/worker-api.md), [trajectory schema](../packages/contracts/robot-trajectory.v1.schema.json).

Still needed:

- a SpacetimeDB login for maincloud
- website and gateway hosts
- an R2 bucket and Cloudflare TURN key
- the ElevenLabs agent id and key (from Matthew)
- agreement with Stephen, Matthew and Silas on the contracts

### Deployment (October 3, 2026)

| Piece | Where | Notes |
| --- | --- | --- |
| Website | https://scalpal-companion.vercel.app | Vercel project `scalpal-companion` (Nathan's account), built from `apps/companion` |
| Realtime database | SpacetimeDB maincloud, database `scalpal` | Published from `services/realtime` with Nathan's SpacetimeDB login |
| Gateway | https://scalpal-gateway.fly.dev (Fly app `scalpal-gateway`, region `ord`) | One always-on machine; secrets in Fly, `fly.toml` in `services/api` |
| Artifact storage | Cloudflare R2 bucket `scalpal-artifacts` | Private; CORS allows the website origin and localhost dev ports |
| Video relay | Cloudflare Realtime TURN | Short-lived credentials minted per viewer by the gateway |

Verified against the live URLs with headless Chromium, both peers forced onto the TURN relay: session creation, invite join, live video via the relay, the synthetic headset script, a headset-applied action, browser upload to R2 with gateway verification, synthetic processing, a synced replay, source stop, the phone layout, and ending the session. The real Quest mirror, Matthew's Scalpal and Silas's worker are not yet connected.
