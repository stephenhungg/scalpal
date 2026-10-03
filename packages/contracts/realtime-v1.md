# Realtime Session Contract v1 (proposed)

Status: **proposed by Nathan, implemented in `services/realtime/`, awaiting agreement from Stephen (merge owner), Matthew and Silas.** The SpacetimeDB module is the source of truth for field names and types; this page explains intent, permissions and vocabularies. Generated bindings: `apps/companion/src/module_bindings/`, `services/api/src/module_bindings/`, `services/realtime/bindings/csharp/` (Unity).

## Identities and roles

Every client connects to SpacetimeDB with its own identity (a token it keeps). A session is joined with a 6-character invite code; the code decides the role. One identity can hold several roles in a session.

| Role | Who | Can |
| --- | --- | --- |
| `operator` | Person running the demo (companion site) | Create/end session, rotate invite codes, remove members, start a new attempt, upload test artifacts, request/retry/cancel motion jobs, send app actions, publish the live view, drive shared replay |
| `headset` | Stephen's Quest runtime (or the synthetic headset page) | Publish exercise state and events, resolve app actions, set the attempt's learning result, post transcript lines, upload clips/manifests, request motion jobs, drive shared replay |
| `coach` | Matthew's Jarvis bridge | Request app actions, post transcript lines, set coach status, request a voice-provider credential |
| `viewer` | Judges, teammates | Read everything in the session; request downloads |
| service | The gateway (`services/api`) | Issue storage URLs and provider credentials, verify artifacts, claim/heartbeat/complete motion jobs |

Reads go only through views that return rows for the caller's sessions. Filtering a public table on the client is not used as a security boundary.

## Session and attempt identity

- `sessionId`: chosen by the creating client, `[A-Za-z0-9_-]{6,64}`, e.g. `ses_k3v9x2q7mn`.
- `attemptId`: `<sessionId>-a<ordinal>`. `startAttempt` creates the next one (retry). Previous attempts keep their learning result, artifacts and jobs.
- `exerciseId` / `exerciseVersion`: fixed per attempt. Matthew's exercise catalog defines the values.

## Exercise state (headset authority)

`publishExerciseState` replaces the confirmed state. Only the headset role may call it, and only for the session's current attempt.

| Field | Values |
| --- | --- |
| `mode` | `Startup`, `Selecting`, `Confirmed`, `Fitting`, `Practicing`, `Reviewing`, `ProcessingMotion`, `RobotReplay`, `Recap` (the old `ChallengeAccepted` is now `Confirmed`; payouts are removed) |
| `registration` | `unaligned`, `valid`, `uncertain` (+ free-text `registrationReason`) |
| `recording` | `off`, `recording`, `failed`, `complete` |
| `stepId`, `stepIndex`, `stepCount` | From the authored exercise |
| `selectedStructureId`, `highlightedStructureId` | Stable anatomy IDs from the exercise manifest. Use `clear*` flags to unset |
| `previewRotating`, `paused` | booleans |

`stateVersion` increments on every publish. `stepVersion` increments only when attempt, mode, `stepId` or `stepIndex` changes. Commands are validated against `stepVersion`.

Publish coarse state when it changes (and at most a few times per second). Torso transforms, camera frames and per-frame physics stay local to the Quest.

`appendExerciseEvent(kind, message, stepId?, structureId?, deviceTimeMs?)`: suggested kinds are `step_started`, `step_completed`, `mistake`, `hint`, `registration_lost`, `registration_recovered`. `deviceTimeMs` is the headset's monotonic clock and is **not** comparable to the server timestamp `at`.

`setAttemptResult`: `practiceStatus` (`not_started`, `in_progress`, `completed`, `abandoned`), step/mistake/hint counts, summary text. This is the learning result; it is independent of motion processing.

## App actions (Jarvis → headset)

Allowlist and required argument:

| Action | Argument |
| --- | --- |
| `previewExercise` | `targetId` (exercise id) |
| `rotatePreview` | `argBool` |
| `zoomPreview` | `argNumber` |
| `isolateStructure` | `targetId` (structure id) |
| `restoreContext` | none |
| `confirmExercise` | `targetId` (exercise id) |
| `highlightStructure` | `targetId` (structure id) |
| `requestHint` | none |
| `pausePractice` | none |
| `resumePractice` | none |

Flow:

1. The coach (or operator) calls `requestCommand` with a client-generated `commandId` and the `expectedStepVersion` it last saw. Resending the same `commandId` is a no-op.
2. If `expectedStepVersion` is stale, the row is created as `rejected` immediately.
3. Otherwise it is `pending`. The headset validates the target ID and current mode, applies it locally, publishes the new state, then calls `resolveCommand(commandId, applied | rejected | unavailable | failed, reason?)`.
4. Pending commands expire after 15 s, or immediately when a new attempt starts.
5. **Jarvis announces success only after it sees `applied`.**

