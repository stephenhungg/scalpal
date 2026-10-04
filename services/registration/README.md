# Local body-pose boundary

This service estimates body landmarks from an explicitly submitted Quest camera JPEG. Unity owns camera calibration, live surface-depth projection, generic torso fitting, confidence checks, and anatomy suppression when registration is invalid. Integrated AR entry starts acquisition after silent capability checks; this service cannot start it. See the [AR audit](../../docs/ar-surgery-audit.md). The service does not capture a camera, identify a person, train a custom model, or decide whether anatomy is correctly aligned.

The pinned estimator is Google's **MediaPipe Pose Landmarker Lite, float16, version 1**, using MediaPipe **0.10.35**, CPU inference, `IMAGE` mode, and `num_poses=2`. See the official [Python task guide](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker/python) and [model/coordinate documentation](https://developers.google.com/edge/mediapipe/solutions/vision/pose_landmarker).

## Run locally

Requires Python 3.11 and `uv`. From `services/registration/`:

```sh
uv sync --frozen
uv run --frozen scalpal-registration fetch-model
uv run --frozen pytest -q
uv run --frozen scalpal-registration serve
```

The model download is explicit and separate from serving. It uses only the official versioned Google URL below, checks SHA-256 before installing the cached model, and refuses a changed model. The model cache and virtual environment are ignored by Git.

```text
https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task
SHA-256: 59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a
```

`serve` binds **127.0.0.1:8790** only. `--port` changes the port, not the bind address; port `0` chooses an ephemeral loopback port for tests. Requests are processed serially so a single MediaPipe task is never accessed concurrently. Each socket has a five-second read timeout. The server makes no network/provider calls during inference. It stores no frame files, image history, body landmarks, participant identifiers, or request logs; submitted pixels and the result exist only in process memory while processing the request. This is bounded local inference, not a secure memory-erasure guarantee.

For a USB-attached Quest, the host integration can route a native application's loopback requests using `adb reverse tcp:8790 tcp:8790`. This command and the native capture path are owned by the Quest integration; running this Python service alone does not start participant capture. Integrated Theatre AR selection has no extra volunteer-consent checkbox or OS permission request; Unity requires the existing camera/spatial grants and automatically starts detection. Optional recording/export remains separate from transient registration inference.

## HTTP contract

`GET /health` returns `{"schema":"scalpal.body_pose.v1","status":"ready"}` after the pinned model has loaded.

`POST /pose` accepts raw JPEG bytes, not JSON, multipart, Base64, a path, or a URL. Required headers:

```text
Content-Type: image/jpeg
Content-Length: <encoded byte count>
X-Frame-Id: <1–128 printable ASCII characters>
```

Maximum encoded frame size is **2,097,152 bytes**; maximum decoded image size is **16,000,000 pixels**. Missing length, duplicate length/frame headers, chunked transfer, other image types, corrupt JPEGs, and oversized frames are rejected before inference. Sensor pixel orientation is preserved: no EXIF rotation, mirroring, crop, or rescale is applied. Unity must associate each echoed `frameId` with the exact image dimensions, pose/camera timestamp, intrinsics, and capture transform used for that frame.

A successfully processed image returns HTTP 200 even if no usable person is observed. Example below is **synthetic response formatting**, not a measured body pose; an actual valid result contains all 33 records:

```json
{
  "schema": "scalpal.body_pose.v1",
  "frameId": "camera-frame-42",
  "imageWidth": 1280,
  "imageHeight": 960,
  "coordinateConvention": "normalized_image_top_left",
  "model": {
    "name": "mediapipe/pose_landmarker_lite/float16",
    "version": "1",
    "sha256": "59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a",
    "mediapipeVersion": "0.10.35"
  },
  "inferenceMs": 20.0,
  "personCount": 1,
  "valid": true,
  "reason": "",
  "landmarks": [
    {"index": 0, "x": 0.5, "y": 0.4, "z": -0.1, "visibility": 0.9, "presence": 0.8}
  ]
}
```

`inferenceMs` measures only `PoseLandmarker.detect`; it excludes JPEG decode, transport, queuing, Unity projection, and rendering. The frame ID, rather than this duration, establishes the capture association.

`valid=false` always produces `landmarks=[]`. Reasons are `no_person` (zero detected poses), `ambiguous_people` (more than one detected pose), or `invalid_landmarks` (not exactly 33 finite records, missing confidence, or confidence outside [0,1]). `personCount` is the number of poses the detector returned, capped by `num_poses=2`; it is not an assurance that all people in the scene were detected. A single well-formed pose is an image-space observation, not permission to enable surgery interactions. Unity still rejects stale frames, low torso visibility, invalid body geometry, or missing surface depth/calibration.

Input/protocol errors return an `{"error":"<code>"}` object: 400 for invalid framing/image/ID, 411 for missing length, 413 for oversized frames, 415 for wrong content type, 408 for a frame read timeout, 404 for unknown routes, and 500 for inference failure. All JSON responses set `Cache-Control: no-store`; error bodies never echo pixels, headers, or exception text. The loopback endpoint intentionally has no remote authentication or CORS access and must not be publicly exposed.

## Coordinates and integration limits

`x` increases right and `y` increases down from the image's top-left. They are normalized by submitted image width/height. Raw predictions can extend outside the image and are preserved without clamping; Unity must reject unusable bounds. `z` is relative to the hip midpoint, with smaller values closer to the camera, at approximately the same scale as normalized `x`. **This `z` is not camera-space metric depth and does not provide Quest-world depth.** World landmarks are deliberately omitted from this contract.

Shoulders are indices **11/12**, hips **23/24**, elbows **13/14**, and wrists **15/16**. Unity forms calibrated image rays from shoulder/hip points and intersects them with an anterior torso plane estimated from distributed live depth probes. This is not a known-table intersection, body segmentation or a measured local abdominal surface. Torso shape/offset, stability, headset motion and occlusion remain Unity registration acceptance requirements. Neither this detector nor a camera projection locates internal organs, reconstructs patient anatomy, or makes the simulator clinically valid.

The official model is not yet verified here for a person lying on a table. Rotation, foreshortening, drapes, arms covering the torso, partial framing, and more than one person can invalidate it. Headset testing must prove those assumptions before relying on the overlay.

## Verification scope

Tests exercise real CPU inference on a uniform 320×240 JPEG generated in memory and the actual loopback HTTP boundary. The blank image must return zero poses and an empty invalid result. Separate, explicitly synthetic landmark fixtures verify multi-person rejection, 33-index output, nonfinite/missing data rejection, and confidence preservation. Input tests cover malformed JPEGs, other formats, encoded/decompressed size limits, duplicate/missing headers, and the model hash check.

These tests demonstrate the service boundary and no-person failure path. They do not measure real-body detection accuracy, lying-person robustness, multi-person detection recall, Quest frame-to-result latency, or world overlay alignment. No participant video/image is downloaded, captured, or retained by the tests.
