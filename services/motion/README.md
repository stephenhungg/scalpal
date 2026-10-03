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

| Worker job/result boundary (`process`, HTTP `serve`) | Success, no-hand, missing input, bad job, and duplicate run_id paths exercised; a duplicate run never overwrites an existing result; HTTP auth, job, and replay download checked locally | Local test, no gateway yet |

`uv run pytest` covers the round trip, gap reporting, joint naming, fixed wrist, joint limits, and the job failure paths.

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

`run` writes `hand_track.json`, `motion.json`, and `replay.mp4` (H.264, plays in browsers when ffmpeg is installed) (source clip with landmarks next to the robot). Inputs are assumed unmirrored, like Quest passthrough. Pass `--mirrored` for selfie footage, since MediaPipe's handedness label assumes a mirrored image. Smoothing is off by default. With `--smooth`, the low-pass filter resets after every tracking gap. `out/` and `models/` are gitignored. Keep participant clips outside the repo.

## Worker Boundary for Nathan's Gateway

The processor is a job-in, result-out worker. Transport and storage stay Nathan's call; both entry points below share the same code.

```sh
uv run scalpal-motion process examples/job.example.json       # one job file -> out/runs/<run_id>/
SCALPAL_MOTION_TOKEN=... uv run scalpal-motion serve --port 8765
#   POST /jobs                body: job JSON -> result JSON (synchronous, one job at a time)
#   GET  /runs/<run_id>/<f>   hand_track.json | motion.json | replay.mp4 | result.json
#   GET  /health
```

- **Job** (`scalpal.motion_job/0`, [example](examples/job.example.json)): `job_id`, `run_id`, optional `attempt_id`, `input.artifact_id`, `input.source` (local path or signed http(s) URL), and optional `config` (`hand`, `mirrored`, `smooth`).
- **Result** (`scalpal.motion_result/0`, [example](examples/result.example.json)): echoes the job/run/attempt/artifact IDs and has `status` `ready` or `failed`. On failure, `error.code` is one of `bad_job`, `unsupported_config`, `input_unavailable`, `decode_failed`, `no_hand_detected`, `run_exists`, or `processor_error`. The result also carries processor versions, the config used, `quality` (valid fraction, longest valid segment, vector error, segments), and `artifacts` with kind, content type, size, and sha256.
- **Run isolation:** outputs go to `<output-root>/<run_id>/`. A repeated `run_id` is refused with `run_exists` and never touches the existing files, so a retry needs a new `run_id`. Deciding which run is current stays with SpacetimeDB.
- **Not decided here:** learning completion and whether a clip counts as a useful contribution. The worker reports quality, and acceptance thresholds should come from measured clips.
- **For the companion:** `replay.mp4` is the display artifact (source with landmarks next to the robot, H.264). `motion.json` has named joint angles per frame if a 3D web viewer is wanted later.

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

`vector_error_m` measures how well the robot reached the *scaled human vectors*. It doesn't measure how accurately MediaPipe reconstructed the real hand.

## Needs From Other Lanes

- **Stephen (capture):** a short permitted raw passthrough clip (mp4 is fine) of one hand opening and closing with a simple wrist turn, fingers visible. Also its capture manifest: camera source, crop, intrinsics, frame timestamps and their clock, and dropped frames. The pipeline only uses decoded presentation time until those exist.
- **Nathan (routing/companion):** confirm or change the job/result shapes above, how the gateway reaches the worker (HTTP or job file), and where inputs and outputs are stored. The companion can show `replay.mp4` directly.
- **Team:** whether a headset replay panel is wanted too. That would need a Unity renderer for the same joint stream.

## Not Done / Limits

- No real headset clip yet, so accuracy on occluded first-person footage is unmeasured.
- Monocular estimates: metric depth, wrist position, and wrist orientation are not reconstructed. Finger poses inherit MediaPipe's 3D errors, which are clearly visible on foreshortened, palm-facing hands.
- Only the right hand. A left Shadow hand exists in dex-urdf and would need a mirrored config.
- No physics, object contact, or task outcome. This is joint-target playback, not a learned policy.
