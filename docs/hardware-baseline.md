# Quest 3S Hardware Baseline

Measured October 3, 2026 on one physical Quest 3S using the official native Unity Passthrough Camera API samples and development telemetry. This is infrastructure evidence, not a working Scalpal surgery application.

This portable summary deliberately excludes personal device identifiers, local-machine paths, raw camera footage, participant imagery, credentials, and private test artifacts.

## Verified Infrastructure

| Item | Observed result | Meaning |
|---|---|---|
| USB debugging and deployment | Headset connection authorized; sample APK builds, installs, and launches | Native development/test path exists |
| Unity environment | Unity 6000.0.66f2; Unity Personal active | Existing experimental toolchain, not yet a pinned Scalpal dependency manifest |
| Device platform | Quest 3S; Android 14 / API 34 | Platform of these observations |
| Native left camera output | 1280 × 960 YUV | Actual tested output dimensions |
| Native camera freshness | 4,455 distinct frames over 75.26 seconds: **59.19 FPS** | Source frame freshness; zero duplicate/backward counters in those records |
| Sample camera texture | CameraViewer visibly renders the image and permission-granted state inside VR | Camera acquisition reaches native XR rendering |
| Stereo detection | Laptop and plastic bottle receive visible green labels/rectangles in both eyes | Detection plus surface placement demonstrated on these objects |
| Bottle surface rays | 150 hits and one miss in an earlier telemetry run; one observed hit around 0.63 m | Placement availability, not recognition accuracy or geometric error |
| Desktop mirror | Both eyes and passthrough visible; mirror capped at **30 FPS** | Spectator/audit view; not headset display FPS |
| Recovery | CameraViewer reacquired fresh frames after clean app exit/relaunch | One exercised lifecycle path; not complete pause/resume coverage |

Unity observers counted distinct timestamps seen during `LateUpdate`: CameraViewer median 59.18, CameraToWorld 59.21, and MultiObjectDetection 58.95 changes/second, including scene startup/focus transitions. These are not independent hardware frame counters.

## Inference Performance

The same 640 × 640, 80-class YOLO model and placement configuration were compared sequentially after 10 seconds of warmup. Camera content and user movement were not held identical.

| Backend | Focused telemetry windows | Median completed inferences/second | Median sampled last-result processing time |
|---|---:|---:|---:|
| CPU | 42 | **4.94** | **208.39 ms** |
| GPUCompute | 17 | **2.96** | **320.39 ms** |

GPUCompute was slower in this measured application path. Vulkan, Adreno 740, and compute-shader support were observed, but the cause of the regression was not established. The sampled processing duration includes the inference path's conversion/scheduling/readback behavior; it excludes sensor capture age and is not a full per-inference latency distribution.

The CPU remains the experimental default. Do not assume a new hand/body model will have the same performance or that selecting GPU will improve it. Benchmark the actual model, input bridge, output handling, and concurrent rendering workload.

The following quantities are distinct:

- Camera freshness: approximately 59 distinct frames/second.
- Detection throughput: approximately 4.94 completed inferences/second on CPU.
- Desktop mirror: capped at 30 frames/second.
- Headset display/render FPS and frame time: **not measured in this validation**.
- Sensor-to-display latency: **not measured**.

## Calibration and Timing

Runtime Camera2 metadata reported a 1280 × 1280 sensor, focal lengths of 851.86847 pixels, and sensor principal point `(638.02747, 640.92474)`. The observed crop was `(0, 160)` through `(1280, 1120)`, producing 1280 × 960 output and an approximately `(638.03, 480.92)` output principal point.

These intrinsics imply about 73.83° horizontal and 58.80° vertical field of view for the observed crop. The previous browser source assumed 80° horizontally. At a one-meter plane, that assumption produces a width approximately 11.69% larger than the calibrated width. This isolates a possible geometry error; it does not prove the browser used the same crop or explain the entire previous failure.

Saved native snapshots were preview copies, not exposure-synchronized captures. Nearby raw timestamps were not proven to be their capture times. Camera2 reported timestamp source `UNKNOWN`; comparing those timestamps directly with elapsed realtime would not establish image age. Preview readbacks were also unsynchronized with nearby pose metadata.

