"""Python port of how the Quest grades `mark_incision`, so the robot is graded exactly like the learner.

Two halves, each ported from the code that grades the human:
- Measurement (apps/quest/.../Surgery/Runtime/OpenSurgeryMeasurements.cs `OpenSurgeryStroke` and
  OpenBodyInteraction.FlushStroke for verb "mark"): the stroke is measured in the registered McBurney wound
  frame against the authored 6 cm reference line.
    markLengthMm      extent of the stroke along the reference axis (max - min of the projections)
    markAngleDegrees  angle between (end - start), projected on the skin plane, and the axis, folded to 0..90
    markErrorMm       in-plane distance from the stroke's midpoint (start + end) / 2 to McBurney's point
- Predicates (services/preop/src/catalog/open-appendectomy.ts milestone `mark_incision`, evaluated like
  open-body.ts `BodyState.test`): marked == 1, markErrorMm <= 20, 50 <= markLengthMm <= 80, markAngleDegrees <= 25.

Frames: points are in the patient frame (PatientRoot local, metres): +X patient left, +Y anterior, +Z cranial,
umbilicus at the origin. The wound frame sits at McBurney's point (a third of the way from the right ASIS to the
umbilicus) with Unity rotation Euler(90, 0, -35) (OpenSurgerySession.cs), whose local +x is the reference axis and
local +z the inward skin normal.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

AUTHORED_RIGHT_ASIS = np.array([-0.13, -0.015, -0.14])  # OpenSurgerySession.AuthoredRightAsis
UMBILICUS = np.zeros(3)
REFERENCE_LENGTH_M = 0.06  # OpenBodyInteraction default referenceStart/End: +/-3 cm along wound x

# services/preop/src/catalog/open-appendectomy.ts, milestone mark_incision (tissue "skin").
MARK_INCISION_PREDICATES: list[tuple[str, str, float]] = [
    ("marked", "eq", 1),
    ("markErrorMm", "lte", 20),
    ("markLengthMm", "gte", 50),
    ("markLengthMm", "lte", 80),
    ("markAngleDegrees", "lte", 25),
]


def unity_euler(x_deg: float, y_deg: float, z_deg: float) -> np.ndarray:
    """Rotation matrix of Unity's Quaternion.Euler(x, y, z): z first, then x, then y (R = Ry Rx Rz)."""
    x, y, z = np.deg2rad([x_deg, y_deg, z_deg])
    rx = np.array([[1, 0, 0], [0, np.cos(x), -np.sin(x)], [0, np.sin(x), np.cos(x)]])
    ry = np.array([[np.cos(y), 0, np.sin(y)], [0, 1, 0], [-np.sin(y), 0, np.cos(y)]])
    rz = np.array([[np.cos(z), -np.sin(z), 0], [np.sin(z), np.cos(z), 0], [0, 0, 1]])
    return ry @ rx @ rz


WOUND_ROTATION = unity_euler(90, 0, -35)  # OpenSurgerySession: woundFrame.localRotation


@dataclass(frozen=True)
class Landmarks:
    """Registered landmarks in the patient frame (metres)."""

    right_asis: np.ndarray
    umbilicus: np.ndarray

    @classmethod
    def authored(cls) -> Landmarks:
        return cls(AUTHORED_RIGHT_ASIS.copy(), UMBILICUS.copy())

    @property
    def mcburney(self) -> np.ndarray:
        """OpenSurgeryStroke.McBurney: Vector3.Lerp(rightAsis, umbilicus, 1/3)."""
        return self.right_asis + (self.umbilicus - self.right_asis) / 3.0

    @property
    def axis(self) -> np.ndarray:
        return WOUND_ROTATION[:, 0].copy()

    @property
    def normal(self) -> np.ndarray:
        """Inward skin normal (wound local +z)."""
        return WOUND_ROTATION[:, 2].copy()

    def reference_line(self) -> tuple[np.ndarray, np.ndarray]:
        c = self.mcburney
        return c - self.axis * REFERENCE_LENGTH_M / 2, c + self.axis * REFERENCE_LENGTH_M / 2

    def to_dict(self) -> dict:
        return {"rightAsis": [round(float(v), 5) for v in self.right_asis], "umbilicus": [round(float(v), 5) for v in self.umbilicus]}


def _in_plane(v: np.ndarray, normal: np.ndarray) -> np.ndarray:
    return v - np.outer(v @ normal, normal) if v.ndim == 2 else v - (v @ normal) * normal


def _angle(a: np.ndarray, b: np.ndarray) -> float:
    c = float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))
    return float(np.degrees(np.arccos(np.clip(c, -1.0, 1.0))))


def measure_stroke(points, lm: Landmarks) -> dict:
    """Facts one continuous marker stroke reports to the body (OpenSurgeryStroke + FlushStroke "mark")."""
    p = np.asarray(points, dtype=float).reshape(-1, 3)
    if len(p) < 2:
        return {}
    start_ref, _ = lm.reference_line()
    axis, normal = lm.axis, lm.normal
    along = (p - start_ref) @ axis
    angle = 0.0
    for q in p[1:]:  # Sample(): updated whenever the in-plane travel from the start exceeds 1 mm
        travel = _in_plane(q - p[0], normal)
        if travel @ travel > 1e-6:
            angle = min(_angle(travel, axis), _angle(travel, -axis))
    mid = (p[0] + p[-1]) / 2
    error = float(np.linalg.norm(_in_plane(mid - lm.mcburney, normal)) * 1000)
    return {"marked": 1.0, "markErrorMm": error, "markLengthMm": float((along.max() - along.min()) * 1000),
            "markAngleDegrees": angle}


def split_strokes(points, drawing) -> list[np.ndarray]:
    """Contiguous pen-down runs. Each lift flushes one stroke to the body, as OpenBodyInteraction does."""
    strokes, cur = [], []
    for p, d in zip(np.asarray(points, float).reshape(-1, 3), drawing):
        if d:
            cur.append(p)
        elif cur:
            strokes.append(np.array(cur))
            cur = []
    if cur:
        strokes.append(np.array(cur))
    return [s for s in strokes if len(s) >= 2]


def measure_marks(strokes: list[np.ndarray], lm: Landmarks) -> dict:
    """Body facts after every stroke is applied in order: the latest mark overwrites the facts (open-body.ts)."""
    facts: dict = {}
    for s in strokes:
        f = measure_stroke(s, lm)
        if f and f["markLengthMm"] >= 1:  # FlushStroke ignores strokes under 1 mm
            facts = f
    return facts


def check(facts: dict, predicates=MARK_INCISION_PREDICATES) -> tuple[bool, list[str]]:
    """BodyState.test: a missing fact never satisfies a predicate."""
    failed = []
    for fact, op, value in predicates:
        if fact not in facts:
            failed.append(fact)
            continue
        v = facts[fact]
        ok = v >= value if op == "gte" else v <= value if op == "lte" else v == value if op == "eq" else False
        if not ok:
            failed.append(fact)
    return not failed, list(dict.fromkeys(failed))


def path_error_mm(points, lm: Landmarks) -> float | None:
    """Median in-plane distance (mm) from drawn points to the accepted 6 cm reference line (not a grader fact)."""
    p = np.asarray(points, float).reshape(-1, 3)
    if not len(p):
        return None
    a, b = lm.reference_line()
    ab = b - a
    t = np.clip(((p - a) @ ab) / (ab @ ab), 0.0, 1.0)
    d = _in_plane(p - (a + np.outer(t, ab)), lm.normal)
    return float(np.median(np.linalg.norm(d, axis=1)) * 1000)
