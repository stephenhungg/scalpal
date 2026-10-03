# Shared Helpers

Owner: Stephen, with component owners contributing scoped helpers.

Add build, launch, validation, and demo helpers only when a working component needs them. The instrument/environment helpers prepare authored assets. `quest/setup_camera_baseline.py` reconstructs the pinned earlier camera experiment, and `quest/native_smoke.py` installs/launches the native workbench and checks app-scoped tracking telemetry. See the [native workbench instructions](../docs/native-workbench.md). Component-specific commands belong with that component unless they coordinate the full system.

Use explicit component working directories, document requirements, and keep secrets and device identifiers out of committed scripts. A helper must report failed checks honestly. See the [team plan](../docs/team-plan.md).

## Repeatable Session Gate

From the repo root, run `python3 scripts/quest/verify_session.py --suite all`. Use `services`, `unity` or `voice` for an affected component. Requirements: Node/npm, SpacetimeDB CLI 2.10.2 on PATH with a local server on port 3000, and Unity 6000.0.66f2 with Android modules (`SCALPAL_UNITY` can override the executable). `services` runs service checks, isolated recorded-fixture coach HTTP/relay failures, and actual SDK reconnect/attempt exchanges on a unique throwaway database. It forces loopback test endpoints rather than inheriting a demo database. It does not contact FinchNode or a voice provider.

`unity` checks the actual native scene, tool input, authored playthrough, coach forwarding and attempt boundaries. The production bridge fault harness uses controlled SDK/transport doubles. `voice` checks native PCM/protocol logic. APK builds run the editor scene checks first; they do not require a running coach/backend/provider.

Add `--headset --config /absolute/private/session-config.json` for USB pairing and installed-player readiness. This restores USB routes after Unity restarts adb. Readiness is not a human playthrough, stereo-alignment test or authenticated voice call. The gate always labels physical playthrough as unverified. See [native session evidence](../docs/native-session.md), [failure harness details](quest/session-check/README.md) and [coach harness details](quest/native-coach-check/README.md).
