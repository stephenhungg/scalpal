# native coach regression check

Run `python3 scripts/quest/native-coach-check/run.py` from the repository root. It uses the installed Unity Mono compiler, Node, and the existing `services/preop/node_modules` dependencies. Set `SCALPAL_UNITY_CONTENTS` if Unity is installed elsewhere.

The check compiles the production `CoachRelay` and `CaseRunner` with controlled coroutine/request timing, then exercises the actual Hono coach routes in a temporary loopback server with recorded synthetic health records. The server is stopped in `finally`; no demo session, provider, Unity Editor, headset, microphone, or shared reducer is used.

Coverage includes exact adoption identities, paused untouched sessions, before-step metadata, whole-case parity, lost committed responses, queued batch duplicates, bounded event/ACK retries, command effect deduplication, generation/disable boundaries, stale/unknown step rejection, lost rejection receipts, and terminal command results. The adversarial registration probe documents why the caller must gate local scoring while tracking is invalid.

`NativeCoachRelayValidation.Run()` is a separate scene/geometry/binding check inside Unity. It does not start this HTTP harness.
