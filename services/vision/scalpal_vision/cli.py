"""scalpal-vision serve | detect."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from .detector import BACKENDS, DEFAULT_MODEL, Detector, decode_image
from .labels import DEFAULT_LABELS


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(prog="scalpal-vision")
    sub = parser.add_subparsers(dest="command", required=True)

    serve = sub.add_parser("serve", help="run the HTTP service")
    serve.add_argument("--host", default="127.0.0.1")
    serve.add_argument("--port", type=int, default=8791)
    detect = sub.add_parser("detect", help="detect on one image file and print JSON")
    detect.add_argument("image", type=Path)
    detect.add_argument("--labels", nargs="+", default=list(DEFAULT_LABELS))
    detect.add_argument("--threshold", type=float, default=0.2)
    for command in (serve, detect):
        command.add_argument("--model", choices=sorted(BACKENDS), default=DEFAULT_MODEL)
        command.add_argument("--device", help="mps, cpu or cuda; default picks mps when available")
        command.add_argument("--weights", help="fine-tuned checkpoint directory (same architecture)")
    args = parser.parse_args(argv)

    detector = Detector(args.model, device=args.device, weights=args.weights)
    warmup_ms = detector.warmup()
    if args.command == "detect":
        image = decode_image(args.image.read_bytes())
        result = detector.detect(image, args.labels, args.threshold)
        print(json.dumps({"detections": result, "model": detector.model}, indent=2))
        return

    import uvicorn

    from .api import create_app

    print(f"scalpal-vision {detector.model} on {detector.device}, warmup {warmup_ms:.0f} ms, "
          f"http://{args.host}:{args.port}", flush=True)
    uvicorn.run(create_app(detector, round(warmup_ms, 1)), host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
