"""Open-vocabulary detector backends and box normalization.

Coordinates: backends return pixel boxes (x0, y0, x1, y1) in the coordinates of the image
they were given. `normalize_box` converts them to the API's normalized top-left x, y, w, h
in [0, 1] relative to that same image. These are camera-image coordinates only; nothing
here knows about depth, Unity world space or robot space.
"""

from __future__ import annotations

import io
import time
from dataclasses import dataclass

from PIL import Image, ImageOps

MODELS = {
    "owlv2": "google/owlv2-base-patch16-ensemble",
    "grounding-dino": "IDEA-Research/grounding-dino-tiny",
}
DEFAULT_MODEL = "owlv2"
MAX_SIDE = 1024


@dataclass(frozen=True)
class RawDetection:
    label_index: int
    score: float
    xyxy: tuple[float, float, float, float]


def pick_device(preferred: str | None = None) -> str:
    import torch

    if preferred:
        return preferred
    if torch.backends.mps.is_available():
        return "mps"
    if torch.cuda.is_available():
        return "cuda"
    return "cpu"


def decode_image(data: bytes, max_side: int = MAX_SIDE) -> Image.Image:
    """Decode JPEG/PNG bytes, apply EXIF rotation, convert to RGB and cap the long side."""
    image = Image.open(io.BytesIO(data))
    if image.format not in {"JPEG", "PNG", "WEBP"}:
        raise ValueError("unsupported_image_format")
    image = ImageOps.exif_transpose(image).convert("RGB")
    if max(image.size) > max_side:
        image.thumbnail((max_side, max_side), Image.Resampling.BILINEAR)
    return image


def normalize_box(xyxy, width: int, height: int) -> dict[str, float]:
    """Pixel x0,y0,x1,y1 -> normalized top-left x,y,w,h, clamped to the image."""
    x0, y0, x1, y1 = (float(v) for v in xyxy)
    x0, x1 = sorted((min(max(x0, 0.0), width), min(max(x1, 0.0), width)))
    y0, y1 = sorted((min(max(y0, 0.0), height), min(max(y1, 0.0), height)))
    return {
        "x": round(x0 / width, 5),
        "y": round(y0 / height, 5),
        "w": round((x1 - x0) / width, 5),
        "h": round((y1 - y0) / height, 5),
    }


def iou(a, b) -> float:
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def nms(detections: list[RawDetection], threshold: float = 0.5) -> list[RawDetection]:
    """Greedy per-label non-maximum suppression."""
    kept: list[RawDetection] = []
    for det in sorted(detections, key=lambda d: d.score, reverse=True):
        if all(k.label_index != det.label_index or iou(k.xyxy, det.xyxy) < threshold for k in kept):
            kept.append(det)
    return kept


class Owlv2Backend:
    name = "owlv2"

    def __init__(self, device: str, repo: str = MODELS["owlv2"], weights: str | None = None):
        import torch
        from transformers import Owlv2ForObjectDetection, Owlv2Processor

        self.torch = torch
        self.device = device
        self.repo = weights or repo
        self.processor = Owlv2Processor.from_pretrained(repo)
        self.model = Owlv2ForObjectDetection.from_pretrained(self.repo).to(device).eval()
        self._text_cache: dict[tuple[str, ...], dict] = {}

    def _text_inputs(self, labels: tuple[str, ...]) -> dict:
        if labels not in self._text_cache:
            tokens = self.processor.tokenizer(
                list(labels), padding="max_length", max_length=16, truncation=True, return_tensors="pt"
            )
            self._text_cache[labels] = {k: v.to(self.device) for k, v in tokens.items()}
            if len(self._text_cache) > 32:
                self._text_cache.pop(next(iter(self._text_cache)))
        return self._text_cache[labels]

    def predict(self, image: Image.Image, labels: list[str], threshold: float) -> list[RawDetection]:
        torch = self.torch
        pixel = self.processor.image_processor(images=image, return_tensors="pt")["pixel_values"].to(self.device)
        text = self._text_inputs(tuple(labels))
        with torch.inference_mode():
            outputs = self.model(pixel_values=pixel, input_ids=text["input_ids"], attention_mask=text["attention_mask"])
        # OWLv2 pads the image to a square at the bottom/right, so boxes are relative to
        # the padded square. Scale by the long side, then clamp in normalize_box.
        side = max(image.size)
        logits = outputs.logits[0].float().cpu()  # (patches, labels)
        boxes = outputs.pred_boxes[0].float().cpu()  # (patches, 4) cx, cy, w, h normalized
        scores, label_idx = torch.sigmoid(logits).max(dim=-1)
        keep = scores >= threshold
        result = []
        for score, li, (cx, cy, w, h) in zip(scores[keep].tolist(), label_idx[keep].tolist(), boxes[keep].tolist()):
            xyxy = ((cx - w / 2) * side, (cy - h / 2) * side, (cx + w / 2) * side, (cy + h / 2) * side)
            result.append(RawDetection(li, score, xyxy))
        return nms(result)


