# API Service

Latest proposed owner: Nathan for routing/storage integration. Reconcile with his incoming plan before implementation; no GitHub identity mapping is assumed.

Potential service responsibilities include authorized artifact upload/download, motion-worker integration, and scoped voice authorization. SpacetimeDB in `services/realtime/` is the proposed core shared-state backend. Solana, wallet pairing, challenge payouts, and monetary completion rewards have been removed. This is not an initialized backend and no endpoints are implemented. See [data and realtime proposal](../../docs/data-and-realtime.md) and [current direction](../../docs/current-direction.md) before older plans.

The earlier ownership plan connected exercise content with Matthew, capture metadata with Stephen, and replay results with Silas. These assignments remain provisional pending Nathan's plan. Keep learning results and motion quality separate, and keep provider secrets service-side.

The latest recommendation uses one core state backend rather than adding Tiger Data alongside it. Detailed clips and replay artifacts need a restricted file/object store with stable references. A private R2 bucket is a researched candidate; local restricted artifact storage can serve the first test. No store or account is configured by this scaffold.

See the [architecture](../../docs/architecture.md), [integration contracts](../../docs/integration-contracts.md), and [team plan](../../docs/team-plan.md).
