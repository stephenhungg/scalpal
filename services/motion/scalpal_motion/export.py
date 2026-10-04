"""Saved teleop attempts -> a LeRobot-style dataset folder (LeRobotDataset v2.1 layout), no LeRobot install.

    <out>/meta/info.json               features, fps, counts, path templates
    <out>/meta/tasks.jsonl             {"task_index", "task"}: one per surgery step title
    <out>/meta/episodes.jsonl          {"episode_index", "tasks", "length", "success"}
    <out>/meta/episodes_stats.jsonl    per-episode min/max/mean/std/count of every numeric feature
    <out>/meta/scalpal_episodes.jsonl  Scalpal extras: source attempt, coach session, step ids, held tools, outcome
    <out>/data/chunk-000/episode_000000.parquet   (or .jsonl when pyarrow is not installed)

One row per 20 Hz control frame:
    observation.state  float32[26]  measured wrist x, y, z (m, sim world), yaw (rad), then 22 finger joint angles (rad)
    action             float32[22]  commanded grip point x, y, z, yaw, then 18 finger actuator targets (rad)
    timestamp          float32      sim time = frame_index / fps (control is a fixed 20 Hz tick)
    teleop_time_s      float32      wall-clock time the frame was computed, from the attempt's first frame
    next.done          bool         last frame of the attempt
    next.success       bool         the attempt succeeded and this is its last frame
    frame_index, episode_index, index, task_index   int64
The task of a frame is the coach's step title at that frame ("instrument transfer" when unlabeled).

Parquet is written when pyarrow is importable (`uv run --with pyarrow scalpal-motion export-lerobot ...`);
otherwise the same columns go to JSON Lines and info.json's data_path says so.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np

from .learning.env import DT, FINGER_ACTUATORS, FINGER_JOINTS

FPS = int(round(1 / DT))
CHUNK = 1000
UNLABELED_TASK = "instrument transfer"
STATE_NAMES = ["wrist_x", "wrist_y", "wrist_z", "wrist_yaw"] + [f"joint_{j}" for j in FINGER_JOINTS]
ACTION_NAMES = ["grip_x", "grip_y", "grip_z", "grip_yaw"] + [f"act_{a}" for a in FINGER_ACTUATORS]


def _has_pyarrow() -> bool:
    try:
        import pyarrow  # noqa: F401
        return True
    except ImportError:
        return False


def _stats(x: np.ndarray) -> dict:
    x = np.asarray(x, dtype=float)
    if x.ndim == 1:
        x = x[:, None]
    return {"min": x.min(0).tolist(), "max": x.max(0).tolist(), "mean": x.mean(0).tolist(),
            "std": x.std(0).tolist(), "count": [int(len(x))]}


def load_attempts(folder: Path) -> tuple[list[tuple[Path, dict]], list[str]]:
    kept, skipped = [], []
    for f in sorted(Path(folder).glob("*.json")):
        try:
            d = json.loads(f.read_text())
        except ValueError:
            skipped.append(f"{f.name}: not JSON")
            continue
        if d.get("schema") != "scalpal.teleop_episode.v1":
            skipped.append(f"{f.name}: not a teleop attempt")
        elif not d.get("wrist") or len(d["wrist"]) != len(d.get("grip", [])):
            skipped.append(f"{f.name}: recorded before state logging (no measured wrist/joints)")
        elif not d["grip"]:
            skipped.append(f"{f.name}: empty")
        else:
            kept.append((f, d))
    return kept, skipped


def export_lerobot(src: Path, out: Path, success_only: bool = False, fmt: str | None = None) -> dict:
    attempts, skipped = load_attempts(src)
    if success_only:
        skipped += [f"{f.name}: not a success" for f, d in attempts if not d.get("success")]
        attempts = [(f, d) for f, d in attempts if d.get("success")]
    if not attempts:
        raise SystemExit(f"no exportable teleop attempts in {src}" + ("".join(f"\n  {s}" for s in skipped)))
    fmt = fmt or ("parquet" if _has_pyarrow() else "jsonl")
    if fmt == "parquet" and not _has_pyarrow():
        raise SystemExit("parquet needs pyarrow: uv run --with pyarrow scalpal-motion export-lerobot ...")
    (out / "meta").mkdir(parents=True, exist_ok=True)

    tasks: dict[str, int] = {}
    episodes, stats, extras = [], [], []
    index = 0
    for ep_i, (f, d) in enumerate(attempts):
        T = len(d["grip"])
        state = np.c_[np.asarray(d["wrist"], float), np.asarray(d["joints"], float)]
        action = np.c_[np.asarray(d["grip"], float), np.asarray(d["finger"], float)]
        labels = d.get("coach") or [None] * T
        titles = [(lab or {}).get("stepTitle") or UNLABELED_TASK for lab in labels]
        for t in titles:
            tasks.setdefault(t, len(tasks))
        task_index = np.array([tasks[t] for t in titles], dtype=np.int64)
        frame_index = np.arange(T, dtype=np.int64)
        success = bool(d.get("success"))
        done = frame_index == T - 1
        cols = {
            "observation.state": state.astype(np.float32),
            "action": action.astype(np.float32),
            "timestamp": (frame_index / FPS).astype(np.float32),
            "teleop_time_s": np.asarray(d.get("t") or frame_index * DT, dtype=np.float32),
            "next.done": done,
            "next.success": done & success,
            "frame_index": frame_index,
            "episode_index": np.full(T, ep_i, dtype=np.int64),
            "index": np.arange(index, index + T, dtype=np.int64),
            "task_index": task_index,
        }
        index += T
        chunk = ep_i // CHUNK
        path = out / "data" / f"chunk-{chunk:03d}" / f"episode_{ep_i:06d}.{fmt}"
        path.parent.mkdir(parents=True, exist_ok=True)
        _write(path, cols, fmt)

        ep_tasks = list(dict.fromkeys(titles))
        episodes.append({"episode_index": ep_i, "tasks": ep_tasks, "length": T, "success": success})
        stats.append({"episode_index": ep_i, "stats": {k: _stats(v) for k, v in cols.items()}})
        lab = [x for x in labels if x]
        extras.append({
            "episode_index": ep_i, "source": f.name, "success": success,
            "coach_session": lab[-1]["sessionId"] if lab else "",
            "procedure_id": lab[-1]["procedureId"] if lab else "",
            "step_ids": list(dict.fromkeys(x["stepId"] for x in lab if x.get("stepId"))),
            "held_instruments": sorted({h["instrumentId"] for x in lab for h in x.get("held", [])}),
            "case_outcome": lab[-1].get("outcome", "") if lab else "",
            "labeled_fraction": round(len(lab) / T, 3),
            "scene": d.get("scene"),
        })

    n_frames = index
    info = {
        "codebase_version": "v2.1",
        "robot_type": "shadow_hand_floating_mujoco_sim",
        "total_episodes": len(episodes),
        "total_frames": n_frames,
        "total_tasks": len(tasks),
        "total_videos": 0,
        "total_chunks": (len(episodes) - 1) // CHUNK + 1,
        "chunks_size": CHUNK,
        "fps": FPS,
        "splits": {"train": f"0:{len(episodes)}"},
        "data_path": "data/chunk-{episode_chunk:03d}/episode_{episode_index:06d}." + fmt,
        "video_path": None,
        "features": {
            "observation.state": {"dtype": "float32", "shape": [len(STATE_NAMES)], "names": STATE_NAMES},
            "action": {"dtype": "float32", "shape": [len(ACTION_NAMES)], "names": ACTION_NAMES},
            "timestamp": {"dtype": "float32", "shape": [1], "names": None},
            "teleop_time_s": {"dtype": "float32", "shape": [1], "names": None},
            "next.done": {"dtype": "bool", "shape": [1], "names": None},
            "next.success": {"dtype": "bool", "shape": [1], "names": None},
            "frame_index": {"dtype": "int64", "shape": [1], "names": None},
            "episode_index": {"dtype": "int64", "shape": [1], "names": None},
            "index": {"dtype": "int64", "shape": [1], "names": None},
            "task_index": {"dtype": "int64", "shape": [1], "names": None},
        },
        "scalpal": {
            "source": "Quest controller teleoperation of a simulated Shadow hand (MuJoCo); not a physical robot",
            "frames": "MuJoCo world: x forward, y left, z up; wrist = grip point of the floating hand",
            "data_format": fmt,
        },
    }
    (out / "meta" / "info.json").write_text(json.dumps(info, indent=2))
    _jsonl(out / "meta" / "tasks.jsonl", [{"task_index": i, "task": t} for t, i in tasks.items()])
    _jsonl(out / "meta" / "episodes.jsonl", episodes)
    _jsonl(out / "meta" / "episodes_stats.jsonl", stats)
    _jsonl(out / "meta" / "scalpal_episodes.jsonl", extras)
    for s in skipped:
        print(f"skipped {s}", file=sys.stderr)
    return {"episodes": len(episodes), "frames": n_frames, "tasks": list(tasks), "format": fmt,
            "successes": sum(e["success"] for e in episodes), "skipped": len(skipped), "out": str(out)}


def _jsonl(path: Path, rows: list[dict]) -> None:
    path.write_text("".join(json.dumps(r) + "\n" for r in rows))


def _write(path: Path, cols: dict, fmt: str) -> None:
    if fmt == "parquet":
        import pyarrow as pa
        import pyarrow.parquet as pq

        arrays = {}
        for k, v in cols.items():
            if v.ndim == 2:
                arrays[k] = pa.FixedSizeListArray.from_arrays(pa.array(v.reshape(-1)), v.shape[1])
            else:
                arrays[k] = pa.array(v)
        pq.write_table(pa.table(arrays), path)
    else:
        T = len(cols["frame_index"])
        rows = [{k: (v[i].tolist() if hasattr(v[i], "tolist") else v[i]) for k, v in cols.items()} for i in range(T)]
        _jsonl(path, rows)
