import json

import cv2
import numpy as np
import pytest

from scalpal_motion.jobs import JOB_SCHEMA, run_job


def _job(source, run_id="run-1"):
    return {"schema": JOB_SCHEMA, "job_id": "job-1", "run_id": run_id, "attempt_id": "att-1",
            "input": {"artifact_id": "art-1", "source": str(source)}}


def _blank_clip(path, frames=10):
    writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), 30, (160, 120))
    for _ in range(frames):
        writer.write(np.zeros((120, 160, 3), np.uint8))
    writer.release()
    return path


def test_bad_job_is_rejected_without_outputs(tmp_path):
    result = run_job({"schema": "nope"}, tmp_path)
    assert result["status"] == "failed" and result["error"]["code"] == "bad_job"
    assert not any(tmp_path.iterdir())


@pytest.mark.parametrize("run_id", ["..", ".", "../escape", "run/alias", "run%2Falias", "", "a" * 129, 12])
def test_unsafe_run_id_is_rejected_without_outputs(tmp_path, run_id):
    result = run_job(_job("unused.mp4", run_id), tmp_path)
    assert result["status"] == "failed" and result["error"]["code"] == "bad_job"
    assert not any(tmp_path.iterdir())


@pytest.mark.parametrize("job", [None, [], "text", {"schema": JOB_SCHEMA, "job_id": "job", "run_id": "run", "input": "path"}])
def test_wrong_job_shape_reports_bad_job(tmp_path, job):
    result = run_job(job, tmp_path)
    assert result["status"] == "failed" and result["error"]["code"] == "bad_job"
    assert not any(tmp_path.iterdir())


def test_missing_input_reports_input_unavailable(tmp_path):
    result = run_job(_job(tmp_path / "missing.mp4"), tmp_path / "runs")
    assert result["error"]["code"] == "input_unavailable"


def test_clip_without_hand_fails_honestly(tmp_path):
    result = run_job(_job(_blank_clip(tmp_path / "blank.mp4")), tmp_path / "runs")
    assert result["status"] == "failed" and result["error"]["code"] == "no_hand_detected"
    assert result["quality"]["valid_frames"] == 0
    assert [a["kind"] for a in result["artifacts"]] == ["hand_track", "robot_motion"]


def test_repeated_run_id_never_overwrites_existing_result(tmp_path):
    clip = _blank_clip(tmp_path / "blank.mp4")
    first = run_job(_job(clip), tmp_path / "runs")
    saved = (tmp_path / "runs" / "run-1" / "result.json").read_text()
    second = run_job(_job(clip), tmp_path / "runs")
    assert second["error"]["code"] == "run_exists" and "output_dir" not in second
    assert (tmp_path / "runs" / "run-1" / "result.json").read_text() == saved
    assert json.loads(saved)["run_id"] == first["run_id"]
