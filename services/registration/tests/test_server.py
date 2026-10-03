import http.client
import json
import threading

import pytest

from scalpal_registration.pose import MAX_JPEG_BYTES
from scalpal_registration.server import make_server


@pytest.fixture
def pose_server(real_estimator):
    server = make_server(real_estimator, port=0)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    yield server
    server.shutdown()
    thread.join(timeout=5)
    server.server_close()


def request(server, method="POST", path="/pose", body=b"bad", headers=None):
    connection = http.client.HTTPConnection("127.0.0.1", server.server_port, timeout=10)
    connection.putrequest(method, path)
    for key, value in headers if headers is not None else [
        ("X-Frame-Id", "request-frame"), ("Content-Type", "image/jpeg"), ("Content-Length", str(len(body)))
    ]:
        connection.putheader(key, value)
    connection.endheaders(body)
    response = connection.getresponse()
    status, response_headers, output = response.status, dict(response.getheaders()), json.loads(response.read())
    connection.close()
    return status, response_headers, output


def test_actual_http_blank_pose_and_no_cache(pose_server, blank_jpeg, capsys):
    status, headers, output = request(pose_server, body=blank_jpeg)
    assert status == 200
    assert headers["Cache-Control"] == "no-store"
    assert headers["Connection"] == "close"
    assert output["frameId"] == "request-frame"
    assert output["valid"] is False and output["landmarks"] == []
    assert output["personCount"] == 0 and output["reason"] == "no_person"
    assert capsys.readouterr().out == ""


@pytest.mark.parametrize(("headers", "status", "error"), [
    ([("Content-Type", "image/jpeg"), ("Content-Length", "3")], 400, "invalid_frame_id"),
    ([("X-Frame-Id", "one"), ("X-Frame-Id", "two"), ("Content-Type", "image/jpeg"), ("Content-Length", "3")], 400, "invalid_frame_id"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/png"), ("Content-Length", "3")], 415, "unsupported_media_type"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg")], 411, "content_length_required"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg"), ("Transfer-Encoding", "chunked")], 400, "unsupported_transfer_encoding"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg"), ("Content-Length", "-1")], 400, "invalid_content_length"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg"), ("Content-Length", "3"), ("Content-Length", "4")], 400, "invalid_content_length"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg"), ("Content-Length", str(MAX_JPEG_BYTES + 1))], 413, "frame_too_large"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg"), ("Content-Length", "9" * 4301)], 413, "frame_too_large"),
    ([("X-Frame-Id", "one"), ("Content-Type", "image/jpeg"), ("Content-Length", "0" * 4301)], 400, "invalid_jpeg"),
])
def test_http_rejects_bad_envelopes_before_inference(pose_server, headers, status, error):
    actual_status, _, output = request(pose_server, body=b"bad", headers=headers)
    assert actual_status == status
    assert output == {"error": error}


def test_http_rejects_malformed_image(pose_server):
    status, _, output = request(pose_server)
    assert status == 400 and output == {"error": "invalid_jpeg"}


def test_health_and_unknown_routes(pose_server):
    assert pose_server.server_address[0] == "127.0.0.1"
    assert request(pose_server, method="GET", path="/health", body=b"", headers=[])[2] == {"schema": "scalpal.body_pose.v1", "status": "ready"}
    assert request(pose_server, path="/other")[0] == 404
