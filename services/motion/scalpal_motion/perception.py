"""Decode a clip and estimate one hand's landmarks per frame with MediaPipe Hand Landmarker.

Every decoded frame produces a record. Frames without a usable hand are kept with
valid=False and a reason, so later stages never mistake a gap for observed motion.
"""

from __future__ import annotations

import urllib.request
from pathlib import Path

import cv2
import mediapipe as mp
from mediapipe.tasks.python import BaseOptions, vision

from . import HAND_TRACK_SCHEMA
from .paths import HAND_LANDMARKER_MODEL

MODEL_NAME = "mediapipe/hand_landmarker float16 (latest)"
MODEL_URL = "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task"


def ensure_model(path: Path = HAND_LANDMARKER_MODEL) -> Path:
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        print(f"downloading {MODEL_URL}")
        urllib.request.urlretrieve(MODEL_URL, path)
    return path


def _flip(label: str) -> str:
    return {"Left": "Right", "Right": "Left"}[label]


def track_hand(
    video_path: str | Path,
    hand: str = "Right",
    mirrored: bool = False,
    min_detection_confidence: float = 0.5,
    min_presence_confidence: float = 0.5,
    min_tracking_confidence: float = 0.5,
    model_path: str | Path | None = None,
) -> dict:
    """Return a hand-track record for one hand ("Right" or "Left") of the person in the clip.

    MediaPipe labels handedness assuming a mirrored (selfie) image. Quest passthrough
    and most rear/head-mounted footage is not mirrored, so labels are flipped unless
    mirrored=True.
    """
    model_path = model_path or ensure_model()
    video_path = Path(video_path)
    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise FileNotFoundError(f"could not open video: {video_path}")
    fps = cap.get(cv2.CAP_PROP_FPS) or 0.0
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

    options = vision.HandLandmarkerOptions(
        base_options=BaseOptions(
            model_asset_path=str(model_path),
            # Keep inference on CPU. mediapipe 1.0.x aborts on Apple Silicon in its Metal helper
            # even with this set, which is why pyproject pins 0.10.x.
            delegate=BaseOptions.Delegate.CPU,
        ),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=2,
        min_hand_detection_confidence=min_detection_confidence,
        min_hand_presence_confidence=min_presence_confidence,
        min_tracking_confidence=min_tracking_confidence,
    )

    frames = []
    last_ts_ms = -1
    nonmonotonic_pts = 0
    with vision.HandLandmarker.create_from_options(options) as landmarker:
        index = 0
        while True:
            ok, bgr = cap.read()
            if not ok:
                break
            pts_ms = cap.get(cv2.CAP_PROP_POS_MSEC)
            ts_ms = int(round(pts_ms))
            # The landmarker requires strictly increasing timestamps. Record when the
            # container's presentation times did not provide that instead of hiding it.
            if ts_ms <= last_ts_ms:
                nonmonotonic_pts += 1
                ts_ms = last_ts_ms + 1
            last_ts_ms = ts_ms

            rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
            result = landmarker.detect_for_video(
                mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), ts_ms
            )

            record = {"frame": index, "t_ms": pts_ms, "valid": False}
            detected = []
            for i, cats in enumerate(result.handedness):
                raw = cats[0].category_name
                label = raw if mirrored else _flip(raw)
                detected.append(label)
                if label != hand or "image" in record:
                    continue
                record.update(
                    valid=True,
                    handedness_score=float(cats[0].score),
                    image=[[p.x, p.y, p.z] for p in result.hand_landmarks[i]],
                    world=[[p.x, p.y, p.z] for p in result.hand_world_landmarks[i]],
                )
            if not record["valid"]:
                record["reason"] = "other_hand_only" if detected else "no_hand"
            record["hands_detected"] = detected
            frames.append(record)
            index += 1
    cap.release()

    valid = sum(f["valid"] for f in frames)
    return {
        "schema": HAND_TRACK_SCHEMA,
        "source": {
            "video": video_path.name,
            "width": width,
            "height": height,
            "fps_reported": fps,
            "frame_count": len(frames),
            "time_domain": "decoded video presentation time, ms from clip start",
            "nonmonotonic_pts": nonmonotonic_pts,
        },
        "model": {
            "name": MODEL_NAME,
            "mediapipe": mp.__version__,
            "min_detection_confidence": min_detection_confidence,
            "min_presence_confidence": min_presence_confidence,
            "min_tracking_confidence": min_tracking_confidence,
        },
        "hand": hand,
        "mirrored_input": mirrored,
        "coordinates": {
            "image": "normalized [0,1] x right, y down; z relative depth, wrist-origin, roughly x scale",
            "world": "MediaPipe hand world landmarks, meters, origin near hand geometric center; "
            "NOT camera, Quest world, or metric wrist position",
        },
        "summary": {"frames": len(frames), "valid_frames": valid},
        "frames": frames,
    }
