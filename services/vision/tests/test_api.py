import base64
import io

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from scalpal_vision.api import MAX_LABELS, create_app


class FakeDetector:
    model = "fake"
    repo = "fake/repo"
    device = "cpu"

    def __init__(self):
        self.calls = []

    def detect(self, image, labels, threshold):
        self.calls.append((image.size, labels, threshold))
        return [
            {
                "label": "scissors",
                "score": 0.9,
                "box": {"x": 0.1, "y": 0.2, "w": 0.3, "h": 0.4},
                "id": "lap_scissors",
                "kind": "instrument",
                "candidateIds": ["lap_scissors"],
            }
        ]


def encode(size=(64, 48), fmt="PNG") -> str:
    buffer = io.BytesIO()
    Image.new("RGB", size, (10, 20, 30)).save(buffer, format=fmt)
    return base64.b64encode(buffer.getvalue()).decode()


@pytest.fixture
def client():
    detector = FakeDetector()
    test_client = TestClient(create_app(detector, warmup_ms=1.0))
    test_client.detector = detector
    return test_client


def test_health_and_models(client):
    assert client.get("/health").json()["status"] == "ready"
    models = client.get("/models").json()
    assert models["active"]["name"] == "fake"
    assert {m["name"] for m in models["available"]} == {"owlv2", "grounding-dino"}
    scissors = next(item for item in models["defaultLabels"] if item["label"] == "scissors")
    assert scissors["id"] == "lap_scissors"


def test_detect_png_and_jpeg(client):
    for fmt in ("PNG", "JPEG"):
        response = client.post("/detect", json={"image": encode(fmt=fmt), "labels": ["scissors"], "threshold": 0.3})
        assert response.status_code == 200, response.text
        body = response.json()
        assert body["model"] == "fake"
        assert isinstance(body["ms"], float)
        assert body["detections"][0]["box"] == {"x": 0.1, "y": 0.2, "w": 0.3, "h": 0.4}
        assert body["image"] == {"width": 64, "height": 48}
    assert client.detector.calls[-1] == ((64, 48), ["scissors"], 0.3)


def test_data_url_prefix_and_default_labels(client):
    response = client.post("/detect", json={"image": "data:image/png;base64," + encode()})
    assert response.status_code == 200
    labels = client.detector.calls[-1][1]
    assert "scissors" in labels and "hand" in labels


def test_large_images_are_capped_at_1024(client):
    client.post("/detect", json={"image": encode(size=(2048, 1024), fmt="JPEG")})
    assert client.detector.calls[-1][0] == (1024, 512)


@pytest.mark.parametrize(
    "body,status,error",
    [
        ({}, 422, "invalid_request"),
        ({"image": ""}, 422, "invalid_request"),
        ({"image": "not base64!!"}, 400, "invalid_base64"),
        ({"image": base64.b64encode(b"hello").decode()}, 400, "invalid_image"),
        ({"image": "AAAA", "threshold": 1.5}, 422, "invalid_request"),
        ({"image": "AAAA", "labels": []}, 422, "invalid_request"),
        ({"image": "AAAA", "labels": ["  "]}, 422, "invalid_request"),
        ({"image": "AAAA", "labels": ["x"] * (MAX_LABELS + 1)}, 422, "invalid_request"),
        ({"image": "AAAA", "labels": ["x" * 65]}, 422, "invalid_request"),
    ],
)
def test_request_validation(client, body, status, error):
    response = client.post("/detect", json=body)
    assert response.status_code == status
    assert response.json()["error"] == error
    assert client.detector.calls == []


def test_unsupported_format_rejected(client):
    buffer = io.BytesIO()
    Image.new("RGB", (8, 8)).save(buffer, format="GIF")
    response = client.post("/detect", json={"image": base64.b64encode(buffer.getvalue()).decode()})
    assert response.status_code == 400
    assert response.json()["error"] == "unsupported_image_format"


def test_cors_localhost_only(client):
    ok = client.options(
        "/detect", headers={"Origin": "http://localhost:5173", "Access-Control-Request-Method": "POST"}
    )
    assert ok.headers.get("access-control-allow-origin") == "http://localhost:5173"
    blocked = client.options(
        "/detect", headers={"Origin": "https://evil.example", "Access-Control-Request-Method": "POST"}
    )
    assert "access-control-allow-origin" not in blocked.headers
