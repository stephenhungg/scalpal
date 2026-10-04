# Gateway (API Service)

Owner: Nathan. Follows [Nathan's implementation plan](../../docs/nathan-plan.md).

A thin Node service that does the I/O SpacetimeDB reducers cannot. It connects to the realtime database as a **service identity** and:

- **Transfer grants:** fills upload/download requests with short-lived signed URLs. It supports local disk (dev) or S3/R2 (production), so clip bytes never pass through the database.
- **Upload verification:** checks that each uploaded object exists, has the declared size and, if given, the declared SHA-256, before marking it `available`.
- **Provider credentials:** fills `voice` grants with an ElevenLabs signed URL and `ice` grants with Cloudflare TURN credentials. API keys never reach clients.
- **Motion worker API:** an HTTP pull API for Silas's processor (claim, heartbeat, outputs, complete, fail) on top of the module's lease and run checks. See [worker-api.md](../../packages/contracts/worker-api.md).
- **Restart safety:** state lives in SpacetimeDB, so after a restart the gateway resumes whatever is outstanding.

Browsers and the Quest use SpacetimeDB for shared state and grants. The read-only recap routes below also allow an authenticated member to fetch replay state and expiring video URLs through the gateway.

## Run

See `.env.example` for every setting.

```sh
npm install
cp .env.example .env
npm run dev               # tsx watch
npm run build && npm start  # bundled (esbuild) production build
npm run worker:synthetic  # synthetic motion worker (labelled output; not reconstruction)
```

On first start without `SPACETIMEDB_TOKEN`, the gateway creates an identity, saves its token to `.gateway-token`, and logs the `spacetime call … add_service_identity` command that registers it. In production, store that token as a secret: with `NODE_ENV=production` (the Docker image and `fly.toml`) the gateway refuses to start without `SPACETIMEDB_TOKEN`, because the token file does not survive a container restart.

`GET /healthz` reports database connection, service registration, storage driver and which providers are configured.

The gateway listens on port 8788 by default (`PORT`). Preop/coach uses 8787, and the motion worker (`services/motion`, `gateway-worker`) targets `http://localhost:8788`.

## Tests

The integration tests need a local `spacetime start`. They publish the module to a throwaway `scalpal-test` database and run the real gateway in-process:

```sh
npm test
npm run test:unit   # no SpacetimeDB needed
```

The integration harness publishes with `--delete-data=always`, so it refuses any database not named `scalpal-test-*` and any server that is not local (the `local` CLI server with a loopback `TEST_SPACETIMEDB_URI`). Set `TEST_SPACETIME_ALLOW_REMOTE=1` to target a remote server on purpose; the name rule still applies.

They cover:

- **State and access:** propagation to every member, reconnect with the same identity, outsider read/write isolation, role checks, presence, invite rotation.
- **Commands:** idempotency, stale-step rejection, expiry.
- **Uploads:** verified upload/download, tampered signatures, size mismatch, missing object.
- **Jobs:** duplicate requests, the full worker lifecycle, lease expiry with a late completion rejected, concurrent claims, retryable and fatal failures.
- **Grants and signaling:** ICE and voice grants, recipient-only signals.
- **Lifecycle:** new attempts superseding pending commands, ended sessions.

Last run: 23/23 passing (Oct 3, local SpacetimeDB 2.10.2).

## Deploy

`Dockerfile` builds a small Node 22 image (`docker build -t scalpal-gateway services/api`). It needs:

- **Outbound** WebSocket access to SpacetimeDB.
- **Inbound** HTTPS for workers (and for `/files` when `STORAGE_DRIVER=local`).

For a hosted site use `STORAGE_DRIVER=s3` with a private R2 bucket. Configure the bucket's CORS to allow `PUT`/`GET` from the companion origin with the `content-type` header.

## Recap and replay

`GET /v1/sessions/:sessionId/replay/:jobId` requires `Authorization: Bearer <client SpacetimeDB token>`. The gateway reuses a live connection using that token and checks the current database-scoped `myMemberships` view on every request; it never trusts a caller-supplied identity and never accepts worker credentials for this route. An absent bearer returns 401, denied membership 403, unknown/cross-session job 404, and authorization timeout 503. Use HTTPS outside localhost.

The response is `{ schemaVersion: "scalpal.replay.v1", sessionId, attemptId, jobId, status, reason, progress, stage, sourceVideoUrl, replayVideoUrl, expiresAtUnixMs, replayKind, label }`. Status is `queued`, `processing` (worker `running`), `ready`, or `failed` (`cancelled` also maps to failed with a reason). A ready job lacking an available matching output, or reporting a non-kinematic replay kind, is explicitly failed; unsupported physics outputs are never relabeled kinematic or signed for playback. URLs are empty when unavailable and expire within five minutes; only available video artifacts matching the session/attempt and the completed output's job/run are signed. Responses are `no-store`. No capture, scoring, fallback selection, or job creation occurs here: clients still submit uploaded clips through the existing reducers. Signed local MP4/WebM downloads support byte ranges for scrubbing; S3/R2 supports ranges natively.

### Recap playback resolution

`GET /v1/sessions/:sessionId/replay/:jobId` requires the viewer's SpacetimeDB bearer token. The response includes stable `jobRun`, `sourceArtifactId`, `replayArtifactId`, and `source`; `sourceVideoUrl` / `replayVideoUrl` are temporary playback capabilities (at most five minutes), not fields to persist in an exported RunResult. Resolve these references when opening a view, and again on playback error/expiry while preserving playback time. Stop polling when ready/failed; the headset and companion back off pending requests from 3 to 10 seconds.

Provenance is explicit. Existing raw-clip rows have no source classification. To declare it, upload one available `capture_manifest` artifact through the normal grant flow, include its id in the job's `extraArtifactIds`, and use this new, versioned JSON contract (maximum 16 KiB):

```json
{"schemaVersion":"scalpal.capture-provenance.v1","sessionId":"<session>","attemptId":"<attempt>","inputArtifactId":"<raw_clip artifact>","source":"learner"}
```

`source` accepts `learner`, `rehearsal`, or `sample`. The session, attempt and input must match the job and registered artifact. Missing, conflicting, oversized or mismatched manifests produce `source: "unknown"`; a completed job with unknown provenance fails recap playback rather than asserting that it belongs to the learner. This metadata is a capture-owner declaration, not independent proof of camera origin or permission. Missing `quality.replayKind` likewise stays `unknown`, including through the worker completion endpoint.

Authorization retains a live membership subscription for up to 30 seconds, so permission removals continue to apply; it does not cache an allow decision. Connections are bounded to 64, concurrent requests for one token share the same pending connection, and disconnected subscriptions fail closed with retryable status. Replay requests are limited to 30/minute per token and 240/minute per transport IP (or an unattributed bucket when the adapter has no peer address), with bounded counters and `Retry-After: 60` on 429. Proxy forwarding headers are not trusted.

The old unauthenticated `/v1/recap/voice-prompt` endpoint is removed. Preop owns run-bound recap speech at `POST /coach/runs/:runId/recap` and its returned reaction audio route. Native coach creation/recovery registers the canonical `HandoffTicket.runId` with the real coach session; the session-ID route remains for legacy standalone compatibility. It synthesizes a fixed server question through Jarvis's existing TTS voice without issuing a conversational agent configuration or surgery tools.
