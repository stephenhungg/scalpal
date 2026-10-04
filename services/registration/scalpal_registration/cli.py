"""Explicit model download and loopback service entry points."""

import argparse
from pathlib import Path

from .model import DEFAULT_MODEL_PATH, download_model
from .pose import PoseEstimator
from .server import make_server


def main():
    parser = argparse.ArgumentParser(description="Local ephemeral body-pose inference (no camera capture)")
    parser.add_argument("command", choices=("fetch-model", "serve"))
    parser.add_argument("--model", type=Path, default=DEFAULT_MODEL_PATH)
    parser.add_argument("--port", type=int, default=8790)
    args = parser.parse_args()
    if args.command == "fetch-model":
        print(download_model(args.model))
        return
    estimator = PoseEstimator(args.model)
    try:
        with make_server(estimator, args.port) as server:
            print(f"pose service ready on http://127.0.0.1:{server.server_port}; no frame retention")
            try:
                server.serve_forever()
            except KeyboardInterrupt:
                pass
    finally:
        estimator.close()
