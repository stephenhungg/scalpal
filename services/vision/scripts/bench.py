"""Compare detector backends on the Wikimedia benchmark images.

    uv run python scripts/fetch_bench_images.py
    uv run python scripts/bench.py --models owlv2 grounding-dino

Every image is capped at 1024 px on its long side, the same as the service. Latency is the
median of warm runs (model loaded, one untimed run first). Annotated images and the raw
detections land in data/bench/out/<model>/ for side-by-side review.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import time
from pathlib import Path

from PIL import Image, ImageDraw

from scalpal_vision.detector import Detector, decode_image

ROOT = Path(__file__).resolve().parent.parent
BENCH = ROOT / "data" / "bench"
LABELS = [
    "scissors",
    "forceps",
    "scalpel",
    "knife",
    "hand",
    "gloved hand",
    "person",
    "whisk",
    "spatula",
    "ladle",
    "strainer",
    "operating table",
    "surgical light",
]


def draw(image: Image.Image, detections: list[dict]) -> Image.Image:
    canvas = image.copy()
    pen = ImageDraw.Draw(canvas)
    w, h = canvas.size
    for det in detections:
        b = det["box"]
        xy = (b["x"] * w, b["y"] * h, (b["x"] + b["w"]) * w, (b["y"] + b["h"]) * h)
        pen.rectangle(xy, outline=(255, 40, 40), width=3)
        pen.text((xy[0] + 4, xy[1] + 2), f"{det['label']} {det['score']:.2f}", fill=(255, 255, 0))
    return canvas


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", nargs="+", default=["owlv2", "grounding-dino"])
    parser.add_argument("--device")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--size", type=int, help="OWLv2 input size (default 960)")
    parser.add_argument("--threshold", type=float, default=0.2)
    args = parser.parse_args()
    images = sorted(p for p in BENCH.glob("*.jpg"))
    summary = {}
    for model in args.models:
        start = time.perf_counter()
        detector = Detector(model, device=args.device, size=args.size)
        load_ms = (time.perf_counter() - start) * 1000
        warm_ms = detector.warmup()
        tag = f"{model}-{args.size}" if args.size else model
        out = BENCH / "out" / tag
        out.mkdir(parents=True, exist_ok=True)
        per_image = {}
        for path in images:
            image = decode_image(path.read_bytes())
            detector.detect(image, LABELS, args.threshold)
            times = []
            for _ in range(args.runs):
                t = time.perf_counter()
                detections = detector.detect(image, LABELS, args.threshold)
                times.append((time.perf_counter() - t) * 1000)
            draw(image, detections).save(out / path.name, quality=85)
            per_image[path.name] = {
                "size": image.size,
                "median_ms": round(statistics.median(times), 1),
                "detections": [
                    {"label": d["label"], "score": d["score"], "box": d["box"]} for d in detections
                ],
            }
            labels = ", ".join(f"{d['label']}:{d['score']:.2f}" for d in detections[:12])
            print(f"{model:15s} {path.name[:40]:40s} {statistics.median(times):7.1f} ms  {labels}", flush=True)
        medians = [v["median_ms"] for v in per_image.values()]
        summary[tag] = {
            "device": detector.device,
            "load_ms": round(load_ms),
            "first_inference_ms": round(warm_ms),
            "median_ms": round(statistics.median(medians), 1),
            "max_ms": max(medians),
            "load_average_1m_after": round(os.getloadavg()[0], 1),
            "images": per_image,
        }
        print(f"== {model} on {detector.device}: load {load_ms:.0f} ms, first {warm_ms:.0f} ms, "
              f"median warm {statistics.median(medians):.1f} ms, load avg {os.getloadavg()[0]:.1f}", flush=True)
        del detector
    (BENCH / "out" / f"summary-{'-'.join(args.models)}{'-' + str(args.size) if args.size else ''}.json").write_text(json.dumps(summary, indent=2) + "\n")


if __name__ == "__main__":
    main()
