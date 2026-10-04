import numpy as np

from scalpal_motion.retarget import retarget_frames
from scalpal_motion.synthetic import make_synthetic


def test_synthetic_round_trip_recovers_link_vectors():
    frames, truth = make_synthetic(n_frames=60, gap=(25, 32))
    motion = retarget_frames(frames, keypoint_frame="mano", scaling="config")
    s = motion["summary"]
    assert s["valid_frames"] == 53
    assert s["vector_error_m_median"] < 0.005
    q_true = np.array(truth["qpos"])
    errs = [np.abs(np.array(f["qpos"]) - q_true[f["frame"]]).mean() for f in motion["frames"] if f["valid"]]
    assert np.median(errs) < 0.1


def test_gap_is_reported_not_filled():
    frames, _ = make_synthetic(n_frames=60, gap=(25, 32))
    motion = retarget_frames(frames, keypoint_frame="mano", scaling="config", low_pass_alpha=0.3)
    gap = [f for f in motion["frames"] if 25 <= f["frame"] < 32]
    assert all(not f["valid"] and f["qpos"] is None and f["reason"] == "synthetic_gap" for f in gap)
    statuses = [seg["status"] for seg in motion["summary"]["segments"]]
    assert statuses == ["valid", "synthetic_gap", "valid"]


def test_joints_are_named_and_wrist_fixed():
    frames, _ = make_synthetic(n_frames=10, gap=None)
    motion = retarget_frames(frames, keypoint_frame="mano", scaling="config")
    names = motion["joint_names"]
    assert len(names) == len(set(names)) == 24
    wrist = [names.index("WRJ1"), names.index("WRJ2")]
    assert all(f["qpos"][i] == 0.0 for f in motion["frames"] for i in wrist)
    lo, hi = np.array(motion["joint_limits"]).T
    q = np.array([f["qpos"] for f in motion["frames"]])
    assert np.all(q >= lo - 1e-6) and np.all(q <= hi + 1e-6)


def test_left_hand_round_trip():
    frames, _ = make_synthetic(n_frames=30, gap=None, hand="Left")
    motion = retarget_frames(frames, hand="Left", keypoint_frame="mano", scaling="config")
    assert motion["robot"]["name"] == "shadow_hand_left"
    assert motion["summary"]["vector_error_m_median"] < 0.005
