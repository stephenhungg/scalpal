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

## Quest Controllers Drive the Robot Hand (current demo path)

Surgery in the headset is done with Quest controllers, so controller motion is the robot input. There is no hand camera and no passthrough video in this path. The Quest's `ControllerMotionCapture` (`apps/quest/Assets/Scalpal/Robotics`) streams `scalpal.controller_motion.v1` frames over UDP: each controller's tracked pose (relative to the registered `PatientRoot` when set), grip, trigger, and held instrument.

The mapping onto the floating Shadow hand of the instrument-transfer task (`learning/env.py`, physics on):
- **Position:** controller position moves the grip point. The first tracked pose anchors to the hand's home.
- **Yaw:** the controller's heading turns the hand.
- **Fingers:** per finger, exactly like the VR gloves (table below).

```sh
uv run scalpal-motion teleop --consented          # window: robot hand follows the controller; each attempt saved to out/teleop
uv run scalpal-motion send-controller             # stand-in headset: a scripted reach-and-place over UDP
uv run scalpal-motion learn sweep --teleop out/teleop --n 1,5,10   # train and evaluate on the teleop demos
```

### Finger mapping (matches the gloved hands in the Quest build)

Stephen's gloves (`apps/quest/Assets/Scalpal/Quest/Runtime/ControllerHandPose.cs`) and the robot hand read the same controller inputs the same way, so what the gloved hand does in VR is what the Shadow hand does in sim:

| Controller input | Glove in VR | Shadow hand actuators |
| --- | --- | --- |
| Trigger | index curls | `FFJ4`, `FFJ3`, `FFJ0` blend open to grasp by trigger |
| Grip | middle, ring, pinky curl | `MF*`, `RF*`, `LF*` blend open to grasp by grip |
| Either | thumb follows the harder of the two | `TH*` blend by max(grip, trigger) |
| Instrument held (`heldInstrument` non-empty) | fist closes on the handle | every finger at the full grasp, regardless of grip/trigger |

"Open" is the pre-shape and "grasp" is the closed shape validated in physics (`learning-results/human_profiles.json`). The glove's 0.2 rest curl is cosmetic and is not copied, so an idle controller leaves the robot hand open. Code: `finger_curls` and `curl_vector` in `scalpal_motion/teleop.py`; test: `tests/test_teleop.py::test_finger_mapping_matches_the_quest_gloves`.

Successful attempts become training demos with a real wrist path and grip timing. Lost tracking holds the last command instead of inventing motion. Recording requires `--consented`. The Shadow hand model is not committed: fetch it once (see `scalpal_motion/learning/README.md`).

Verified on an M2 MacBook (branch `matthew/preop-finchnode`, carried to main):
- **Network path:** a scripted controller reach-and-place sent over UDP placed the handle and saved the attempt.
- **Without the network:** 5 of 5 scripted seeds placed the handle.
- **Training data:** two such episodes, converted to demos, generated training data at 55% physics yield.
- **Not yet run:** a real headset session. The capture compiles in the .NET check but has not run on a Quest.

### Surgery-step labels from the coach

```sh
uv run scalpal-motion teleop --consented --coach http://127.0.0.1:8787                 # follow the newest coach session
uv run scalpal-motion teleop --consented --coach http://127.0.0.1:8787 --session coach-<id>
uv run scalpal-motion teleop --consented --coach http://127.0.0.1:8787 --patient patient-demo-sparse
```

A background thread asks the coach (`GET /coach/current`, then `GET /coach/sessions/:sid`) about every 250 ms. Every 20 Hz control frame of an attempt is stamped with the newest answer: `sessionId`, `procedureId`, `stepId`, `stepTitle`, `stepNumber`, `held` instruments, case `outcome`, plus `age_s` and `stale` (older than 2 s). Frames are stamped `null` while the coach is down or has no session; teleop never waits on it. Attempts also log the measured wrist pose (`wrist`), the 22 finger joint angles (`joints`) and frame time (`t`). The window shows the current step.

When an attempt is saved and `--coach` is set, a `scalpal.robot_attempt.v1` summary is POSTed to `/coach/sessions/:sid/robot-attempts` in the background (step, steps during the attempt, held tools, success, frames, duration, max lift). Failures are printed and ignored; `--no-report` turns it off. The route is proposed and not in `services/preop` yet, so today the coach answers 404 and only the local file is written.

### Export to a LeRobot-style dataset

