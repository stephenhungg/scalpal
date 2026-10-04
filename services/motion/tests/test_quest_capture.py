"""The headset recorder's clip format must stay decodable by this worker.

`fixtures/quest-capture-mjpeg.avi` is written by the Quest recorder's own
MjpegAviWriter + JPEG encoder (Unity `CaptureValidation` fails if the fixture
drifts from what the headset code produces). Its frames are a synthetic test
pattern, not a hand.
"""

from pathlib import Path

import cv2
import pytest

from scalpal_motion.paths import HAND_LANDMARKER_MODEL

CLIP = Path(__file__).parent / "fixtures" / "quest-capture-mjpeg.avi"


def test_headset_mjpeg_avi_decodes_with_monotonic_presentation_times():
    cap = cv2.VideoCapture(str(CLIP))
    assert cap.isOpened(), "worker decoder cannot open the headset container"
    assert cap.get(cv2.CAP_PROP_FPS) == pytest.approx(15.0)
    assert (int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)), int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))) == (64, 48)
    times, frames = [], []
    while True:
        ok, bgr = cap.read()
        if not ok:
            break
        times.append(cap.get(cv2.CAP_PROP_POS_MSEC))
        frames.append(bgr)
    cap.release()
    assert len(frames) == 4
    assert all(b > a for a, b in zip(times, times[1:])), times
    # Unity texture row 0 is the image bottom (same as Texture2D.EncodeToJPG used by
    # body registration). The pattern's green channel is 5*row, so an upright decode
    # is bright at the top and dark at the bottom. A flipped writer would invert this.
    for bgr in frames:
        assert bgr[0, :, 1].mean() > 200 and bgr[-1, :, 1].mean() < 30
        # Red channel ramps left to right (4*x, no byte wrap below x=40): not mirrored.
        assert int(bgr[24, 30, 2]) > int(bgr[24, 10, 2]) + 40


@pytest.mark.skipif(not HAND_LANDMARKER_MODEL.exists(), reason="hand model not downloaded (run `uv run scalpal-motion synthetic` once)")
def test_worker_perception_reads_every_headset_frame_without_inventing_hands():
    from scalpal_motion.perception import track_hand

    track = track_hand(CLIP)
    assert track["source"]["frame_count"] == 4
    assert track["source"]["nonmonotonic_pts"] == 0
    assert [f["valid"] for f in track["frames"]] == [False] * 4
    assert {f["reason"] for f in track["frames"]} == {"no_hand"}
