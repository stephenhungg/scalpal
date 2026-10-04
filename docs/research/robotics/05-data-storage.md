# 05: Storing video + motion data for Scalpal

Research date: 2026-10-03. Repo state checked: `nathan/companion-realtime` (`services/realtime/src/schema.ts`, `packages/contracts/realtime-v1.md`, `packages/contracts/robot-trajectory.v1.schema.json`, `docs/data-and-realtime.md`). Read-only; nothing modified.

## TL;DR

- **SpacetimeDB = control plane** (sessions, attempts, artifacts-as-metadata, jobs, quality, consent, dataset membership). **R2 = data plane** (MP4s, per-frame landmarks, trajectories). Nathan already built it this way; keep it.
- **Export format: LeRobotDataset v3** (Parquet + MP4 + `meta/`). It's what HF tooling, `lerobot-train`, and judges recognize. Write an EgoDex-style per-episode sidecar (intrinsics, head pose, 21 landmarks + confidence) as extra Parquet columns rather than inventing a format.
- **Add three things**: a `consent` row gate, an `episode` (dataset membership + acceptance) table, and provenance fields (digests + model/retargeter versions) on job outputs.
- **Publish only derived, de-identified trajectories** (and synthetic sim renders). Raw passthrough footage stays private in R2.

## 1. SpacetimeDB: strengths, limits, and the video question

