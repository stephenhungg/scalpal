# TAPNet Family: Where It Fits Scalpal

Research snapshot: October 3, 2026. Recommendation, not an installed model or a measured Quest result.

## Decision

Try pretrained TAPNext++ as an offline video experiment after a permitted raw-camera recording route exists. Keep it out of the current native interaction loop until tested on the intended hardware. Virtual tool and anatomy positions already exist as Unity transforms; image tracking would add uncertainty to those known positions.

The [official Google DeepMind repository](https://github.com/google-deepmind/tapnet) contains the original TAP-Net baseline, TAPIR/BootsTAPIR and TAPNext/TAPNext++. These trackers follow query points across images and estimate visibility. They do not supply organ labels, a human hand skeleton, internal anatomy placement, or a robot controller. The repository declares Apache 2.0 for its software and linked pretrained checkpoints; datasets have separate conditions.

## Why TAPNext++

[TAPNext](https://tap-next.github.io/) processes frames causally and reports 1,024 query points at 5.33 ms on H100 and 23 ms on V100. The authors report substantial long-sequence failure beyond 150 frames. [TAPNext++](https://tap-next-plus-plus.github.io/) specifically improves long-duration tracking and recovering points after occlusion or leaving the frame. Its reported 5.18 ms/frame for 256 points and separate H100 window-mode throughput are different workloads. None establishes Quest speed, memory use, conversion compatibility, battery impact or rendering contention.

The official TAPIR live demo reports approximately 17 FPS on 480×480 images with a Quadro RTX 4000. The inspected distribution offers Python/JAX and PyTorch paths, not a verified Scalpal Unity/Android deployment. Our [YOLO hardware baseline](../hardware-baseline.md) measures another model and cannot predict TAP performance.

## Candidate Boundaries

| Use | Possible benefit | What remains required |
| --- | --- | --- |
| Recorded visible fingertips/tool surfaces | Follow a seeded point between detections; compare continuity and occlusion recovery | Semantic hand estimator, identity validation, camera motion, depth/scale, retargeting and joint limits |
| External torso surface | Maintain correspondences after an initial landmark fit | Calibrated capture geometry, synchronized pose, usable depth, deformation handling and measured registration error |
| Native virtual instruments | No tracking requirement: transforms are already known | Record actual simulation state and timing instead |
| Hidden organs/vessels | No observation supplied by a surface-point tracker | Generic teaching atlas, or a separately validated patient-imaging/registration workflow |

These uses are engineering inferences from the tracking interface, not medical validation. A clothing point follows clothing. A predicted location during occlusion is not an observed tissue position. Image coordinates change with headset movement even when the participant stays still.

[TAPVid-3D](https://tapvid3d.github.io/) is a dataset and benchmark for metric 3D tracks. It is not a plug-in that turns a monocular video into correct world-space robot motion.

## Bounded Experiment

1. Use one short permitted raw-camera clip with capture identity, dimensions, timestamps and available calibration. The [native session](../native-session.md) currently has no raw-video producer.
2. Annotate visible fingertip/tool-surface points and intervals of occlusion on the same clip. Run a pinned pretrained checkpoint off-device without training a custom model.
3. Compare against the existing hand-estimation route: pixel error at annotated frames, identity swaps, false visibility, recovery after occlusion, processing time and peak memory. Preserve missing/uncertain intervals.
4. Keep the hand model for joint semantics. Attempt metric retargeting only with an explicit frame/depth/scale model. A prettier trajectory is not proof of useful robot contacts or a learned policy.

Promotion requires evidence on actual footage and the intended compute device. Do not advertise this experiment as implemented, realtime on Quest, or an anatomy-registration solution.

For the reusable organ/vessel/case foundation, see [comprehensive anatomy](comprehensive-anatomy.md).
