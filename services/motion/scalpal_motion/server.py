"""Minimal HTTP wrapper around run_job for local integration with Nathan's gateway.

POST /jobs            body: scalpal.motion_job/0 JSON -> scalpal.motion_result/0 JSON (synchronous)
GET  /runs/<run>/<f>  one output file of a run (hand_track.json, motion.json, replay.mp4, result.json)
GET  /health

Binds to 127.0.0.1 by default. Every request needs "Authorization: Bearer <token>":
SCALPAL_MOTION_TOKEN, or a random token printed at startup. input.source must be under
SCALPAL_MOTION_ALLOWED_URL_PREFIXES (default: the local gateway's /files/ route) or, for
local paths, under SCALPAL_MOTION_INPUT_DIR (unset: local paths refused). Inputs are capped
at SCALPAL_MOTION_MAX_INPUT_BYTES. Jobs run one at a time.
"""

from __future__ import annotations

import json
import os
import secrets
import threading
from collections.abc import Mapping
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from .jobs import InputPolicy, run_job, valid_run_id

SERVED_FILES = {
    "hand_track.json": "application/json",
    "motion.json": "application/json",
    "result.json": "application/json",
    "replay.mp4": "video/mp4",
}
MAX_JOB_BODY_BYTES = 64 * 1024


def resolve_token(env: Mapping[str, str] = os.environ) -> str:
    """The configured token, or a fresh random one: the server never runs without auth."""
    return env.get("SCALPAL_MOTION_TOKEN") or secrets.token_urlsafe(24)


def make_handler(output_root: Path, token: str | None, policy: InputPolicy | None = None):
    lock = threading.Lock()
    policy = policy if policy is not None else InputPolicy()  # default: no input allowed

    class Handler(BaseHTTPRequestHandler):
        def _send(self, code: int, body: bytes, content_type: str = "application/json") -> None:
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _json(self, code: int, obj: dict) -> None:
            self._send(code, json.dumps(obj).encode())

        def _authorized(self) -> bool:
            if token and self.headers.get("Authorization") != f"Bearer {token}":
                self._json(401, {"error": "unauthorized"})
                return False
            return True

        def do_GET(self) -> None:
            if not self._authorized():
                return
            parts = self.path.strip("/").split("/")
            if parts == ["health"]:
                return self._json(200, {"ok": True})
            if len(parts) == 3 and parts[0] == "runs" and parts[2] in SERVED_FILES:
                run = parts[1]
                if not valid_run_id(run):
                    return self._json(404, {"error": "not_found"})
                path = (output_root / run / parts[2]).resolve()
                if path.is_relative_to(output_root.resolve()) and path.is_file():
                    return self._send(200, path.read_bytes(), SERVED_FILES[parts[2]])
            self._json(404, {"error": "not_found"})

        def do_POST(self) -> None:
            if not self._authorized():
                return
            if self.path.rstrip("/") != "/jobs":
                return self._json(404, {"error": "not_found"})
            length = self.headers.get("Content-Length", "")
            if not length.isdigit() or int(length) == 0:
                return self._json(400, {"error": "content_length_required"})
            if int(length) > MAX_JOB_BODY_BYTES:
                return self._json(413, {"error": "job_too_large"})
            try:
                job = json.loads(self.rfile.read(int(length)))
            except (ValueError, json.JSONDecodeError):
                return self._json(400, {"error": "invalid_json"})
            with lock:
                result = run_job(job, output_root, policy)
            self._json(200, result)

        def log_message(self, fmt: str, *args) -> None:
            print(f"[motion] {self.address_string()} {fmt % args}")

    return Handler


def serve(host: str, port: int, output_root: Path) -> None:
    output_root.mkdir(parents=True, exist_ok=True)
    configured = bool(os.environ.get("SCALPAL_MOTION_TOKEN"))
    token = resolve_token()
    policy = InputPolicy.from_env()
    server = ThreadingHTTPServer((host, port), make_handler(output_root, token, policy))
    print(f"scalpal-motion worker on http://{host}:{port}, outputs in {output_root}")
    if not configured:
        print(f"SCALPAL_MOTION_TOKEN not set; token for this run: {token}")
    print(f"inputs: URLs under {list(policy.url_prefixes)}, local paths under "
          f"{policy.input_dir or '(disabled)'}, max {policy.max_bytes} bytes")
    server.serve_forever()
