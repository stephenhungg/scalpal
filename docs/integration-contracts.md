# Integration Contracts

Updated October 3, 2026. These are current boundary requirements, not a claim that one agreed schema is deployed. Read the [system integration map](system-integration.md) first: Nathan has implemented proposed realtime/worker/trajectory contracts in his branch; Matthew and Silas have different working protocols. Their schemas must be reconciled rather than copied blindly. Historical wallet/reward contracts are removed.

## Identity and Versions

Each exchange identifies its session, attempt, authored exercise ID/content version, schema version and applicable step version. A retry of practice creates a new attempt; a processor retry creates a new run under the same job. Pair explicitly with an authorized session rather than adopting the newest patient session.

Matthew currently uses case/patient/procedure IDs and coach IDs; Nathan uses session/attempt/exercise IDs. Agree the mapping and selected authored case/version. The browser's `instrument-transfer` @ `0.1.0` placeholder is not a catalog agreement. Matthew's newer anatomy slice prepares adult `patient-demo-multi-source` / `lap_appendectomy` first; map that exact case/version rather than the placeholder. Artifact, command, event and job IDs refer to the same session and attempt; existence alone is insufficient.

Keep presentation mode separate from lifecycle phase. Matthew's coach `mode` already means `mixed_reality` or `virtual`; Nathan's current `mode` means lifecycle phase. Map those semantics explicitly and agree the separate presentation field/migration before publishing it from Unity.

## Exercise and Applied Events

The exercise fixes anatomy asset/version, stable structure IDs, tool IDs, allowed actions, step transitions, hints, mistakes, completion rules and invalid states. A voice model cannot invent scoring rules or scene IDs. An applied tool effect is not automatically a completed step.

The scoring dispatcher validates the active attempt/phase, valid scene/body fit, tracked held tool, allowed target/tool/action and authored step. It produces one accepted outcome per action. Input retries need a stable client event ID and deduplication; multiple colliders and contact reentry cannot inflate counts. Preserve rejected/blocked events separately from accepted progression when useful for feedback.

Main tool events carry `instrumentId`, `targetId`, action/outcome, Unity-world point and `unityMonotonicSeconds`. The application supplies session/attempt/version identity; the standalone kit must not guess it. The existing coach event batch lacks those envelope fields. Use exactly one scoring adapter; keep raw focus distinct from scored activation.

## Registration and Scene Binding

Both presentations supply one patient/torso root and an explicit validity signal, with source, observation age, failure reason and recovery policy. Torso units are meters, origin at the skin umbilicus, +Z toward head, +Y out of abdomen, +X participant left. Each anatomy asset needs a validated source-to-torso conversion; source origin is not automatically the umbilicus.

Validity gates practice visibility, interaction colliders, tool effects, contact/UI scoring and coach context. Missing required bindings fail closed. Preview visibility never authorizes scoring. Reset/mode change releases tools, invalidates old alignment and pending context, restores target state and rebinds before input resumes.

## Voice Requests and Outcomes

A command identifies command/session/attempt, expected step/state version, allowlisted action, stable target and bounded parameters. Serialize conflicting actions and reject stale requests. The actual scene consumer returns applied, rejected, unavailable or loading with a reason; Scalpal announces completion after actual application.

Nathan's allowlist includes preview/rotate/zoom/isolate/restore/confirm/highlight/hint/pause/resume. Matthew's current anatomy command consumer implements only highlight/clear-highlight. Reconcile action names and capability availability; a requested row is not proof of a scene handler. Browser auto-ack and autoplay are test tools and cannot acknowledge a real headset outcome.

Failed event/ack delivery remains retryable. Explicitly publish initial tracking and confirmed state on pairing/reconnect. A server snapshot version alone does not provide inbound event deduplication or expected-version validation. The latest Scalpal HTTP receipt separates `accepted` from `applied` and exposes `eventCount`; the anatomy relay still checks version zero and reads only receipt acceptance. Reconcile late-join freshness and progression confirmation explicitly.

