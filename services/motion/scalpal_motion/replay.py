"""Kinematic replay of a robot-motion record in MuJoCo, rendered offscreen to an mp4.

This sets joint positions directly and runs forward kinematics only: no physics,
contacts, or controller dynamics. Frames without a valid estimate hold the last
valid pose and are labeled as such on screen.
"""

from __future__ import annotations

import re
import shutil
import subprocess
from pathlib import Path

import cv2
import mujoco
import numpy as np

from .paths import ROBOTS_DIR

VIEWS = {  # (azimuth, elevation); the same presets face the left Shadow hand correctly
    "palm": (180.0, -10.0),  # palm toward the viewer, like a webcam/selfie clip
    "back": (0.0, -10.0),  # back of the hand, like a first-person headset view
}
VISUAL_GROUP = 1  # MuJoCo's URDF importer puts visual geoms in group 1 (contype 0)
HAND_CONNECTIONS = [
    (0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8), (5, 9), (9, 10), (10, 11),
    (11, 12), (9, 13), (13, 14), (14, 15), (15, 16), (13, 17), (0, 17), (17, 18), (18, 19), (19, 20),
]


def load_model(urdf_rel: str) -> mujoco.MjModel:
    urdf = ROBOTS_DIR / urdf_rel
    compiler = f'<mujoco><compiler discardvisual="false" meshdir="{urdf.parent}"/></mujoco>'
    xml = re.sub(r"(<robot[^>]*>)", r"\1" + compiler, urdf.read_text(), count=1)
    model = mujoco.MjModel.from_xml_string(xml)
    model.vis.global_.offwidth = max(model.vis.global_.offwidth, 1280)
    model.vis.global_.offheight = max(model.vis.global_.offheight, 960)
    return model


def _label(img: np.ndarray, lines: list[tuple[str, tuple[int, int, int]]]) -> None:
    for i, (text, color) in enumerate(lines):
        y = 28 + 26 * i
        (tw, th), base = cv2.getTextSize(text, cv2.FONT_HERSHEY_SIMPLEX, 0.65, 1)
        cv2.rectangle(img, (10, y - th - 5), (18 + tw, y + base + 2), (0, 0, 0), -1)
        cv2.putText(img, text, (14, y), cv2.FONT_HERSHEY_SIMPLEX, 0.65, color, 1, cv2.LINE_AA)


def _draw_landmarks(img: np.ndarray, image_pts: list[list[float]], box: tuple[int, int, int, int]) -> None:
    bx, by, bw, bh = box
    pts = [(bx + int(x * bw), by + int(y * bh)) for x, y, _ in image_pts]
    for a, b in HAND_CONNECTIONS:
        cv2.line(img, pts[a], pts[b], (255, 255, 255), 2, cv2.LINE_AA)
    for p in pts:
        cv2.circle(img, p, 4, (48, 48, 255), -1, cv2.LINE_AA)


class _VideoOut:
    """H.264/yuv420p mp4 via ffmpeg so browsers can play it; OpenCV mp4v fallback without ffmpeg."""

    def __init__(self, path: Path, fps: float, size: tuple[int, int]):
        self.ffmpeg = shutil.which("ffmpeg")
        w, h = size
        if self.ffmpeg:
            self.proc = subprocess.Popen(
                [self.ffmpeg, "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgr24",
                 "-s", f"{w}x{h}", "-r", f"{fps:.3f}", "-i", "-",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(path)],
                stdin=subprocess.PIPE,
            )
        else:
            print("warning: ffmpeg not found; writing mp4v, which browsers may not play")
            self.writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps, size)

    def write(self, frame: np.ndarray) -> None:
        if self.ffmpeg:
            self.proc.stdin.write(np.ascontiguousarray(frame).tobytes())
        else:
            self.writer.write(frame)

    def close(self) -> None:
        if self.ffmpeg:
            self.proc.stdin.close()
            if self.proc.wait() != 0:
                raise RuntimeError("ffmpeg failed to encode the replay")
        else:
            self.writer.release()


