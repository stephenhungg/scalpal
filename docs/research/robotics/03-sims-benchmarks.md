# 03: Sims, benchmarks, and policy libraries for Scalpal (Mac-first)

Context: human hand motion from Quest video, retargeted with dex-retargeting to a Shadow Hand, replayed in MuJoCo (Py 3.11, uv, M2 Mac, no NVIDIA). Goal: show those demos train and drive a robot hand/arm on a peg-transfer style task by noon ET Oct 4.

**Bottom line:** stay in MuJoCo. Use **Gymnasium-Robotics Adroit (`AdroitHandRelocate-v1`)** as the credible benchmark: it's a Shadow Hand on an arm, built for learning from human demos. Pair it with a **custom MuJoCo "instrument transfer" scene** (Menagerie Shadow Hand) for the surgical theme. Train a **plain PyTorch MLP BC policy** on CPU/MPS. Every surgical sim is either Linux/NVIDIA-only or a Py3.7-era PyBullet stack, so treat them as references, not dependencies.

**Verified on this M2 tonight:** `uv venv -p 3.11 && uv pip install mujoco gymnasium-robotics` installed MuJoCo 3.14.0 with no friction. `AdroitHandRelocate-v1` ran with obs 39, action 30, and `info['success']` exposed. 1000 random steps took **0.23 s**. `pybullet` has no arm64 wheel for 3.11, so it tried to compile from source for ~2 min and ended with `pybullet` not importable (failed build), so SurRoL on native arm64 is a real risk.

---

## 1. General manipulation sims

| Sim | Apple Silicon | Install friction | Dexterous hands | IL tooling | Verdict |
|---|---|---|---|---|---|
| **MuJoCo 3.x + Menagerie** | Native arm64 wheels. Viewer needs `mjpython` | `pip install mujoco` (verified) | Shadow E3M5 (24 DoF, L/R), Shadow DEX-EE, LEAP (16), Allegro V3 (16), Sharpa Wave. Arms: Franka/FR3, UR5e, xArm7 | You write it (or use the libraries below) | **Use** (already our stack) |
| **Gymnasium-Robotics (Adroit, Shadow Hand manip)** | Pure MuJoCo, works (verified) | `pip install gymnasium-robotics` | Adroit = Shadow hand + arm, 30 actuated DoF | Minari datasets incl. 25 human demos | **Use as benchmark** |
| robosuite 1.5 | Officially supports macOS (`mjpython` for viewer) | pip, easy | Mostly grippers. 1.5 added dex hands for humanoids | robomimic + HDF5 demo format | Backup |
| robomimic | Runs wherever robosuite runs. Torch CPU/MPS | pip | n/a | BC, BC-RNN, BC-Transformer, Diffusion. Standard success-rate eval | Backup library |
| RoboCasa | Lists macOS support (`mjpython`), Py 3.10, 5 GB of assets | Heavy | Grippers / humanoid | robomimic | Overkill |
| LIBERO | robosuite 1.4 based. LeRobot's `libero` extra is **Linux-only** | Medium-high | No | Benchmark of 130 tasks | Skip |
| MyoSuite | MuJoCo, pip installs on Mac | Low | Musculoskeletal human hand (MyoHand), not a robot | RL baselines | Skip (wrong embodiment for the pitch) |
| ManiSkill3 (SAPIEN) | CPU sim only on macOS, needs the Vulkan SDK / MoltenVK env vars. Docs: "inference, local debugging" only | Medium-high | Allegro, LEAP, etc. | Demo datasets, BC/DP baselines | Skip tonight |
| Genesis | Has a `gs.metal` backend for Apple Silicon | Medium, young API | Loads MJCF/URDF hands | Minimal | Risky tonight |
| Isaac Lab / Isaac Sim | Linux/Windows + NVIDIA RTX (≥16 GB VRAM). macOS unsupported | n/a | Excellent (Shadow, Allegro) | Mimic, robomimic | Cloud only, too slow to stand up |
| PyBullet | No arm64 wheel for 3.11, so it builds from source (verified slow) | Medium | Some URDF hands | None built in | Only if we use SurRoL |
| DexArt | SAPIEN (Linux), XArm6 + Allegro | High | Allegro | RL + point-cloud | Skip |

DexJoCo (arXiv 2605.16257, 2026) is a new MuJoCo dexterous benchmark: 11 tool-use / bimanual tasks, 1.1K trajectories. It's worth a citation as "MuJoCo is where dex benchmarks are going," but it's not worth integrating tonight.

## 2. Surgical sims

