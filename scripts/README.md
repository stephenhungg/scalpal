# Shared Helpers

Owner: Stephen, with component owners contributing scoped helpers.

Add build, launch, validation, and demo helpers only when a working component needs them. The instrument/environment helpers prepare authored assets. `quest/setup_camera_baseline.py` reconstructs the pinned earlier camera experiment, and `quest/native_smoke.py` installs/launches the native workbench and checks app-scoped tracking telemetry. See the [native workbench instructions](../docs/native-workbench.md). Component-specific commands belong with that component unless they coordinate the full system.

Use explicit component working directories, document requirements, and keep secrets and device identifiers out of committed scripts. A helper must report failed checks honestly. See the [team plan](../docs/team-plan.md).
