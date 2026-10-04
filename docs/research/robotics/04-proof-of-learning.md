# 04: Proof of Learning (smallest honest experiment)

Goal: answer "does your video data train a robot, or just replay it?" with one chart and one video, built by one person in ~8 hours on a Mac, in simulation.

The one-sentence result we want to say on stage:

> "A policy trained only on demonstrations derived from our headset videos picks up an instrument from positions it never saw and places it on target in X% of 100 held-out trials. Replaying the nearest recorded demo gets Y%. When we bump the instrument mid-grasp, the policy recovers and replay doesn't."

Prior art shows this shape is credible: DexMV turned human video into retargeted hand and object trajectories and showed imitation learning from them beats learning from scratch [1]. VideoDex used retargeted human hand motion as an action prior [2]. MimicGen showed a handful of demos can be transformed into object-relative variants that train good policies [3]. PSI used simulation to filter human-video trajectories before imitating them [4]. We do a tiny, honest version of that recipe.

## 1. Task choice

**Task: "instrument transfer."** Floating robot hand picks up a capsule-shaped instrument (a scalpel-handle stand-in, ~12 cm x 1.2 cm) from a randomized spot on a table and puts it down in a target tray. It reads as a scaled-down FLS peg transfer, which is a real surgical training drill, so the surgery story holds.

**Embodiment, primary:** MuJoCo Menagerie Shadow Hand (MJCF with tuned contacts and position actuators) [5], mounted on a free base driven through a mocap body with a weld constraint. That gives 6 wrist DoF plus the finger actuators, no arm IK. Silas's 22 dex-retargeting joints map onto Menagerie by name (`FFJ3` to `rh_FFJ3`). J1 and J2 on FF/MF/RF/LF are tendon-coupled into one actuator, so the target is `J1+J2`.

**Embodiment, fallback (decision gate at hour 1.5):** a floating parallel gripper (Menagerie Panda hand). Gripper width comes from the human thumb-tip to index-tip distance (MediaPipe landmarks 4 and 8 in `hand_track.json`), mapped linearly to [0, 8 cm]. Less sexy, much more reliable contacts. Not using a full Franka/UR5 arm: arm IK and joint limits add risk and nothing to the claim.

Reach-to-target alone is too weak (no contact, judges will call it a lookup table). Pinch-and-place is the minimum that needs fingers, contact, and closed-loop object tracking.

**What our data lacks, and how we supply it:**

| Missing | Why it matters | Supply it (best to fallback) |
|---|---|---|
| Wrist pose (README: discarded, WRJ held at 0) | A hand that can't move can't pick anything up | (a) Log Quest hand tracking wrist pose from Unity (XR Hands / OVRHand root pose) per frame to JSON next to the clip. (b) No log: synthesize wrist path as min-jerk reach to the object, timed by the human's grasp onset. |
| Object state | The policy must condition on where the instrument is | (a) Use a virtual instrument in the Unity scene and log its pose + grabbed flag per frame. (b) Real object: tape start and tray marks on the table, measure them once; start and end poses are enough for object-frame transforms. |
| Contact / force | Video can't see grip force | Don't fake it. Get contact from MuJoCo physics, and keep only demos that physically succeed in sim (PSI-style filter [4]). |
| Time sync | Unity log and video clocks differ | Cross-correlate a finger-closure signal from both sources (Unity hand joints vs MediaPipe mean flex) to find the offset. Also do a deliberate fist-open "clap" at clip start. |

Honesty note: if we use fallback (b) for wrist, the human contribution is grasp shape and timing, not the reach path. Say so.

## 2. Data pipeline

**Input per human demo:** `motion.json` (22 finger joints x frames, `valid`, `at_limit`, `vector_error_m`) + `unity_log.json` (t, wrist pos/quat, instrument pos/quat, grabbed).

