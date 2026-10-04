# Hands: MediaPipe Joints From the Quest

> **Not in the current demo.** Surgery in the headset uses Quest controllers, so the robot input is controller motion: see `apps/quest/Assets/Scalpal/Robotics` and `scalpal-motion teleop` in `services/motion`. This module stays as a verified option for camera-based hand tracking.

Owner: Matthew.

Live MediaPipe hand joints are the shared signal for three things:
- the overlay the learner sees on their real hands in the headset
- the live robot-hand mirror on the laptop
- the recorded episodes that become robot training data

The headset runs the same MediaPipe models the robot pipeline uses, so what the learner sees is exactly what the robot learns from.

```
Quest passthrough frame
  -> MediaPipe palm detector + 21-joint landmarker (Unity Inference Engine, on the headset)
  -> joints lifted into world space with the frame's camera pose
  -> HandSkeletonView (overlay) | HandStreamer (UDP to the Mac) | HandEpisodeRecorder (consented JSONL)
Mac: scalpal-motion live / import-episode (services/motion) -> Shadow hand retargeting -> MuJoCo
```

| Piece | Where |
| --- | --- |
| Headset module | `apps/quest/Assets/Scalpal/Hands/` (`BlazeHandTracker`, `HandSkeletonView`, `HandStreamer`, `HandEpisodeRecorder`) |
| Wire format | `scalpal.hand_joints.v1`, one JSON object per frame (`HandJointsFrame.cs`) |
| Live mirror, stand-in sender, episode import | `services/motion` (`scalpal-motion live`, `send`, `import-episode`) |
| Reference check | `reference.py` here |

## Verification

`reference.py` runs the exact ONNX files the headset runs (MediaPipe's hand models, converted by Unity) with the same crop math as the C# port, and compares the joints with Google's MediaPipe HandLandmarker:

| Path | Mean joint error vs MediaPipe |
| --- | --- |
| Palm detector crop | 1.4% of hand size |
| Frame-to-frame tracking crop (1.8x extent) | 1.8% of hand size |

`tests/golden.json` holds its intermediate values. `npm run test:unity` in `services/preop` checks the C# `BlazeHandMath` against those values, and compiles the whole hand module against API stand-ins for MRUK 85 and Inference Engine.

```sh
uv sync
uv run python reference.py path/to/hand-photo.jpg --golden tests/golden.json
```

## Not verified yet

- On-headset frame rate. The camera baseline ran a larger YOLO model at about 5 inferences per second on CPU, and GPUCompute was slower; benchmark both backends here.
- Overlay alignment on real hands.
- The texture color-space flag (`convertToSrgb`).
- Monocular depth bias of the lifted joints.

## Setting it up in a Quest project

1. The project needs `com.unity.ai.inference` and `com.meta.xr.mrutilitykit` (85+). The camera baseline in `experiments/quest-camera-baseline` already has both. `Editor/ScalpalHandsDefine.cs` then turns on `SCALPAL_HANDS`. Without those packages the module compiles to nothing.
2. Run **Scalpal > Hands > Set Up Live Hands In Scene**. It finds or adds the `PassthroughCameraAccess`, then adds `BlazeHandTracker`, `HandSkeletonView`, `HandStreamer`, and `HandEpisodeRecorder` with models, anchors, and references wired. It also creates an unlit joint material and enables the Android internet permission.
3. Set `HandStreamer.host` to the Mac's LAN IP.
4. To record demo clips without UI, tick `HandEpisodeRecorder` → **Operator Confirmed Consent** (only after the participant agrees) and **Record On Start**. Each app launch then records one episode under `files/scalpal-hands/`.
5. On the Mac: `cd services/motion && uv run scalpal-motion live`. Pull recordings with `adb pull /sdcard/Android/data/<package>/files/scalpal-hands` and import them with `uv run scalpal-motion import-episode <episode>`.

Joints computed from camera images are Meta Device User Data: show them in-app freely, and record or export them only with participant consent.
