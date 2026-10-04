# Encounter Boundary Check

`live-exchange.ts` runs a real loopback HTTP server with recorded fictional FinchNode fixtures, the production `EncounterSession` routes, the production `RealtimeBridge`, and actual SpacetimeDB subscriptions. It verifies both adult demo characters, gathered action events, distinct patient transcript role, attending transitions, deterministic score propagation, rejected late actions, and unjoined read isolation.

Run after publishing the current realtime module to a **new throwaway** `scalpal-test-*` database on the pinned SpacetimeDB **2.10.2** local server. The script refuses a remote server or a database outside that prefix. It creates fresh identities and its own session, ends that session, and removes its temporary coach token. It does not publish, delete/reset data, load developer `.env` files, authenticate providers, or capture participant data.

```sh
npm ci --prefix services/preop
TEST_SPACETIMEDB_URI=ws://127.0.0.1:3000 \
TEST_SPACETIMEDB_DB=scalpal-test-your-fresh-database \
node services/preop/node_modules/tsx/dist/cli.mjs scripts/quest/encounter-check/live-exchange.ts
```

Success emits `SCALPAL_ENCOUNTER_EXCHANGE_OK checks=30`. This is real service-boundary evidence with synthetic cases, not physical Quest, microphone, WSS, ElevenLabs speech, or end-to-end participant evidence. The service component tests additionally verify unknown exam findings, invalid payloads, phase freezing, synthetic-only source/demographic matching, and prompt grounding.

The native office client uses the same routes: create with `POST /encounters`, interview through `answer`, `examine`, and `order_test`, switch through `POST /encounters/:id/attending`, then call `record_assessment` with string `diagnosis`, `procedure`, `urgency`, and string-array `differential`. Read `/score` after assessment. Invalid arguments return 400; actions outside their phase return 409. Patient `result` text is a tool instruction for the patient role; use the additive `display` field for the visual fallback. Clinical findings/results are in `state.exams` and `state.tests`. Jarvis summary includes only facts actually returned during this interview. A fresh encounter is required for another attempt.