For new recording/registration work, verify exposure/frame identity, crop/resizing, camera-to-head transform, acquisition-time pose association, clock conversion, per-eye projection, and depth. Do not reuse this summary as proof those relationships are synchronized.

## What the Bottle Result Establishes

The user reported that the bottle box stayed attached during slow left/right head turns. The box was independently visible in both eye views. This is useful practical evidence, but there is no measured geometric-error bound or controlled head-motion accuracy study.

The official sample places a camera-facing rectangle using a calibrated ray through a YOLO rectangle's center and an environment surface hit. It does not estimate complete object shape/orientation or persistent object identity. A saved spawned marker remains at its world position if the object moves; detection rectangles may persist after recognition is lost.

The implementation caches camera pose before inference, but later environment raycasts do not take the RGB capture timestamp. Recognition can succeed while placement fails. Fresh camera frames can also coexist with an invalid tracked head pose; the tested guard skipped placement/inference rather than using that unreliable pose.

Two saved low-light images missed a visible shaker bottle in host inference. Later live recognition worked. These observations do not establish general bottle recall, all-lighting robustness, or performance of a different model.

## What Has Not Been Demonstrated

- Body landmarks or stable torso registration on a reclining person.
- Patient-specific internal anatomy, surgical-tool tip tracking, contact forces, tissue physics, or clinical accuracy.
- Permitted camera clip recording/export with synchronized virtual scene state.
- Video-derived hand reconstruction, articulated robot-hand retargeting, replay, or learned robot policies.
- Scalpal voice tools, actual Blender selection assets, a complete exercise, backend validation, wallet pairing, or rewards.
- Quantitative stereo alignment under head/object movement, total latency, sustained render performance, and complete focus/pause/resume behavior.

The supplied 80-class detector does not identify surgical instrument categories and does not output body or hand joint poses. A bottle detector is infrastructure evidence; it is not a torso tracker or motion-capture system.

## Native Workbench Follow-Up

The repository now has a separate OpenXR full-VR tool workbench. Its build/install, live tracking/pickup telemetry and the user-reported motion lag are recorded in [native workbench evidence](native-workbench.md). The user confirmed that held tools keep up after the interpolation/before-render correction; quantitative latency remains unmeasured. These new results do not change the earlier camera/inference measurements or establish surgery, MR fit or capture integration.

## Next Hardware Evidence

Test torso registration and video-to-hand replay as separate bounded experiments. For torso tracking, measure normal-visibility coverage, external-point alignment in both eyes, occlusion behavior, and recovery on the actual reclining participant. For recording, first establish a valid capture route and frame/pose/scene timing, then evaluate a short clip with visible fingers, known movement, and documented camera motion.

Use the CPU/GPU results as a warning to measure the complete new pipeline, not as a universal backend preference. Rehearse a full session only after individual links work, and record display settings, rendering frame time, inference cadence, tracking quality, processing time, and failure behavior.

See [Architecture](architecture.md) for the proposed stack and provisional targets. None of those targets are additional measured results in this baseline.

## Public References

- [Official Unity Passthrough Camera API samples](https://github.com/meta-quest/Unity-PassthroughCameraApiSamples), tested experimental reference revision `f9c382190907e20232a74c5ecf6513df47bc13b8`.
- [Placement implementation at the tested revision](https://github.com/meta-quest/Unity-PassthroughCameraApiSamples/blob/f9c382190907e20232a74c5ecf6513df47bc13b8/Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceUiManager.cs#L81).
- [Inference loop at the tested revision](https://github.com/meta-quest/Unity-PassthroughCameraApiSamples/blob/f9c382190907e20232a74c5ecf6513df47bc13b8/Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Scripts/SentisInferenceRunManager.cs#L92).
- [Meta camera documentation](https://developers.meta.com/vr/documentation/unity/unity-pca-overview/) and [environment raycasting](https://developers.meta.com/vr/documentation/unity/unity-mr-utility-kit-environment-raycast/).
- [Unity inference execution](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.2/manual/how-inference-engine-runs-a-model.html) and [asynchronous readback](https://docs.unity3d.com/Packages/com.unity.ai.inference@2.2/manual/read-output-async.html).