## Capture Artifact

A clip belongs to an attempt and identifies raw-versus-composited source, camera, resolution/orientation/crop, available intrinsics, encoding, frame sequence or PTS, start/end and dropped frames. Record clock domains, their mapping/uncertainty and available acquisition-time camera poses. A nearby preview timestamp is not automatically an exposure timestamp.

The associated scene timeline records applied virtual tool/object states and accepted events, since raw passthrough excludes them. Capture manifest and timeline artifacts must belong to the same attempt as the clip. Uploaded file labels alone do not verify provenance or metadata validity. Do not promise metric depth, stereo capture or precise pose association before measuring them.

Large artifacts remain private files; shared state exposes scoped metadata and temporary authorized access. No participant footage or identifying logs enter Git. Retention/deletion and intended processing/sharing use must be explicit for the selected route.

## Hand Estimates and Robot Replay

An estimate links input artifact/frame/time, model/version, handedness, landmarks, coordinate frame/units, confidence and validity. Preserve missing observations, skipped frames and unknown handedness. Hand-centered model coordinates are not calibrated Unity-world wrist pose.

A robot output specifies model/version, named joints/order/units/limits, neutral/start pose, root frame, retarget configuration, applied targets and timebase. Distinguish kinematic visualization from physics/contact simulation; arm inverse kinematics is separate from finger mapping.

Silas emits `scalpal.robot_motion/0` frame records with null `qpos` on invalid frames; Nathan's viewer expects `scalpal.robot_trajectory.v1` parallel `t/q/valid` arrays. Agree a conversion that preserves validity and provenance. Validate dimensions, finite values, ordering, units and limits before replay-ready. A held display pose with `valid:false`, if chosen, must never be labeled as observed motion. Video timing approximation is explicit.

## Storage and Worker Lifecycle

Artifact metadata records stable object key, MIME type, size, digest/verification status, source and ownership. The gateway verifies a submitted upload before availability; file availability does not imply valid content or successful processing. Mint fresh scoped access rather than persisting expiring URLs as the only reference.

A processing job deduplicates by attempt/input/configuration. An atomic claim records worker identity, lease and run generation. Heartbeat/completion must match the authenticated worker, active unexpired run, expected attempt and run-specific outputs. Reject obsolete results; reconcile pending/expired work after restart. Repeated computation may occur, so do not promise exactly-once execution.

Nathan implements pull claim/heartbeat/upload/complete; Silas implements a synchronous push processor. A worker adapter must connect the real processor to Nathan's protocol and convert artifacts. Synthetic worker output is a fixture, not reconstruction evidence.

## Shared State and Media

Unity applies immediate simulation and validity gates; one deliberately chosen authored-step authority produces confirmed progression. SpacetimeDB holds coordinated session state, command outcomes, learning results and job/artifact status. Matthew's coach consumes confirmed context; the gateway does not introduce a third scorer. The current duplicated engines still need reconciliation.

Spectator media uses a separate composited WebRTC stream with explicit publisher source, session access, connection/status and measured delay. State disconnect, media disconnect and recording failure are separate. Raw clips for offline inference are not equivalent spectator media; database notifications do not provide video transport.

Learning completion, recording availability, processing success, replay readiness and reconstruction quality are separate statuses. A finished lesson can have failed motion processing. No wallet/payment/contribution-reward status exists.

## Changes and Acceptance

Boundary owners review breaking schema/action changes with Stephen, update the [map](system-integration.md), and supply a synthetic exchange that the real producer and consumer both accept. Include an invalid/retry case. Main's `packages/contracts/` remains a scaffold until agreed definitions land; branch-only schemas are not silently authoritative.

State what was tested in an editor, synthetic browser/worker, native headset or complete session. Follow the map's shipping checks before marking an integration complete. Preserve primary source and asset notices independently of interface compatibility.
