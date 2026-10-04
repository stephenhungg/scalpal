"""Convert a robot-motion record into Nathan's companion format, scalpal.robot_trajectory.v1
(packages/contracts/robot-trajectory.v1.schema.json on nathan/companion-realtime).
"""

from __future__ import annotations

from importlib.metadata import version

TRAJECTORY_SCHEMA = "scalpal.robot_trajectory.v1"
NOTES = (
    "Kinematic replay of finger motion estimated from monocular video. Wrist position and "
    "orientation are not reconstructed; robot wrist joints are held at 0. Invalid frames hold "
    "the last valid pose and are marked valid=false. Replay of recorded motion, not a learned policy."
)


def to_robot_trajectory(motion: dict, input_artifact_id: str | None = None, hand: str = "Right") -> dict:
    names = motion["joint_names"]
    limits = motion["joint_limits"]
    # Before the first valid frame there is no observed pose; use the neutral pose clipped to limits.
    neutral = [min(max(0.0, lo), hi) for lo, hi in limits]

    t, q, valid = [], [], []
    last = neutral
    for f in motion["frames"]:
        if f["valid"]:
            last = f["qpos"]
        t.append(float(f["t_ms"]))
        q.append([float(v) for v in last])
        valid.append(bool(f["valid"]))

    intervals = [
        {"startMs": seg["start_ms"], "endMs": seg["end_ms"], "reason": seg["status"]}
        for seg in motion["summary"]["segments"]
        if seg["status"] != "valid"
    ]
    model = motion.get("input", {}).get("model", {})
    return {
        "schema": TRAJECTORY_SCHEMA,
        "kind": "kinematic",
        "robot": {
            "model": motion["robot"]["name"],
            "version": f"dex-urdf {motion['robot']['urdf']}",
            "joints": [{"name": n, "unit": "rad", "lower": lo, "upper": hi} for n, (lo, hi) in zip(names, limits)],
        },
        "timebase": {"unit": "ms", "clock": "clip_pts", "startMs": t[0] if t else 0.0},
        "frames": {"t": t, "q": q, "valid": valid},
        "invalidIntervals": intervals,
        "source": {
            **({"inputArtifactId": input_artifact_id} if input_artifact_id else {}),
            "handedness": hand.lower(),
            "perception": f"mediapipe-hand-landmarker@{model.get('mediapipe', 'unknown')}",
            "retargeting": f"dex-retargeting@{version('dex-retargeting')} {motion['retargeting']['config_version']}",
        },
        "notes": NOTES,
    }
