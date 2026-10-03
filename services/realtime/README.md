# Realtime Session State

Owner: Nathan, following the user's companion + SpacetimeDB assignment. Follow [Nathan's implementation plan](../../docs/nathan-plan.md). This is a documentation scaffold, not an initialized or deployed module.

Responsibility: authoritative shared session/exercise state, required command acknowledgements, artifact metadata, motion jobs/results, and session-scoped media setup/status if needed. Use identity-scoped access and subscriptions so the Quest, companion website, and Matthew's Jarvis consume consistent state. Live video uses a separate media transport; recordings/full motion files use private file storage; inference belongs in `services/motion/`.

The thin gateway in `services/api/` handles signed storage access, provider authorization, and worker integration. Do not perform network/file/video processing inside transactional reducers.

See [data and realtime proposal](../../docs/data-and-realtime.md), [current direction](../../docs/current-direction.md), and [integration contracts](../../docs/integration-contracts.md). No Solana or completion payouts are part of this backend.
