import json
from pathlib import Path

from PIL import Image
import pytest

from scalpal_motion.mark.plot import _read_curve, plot_learning_curve


SOURCE = Path(__file__).resolve().parents[1] / "learning-results/robot_mark_curve.json"


def test_saved_curve_is_rendered_without_changing_evidence(tmp_path):
    before = SOURCE.read_bytes()
    output = plot_learning_curve(SOURCE, tmp_path / "curve.png")
    with Image.open(output) as image:
        assert image.size == (1920, 1080)
        assert "0 headset demonstrations" in image.info["Description"]
        assert "seed range, not confidence interval" in image.info["Description"]
        for rate in ("0.383", "0.717", "0.883", "0.950", "1.000"):
            assert rate in image.info["Description"]
    assert SOURCE.read_bytes() == before


@pytest.mark.parametrize("change", ["human_source", "seed_count", "aggregate"])
def test_refuses_mislabeled_or_inconsistent_evidence(tmp_path, change):
    curve = json.loads(SOURCE.read_text())
    if change == "human_source":
        curve["rows"][0]["humanDemos"] = 1
    elif change == "seed_count":
        curve["rows"][0]["rollouts"] = 30
    else:
        curve["rows"][0]["successRate"] = 1
    source = tmp_path / "bad.json"
    source.write_text(json.dumps(curve))
    with pytest.raises(ValueError):
        _read_curve(source)
