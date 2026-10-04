"""Provenance-labelled simulated policy footage; rendering never changes the rollout.

One-second establishing shot, source reveal, continuous contact/lift, two-second
result hold. All strokes keep their patient-frame coordinates (only a sub-mm
normal lift prevents z fighting). H.264/yuv420p/faststart, 1280x720.
"""
from __future__ import annotations

from functools import lru_cache
from pathlib import Path

import matplotlib
import mujoco
import numpy as np
from PIL import Image, ImageDraw, ImageFont

from ..replay import _VideoOut
from .learn import Rollout
from .scene import MarkRobot, onto_skin, skin_normal_out

W, H, FPS = 1280, 720, 30
MAX_S = 15.0
GHOST_RGBA = (0.45, 0.88, 0.75, 1.0)
INK_RGBA = (0.34, 0.16, 0.55, 1.0)
MINT, PURPLE = '#73e0c0', '#b09aff'


def _segment(scn, a, b, width, rgba):
    if scn.ngeom >= scn.maxgeom or np.linalg.norm(b - a) < 1e-5:
        return
    g = scn.geoms[scn.ngeom]
    mujoco.mjv_initGeom(g, mujoco.mjtGeom.mjGEOM_CAPSULE, np.zeros(3), np.zeros(3), np.eye(3).reshape(-1), np.array(rgba, np.float32))
    mujoco.mjv_connector(g, mujoco.mjtGeom.mjGEOM_CAPSULE, width, a, b)
    g.emission, g.specular = 0.05, 0.0
    scn.ngeom += 1


def _polyline(scn, robot, pts, width, rgba, lift=0.0006, dashed=False):
    w = [robot.patient.to_world(onto_skin(p) + skin_normal_out(p) * lift) for p in pts]
    # Dash by arc length, not sample count: short/uneven recordings remain visible.
    distance = 0.0
    for a, b in zip(w, w[1:]):
        length = float(np.linalg.norm(b - a))
        if length < 1e-8:
            continue
        used = 0.0
        while used < length - 1e-9:
            phase = (distance + used) % 0.006
            take = min(length - used, (0.0036 if phase < 0.0036 else 0.006) - phase)
            if not dashed or phase < 0.0036:
                _segment(scn, a + (b-a)*used/length, a + (b-a)*(used+take)/length, width, rgba)
            used += max(take, 1e-8)
        distance += length


@lru_cache(maxsize=12)
def _font(size, bold=False):
    name = 'DejaVuSans-Bold.ttf' if bold else 'DejaVuSans.ttf'
    return ImageFont.truetype(str(Path(matplotlib.get_data_path()) / 'fonts/ttf' / name), size)


def source_caption(source: str) -> str:
    return {'synthetic-held-out': 'Held-out synthetic stroke · 0 headset demonstrations',
            'synthetic': 'Synthetic demonstration · scripted stroke',
            'stand-in': 'Synthetic stand-in · not a headset recording',
            'headset': 'Headset controller demonstration · retargeted marker path'}.get(source, 'Source provenance unverified')


def grade_rows(ro):
    """Use shared milestone facts, never the separate path-error diagnostic."""
    return [(label, ro.facts.get(key), unit, limit,
             bool(ro.success and key in ro.facts and key not in ro.failed))
            for key, label, unit, limit in [
                ('markErrorMm', 'Position error', 'mm', '≤20 mm'),
                ('markLengthMm', 'Line length', 'mm', '50–80 mm'),
                ('markAngleDegrees', 'Angle', '°', '≤25°')]]


def _label(img, ro, source, phase, result=False):
    im = Image.fromarray(img[:, :, ::-1])
    d = ImageDraw.Draw(im)
    d.rectangle((0, 0, W, 112), fill='#111b27')
    d.rectangle((32, 25, 37, 84), fill=MINT)
    d.text((53, 19), 'Learned policy · MuJoCo simulation', font=_font(30, True), fill='#f3f6fb')
    d.text((54, 66), source_caption(source), font=_font(20), fill='#bfccd9')
    d.text((W-155, 30), 'SCALPAL', font=_font(20, True), fill=MINT)
    # A permanent legend makes source vs output readable even in a short cut.
    d.rounded_rectangle((24, 120, 535, 160), radius=8, fill='#111b27')
    for x, color, text in [(35, MINT, 'Source demonstration'), (330, PURPLE, 'Robot output')]:
        d.line((x, 139, x+30, 139), fill=color, width=4)
        d.text((x+40, 126), text, font=_font(19), fill='#f0f5fa')
    if result:
        d.rectangle((24, 501, W-24, 655), fill='#111b27')
        d.text((44, 514), 'Same milestone checks as the learner', font=_font(23, True), fill='#f3f6fb')
        d.text((W-230, 516), 'PASS' if ro.success else 'NOT PASSED', font=_font(21, True), fill=MINT if ro.success else '#ffb195')
        for j, (name, value, unit, limit, passed) in enumerate(grade_rows(ro)):
            x = 44 + j*408
            d.text((x, 554), name, font=_font(18), fill='#bfccd9')
            val = 'Unavailable' if value is None else f'{value:.1f}{unit if unit == "°" else " " + unit}'
            d.text((x, 580), val, font=_font(30, True), fill='#f3f6fb')
            d.text((x, 623), f'{limit}  ·  {"PASS" if passed else "NOT PASSED"}', font=_font(17), fill=MINT if passed else '#ffb195')
    d.rectangle((0, 674, W, H), fill='#111b27')
    d.text((32, 687), phase, font=_font(17, True), fill=MINT)
    d.text((690, 687), 'Simulated policy rollout · kinematic, no contact dynamics', font=_font(16), fill='#bfccd9')
    img[:] = np.asarray(im)[:, :, ::-1]