```sh
uv run scalpal-motion export-lerobot out/teleop --out out/lerobot/scalpal_robot_hand              # JSON Lines data files
uv run --with pyarrow scalpal-motion export-lerobot out/teleop --out out/lerobot/scalpal_robot_hand # Parquet, as LeRobot reads it
```

No LeRobot install. It writes the LeRobotDataset v2.1 folder layout:

```
meta/info.json               fps 20, features, counts, data_path template
meta/tasks.jsonl             one task per surgery step title ("instrument transfer" for unlabeled frames)
meta/episodes.jsonl          episode_index, tasks, length, success
meta/episodes_stats.jsonl    per-episode min/max/mean/std/count
meta/scalpal_episodes.jsonl  source attempt, coach session, step ids, held tools, case outcome, labeled fraction
data/chunk-000/episode_000000.parquet  (or .jsonl without pyarrow)
```

Per frame: `observation.state` float32[26] = measured wrist x, y, z, yaw + 22 finger joints; `action` float32[22] = commanded grip point x, y, z, yaw + 18 finger actuator targets; `timestamp` (sim time, frame/20), `teleop_time_s` (wall clock), `next.done`, `next.success`, `frame_index`, `episode_index`, `index`, `task_index` (the step title at that frame). `--success-only` keeps attempts that placed the handle. Attempts recorded before state logging (no `wrist`) are skipped, not filled in. Not checked against an installed `lerobot` loader; videos are absent (`total_videos` 0) because there is no camera.

**What is human data here and what is not.** The committed learning result (`learning-results/`, 96.7% policy vs 20% replay) used finger shapes extracted from one public MediaPipe sample clip; `learning-results/human_profiles.json` is that frozen extraction and is the only camera-derived file on main. Teleop uses its open and closed shapes as the two ends of the finger blend. The MediaPipe hand-camera stream (`services/hands`, `live`, `import-episode`) stayed on the branch and is not part of the demo.

## Robot Learns `mark_incision` From the Headset (sim)

The headset's own tracking is the egocentric record: both controllers' 6-DoF pose, grip, trigger, held instrument and the head pose, relative to `PatientRoot`, posted to the coach for one step. `robot-serve` retargets that to a simulated arm + hand, behavior-clones a policy, and grades the robot's rollouts with the same `mark_incision` predicates that grade the learner.

```sh
git -C models/menagerie sparse-checkout set shadow_hand franka_emika_panda   # once (see FETCH_HINT in mark/scene.py)
uv run scalpal-motion robot-serve --coach http://127.0.0.1:8787   # baseline in ~15 s, then one result per new demo
uv run scalpal-motion robot-send-demo --coach http://127.0.0.1:8787   # stand-in headset: a SYNTHETIC stroke via the demo route
uv run scalpal-motion robot-curve                                  # -> learning-results/robot_mark_curve.json (~2 min)
```

