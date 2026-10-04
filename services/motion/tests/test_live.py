"""Headset joint stream (scalpal.hand_joints.v1) -> live retargeting and episode import."""

import json
from pathlib import Path

import numpy as np

from scalpal_motion.live import LiveRetargeter, episode_frames, pick_hand


def _frame(index, hands):
    return {"schema": "scalpal.hand_joints.v1", "frameIndex": index, "unityTime": index / 30, "imageWidth": 1280, "imageHeight": 960, "hands": hands}


def _hand(side, presence, world):
    return {"hand": side, "presence": presence, "pixels": [0.0] * 42, "worldModelMeters": np.asarray(world).ravel().tolist()}


def test_pick_hand_prefers_requested_side_then_confidence():
    w = np.zeros((21, 3))
    frame = _frame(0, [_hand("left", 0.99, w), _hand("right", 0.6, w)])
    assert pick_hand(frame, "Right")["hand"] == "right"
    assert pick_hand(_frame(0, [_hand("left", 0.7, w), _hand("left", 0.9, w)]), "Right")["presence"] == 0.9
    assert pick_hand(_frame(0, []), "Right") is None


def test_episode_frames_marks_missing_hands_invalid(tmp_path):
    world = np.random.default_rng(0).normal(size=(21, 3)) * 0.02
    lines = [_frame(0, [_hand("right", 0.95, world)]), _frame(1, []), _frame(2, [_hand("right", 0.9, world)])]
    path = tmp_path / "frames.jsonl"
    path.write_text("\n".join(json.dumps(f) for f in lines))
    frames, meta = episode_frames(path, "Right")
    assert [f["valid"] for f in frames] == [True, False, True]
    assert frames[1]["reason"] == "no_hand_in_frame"
    assert meta == {"source": str(path), "frames": 3, "valid": 2}
    assert np.allclose(frames[2]["world"], world)


FIXTURE = Path(__file__).parent / "fixtures" / "hand_joints_sample.jsonl"


def test_live_retargeter_calibrates_and_stays_in_limits():
    # MediaPipe joints from dex-retargeting's public sample clip (MIT), streamed by `scalpal-motion send`.
    frames = [json.loads(line) for line in FIXTURE.read_text().splitlines()]
    live = LiveRetargeter("Right")
    for _ in range(2):  # 20 frames reaches calibration
        for f in frames:
            q = live(np.asarray(pick_hand(f, "Right")["worldModelMeters"], float).reshape(21, 3))
            assert q is not None
            assert np.all(q >= live.limits[:, 0] - 1e-6) and np.all(q <= live.limits[:, 1] + 1e-6)
    assert live.calibrated


def test_live_retargeter_rejects_unusable_frames_instead_of_hanging():
    live = LiveRetargeter("Right")
    assert live(np.zeros((21, 3))) is None
    assert live(np.full((21, 3), np.nan)) is None
    assert live(np.zeros((20, 3))) is None
