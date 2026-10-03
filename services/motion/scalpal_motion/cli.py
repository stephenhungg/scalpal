"""Command line entry point: `uv run scalpal-motion <command> --help`."""

from __future__ import annotations

import argparse
import json
import time
from pathlib import Path

import numpy as np

from .paths import MOTION_ROOT


def _write_json(path: Path, obj: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(obj, indent=1))


def _print_summary(motion: dict) -> None:
    s = motion["summary"]
    print(f"valid {s['valid_frames']}/{s['frames']} frames ({100 * s['valid_fraction']:.1f}%)")
    if s["vector_error_m_median"] is not None:
        print(f"vector error median {1000 * s['vector_error_m_median']:.1f} mm, p95 {1000 * s['vector_error_m_p95']:.1f} mm")
    if s.get("finger_bend_correlation"):
        print("finger bend correlation (robot vs estimate):", s["finger_bend_correlation"])
    for seg in s["segments"]:
        print(f"  {seg['status']:>16}  frames {seg['start_frame']}-{seg['end_frame']}  "
              f"({seg['start_ms'] / 1000:.2f}s-{seg['end_ms'] / 1000:.2f}s)")


def cmd_synthetic(args: argparse.Namespace) -> None:
    from .replay import render_replay
    from .retarget import retarget_frames
    from .synthetic import make_synthetic

    frames, truth = make_synthetic(n_frames=args.frames)
    motion = retarget_frames(frames, keypoint_frame="mano", low_pass_alpha=args.smooth, scaling="config")
    q_true = np.array(truth["qpos"])
    errs = np.array([np.abs(np.array(f["qpos"]) - q_true[f["frame"]]) for f in motion["frames"] if f["valid"]])
    motion["synthetic_check"] = {
        "ground_truth": "robot FK of a known joint trajectory",
        "joint_abs_error_rad_median": float(np.median(errs)),
        "joint_abs_error_rad_max": float(errs.max()),
        "note": "joint error includes redundant solutions reaching the same link positions; "
        "vector_error_m is the primary check",
    }
    out = Path(args.out)
    _write_json(out / "motion.json", motion)
    _write_json(out / "truth.json", truth)
    _print_summary(motion)
    print(f"joint error median {motion['synthetic_check']['joint_abs_error_rad_median']:.3f} rad")
    if not args.no_render:
        print("wrote", render_replay(motion, out / "replay.mp4"))


def cmd_run(args: argparse.Namespace) -> None:
    from .perception import track_hand
    from .replay import render_replay
    from .retarget import retarget_frames

    out = Path(args.out)
    t0 = time.perf_counter()
    track = track_hand(args.video, hand=args.hand, mirrored=args.mirrored)
    t1 = time.perf_counter()
    motion = retarget_frames(track["frames"], hand=args.hand, low_pass_alpha=args.smooth)
    t2 = time.perf_counter()
    motion["input"] = {"video": track["source"]["video"], "hand_track_schema": track["schema"], "model": track["model"]}
    motion["processing_s"] = {"perception": round(t1 - t0, 2), "retargeting": round(t2 - t1, 2)}
    _write_json(out / "hand_track.json", track)
    _write_json(out / "motion.json", motion)
    _print_summary(motion)
    print(f"perception {t1 - t0:.1f}s, retargeting {t2 - t1:.1f}s")
    if not args.no_render:
        print("wrote", render_replay(motion, out / "replay.mp4", video_path=args.video, track=track, view=args.view))


def cmd_replay(args: argparse.Namespace) -> None:
    from .replay import render_replay

    motion = json.loads(Path(args.motion).read_text())
    track = json.loads(Path(args.track).read_text()) if args.track else None
    print("wrote", render_replay(motion, args.out, video_path=args.video, track=track))


def cmd_process(args: argparse.Namespace) -> None:
    from .jobs import run_job

    result = run_job(json.loads(Path(args.job).read_text()), args.output_root)
    print(json.dumps({k: result.get(k) for k in ("status", "error", "output_dir", "processing_s")}, indent=1))


def cmd_serve(args: argparse.Namespace) -> None:
    from .server import serve

    serve(args.host, args.port, Path(args.output_root))


def cmd_gateway_worker(args: argparse.Namespace) -> None:
    import os

    from .gateway_worker import run_worker

    token = os.environ.get("WORKER_TOKEN")
    if not token:
        raise SystemExit("set WORKER_TOKEN (and GATEWAY_URL) for Nathan's gateway")
    run_worker(args.gateway, token, lease_ms=args.lease_ms, mirrored=args.mirrored, once=args.once, hand=args.hand)


def main() -> None:
    parser = argparse.ArgumentParser(prog="scalpal-motion", description=__doc__)
    sub = parser.add_subparsers(required=True)
    default_out = MOTION_ROOT / "out"

    p = sub.add_parser("synthetic", help="known-motion round trip through the retargeter (no video)")
    p.add_argument("--out", default=default_out / "synthetic")
    p.add_argument("--frames", type=int, default=90)
    p.add_argument("--smooth", type=float, default=None, help="low-pass alpha in (0,1]; default none")
    p.add_argument("--no-render", action="store_true")
    p.set_defaults(func=cmd_synthetic)

    p = sub.add_parser("run", help="clip -> hand track -> robot motion -> replay video")
    p.add_argument("video")
    p.add_argument("--out", default=None, help="default: out/<video name>")
    p.add_argument("--hand", choices=["Right", "Left"], default="Right")
    p.add_argument("--mirrored", action="store_true", help="input is a mirrored/selfie image")
    p.add_argument("--smooth", type=float, default=None, help="low-pass alpha in (0,1]; default none")
    p.add_argument("--view", choices=["palm", "back"], default=None, help="robot camera; default from --mirrored")
    p.add_argument("--no-render", action="store_true")
    p.set_defaults(func=cmd_run)

    p = sub.add_parser("replay", help="re-render a saved motion.json")
    p.add_argument("motion")
    p.add_argument("--out", required=True)
    p.add_argument("--video", default=None)
    p.add_argument("--track", default=None)
    p.set_defaults(func=cmd_replay)

    p = sub.add_parser("process", help="run one scalpal.motion_job/0 file, write result.json")
    p.add_argument("job")
    p.add_argument("--output-root", default=default_out / "runs")
    p.set_defaults(func=cmd_process)

    p = sub.add_parser("serve", help="HTTP worker: POST /jobs, GET /runs/<run>/<file>")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--output-root", default=default_out / "runs")
    p.set_defaults(func=cmd_serve)

    import os

    p = sub.add_parser("gateway-worker", help="pull jobs from Nathan's gateway (worker-api v1)")
    p.add_argument("--gateway", default=os.environ.get("GATEWAY_URL", "http://localhost:8788"))
    p.add_argument("--lease-ms", type=int, default=120_000)
    p.add_argument("--hand", choices=["Right", "Left"], default="Right")
    p.add_argument("--mirrored", action="store_true", help="treat clips as mirrored (selfie/webcam test footage)")
    p.add_argument("--once", action="store_true", help="handle at most one job, then exit")
    p.set_defaults(func=cmd_gateway_worker)

    args = parser.parse_args()
    if getattr(args, "video", None) and args.func is cmd_run and args.out is None:
        args.out = default_out / Path(args.video).stem
    args.func(args)


if __name__ == "__main__":
    main()