def _fit(img: np.ndarray, w: int, h: int) -> tuple[np.ndarray, tuple[int, int, int, int]]:
    """Letterbox img into w x h without distorting it. Returns the canvas and the content box."""
    scale = min(w / img.shape[1], h / img.shape[0])
    nw, nh = int(img.shape[1] * scale), int(img.shape[0] * scale)
    x, y = (w - nw) // 2, (h - nh) // 2
    canvas = np.zeros((h, w, 3), np.uint8)
    canvas[y : y + nh, x : x + nw] = cv2.resize(img, (nw, nh))
    return canvas, (x, y, nw, nh)


def render_replay(
    motion: dict,
    out_path: str | Path,
    video_path: str | Path | None = None,
    track: dict | None = None,
    size: tuple[int, int] = (640, 480),
    view: str | None = None,
    distance: float = 0.45,
) -> Path:
    """Write an mp4 of the replay. With video_path (and track), the source clip with
    detected landmarks is placed to the left for comparison. view defaults to "palm" for
    mirrored (selfie) tracks and "back" otherwise."""
    if view is None:
        view = "palm" if track and track.get("mirrored_input") else "back"
    azimuth, elevation = VIEWS[view]
    model = load_model(motion["robot"]["urdf"])
    data = mujoco.MjData(model)
    qadr = {mujoco.mj_id2name(model, mujoco.mjtObj.mjOBJ_JOINT, j): model.jnt_qposadr[j] for j in range(model.njnt)}
    order = [qadr[name] for name in motion["joint_names"]]  # match joints by name, never by index

    w, h = size
    renderer = mujoco.Renderer(model, h, w)
    opt = mujoco.MjvOption()
    opt.geomgroup[:] = 0
    opt.geomgroup[VISUAL_GROUP] = 1
    cam = mujoco.MjvCamera()
    mujoco.mj_forward(model, data)
    cam.lookat[:] = data.xpos[1:].mean(axis=0)
    cam.azimuth, cam.elevation, cam.distance = azimuth, elevation, distance

    frames = motion["frames"]
    times = [f["t_ms"] for f in frames]
    fps = (len(times) - 1) / ((times[-1] - times[0]) / 1000.0) if len(times) > 1 and times[-1] > times[0] else 30.0

    cap = cv2.VideoCapture(str(video_path)) if video_path else None
    track_frames = {f["frame"]: f for f in track["frames"]} if track else {}
    out_w = w * 2 if cap else w
    out_path = Path(out_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    writer = _VideoOut(out_path, fps, (out_w, h))

    last_q = None
    for f in frames:
        if f["valid"]:
            last_q = f["qpos"]
        if last_q is not None:
            data.qpos[order] = last_q
        mujoco.mj_forward(model, data)
        renderer.update_scene(data, cam, scene_option=opt)
        robot_img = cv2.cvtColor(renderer.render(), cv2.COLOR_RGB2BGR)

        t = f"t={f['t_ms'] / 1000:.2f}s  frame {f['frame']}"
        if f["valid"]:
            status = ("tracked", (120, 230, 120))
            limits = f.get("at_limit") or []
            extra = [(f"at limit: {', '.join(limits)}", (80, 200, 255))] if limits else []
        else:
            held = "holding last valid pose" if last_q is not None else "no pose yet"
            status = (f"NO TRACKING ({f.get('reason', 'invalid')}), {held}", (60, 60, 255))
            extra = []
        _label(robot_img, [("kinematic replay, not physics", (230, 230, 230)), (t, (230, 230, 230)), status, *extra])

        if cap:
            ok, src = cap.read()
            src, box = _fit(src, w, h) if ok else (np.zeros((h, w, 3), np.uint8), (0, 0, w, h))
            tf = track_frames.get(f["frame"])
            if tf and tf.get("valid"):
                _draw_landmarks(src, tf["image"], box)
            _label(src, [("source clip + MediaPipe landmarks", (230, 230, 230))])
            robot_img = np.hstack([src, robot_img])
        writer.write(robot_img)

    writer.close()
    if cap:
        cap.release()
    return out_path
