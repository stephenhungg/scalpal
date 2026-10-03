# Realtime Session State (SpacetimeDB module)

Owner: Nathan. Follows [Nathan's implementation plan](../../docs/nathan-plan.md). Contract: [realtime-v1.md](../../packages/contracts/realtime-v1.md).

This TypeScript module (SpacetimeDB 2.10.2) is the authoritative shared state for a session. It holds:

- sessions, invite codes, role memberships and presence
- the headset-published exercise state and events
- the Jarvis transcript and status
- app-action commands with their acknowledgements
- artifact metadata and transfer/provider grants
- motion jobs with leases and run numbers
- shared replay transport and WebRTC signaling

**All tables are private.** Clients read through views that only return rows for sessions they belong to (`session_*`, `my_*`). Writes go through reducers that check the caller's role. Service identities (the gateway) are the only callers that can issue URLs, verify artifacts or drive jobs. A sweep scheduled every 2 s expires leases, commands, grants and old signals.

No network or file I/O happens in reducers. The gateway in `services/api/` does that work and reports back through service-only reducers.

## Commands

```sh
npm install
npm run build                 # spacetime build
npm run publish:local         # to `spacetime start` on :3000 as `scalpal`
npm run generate              # TS bindings for companion + gateway, C# for Unity
spacetime publish --server maincloud <name>   # hosted (requires `spacetime login`)
```

The identity that first publishes becomes a service identity. Register the gateway with:

```sh
spacetime call [--server local] <db> add_service_identity '"0x<gateway identity hex>"' '"gateway"'
```

## Bindings

- **TypeScript:** `apps/companion/src/module_bindings/` and `services/api/src/module_bindings/`.
- **C# (Unity):** `bindings/csharp/`.

Regenerate after any schema change and commit the result, so consumers build without the CLI.
