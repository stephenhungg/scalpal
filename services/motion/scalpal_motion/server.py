"""Minimal HTTP wrapper around run_job for local integration with Nathan's gateway.

POST /jobs            body: scalpal.motion_job/0 JSON -> scalpal.motion_result/0 JSON (synchronous)
GET  /runs/<run>/<f>  one output file of a run (hand_track.json, motion.json, replay.mp4, result.json)
GET  /health

Binds to 127.0.0.1 by default. If SCALPAL_MOTION_TOKEN is set, every request needs
"Authorization: Bearer <token>". Jobs run one at a time.
"""

from __future__ import annotations

import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from .jobs import run_job

SERVED_FILES = {
    "hand_track.json": "application/json",
    "motion.json": "application/json",
    "result.json": "application/json",
    "replay.mp4": "video/mp4",
}


def make_handler(output_root: Path, token: str | None):
    lock = threading.Lock()

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
                run = "".join(c for c in parts[1] if c.isalnum() or c in "-_.")
                path = output_root / run / parts[2]
                if run and path.is_file():
                    return self._send(200, path.read_bytes(), SERVED_FILES[parts[2]])
            self._json(404, {"error": "not_found"})

        def do_POST(self) -> None:
            if not self._authorized():
                return
            if self.path.rstrip("/") != "/jobs":
                return self._json(404, {"error": "not_found"})
            try:
                job = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
            except (ValueError, json.JSONDecodeError):
                return self._json(400, {"error": "invalid_json"})
            with lock:
                result = run_job(job, output_root)
            self._json(200, result)

        def log_message(self, fmt: str, *args) -> None:
            print(f"[motion] {self.address_string()} {fmt % args}")

    return Handler


def serve(host: str, port: int, output_root: Path) -> None:
    output_root.mkdir(parents=True, exist_ok=True)
    token = os.environ.get("SCALPAL_MOTION_TOKEN")
    server = ThreadingHTTPServer((host, port), make_handler(output_root, token))
    print(f"scalpal-motion worker on http://{host}:{port} (auth {'on' if token else 'off'}), outputs in {output_root}")
    server.serve_forever()
