"""Retarget a hand track to a robot hand with dex-retargeting vector retargeting.

Only hand-relative finger motion is used. MediaPipe world landmarks are re-expressed in a
wrist frame (MANO convention), so the output says nothing about where the wrist was in
the room or how it was oriented; the robot's wrist joints stay at 0.
"""

from __future__ import annotations

import numpy as np
from dex_retargeting.retargeting_config import RetargetingConfig
from dex_retargeting.seq_retarget import SeqRetargeting

from . import ROBOT_MOTION_SCHEMA
from .paths import ROBOTS_DIR

# Wrist-frame conversion adapted from dex-retargeting's example SingleHandDetector (MIT).
OPERATOR2MANO = {
    "Right": np.array([[0, 0, -1], [-1, 0, 0], [0, 1, 0]], dtype=float),
    "Left": np.array([[0, 0, -1], [1, 0, 0], [0, -1, 0]], dtype=float),
}

ROBOTS = {
    "shadow": {
        "Right": {
            "type": "vector",
            "urdf_path": "shadow_hand/shadow_hand_right.urdf",
            # Fingers are measured from their own knuckle so a straight human finger maps to a
            # straight robot finger regardless of palm proportions. The thumb stays palm-relative.
            "target_origin_link_names": [
                "palm", "ffknuckle", "mfknuckle", "rfknuckle", "lfknuckle",
                "palm", "ffknuckle", "mfknuckle", "rfknuckle", "lfknuckle",
            ],
            "target_task_link_names": [
                "thtip", "fftip", "mftip", "rftip", "lftip",
                "thmiddle", "ffmiddle", "mfmiddle", "rfmiddle", "lfmiddle",
            ],
            # Input is hand-relative (MANO wrist frame), so wrist orientation is already factored
            # out. Leaving WRJ1/WRJ2 free lets the solver trade wrist flex against finger flex.
            "target_joint_names": [
                f"{finger}J{j}" for finger in ("FF", "MF", "RF") for j in (4, 3, 2, 1)
            ] + [f"LFJ{j}" for j in (5, 4, 3, 2, 1)] + [f"THJ{j}" for j in (5, 4, 3, 2, 1)],
            "scaling_factor": 1.2,
            "target_link_human_indices": [
                [0, 5, 9, 13, 17, 0, 5, 9, 13, 17],
                [4, 8, 12, 16, 20, 2, 6, 10, 14, 18],
            ],
        }
    }
}
RETARGET_CONFIG_VERSION = "shadow_right_vector/2 (knuckle-origin fingers, per-vector bone-length scale, wrist fixed at 0)"
LIMIT_EPS = 1e-3


def build_retargeter(
    robot: str = "shadow", hand: str = "Right", low_pass_alpha: float | None = None, unit_scale: bool = False
) -> tuple[SeqRetargeting, dict]:
    """low_pass_alpha=None applies no smoothing (alpha 1.0 passes values through)."""
    if hand not in ROBOTS.get(robot, {}):
        raise ValueError(f"no {hand} {robot} hand configured (only the right Shadow hand is vendored)")
    cfg = dict(ROBOTS[robot][hand], low_pass_alpha=1.0 if low_pass_alpha is None else low_pass_alpha)
    if unit_scale:
        cfg["scaling_factor"] = 1.0
    RetargetingConfig.set_default_urdf_dir(str(ROBOTS_DIR))
    return RetargetingConfig.from_dict(cfg).build(), cfg


def wrist_frame(points: np.ndarray) -> np.ndarray:
    """Orientation of the hand from wrist, index MCP, and middle MCP (MediaPipe indices 0, 5, 9)."""
    p = points[[0, 5, 9], :]
    x_vector = p[0] - p[2]
    centered = p - p.mean(axis=0, keepdims=True)
    normal = np.linalg.svd(centered)[2][2, :]
    x = x_vector - np.dot(x_vector, normal) * normal
    x = x / np.linalg.norm(x)
    z = np.cross(x, normal)
    if np.dot(z, p[1] - p[2]) < 0:
        normal, z = -normal, -z
    return np.stack([x, normal, z], axis=1)


def to_mano(world: np.ndarray, hand: str) -> np.ndarray:
    pts = world - world[0:1]
    return pts @ wrist_frame(pts) @ OPERATOR2MANO[hand]


def ref_vectors(joint_pos: np.ndarray, indices: np.ndarray) -> np.ndarray:
    return joint_pos[indices[1]] - joint_pos[indices[0]]


