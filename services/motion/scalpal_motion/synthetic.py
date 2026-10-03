"""Known-motion test input that isolates retargeting/simulator errors from video perception.

A ground-truth robot joint trajectory is pushed through forward kinematics, and the
resulting palm-relative link vectors are written as a 21-point "human" hand in the
indices the retargeting config reads. A perfect retargeter recovers the same link
positions. A tracking gap is injected so gap handling is exercised too.
"""

from __future__ import annotations

import numpy as np

from .retarget import build_retargeter

FPS = 30.0


def ground_truth_qpos(names: list[str], limits: np.ndarray, n_frames: int) -> np.ndarray:
    """Fingers open -> fist -> open. The wrist stays at 0: hand-relative input carries no
    wrist orientation, and the retargeter keeps WRJ1/WRJ2 fixed."""
    t = np.linspace(0.0, 1.0, n_frames)
    curl = 0.5 - 0.5 * np.cos(2 * np.pi * t)  # 0 -> 1 -> 0
    q = np.zeros((n_frames, len(names)))
    for j, name in enumerate(names):
        lo, hi = limits[j]
        if name[:2] in ("FF", "MF", "RF", "LF") and name[-1] in "123":
            q[:, j] = max(lo, 0.0) + curl * 0.7 * (hi - max(lo, 0.0))
        elif name == "THJ5":
            q[:, j] = 0.3 * curl
        elif name in ("THJ4", "THJ1"):
            q[:, j] = max(lo, 0.0) + curl * 0.5 * (hi - max(lo, 0.0))
        else:
            q[:, j] = np.clip(0.0, lo, hi)
    return q


def make_synthetic(
    n_frames: int = 90, gap: tuple[int, int] | None = (40, 50), hand: str = "Right"
) -> tuple[list[dict], dict]:
    retargeting, cfg = build_retargeter(hand=hand)
    robot = retargeting.optimizer.robot
    names = list(retargeting.joint_names)
    q_true = ground_truth_qpos(names, robot.joint_limits, n_frames)
    human_idx = cfg["target_link_human_indices"]
    scale = cfg["scaling_factor"]

    frames = []
    for i, q in enumerate(q_true):
        rec = {"frame": i, "t_ms": 1000.0 * i / FPS, "valid": True}
        if gap and gap[0] <= i < gap[1]:
            rec.update(valid=False, reason="synthetic_gap")
            frames.append(rec)
            continue
        robot.compute_forward_kinematics(q)
        pos = lambda name: robot.get_link_pose(robot.get_link_index(name))[:3, 3]
        pts = np.zeros((21, 3))
        for origin, task, o_idx, t_idx in zip(
            cfg["target_origin_link_names"], cfg["target_task_link_names"], human_idx[0], human_idx[1]
        ):
            pts[t_idx] = (pos(task) - pos(origin)) / scale + pts[o_idx]
        rec["world"] = pts.tolist()
        frames.append(rec)

    truth = {"joint_names": names, "qpos": q_true.tolist(), "gap": list(gap) if gap else None}
    return frames, truth