def frame_plan(ro):
    """Only retime presentation. Preserve every contact span; never cut a failed run."""
    ticks = len(ro.arm_q)
    drawn = np.flatnonzero(ro.drawing)
    first = int(drawn[0]) if len(drawn) else 0
    start = max(0, first-12) if ro.success else 0  # trim only successful idle approach
    end = ticks-1  # preserve the complete lift/tail, especially on failed runs
    # Continuous retiming keeps long rollouts within a ten-second presentation.
    action_s = min(6.3, max(4.8, (end-start)/20))
    action = np.linspace(start, end, round(action_s*FPS))
    return np.r_[np.full(FPS, start), np.full(round(.7*FPS), start), action,
                 np.full(2*FPS, end)], 1.7, (round(1.7*FPS)+len(action))/FPS


def _camera(centre, establishing=False, azimuth=215):
    cam = mujoco.MjvCamera()
    if establishing:
        cam.lookat[:] = [0.52, 0.0, 0.22]
        cam.distance, cam.azimuth, cam.elevation = 1.6, 135, -27
    else:
        cam.lookat[:] = centre + [0, 0, 0.025]
        cam.distance, cam.azimuth, cam.elevation = 0.42, azimuth, -60
    return cam


def render_rollout(robot: MarkRobot, ro: Rollout, ghost_strokes: list[np.ndarray], out: Path,
                   *, source: str = 'unknown') -> dict:
    out.parent.mkdir(parents=True, exist_ok=True)
    robot.reset(ro.patient)
    ren = mujoco.Renderer(robot.m, H, W)
    centre = robot.patient.to_world(onto_skin(ro.landmarks.mcburney))
    opt = mujoco.MjvOption()
    opt.sitegroup[:] = 0  # beauty render only: retain sites/coordinates for IK and grading
    plan, reveal_end, result_start = frame_plan(ro)
    video = _VideoOut(out, FPS, (W, H))
    drawn = np.flatnonzero(ro.drawing)
    try:
        for f, tick in enumerate(plan):
            t = f/FPS
            i0, i1 = int(tick), min(int(tick)+1, len(ro.arm_q)-1)
            a = tick-i0
            robot.d.qpos[robot.arm_q] = (1-a)*ro.arm_q[i0] + a*ro.arm_q[i1]
            mujoco.mj_kinematics(robot.m, robot.d)
            mujoco.mj_camlight(robot.m, robot.d)
            ren.update_scene(robot.d, _camera(centre, t < 1), opt)
            fraction = np.clip((t-1)/.6, 0, 1)
            for ghost in ghost_strokes:
                count = round(len(ghost)*fraction)
                _polyline(ren.scene, robot, ghost[:count], .0008, GHOST_RGBA, dashed=True)
            if t >= reveal_end:
                run = []
                for i in drawn:
                    if i > tick:
                        break
                    if run and i != run[-1]+1:
                        _polyline(ren.scene, robot, ro.tips[run], .001, INK_RGBA)
                        run = []
                    run.append(i)
                if len(run) > 1:
                    _polyline(ren.scene, robot, ro.tips[run], .001, INK_RGBA)
            img = ren.render()[:, :, ::-1].copy()
            phase = ('01  /  SOURCE → SIMULATION' if t < reveal_end else
                     '03  /  SHARED GRADING' if t >= result_start else '02  /  POLICY EXECUTION')
            _label(img, ro, source, phase, t >= result_start)
            video.write(img)
    finally:
        video.close()
        ren.close()
    return {'path': str(out), 'frames': len(plan), 'seconds': round(len(plan)/FPS, 2),
            'size': [W, H], 'fps': FPS, 'source': source, 'sourceCaption': source_caption(source),
            'presentation': 'establish / source reveal / continuous action / 2s result hold'}
