# Shared Contracts

Merge owner: Stephen. All component owners agree changes before integration.

Potential location for language-neutral schemas and synthetic fixtures covering exercise/session identity, capture manifests, voice tools, and processing/replay results. Reward receipts and onchain challenge contracts have been removed. Await Nathan's plan before finalizing these interfaces; see [current direction](../../docs/current-direction.md). No schemas or generated bindings exist yet.

Start from [integration contracts](../../docs/integration-contracts.md). Define fields, units, coordinate frames, clock domains, and versions for the boundaries actually used. Do not create separate incompatible session structures in every service or require a code-generation framework before one synthetic exchange works.

Consumer-specific adapters can live in the consuming component. Include only synthetic or appropriately licensed nonpersonal fixtures; no participant footage or secrets.
