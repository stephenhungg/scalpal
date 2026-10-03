# Agent Instructions

## Communication

- Write casual chat updates clearly and in lowercase, without emojis. Preserve correct capitalization in code, paths, technical names, and professional documents.
- Lead with the outcome. Explain meaningful decisions and tradeoffs; do not narrate every tool operation.
- Ask for decisions when missing information materially changes correctness, architecture, or an irreversible action. Make reasonable assumptions for routine reversible work.

## Read Before Working

Read `README.md` and `docs/current-direction.md` first, then the thesis, decisions, team plan, and your component document. Current direction supersedes older removed-feature proposals. Check the repository before editing: this handoff contains documentation and folder scaffolds, and the hardware results come from a separate official Meta sample. The latest user assigned Nathan the companion website + SpacetimeDB/routing lane; follow `docs/nathan-plan.md` for that implementation work order. Matthew is already working on Jarvis. The earlier blanket wait is superseded for Nathan's assigned lane; do not imply the remaining open product decisions or unbuilt integrations are resolved.

User direction takes precedence over older proposals. In particular:

- Use a real reclining participant and a generic teaching anatomy overlay.
- Selection is conversational, with a rotating 3D anatomy preview during selection.
- The robotics input proposal is recorded passthrough video, not Meta SDK hand-joint telemetry.
- First demonstrate video-derived hand motion retargeting and replay in simulation. Do not describe replay as a learned autonomous policy.
- Preserve both education and robotics goals. Do not silently replace the project with a pure analytics dashboard or robotics-only collector.
- Solana, wallets, onchain challenges, payouts, and monetary completion rewards are removed. Do not implement the historical reward lane. Nathan owns the companion/realtime routing assignment; Matthew owns the single Jarvis voice agent.

## Implementation Discipline

- Identify what result would demonstrate success before implementing a component.
- Build the simplest complete slice for one exercise. Avoid speculative catalogs, custom neural models, dashboards, and multiple competing databases.
- Delegate bounded independent work when useful, with explicit file ownership. Avoid overlapping edits.
- Coordinate shared schemas and state transitions through `docs/integration-contracts.md`; proposed contracts are not existing APIs.
- Preserve unrelated work and inspect the final diff. Choose verification based on behavior and risk, and never claim a check passed unless it ran.
- State whether a result is measured on the headset, reported by a participant, simulated, or merely proposed.
- Keep camera-image coordinates, camera-relative estimates, Unity world coordinates, and robot coordinates explicit. Do not conceal missing depth, clock synchronization, or registration behind smoothing.
- Pause scoring and hide misleading anatomy when registration is invalid. Handle missing video landmarks explicitly in replay rather than inventing precise movement.
- Use authored exercise rules for feedback and assessment. Voice agents do not execute arbitrary scene code.
- Keep secrets on the service side. Never commit treasury keys, provider credentials, participant footage, raw motion datasets, or identifying device logs.
- Camera permission and participant consent do not by themselves establish rights to export, license, or train on platform data. Consult the primary sources in `docs/research/README.md` for the intended use.

## Status and Handoff

The repository/context and team-layout requests authorize this handoff and scaffold, not implementation of every proposed component. Follow subsequent user-assigned scope. Before a broad build, reconcile the expanded architecture and unresolved decisions with the owner; do not treat the prior pre-robotics draft review as approval of the current specification.

For completed work, report changed files, relevant checks, measured limitations, and the next blocked interface or decision. Update the relevant document when evidence changes a premise. Do not add a tracking file for every small task.