class GroundingDinoBackend:
    name = "grounding-dino"

    def __init__(self, device: str, repo: str = MODELS["grounding-dino"], weights: str | None = None):
        import torch
        from transformers import AutoModelForZeroShotObjectDetection, AutoProcessor

        self.torch = torch
        self.device = device
        self.repo = weights or repo
        self.processor = AutoProcessor.from_pretrained(repo)
        self.model = AutoModelForZeroShotObjectDetection.from_pretrained(self.repo).to(device).eval()

    def predict(self, image: Image.Image, labels: list[str], threshold: float) -> list[RawDetection]:
        torch = self.torch
        text = ". ".join(labels) + "."
        inputs = self.processor(images=image, text=text, return_tensors="pt").to(self.device)
        with torch.inference_mode():
            outputs = self.model(**inputs)
        results = self.processor.post_process_grounded_object_detection(
            outputs,
            inputs["input_ids"],
            threshold=threshold,
            text_threshold=threshold,
            target_sizes=[image.size[::-1]],
        )[0]
        lookup = {label: i for i, label in enumerate(labels)}
        detections = []
        for score, text_label, box in zip(results["scores"].tolist(), results["text_labels"], results["boxes"].tolist()):
            index = _match_phrase(text_label, labels, lookup)
            if index is not None:
                detections.append(RawDetection(index, score, tuple(box)))
        return nms(detections)


def _match_phrase(phrase: str, labels: list[str], lookup: dict[str, int]) -> int | None:
    """Grounding DINO returns the matched token span, which can be a fragment or a merge
    of adjacent prompts. Prefer an exact match, then the label with the most shared words."""
    phrase = phrase.strip().lower()
    if phrase in lookup:
        return lookup[phrase]
    words = set(phrase.split())
    best, best_overlap = None, 0
    for i, label in enumerate(labels):
        overlap = len(words & set(label.split()))
        if overlap > best_overlap:
            best, best_overlap = i, overlap
    return best


BACKENDS = {"owlv2": Owlv2Backend, "grounding-dino": GroundingDinoBackend}


class Detector:
    """Loads one backend once and serves detections with catalog metadata."""

    def __init__(self, model: str = DEFAULT_MODEL, device: str | None = None, weights: str | None = None):
        if model not in BACKENDS:
            raise ValueError(f"unknown model {model!r}; choose one of {sorted(BACKENDS)}")
        self.device = pick_device(device)
        try:
            self.backend = BACKENDS[model](self.device, weights=weights)
        except Exception:
            if self.device == "cpu":
                raise
            self.device = "cpu"
            self.backend = BACKENDS[model](self.device, weights=weights)
        self.model = model
        self.repo = self.backend.repo

    def warmup(self) -> float:
        from .labels import DEFAULT_LABELS

        image = Image.new("RGB", (MAX_SIDE, 768), (120, 120, 120))
        start = time.perf_counter()
        self.backend.predict(image, list(DEFAULT_LABELS), 0.99)
        return (time.perf_counter() - start) * 1000

    def detect(self, image: Image.Image, labels: list[str], threshold: float) -> list[dict]:
        from .labels import resolve

        resolved = [resolve(label) for label in labels]
        prompts: list[str] = []
        for prompt, _ in resolved:
            if prompt not in prompts:
                prompts.append(prompt)
        infos = {prompt: info for prompt, info in reversed(resolved)}
        raw = self.backend.predict(image, prompts, threshold)
        width, height = image.size
        detections = []
        for det in sorted(raw, key=lambda d: d.score, reverse=True):
            prompt = prompts[det.label_index]
            box = normalize_box(det.xyxy, width, height)
            if box["w"] <= 0 or box["h"] <= 0:
                continue
            detections.append({"label": prompt, "score": round(det.score, 4), "box": box, **infos[prompt].as_dict()})
        return detections

