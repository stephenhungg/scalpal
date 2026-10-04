"""One decoded image in, image-space pose out. Frames are never written to disk."""

from __future__ import annotations

import io
import math
import time
import warnings
from pathlib import Path

import mediapipe as mp
import numpy as np
from mediapipe.tasks.python import BaseOptions, vision
from PIL import Image, UnidentifiedImageError

from . import POSE_SCHEMA
from .model import DEFAULT_MODEL_PATH, MODEL_NAME, MODEL_SHA256, MODEL_VERSION, verify_model

MAX_JPEG_BYTES = 2 * 1024 * 1024
MAX_IMAGE_PIXELS = 16_000_000


class InvalidFrame(ValueError):
    def __init__(self, code: str, status: int = 400):
        self.code = code
        self.status = status
        super().__init__(code)


def validate_frame_id(frame_id: str | None) -> str:
    if not frame_id or len(frame_id) > 128 or any(ord(c) < 32 or ord(c) > 126 for c in frame_id):
        raise InvalidFrame("invalid_frame_id")
    return frame_id


def decode_jpeg(data: bytes) -> np.ndarray:
    if len(data) > MAX_JPEG_BYTES:
        raise InvalidFrame("frame_too_large", 413)
    if not data:
        raise InvalidFrame("invalid_jpeg")
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(data)) as image:
                if image.format != "JPEG":
                    raise InvalidFrame("invalid_jpeg")
                if image.width * image.height > MAX_IMAGE_PIXELS:
                    raise InvalidFrame("decoded_frame_too_large", 413)
                # Preserve the submitted sensor pixel orientation; no EXIF rotation or mirroring.
                return np.ascontiguousarray(image.convert("RGB"), dtype=np.uint8)
    except (UnidentifiedImageError, OSError, SyntaxError, Image.DecompressionBombError, Image.DecompressionBombWarning):
        raise InvalidFrame("invalid_jpeg") from None


def format_result(result, frame_id: str, width: int, height: int, inference_ms: float) -> dict:
    poses = result.pose_landmarks
    count = len(poses)
    reason = "no_person" if count == 0 else "ambiguous_people" if count > 1 else ""
    landmarks = []
    if count == 1:
        if len(poses[0]) != 33:
            reason = "invalid_landmarks"
        else:
            try:
                for index, point in enumerate(poses[0]):
                    values = {key: float(getattr(point, key)) for key in ("x", "y", "z", "visibility", "presence")}
                    if not all(math.isfinite(value) for value in values.values()):
                        raise ValueError("nonfinite landmark")
                    if not all(0 <= values[key] <= 1 for key in ("visibility", "presence")):
                        raise ValueError("invalid confidence")
                    landmarks.append({"index": index, **values})
            except (AttributeError, TypeError, ValueError):
                reason = "invalid_landmarks"
    if reason:
        landmarks = []
    return {
        "schema": POSE_SCHEMA,
        "frameId": frame_id,
        "imageWidth": width,
        "imageHeight": height,
        "coordinateConvention": "normalized_image_top_left",
        "model": {"name": MODEL_NAME, "version": MODEL_VERSION, "sha256": MODEL_SHA256, "mediapipeVersion": mp.__version__},
        "inferenceMs": round(inference_ms, 3),
        "personCount": count,
        "valid": not reason,
        "reason": reason,
        "landmarks": landmarks,
    }


class PoseEstimator:
    def __init__(self, model_path: Path = DEFAULT_MODEL_PATH):
        verify_model(model_path)
        options = vision.PoseLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=str(model_path), delegate=BaseOptions.Delegate.CPU),
            running_mode=vision.RunningMode.IMAGE,
            num_poses=2,
            min_pose_detection_confidence=0.5,
            min_pose_presence_confidence=0.5,
            output_segmentation_masks=False,
        )
        self.landmarker = vision.PoseLandmarker.create_from_options(options)

    def infer(self, data: bytes, frame_id: str) -> dict:
        validate_frame_id(frame_id)
        rgb = decode_jpeg(data)
        height, width, _ = rgb.shape
        image = mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)
        started = time.perf_counter()
        result = self.landmarker.detect(image)
        return format_result(result, frame_id, width, height, (time.perf_counter() - started) * 1000)

    def close(self):
        self.landmarker.close()
