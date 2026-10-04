"""The gateway worker against a stand-in gateway that follows worker-api.md v1.

The schema fixture is copied from nathan/companion-realtime (packages/contracts,
commit 9bd6517). Replace it when the contract merges.
"""

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import cv2
import jsonschema
import numpy as np
import pytest

from scalpal_motion import gateway_worker
from scalpal_motion.gateway_worker import run_worker
from scalpal_motion.retarget import retarget_frames
from scalpal_motion.synthetic import make_synthetic
from scalpal_motion.trajectory import to_robot_trajectory

SCHEMA = json.loads((Path(__file__).parent / "fixtures" / "robot-trajectory.v1.schema.json").read_text())
SAMPLE_HAND = Path.home() / "dev/third_party/dex-retargeting/example/vector_retargeting/data/human_hand_video.mp4"


class FakeGateway:
    """One queued job; records heartbeats, uploads, completion, and failure."""

    def __init__(self, clip: Path, stale_heartbeat: bool = False, config_version: str = "motion-v1",
                 fail_drops: int = 0, input_meta: dict | None = None):
        self.clip, self.stale_heartbeat = clip, stale_heartbeat
        self.fail_drops = fail_drops  # drop this many /fail requests without answering
        self.fail_attempts = 0
        self.claimed = False
        self.heartbeats, self.registered, self.uploaded = [], {}, {}
        self.completed = self.failed = None
        gw = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _json(self, code, obj=None):
                body = json.dumps(obj).encode() if obj is not None else b""
                self.send_response(code)
                self.send_header("content-type", "application/json")
                self.send_header("content-length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _body(self):
                return self.rfile.read(int(self.headers.get("content-length", 0)))

            def do_GET(self):
                if self.path == "/files/clip":
                    data = gw.clip.read_bytes()
                    self.send_response(200)
                    self.send_header("content-length", str(len(data)))
                    self.end_headers()
                    self.wfile.write(data)
                else:
                    self._json(404, {})

            def do_PUT(self):
                art = self.path.rsplit("/", 1)[-1]
                gw.uploaded[art] = self._body()
                self._json(200)

            def do_POST(self):
                assert self.headers["authorization"] == "Bearer tok"
                body = json.loads(self._body() or b"{}")
                base = "/v1/worker/jobs/job_1/runs/1"
                if self.path == "/v1/worker/claim":
                    if gw.claimed:
                        return self._json(204)
                    gw.claimed = True
                    return self._json(200, {
                        "job": {"jobId": "job_1", "run": 1, "sessionId": "ses_1", "attemptId": "ses_1-a1",
                                "configVersion": config_version, "leaseMs": 3000},
                        "inputs": [{"role": "input", "artifactId": "art_clip", "kind": "raw_clip",
                                    "filename": "clip.mp4", "contentType": "video/mp4",
                                    "download": {"url": "/files/clip", "method": "GET", "headers": {}},
                                    **(input_meta or {})}],
                        "endpoints": {k: f"{base}/{k}" for k in ("heartbeat", "outputs", "complete", "fail")},
                    })
                if self.path == f"{base}/heartbeat":
                    gw.heartbeats.append(body)
                    return self._json(409, {"error": "stale"}) if gw.stale_heartbeat else self._json(200, {})
                if self.path == f"{base}/outputs":
                    art = f"art_{len(gw.registered)}"
                    gw.registered[art] = body
                    return self._json(200, {"artifactId": art, "upload": {
                        "url": f"/upload/{art}", "method": "PUT", "headers": {"content-type": body["contentType"]}}})
                if self.path == f"{base}/complete":
                    if any(a not in gw.uploaded for a in body["outputArtifactIds"]):
                        return self._json(422, {"error": "missing upload"})
                    gw.completed = body
                    return self._json(200, {})
                if self.path == f"{base}/fail":
                    gw.fail_attempts += 1
                    if gw.fail_attempts <= gw.fail_drops:
                        self.close_connection = True  # network error: no response at all
                        return
                    gw.failed = body
                    return self._json(200, {})
                self._json(404, {})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.server.server_port}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()


def _blank_clip(path, frames=10):
    w = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), 30, (160, 120))
    for _ in range(frames):
        w.write(np.zeros((120, 160, 3), np.uint8))
    w.release()
    return path


def test_trajectory_matches_companion_schema():
    frames, _ = make_synthetic(n_frames=40, gap=(10, 15))
    traj = to_robot_trajectory(retarget_frames(frames, keypoint_frame="mano", scaling="config"), "art_x")
    jsonschema.validate(traj, SCHEMA)
    assert len(traj["robot"]["joints"]) == len(traj["frames"]["q"][0]) == 24
    assert traj["frames"]["valid"][10:15] == [False] * 5
    assert traj["frames"]["q"][12] == traj["frames"]["q"][9]  # gap holds the last valid pose
    assert traj["invalidIntervals"][0]["reason"] == "synthetic_gap"


def test_clip_without_hand_fails_final(tmp_path):
    gw = FakeGateway(_blank_clip(tmp_path / "blank.mp4"))
    try:
        run_worker(gw.url, "tok", once=True, log=lambda *_: None)
    finally:
        gw.close()
    assert gw.completed is None
    assert gw.failed["retryable"] is False and "no right hand" in gw.failed["error"]
    assert gw.heartbeats and gw.heartbeats[0]["stage"] == "downloading clip"


def test_stale_run_is_dropped_without_completing(tmp_path):
    gw = FakeGateway(_blank_clip(tmp_path / "blank.mp4"), stale_heartbeat=True)
    try:
        run_worker(gw.url, "tok", once=True, log=lambda *_: None)
    finally:
        gw.close()
    assert gw.completed is None and gw.failed is None and not gw.registered


