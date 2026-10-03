# Quest Application

Owner: Stephen for runtime, capture, registration, bootstrap and Unity configuration; Matthew for anatomy experience, voice client and exercise assets. See the [team plan](../../docs/team-plan.md).

## Open the Current 3D Environment

1. Clone the repository and run `git lfs install` followed by `git lfs pull`.
2. In Unity Hub, add this repository's `apps/quest` directory as a project.
3. Open it with Unity **6000.0.66f2**.
4. Open `Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity`.

The repository contains the complete source for this standalone instrument workbench: `Assets/`, stable `.meta` files, `Packages/manifest.json`, the package lock and `ProjectSettings/`. The sandbox is the enabled build scene. Its table, fifteen virtual instruments, teaching patch, materials, lighting and preview camera are committed. [Blender sources and renders](../../assets/instruments/README.md) are also committed, alongside an optional importable Unity package.

This is an initialized Unity editor project with built-in physics/XR modules, not yet a configured native Quest application. No OpenXR/Meta XR loader, headset rig, passthrough layer, Android platform setup, torso registration, complete surgery environment or Jarvis session is configured here. The controller adapters need the actual headset rig; the sandbox preview camera and placeholder tracking origin do not start XR. Use [runtime integration](../../docs/instrument-runtime.md) for the supported tool effects and gates. Ordinary Play mode does not provide mouse/keyboard pickup controls.

Use **Scalpal → Instruments → Build Prefabs and Sandbox** to regenerate tool assets. Prefab variants should hold authored customizations because the builder replaces its generated outputs.

## Operating-Room Art Preview

Open `Assets/Scalpal/Environment/Samples/OperatingRoomPreview.unity` to inspect the separate static CC0 room/patient scene. Its source models, Blender scene, runtime FBXs, materials, prefabs, metadata and reproducible builder are committed. The room/patient are not yet bound to organs, tools or an XR rig; the instrument sandbox remains the enabled build scene. See [two-mode engineering](../../docs/environment-modes.md) and [asset provenance](../../assets/environments/ATTRIBUTION.md).

## Earlier Camera Experiment

The physical-headset camera/bottle test is a separate official Meta sample. Its pinned source, our code modifications and reconstruction instructions are preserved in [experiments/quest-camera-baseline](../../experiments/quest-camera-baseline/README.md). [Hardware evidence](../../docs/hardware-baseline.md) describes what was actually measured. These scenes are not integrated into this workbench.

## Source Control and Shared Interfaces

Commit authored scenes, prefabs, models, scripts, materials, package configuration and project settings. Keep Unity `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/` and build outputs out of Git; Unity regenerates them. Keep credentials, participant footage and identifying device logs out of the source tree. Consume the shared [contracts](../../packages/contracts/README.md); integration contracts remain distinct from existing APIs.
