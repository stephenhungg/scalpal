"""Pinned model acquisition, separate from the camera-frame inference path."""

from __future__ import annotations

import hashlib
import os
import tempfile
import urllib.request
from pathlib import Path

MODEL_NAME = "mediapipe/pose_landmarker_lite/float16"
MODEL_VERSION = "1"
MODEL_SHA256 = "59929e1d1ee95287735ddd833b19cf4ac46d29bc7afddbbf6753c459690d574a"
MODEL_URL = "https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/1/pose_landmarker_lite.task"
DEFAULT_MODEL_PATH = Path(__file__).resolve().parents[1] / "models" / "pose_landmarker_lite_v1.task"


def verify_model(path: Path) -> Path:
    if hashlib.sha256(path.read_bytes()).hexdigest() != MODEL_SHA256:
        raise ValueError("pose model SHA-256 mismatch")
    return path


def download_model(path: Path = DEFAULT_MODEL_PATH) -> Path:
    """Download only the official model, never any camera frames."""
    if path.exists():
        return verify_model(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with urllib.request.urlopen(MODEL_URL, timeout=60) as response:
            data = response.read(16 * 1024 * 1024 + 1)
        if len(data) > 16 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != MODEL_SHA256:
            raise ValueError("downloaded pose model SHA-256 mismatch")
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as handle:
            temporary = Path(handle.name)
            handle.write(data)
        os.replace(temporary, path)
        return verify_model(path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
