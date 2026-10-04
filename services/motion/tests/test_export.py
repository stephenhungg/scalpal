"""Teleop attempts -> LeRobot-style dataset layout."""

import json

import numpy as np
import pytest

from scalpal_motion.export import ACTION_NAMES, STATE_NAMES, export_lerobot


def attempt(T, success, steps):
    labels = [{"sessionId": "coach-a", "procedureId": "lap_appendectomy", "stepId": sid, "stepTitle": title,
               "held": [{"hand": "right", "instrumentId": "inst_atraumatic_grasper", "name": "Grasper"}],
               "outcome": "in_progress"} if sid else None for sid, title in steps]
    return {
        "schema": "scalpal.teleop_episode.v1", "scene": {"obj_xy": [0, 0]}, "control_dt_s": 0.05,
        "grip": np.zeros((T, 4)).tolist(), "finger": np.ones((T, 18)).tolist(), "closure": [0.0] * T,
        "object": np.zeros((T, 4)).tolist(), "wrist": np.full((T, 4), 0.1).tolist(), "joints": np.zeros((T, 22)).tolist(),
        "t": (np.arange(T) * 0.051).tolist(), "coach": labels, "success": success,
    }


def write(folder, name, data):
    folder.mkdir(exist_ok=True)
    (folder / name).write_text(json.dumps(data))


def test_export_writes_lerobot_layout_with_step_tasks(tmp_path):
    src = tmp_path / "attempts"
    write(src, "a.json", attempt(4, True, [("expose", "Expose the appendix")] * 2 + [("clip", "Clip the artery")] * 2))
    write(src, "b.json", attempt(3, False, [(None, None)] * 3))
    old = attempt(2, True, [(None, None)] * 2)
    del old["wrist"]
    write(src, "c.json", old)  # pre-state-logging attempt is skipped, not invented
    out = tmp_path / "ds"
    r = export_lerobot(src, out, fmt="jsonl")
    assert r["episodes"] == 2 and r["frames"] == 7 and r["skipped"] == 1 and r["successes"] == 1

    info = json.loads((out / "meta/info.json").read_text())
    assert info["fps"] == 20 and info["total_episodes"] == 2 and info["total_frames"] == 7
    assert info["features"]["observation.state"]["shape"] == [26] == [len(STATE_NAMES)]
    assert info["features"]["action"]["shape"] == [22] == [len(ACTION_NAMES)]
    tasks = [json.loads(l)["task"] for l in (out / "meta/tasks.jsonl").read_text().splitlines()]
    assert tasks == ["Expose the appendix", "Clip the artery", "instrument transfer"]
    eps = [json.loads(l) for l in (out / "meta/episodes.jsonl").read_text().splitlines()]
    assert eps[0] == {"episode_index": 0, "tasks": ["Expose the appendix", "Clip the artery"], "length": 4, "success": True}
    extra = json.loads((out / "meta/scalpal_episodes.jsonl").read_text().splitlines()[0])
    assert extra["step_ids"] == ["expose", "clip"] and extra["held_instruments"] == ["inst_atraumatic_grasper"]

    rows = [json.loads(l) for l in (out / "data/chunk-000/episode_000000.jsonl").read_text().splitlines()]
    assert [r["task_index"] for r in rows] == [0, 0, 1, 1]
    assert [r["next.success"] for r in rows] == [False, False, False, True]
    assert np.allclose([r["timestamp"] for r in rows], [0, 0.05, 0.1, 0.15])
    assert len(rows[0]["observation.state"]) == 26 and len(rows[0]["action"]) == 22
    rows_b = [json.loads(l) for l in (out / "data/chunk-000/episode_000001.jsonl").read_text().splitlines()]
    assert rows_b[0]["index"] == 4 and not any(r["next.success"] for r in rows_b)
    stats = json.loads((out / "meta/episodes_stats.jsonl").read_text().splitlines()[0])
    assert stats["stats"]["observation.state"]["count"] == [4]


def test_success_only_and_parquet_when_available(tmp_path):
    src = tmp_path / "attempts"
    write(src, "a.json", attempt(3, True, [(None, None)] * 3))
    write(src, "b.json", attempt(3, False, [(None, None)] * 3))
    pa = pytest.importorskip("pyarrow.parquet")
    r = export_lerobot(src, tmp_path / "ds", success_only=True, fmt="parquet")
    assert r["episodes"] == 1
    table = pa.read_table(tmp_path / "ds/data/chunk-000/episode_000000.parquet")
    assert table.num_rows == 3 and len(table.column("observation.state")[0]) == 26
