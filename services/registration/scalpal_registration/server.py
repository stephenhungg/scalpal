"""Loopback-only, serial stdlib HTTP boundary with no request/body logging."""

from __future__ import annotations

import json
from http.server import BaseHTTPRequestHandler, HTTPServer

from . import POSE_SCHEMA
from .pose import InvalidFrame, MAX_JPEG_BYTES, PoseEstimator, validate_frame_id


def handler_for(estimator: PoseEstimator):
    class PoseHandler(BaseHTTPRequestHandler):
        def setup(self):
            super().setup()
            self.connection.settimeout(5)

        def log_message(self, _format, *args):
            pass

        def respond(self, status: int, payload: dict):
            body = json.dumps(payload, allow_nan=False, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(body)
            self.close_connection = True

        def do_GET(self):
            if self.path == "/health":
                self.respond(200, {"schema": POSE_SCHEMA, "status": "ready"})
            else:
                self.respond(404, {"error": "not_found"})

        def do_POST(self):
            if self.path != "/pose":
                self.respond(404, {"error": "not_found"})
                return
            try:
                frame_ids = self.headers.get_all("X-Frame-Id", [])
                if len(frame_ids) != 1:
                    raise InvalidFrame("invalid_frame_id")
                frame_id = validate_frame_id(frame_ids[0])
                if self.headers.get("Content-Type", "").lower() != "image/jpeg":
                    raise InvalidFrame("unsupported_media_type", 415)
                if self.headers.get_all("Transfer-Encoding"):
                    raise InvalidFrame("unsupported_transfer_encoding")
                lengths = self.headers.get_all("Content-Length", [])
                if not lengths:
                    raise InvalidFrame("content_length_required", 411)
                if len(lengths) != 1 or not lengths[0].isascii() or not lengths[0].isdecimal():
                    raise InvalidFrame("invalid_content_length")
                normalized_length = lengths[0].lstrip("0") or "0"
                if len(normalized_length) > 7:
                    raise InvalidFrame("frame_too_large", 413)
                length = int(normalized_length)
                if length > MAX_JPEG_BYTES:
                    raise InvalidFrame("frame_too_large", 413)
                if length == 0:
                    raise InvalidFrame("invalid_jpeg")
                data = self.rfile.read(length)
                if len(data) != length:
                    raise InvalidFrame("incomplete_frame")
                self.respond(200, estimator.infer(data, frame_id))
            except InvalidFrame as error:
                self.respond(error.status, {"error": error.code})
            except TimeoutError:
                self.respond(408, {"error": "frame_timeout"})
            except Exception:
                # Do not echo/log frames, headers, tracebacks, or participant data.
                self.respond(500, {"error": "inference_failed"})

    return PoseHandler


def make_server(estimator: PoseEstimator, port: int = 8790) -> HTTPServer:
    # Serial processing prevents concurrent access to the MediaPipe task and bounds memory.
    return HTTPServer(("127.0.0.1", port), handler_for(estimator))
