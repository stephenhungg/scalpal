# Surgery State Tracking

Approved plan, October 3, 2026. How the operating room knows what is happening so Jarvis and the grader get exact context without relying on video. Evidence: [surgery state tracking research](research/surgery-state-tracking.md). Body model: [surgery procedure](surgery-procedure.md#architecture-open-body-cases-on-top).

## Design

1. **Unity is the single producer and local authority.** Every interaction emits a numbered event (`seq` per attempt): scored body actions (`<instrument, verb, tissue>` plus measurements, already `BodyAction`) and non-scoring telemetry (instrument picked up/put down per hand, tip contact, critical-structure proximity bucket on change, AR registration validity and age). A deterministic reducer (`BodyState.cs`, mirror of `open-body.ts`) applies them locally, so gating and scoring never wait on the network. While any bleed is active, a 1 Hz `tick` event runs through the same reducer so blood loss accrues during stalls.
2. **One derived surgery state** per version: phase and completed milestones; the next milestone with its unmet conditions in plain words; hazards (active bleeds and rate, blood lost, contamination, nearest critical structure); each hand's instrument and what it touches; time in step and stall time; last eight actions; errors and hints; AR fit validity. Each version carries a hash of the body facts plus schema, case and reducer versions. Values are rounded at the event boundary so C# and TypeScript hash identically.
3. **Sync with a checker.** Unity batches events to the coach service; the service replays them through `open-body.ts`. A hash mismatch or a `seq` gap triggers a full-state resync, never a silent divergence. A small summary row goes to SpacetimeDB for the companion. Shared golden event logs run in CI through both reducers and must produce identical hashes.
4. **Jarvis context.** A ~120-token state card: hazards first, next milestone and what is missing in the middle, latest actions last. Sent in full on every milestone or phase change and about every 10 s, with one-line deltas between. `get_surgery_state` returns the full table on demand. Safety alerts play a pre-rendered local clip immediately; a spoken alert is added only if the state version still shows the hazard. Video (`look_at_scene`) can support but never decide state.
5. **Grader** replays the same event log at the end, so the recap can never disagree with what Jarvis said live.
6. **Time-window guardrails** as case data (for example, an uncontrolled bleed over 30 s), evaluated by the same detectors.

## Ownership and Status

| Piece | Owner | Status |
| --- | --- | --- |
| Unity events, `seq`, 1 Hz tick, telemetry events, derived state, hash, golden logs | Surgery Codex thread | Partly landed: one committed action per blade/marker stroke, rough handling once per tool+tissue per 3 s, 1 Hz tick only while bleeding, change-only ≤1 Hz fluid snapshots, `BodyAction.Quantize` at the event boundary, and one shared golden log (`services/preop/test/fixtures/open-body-golden.json`) replayed by vitest and `OpenBodyValidation` with exact fact equality. `seq`, hash and resync remain pending; until then a body action the coach drops as tracking-invalid fails relay sync. |
| State card with unmet milestone facts, hands line, `get_surgery_state` | Matthew (`services/preop/src/coach.ts`) | Landed in `9501b8b` |
| Full card on structural change or about every 10 s, `[STATE DELTA vN]` lines between (`services/preop/src/jarvis/context-feed.js`); queued bleeding alerts dropped once the state no longer shows the bleed | Matthew; Quest side Stephen | Landed: laptop page in the browser, native Quest voice through `POST /coach/sessions/:id/voice-context` (`916215e`) |
| Service-side replay checker, hash compare and resync, versioned alert drop, time-window guardrails | Matthew, coordinated with the surgery thread | Pending |
| Retire the legacy Unity `bleeding` totals for open-body cases (`NativeVesselSimulation` feeding blood loss separately) | Main Codex thread with the surgery thread | Pending |
