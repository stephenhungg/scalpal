# Native session failure checks

Run from the repository root. These scripts never start Unity, XR, a microphone,
voice providers, or the live FinchNode client. They do not load private configuration.

```sh
python3 scripts/quest/session-check/run-bridge.py
services/preop/node_modules/.bin/tsx scripts/quest/session-check/coach-http.ts
services/api/node_modules/.bin/tsx scripts/quest/session-check/live-realtime.ts
```

`run-bridge.py` compiles the **unchanged production `QuestSessionBridge.cs`** using
the installed Unity Mono compiler. Deterministic Unity/SDK transport/cache doubles
control reducer callback order, connection loss, and scene-handler failures.
It verifies commit-before-ack, failed snapshots, stale/expired/foreign commands,
single execution across reconnect, no uncertain event retries, bounded snapshots,
attempt confirmation ordering, and learning-result acknowledgement. This is a
production logic check, not a real SDK/WebSocket or headset test.

`coach-http.ts` starts an isolated ephemeral loopback HTTP server using the actual
preop app, coach routes, engine, and recorded synthetic fixture client. It verifies
paused activations, event identity across fresh sessions, terminal command ack
immutability, and rejection of stale or unknown step metadata. It never modifies
the already running coach service or its current-session pointer. The synthetic
clock also documents the currently unversioned coach-command expiry behavior.

`live-realtime.ts` connects to the **existing** local SpacetimeDB with new identities
and creates two test-owned sessions. It verifies native-shaped snapshot/command
commit exchange, real headset-identity reconnect, fresh-attempt resets, and rejection
of abandoned or foreign-session attempt events. Cleanup ends only these test sessions.
Ended synthetic session rows remain in the database. It never publishes a module,
resets a database, registers a service identity, reads another operator's private
rows, or contacts gateway/provider endpoints. URI defaults to `ws://127.0.0.1:3000`
and database to `scalpal`; use `SCALPAL_CHECK_URI` / `SCALPAL_CHECK_DATABASE` to change
them. Only unencrypted loopback WebSocket hosts are permitted. Output excludes tokens
and invitation codes.

All scripts return nonzero when an assertion fails. A failure on the live database
may mean its deployed module differs from the checked-in backend source; deploying
backend changes is intentionally outside these scripts.