Local pause on the headset never waits for this round trip.

## Coach transcript and status

`postCoachMessage(speaker: learner | coach | system, text)` and `setCoachStatus(offline | connecting | listening | thinking | speaking | error, detail?)`. The companion shows exactly what Jarvis posts. Matthew decides what is posted; no second agent runs in the companion.

Voice credentials: `requestServiceGrant(grantId, sessionId, 'voice')` → the gateway fills `payload` with `{"provider":"elevenlabs","agentId","signedUrl","conversationToken?"}` in a row visible only to the requester (`my_service_grants`). The ElevenLabs API key stays on the gateway.

## Artifacts and the grant pattern

Large files never pass through the database:

1. `requestUpload(grantId, artifactId, sessionId, attemptId, kind, filename, contentType, declaredBytes?, sha256?)` declares the artifact (`pending_upload`) and asks for an upload URL.
2. The gateway issues the grant: `my_transfer_grants` row becomes `issued` with `url` + `method` (valid 15 min by default).
3. The client uploads with that method and `content-type` header.
4. `markUploaded(artifactId)` sets the artifact to `verifying`. The gateway checks that the object exists, its size matches `declaredBytes`, and its SHA-256 matches if given. It then sets `available` or `failed` with a reason.
5. `requestDownload(grantId, artifactId)` works the same way for any member.

Artifact kinds: `raw_clip`, `capture_manifest`, `scene_timeline`, `hand_estimates`, `robot_trajectory`, `replay_video`, `quality_report`, `other`.

Storage keys: `sessions/<session>/<attempt>/<artifact>/<file>`. Job outputs use a run-specific key: `.../jobs/<job>/run-<n>/...`.

The raw passthrough clip (`raw_clip`) is distinct from the composited live view. A capture manifest and scene timeline are separate artifacts that travel with the clip as `extraArtifactIds`.

## Motion jobs

- `requestMotionJob(jobId, inputArtifactId, extraArtifactIds, configVersion)` is deduplicated on `attemptId | inputArtifactId | configVersion`. Input must be an available `raw_clip`.
- Status: `queued` → `running` (claimed; `run` increments) → `ready` | `failed` | `cancelled`.
- Each claim holds a lease. An expired lease requeues the job (up to `maxRuns`).
- Completion is accepted only from the current `run` while `running`. A late result from a superseded run gets HTTP 409 and cannot overwrite anything.
- Repeated computation is possible (exactly-once is not promised); repeated *results* are not accepted.
- `quality`: `framesTotal`, `framesValid`, `invalidIntervals`, `robotModel`, `replayKind` (`kinematic` | `physics`), `notes`.

Workers use the gateway's HTTP API, not SpacetimeDB directly: see [worker-api.md](worker-api.md).

## Live view (WebRTC signaling)

`media_source` row per session: `off`, `starting`, `live`, `stopped`, `denied`, `error`, plus publisher identity, label and dimensions. Signals (`join`, `offer`, `answer`, `ice`, `bye`) are rows addressed to one identity (`my_rtc_signals`), acknowledged with `ackSignals` and swept after 60 s. ICE servers come from an `ice` service grant (Cloudflare TURN when configured, otherwise STUN only). Video bytes never enter the database.

## Replay transport

`setReplayState(sessionId, artifactId?, playing, positionMs, rate)` by operator or headset. Clients extrapolate `positionMs + (now - updatedAt) * rate` while playing. Replay format: [robot-trajectory.v1.schema.json](robot-trajectory.v1.schema.json).

## Clock domains

| Field | Clock |
| --- | --- |
| `at`, `updatedAt`, `requestedAt`, `createdAt` … | SpacetimeDB server time (µs since Unix epoch) |
| `deviceTimeMs` | Headset monotonic clock; not comparable to server time |
| Trajectory `frames.t` | Declared by `timebase.clock`; `clip_pts` = decoded presentation time of the raw clip |
| Replay sync | Server `updatedAt` vs viewer wall clock: approximate, subject to client clock skew |

Do not subtract values across clock domains to claim latency.

## Unity integration notes (for Stephen)

- C# bindings: `services/realtime/bindings/csharp/`. Regenerate with `sh services/realtime/scripts/generate-bindings.sh`.
- Connect with `DbConnection.Builder().WithUri(...).WithDatabaseName(...).WithToken(savedToken)`. Advance the connection every frame (`FrameTick()`) or use the SDK's network manager.
- Subscribe to `SELECT * FROM session_exercise_state`, `session_commands`, `my_transfer_grants`, `my_service_grants` (the headset's views).
- Join once with the headset invite code (`JoinSession`), then persist the token so the identity keeps its role.
- **Not yet verified:** Quest Android/IL2CPP compatibility and endpoint reachability from the headset.
