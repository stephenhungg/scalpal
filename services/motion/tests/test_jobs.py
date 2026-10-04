import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import cv2
import numpy as np
import pytest

from scalpal_motion.jobs import JOB_SCHEMA, InputPolicy, run_job


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


# M7: a served job may only read from the gateway's file route (or configured
# prefixes) and from a configured input directory, never anywhere else.

GATEWAY_FILES = "http://localhost:8788/files/"


@pytest.mark.parametrize("url", [
    "http://localhost:8788/files/sessions/s/a/clip.mp4?m=GET&sig=x",
    "http://LOCALHOST:8788/files/x",
])
def test_policy_allows_configured_url_prefix(url):
    assert InputPolicy(url_prefixes=(GATEWAY_FILES,)).allows_url(url)


@pytest.mark.parametrize("url", [
    "http://169.254.169.254/latest/meta-data/",
    "http://localhost:8788.evil.example/files/x",
    "http://user@localhost:8788/files/x",
    "http://localhost:9999/files/x",
    "https://localhost:8788/files/x",
    "http://localhost:8788/admin",
    "http://localhost:8788/files/../admin",
    "file:///etc/passwd",
])
def test_policy_rejects_other_urls(url):
    assert not InputPolicy(url_prefixes=(GATEWAY_FILES,)).allows_url(url)


def test_served_job_cannot_fetch_an_arbitrary_url(tmp_path):
    result = run_job(_job("http://169.254.169.254/latest/meta-data/"), tmp_path / "runs",
                     policy=InputPolicy(url_prefixes=(GATEWAY_FILES,)))
    assert result["error"]["code"] == "input_not_allowed"
    assert not (tmp_path / "runs").exists(), "a refused job must not create a run directory"


def test_served_job_reads_local_files_only_inside_the_input_dir(tmp_path):
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    outside = _blank_clip(tmp_path / "outside.mp4")
    (inputs / "link.mp4").symlink_to(outside)
    policy = InputPolicy(input_dir=inputs)
    for source in (outside, inputs / ".." / "outside.mp4", inputs / "link.mp4"):
        assert run_job(_job(source), tmp_path / "runs", policy=policy)["error"]["code"] == "input_not_allowed"
    assert run_job(_job(outside), tmp_path / "runs", policy=InputPolicy())["error"]["code"] == "input_not_allowed"
    inside = _blank_clip(inputs / "blank.mp4")
    assert run_job(_job(inside), tmp_path / "runs", policy=policy)["error"]["code"] == "no_hand_detected"


def test_served_job_input_is_size_capped(tmp_path):
    inputs = tmp_path / "inputs"
    inputs.mkdir()
    clip = _blank_clip(inputs / "blank.mp4")
    policy = InputPolicy(input_dir=inputs, max_bytes=clip.stat().st_size - 1)
    assert run_job(_job(clip), tmp_path / "runs", policy=policy)["error"]["code"] == "input_too_large"


def _serve_once(handler_body):
    class H(BaseHTTPRequestHandler):
        def log_message(self, *a):
            pass

        do_GET = handler_body

    server = ThreadingHTTPServer(("127.0.0.1", 0), H)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def test_allowed_url_cannot_redirect_elsewhere(tmp_path):
    hits = []

    def target(self):
        hits.append(self.path)
        self.send_response(200)
        self.send_header("content-length", "3")
        self.end_headers()
        self.wfile.write(b"abc")

    def redirector(self):
        self.send_response(302)
        self.send_header("location", f"http://127.0.0.1:{other.server_port}/secret")
        self.send_header("content-length", "0")
        self.end_headers()

    other = _serve_once(target)
    allowed = _serve_once(redirector)
    try:
        prefix = f"http://127.0.0.1:{allowed.server_port}/files/"
        result = run_job(_job(prefix + "clip.mp4"), tmp_path / "runs", policy=InputPolicy(url_prefixes=(prefix,)))
    finally:
        other.shutdown()
        allowed.shutdown()
    assert result["error"]["code"] == "input_unavailable"
    assert hits == [], "the redirect target must never be fetched"


def test_url_download_is_size_capped(tmp_path):
    def big(self):
        self.send_response(200)
        self.end_headers()  # no content-length: the cap must apply while streaming
        self.wfile.write(b"x" * 4096)

    server = _serve_once(big)
    try:
        prefix = f"http://127.0.0.1:{server.server_port}/files/"
        result = run_job(_job(prefix + "clip.mp4"), tmp_path / "runs",
                         policy=InputPolicy(url_prefixes=(prefix,), max_bytes=1024))
    finally:
        server.shutdown()
    assert result["error"]["code"] == "input_too_large"
