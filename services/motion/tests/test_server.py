"""Exercise the real HTTP handler without opening a listening socket."""

import json

import pytest

from scalpal_motion.server import make_handler


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
