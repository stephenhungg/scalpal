# 06: Raw passthrough + metadata capture on Quest 3S (tonight)

Status tags: **[V]** verified in Meta/Android docs or source, **[C]** community source or observed in OSS repos, **[U]** uncertain or inferred, test on device.

## TL;DR

Stop using the hand-rolled Camera2/WebCamTexture preview path for recording. Use MRUK `PassthroughCameraAccess` (MRUK v81+), which is what the official samples at f9c3821 are built on. It gives a per-image `Timestamp` and `GetCameraPose()` for that image, which fixes the "pose not exposure-synced" problem without clock-domain gymnastics. On each frame where `IsUpdatedThisFrame` is true, grab pixels + timestamp + camera pose + head pose + virtual tool poses, write a JPEG plus one JSONL line, then `adb pull`. Two OSS projects already do this (QuestRealityCapture, OpenQuestCapture, both MIT). Fallback: Quest built-in recording plus an external phone on a tripod.

## 1. Passthrough Camera API capabilities

- **Stereo: yes.** Both forward RGB cameras can stream at the same time. MRUK docs: "To access both left and right camera simultaneously, create two instances of PassthroughCameraAccess with different CameraPosition" [V]. Camera2 vendor tag `com.meta.extra_metadata.position` is 0 for left, 1 for right [V].
- **Resolutions:** v81+: 320x240, 640x480, 800x600, 1280x960. v83+ adds 640x360, 720x480, 720x576, 1024x576, 1280x720, 1280x1080, 1280x1280 [V]. If the requested size isn't supported, "the first smaller resolution will be selected", so always log `CurrentResolution` [V].
- **Frame rate:** 60 Hz, 20-40 ms capture latency, about 1-2% GPU per camera, about 45 MB memory, internal YUV420 [V]. `MaxFramerate` can only be set while the component is disabled [V].
- **WebCamTexture vs MRUK vs native Camera2.** WebCamTexture is the legacy path: no timestamps and one camera at a time, and Meta ships a migration guide away from it [V]. MRUK `PassthroughCameraAccess` adds simultaneous L/R, timestamps, intrinsics/extrinsics, `GetTexture()` (GPU) and `GetColors()` (CPU `NativeArray<Color32>`, flagged as expensive) [V]. Native Camera2 via NDK/JNI gives raw YUV and `SENSOR_TIMESTAMP` but leaves time correlation to you.
- **Camera2 timestamp source.** Matt's device reports `UNKNOWN`. Android: UNKNOWN timestamps "are in nanoseconds and monotonic, but can not be compared to timestamps from other subsystems... with accuracy. However, the timestamps are roughly in the same timebase as SystemClock.uptimeMillis()". REALTIME means the `elapsedRealtimeNanos()` base [V]. `SENSOR_TIMESTAMP` is start of exposure per Android docs [V, general Camera2 semantics]. In practice UNKNOWN ≈ CLOCK_MONOTONIC. Quest's XrTime is probably also monotonic-based, and OpenXR `XR_KHR_convert_timespec_time` could bridge them, but the offset and accuracy are unverified on Quest [U]. That's exactly why the MRUK path is preferred.
- **MRUK pose/intrinsics API** (replaces the older sample `PassthroughCameraUtils.GetCameraPoseInWorld/GetCameraIntrinsics`). `Timestamp` (DateTime) is the "timestamp associated with the latest camera image". `GetCameraPose()` "returns the world pose of the passthrough camera at the current timestamp", i.e. the image's timestamp, not render time [V]. `Intrinsics` gives FocalLength, PrincipalPoint, SensorResolution, LensOffset. These are static once enabled [V].
- **Intrinsics gotcha.** Intrinsics are reported against the full **sensor** (1280x1280 on 3S, matching the measured crop (0,160)-(1280,1120)). For a 1280x960 stream, fx = fy ≈ 851.87 stays the same and cy shifts by -160. If you downscale, scale f and c by `CurrentResolution / cropSize` [C/U: matches the measured crop, but verify by reprojecting a known point with `WorldToViewportPoint`]. Record the raw sensor intrinsics, crop, and stream resolution all three, and do the math offline.

## 2. Encoding/recording on device