| Sim | Engine / requirements | Tasks | Mac tonight? |
|---|---|---|---|
| **SurRoL** (med-air, IROS'21; v2 branch) | PyBullet + Panda3D GUI, **Python 3.7, Gym 0.15.6, Ubuntu 20.04**. ROS Noetic only for real dVRK | dVRK PSM/ECM: NeedleReach, GauzeRetrieve, NeedlePick, **PegTransfer**, NeedleRegrasp, **BiPegTransfer**, ECM tasks (MisOrient, StaticTrack, ActiveTrack…). Scripted demos included | Possible via conda + x86 Rosetta or a Linux VM, but the old Gym/Py3.7 pins make it a time sink. Paper numbers to cite: HER+DEMO PegTransfer 0.94 sim success, 20/20 on a real dVRK |
| ORBIT-Surgical | Isaac Sim/Orbit, NVIDIA required | 14 tasks: dVRK + STAR, peg lift/transfer, needle lift, needle handover, shunt insertion… | No (cloud RTX only) |
| LapGym / `sofa_env` | SOFA FEM, Py 3.10 | 12 envs in 4 tracks: spatial reasoning (reach, deflect spheres), deformable grasp (tissue retraction, **grasp-lift-touch, a cholecystectomy proxy**), dissection, thread manipulation (rope threading, ligating loop) | SOFA + SofaPython3 builds on macOS are painful. No |
| Surgical Gym (2023) | Isaac Gym, GPU | 5 envs, 6 robots (dVRK, STAR…) | No |
| FF-SRL, CRESSim-Neo (2025-26) | GPU (CUDA) | Soft-tissue PBD/MPM, batched | No |
| Isaac for Healthcare `i4h-workflows` | NVIDIA | Surgical robot workflows | No |

**Mapping to our exercises.** Peg transfer is the shared FLS primitive, and it maps directly to SurRoL `PegTransfer`/`BiPegTransfer` and ORBIT `Peg Transfer`. Cholecystectomy maps loosely to LapGym grasp-lift-touch / tissue retraction. Appendectomy and colectomy map to ligating loop / rope threading. None of these run on a Shadow *hand*, though: they are dVRK grippers. Our story is "human hand demos → dexterous hand holding a laparoscopic instrument," so we build the scene ourselves.

## 3. Benchmarks and metrics

- **Adroit / DAPG** (Rajeswaran et al., RSS'18): 4 tasks (door, hammer, pen, relocate). The 25 human demos per task were collected in VR (CyberGlove + tracker). That's the closest precedent to Scalpal. Metric: **success rate** (the env reports `info['success']`) and normalized return. Data: `minari download D4RL/relocate/human-v2` (25 eps, 9,942 steps), plus `expert-v2` and `cloned-v2`.
- **robomimic:** success rate over 50 rollouts, max across checkpoints. Data quality buckets (PH/MH).
- **LIBERO:** average success over 50 eval episodes per task.
- **DexArt:** success rate on seen vs unseen objects.
- **SurRoL:** binary success per episode. Mean ± std over seeds.
- **JIGSAWS:** da Vinci kinematics + video for suturing, knot tying, needle passing. GRS (modified OSATS: 6 items × 1-5, range 6-30) plus gesture labels. Good for a "skill score" story, not robot training.
- **FLS peg transfer:** 6 objects transferred one way and back, 300 s cutoff. Score = 300 − time − penalty (penalties for objects dropped outside the field of view). A normalized variant is common.

**Suggested demo metrics:** success rate over N=50 randomized initial poses. Also an "FLS-style score" = 300 − t_sim − 17·drops for the policy vs the human demo. And tracking error (mm / deg) of retargeted vs replayed fingertip positions.

## 4. Policy-learning libraries on Mac

- **Plain MLP / MLP-with-action-chunking BC (PyTorch, CPU or MPS).** State-based (qpos + object pose → next joint targets). With 10-100 demos × ~200-500 steps, that's 2k-50k samples. A 3-layer 256-unit MLP trains in **1-5 min on an M2 CPU**. This is the right call tonight.
- **robomimic BC / BC-RNN (low-dim).** Minutes to under an hour on CPU for low-dim data. It needs robosuite-format HDF5.
- **LeRobot (ACT, Diffusion Policy).** Note: current `main` pyproject says **`requires-python >=3.12`**, so it needs a separate uv env from our 3.11 project. HF's own guide puts **ACT on M1/M2/M3 Max (MPS) at ~6-14 h** for ~50 image episodes, and says "CPU only: don't train." On an L4/A10G that's ~1-2 h, or 30-60 min on a 4090. Diffusion is 2-4 h on a 4090. If we want ACT, run it on HF Jobs (`lerobot-train ... --job.target=a10g-large`) with **state-only, small-step** settings. Its sim extras: `aloha` / `pusht` (MuJoCo / pymunk, Mac OK), `libero` (Linux only).
- **DAPG / BC from Minari:** trivial to reproduce BC on `relocate/human-v2` in minutes as a baseline row.

## 5. Recommendation

### Primary: MuJoCo + Adroit Relocate + custom instrument-transfer scene + MLP BC

The argument: Adroit *is* a Shadow hand, its official demos came from humans in VR, it runs at ~4k steps/s on our M2 (verified), it exposes a success flag, and there's a public 25-human-demo baseline to compare against. Relocate ("pick object, move it to a target") is the dexterous analogue of peg transfer.

```bash
cd ~/dev/scalpal
uv add mujoco gymnasium-robotics minari torch
uv run minari download D4RL/relocate/human-v2      # baseline human demos
# Menagerie Shadow hand for the themed scene
git clone --depth 1 https://github.com/google-deepmind/mujoco_menagerie third_party/menagerie
uv run mjpython -m mujoco.viewer --mjcf third_party/menagerie/shadow_hand/scene_right.xml
```

Plan:
1. Map our retargeted Shadow qpos (24 hand joints, plus wrist pose from the Quest hand root) onto Adroit's 30-D action (6 arm DoF + 24 hand DoF, normalized to [-1, 1]).
2. Replay it open-loop in `AdroitHandRelocate-v1` and report success.
3. Train BC on our N demos (plus mirrored and noise-augmented copies). Evaluate over 50 seeds against BC on the 25 DAPG human demos.
4. In parallel, build `instrument_transfer.xml`: Menagerie Shadow hand on a mocap wrist, a grasper-shaped capsule, and two target pads / a 6-peg board. Use the same BC code for the themed visual and score it FLS-style.

Caveat to flag: Adroit's arm is a 6-DoF slide/hinge base, not a real arm. If judges ask "robot arm," mount the Menagerie Shadow hand on the Menagerie UR5e or Franka in the custom scene and solve the wrist pose with IK (`mink` or MuJoCo's own IK), or just call it a mocap-driven wrist.

### Backup: robosuite + robomimic (Franka gripper), or LeRobot ACT on a cloud GPU

If the dexterous BC looks bad, project the human demo to a wrist pose + pinch aperture. Drive a robosuite Franka in `Lift`/`PickPlaceCan` (`uv pip install robosuite robomimic`, run with `mjpython`), then train robomimic BC-RNN low-dim on CPU. This is the "human video → robot arm" claim with a standard success-rate benchmark. If cloud GPUs come through, LeRobot ACT via HF Jobs (Py 3.12 env) is the flashier option, but budget 1-2 h of training plus setup.

**Don't** spend tonight on SurRoL, LapGym, ORBIT-Surgical, or Isaac Lab. Cite them as the surgical-robotics landscape and roadmap ("sim-to-dVRK next").

---

## Sources
- MuJoCo Menagerie: https://github.com/google-deepmind/mujoco_menagerie
- Gymnasium-Robotics Adroit: https://robotics.farama.org/envs/adroit_hand/
- Minari D4RL relocate human-v2: https://minari.farama.org/datasets/D4RL/relocate/human-v2/
- DAPG paper (Rajeswaran et al. 2018): https://arxiv.org/abs/1709.10087
- robosuite install (macOS, mjpython): https://robosuite.ai/docs/installation.html
- RoboCasa: https://github.com/robocasa/robocasa
- ManiSkill macOS install: https://maniskill.readthedocs.io/en/latest/user_guide/getting_started/macos_install.html ; https://github.com/mani-skill/ManiSkill/issues/809
- Genesis install/backends: https://genesis-world.readthedocs.io/en/latest/user_guide/overview/installation.html
- Isaac Lab install requirements: https://isaac-sim.github.io/IsaacLab/main/source/setup/installation/index.html
- DexArt: https://arxiv.org/abs/2305.05706 , https://github.com/Kami-code/dexart-release
- DexJoCo: https://arxiv.org/abs/2605.16257
- SurRoL: https://github.com/med-air/SurRoL , paper https://arxiv.org/abs/2108.13035
- ORBIT-Surgical: https://arxiv.org/abs/2404.16027 , https://orbit-surgical.github.io/
- LapGym / sofa_env: https://arxiv.org/abs/2302.09606 , https://github.com/ScheiklP/sofa_env
- Surgical Gym: https://arxiv.org/abs/2310.04676
- FF-SRL: https://arxiv.org/abs/2503.18616 ; CRESSim-Neo: https://arxiv.org/abs/2608.25192
- Isaac for Healthcare workflows: https://github.com/isaac-for-healthcare/i4h-workflows
- LeRobot hardware/training-time guide: https://huggingface.co/docs/lerobot/en/hardware_guide ; pyproject: https://github.com/huggingface/lerobot/blob/main/pyproject.toml
- JIGSAWS: https://cirl.lcsr.jhu.edu/research/hmm/datasets/jigsaws_release/
- FLS manual skills instructions: https://www.flsprogram.org/wp-content/uploads/2014/03/Revised-Manual-Skills-Guidelines-February-2014.pdf
