# Quest Camera Baseline Source

This preserves the local source changes from the official Meta passthrough camera sample used for the earlier physical Quest tests. It is a separate experimental project, not the Scalpal surgery environment or an integrated application.

The upstream project is pinned in [SOURCE.json](SOURCE.json). Its scenes, prefabs, models and package configuration remain available in that exact upstream revision. [sample-changes.patch](sample-changes.patch) preserves our inference/placement telemetry and Android development settings. [overlay](overlay/) preserves the added camera diagnostic component, build entry point and Unity metadata. The upstream [license notice](UPSTREAM-LICENSE.txt) is retained; this Meta sample uses the Oculus SDK License Agreement, separately from the instrument kit's MIT-licensed LapGym parts.

## Reconstruct the Experiment

Install Git LFS, then run from the Scalpal repository root:

```bash
python3 scripts/quest/setup_camera_baseline.py
```

This downloads the pinned upstream project, checks out its Git LFS assets, applies the preserved source changes and copies the added assets into `.checkout/`. It refuses an existing destination to preserve local work. Open that directory in Unity 6000.0.66f2. The script does not build, install, start camera capture or change the main Scalpal project. The local checkout, Unity caches, APKs and captured data are excluded from this repo.

`QuestProbeBuild.Build` requires an absolute `QUEST_PROBE_APK` environment variable and the Android build-support modules. `StartScene` remains first so the sample's permission flow is preserved. The diagnostics initialize only in development builds and expect an optional HTTP receiver at `localhost:8098` reached through an explicit USB reverse tunnel. The receiver is not bundled here. Without it, the app produces throttled network warnings; the upstream camera scenes still supply the baseline. The diagnostic code can send JPEG camera previews when such a receiver is connected; these are explicitly unsynchronized previews, not synchronized motion-training recordings.

The patch is verified against the pinned upstream source. This repository publication does not repeat the earlier headset measurements or establish Scalpal torso registration. See [hardware evidence](../../docs/hardware-baseline.md).
