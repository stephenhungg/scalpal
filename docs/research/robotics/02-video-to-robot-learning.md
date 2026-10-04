# Human (egocentric) video to robot policies: literature scan for Scalpal

Date: 2026-10-03. Confidence notes in [brackets]. "Verified" = checked against arXiv/project page in this session; "from memory" = recalled, worth a double-check before quoting a number on stage.

## TL;DR

- The field has settled on a recipe: **3D hand pose from video, then retargeting into robot actions, then behavior cloning (BC), usually co-trained with some real robot data**. Scalpal's pipeline (Quest passthrough, MediaPipe, dex-retargeting, Shadow hand in MuJoCo) is steps 1-2 of that recipe. The missing step is step 3: actually training a policy.
- **Human-video-only, zero robot data** policies exist (Phantom, 2025) but they work because of careful visual editing and they target simple parallel grippers. Most strong results (EgoMimic, DexWild, pi's human-to-robot work) **co-train** human data with real robot data and report **relative** gains (34-228%, ~4x, ~2x).
- Surgical autonomy from demonstrations is real now (SRT, SRT-H, GR00T-H), but all of it trains on **robot kinematics recorded on a dVRK or similar**, not on bare human hands. Nobody has shown "surgical trainee hand video becomes a surgical robot policy". That's Scalpal's honest novelty and also its biggest gap.
- Best credible hackathon claim: **"N human demos, retargeted to the Shadow hand, train a BC policy in MuJoCo that succeeds X% on held-out start states, with a data-scaling curve and a filtered-vs-unfiltered comparison."** Everything sim, everything stated as sim.

## 1. Learning manipulation from human video / hand demos (2022-2026)

| Method | Input data | Embodiment-gap handling | What they showed |
|---|---|---|---|
| **DexMV** (ECCV 2022) | Multi-view RGB-D of humans doing tasks | 3D hand+object pose, retargeting to Adroit hand; demos augment RL | **Sim only.** Demos made tasks solvable that pure RL fails on; generalized to unseen object instances. Closest ancestor to Scalpal (dex-retargeting is from the same group). Verified. |
| **VideoDex** (CoRL 2022) | Internet human video | Retargeted hand + arm as a prior/regularizer | Real robot, video used as prior plus robot data. [from memory] |
| **MimicPlay** (CoRL 2023) | Cheap human "play" video | Human hand trajectories train a *high-level* latent planner; low-level from robot teleop | Real long-horizon tasks; human data mostly helps the planner, not motor control. [from memory] |
| **DexCap** (RSS 2024) | Mocap gloves + chest RGB-D | Fingertip IK retargeting to LEAP hand; **point clouds** instead of images; human-in-the-loop correction | Real bimanual tasks; point-cloud policies >60% avg success, **image-input policies failed completely** due to hand appearance gap. Verified. |
| **HumanPlus / OKAMI** (2024) | Human video / single human video | Whole-body retargeting to humanoid; OKAMI does object-aware retargeting | Real humanoid imitation; OKAMI from one video. [from memory] |
| **Track2Act** (2024) | Web video | Predicts point tracks, robot residual policy corrects | Real; learns *what should move*, not hand actions. [from memory] |
| **HOP** (2024) | Human video | Hand-object pretraining as a representation prior, then RL/BC fine-tune | Sim + real, representation-level transfer. [from memory] |
| **EgoMimic** (ICRA 2025) | Project Aria glasses: egocentric RGB + 3D hand tracking + SLAM | Low-cost arm designed to look/move like a human; cross-domain alignment; co-train human + robot | Human data boosted success **34-228%** over robot-only; generalized to new scenes. Verified. |
| **Phantom** (CoRL 2025) | Human video only | Hand pose to robot action; **inpaint human hand, overlay rendered robot** | Zero robot data, up to **92%** success on real tasks (sweeping, insertion, deformables). Verified. Strongest "no robot data" result, but grippers, not dexterous hands. |
| **EgoDex** (Apple 2025) | Vision Pro: **829 h**, 338k episodes, 194 tasks, 25 keypoints/hand, camera extrinsics | None; benchmark is *human hand trajectory prediction* | Dataset + hand-trajectory-prediction baselines, no robot. Verified. Very close to Scalpal's capture setup (headset passthrough + hand tracking). |
| **DexWild** (2025) | 9,290 human demos across 93 envs via wearable rig | Co-train human + robot | **68.5%** in unseen envs, ~4x robot-only. Verified. |
| **Cross-embodiment VLAs** | Open X-Embodiment (robot only), GR00T N1, pi | GR00T N1: human egocentric video gets **latent actions** from a VQ-VAE (no action labels needed). pi (Dec 2025): human-to-robot transfer **emerges** with scale, ~2x on data-poor tasks | Real robots, massive scale. Verified (GR00T N1, pi blog). |

Pattern: the field converted "video has no actions" into "hand pose *is* the action" (retargeting) or "learn latent actions". Gains are reported as relative improvement over a baseline, almost always with robot data in the loop.

## 2. Surgical robot learning

- **SRT** (JHU/Stanford, CoRL 2024, arXiv 2407.12998): ACT-style transformer on dVRK, **images in, relative pose actions out** (20-D: two arms x [3 trans + 6 rot + 1 jaw]). Key insight: dVRK forward kinematics is inaccurate, so absolute actions fail; relative actions transfer. Tasks: tissue lift, needle pickup/handover, knot tying, on real dVRK. Data = teleoperated robot demos, not human hand video. Verified.
- **SRT-H** (Science Robotics 2025): hierarchical, language-conditioned (high-level policy emits language corrections, low-level executes). Clip-and-cut step of cholecystectomy, **100% on 8 unseen ex vivo gallbladders**. Verified.
- **Open-H-Embodiment + GR00T-H** (NVIDIA + 49 institutions, 2026): 770 h paired video + kinematics, 9 platforms, 65% clinical. GR00T-H: 25% full end-to-end structured suturing (others 0%), 64% avg across a 29-step ex vivo sequence. Verified. Data format = synchronized endoscope video + robot kinematics in a unified action space.
- **Sim frameworks**: SurRoL (dVRK tasks in PyBullet, RL + demos), **ORBIT-Surgical** (Isaac/Omniverse, 14 dVRK/STAR tasks, RL + IL, supports human-expert data via input peripherals), **SurgicAI** (dVRK suturing with thread sim), **Surgical Gym** (GPU RL). Verified for ORBIT-Surgical; others from memory. **SuFIA-BC** (2025) generates demo data for visuomotor BC in surgical sim.
- **JIGSAWS** (2014): da Vinci kinematics + video, 8 surgeons labeled novice/intermediate/expert. Mostly used for **skill assessment and gesture recognition**, not policy learning. Important for Scalpal: the skill-label idea maps directly to "novice vs expert demo" filtering.

**Gap Scalpal sits in:** every surgical policy above is trained on robot-side kinematics. Bare-hand human video for surgical robots is unexplored in the published work I found [moderate confidence]. That's the pitch, but the claim has to stop at "first step, in sim, with a dexterous hand proxy, not a surgical instrument".

## 3. Known pitfalls (and how they bite Scalpal)

1. **Embodiment gap.** Shadow hand is not a surgeon's hand and not a dVRK wrist. DexCap needed human-in-the-loop correction when IK retargeting wasn't enough. Kinematic retargeting gives *plausible poses*, not *task success*: replayed fingers often miss or penetrate objects.
2. **No action labels.** Hand pose is a proxy action. Timing, grasp force and gripper closure are inferred.
3. **No force/contact.** Video has zero force info. Replay in MuJoCo with physics on will drop objects. Common fix: replay kinematically, or use the demo as a reference for a residual/RL policy (DexMV style).
4. **Monocular depth.** MediaPipe gives weak wrist depth from one camera, so absolute 3D wrist trajectories are noisy. Quest's native hand tracking (if you can log it) is much better than MediaPipe on passthrough video. Use relative actions (SRT lesson) to lower the damage.
5. **Visual gap.** Image policies trained on human hands fail on robot hands (DexCap). Use **state-based** (object pose + hand joints) policies in sim to dodge it entirely.
6. **Novice vs expert / data quality.** Mixed-quality demos hurt BC (robomimic found multi-human data underperforms proficient-human data [from memory]). Recent work filters by smoothness/jerk (RINSE, 2026). Scalpal's learners *are* novices, so a quality filter is a natural, on-thesis experiment.

## 4. What a 15-hour, no-robot team can credibly claim

Do **not** claim: robot learning on hardware, surgical autonomy, "trains surgical robots", or generalization you didn't measure.

**Smallest credible experiment (recommended):**

1. **Task**: one simple sim task in MuJoCo with the Shadow hand, e.g. "pick up a small object (needle-driver/peg proxy) and place it in a target zone". Define success programmatically (object within r cm of target, held for t s).
2. **Data**: 20-40 human demos captured in Quest MR, retargeted. Convert each to `(state, action)` pairs where state = hand joint pos + object pose + target pose, action = **delta** joint targets (relative actions, cites SRT). Drop demos whose retargeted replay doesn't succeed in sim (that filter itself is a result: "retargeting yield = K/N").
3. **Policy**: small MLP BC (or ACT if time) on state, not pixels. Train in minutes on CPU/laptop GPU.
4. **Eval**: 50 rollouts on **held-out randomized start states** (object position jittered). Report success rate with a 95% CI.
5. **Baselines + curves** (this is what makes it look like research):
   - Open-loop replay of the nearest demo (expect low success under jitter).
   - **Data-scaling curve**: success vs N = 5, 10, 20, all demos.
   - **Quality filter**: train on all demos vs top-k by a smoothness/jerk or time-to-complete score. If filtered wins, that's literally "learner performance metrics curate robot data", which is the thesis.

**Claim template**: "From N mixed-reality practice sessions, K retargeted demos trained a state-based BC policy for a simulated Shadow hand that succeeds X% (95% CI) on 50 held-out start states, vs Y% for demo replay. Success rises with demo count, and filtering demos by our skill score improves it Z points. All results in simulation."

Stretch only if the core lands: residual RL on top of BC, or a dVRK-ish gripper target in SurRoL/ORBIT-Surgical. Don't chase that before noon tomorrow.

## Sources

- DexMV: https://arxiv.org/abs/2108.05877 , code https://github.com/yzqin/dexmv-sim
- DexCap: https://arxiv.org/abs/2403.07788
- EgoMimic: https://arxiv.org/abs/2410.24221 , https://egomimic.github.io/
- Phantom: https://arxiv.org/abs/2503.00779 , https://phantom-human-videos.github.io/
- EgoDex: https://arxiv.org/abs/2505.11709 , https://github.com/apple/ml-egodex
- DexWild: https://arxiv.org/abs/2505.07813 , https://dexwild.github.io/
- GR00T N1: https://arxiv.org/abs/2503.14734
- Physical Intelligence, Emergence of Human to Robot Transfer: https://www.pi.website/research/human_to_robot (arXiv 2512.22414)
- EgoVLA: https://arxiv.org/abs/2507.12440
- SRT: https://arxiv.org/abs/2407.12998
- SRT-H: https://arxiv.org/abs/2505.10251
- Open-H-Embodiment: https://arxiv.org/abs/2604.21017 ; GR00T-H: https://github.com/NVIDIA-Medtech/GR00T-H
- ORBIT-Surgical: https://arxiv.org/abs/2404.16027 , https://orbit-surgical.github.io/
- SuFIA-BC: https://arxiv.org/abs/2504.14857
- RINSE (smoothness-based demo quality): https://arxiv.org/abs/2604.23000
- From memory, not re-verified this session: VideoDex (Shaw et al. 2022), MimicPlay (Wang et al. 2023), HumanPlus (Fu et al. 2024), OKAMI (Li et al. 2024), Track2Act (Bharadhwaj et al. 2024), HOP (Singh et al. 2024), SurRoL (Xu et al. 2021), SurgicAI (2024), Surgical Gym (Schmidgall et al. 2023), JIGSAWS (Gao et al. 2014), robomimic (Mandlekar et al. 2021).