**Good at:** transactional state transitions and fan-out. Every reducer "runs inside a database transaction"; errors roll back everything ([reducers](https://spacetimedb.com/docs/functions/reducers/)). Subscriptions push initial matching rows plus live inserts/updates/deletes into a local client cache ([subscriptions](https://spacetimedb.com/docs/clients/subscriptions/)). That's exactly the job-lease/claim/complete dance Nathan built (`motion_job.run`, `dedupeKey` unique, stale-run rejection) and the headset/companion/coach shared state.

**Bad at / constraints:**
- Reducers: "No network requests, no file system access, no system calls, only database operations"; module globals are undefined behavior. External calls go through [procedures](https://spacetimedb.com/docs/functions/procedures/) or an outside gateway (Nathan's `services/api`).
- Blobs: the docs allow inline bytes (`t.array(t.u8())`) "up to ~100MB" but warn large binary data costs memory, bandwidth and transaction size, and recommend presigned-URL external storage for large files because "files don't pass through SpacetimeDB" and "SpacetimeDB only handles metadata"; storage is ~$1/GB vs much cheaper object storage ([file storage](https://spacetimedb.com/docs/tables/file-storage/)).
- Subscriptions replicate rows to every subscriber's memory. A 30 fps x 21-landmark x 2-hand stream as rows would be thousands of tiny rows per clip broadcast to the companion, for no reader that needs them live.
- Client-side filtering of public tables is not a security boundary; use private tables + views ([access permissions](https://spacetimedb.com/docs/tables/access-permissions/)). Nathan already does this (all tables private, `my_*`/`session_*` views).

**Verdict:** video bytes never go in SpacetimeDB. Per-frame motion arrays don't either. Store in SpacetimeDB: IDs, storage keys, sha256, byte counts, status, quality summaries, versions, consent, dataset membership. This is also the more credible story for the Spacetime sponsor: "the DB is the source of truth for what exists, who may see it, and which run produced it."

## 2. Robot-learning formats

| Format | Layout | Fit for us |
|---|---|---|
| **LeRobotDataset v3** | `meta/info.json` (features, fps, path templates), `meta/stats.json`, `meta/tasks.jsonl`, `meta/episodes/*.parquet` (offsets), `data/*.parquet` frames, `videos/<cam>/*.mp4`; many episodes per file; `create` / `add_frame` / `save_episode` / `finalize()` / `push_to_hub`; streamable from Hub ([docs](https://huggingface.co/docs/lerobot/lerobot-dataset-v3)) | **Pick this.** Parquet+MP4 matches our clip+trajectory split, private HF repo or R2 both work, `lerobot-train` consumes it directly. |
| RLDS / Open X-Embodiment | TFDS episodes of steps: `observation`, `action`, `reward`, `discount`, `is_first`, `is_last`, `is_terminal` ([rlds](https://github.com/google-research/rlds)) | TF dependency, heavy for a hackathon. Mention convertibility only. |
| robomimic HDF5 | `data/demo_N/{actions, obs/*, states, rewards, dones}`, `num_samples`, `env_args`, `mask/train|valid` filter keys ([robomimic](https://robomimic.github.io/docs/datasets/overview.html)) | Good for sim-generated demos later; steal the `mask/` train/valid split idea. |
| EgoDex (Apple) | MP4 + per-episode HDF5: 3x3 intrinsics, N x 4x4 transforms for camera + 25 joints/hand, per-joint confidence; 1080p 30 Hz ([ml-egodex](https://github.com/apple/ml-egodex), [paper](https://arxiv.org/abs/2505.11709)) | Closest analog (egocentric human hands, headset). Copy its field set: intrinsics, camera pose per frame, per-joint confidence. |

### Recommended episode schema (LeRobot v3 features, one episode = one accepted attempt clip)

| Feature | dtype / shape | Notes |
|---|---|---|
| `timestamp` | float32 | seconds from episode start, on `clip_pts` timebase |
| `frame_index`, `episode_index`, `index`, `task_index` | int64 | LeRobot required |
| `observation.images.passthrough_left` | video (H,W,3) | **private datasets only**; omit in public export |
| `observation.camera.intrinsics` | float32 (4,) | fx, fy, cx, cy from PCA `FocalLength`/`PrincipalPoint` ([Unity PCA](https://developers.meta.com/horizon/documentation/unity/unity-pca-documentation/)); constant per episode, also in episode meta |
| `observation.head_pose` | float32 (7,) | camera pose in world (xyz + quat) from `GetCameraPose()` at frame timestamp |
| `observation.hand.{left,right}.landmarks` | float32 (21,3) | MediaPipe 21-pt, camera frame, meters if lifted, else normalized image coords (declare in info.json) |
| `observation.hand.{left,right}.confidence` | float32 (21,) or (1,) | per-landmark if available, else handedness score |
| `observation.hand.{left,right}.valid` | bool | detection accepted; matches trajectory `frames.valid` |
| `observation.state` | float32 (J,) | robot joint state (previous applied target in kinematic replay) |
| `action.retarget_target` | float32 (J,) | raw retargeter output, pre-limit |
| `action` | float32 (J,) | **applied** post-limit command (= trajectory `frames.q`) |
| `exercise.step_index` | int32 | from `exercise_event` aligned to clip time |

Episode-level meta (in `meta/episodes` + a sidecar JSON): `episode_id` (= attemptId), `exercise_id@version`, task text, robot model + joint names/limits, `fps`, `perception_model@version`, `retargeter@config_version`, `source_clip_sha256`, `trajectory_sha256`, `quality` (framesTotal/framesValid/invalidIntervals), `consent_id`, `license`, `rights: {raw_video: private, derived: publishable|private}`.

## 3. Mapping to Nathan's tables

Already there and good:
- `artifact` (kind, storageKey `sessions/<s>/<a>/<artifact>/<file>`, declared/verified bytes, `sha256`, `jobId`/`jobRun`, status `available` only after gateway verification) = the provenance spine.
- `motion_job` (dedupeKey on attempt+input+`configVersion`, run/lease, `QualitySummary`) = per-episode processing record.
- Artifact kinds `raw_clip`, `capture_manifest`, `scene_timeline`, `hand_estimates`, `robot_trajectory`, `quality_report` map 1:1 to LeRobot inputs.
- `robot-trajectory.v1` already has `timebase.clock = clip_pts`, `valid[]`, `invalidIntervals`, `source.perception`, `source.retargeting`. Nice.

Add (small, all private tables + views):
1. **`consent`**: `consentId, sessionId, participantLabel (pseudonym), scopes (record_raw | process | include_in_dataset | publish_derived), version of consent text, grantedAt, revokedAt?`. `request_upload` for `raw_clip` and `request_motion_job` reducers check an active scope. Revocation flips artifacts to deletion-pending (gateway deletes from R2; Meta policy requires deletion on request).
2. **`dataset_episode`**: `episodeId, datasetId, attemptId, trajectoryArtifactId, handArtifactId, clipArtifactId, acceptance (pending | accepted | rejected), rejectReason, reviewedBy, split (train | valid), exportedRevision?`. Acceptance rule default: `framesValid/framesTotal >= 0.8` and registration never `unaligned` during recording; operator can override.
3. **`dataset`**: `datasetId, schemaVersion (scalpal.lerobot.v1), visibility (private | public_derived), hfRepo?, lastExportSha?`.
4. On `artifact`/`motion_job` outputs: `inputSha256s`, `perceptionVersion`, `retargeterVersion`, `codeCommit`. Cheapest path: put these in `QualitySummary` + the trajectory JSON `source` block rather than migrating columns.

The exporter (Python, outside the DB) reads accepted `dataset_episode` rows via the gateway, pulls R2 objects with signed GETs, verifies sha256, and writes LeRobot v3.

## 4. Time sync and provenance

- **One timebase per episode: `clip_pts`** (decoded presentation timestamps of the raw MP4). All per-frame features are indexed by decoded frame; never resample across invalid gaps.
- Capture manifest records, per frame: PCA image timestamp (device monotonic ns), `clip_pts`, head pose sampled at that timestamp, plus one anchor pair `{deviceMonotonicMs, serverAt}` captured at recording start (e.g. echo of a reducer call) so `exercise_event.deviceTimeMs` can be mapped onto clip time. Nathan's contract already forbids subtracting across clock domains; keep that, use the anchor only for alignment of coarse events, and report its uncertainty (round-trip/2).
- **Frame IDs:** `frame_index` = decoded frame ordinal; keep a `pts_ms` column so dropped/duplicated frames are visible (PCA is variable rate, up to 60 fps).
- **Versioning:** `configVersion` already in the job dedupe key; make it a composite string `mediapipe-hand@0.10.x+dexret@<sha>+robot@<urdf-sha>`. A retargeter change = new job = new run-specific artifacts; old ones remain addressable.
- **Digests:** sha256 for every artifact (gateway already verifies when provided). Make the Quest always send it for `raw_clip`. Trajectory JSON carries `source.inputArtifactId` + input sha; dataset export writes a manifest of all episode digests and stores its own sha in `dataset.lastExportSha`. That's a verifiable lineage chain: clip -> landmarks -> trajectory -> dataset revision.

## 5. Privacy and rights

- Meta's policy: Device User Data "includes data from sensors such as a microphone or camera and the position of a user's headset" and "data calculated about a user's hands and body" ([Meta Developer Data Use Policy](https://developers.meta.com/horizon/policy/data-use/)). Passthrough camera images are explicitly Device User Data and the passthrough camera is gated by `horizonos.permission.HEADSET_CAMERA` ([PCA overview](https://developers.meta.com/vr/documentation/spatial-sdk/spatial-sdk-pca-overview/), [UploadVR](https://www.uploadvr.com/quest-passthrough-camera-api-experimental-out-now/)).
- Permitted: running/improving the app experience; analytics only if aggregated/de-identified. Prohibited: selling/licensing/renting user data. Must: public privacy policy, safeguards that meet industry standards, delete on request or when no longer needed. The policy does not explicitly address model training, so treat dataset building as a grey area requiring explicit participant consent.
- Note the trap: **hand landmarks and head pose are also Device User Data** under that definition, not just video. Derived robot joint trajectories (post-retarget, on a robot model) are much further removed, but publish them only with `publish_derived` consent and no identifiers.
- Raw footage stays private because passthrough captures surroundings, bystanders, screens, and possibly the participant's face/voice; it's sensitive and the policy limits use to the app experience.
- **Publishable:** robot-space trajectories, quality stats, sim renders of the robot replay, synthetic test sessions. **Not publishable:** raw clips, hand landmarks with head pose, anything with real names. For the demo, use teammates as participants with a signed one-paragraph consent and say so on stage.

## 6. Recommendation + 2-3 hour plan

Keep Nathan's control-plane / data-plane split; add consent + dataset/episode tables; export to LeRobot v3 (private by default, `public_derived` variant strips images and hand/head features). Skip Tiger Data unless you want the prize: if so, the only honest use is mirroring per-frame quality metrics (confidence, validity, joint velocity) into a hypertable for a "data quality over time" dashboard, not a second source of truth.

| Time | Task | Owner |
|---|---|---|
| 0:00-0:40 | Add `consent`, `dataset`, `dataset_episode` tables + views + reducers (`grant_consent`, `revoke_consent`, `accept_episode`, `reject_episode`); gate `request_upload(raw_clip)` and `request_motion_job` on consent; regenerate bindings | Nathan |
| 0:40-1:00 | Extend `QualitySummary`/trajectory `source` with perception/retargeter/robot versions + input sha256; make Quest always send `sha256` on clip upload | Nathan + Silas |
| 1:00-1:45 | `scripts/export_lerobot.py`: list accepted episodes via gateway, signed GET from R2, verify digests, `LeRobotDataset.create(features=...)`, `add_frame` per decoded frame, `save_episode`, `finalize()`; emit `public_derived` variant | Silas |
| 1:45-2:15 | Companion panel: episode list with valid-frame %, accept/reject buttons, consent badge, dataset revision sha | Nathan |
| 2:15-2:45 | End-to-end on one synthetic + one teammate clip; load with `LeRobotDataset(root=...)` and print a sample in the demo; push the derived variant to a private HF repo | all |

Pitch line for judges: "Every robot training frame traces back by sha256 to a consented, verified clip and a versioned retargeter run, and the raw video never leaves private storage."

## Sources

- SpacetimeDB reducers: https://spacetimedb.com/docs/functions/reducers/
- SpacetimeDB procedures: https://spacetimedb.com/docs/functions/procedures/
- SpacetimeDB file storage: https://spacetimedb.com/docs/tables/file-storage/
- SpacetimeDB subscriptions: https://spacetimedb.com/docs/clients/subscriptions/
- SpacetimeDB access permissions: https://spacetimedb.com/docs/tables/access-permissions/
- LeRobotDataset v3: https://huggingface.co/docs/lerobot/lerobot-dataset-v3
- RLDS: https://github.com/google-research/rlds
- robomimic datasets: https://robomimic.github.io/docs/datasets/overview.html
- EgoDex: https://github.com/apple/ml-egodex , https://arxiv.org/abs/2505.11709
- Meta Developer Data Use Policy: https://developers.meta.com/horizon/policy/data-use/
- Meta Passthrough Camera API overview: https://developers.meta.com/vr/documentation/spatial-sdk/spatial-sdk-pca-overview/
- Meta PCA Unity docs: https://developers.meta.com/horizon/documentation/unity/unity-pca-documentation/
- UploadVR on PCA data use: https://www.uploadvr.com/quest-passthrough-camera-api-experimental-out-now/
- Cloudflare R2 presigned URLs: https://developers.cloudflare.com/r2/api/s3/presigned-urls/
- Scalpal repo, branch nathan/companion-realtime: services/realtime/src/schema.ts, packages/contracts/realtime-v1.md, packages/contracts/robot-trajectory.v1.schema.json, docs/data-and-realtime.md
