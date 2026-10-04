# Video to Robot: Research and Plan

Updated October 3, 2026, 9:40 PM ET. Synthesis of six research reports in this folder, written for Silas (motion), Stephen (Quest capture), and Nathan (data). Each report cites its sources. Treat the recommendations as proposals: none of the experiment below has been built or run yet.

| Report | Question |
| --- | --- |
| [01 Hand pose](01-hand-pose.md) | Extracting hand motion reliably from Quest egocentric video, including wrist pose |
| [02 Video to robot learning](02-video-to-robot-learning.md) | What the literature shows and what we can honestly claim |
| [03 Simulators and benchmarks](03-sims-benchmarks.md) | What runs on our Macs tonight |
| [04 Proof of learning](04-proof-of-learning.md) | The smallest experiment that shows the data trains a policy |
| [05 Data storage](05-data-storage.md) | SpacetimeDB, R2, dataset format, consent |
| [06 Quest capture](06-quest-capture.md) | Recording raw passthrough plus per-frame metadata |

## Where we are

Silas's `services/motion` (branch `silas/motion-replay`) already runs clip → MediaPipe hand landmarks → dex-retargeting to a Shadow hand → MuJoCo replay, and is verified end to end against Nathan's gateway and SpacetimeDB (621/621 frames on a webcam sample clip). Three things are missing:

1. **A real Quest clip.** None has been recorded.
2. **Wrist and world pose.** The pipeline discards the wrist; only finger shapes transfer.
3. **Learning.** Replay is not training. Judges will ask how we know the data powers a robot.

## The claim we can make

No published work turns bare-hand trainee video into a surgical robot policy. SRT-H (Science Robotics 2025) and NVIDIA's GR00T-H train on robot kinematics recorded during teleoperation, not on human video (report 02, moderate confidence). The closest precedents are DexMV (human video → dexterous hand, simulation) and the Adroit/DAPG demos (human VR demos for a Shadow hand).

Say: *"Retargeted demonstrations from learners' Quest video train a behavior-cloning policy that completes a simulated instrument transfer from unseen starting positions and recovers from disturbances, where replaying the demonstrations does not."*

Do not say: autonomous surgery, a trained surgical robot, expert data, or real-robot results.

## The experiment (report 04)

- **Task:** instrument transfer, a scaled-down FLS peg transfer in MuJoCo. A floating Menagerie Shadow hand picks a capsule handle from a randomized spot and places it in a tray.
- **Gate at hour 1.5:** if the Shadow hand can't hold the object in at least 5 of 10 scripted lifts, switch to a floating parallel gripper driven by thumb-to-index pinch distance.
- **Data:**
  - Drop low-quality clips; never fill gaps.
  - Segment phases, then transform each demo into new object positions (MimicGen-style).
  - Roll out with DART-style noise and keep only rollouts that succeed in physics.
- **Policy:** MLP behavior cloning on state: hand joints plus object pose, with action chunks of 10. Minutes on CPU. LeRobot ACT is about 6-14 hours on Apple Silicon (report 03), so it's only an option on a cloud GPU.
- **Evaluation:**
  - 100 held-out starting positions, 20 of them outside the training range, 3 seeds.
  - Baselines: random, replay of the nearest human demo, and an object-adjusted replay.
  - A mid-grasp shove test. This is the direct answer to "replay or learning".
- **Results to show:**
  - Success versus 1/5/10/20 human demos.
  - All demos versus filtered high-quality demos. That's the thesis in one chart: learner skill scores select the robot's training data.
- **Benchmark anchor (report 03):** Gymnasium-Robotics `AdroitHandRelocate-v1` is a Shadow hand whose 25 official demos were recorded by humans in VR. It's verified on an M2: MuJoCo 3.14, 1000 steps in 0.23 s. Training on our retargeted demos versus their human VR demos gives a published-benchmark comparison.
- **Surgical simulators** (SurRoL, ORBIT-Surgical, LapGym) need Linux, NVIDIA, or SOFA. Cite them as the roadmap, not tonight's work.

