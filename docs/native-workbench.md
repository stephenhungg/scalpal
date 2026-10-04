# Native Quest Workbench

This is the first native full-VR hardware slice, using the existing room/patient art and shared instrument runtime. It is a tool/input test, not an assembled surgical case. The committed entry scene is `Assets/Scalpal/Quest/Scenes/NativeWorkbench.unity` in `apps/quest`.

## What Is Connected

Android OpenXR starts the stereo camera. Head and Touch-controller poses share one floor-space tracking origin. The first valid head pose places the user in front of the authored instrument table; physical movement then changes the head/controller poses within that origin. Blue and gold spheres mark left/right grip poses. There is no locomotion or hand-tracking interaction in this slice.

`NativeWorkbench` gates the existing `XRInstrumentInput` and `TrainingTarget` until XR is running, floor tracking and head pose are valid, and the app is focused. Each hand separately checks controller tracking through the shared instrument interactor. Tracking/focus loss drops held instruments and requires grip release before another pickup. Here `TrainingTarget.RegistrationValid` means a valid authored VR workbench, not a measured body fit. Do not copy this signal into the real-person MR registration path.

Head and controller poses refresh again before rendering, while buttons and simulation actions execute only in Update. A held tool disables Rigidbody interpolation so fixed-step smoothing does not replay an older hand pose; release restores its original physics/interpolation policy. [Unity documents interpolation’s one-physics-update delay](https://docs.unity3d.com/6000.0/Documentation/Manual/rigidbody-interpolation.html).

Grip selects the nearest instrument within the existing pickup radius. Trigger applies its existing authored effect to the practice patch. A resets tool poses, target state and target root pose, including a patch moved by a grasper or retrieval tool. Instrument effects still publish `InstrumentBehaviour.ActionApplied`; this slice only logs them locally. It adds no competing exercise scorer, coach engine, capture route or backend writer.

The static room/patient art is behind the tool station. `AnatomyRoot_Unbound` remains unbound. No organs, Jarvis, case progression, SpacetimeDB, passthrough camera, body registration or recording are connected.

## Build and Deploy

Use Unity **6000.0.66f2** with Android SDK, NDK and OpenJDK modules. Unity Personal is sufficient for this development build. The pinned project uses OpenXR **1.15.1**, XR Management **4.5.4**, Input System **1.17.0**, ARM64 IL2CPP, Vulkan, Android minimum API 32 and target API 34. Package ID: `com.scalpal.nativeworkbench`.

Open the committed scene directly. To regenerate it from the instrument and room sample scenes, use **Scalpal → Quest → Prepare Native Workbench**. This replaces the generated native scene; preserve custom scene edits in a separate scene. The two source samples remain available.

For batch commands, set `UNITY_EDITOR` to your Unity executable and run from the repository root. Keep the APK and logs in the ignored `apps/quest/Builds` directory:

```sh
mkdir -p apps/quest/Builds
SCALPAL_QUEST_APK="$PWD/apps/quest/Builds/native-workbench.apk" \
  "$UNITY_EDITOR" -batchmode -quit -buildTarget Android \
  -projectPath "$PWD/apps/quest" \
  -executeMethod Scalpal.Quest.Editor.NativeQuestBuild.Build \
  -logFile "$PWD/apps/quest/Builds/native-build.log"
python3 scripts/quest/native_smoke.py --apk apps/quest/Builds/native-workbench.apk
```

Connect one Quest with a data-capable USB cable and approve USB debugging. Put on the headset and wake both controllers. Quest may intercept launch with a controllers-required dialog; accept it in the headset. If necessary, open **Scalpal Native Workbench** from Unknown Sources. A successful install or Android launch request alone does not prove XR started.

Run `python3 scripts/quest/native_smoke.py` to launch/read app telemetry without reinstalling. It prints app-scoped records without device serials, clears no logs and saves no camera media. Exit 0 means the latest sampled XR/head/floor/focus state is valid; exit 2 means a required human/device state is pending. `updateHz` is sampled Unity Update cadence, not measured headset display FPS or GPU frame time.

## Physical Test

1. See the room, patient, tool table and instructions in both eyes. Slowly turn your head: geometry should remain stationary in the room.
2. Check the blue/gold dots follow the left/right controllers. Move a dot near the scalpel and squeeze the side grip; the tool should follow the controller. Release grip to drop it.
3. Pick it up again, hold trigger and move the tip along the authored patch seam. Confirm the patch separates and one `SCALPAL_NATIVE_EFFECT` reports `seam_separated`.
4. Press A. Confirm tools and patch return to their starting state. Repeat after moving the patch with a grasper.
5. Open the system menu, then return. Actions should stop during focus loss. Release grip before picking up again. Test a controller tracking loss/recovery separately.

## Evidence and Remaining Work

October 3, 2026: native scene validation passed with 15 unique instruments, two controller adapters, one initially invalid target, materials/scripts and shared-origin bindings checked. A regression check moved the target root and confirmed reset restored its pose and parent. All 157 existing instrument editor checks passed after adding the XR packages. The ARM64 development APK built successfully (247,582,381 bytes), and USB install reported Success on Quest 3S / Android 14. Quest initially intercepted launch with its controllers-required prompt. After the controllers were active, app telemetry reported valid XR/head/floor/focus, both hands tracked, held states for the laparoscope/grasper and a reset event. Sampled steady Update cadence was about 72 Hz; render frame time and total latency were not measured. The user reported tool motion was too slow relative to hand movement. Version 0.1.1-native disables held-body interpolation and adds before-render controller pose refresh. Its 160 instrument checks passed, including immediate moved/rotated grip following and restoration of the original interpolation policy on release; its native scene/reset checks passed and the ARM64 APK built (247,620,483 bytes) and installed. After launch confirmation, version 0.1.1-native emitted valid XR/head/floor/focus, both-hand pickup/release states and applied grasper/dissector effects on the practice patch. The user then confirmed that a held tool keeps up with controller movement. This is a subjective physical retest, not a sensor-to-display latency measurement. Stereo appearance, cutting and reset appearance still require dedicated checks. These are separate evidence levels.

Next connect Matthew's selected anatomy/case assembly to this same origin and instrument core. Follow the [integration map](system-integration.md): choose one scored contact path, one accepted step authority and explicit validity bindings before adding Jarvis/shared-state adapters. Validate real-person MR body fit separately.
