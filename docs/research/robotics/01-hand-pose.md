# 01: Hand motion from Quest 3S passthrough video (Scalpal)

Researched 2026-10-03. Confidence notes are inline; "uncertain" means I could not confirm it from a primary source.

## TL;DR

- **Keep MediaPipe as the realtime/default backbone.** It's the only option here that's fast on a Mac CPU with zero setup. Fix the real gap, which is wrist pose: recover metric camera-frame wrist translation and orientation with **`cv2.solvePnP`** (MediaPipe metric world landmarks paired with 2D image landmarks, using the calibrated K), then compose with the **per-frame headset camera pose** logged from the Quest.
- **Log `GetCameraPose()` and `Timestamp` per frame on the Quest NOW.** Without head pose, "world frame" from video alone needs SLAM (the HaWoR route), and that's CUDA-only and heavy. Logging the pose is the single highest-leverage change.
- **Optional quality upgrade:** WiLoR (via `WiLoR-mini`, which runs on CPU and probably MPS) for MANO output that's more robust under occlusion. HaWoR, Hamba and Dyn-HaMR need NVIDIA, so they're out for a ~15 h Mac-only window unless someone has a CUDA box.

## 1. Model options

| Model | Output | Mac (CPU/MPS)? | Speed | License | Notes |
|---|---|---|---|---|---|
| **MediaPipe Hand Landmarker** | 21 2D landmarks + 21 metric "world" landmarks (hand-centered), handedness, score | Yes, CPU | Realtime (~ms/frame) | Apache-2.0 | Weak under heavy occlusion and hand-object contact. No camera-space translation. Fingers are good (you measured r=0.93-0.97). |
| **HaMeR** (CVPR'24) | MANO pose/shape + weak-persp. cam translation | Probably runs on CPU/MPS (ViT-H backbone, slow, ~0.5-2 s/frame on CPU, my estimate). Needs a separate hand detector (ViTPose/Detectron2), which is painful on Mac | GPU realtime-ish | Code MIT, MANO is non-commercial | Very robust single-frame model. |
| **WiLoR** (CVPR'25) | Detector + MANO, cam translation | `WiLoR-mini` pip wrapper runs on CPU (MPS not documented; try it) | 130-175 FPS on RTX 4090 per paper | CC-BY-NC-ND + MANO | Best frame-wise accuracy/speed tradeoff, with its own detector built in. **Best swap candidate.** |
| **Hamba** (NeurIPS'24) | MANO | No. Mamba SSM kernels need CUDA | n/a | check repo | Skip. |
| **HaWoR** (CVPR'25 Highlight) | **World-space** MANO trajectories + camera trajectory from egocentric video | No. Masked DROID-SLAM + Metric3D, tested on CUDA 11.7 | Offline, minutes per clip (uncertain) | CC-BY-NC-ND | Purpose-built for exactly this problem, including infilling hands that leave the FOV. Use only if a CUDA machine is available. |
| **Dyn-HaMR** (CVPR'25 Highlight) | 4D two-hand motion from moving camera, optimization-based | No (CUDA, SLAM) | Slow, per-sequence optimization | check repo | Offline gold-standard style. |
| 2026 research: **EgoForce** (forearm-guided camera-space pose, up to -28% MPJPE on HOT3D), **StableHand** (world-space dual-hand flow matching), **ACE-Ego-Hand** and "video diffusion for hand motion" (occlusion-robust), **MEgoVista** (metric 4D hands+head) | MANO | Unknown, likely CUDA | Unknown | Unknown | Code availability unverified. Don't plan around these tonight. |

Takeaway: frame-wise regressors (HaMeR/WiLoR) give camera-space MANO. World-space needs camera motion, which you get from SLAM (HaWoR/Dyn-HaMR) **or for free from the Quest's own tracking**.

## 2. Metric wrist pose in the world frame

**What the Quest gives you.** Meta's `PassthroughCameraAccess` (Unity, Horizon OS v74+) exposes **both cameras simultaneously** (`CameraPosition` left/right), a per-image `Timestamp`, `GetCameraPose()` ("camera's world-space pose... uses current timestamp"), and static `Intrinsics`. Meta documents up to 1280x960 at **30 FPS per camera**. Your measured ~59 FPS doesn't match that. Verify you aren't getting duplicated frames, and check real timestamps.

**Recipe A: monocular PnP (recommended, ~2-3 h).**
1. MediaPipe gives `world_landmarks` (meters, origin near hand center, scale from a learned average hand) and `landmarks` (normalized 2D).
2. `cv2.solvePnP(world_lms, image_lms_px, K, dist=None, flags=SOLVEPNP_SQPNP)`, then refine with `SOLVEPNP_ITERATIVE` seeded from the last frame. This yields R, t of the hand in the camera frame, so you get wrist position and orientation directly.
3. World pose: `T_world_hand = T_world_cam(t) · T_cam_hand`, with T_world_cam from logged `GetCameraPose()` at the frame timestamp. Mind Unity's left-handed frame vs OpenCV's (flip Y), and that the pose is the camera's, not the head's (the API already returns camera pose).
4. Error: depth scales with real/assumed hand size, so expect ~10-15% depth bias for non-average hands (my estimate). Fix it with a one-time scale calibration: hold the hand at a known distance, or use the stereo below once.

**Recipe B: stereo triangulation (~4-6 h, riskier).** Run MediaPipe on both cameras and triangulate the 21 joints with both cameras' poses and intrinsics. That gives true metric depth with no hand-size prior. Costs: double recording bandwidth, synced timestamps, and handedness/ID matching across views. Worth it only if Recipe A's depth jitter is visibly bad. A cheap middle ground is to use stereo once to estimate the user's hand scale, then run mono.

**Recipe C: model translation (HaMeR/WiLoR `cam_t`).** These predict translation assuming a fixed focal length on the crop. You must re-project with the real fx=851.87 and the crop box. Fine, but it's no better than PnP and needs more plumbing.

**Depth API:** low-res environment depth, and the `RemoveHands` option explicitly strips hands out. Not useful for hand depth. Skip.

**No head pose logged?** Then "world frame" means either camera-frame only (fine for a demo if the head is mostly still) or HaWoR/DROID-SLAM offline on CUDA.

## 3. Failure modes and mitigations (egocentric, surgical-ish)

- **Self-occlusion and hand-object occlusion** (real props, pinch grips on instruments): MediaPipe fingers hallucinate or collapse. Mitigate with confidence gating (handedness score and presence <0.5 means drop the frame), interpolate short gaps (<150 ms), and hold the last pose on longer gaps. WiLoR/HaMeR are more robust here.
- **Motion blur** from fast head turns: drop frames whose camera angular velocity (from logged poses) exceeds a threshold, and lock exposure if the API allows (uncertain).
- **Handedness swaps:** egocentric left/right is stable spatially. Enforce temporal consistency with Hungarian matching on wrist position, and check that MediaPipe's label isn't mirror-flipped (it assumes selfie-mirrored input, so on unmirrored video "Left" and "Right" can come out swapped; verify on one clip).
- **Hands leaving the FOV / at the edge:** the 1280x960 FOV is narrow-ish, and hands near the bottom get cut. Gate on all 21 2D landmarks being inside the frame.
- **Low light:** passthrough cameras are noisy indoors. Light the demo table well (cheapest fix there is).
- **Jitter:** apply a One Euro filter per joint and on wrist translation. For offline replay, use **bidirectional** smoothing: zero-phase Savitzky-Golay or RTS smoother over the whole clip, plus a SLERP-based filter on wrist rotation. Run MediaPipe forward and on the reversed video, then fuse by per-frame confidence. That's cheap, and it fixes init lag.
- **Virtual instruments:** the hand is unoccluded by the virtual tool in raw camera video, which is good for tracking. Grasp poses with nothing in hand, though, can be ambiguous to models trained on HOI.

## 4. Validation datasets and a quick sanity check

- **HOT3D** (Meta, CVPR'25): Aria and **Quest 3** recordings with mocap GT hands (MANO/UmeTrack) and camera poses. That's the closest domain, but Quest 3 streams there are the monochrome SLAM cameras, not color passthrough. HOT3D-Clips is the easy subset.
- **EgoDex** (Apple, 2025): 829 h of Vision Pro video, 1080p/30 Hz, 25 joints per hand SE(3) plus camera extrinsics. GT comes from the device tracker, not mocap. Great for "hand-in-world via headset pose", the same structure as your pipeline. 2 TB total, so grab a single task shard.
- ARCTIC (bimanual articulated objects, has an egocentric view), H2O (egocentric 2-hand + object), Assembly101 (egocentric hand poses, noisier GT), and Ego-Exo4D (hand keypoints, large) are reference only. Too big or slow to set up tonight.

**Sanity checks you can actually do tonight (<1 h each):**
1. **Self-consistency vs Meta's own tracker:** record the SDK hand joints *for evaluation only* alongside the video and compare wrist position RMSE and finger angles. That's in-app use (check the policy wording with the team), and the robot input stays video-derived.
2. **Known-geometry test:** put the wrist on tape marks 20/40/60 cm from the headset and check PnP depth error. Tap a fixed point on the table repeatedly while moving the head; world-frame wrist position should stay put (measures head-pose composition error).
3. **EgoDex shard:** run the pipeline on ~5 clips and compute wrist MPJPE vs GT in world frame, plus PA-MPJPE for fingers.

## 5. Recommended recipe (priority order)

1. **Log per-frame camera pose + timestamp + intrinsics on the Quest** alongside the video (JSONL keyed by timestamp). Effort 1-2 h. Highest leverage, and it can't be recovered later.
2. **PnP wrist pose** from MediaPipe world+image landmarks with real K, composed with camera pose, feeding the wrist 6-DoF into MuJoCo as a mocap/free-joint target (Shadow hand on a floating base). Effort 2-3 h.
3. **Confidence gating, gap interpolation, handedness consistency, and handedness flip check.** Effort 1-2 h.
4. **Offline bidirectional smoothing** (Savitzky-Golay / One Euro, plus SLERP on rotation) before dex-retargeting. Effort 1 h.
5. **Accuracy check** with tape marks and the static-point test (and SDK joints as eval-only reference if allowed). Effort 1 h. Gives a number to put on the demo slide.
6. *Stretch:* WiLoR-mini as a fallback when MediaPipe confidence is low, or as the primary for occluded grasps. Effort 3-4 h, and Mac compatibility needs a test first. Stereo hand-scale calibration, 2-3 h.
7. *Skip unless CUDA appears:* HaWoR / Dyn-HaMR / Hamba.

**License flag:** MANO, WiLoR, and HaWoR are non-commercial (NC-ND). Fine for a hackathon, but MediaPipe (Apache-2.0) keeps any future product path clean.

## Sources

- HaWoR: https://arxiv.org/abs/2501.02973 , https://github.com/ThunderVVV/HaWoR , https://hawor-project.github.io/
- WiLoR: https://arxiv.org/abs/2409.12259 , https://github.com/rolpotamias/WiLoR ; WiLoR-mini: https://github.com/warmshao/WiLoR-mini
- HaMeR: https://geopavlakos.github.io/hamer/
- Hamba: https://arxiv.org/abs/2407.09646 , https://github.com/humansensinglab/Hamba
- Dyn-HaMR: https://github.com/ZhengdiYu/Dyn-HaMR
- EgoForce: https://arxiv.org/abs/2605.12498 ; StableHand: https://arxiv.org/abs/2605.18553 ; ACE-Ego-Hand: https://arxiv.org/abs/2608.20308 ; Video diffusion for hand motion: https://arxiv.org/abs/2606.30308 ; MEgoVista: https://arxiv.org/abs/2609.16684 ; EgoGrasp: https://arxiv.org/abs/2601.01050
- Meta Passthrough Camera API: https://developers.meta.com/vr/documentation/spatial-sdk/spatial-sdk-pca-overview/ , https://developers.meta.com/vr/documentation/unity/unity-pca-migration-from-webcamtexture/ , https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples
- Meta Depth API hands removal: https://developers.meta.com/vr/documentation/unity/unity-depthapi-hands-removal/
- MediaPipe Hands: https://github.com/google-ai-edge/mediapipe/blob/master/docs/solutions/hands.md
- HOT3D: https://arxiv.org/abs/2411.19167 , https://facebookresearch.github.io/hot3d/
- EgoDex: https://arxiv.org/abs/2505.11709 , https://github.com/apple/ml-egodex