- **Recommended: JPEG sequence.** `GetColors()` (or `AsyncGPUReadback` on `GetTexture()`), then `ImageConversion.EncodeNativeArrayToJPG` on a worker thread, then `File.WriteAllBytes`. Each frame is self-contained with its own timestamp, nothing to sync, and a crash loses only the tail. At 1280x960 q85 a frame is about 150-300 KB, so ~5-9 MB/s at 30 fps [U: estimate]. Encode cost on XR2 Gen 2 is roughly 10-25 ms per frame per core [U], so use a bounded queue of 2-3 worker threads and drop+log when the queue is full.
- **Raw RGBA** (QuestRealityCapture's MRUK default) is 4.9 MB/frame at 1280x960 (6.2 MB at 1280x1280) [C]. At 30 fps that's ~150 MB/s of flash writes, which risks stalls and fills storage fast. Only do it at a low rate (QRC defaults to 10 fps for cameras) [C].
- **H.264 via MediaCodec/MediaRecorder.** Needs a Java/Kotlin plugin plus a Surface input and per-frame PTS bookkeeping. Skip it tonight. Build the mp4 on the Mac with `ffmpeg -framerate 30 -i %06d.jpg` if needed.
- **Existing projects to steal from:**
  - **QuestRealityCapture** (t-34400, MIT): MRUK or native Camera2 backend, stereo, per-frame CSV (`frame_index, unix_time_ms, timestamp_us_realtime, pose_pos/rot`), intrinsics JSON, HMD/controller pose CSVs with `unix_time, ovr_timestamp`, depth. Its spec requires that camera pose "must come from MRUK camera pose for the recorded frame, not from a later current-frame HMD pose sample" [C]. Built on Unity 6000.4.5f1, so cherry-pick scripts rather than open the project in 6000.0 [C].
  - **OpenQuestCapture** (samuelm2, MIT): raw YUV plus `baseMonoTimeNs/baseUnixTimeMs` anchors, default 3 FPS [C].
- **Storage/transfer.** Write to `Application.persistentDataPath` (`/sdcard/Android/data/<pkg>/files/`). No storage permission is needed on Android 14. Pull with `adb pull /sdcard/Android/data/<pkg>/files/<session> .`. Wi-Fi adb works but is slow, so use USB-C.
- **Frame-drop detection.** Log every frame with `Timestamp` and keep these per-session counters: frames seen (`IsUpdatedThisFrame`), frames encoded, frames dropped (queue full). Offline, flag deltas over 1.5x the nominal period, and also catch duplicates (same timestamp twice). Also log Unity `Time.frameCount`/`Time.realtimeSinceStartupAsDouble` so render hitches are visible.

## 3. Metadata sidecar (one JSONL line per recorded frame)

```json
{"i":412,"cam":"L","file":"L/000412.jpg","img_ts_unix_us":1759525000123456,
 "unity_t":83.4412,"unity_frame":5006,"ovr_t":12345.678,
 "cam_pose":{"p":[x,y,z],"q":[x,y,z,w]},
 "head_pose":{"p":[...],"q":[...]},"head_pose_src":"centerEye@render",
 "tracking":{"hmd_tracked":true,"hand_L":true,"hand_R":true},
 "tools":[{"id":"scalpel","p":[...],"q":[...],"grabbed_by":"R","state":"cutting"}],
 "res":[1280,960],"dropped_since_last":0}
```

Put the static data in `session.json`: intrinsics (focal, principal point, sensor res, lens offset), stream res, crop, OS/MRUK versions, Unity world-origin convention (left-handed, Y-up, meters), tracking origin type (floor/eye), and recenter events. Convert Unity left-handed coordinates to a right-handed robot frame offline, not on device. Virtual tool poses are sampled at render time, not exposure time, so log `unity_t` and interpolate offline. Error is ≤1 frame of render latency (about 14-28 ms), which is fine for retargeting [U].

## 4. Hand tracking and the data policy

- Meta's hand-tracking docs say hand pose and hand size data are "only permitted to be used for enabling hand tracking within your app and is expressly forbidden for any other purpose" [V]. Writing `OVRSkeleton` joints to disk to train or retarget a robot is a stretch of that wording.
- The Developer Data Use Policy classes camera data, headset position, and "data calculated about a user's hands and body" all as **Device User Data**. Allowed uses are running/improving your content and analytics (aggregated/de-identified). You need a public privacy policy and user permission, can share with third parties only with express consent, must delete when no longer needed, and must not send any of it to Meta business tools [V].
- **Practical read [U, not legal advice]:** for a hackathon demo on a team member's own hands with consent, use live in-app hand tracking (e.g. to drive the virtual tool, or a live validation overlay), and keep exported hand joints to a dev-only debug flag. Derive the "ground truth" offline from the raw video (MediaPipe/HaMeR/WiLoR) instead of exporting Meta's joints. In the pitch, say camera capture is opt-in and data stays local.

## 5. Alternatives if native capture fails

| Option | Pros | Cons for hand extraction |
|---|---|---|
| Quest built-in recording | Zero code, includes virtual tools | Composited view (virtual objects occlude hands), single eye, compressed, no per-frame pose or intrinsics. Default about 1024x1024 at 30 fps, and `debug.oculus.capture.*` setprops can raise it [C] |
| scrcpy / `adb shell screenrecord` mirror | Live on the Mac, easy | Same compositor mirror (distorted/cropped eye buffer, composited), mirror timing unrelated to camera exposure [C/U] |
| External phone on a tripod | Most robust, high res, clean hands, 60-240 fps | Third-person view, no headset pose. Sync with a clap or LED flash visible in both, and calibrate extrinsics with a checkerboard/ArUco marker |
| MRUK capture (recommended) | Egocentric, exposure-time pose, intrinsics | Needs about 2-3 h of code. Virtual objects are absent from images, so the sidecar carries them |

## 6. Plan for tonight

1. **(20 min)** Bump MRUK to v81+ (match the samples' `Packages/manifest.json`). Make sure `horizonos.permission.HEADSET_CAMERA` is in the manifest (Unity 6 needs it re-added [V]) and that Horizon OS is v74+ (v83+ if you want 1280x1280).
2. **(30 min)** Add one or two `PassthroughCameraAccess` components (L, optional R), `RequestedResolution=1280x960`, `MaxFramerate=30` set while disabled. Log `CurrentResolution` and `Intrinsics` into `session.json`.
3. **(60 min)** `CaptureRecorder.cs`: in `Update`, when `IsUpdatedThisFrame`, read `Timestamp`, `GetCameraPose()`, the center-eye pose, hand-tracking validity bools, and the tool transforms. Copy `GetColors()` into a pooled buffer and enqueue it. Worker threads JPEG-encode and write, then append the JSONL line. Start/stop on a controller button or pinch, with a REC indicator plus a dropped-frame count in the HUD.
4. **(20 min)** Validation on device: project the tool tip with `WorldToViewportPoint` onto the saved JPEG offline and check it lands where a physical marker is. Check timestamp deltas.
5. **(15 min)** `adb pull` the session, then a Python loader that checks deltas and gaps and runs a hand detector on a few frames.
6. **Fallback, start it in parallel now (10 min):** phone on a tripod at 60 fps filming the hands, plus Quest built-in recording, synced by a hand clap. If the MRUK recorder isn't solid by about 1 AM ET, ship the phone video pipeline.

Total: about 2.5-3 h with buffer.

## Sources

- Meta, Passthrough Camera API overview: https://developers.meta.com/horizon/documentation/unity/unity-pca-overview/
- Meta, Getting Started with PCA in Unity (resolutions, Timestamp, GetCameraPose): https://developers.meta.com/horizon/documentation/unity/unity-pca-documentation/
- Meta, Migration from WebCamTexture to PassthroughCameraAccess: https://developers.meta.com/vr/documentation/unity/unity-pca-migration-from-webcamtexture/
- Meta, MRUK `PassthroughCameraAccess` reference v83: https://developers.meta.com/horizon/reference/mruk/v83/class_meta_x_r_passthrough_camera_access/
- Meta, native Android PCA overview (vendor tags): https://developers.meta.com/horizon/documentation/native/android/pca-native-overview/
- oculus-samples/Unity-PassthroughCameraApiSamples: https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples
- Android `CameraMetadata` (SENSOR_INFO_TIMESTAMP_SOURCE_UNKNOWN/REALTIME): https://developer.android.com/reference/android/hardware/camera2/CameraMetadata
- t-34400/QuestRealityCapture (+ specs/mruk_recording_format.md): https://github.com/t-34400/QuestRealityCapture
- samuelm2/OpenQuestCapture: https://github.com/samuelm2/OpenQuestCapture
- Meta, Developer Data Use Policy: https://developers.meta.com/horizon/policy/data-use/
- Meta, Enable Hand Tracking (data use restriction): https://developers.meta.com/horizon/documentation/native/android/mobile-hand-tracking/
- Meta, Hand and Body Privacy Notice: https://www.meta.com/legal/quest/hand-tracking-privacy-notice/