## What each person needs to do

### Stephen (Quest capture, report 06): start first, everything depends on it

1. Record with MRUK `PassthroughCameraAccess` (v81+), which the official samples already use. It gives a per-image timestamp, `GetCameraPose()` at that image's timestamp, and intrinsics. That solves the pose-sync problem the earlier Camera2 path had. Both cameras can stream for stereo if needed later.
2. Write a JPEG sequence plus one JSONL line per frame:
   - frame index and image timestamp
   - Unity time
   - camera world pose and head pose
   - tracking validity
   - virtual instrument and object poses, plus a grabbed flag

   Static intrinsics, crop, and versions go in `session.json`. Pull to the Mac with `adb pull`. QuestRealityCapture and OpenQuestCapture (MIT) are worth cherry-picking from.
3. Check two things:
   - Meta documents 30 FPS per camera, but we measured about 59, so there may be duplicated frames.
   - Intrinsics are probably against the 1280x1280 sensor, so for 1280x960 the principal point likely shifts by -160 in y. Verify by reprojection.
4. Record 20+ short clips of the transfer task, hands side-on to the headset where possible. Cutoff around 1 AM ET. In parallel, film with a phone on a tripod as the fallback.
5. Don't export Meta SDK hand joints. Meta allows hand-tracking data only to enable hand tracking in the app.

### Silas (motion and experiment, reports 01, 03, 04)

1. Add wrist pose: `cv2.solvePnP` of MediaPipe metric world landmarks against image landmarks with the real intrinsics, composed with the logged camera pose. That drives a floating-base hand. Expect roughly 10-15% depth bias; do a one-time scale calibration. Skip HaWoR, Hamba, and Dyn-HaMR: they need CUDA.
2. Robustness: confidence gating, short-gap interpolation, left/right consistency by wrist position, bidirectional smoothing with SLERP on rotation.
3. Run the experiment above. The 8-hour plan in report 04 cuts to N = {1, 5, 20} if behind. Never drop the replay baseline or the shove test.
4. Accuracy check: tape marks at known distances, plus tapping a fixed point while moving the head.

### Nathan (data, report 05)

1. Keep the current design. SpacetimeDB holds metadata and job state, R2 holds bytes via signed URLs, and the gateway verifies sha256. SpacetimeDB's own docs warn against storing large blobs inline.
2. Add three tables:
   - `consent`: block raw-clip upload and motion jobs without active consent.
   - `dataset`
   - `dataset_episode`: accepted or rejected, with a reason.

   Require perception and retargeter versions on results, and keep the provenance chain as sha256: clip → landmarks → trajectory → dataset revision.
3. Export LeRobotDataset v3 (Parquet + MP4 + `meta/`). Per-frame fields follow Apple's EgoDex: intrinsics, head pose, 21 landmarks with confidence and validity, the raw retargeter target, the applied command, and the exercise step, all on the clip's timestamps.
4. Privacy: Meta's Developer Data Use Policy treats camera images *and data calculated about hands and body* as Device User Data. Raw clips and landmarks stay private. Only robot-space trajectories, quality stats, and sim renders may be published. Building a dataset needs explicit participant consent.

## Order of operations tonight

1. Now: Stephen starts the capture build and the phone fallback. Silas builds the MuJoCo scene and gate (no clips needed yet).
2. By about 1 AM: first Quest clips with JSONL; Silas adds wrist pose and runs the real clips.
3. Overnight: data generation, training sweeps in the background, Nathan's consent and dataset tables.
4. Morning: chart, three-panel video (human clip | retargeted replay | learned policy recovering from the shove), claims block in the README.

If the experiment slips, the honest fallback demo is the current pipeline on a real Quest clip, plus the Adroit benchmark comparison, presented as replay with the learning experiment as next step.
