import io
from types import SimpleNamespace

import pytest
from PIL import Image

from scalpal_registration.model import DEFAULT_MODEL_PATH, MODEL_SHA256, verify_model
from scalpal_registration.pose import InvalidFrame, MAX_JPEG_BYTES, decode_jpeg, format_result, validate_frame_id


def synthetic_pose():
    """Formatting fixture only: these points are not model measurements or body evidence."""
    return [SimpleNamespace(x=0.3, y=0.6, z=-0.1, visibility=0.9, presence=0.8) for _ in range(33)]


def test_actual_blank_image_fails_closed(real_estimator, blank_jpeg):
    result = real_estimator.infer(blank_jpeg, "blank-real-inference")
    assert result["schema"] == "scalpal.body_pose.v1"
    assert result["frameId"] == "blank-real-inference"
    assert (result["imageWidth"], result["imageHeight"]) == (320, 240)
    assert result["coordinateConvention"] == "normalized_image_top_left"
    assert result["model"]["sha256"] == MODEL_SHA256
    assert result["model"]["version"] == "1"
    assert result["model"]["mediapipeVersion"] == "0.10.35"
    assert result["inferenceMs"] > 0
    assert result["personCount"] == 0
    assert result["valid"] is False
    assert result["reason"] == "no_person"
    assert result["landmarks"] == []


def test_synthetic_multiple_people_fails_closed():
    output = format_result(SimpleNamespace(pose_landmarks=[synthetic_pose(), synthetic_pose()]), "synthetic-two", 320, 240, 2)
    assert output["personCount"] == 2
    assert output["valid"] is False
    assert output["reason"] == "ambiguous_people"
    assert output["landmarks"] == []


def test_synthetic_single_pose_keeps_index_confidence_and_unclamped_coordinates():
    points = synthetic_pose()
    points[12].x = 1.1
    output = format_result(SimpleNamespace(pose_landmarks=[points]), "synthetic-one", 320, 240, 2)
    assert output["valid"] is True
    assert output["reason"] == ""
    assert [point["index"] for point in output["landmarks"]] == list(range(33))
    assert output["landmarks"][12] == {"index": 12, "x": 1.1, "y": 0.6, "z": -0.1, "visibility": 0.9, "presence": 0.8}
    assert "worldLandmarks" not in output


@pytest.mark.parametrize("corruption", ["count", "nan", "missing", "confidence"])
def test_synthetic_bad_landmarks_fails_closed(corruption):
    points = synthetic_pose()
    if corruption == "count":
        points.pop()
    elif corruption == "nan":
        points[3].z = float("nan")
    elif corruption == "missing":
        points[3].presence = None
    else:
        points[3].visibility = 1.1
    output = format_result(SimpleNamespace(pose_landmarks=[points]), "synthetic-bad", 320, 240, 2)
    assert output["valid"] is False
    assert output["reason"] == "invalid_landmarks"
    assert output["landmarks"] == []


@pytest.mark.parametrize("data", [b"", b"not a jpeg", b"\xff\xd8truncated"])
def test_malformed_jpeg_rejected(data):
    with pytest.raises(InvalidFrame, match="invalid_jpeg"):
        decode_jpeg(data)


def test_other_image_format_rejected():
    buffer = io.BytesIO()
    Image.new("RGB", (8, 8)).save(buffer, format="PNG")
    with pytest.raises(InvalidFrame, match="invalid_jpeg"):
        decode_jpeg(buffer.getvalue())


def test_encoded_size_limit_before_decode():
    with pytest.raises(InvalidFrame, match="frame_too_large") as error:
        decode_jpeg(b"x" * (MAX_JPEG_BYTES + 1))
    assert error.value.status == 413


def test_compressed_oversized_dimensions_rejected():
    buffer = io.BytesIO()
    Image.new("L", (4001, 4000)).save(buffer, format="JPEG")
    assert len(buffer.getvalue()) < MAX_JPEG_BYTES
    with pytest.raises(InvalidFrame, match="decoded_frame_too_large") as error:
        decode_jpeg(buffer.getvalue())
    assert error.value.status == 413


@pytest.mark.parametrize("frame_id", [None, "", "x" * 129, "a\nb", "a\x00b", "café"])
def test_malformed_frame_id(frame_id):
    with pytest.raises(InvalidFrame, match="invalid_frame_id"):
        validate_frame_id(frame_id)


def test_pinned_model_and_tampering(tmp_path):
    assert verify_model(DEFAULT_MODEL_PATH) == DEFAULT_MODEL_PATH
    altered = tmp_path / "altered.task"
    altered.write_bytes(b"not the pinned model")
    with pytest.raises(ValueError, match="SHA-256 mismatch"):
        verify_model(altered)