@pytest.mark.skipif(not SAMPLE_HAND.exists(), reason="dex-retargeting sample hand video not present")
def test_real_hand_clip_completes_with_all_outputs():
    gw = FakeGateway(SAMPLE_HAND)
    try:
        run_worker(gw.url, "tok", mirrored=True, once=True, log=lambda *_: None)
    finally:
        gw.close()
    assert gw.failed is None, gw.failed
    kinds = {gw.registered[a]["kind"] for a in gw.completed["outputArtifactIds"]}
    assert kinds == {"robot_trajectory", "replay_video", "hand_estimates", "quality_report"}
    q = gw.completed["quality"]
    assert q["framesValid"] > 0.9 * q["framesTotal"] and q["replayKind"] == "kinematic"
    traj_id = next(a for a, r in gw.registered.items() if r["kind"] == "robot_trajectory")
    jsonschema.validate(json.loads(gw.uploaded[traj_id]), SCHEMA)


def test_unsupported_config_fails_before_downloading_or_inference(tmp_path):
    # No file exists: entering the download route would fail rather than satisfy this receipt.
    gw = FakeGateway(tmp_path / "must-not-read.mp4", config_version="motion-v999")
    try:
        run_worker(gw.url, "tok", once=True, log=lambda *_: None)
    finally:
        gw.close()
    assert gw.completed is None and not gw.registered and not gw.uploaded
    assert gw.failed["retryable"] is False
    assert gw.failed["error"] == "unsupported motion configVersion; expected motion-v1"
    assert not gw.heartbeats


@pytest.fixture
def no_backoff(monkeypatch):
    monkeypatch.setattr(gateway_worker, "FAIL_REPORT_BACKOFF_S", (0, 0, 0))


def test_fail_report_retries_through_network_errors(tmp_path, no_backoff):
    # M1: a gateway blip while reporting a failure must not lose the report.
    gw = FakeGateway(_blank_clip(tmp_path / "blank.mp4"), fail_drops=2)
    try:
        run_worker(gw.url, "tok", once=True, log=lambda *_: None)
    finally:
        gw.close()
    assert gw.fail_attempts == 3
    assert gw.failed["retryable"] is False and "no right hand" in gw.failed["error"]


def test_unreportable_failure_does_not_kill_the_worker(tmp_path, no_backoff):
    # M1: if the gateway stays unreachable, log it and return; the lease sweep
    # requeues the run.
    gw = FakeGateway(_blank_clip(tmp_path / "blank.mp4"), fail_drops=1000)
    logs = []
    try:
        run_worker(gw.url, "tok", once=True, log=logs.append)
    finally:
        gw.close()
    assert gw.failed is None
    assert gw.fail_attempts == len(gateway_worker.FAIL_REPORT_BACKOFF_S) + 1
    assert any("could not report failure" in line for line in logs)


def _run_with_input_meta(tmp_path, meta_for):
    clip = _blank_clip(tmp_path / "blank.mp4")
    gw = FakeGateway(clip, input_meta=meta_for(clip.read_bytes()))
    try:
        run_worker(gw.url, "tok", once=True, log=lambda *_: None)
    finally:
        gw.close()
    return gw


def test_input_with_wrong_sha256_fails_final_before_inference(tmp_path):
    # M8: a replaced or corrupted object must not be processed as the claimed clip.
    gw = _run_with_input_meta(tmp_path, lambda data: {"bytes": len(data), "sha256": "0" * 64})
    assert gw.failed["retryable"] is False and "sha256 mismatch" in gw.failed["error"]
    assert "hand inference" not in [h["stage"] for h in gw.heartbeats]
    assert gw.completed is None and not gw.registered


def test_input_with_wrong_size_fails_final(tmp_path):
    gw = _run_with_input_meta(tmp_path, lambda data: {"bytes": len(data) + 1, "sha256": None})
    assert gw.failed["retryable"] is False and "size mismatch" in gw.failed["error"]


def test_input_larger_than_claimed_is_cut_off(tmp_path):
    # M8: the download stops at the claimed size instead of filling the disk.
    gw = _run_with_input_meta(tmp_path, lambda data: {"bytes": 16, "sha256": None})
    assert gw.failed["retryable"] is False and "larger than" in gw.failed["error"]


def test_input_matching_its_claim_is_processed(tmp_path):
    import hashlib

    gw = _run_with_input_meta(tmp_path, lambda data: {"bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
    # Verification passed, so the blank clip reaches inference and fails there.
    assert "no right hand" in gw.failed["error"]


class _StopLoop(BaseException):
    """Ends run_worker's infinite loop from a test (Exception would be caught)."""


def test_unexpected_handler_crash_keeps_polling(monkeypatch):
    # M1: any exception from one job is logged and the loop polls again.
    replies = iter([(200, {"job": {"jobId": "job_1", "run": 1}})])
    polls = []

    class Gw:
        def __init__(self, url, token):
            self.url = url

        def call(self, path, body=None):
            polls.append(path)
            try:
                return next(replies)
            except StopIteration:
                raise _StopLoop from None

    def crash(*_a, **_k):
        raise RuntimeError("handler bug")

    monkeypatch.setattr(gateway_worker, "Gateway", Gw)
    monkeypatch.setattr(gateway_worker, "process_claim", crash)
    logs = []
    with pytest.raises(_StopLoop):
        run_worker("http://gateway.test", "tok", log=logs.append)
    assert polls == ["/v1/worker/claim", "/v1/worker/claim"], "the loop must survive the crash"
    assert any("handler bug" in line for line in logs)