**Steps:**
1. **Quality gate.** Drop the clip if valid fraction < 0.9 or any tracking gap > 300 ms. Split at shorter gaps; never fill invalid frames (matches the motion service's no-invented-motion rule).
2. **Resample** to 20 Hz control rate.
3. **Segment** from signals, no hand labeling:
   - approach: start to grasp onset (mean finger flex crosses 40% of its clip range)
   - grasp: onset to flex plateau
   - transport: object `grabbed` true (or object moving with the wrist)
   - release: flex drops below 40%
4. **Object-relative transform (MimicGen-style [3]).** Express approach+grasp wrist poses in the instrument frame, transport+release in the tray frame. For a new scene, map them back to world with the new instrument and tray poses, with a linear blend between segments.
5. **Generate sim demos.** Track the transformed reference in MuJoCo with physics on. Inject DART-style action noise (Gaussian, sigma = 1 cm / 3 deg on wrist, 0.05 rad on fingers) during collection, and always label the clean corrective action back toward the reference [6]. That gives the off-distribution states BC needs to recover.
6. **Success filter.** Keep only episodes where the instrument ends in the tray. Log yield per human demo; it doubles as a demo-quality score.

**Observation (43-dim, all state, no pixels):**
- finger joint positions (22)
- palm position (3) + palm orientation as 6D rotation (6)
- instrument pose relative to palm: position (3) + 6D rotation (6)
- tray center relative to instrument (3)
- 2 binary contact flags (thumb-tip and index-tip touching instrument), from sim

No time or phase input. That would let the policy memorize a clock instead of reacting to state.

**Action (28-dim):** delta palm pose (3 position, clipped to 1 cm/step; 3 axis-angle, clipped to 3 deg/step) + absolute finger joint targets (22), fed to the position actuators. Predict a chunk of the next 10 actions and execute with temporal ensembling (ACT's trick [7]). This is a cheap fix for jitter.

**Augmentation for small N:** per human demo, instrument xy uniform in a 20 x 20 cm box, yaw +/- 30 deg, tray shift +/- 5 cm, time-warp 0.8 to 1.2x, plus DART noise. Gaussian noise (sigma 0.005) on observations during training. Normalize obs/actions to zero mean, unit variance.

## 3. Policy

**MLP behavior cloning with action chunking.** No vision, so ACT or Diffusion Policy via LeRobot is overkill. LeRobot does run on Apple Silicon with `--policy.device=mps`, but users report it as slow [8], and its transformer/vision stack buys nothing on a 43-dim state input.

- MLP: 43 -> 512 -> 512 -> 512 -> 280 (10 x 28), LayerNorm + GELU, dropout 0.1
- Loss: L1 on normalized actions (ACT found L1 better than L2 for chunks [7])
- AdamW, lr 1e-3, cosine decay, weight decay 1e-4, batch 512, 20k steps
- Torch on CPU (small MLPs often run faster on CPU than MPS). Expect 2 to 5 min per run on an M-series chip.
- 3 training seeds per config.

Stretch only, if the MLP sits below ~60%: swap in LeRobot Diffusion Policy on the same state dataset. Don't start there.

## 4. Evaluation protocol

**Held-out initial conditions:** 100 instrument/tray configurations from a fixed eval seed, disjoint from every training seed. 80 inside the training box, 20 in a 5 cm ring outside it (OOD, reported separately).

**Success:** instrument center within 3 cm of tray center, resting (speed < 1 cm/s), hand released, within 10 s. Secondary: grasp success (instrument lifted 5 cm). Report mean and 95% Wilson CI over 100 rollouts x 3 seeds.

**Baselines:**
| Baseline | What it shows |
|---|---|
| Random actions | Floor (~0%) |
| Raw replay: nearest human demo replayed open loop, no adaptation | "Just replay" (the judges' question). Should only work near the original object spot. |
| Transformed replay: object-adapted reference run open loop | Strong oracle that knows the object pose at t=0. Policy should match it on clean trials and beat it under perturbation. |
| Scripted grasp (optional): hand-coded close-all-fingers grasp | What the human finger shapes add |

**Perturbation test (the "powers, not replays" proof):** in 50 trials, shove the instrument 3 to 5 cm in a random direction at t = 1.0 s (before contact). Closed-loop BC should re-target; open-loop replay can't.

**Data-scaling curve:** train on N = 1, 5, 10, 20 human demos with a **fixed budget of 1000 sim episodes** (so the curve measures human-demo diversity, not just data volume). Optionally also plot a K-per-demo variant.

**Ablation, noisy vs filtered:**
- noisy: no smoothing, at-limit-heavy frames kept, no sim-success filter
- filtered: `--smooth 0.3`, quality gate, success filter

Both at N = 20 (or the full scaling sweep if time allows). Expectation: filtered > noisy. That ties straight back to the quality metrics the motion service already reports.

**Compute:** 4 N x 2 variants x 3 seeds = 24 training runs (~1.5 h total) + 24 x 100 rollouts (headless, ~2 min each). Run in the background.

**3-minute presentation:**
- **One chart:** success rate (y) vs number of human demos (x, 1/5/10/20). Lines: BC filtered, BC noisy. Dashed horizontals: raw replay, transformed replay, random. Small inset bar: clean vs perturbed for BC and transformed replay.
- **One video (~25 s):** three panels. Left, the Quest passthrough clip. Middle, Silas's retargeted replay. Right, the learned policy on 4 held-out instrument positions, then a mid-grasp shove and recovery. Caption: "policy never sees the video at test time; it acts from the robot's state."
- Script: 20 s setup, 40 s video, 40 s chart, 20 s limits.

## 5. Honest claims vs overclaims

| Say | Don't say |
|---|---|
| "Policy trained on demonstrations derived from our headset videos, in simulation" | "Our robot learned surgery" |
| "Generalizes to held-out instrument positions within the training range; X% beyond it" | "Generalizes to new tasks/objects" |
| "Closed-loop: recovers from perturbations open-loop replay can't" | "Robust" without numbers |
| "Finger motion from video; wrist and object pose from the Quest/Unity log" (or "wrist path synthesized") | "Learned end to end from video pixels" |
| "Sim-filtered: we kept the M of N generated demos that physically succeeded" | Hiding the filter yield |
| "Success scales with number of human demos (1 to 20)" | "Scales" from one data point or one seed |
| "No physical robot; sim-to-real untested" | "Ready for a real robot" |

Always show CIs and the replay baselines next to the policy number.

## 6. Build plan (~8 h, one person)

| Time | Step | Done when |
|---|---|---|
| 0:00 to 0:30 | Ask Unity owner for per-frame log (wrist pose, instrument pose, grabbed) and 20+ short clips of the transfer task (~25 min of recording). Meanwhile: `uv add mujoco torch`, pull Menagerie shadow_hand, build scene (table, capsule, tray, mocap weld). | Scene loads, hand moves with mocap |
| 0:30 to 1:30 | Joint-name map; replay one retargeted finger trajectory with a scripted wrist path; tune friction / actuator gains. **Gate:** lift holds in >= 5/10 scripted tries, else switch to gripper. | Instrument lifted in sim |
| 1:30 to 2:30 | Ingest: quality gate, resample, sync offset, segmentation, object-frame transform. Unit test on a synthetic demo. | Transformed references for every clip |
| 2:30 to 3:30 | Sim data generator: augmentation + DART noise + success filter, writing `npz` (obs, action chunk, episode id, source demo id). Multiprocess over cores. | 1000-episode dataset, yield logged |
| 3:30 to 4:30 | BC trainer + rollout evaluator + baselines (random, raw replay, transformed replay). Smoke test at N = 20. | Policy > replay on held-out ICs |
| 4:30 to 6:00 | Background sweep: scaling x noisy/filtered x 3 seeds, perturbation test. Fix bugs while it runs. | `results.json` |
| 6:00 to 7:00 | Chart (matplotlib) + three-panel video (MuJoCo offscreen renderer + ffmpeg hstack). | chart.png, demo.mp4 |
| 7:00 to 8:00 | Buffer. Write the claims block into the README. Commit. | Done |

**Main risks and mitigations:**
1. **Shadow grasp physics is finicky** (slipping, penetration). Mitigation: capsule with high friction (1.5), condim 4, the hour-1.5 gate, gripper fallback.
2. **No Unity log in time.** Synthesize the wrist path (fallback b) and narrow the claim.
3. **Too few usable clips** (first-person occlusion kills MediaPipe). Record extra clips with the hand side-on to the headset; the N = 20 point can drop to N = 15 honestly.
4. **Covariate shift / policy freezes near the object.** DART noise, action chunking, contact flags in obs.
5. **Policy just memorizes.** The disjoint held-out seed set, OOD ring, and raw-replay baseline catch it.
6. **Mac rendering.** MuJoCo's offscreen `mujoco.Renderer` works headless on macOS; render only eval episodes for the video.
7. **Time.** If behind at 4:30, cut to N = {1, 5, 20}, one seed for noisy, and skip the scripted-grasp baseline. Never cut the replay baseline or the perturbation test; they are the answer to the judges' question.

## Sources

1. Qin et al., "DexMV: Imitation Learning for Dexterous Manipulation from Human Videos," ECCV 2022. https://arxiv.org/abs/2108.05877 , code https://github.com/yzqin/dexmv-sim
2. Shaw, Bahl et al., "VideoDex: Learning Dexterity from Internet Videos," CoRL 2022. https://arxiv.org/abs/2212.04498
3. Mandlekar et al., "MimicGen: A Data Generation System for Scalable Robot Learning using Human Demonstrations," CoRL 2023. https://arxiv.org/abs/2310.17596
4. Zhai et al., "Imitating What Works: Simulation-Filtered Modular Policy Learning from Human Videos," 2026. https://arxiv.org/abs/2602.13197
5. MuJoCo Menagerie, Shadow Hand model. https://github.com/google-deepmind/mujoco_menagerie/tree/main/shadow_hand
6. Laskey et al., "DART: Noise Injection for Robust Imitation Learning," CoRL 2017. https://arxiv.org/abs/1703.09327
7. Zhao et al., "Learning Fine-Grained Bimanual Manipulation with Low-Cost Hardware" (ACT/ALOHA), RSS 2023. https://arxiv.org/abs/2304.13705
8. LeRobot training docs (MPS device flag) and Mac speed reports. https://huggingface.co/datasets/SiliconVandals/lerobot/blob/main/examples/4_train_policy_with_script.md , https://www.reduct.store/blog/hugging-face-lerobot
9. Qin et al., "AnyTeleop" / dex-retargeting (the retargeter Silas uses). https://github.com/dexsuite/dex-retargeting
10. Scalpal motion service, branch `silas/motion-replay`, `services/motion/README.md` (wrist discarded, kinematic only, quality fields).
