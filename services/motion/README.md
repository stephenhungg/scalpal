# Motion Reconstruction and Robot Replay

Owner: Silas.

Offline pipeline on the Mac: decode a short permitted passthrough clip, estimate one hand's landmarks with MediaPipe Hand Landmarker, retarget the hand-relative finger motion to a simulated Shadow Dexterous Hand (right or left) with dex-retargeting, and render a labeled kinematic replay in MuJoCo. Frames without a usable hand are kept and reported as invalid. They are never filled with invented motion.

## Status (October 3, 2026)

| Check | Result | Kind |
| --- | --- | --- |
| Known-motion round trip (robot FK of an open/fist/open trajectory, 10-frame gap) | Link vectors recovered to 1.9 mm median, 2.8 mm p95; joints 0.03 rad median; gap reported as its own segment | Simulated |
| Single public MediaPipe sample photo looped into a 1 s video | Hand detected in 30/30 frames; replay shows a mostly open hand with extra fingertip curl, partly from MediaPipe's own 3D estimate (15 to 33° knuckle flex on a palm-facing hand) | Perception smoke test, not motion |
| Real Quest passthrough clip | **Not run yet.** No clip exists | — |
| Real-time / headset display | Out of scope for this slice | — |

| Real moving hand, dex-retargeting's 20 s sample webcam clip (MIT) | 621/621 frames tracked; robot finger bend follows the estimate at r = 0.93 to 0.97 (fingers) and 0.86 (thumb); spread hits Shadow's ±20° abduction limits (flagged) | Real video, not headset |
| Nathan's gateway worker (`gateway-worker`) | Real gateway + SpacetimeDB: upload → job → claim → 4 verified outputs → `ready` → viewer download, 17.5 s for a 20 s clip. Stand-in gateway: no-hand fails without retry, stale run dropped. Trajectory validates against his schema | Local end to end, not deployed |
| Worker job/result boundary (`process`, HTTP `serve`) | Success, no-hand, missing input, bad job, and duplicate run_id paths exercised; a duplicate run never overwrites an existing result; HTTP auth, job, and replay download checked locally | Local test, no gateway yet |

`uv run pytest` covers the round trip, gap reporting, joint naming, fixed wrist, joint limits, job failures and gateway-worker handling. Current merged-source verification: frozen dependencies and 30 tests passed; one external real-hand sample-video test skipped. A prior 60-frame synthetic replay preserved ten explicit gap frames and encoded H.264/yuv420p at 640×480, 30 FPS. These results do not establish actual Quest-video reconstruction.

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

## Nathan's Gateway (integration path)

Nathan's gateway (`nathan/companion-realtime`, `packages/contracts/worker-api.md`) is pull-based. This worker implements it:

```sh
GATEWAY_URL=https://<gateway> WORKER_TOKEN=<token> uv run scalpal-motion gateway-worker
#   --once to handle one job; --mirrored only for selfie/webcam test clips
```

The gateway worker accepts `configVersion: motion-v1`; other versions fail without retry before download/inference. `--hand` and `--mirrored` are worker-wide settings, not per-job options. Their effective values are recorded in the quality report. Additional capture manifests are not yet consumed; configuration identity/manifest support needs a shared contract before these jobs can be described as reproducible capture processing.

For each job it claims, downloads the clip from the signed URL, runs inference, retargeting, and rendering, and heartbeats every lease/3. It then uploads four outputs and completes with the contract's `quality` fields:
- `robot_trajectory` in `scalpal.robot_trajectory.v1`, the format the companion replay view reads
- `replay_video`
- `hand_estimates`
- `quality_report`

When no hand is found or the clip won't decode, it fails without retry. Download and processor errors fail with retry. The download stops at the claim's `bytes` (or `SCALPAL_MOTION_MAX_INPUT_BYTES`, default 2 GiB), and a clip whose size or sha256 differs from the claim fails without retry before inference. Failure reports are retried with backoff on network errors and 5xx; if the gateway stays unreachable the worker logs it, leaves the run to lease expiry and keeps polling. A `409` at any point drops the run without completing.

