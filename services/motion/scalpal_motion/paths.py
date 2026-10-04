from pathlib import Path

MOTION_ROOT = Path(__file__).resolve().parent.parent
REPO_ROOT = MOTION_ROOT.parent.parent
ROBOTS_DIR = REPO_ROOT / "assets" / "robots"
HAND_LANDMARKER_MODEL = MOTION_ROOT / "models" / "hand_landmarker.task"
