"""Marking demos -> LeRobot-style dataset (v2.1 layout), using the teleop exporter's writers (export.py).

    observation.state  float32[9]  marker tip xyz, right ASIS xyz, umbilicus xyz (patient frame, m)
    action             float32[4]  next commanded tip xyz, trigger (0/1)
Each episode's extras (meta/scalpal_episodes.jsonl) say whether it is a headset or a SYNTHETIC demo.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np

from ..export import _has_pyarrow, _jsonl, _stats, _write
from .demos import HZ, MarkDemo

TASK = "Mark McBurney incision"
STATE_NAMES = ["tip_x", "tip_y", "tip_z", "asis_x", "asis_y", "asis_z", "umbilicus_x", "umbilicus_y", "umbilicus_z"]
ACTION_NAMES = ["tip_x", "tip_y", "tip_z", "trigger"]


def export_mark_lerobot(demos: list[tuple[MarkDemo, bool]], out: Path, fmt: str | None = None) -> dict:
    fmt = fmt or ("parquet" if _has_pyarrow() else "jsonl")
    (out / "meta").mkdir(parents=True, exist_ok=True)
    episodes, stats, extras = [], [], []
    index = 0
    for ep, (d, success) in enumerate(demos):
        T = len(d.tip) - 1
        lm = np.r_[d.landmarks.right_asis, d.landmarks.umbilicus]
        frame_index = np.arange(T, dtype=np.int64)
        done = frame_index == T - 1
        cols = {
            "observation.state": np.c_[d.tip[:-1], np.repeat(lm[None], T, 0)].astype(np.float32),
            "action": np.c_[d.tip[1:], d.trigger[1:]].astype(np.float32),
            "timestamp": (frame_index / HZ).astype(np.float32),
            "next.done": done, "next.success": done & bool(success),
            "frame_index": frame_index, "episode_index": np.full(T, ep, dtype=np.int64),
            "index": np.arange(index, index + T, dtype=np.int64), "task_index": np.zeros(T, dtype=np.int64),
        }
        index += T
        path = out / "data" / "chunk-000" / f"episode_{ep:06d}.{fmt}"
        path.parent.mkdir(parents=True, exist_ok=True)
        _write(path, cols, fmt)
        episodes.append({"episode_index": ep, "tasks": [TASK], "length": T, "success": bool(success)})
        stats.append({"episode_index": ep, "stats": {k: _stats(v) for k, v in cols.items()}})
        extras.append({"episode_index": ep, "demo_id": d.demo_id, "source": d.source, "synthetic": d.synthetic,
                       "landmarks": d.landmarks.to_dict(), "meta": d.meta})
    info = {
        "codebase_version": "v2.1", "robot_type": "panda_shadow_hand_mujoco_sim_task_space",
        "total_episodes": len(episodes), "total_frames": index, "total_tasks": 1, "total_videos": 0, "total_chunks": 1,
        "chunks_size": 1000, "fps": HZ, "splits": {"train": f"0:{len(episodes)}"},
        "data_path": "data/chunk-{episode_chunk:03d}/episode_{episode_index:06d}." + fmt, "video_path": None,
        "features": {
            "observation.state": {"dtype": "float32", "shape": [len(STATE_NAMES)], "names": STATE_NAMES},
            "action": {"dtype": "float32", "shape": [len(ACTION_NAMES)], "names": ACTION_NAMES},
            **{k: {"dtype": t, "shape": [1], "names": None} for k, t in [("timestamp", "float32"), ("next.done", "bool"),
                ("next.success", "bool"), ("frame_index", "int64"), ("episode_index", "int64"), ("index", "int64"), ("task_index", "int64")]},
        },
        "scalpal": {"source": "mark_incision demos: headset ControllerMotionCapture retargeted to the marker tip, plus labelled synthetic strokes",
                    "frames": "patient frame: +X patient left, +Y anterior, +Z cranial, umbilicus origin", "data_format": fmt,
                    "headset_episodes": sum(not d.synthetic for d, _ in demos), "synthetic_episodes": sum(d.synthetic for d, _ in demos)},
    }
    (out / "meta" / "info.json").write_text(json.dumps(info, indent=2))
    _jsonl(out / "meta" / "tasks.jsonl", [{"task_index": 0, "task": TASK}])
    _jsonl(out / "meta" / "episodes.jsonl", episodes)
    _jsonl(out / "meta" / "episodes_stats.jsonl", stats)
    _jsonl(out / "meta" / "scalpal_episodes.jsonl", extras)
    return {"episodes": len(episodes), "frames": index, "format": fmt, "out": str(out)}
