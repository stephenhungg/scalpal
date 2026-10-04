"""Exercise the real HTTP handler without opening a listening socket."""

import io
import json

import pytest

from scalpal_motion.jobs import InputPolicy
from scalpal_motion.server import make_handler, resolve_token


def _get(root, path, token=None, authorization=None):
    handler_type = make_handler(root, token)
    handler = handler_type.__new__(handler_type)
    handler.path = path
    handler.headers = {"Authorization": authorization} if authorization else {}
    received = []
    handler._send = lambda code, body, content_type="application/json": received.append((code, body))
    handler.do_GET()
    return received[0]


@pytest.mark.parametrize("run", ["..", ".", "%2e%2e", "run%2Falias"])
def test_output_read_cannot_escape_or_alias_run_id(tmp_path, run):
    root = tmp_path / "runs"
    root.mkdir()
    (tmp_path / "result.json").write_text("outside-root fixture")
    alias = root / "run2Falias"
    alias.mkdir()
    (alias / "result.json").write_text("sanitized alias fixture")
    status, body = _get(root, f"/runs/{run}/result.json")
    assert status == 404 and json.loads(body) == {"error": "not_found"}


def test_output_read_rejects_symlink_outside_root(tmp_path):
    root = tmp_path / "runs"
    root.mkdir()
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "result.json").write_text("outside-root fixture")
    (root / "run-1").symlink_to(outside, target_is_directory=True)
    assert _get(root, "/runs/run-1/result.json")[0] == 404


def test_valid_output_read_still_requires_configured_token(tmp_path):
    run = tmp_path / "run-1"
    run.mkdir()
    (run / "result.json").write_text('{"status":"ready"}')
    assert _get(tmp_path, "/runs/run-1/result.json", token="secret")[0] == 401
    assert _get(tmp_path, "/runs/run-1/result.json", token="secret", authorization="Bearer secret") == (
        200, b'{"status":"ready"}')


# M7: the local server must not be an open, unauthenticated fetch-anything proxy.

def _post(root, body: bytes, length=None, policy=None, token="secret"):
    handler_type = make_handler(root, token, policy)
    handler = handler_type.__new__(handler_type)
    handler.path = "/jobs"
    handler.headers = {"Authorization": f"Bearer {token}",
                       "Content-Length": str(len(body) if length is None else length)}
    handler.rfile = io.BytesIO(body)
    received = []
    handler._send = lambda code, body, content_type="application/json": received.append((code, body))
    handler.do_POST()
    return received[0]


def test_serve_always_has_a_token():
    assert resolve_token({}) and len(resolve_token({})) >= 16
    assert resolve_token({}) != resolve_token({})
    assert resolve_token({"SCALPAL_MOTION_TOKEN": "configured-token"}) == "configured-token"


def test_policy_from_env_defaults_to_the_local_gateway_only(tmp_path):
    policy = InputPolicy.from_env({})
    assert policy.allows_url("http://localhost:8788/files/k?sig=x")
    assert not policy.allows_url("http://example.com/clip.mp4")
    assert policy.input_dir is None
    configured = InputPolicy.from_env({"SCALPAL_MOTION_INPUT_DIR": str(tmp_path),
                                       "SCALPAL_MOTION_ALLOWED_URL_PREFIXES": "https://bucket.example/clips/",
                                       "SCALPAL_MOTION_MAX_INPUT_BYTES": "1000"})
    assert configured.input_dir == tmp_path.resolve() and configured.max_bytes == 1000
    assert configured.allows_url("https://bucket.example/clips/a.mp4")
    assert not configured.allows_url("http://localhost:8788/files/k")


def test_posted_job_cannot_fetch_internal_urls(tmp_path):
    job = {"schema": "scalpal.motion_job/0", "job_id": "j", "run_id": "r1",
           "input": {"source": "http://169.254.169.254/latest/meta-data/"}}
    status, body = _post(tmp_path, json.dumps(job).encode(), policy=InputPolicy.from_env({}))
    assert status == 200 and json.loads(body)["error"]["code"] == "input_not_allowed"


@pytest.mark.parametrize("length", ["-1", "abc", "0", str(10 * 1024 * 1024)])
def test_bad_content_length_is_refused_without_reading(tmp_path, length):
    status, _ = _post(tmp_path, b"{}", length=length)
    assert status in (400, 413)
