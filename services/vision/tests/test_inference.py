"""Real inference on a committed public-domain image. Skipped when weights are not cached."""

import os
from pathlib import Path

import pytest

from scalpal_vision.detector import DEFAULT_MODEL, MODELS, Detector, decode_image

FIXTURE = Path(__file__).parent / "fixtures" / "hand_holding_scissors.jpg"


def weights_cached(repo: str) -> bool:
    try:
        from huggingface_hub import try_to_load_from_cache

        path = try_to_load_from_cache(repo, "config.json")
        return isinstance(path, str)
    except Exception:
        return False


@pytest.fixture(scope="module")
def detector():
    if not weights_cached(MODELS[DEFAULT_MODEL]):
        pytest.skip(f"{MODELS[DEFAULT_MODEL]} weights not cached; run the service once online")
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    return Detector(DEFAULT_MODEL)


def test_fixture_is_small():
    assert FIXTURE.stat().st_size < 200_000


def test_finds_scissors_and_hand(detector):
    image = decode_image(FIXTURE.read_bytes())
    detections = detector.detect(image, ["scissors", "hand", "liver"], 0.2)
    best = {}
    for det in detections:
        best.setdefault(det["label"], det)
    assert "scissors" in best and "hand" in best, detections
    scissors = best["scissors"]
    assert scissors["id"] == "lap_scissors"
    box = scissors["box"]
    # The scissors fill most of this portrait image: centered horizontally, spanning top to bottom.
    assert box["h"] > 0.6 and box["w"] > 0.5
    assert 0.3 < box["x"] + box["w"] / 2 < 0.7
    hand = best["hand"]["box"]
    assert hand["y"] + hand["h"] / 2 > 0.6  # the hand is in the lower part
    for det in detections:
        b = det["box"]
        assert 0 <= b["x"] <= 1 and 0 <= b["y"] <= 1 and b["x"] + b["w"] <= 1.00001 and b["y"] + b["h"] <= 1.00001
    assert "liver" not in best
