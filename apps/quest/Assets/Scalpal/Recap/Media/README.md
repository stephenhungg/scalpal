# Recap fallback media

`recap-sample.mp4` is an explicitly **synthetic sample**, not a participant recording or the learner's replay. Its permanent burned-in label states this. The same bytes are served by the companion at `/recap-sample.mp4`.

- Generated on October 3, 2026 using `services/motion` at source snapshot `fc0c16260debc99694e25c79e4230bc08843e16b`.
- Known Shadow-hand open → fist → open joint motion is converted to synthetic hand landmarks through robot forward kinematics, passed through the existing dex-retargeting solver, and rendered in MuJoCo. No video perception or participant footage is involved.
- Frames 40–49 (1.333–1.633 seconds; next valid frame at 1.667 seconds) deliberately lack tracking. They hold the last valid pose under an explicit `INJECTED GAP` label. Do not count these as learner mistakes.
- 600 frames, 20 seconds, 30 FPS, 640×480, H.264/yuv420p; no audio. The render uses kinematics only: no physics, task execution, or trained robot policy.
- Robot assets and license provenance: [`assets/robots/README.md`](../../../../../../assets/robots/README.md); Shadow Robot BSD URDF header and vendored dex-urdf Apache 2.0 license are retained in that directory.
- SHA-256: `d3bdb2ba2b26e2ce8fa6af3256c53da61544020c647877184caa821737b47e5b`.

Generate the source render from the repository root:

```sh
cd services/motion
uv run scalpal-motion synthetic --frames 600 --out /tmp/scalpal-recap-synthetic
```

The shipped render additionally burns in a persistent lower banner: `SYNTHETIC SAMPLE - not your recording`, `Shadow hand: generated open / fist / open motion`, and `Kinematic replay, not a trained robot.` The original `tracked` status is replaced with `synthetic input valid`; the injected gap is explicitly labeled. These captions must survive any replacement sample export.

Use the clip as the fallback VideoClip on the native recap panel and as the companion replay fallback. Keep the surrounding UI's sample label visible, retain the actual job's queued/processing/failed state, and never promote this sample to a ready learner artifact. No original learner clip should be fabricated for its comparison panel.

Verification: source generation reported 590/600 valid frames, median retargeting vector error 0.8 mm (p95 1.8 mm), and median joint error 0.020 rad against the synthetic source. These are numerical round-trip measurements, not real-hand reconstruction accuracy. `ffprobe` confirmed the encoding, dimensions, frame count and duration; valid-pose and injected-gap frames were visually inspected. No physical headset playback test has run.
