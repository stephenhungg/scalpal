# Quest Application

Owner: Stephen for runtime, capture, registration, bootstrap and Unity configuration; Matthew for anatomy experience, voice client and exercise assets. See the [team plan](../../docs/team-plan.md) and [system integration map](../../docs/system-integration.md) for actual teammate branches, required shared scene bindings and routing checks.

## Open the Current 3D Environment

1. Clone the repository and run `git lfs install` followed by `git lfs pull`.
2. In Unity Hub, add this repository's `apps/quest` directory as a project.
3. Open it with Unity **6000.0.66f2**.
4. Open `Assets/Scalpal/Quest/Scenes/NativeSession.unity` for the integrated appendectomy rehearsal, `Assets/Scalpal/Quest/Scenes/NativeWorkbench.unity` for the isolated native tool test, or `Assets/Scalpal/Instruments/Samples/InstrumentSandbox.unity` for the original editor sample.

The repository contains the complete source for this standalone instrument workbench: `Assets/`, stable `.meta` files, `Packages/manifest.json`, the package lock and `ProjectSettings/`. The native appendectomy session is the enabled build scene; see [native session](../../docs/native-session.md) for services, pairing, controls and current verification. Its table, fifteen virtual instruments, teaching patch, materials, lighting and preview camera are committed. [Blender sources and renders](../../assets/instruments/README.md) are also committed, alongside an optional importable Unity package.

The native workbench now configures Android OpenXR, stereo head/controller tracking, a room/patient backdrop and the shared instrument practice patch. See [build/deploy instructions and physical checks](../../docs/native-workbench.md). This is a standalone native input/tool test; organs, torso registration, passthrough, Jarvis and network adapters remain unconnected. Use [runtime integration](../../docs/instrument-runtime.md) for supported tool effects and gates. Ordinary Play mode does not provide mouse/keyboard pickup controls.

Use **Scalpal → Instruments → Build Prefabs and Sandbox** to regenerate tool assets. Prefab variants should hold authored customizations because the builder replaces its generated outputs.

## Operating-Room Art Preview

Open `Assets/Scalpal/Environment/Samples/OperatingRoomPreview.unity` to inspect the separate static CC0 room/patient scene. Its source models, Blender scene, runtime FBXs, materials, prefabs, metadata and reproducible builder are committed. The separate preview has no organ or XR bindings. The native workbench reuses this art behind its tracked tool station; it does not yet bind tools to patient anatomy. See [two-mode engineering](../../docs/environment-modes.md) and [asset provenance](../../assets/environments/ATTRIBUTION.md).

## Earlier Camera Experiment

The physical-headset camera/bottle test is a separate official Meta sample. Its pinned source, our code modifications and reconstruction instructions are preserved in [experiments/quest-camera-baseline](../../experiments/quest-camera-baseline/README.md). [Hardware evidence](../../docs/hardware-baseline.md) describes what was actually measured. These scenes are not integrated into this workbench.

## Source Control and Shared Interfaces

Commit authored scenes, prefabs, models, scripts, materials, package configuration and project settings. Keep Unity `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/` and build outputs out of Git; Unity regenerates them. Keep credentials, participant footage and identifying device logs out of the source tree. Consume the shared [contracts](../../packages/contracts/README.md); integration contracts remain distinct from existing APIs.
