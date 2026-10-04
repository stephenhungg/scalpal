"""HTTP boundary: POST /detect, GET /health, GET /models.

Frames are processed in memory and never written to disk or logged.
"""

from __future__ import annotations

import base64
import binascii
import threading
import time
from typing import Protocol

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field, field_validator

from .detector import BACKENDS, MODELS, decode_image
from .labels import DEFAULT_LABELS, resolve

MAX_IMAGE_BYTES = 8 * 1024 * 1024
MAX_LABELS = 32
LOCALHOST_ORIGINS = r"^https?://(localhost|127\.0\.0\.1|\[::1\])(:\d+)?$"


class DetectorLike(Protocol):
    model: str
    repo: str
    device: str

    def detect(self, image, labels: list[str], threshold: float) -> list[dict]: ...


class DetectRequest(BaseModel):
    image: str = Field(min_length=1, description="base64 JPEG or PNG; a data: URL prefix is accepted")
    labels: list[str] | None = Field(default=None, description="text prompts or catalog ids; defaults to DEFAULT_LABELS")
    threshold: float = Field(default=0.2, ge=0.0, le=1.0)

    @field_validator("labels")
    @classmethod
    def check_labels(cls, labels: list[str] | None) -> list[str] | None:
        if labels is None:
            return None
        cleaned = [label.strip() for label in labels if label.strip()]
        if not cleaned:
            raise ValueError("labels must contain at least one non-empty string")
        if len(cleaned) > MAX_LABELS:
            raise ValueError(f"at most {MAX_LABELS} labels")
        if any(len(label) > 64 for label in cleaned):
            raise ValueError("each label must be at most 64 characters")
        return cleaned


class ApiError(Exception):
    def __init__(self, code: str, status: int = 400):
        self.code = code
        self.status = status


def decode_base64_image(value: str) -> bytes:
    if value.startswith("data:"):
        _, _, value = value.partition(",")
    try:
        data = base64.b64decode(value, validate=True)
    except (binascii.Error, ValueError):
        raise ApiError("invalid_base64")
    if not data:
        raise ApiError("empty_image")
    if len(data) > MAX_IMAGE_BYTES:
        raise ApiError("image_too_large", 413)
    return data


def create_app(detector: DetectorLike, warmup_ms: float | None = None) -> FastAPI:
    app = FastAPI(title="scalpal-vision", docs_url=None, redoc_url=None)
    app.add_middleware(
        CORSMiddleware,
        allow_origin_regex=LOCALHOST_ORIGINS,
        allow_methods=["GET", "POST", "OPTIONS"],
        allow_headers=["Content-Type"],
    )
    # The model is not thread-safe on MPS; serialize inference.
    lock = threading.Lock()

    @app.exception_handler(ApiError)
    async def api_error(_: Request, error: ApiError):
        return JSONResponse({"error": error.code}, status_code=error.status)

    @app.exception_handler(RequestValidationError)
    async def validation_error(_: Request, error: RequestValidationError):
        details = [{"loc": list(e["loc"]), "msg": e["msg"]} for e in error.errors()]
        return JSONResponse({"error": "invalid_request", "details": details}, status_code=422)

    @app.get("/health")
    def health():
        return {"status": "ready", "model": detector.model, "device": detector.device, "warmupMs": warmup_ms}

    @app.get("/models")
    def models():
        return {
            "active": {"name": detector.model, "repo": detector.repo, "device": detector.device},
            "available": [{"name": name, "repo": MODELS[name]} for name in BACKENDS],
            "defaultLabels": [{"label": p, **resolve(p)[1].as_dict()} for p in DEFAULT_LABELS],
        }

    @app.post("/detect")
    def detect(body: DetectRequest):
        data = decode_base64_image(body.image)
        try:
            image = decode_image(data)
        except ValueError as error:
            raise ApiError(str(error) if str(error) == "unsupported_image_format" else "invalid_image")
        except Exception:
            raise ApiError("invalid_image")
        labels = body.labels or list(DEFAULT_LABELS)
        start = time.perf_counter()
        with lock:
            detections = detector.detect(image, labels, body.threshold)
        ms = round((time.perf_counter() - start) * 1000, 1)
        return {
            "detections": detections,
            "model": detector.model,
            "ms": ms,
            "image": {"width": image.size[0], "height": image.size[1]},
        }

    return app
