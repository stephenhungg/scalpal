"""Replay video: the learner's recorded stroke as a ghost path, and the arm + hand running the policy on the same
patient patch, drawing its line. H.264 / yuv420p / faststart MP4 (Android MediaPlayer and Unity VideoPlayer play it),
1280x720, 30 fps, at most 15 s. The only text is a small "Robot · Mark incision" label."""

from __future__ import annotations

from pathlib import Path

import cv2
import mujoco
import numpy as np

from ..replay import _VideoOut
from .learn import Rollout
from .scene import MarkRobot, onto_skin, skin_normal_out

W, H, FPS = 1280, 720, 30
MAX_S = 15.0
GHOST_RGBA = (0.45, 0.95, 0.75, 1.0)  # opaque: transparent overlays render black from some views
INK_RGBA = (0.30, 0.10, 0.55, 1.0)


def _segment(scn: mujoco.MjvScene, a: np.ndarray, b: np.ndarray, width: float, rgba) -> None:
    if scn.ngeom >= scn.maxgeom or np.linalg.norm(b - a) < 1e-5:
        return
    g = scn.geoms[scn.ngeom]
    mujoco.mjv_initGeom(g, mujoco.mjtGeom.mjGEOM_CAPSULE, np.zeros(3), np.zeros(3), np.eye(3).reshape(-1), np.array(rgba, np.float32))
    mujoco.mjv_connector(g, mujoco.mjtGeom.mjGEOM_CAPSULE, width, a, b)
    g.emission = 0.5
    g.specular = 0.0
    scn.ngeom += 1


def _polyline(scn, robot: MarkRobot, pts, width, rgba, lift=0.0008, step=1) -> None:
    w = [robot.patient.to_world(onto_skin(p) + skin_normal_out(p) * lift) for p in pts[::step]]
    for a, b in zip(w, w[1:]):
        _segment(scn, a, b, width, rgba)


def _label(img: np.ndarray) -> None:
    x, y, s = 28, 50, 0.75
    font, col, th = cv2.FONT_HERSHEY_SIMPLEX, (245, 245, 245), 2
    (tw, _), _ = cv2.getTextSize("Robot", font, s, th)
    (mw, _), _ = cv2.getTextSize("Mark incision", font, s, th)
    cv2.rectangle(img, (x - 14, y - 30), (x + tw + mw + 52, y + 14), (30, 30, 30), -1)
    cv2.putText(img, "Robot", (x, y), font, s, col, th, cv2.LINE_AA)
    cv2.circle(img, (x + tw + 14, y - 8), 4, col, -1, cv2.LINE_AA)
    cv2.putText(img, "Mark incision", (x + tw + 28, y), font, s, col, th, cv2.LINE_AA)


def render_rollout(robot: MarkRobot, ro: Rollout, ghost_strokes: list[np.ndarray], out: Path) -> dict:
    """Re-pose the robot along the rollout (kinematic) and draw ghost + ink overlays."""
    out.parent.mkdir(parents=True, exist_ok=True)
    robot.reset(ro.patient)
    ren = mujoco.Renderer(robot.m, H, W)
    cam = mujoco.MjvCamera()
    centre = robot.patient.to_world(onto_skin(ro.landmarks.mcburney))
    cam.lookat[:] = centre + np.array([-0.04, 0.0, 0.07])
    cam.distance, cam.azimuth, cam.elevation = 0.5, 215.0, -42.0
    opt = mujoco.MjvOption()
    ticks = len(ro.arm_q)
    hold = int(FPS * 1.0)
    n_frames = min(int(MAX_S * FPS), int((ticks - 1) * FPS / 20) + 1 + hold)
    video = _VideoOut(out, FPS, (W, H))
    drawn = [i for i, d in enumerate(ro.drawing) if d]
    try:
        for f in range(n_frames):
            s = min(f * 20 / FPS, ticks - 1)
            i0 = int(np.floor(s))
            i1 = min(i0 + 1, ticks - 1)
            a = s - i0
            robot.d.qpos[robot.arm_q] = (1 - a) * ro.arm_q[i0] + a * ro.arm_q[i1]
            mujoco.mj_kinematics(robot.m, robot.d)
            ren.update_scene(robot.d, cam, opt)
            scn = ren.scene
            for g in ghost_strokes:
                _polyline(scn, robot, g, 0.0035, GHOST_RGBA, lift=0.0006)
            # Ink: every contiguous drawn run up to the current tick.
            run = []
            for i in drawn:
                if i > s:
                    break
                if run and i != run[-1] + 1:
                    _polyline(scn, robot, ro.tips[run], 0.0022, INK_RGBA, lift=0.0016)
                    run = []
                run.append(i)
            if len(run) > 1:
                _polyline(scn, robot, ro.tips[run], 0.0022, INK_RGBA, lift=0.0016)
            img = ren.render()[:, :, ::-1].copy()
            _label(img)
            video.write(img)
    finally:
        video.close()
        ren.close()
    return {"path": str(out), "frames": n_frames, "seconds": round(n_frames / FPS, 2), "size": [W, H], "fps": FPS}