- **Embodiment** (`mark/scene.py`): Menagerie Franka Panda (`panda_nohand.xml`) with the Menagerie Shadow hand attached at the flange, holding a skin marker in the fist (glove mapping: an instrument in hand closes every finger on the grasp shape). Skin patch `y = -0.89 x²` in the patient frame (+X patient left, +Y anterior, +Z cranial, umbilicus at the origin; right ASIS at (-0.13, -0.015, -0.14) as in `OpenSurgerySession.AuthoredRightAsis`). Kinematic: 5-DoF damped-least-squares IK puts the marker tip on the commanded point with the marker along the inward skin normal; no contact dynamics. The marker draws while the trigger is closed and the tip is within 2 mm of the skin. The patient frame is mapped into the right-handed sim with a proper rotation, so the scene is a mirror image of Unity's left-handed one; every graded quantity (distances, lengths, angles) is mirror-invariant.
- **Grader** (`mark/grader.py`): a port of `OpenSurgeryStroke` + `FlushStroke("mark")` (error = midpoint to McBurney's point in the wound plane, length along the reference axis, folded angle) and the catalog predicates (error <= 20 mm, 50 to 80 mm, <= 25 deg). `services/preop/test/fixtures/robot-mark-strokes.json` is shared: pytest checks the port's numbers and verdicts, vitest feeds the same numbers through `BodyState` and the catalog milestone.
- **Demos** (`mark/demos.py`): headset frames -> marker-tip path. Frames are relative to `PatientRoot`; they are shifted into the umbilicus-origin frame by `PATIENT_ROOT_TO_UMBILICUS` = (0, 1.3269, 0.2093) m, where `AuthoredPatientTorsoFrame` sits in `NativeSession.unity` (a demo body may send `frameOrigin` to override it). The capture has no marker-tip offset, so one constant vertical offset puts the trigger-held samples on the skin; the trigger-held run is the stroke. Landmarks are the authored ones (the frames carry none). Synthetic demos are scripted human-like strokes (aim offset ~7 mm, angle ~9 deg, length 46 to 80 mm, tremor); 85% pass the grader and only passing demos (synthetic or headset) are trained on. `robot-send-demo` frames are stamped `stand-in` and stay labelled synthetic.
- **Policy** (`mark/learn.py`): the transfer task's chunked MLP and L1 BC (`learning/policy.py`, now sized from the data), 14-dim state (tip, landmarks, drawn extent, on-skin, last step), 10 x (tip step, trigger) chunks, DART-style noisy generation around each demo (400 episodes). Policies are cached per exact dataset.
- **Evaluation**: K = 30 closed-loop rollouts on the full arm + hand sim at held-out landmarks (right ASIS +/-3 cm), patient poses (+/-5 cm, +/-15 deg) and starts.

Measured (M-series Mac, CPU, October 4, 2026; synthetic demos only, 0 headset demos):

| Demos (synthetic) | Success, 60 rollouts (95% CI) | Median path error |
| --- | --- | --- |
| 1 | 38.3% (27.1 to 51.0) | 18.8 mm |
| 2 | 71.7% (59.2 to 81.5) | 9.8 mm |
| 3 | 71.7% (59.2 to 81.5) | 11.4 mm |
| 5 | 88.3% (77.8 to 94.2) | 6.2 mm |
| 10 | 95.0% (86.3 to 98.3) | 5.0 mm |
| 20 | 100% (94.0 to 100) | 3.7 mm |

Path error = median in-plane distance of the drawn line from the accepted 6 cm McBurney line (not a grader fact). The worker's default dataset (10 synthetic) scored 29/30 (96.7%) with a 4.1 mm median path error; a cycle takes ~15 to 18 s with retraining (train ~9 s, 30 rollouts <1 s, render ~5 s) and ~7 s when the dataset is unchanged. Each replay is H.264 High / yuv420p / faststart, 1280x720, 30 fps, at most 15 s.

**Say:** "In simulation, a policy behavior-cloned from marker strokes draws the McBurney incision line with a simulated Panda arm and Shadow hand, and passes the same milestone predicates that grade the learner in 95% of held-out patients at 10 demos." **Don't say:** that it learned from people (no headset demo has been recorded yet; every number above is synthetic), that a robot marked a patient, or anything about autonomous surgery. Not verified: a real Quest posting `robot-demo`, Unity's playback of the MP4, and the marker-tip offset of real captures.

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

To rerun it, start `spacetime start`, then from `services/api` run `E2E_CLIP=<clip> MOTION_DIR=../motion node --import tsx --test ../motion/integration/silas-e2e.test.ts`. It uses the API integration harness in place and is skipped when `E2E_CLIP` or `MOTION_DIR` is unset. The stand-in gateway tests in `tests/test_gateway_worker.py` cover the failure paths (no hand, stale run) without SpacetimeDB.

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

### Demo presentation exports

`RobotWorker.baseline()` renders `out/robot/replays/baseline-<policy-key>.mp4`;
`process()` retains `<demo-id>.mp4` for the coach upload and Quest recap consumer.
The 1280×720 streamable H.264 replay now establishes the full arm, reveals the
source stroke, holds a fixed contact camera, and finishes with two seconds of
actual shared milestone facts. Source/robot radii are 0.8/1.0 mm, with a dashed
source at its original patient-frame coordinates. Beauty rendering hides sites,
uses a matte draped field and updates MuJoCo lights after each kinematic pose.
Policy, IK, skin surface, landmark coordinates and grading predicates are unchanged.

The baseline caption explicitly says **held-out synthetic stroke, 0 headset
demonstrations**. Worker captures carry `headset`, `stand-in` or `synthetic`
provenance into the renderer; unknown provenance stays unverified. Training still
admits passing demonstrations only. A PASS overlay requires rollout success;
position error is `markErrorMm`, not `pathErrorMm`.

Render the existing saved curve without retraining:

```sh
uv run python -m scalpal_motion.mark.plot learning-results/robot_mark_curve.json /path/to/learning-curve-mark-incision.png
```

The 1920×1080 chart preserves saved aggregate values and shows the range across
two training seeds. The same 30 evaluation scenarios are reused by those seeds;
60 rollouts are not 60 distinct patients. Its synthetic-only caption is guarded
against mixed/headset datasets. Exported videos/PNGs remain outside Git.
