# API Service

Owner: Nathan for the assigned companion/backend integration lane. Follow [Nathan's implementation plan](../../docs/nathan-plan.md); no GitHub identity mapping is assumed.

Potential service responsibilities include authorized artifact upload/download, motion-worker integration, and scoped voice authorization. SpacetimeDB in `services/realtime/` is the proposed core shared-state backend. Solana, wallet pairing, challenge payouts, and monetary completion rewards have been removed. This is not an initialized backend and no endpoints are implemented. See [data and realtime proposal](../../docs/data-and-realtime.md) and [current direction](../../docs/current-direction.md) before older plans.

Connect Matthew's existing Jarvis context/actions, Stephen's Quest state/capture metadata, and Silas's replay results. Keep learning results and motion quality separate, and keep provider/storage secrets service-side. Nathan supplies routing and scoped authorization where needed; he does not build a second voice agent or take over the CV pipeline.

The latest recommendation uses one core state backend rather than adding Tiger Data alongside it. Detailed clips and replay artifacts need a restricted file/object store with stable references. A private R2 bucket is a researched candidate; local restricted artifact storage can serve the first test. No store or account is configured by this scaffold.

See the [architecture](../../docs/architecture.md), [integration contracts](../../docs/integration-contracts.md), and [team plan](../../docs/team-plan.md).
