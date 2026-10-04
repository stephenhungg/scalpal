"""Marking demos in task space: the marker tip path in the patient frame plus the trigger, at 20 Hz.

Two sources, always labelled:
- "headset": a learner's ControllerMotionCapture frames (scalpal.controller_motion.v1, patient space) posted to the
  coach for step mark_incision. Retargeting: the hand holding the skin marker (else the right controller) is the
  wrist; "pen down" is trigger > 0.5. The capture has no marker-tip offset, so one constant offset is fitted that
  puts the pen-down samples on the skin (vertical in the patient frame), and pen-down samples press PRESS_M into
  the skin, the way the marker is pushed onto it. Gaps in tracking are bridged linearly only up to MAX_GAP_S;
  longer gaps end the demo there rather than inventing motion.
- "synthetic": a scripted, perturbed human-like stroke (approach, touch down, draw ~6 cm along the McBurney line with
  tremor and small aim errors, lift). Stand-in data so the pipeline works with zero headset demos.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field

import numpy as np

from .grader import AUTHORED_RIGHT_ASIS, WOUND_ROTATION, Landmarks, check, measure_marks, path_error_mm, split_strokes
from .scene import onto_skin, skin_distance, skin_y

HZ = 20
DT = 1.0 / HZ
PRESS_M = 0.004
MAX_GAP_S = 0.3
STAND_IN = "stand-in"
START_OFFSET = np.array([-0.06, 0.13, -0.16])  # robot/marker start, relative to the umbilicus (patient frame)


@dataclass
class MarkDemo:
    tip: np.ndarray  # (T, 3) commanded marker tip, patient frame
    trigger: np.ndarray  # (T,) 0/1
    landmarks: Landmarks
    source: str  # "headset" | "synthetic" | "stand-in" (synthetic stroke sent through the headset route)
    demo_id: str = field(default_factory=lambda: f"syn-{uuid.uuid4().hex[:10]}")
    meta: dict = field(default_factory=dict)

    @property
    def synthetic(self) -> bool:
        return self.source != "headset"

    def stroke_points(self) -> list[np.ndarray]:
        drawn = [bool(t) and skin_distance(p) <= 0.002 for p, t in zip(self.tip, self.trigger)]
        return [np.array([onto_skin(q) for q in s]) for s in split_strokes(self.tip, drawn)]

    def grade(self) -> dict:
        strokes = self.stroke_points()
        facts = measure_marks(strokes, self.landmarks)
        ok, failed = check(facts)
        pts = np.concatenate(strokes) if strokes else np.zeros((0, 3))
        return {"success": ok, "failed": failed, "facts": {k: round(float(v), 2) for k, v in facts.items()},
                "pathErrorMm": None if not len(pts) else round(path_error_mm(pts, self.landmarks), 2), "strokes": len(strokes)}

    def to_dict(self) -> dict:
        return {"schema": "scalpal.robot_mark_demo.v1", "demoId": self.demo_id, "source": self.source, "hz": HZ,
                "landmarks": self.landmarks.to_dict(), "tip": np.round(self.tip, 5).tolist(),
                "trigger": [int(t) for t in self.trigger], "meta": self.meta}

    @classmethod
    def from_dict(cls, d: dict) -> MarkDemo:
        lm = Landmarks(np.array(d["landmarks"]["rightAsis"], float), np.array(d["landmarks"]["umbilicus"], float))
        return cls(np.array(d["tip"], float), np.array(d["trigger"], float), lm, d["source"], d["demoId"], d.get("meta", {}))


def sample_landmarks(rng: np.random.Generator, spread: float = 0.03) -> Landmarks:
    """Patient-to-patient landmark variation: right ASIS within +/-spread (x, z) and +/-5 mm (y)."""
    j = rng.uniform([-spread, -0.005, -spread], [spread, 0.005, spread])
    return Landmarks(AUTHORED_RIGHT_ASIS + j, np.zeros(3))


def _minjerk(a: np.ndarray, b: np.ndarray, n: int) -> np.ndarray:
    s = np.linspace(0, 1, max(n, 2))[1:]
    s = 10 * s**3 - 15 * s**4 + 6 * s**5
    return a + np.outer(s, b - a)


def _press(p: np.ndarray) -> np.ndarray:
    q = np.array(p, float)
    q[..., 1] = skin_y(q[..., 0]) - PRESS_M
    return q


def synthetic_demo(rng: np.random.Generator, landmarks: Landmarks | None = None) -> MarkDemo:
    """A perturbed, human-like marking stroke (SYNTHETIC). Mostly on target; aim errors are human-sized."""
    lm = landmarks or sample_landmarks(rng)
    c, axis, side = lm.mcburney, WOUND_ROTATION[:, 0], WOUND_ROTATION[:, 1]
    off = rng.normal(0, 0.007, 2)
    theta = np.deg2rad(rng.normal(0, 9))
    length = rng.uniform(0.046, 0.08)
    direction = np.cos(theta) * axis + np.sin(theta) * side
    centre = c + off[0] * axis + off[1] * side
    p0, p1 = centre - direction * length / 2, centre + direction * length / 2
    speed = rng.uniform(0.85, 1.15)
    n = lambda s: max(2, int(round(s * speed * HZ)))
    start = START_OFFSET + rng.uniform(-0.015, 0.015, 3)
    hover = onto_skin(p0) + np.array([0, 0.02, 0])
    seg_a = np.r_[[start], _minjerk(start, hover, n(1.6))]
    seg_d = _minjerk(hover, _press(p0), n(0.6))
    nd = n(2.2)
    s = np.linspace(0, 1, nd)
    s = 10 * s**3 - 15 * s**4 + 6 * s**5
    line = p0 + np.outer(s, p1 - p0)
    tremor = 0.0006 * np.sin(2 * np.pi * rng.uniform(1.5, 3.0) * np.arange(nd) * DT + rng.uniform(0, 6)) + rng.normal(0, 0.0002, nd)
    line = _press(line + np.outer(tremor, side))
    up = onto_skin(p1) + np.array([0, 0.04, 0])
    seg_l = _minjerk(line[-1], up, n(0.6))
    seg_h = np.repeat(up[None], n(0.4), 0)
    tip = np.r_[seg_a, seg_d, line, seg_l, seg_h]
    trig = np.r_[np.zeros(len(seg_a)), (np.arange(len(seg_d)) >= len(seg_d) - 3).astype(float), np.ones(nd), np.zeros(len(seg_l) + len(seg_h))]
    return MarkDemo(tip, trig, lm, "synthetic", meta={"generator": "synthetic_demo v1", "aimOffsetMm": np.round(off * 1000, 1).tolist(),
                                                      "aimAngleDeg": round(float(np.degrees(theta)), 1), "lengthMm": round(length * 1000, 1)})


def synthetic_set(n: int, seed: int = 0) -> list[MarkDemo]:
    """N synthetic demos that pass the grader (like the physics filter on generated data, reported as such)."""
    rng = np.random.default_rng(seed)
    out = []
    while len(out) < n:
        d = synthetic_demo(rng)
        if d.grade()["success"]:
            d.demo_id = f"syn-{seed}-{len(out):03d}"
            out.append(d)
    return out


class DemoRejected(ValueError):
    pass


def from_capture(doc: dict, landmarks: Landmarks | None = None) -> MarkDemo:
    """Coach robot demo (scalpal.robot_demo.v1 with ControllerMotionCapture frames) -> task-space demo."""
    frames = [f for f in doc.get("frames") or [] if f.get("space") == "patient"]
    if len(frames) < 2:
        raise DemoRejected("fewer than two patient-space frames")
    frames.sort(key=lambda f: f["unityTime"])
    held = {}
    for f in frames:
        for c in f.get("controllers") or []:
            if c.get("heldInstrument") == "skin_marker":
                held[c["hand"]] = held.get(c["hand"], 0) + 1
    hand = max(held, key=held.get) if held else "right"
    t, pos, trig = [], [], []
    for f in frames:
        c = next((c for c in f.get("controllers") or [] if c.get("hand") == hand and c.get("tracked")), None)
        if c is None:
            continue
        t.append(float(f["unityTime"]))
        pos.append(np.array(c["position"], float))
        pen = float(c.get("trigger", 0)) > 0.5 and (not held or c.get("heldInstrument") == "skin_marker")
        trig.append(1.0 if pen else 0.0)
    if len(t) < 2:
        raise DemoRejected(f"the {hand} controller was never tracked")
    t, pos, trig = np.array(t), np.array(pos), np.array(trig)
    gaps = np.flatnonzero(np.diff(t) > MAX_GAP_S)
    cut = len(t) if not len(gaps) else int(gaps[0]) + 1
    t, pos, trig = t[:cut], pos[:cut], trig[:cut]
    grid = np.arange(t[0], t[-1] + 1e-9, DT)
    if len(grid) < 2:
        raise DemoRejected("demo shorter than one control tick")
    p = np.stack([np.interp(grid, t, pos[:, k]) for k in range(3)], 1)
    pen = np.interp(grid, t, trig) > 0.5
    if pen.any():
        offset = float(np.mean([skin_y(q[0]) - q[1] for q in p[pen]]))
    else:
        offset = 0.0
    tip = p + np.array([0.0, offset, 0.0])
    tip[pen] = _press(tip[pen])
    lm = landmarks or Landmarks.authored()
    meta = {"hand": hand, "markerHeldFrames": int(held.get(hand, 0)), "frames": len(frames), "usedFrames": int(cut),
            "truncatedAtGap": bool(len(gaps)), "durationS": round(float(t[-1] - t[0]), 2), "tipOffsetM": round(offset, 4),
            "sessionId": doc.get("sessionId"), "stepId": doc.get("stepId"), "landmarks": "authored" if landmarks is None else "registered"}
    # `robot-send-demo` (the stand-in headset) stamps its frames sessionId "stand-in": those stay labelled synthetic.
    source = "stand-in" if all(f.get("sessionId") == STAND_IN for f in frames) else "headset"
    return MarkDemo(tip, pen.astype(float), lm, source, doc.get("demoId") or f"demo-{uuid.uuid4().hex[:10]}", meta)


def to_capture_frames(demo: MarkDemo, controller_above_tip: float = 0.11) -> list[dict]:
    """Stand-in headset: ControllerMotionCapture frames for a task-space demo (the right hand holds the marker)."""
    frames = []
    for i, (p, tr) in enumerate(zip(demo.tip, demo.trigger)):
        ctrl = (p + np.array([0, controller_above_tip, 0])).round(5).tolist()
        frames.append({
            "schema": "scalpal.controller_motion.v1", "sessionId": STAND_IN, "frameIndex": i, "unityTime": round(i * DT, 4),
            "space": "patient", "headTracked": True, "headPosition": [-0.35, 0.45, -0.05], "headRotation": [0.0, 0.0, 0.0, 1.0],
            "controllers": [
                {"hand": "left", "tracked": True, "position": [0.12, 0.2, -0.1], "rotation": [0, 0, 0, 1], "grip": 0.0, "trigger": 0.0, "heldInstrument": ""},
                {"hand": "right", "tracked": True, "position": ctrl, "rotation": [0, 0, 0, 1], "grip": 0.8, "trigger": float(tr), "heldInstrument": "skin_marker"},
            ],
        })
    return frames
