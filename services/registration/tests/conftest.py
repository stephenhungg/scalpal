import io

import pytest
from PIL import Image

from scalpal_registration.pose import PoseEstimator


@pytest.fixture
def blank_jpeg():
    """Generated uniform pixels, never participant footage."""
    buffer = io.BytesIO()
    Image.new("RGB", (320, 240), (128, 128, 128)).save(buffer, format="JPEG")
    return buffer.getvalue()


@pytest.fixture(scope="session")
def real_estimator():
    # This deliberately requires the pinned model; no silent skip or fake inference.
    estimator = PoseEstimator()
    yield estimator
    estimator.close()
