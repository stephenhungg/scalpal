# Gateway (API Service)

Owner: Nathan. Follows [Nathan's implementation plan](../../docs/nathan-plan.md).

A thin Node service that does the I/O SpacetimeDB reducers cannot. It connects to the realtime database as a **service identity** and:

- **Transfer grants:** fills upload/download requests with short-lived signed URLs. It supports local disk (dev) or S3/R2 (production), so clip bytes never pass through the database.
- **Upload verification:** checks that each uploaded object exists, has the declared size and, if given, the declared SHA-256, before marking it `available`.
- **Provider credentials:** fills `voice` grants with an ElevenLabs signed URL and `ice` grants with Cloudflare TURN credentials. API keys never reach clients.
- **Motion worker API:** an HTTP pull API for Silas's processor (claim, heartbeat, outputs, complete, fail) on top of the module's lease and run checks. See [worker-api.md](../../packages/contracts/worker-api.md).
- **Restart safety:** state lives in SpacetimeDB, so after a restart the gateway resumes whatever is outstanding.

Browsers and the Quest never call the gateway for state. They use SpacetimeDB directly and get URLs and credentials through rows only they can see.

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
