# Motion Reconstruction and Robot Replay

Owner: Silas.

Offline pipeline on the Mac: decode a short permitted passthrough clip, estimate one hand's landmarks with MediaPipe Hand Landmarker, retarget the hand-relative finger motion to a simulated Shadow Dexterous Hand (right) with dex-retargeting, and render a labeled kinematic replay in MuJoCo. Frames without a usable hand are kept and reported as invalid. They are never filled with invented motion.

## Status (October 3, 2026)

| Check | Result | Kind |
| --- | --- | --- |
| Known-motion round trip (robot FK of an open/fist/open trajectory, 10-frame gap) | Link vectors recovered to 1.9 mm median, 2.8 mm p95; joints 0.03 rad median; gap reported as its own segment | Simulated |
| Single public MediaPipe sample photo looped into a 1 s video | Hand detected in 30/30 frames; replay shows a mostly open hand with extra fingertip curl, partly from MediaPipe's own 3D estimate (15 to 33° knuckle flex on a palm-facing hand) | Perception smoke test, not motion |
| Real Quest passthrough clip | **Not run yet.** No clip exists | — |
| Real-time / headset display | Out of scope for this slice | — |

`uv run pytest` covers the round trip, gap reporting, joint naming, fixed wrist, and joint limits.

## Setup and Use

Requires [uv](https://docs.astral.sh/uv/). Python 3.11 is pinned; the hand model downloads to `models/` on first run.

```sh
cd services/motion
uv sync
uv run pytest
uv run scalpal-motion synthetic                     # known-motion test -> out/synthetic/
uv run scalpal-motion run path/to/clip.mp4          # clip -> out/<clip name>/
uv run scalpal-motion run clip.mp4 --hand Right --smooth 0.3
```

`run` writes `hand_track.json`, `motion.json`, and `replay.mp4` (source clip with landmarks next to the robot). Inputs are assumed unmirrored, like Quest passthrough. Pass `--mirrored` for selfie footage, since MediaPipe's handedness label assumes a mirrored image. Smoothing is off by default. With `--smooth`, the low-pass filter resets after every tracking gap. `out/` and `models/` are gitignored. Keep participant clips outside the repo.

## Method and Choices

- **Perception:** MediaPipe Tasks Hand Landmarker, VIDEO mode, CPU, `mediapipe` pinned to 0.10.x. Version 1.0.1 aborts on Apple Silicon in its Metal helper, even when the CPU delegate is selected.
- **Hand frame:** World landmarks are re-expressed in a wrist frame from landmarks 0/5/9 (MANO convention, from the dex-retargeting example). Wrist translation and orientation are discarded, so the robot's `WRJ1`/`WRJ2` stay fixed at 0. Leaving them free let the solver bend the wrist -26° against +40° of finger flex.
- **Retargeting:** dex-retargeting vector optimization over the 22 finger/thumb joints. Fingertip and middle-link vectors run from each finger's knuckle, and thumb vectors from the palm. Each vector is scaled by the ratio of robot extended length to human bone-chain length, measured as the clip median. That ratio doesn't change with flexion. The default single 1.2× scale left Shadow's longer fingers curled on a flat hand.
- **Applied commands:** Optimizer output is clipped to URDF limits (dex-retargeting pads its bounds by 1e-3 rad). Joints at a limit are listed per frame.
- **Replay:** MuJoCo 3.x loads the URDF directly and only the visual geoms are rendered. Joint positions are set by name and forward kinematics runs, with no dynamics, contacts, or controller. Invalid frames hold the last valid pose under a red NO TRACKING label.

## Output Records (proposed, version 0)

These are proposals for the capture and replay boundaries in [integration contracts](../../docs/integration-contracts.md). They are not yet in `packages/contracts/`, which Stephen merges.

`hand_track.json` (`scalpal.hand_track/0`): source video name, size, reported fps, and the time domain (decoded presentation time, ms from clip start). Also the model name/version and thresholds, target hand, and mirrored flag. Each frame has `frame`, `t_ms`, `valid`, `reason` (`no_hand` / `other_hand_only`), `handedness_score`, `image` (21 normalized x/y/z), and `world` (21 points in meters, hand-centered, *not* camera or Quest world coordinates).

`motion.json` (`scalpal.robot_motion/0`): robot name, URDF, and root frame, plus the retargeting config version, per-vector scales, smoothing, and fixed joints. Also `joint_names` (order of every `qpos`), `units: radians`, `joint_limits`, and `control_mode`. Each frame has `frame`, `t_ms`, `valid`, `qpos` (null when invalid), `at_limit`, and `vector_error_m`. `summary` holds valid frame count and fraction, contiguous valid/invalid `segments` with times, and median/p95 vector error.

`vector_error_m` measures how well the robot reached the *scaled human vectors*. It doesn't measure how accurately MediaPipe reconstructed the real hand. Nakim's verifier can use `valid_fraction`, the longest valid segment, and the vector error as contribution checks. Thresholds should come from measured clips.

## Needs From Other Lanes

- **Stephen (capture):** a short permitted raw passthrough clip (mp4 is fine) of one hand opening and closing with a simple wrist turn, fingers visible. Also its capture manifest: camera source, crop, intrinsics, frame timestamps and their clock, and dropped frames. The pipeline only uses decoded presentation time until those exist.
- **Nakim (results):** agreement on the `motion.json` summary fields above and where the job input/output artifacts live.
- **Team:** where the replay is shown. `replay.mp4` already works on the laptop; a headset panel would need a Unity renderer for the same joint stream.

## Not Done / Limits

- No real headset clip yet, so accuracy on occluded first-person footage is unmeasured.
- Monocular estimates: metric depth, wrist position, and wrist orientation are not reconstructed. Finger poses inherit MediaPipe's 3D errors, which are clearly visible on foreshortened, palm-facing hands.
- Only the right hand. A left Shadow hand exists in dex-urdf and would need a mirrored config.
- No physics, object contact, or task outcome. This is joint-target playback, not a learned policy.
