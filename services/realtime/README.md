# Realtime Session State

Proposed owner: Nathan, following the user's storage/routing discussion. Reconcile with his incoming plan before implementation. This is a documentation scaffold, not an initialized or deployed SpacetimeDB module.

Recommended responsibility: authoritative shared session/exercise state, required command acknowledgements, artifact metadata, and motion-job/results. Use identity-scoped access and subscriptions so the Quest, observer, and coach consume consistent state. Video bytes and full motion files belong in private file/object storage; inference belongs in `services/motion/`.

The thin gateway in `services/api/` handles signed storage access, provider authorization, and worker integration. Do not perform network/file/video processing inside transactional reducers.

See [data and realtime proposal](../../docs/data-and-realtime.md), [current direction](../../docs/current-direction.md), and [integration contracts](../../docs/integration-contracts.md). No Solana or completion payouts are part of this backend.