**Teammate-reported verification against Nathan's real gateway** (`nathan/companion-realtime` at 9bd6517, local SpacetimeDB 2.10.2, local storage). A headset client uploaded the 20 s sample hand clip and the operator requested a job. This worker claimed it, tracked 621/621 frames, and uploaded 4 outputs that the gateway verified. The job became `ready`, and a viewer downloaded a schema-valid trajectory (24 joints × 621 frames). Worker wall time was 17.5 s.

To rerun it, copy `integration/silas-e2e.test.ts` into `services/api/test/` on Nathan's branch. With `spacetime start` running, run `E2E_CLIP=<clip> MOTION_DIR=<this folder> node --import tsx --test test/silas-e2e.test.ts`. The stand-in gateway tests in `tests/test_gateway_worker.py` cover the failure paths (no hand, stale run) without SpacetimeDB.

Current consolidated-source boundary verification also ran the real gateway, SpacetimeDB and worker on a fresh throwaway database with a generated ten-frame blank clip. Signed upload/hash verification and inference reached final no-hand failure on run 1 with zero outputs, while a previously completed learning result stayed completed. This proves that failure route, not real-hand reconstruction. The worker defaults to local gateway port 8788, the API gateway's default (preop/coach is 8787); override with `GATEWAY_URL` or `--gateway` for hosted routing.

## Local Worker Boundary (standalone)

For runs without the gateway, the same pipeline is available as a job-in, result-out command and a small HTTP server.

```sh
uv run scalpal-motion process examples/job.example.json       # one job file -> out/runs/<run_id>/
SCALPAL_MOTION_TOKEN=... SCALPAL_MOTION_INPUT_DIR=~/clips uv run scalpal-motion serve --port 8765
#   POST /jobs                body: job JSON -> result JSON (synchronous, one job at a time)
#   GET  /runs/<run_id>/<f>   hand_track.json | motion.json | replay.mp4 | result.json
#   GET  /health
```

The server always requires `Authorization: Bearer <token>`. Without `SCALPAL_MOTION_TOKEN` it prints a random token for that run. Served jobs may only read `input.source` from:
- URLs under `SCALPAL_MOTION_ALLOWED_URL_PREFIXES` (comma-separated; default the local gateway's signed file route, `http://localhost:8788/files/` and `http://127.0.0.1:8788/files/`). Scheme and host:port must match exactly, and redirects are refused.
- Local paths under `SCALPAL_MOTION_INPUT_DIR`, after resolving symlinks. When it is unset, local paths are refused.

Inputs larger than `SCALPAL_MOTION_MAX_INPUT_BYTES` (default 2 GiB) are refused. `scalpal-motion process` runs your own job file and is not restricted, though URL downloads are still capped.

- **Job** (`scalpal.motion_job/0`, [example](examples/job.example.json)): `job_id`, `run_id`, optional `attempt_id`, `input.artifact_id`, `input.source` (local path or signed http(s) URL), and optional `config` (`hand`, `mirrored`, `smooth`).
- **Result** (`scalpal.motion_result/0`, [example](examples/result.example.json)): echoes the job/run/attempt/artifact IDs and has `status` `ready` or `failed`. On failure, `error.code` is one of `bad_job`, `unsupported_config`, `input_unavailable`, `input_not_allowed`, `input_too_large`, `decode_failed`, `no_hand_detected`, `run_exists`, or `processor_error`. The result also carries processor versions, the config used, `quality` (valid fraction, longest valid segment, vector error, segments), and `artifacts` with kind, content type, size, and sha256.
- Run IDs start with an ASCII letter or digit, contain only ASCII letters/digits, `-`, `_`, or `.`, and have at most 128 characters. Invalid IDs are rejected rather than sanitized; output reads reject paths or symlinks outside the output root.
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
- One hand per job (`--hand Right|Left`, right by default). Both Shadow hands are vendored, and the left was checked on a mirrored copy of the sample clip (finger bend r 0.87 to 0.98).
- No physics, object contact, or task outcome. This is joint-target playback, not a learned policy.