def _robot_vectors(retargeting: SeqRetargeting, qpos: np.ndarray, cfg: dict) -> np.ndarray:
    robot = retargeting.optimizer.robot
    robot.compute_forward_kinematics(qpos)
    pos = lambda name: robot.get_link_pose(robot.get_link_index(name))[:3, 3]
    return np.array([pos(t) - pos(o) for o, t in zip(cfg["target_origin_link_names"], cfg["target_task_link_names"])])


def _chain_length(points: np.ndarray, origin: int, index: int) -> float:
    """Origin-to-landmark length along MediaPipe bones; unchanged by finger flexion."""
    base = 1 + 4 * ((index - 1) // 4)
    chain = [0, *range(base, index + 1)] if origin == 0 else list(range(origin, index + 1))
    return float(sum(np.linalg.norm(points[b] - points[a]) for a, b in zip(chain, chain[1:])))


def bone_length_scales(frames: list[dict], retargeting: SeqRetargeting, cfg: dict) -> np.ndarray:
    """Per-vector scale = robot length with fingers extended / human bone-chain length.

    A single scaling factor leaves Shadow's long fingers curled when the human hand is
    flat. Bone lengths are taken as the median over valid frames of this clip.
    """
    robot = retargeting.optimizer.robot
    open_q = np.clip(np.zeros(robot.dof), robot.joint_limits[:, 0], robot.joint_limits[:, 1])
    robot_len = np.linalg.norm(_robot_vectors(retargeting, open_q, cfg), axis=1)
    pairs = list(zip(*cfg["target_link_human_indices"]))
    human_len = np.median(
        [[_chain_length(np.asarray(f["world"]), o, t) for o, t in pairs] for f in frames if f["valid"]], axis=0
    )
    return robot_len / human_len


BEND_CHAINS = {"TH": [0, 1, 2, 3, 4], "FF": [0, 5, 6, 7, 8], "MF": [0, 9, 10, 11, 12], "RF": [0, 13, 14, 15, 16], "LF": [0, 17, 18, 19, 20]}
ROBOT_BEND_JOINTS = {"TH": ["THJ4", "THJ2", "THJ1"], **{f: [f"{f}J3", f"{f}J2", f"{f}J1"] for f in ("FF", "MF", "RF", "LF")}}


def human_bend(points: np.ndarray, finger: str) -> float:
    """Total bend (rad) along one MediaPipe finger chain, wrist to tip."""
    total = 0.0
    chain = BEND_CHAINS[finger]
    for a, b, c in zip(chain, chain[1:], chain[2:]):
        u, v = points[b] - points[a], points[c] - points[b]
        total += float(np.arccos(np.clip(u @ v / (np.linalg.norm(u) * np.linalg.norm(v)), -1.0, 1.0)))
    return total


def bend_correlation(frames: list[dict], names: list[str], track_frames: list[dict]) -> dict:
    """Per-finger Pearson correlation between human bend and summed robot flexion over valid frames.

    Both sides come from the same MediaPipe estimate, so this checks that the robot follows the
    estimate, not that the estimate matches the real hand. None when the clip has too little motion.
    """
    world = {f["frame"]: np.asarray(f["world"]) for f in track_frames if f.get("valid")}
    idx = {n: i for i, n in enumerate(names)}
    out = {}
    for finger, joints in ROBOT_BEND_JOINTS.items():
        h, r = [], []
        for f in frames:
            if f["valid"] and f["frame"] in world:
                h.append(human_bend(world[f["frame"]], finger))
                r.append(sum(f["qpos"][idx[j]] for j in joints))
        if len(h) < 10 or np.std(h) < 0.05 or np.std(r) < 0.05:
            out[finger] = None
        else:
            out[finger] = round(float(np.corrcoef(h, r)[0, 1]), 3)
    return out


def retarget_frames(
    frames: list[dict],
    hand: str = "Right",
    robot: str = "shadow",
    keypoint_frame: str = "mediapipe_world",
    low_pass_alpha: float | None = None,
    scaling: str = "bone_length",
) -> dict:
    """frames: dicts with "frame", "t_ms", "valid", and "world" (21x3) when valid.

    keypoint_frame="mano" skips the MediaPipe wrist-frame conversion (synthetic input).
    scaling="bone_length" calibrates per-vector scales from this clip; "config" uses the
    retargeting config's single scaling_factor.
    """
    retargeting, cfg = build_retargeter(robot, hand, low_pass_alpha, unit_scale=scaling == "bone_length")
    indices = retargeting.optimizer.target_link_human_indices
    if scaling == "bone_length" and any(f["valid"] for f in frames):
        scales = bone_length_scales(frames, retargeting, cfg)
    elif scaling == "bone_length":
        scales = np.ones(len(cfg["target_task_link_names"]))
    else:
        scales = np.full(len(cfg["target_task_link_names"]), cfg["scaling_factor"])
    names = list(retargeting.joint_names)
    limits = retargeting.optimizer.robot.joint_limits
    fixed_qpos = np.zeros(len(retargeting.optimizer.idx_pin2fixed))

    out = []
    in_gap = False
    for f in frames:
        rec = {"frame": f["frame"], "t_ms": f["t_ms"], "valid": bool(f["valid"])}
        if not f["valid"]:
            rec.update(qpos=None, reason=f.get("reason", "invalid_input"))
            in_gap = True
            out.append(rec)
            continue
        if in_gap and retargeting.filter is not None:
            # Do not smooth across a tracking gap. The optimizer still warm-starts
            # from the last solution, which only affects initialization.
            retargeting.filter.reset()
        in_gap = False

        pts = np.asarray(f["world"], dtype=float)
        joint_pos = pts if keypoint_frame == "mano" else to_mano(pts, hand)
        ref = ref_vectors(joint_pos, indices) * scales[:, None]
        # dex-retargeting pads its optimizer bounds by 1e-3 rad; clip to the URDF limits.
        qpos = np.clip(
            retargeting.retarget(ref / cfg["scaling_factor"], fixed_qpos=fixed_qpos), limits[:, 0], limits[:, 1]
        )
        at_limit = [
            n for n, q, (lo, hi) in zip(names, qpos, limits) if q <= lo + LIMIT_EPS or q >= hi - LIMIT_EPS
        ]
        rec.update(
            qpos=[float(q) for q in qpos],
            at_limit=at_limit,
            vector_error_m=float(np.linalg.norm(_robot_vectors(retargeting, qpos, cfg) - ref, axis=1).mean()),
        )
        out.append(rec)

    return {
        "schema": ROBOT_MOTION_SCHEMA,
        "robot": {
            "name": f"{robot}_hand_{hand.lower()}",
            "urdf": cfg["urdf_path"],
            "source": "dexsuite/dex-urdf",
            "root_frame": "robot URDF root (forearm); no arm, wrist fixed in space",
        },
        "retargeting": {
            "library": "dex-retargeting",
            "type": cfg["type"],
            "config_version": RETARGET_CONFIG_VERSION,
            "scaling": scaling,
            "vector_scales": {n: float(v) for n, v in zip(cfg["target_task_link_names"], scales)},
            "low_pass_alpha": low_pass_alpha,
            "filter_reset_on_gap": True,
            "input": "hand-relative keypoints in MANO wrist frame; wrist translation and orientation not used",
            "fixed_joints": {n: 0.0 for n in names if n not in cfg["target_joint_names"]},
        },
        "joint_names": names,
        "units": "radians",
        "joint_limits": [[float(lo), float(hi)] for lo, hi in limits],
        "control_mode": "kinematic joint targets (applied = optimizer output after limits and filter)",
        "frames": out,
        "summary": {
            **summarize(out),
            "finger_bend_correlation": None if keypoint_frame == "mano" else bend_correlation(out, names, frames),
        },
    }


def summarize(frames: list[dict]) -> dict:
    segments = []
    for f in frames:
        key = "valid" if f["valid"] else f.get("reason", "invalid")
        if segments and segments[-1]["status"] == key:
            segments[-1]["end_frame"] = f["frame"]
            segments[-1]["end_ms"] = f["t_ms"]
        else:
            segments.append(
                {"status": key, "start_frame": f["frame"], "end_frame": f["frame"], "start_ms": f["t_ms"], "end_ms": f["t_ms"]}
            )
    valid = [f for f in frames if f["valid"]]
    errors = [f["vector_error_m"] for f in valid]
    return {
        "frames": len(frames),
        "valid_frames": len(valid),
        "valid_fraction": len(valid) / len(frames) if frames else 0.0,
        "segments": segments,
        "vector_error_m_median": float(np.median(errors)) if errors else None,
        "vector_error_m_p95": float(np.percentile(errors, 95)) if errors else None,
    }

