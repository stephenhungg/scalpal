"""Downloads MediaPipe's hand models (Apache-2.0): the ONNX pair Unity runs and Google's task file."""

from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parent / "models"
HF = "https://huggingface.co/unity/inference-engine-blaze-hand/resolve/main"
FILES = {
    "detector": (f"{HF}/models/hand_detector.onnx", "hand_detector.onnx"),
    "landmarker": (f"{HF}/models/hand_landmarks_detector.onnx", "hand_landmarks_detector.onnx"),
    "anchors": (f"{HF}/data/anchors.csv", "anchors.csv"),
    "task": ("https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task", "hand_landmarker.task"),
}


def ensure_models() -> dict[str, Path]:
    ROOT.mkdir(exist_ok=True)
    paths = {}
    for key, (url, name) in FILES.items():
        path = ROOT / name
        if not path.exists() or path.stat().st_size < 1000:
            urllib.request.urlretrieve(url, path)
        paths[key] = path
    return paths
