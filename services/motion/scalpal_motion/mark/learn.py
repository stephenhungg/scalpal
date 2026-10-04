"""Behavior cloning for mark_incision, reusing the transfer task's chunked MLP (learning/policy.py).

Observation (14, patient frame): marker tip xyz, right ASIS xyz, umbilicus xyz, drawn extent so far (m), tip on skin,
last tip displacement xyz. No time or phase input, and not the policy's own last trigger (it would just copy it). Action chunk (10 x 4): tip displacement xyz per tick, trigger.

Data generation follows learning/demos.py: each source demo seeds DART-style noisy executions near its own scene
(the landmarks jittered by +/-1 cm and re-anchored to that scene's McBurney point), labelled with the clean
correction back to the demo's path. So coverage of patient-to-patient landmark variation grows with the number of
demos. The evaluation then runs the policy closed loop on the full sim (arm IK + hand + marker) at held-out,
randomized landmarks and patient poses, and grades every rollout with grader.py, the learner's own predicates.
"""

from __future__ import annotations

import hashlib
import json
import time
from dataclasses import dataclass

import numpy as np
import torch

from ..learning.policy import Ensembler, Policy, train
from .demos import DT, PRESS_M, START_OFFSET, MarkDemo, sample_landmarks
from .grader import Landmarks, check, measure_marks, path_error_mm, split_strokes
from .scene import MarkRobot, PatientPose, onto_skin, sample_patient_pose, skin_distance, skin_y

CHUNK = 10
OBS_DIM = 14
ACT_DIM = 4
MAX_STEP_M = 0.015
DRAW_M = 0.002
MAX_TICKS = 260  # 13 s
BUDGET = 400  # generated episodes per training set, split across demos (fixed, like the transfer sweep)


def obs_vector(tip, lm: Landmarks, drawn_m: float, last_step) -> np.ndarray:
    return np.r_[tip, lm.right_asis, lm.umbilicus, drawn_m, float(skin_distance(tip) <= DRAW_M), last_step].astype(np.float32)


def clamp_to_skin(p) -> np.ndarray:
    q = np.array(p, float)
    q[1] = max(q[1], skin_y(q[0]) - PRESS_M)  # the marker presses at most PRESS_M into the skin
    return q


@dataclass
class Stroke:
    """Marker state shared by data generation and rollouts: what is drawn, where."""

    points: list
    drawing: list
    drawn_m: float = 0.0
    pen: bool = False

    @classmethod
    def empty(cls) -> Stroke:
        return cls([], [])

    def add(self, tip: np.ndarray, pen: bool) -> None:
        on = pen and skin_distance(tip) <= DRAW_M
        if on:  # how far the marker has got from where it first touched (robust to jitter, unlike path length)
            first = next(p for p, d in zip(self.points + [onto_skin(tip)], self.drawing + [True]) if d)
            self.drawn_m = max(self.drawn_m, float(np.linalg.norm(onto_skin(tip) - first)))
        self.points.append(onto_skin(tip) if on else np.array(tip, float))
        self.drawing.append(on)
        self.pen = pen

    def strokes(self) -> list[np.ndarray]:
        return split_strokes(self.points, self.drawing)


def _reanchor(demo: MarkDemo, lm: Landmarks) -> tuple[np.ndarray, np.ndarray]:
    """The demo's path moved to another patient's McBurney point (start stays put, as the robot's home does)."""
    shift = lm.mcburney - demo.landmarks.mcburney
    w = np.clip(np.arange(len(demo.tip)) / 10.0, 0, 1)[:, None]  # blend in the shift over the first 0.5 s
    ref = demo.tip + w * shift
    pen = demo.trigger > 0.5
    ref[pen, 1] = skin_y(ref[pen, 0]) - PRESS_M
    return ref, demo.trigger.copy()


def generate(demos: list[MarkDemo], episodes: int = BUDGET, seed: int = 0, noise_m: float = 0.0015) -> dict:
    """DART-style noisy executions around each demo; labels are clean corrective action chunks."""
    rng = np.random.default_rng(seed)
    obs, act = [], []
    for e in range(episodes):
        demo = demos[e % len(demos)]
        jitter = rng.uniform([-0.01, -0.003, -0.01], [0.01, 0.003, 0.01])
        lm = Landmarks(demo.landmarks.right_asis + jitter, demo.landmarks.umbilicus.copy())
        ref, trig = _reanchor(demo, lm)
        T = len(ref)
        tip = ref[0] + rng.uniform(-0.015, 0.015, 3)
        state = Stroke.empty()
        drift = np.zeros(3)
        last = np.zeros(3)
        for k in range(T + 15):
            i = min(k, T - 1)
            nxt = [min(i + 1 + j, T - 1) for j in range(CHUNK)]
            chunk = np.zeros((CHUNK, ACT_DIM))
            prev = tip
            for j, n in enumerate(nxt):
                chunk[j, :3] = ref[n] - prev
                chunk[j, 3] = trig[n]
                prev = ref[n]
            obs.append(obs_vector(tip, lm, state.drawn_m, last))
            act.append(chunk)
            drift = 0.5 * drift + rng.normal(0, noise_m, 3)
            step = chunk[0, :3] + drift
            n = np.linalg.norm(step)
            if n > MAX_STEP_M:
                step *= MAX_STEP_M / n
            new = clamp_to_skin(tip + step)
            last, tip = new - tip, new
            state.add(tip, chunk[0, 3] > 0.5)
    return {"obs": np.array(obs, np.float32), "act": np.array(act, np.float32)}


def train_policy(demos: list[MarkDemo], seed: int = 0, steps: int = 3000, episodes: int = BUDGET) -> tuple[Policy, dict]:
    t0 = time.perf_counter()
    data = generate(demos, episodes=episodes, seed=seed)
    gen_s = time.perf_counter() - t0
    policy, info = train(data, seed=seed, steps=steps, batch=256, hidden=256, threads=4)
    info["generate_s"] = round(gen_s, 1)
    return policy, info


@dataclass
class Rollout:
    success: bool
    failed: list
    facts: dict
    path_error_mm: float | None
    ik_error_mm: float
    ticks: int
    landmarks: Landmarks
    patient: PatientPose
    tips: np.ndarray
    drawing: list
    arm_q: np.ndarray  # (T, 7) for rendering
    finger_q: np.ndarray  # constant grasp, kept for completeness

    def summary(self) -> dict:
        return {"success": self.success, "failed": self.failed, "facts": {k: round(float(v), 2) for k, v in self.facts.items()},
                "pathErrorMm": None if self.path_error_mm is None else round(self.path_error_mm, 2),
                "ikErrorMm": round(self.ik_error_mm, 3), "durationS": round(self.ticks * DT, 2),
                "landmarks": self.landmarks.to_dict(), "patient": self.patient.to_dict()}


def rollout(policy: Policy, robot: MarkRobot, lm: Landmarks, patient: PatientPose, start: np.ndarray, max_ticks: int = MAX_TICKS) -> Rollout:
    """Closed loop on the full embodiment: policy -> tip command -> arm IK -> measured tip -> marker on skin."""
    robot.reset(patient)
    cmd = np.array(start, float)
    robot.ik(cmd, iters=200)
    tip = robot.tip_patient()
    ens = Ensembler(CHUNK)
    state = Stroke.empty()
    tips, arm, ik_errs = [tip], [robot.d.qpos[robot.arm_q].copy()], []
    state.add(tip, False)
    lifted = 0
    t = 0
    for t in range(max_ticks):
        a = ens(t, policy.predict(obs_vector(tip, lm, state.drawn_m, tip - tips[-2] if len(tips) > 1 else np.zeros(3))))
        step = a[:3]
        n = np.linalg.norm(step)
        if n > MAX_STEP_M:
            step = step * (MAX_STEP_M / n)
        cmd = clamp_to_skin(cmd + step)
        ik_errs.append(robot.ik(cmd))
        tip = robot.tip_patient()
        state.add(tip, a[3] > 0.5)
        tips.append(tip)
        arm.append(robot.d.qpos[robot.arm_q].copy())
        # Done once something is drawn and the marker is lifted clear of the skin with the pen up.
        lifted = lifted + 1 if (state.drawn_m > 0.01 and not state.pen and skin_distance(tip) > 0.03) else 0
        if lifted >= 8:
            break
    facts = measure_marks(state.strokes(), lm)
    ok, failed = check(facts)
    drawn = [p for p, d in zip(state.points, state.drawing) if d]
    return Rollout(ok, failed, facts, path_error_mm(drawn, lm) if drawn else None, float(np.median(ik_errs) * 1000) if ik_errs else 0.0,
                   t + 1, lm, patient, np.array(tips), list(state.drawing), np.array(arm), robot.d.qpos[robot.finger_q].copy())


def wilson(k: int, n: int, z: float = 1.96) -> tuple[float, float]:
    if n == 0:
        return 0.0, 0.0
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * np.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return float(max(0, c - h)), float(min(1, c + h))


def evaluate(policy: Policy, robot: MarkRobot, k: int = 30, seed: int = 10_000) -> dict:
    """K held-out rollouts: randomized landmarks (right ASIS +/-3 cm), patient pose (+/-5 cm, +/-15 deg), start."""
    rng = np.random.default_rng(seed)
    t0 = time.perf_counter()
    outs = []
    for _ in range(k):
        lm = sample_landmarks(rng)
        patient = sample_patient_pose(rng)
        start = START_OFFSET + rng.uniform(-0.015, 0.015, 3)
        outs.append(rollout(policy, robot, lm, patient, start))
    wins = sum(o.success for o in outs)
    errs = [o.path_error_mm for o in outs if o.path_error_mm is not None]
    fails: dict[str, int] = {}
    for o in outs:
        for f in o.failed:
            fails[f] = fails.get(f, 0) + 1
    lo, hi = wilson(wins, k)
    return {"rollouts": k, "successes": wins, "successRate": round(wins / k, 3), "ci95": [round(lo, 3), round(hi, 3)],
            "medianPathErrorMm": round(float(np.median(errs)), 2) if errs else None, "rolloutsWithoutMark": k - len(errs),
            "failedPredicates": fails, "medianRolloutS": round(float(np.median([o.ticks * DT for o in outs])), 2),
            "medianIkErrorMm": round(float(np.median([o.ik_error_mm for o in outs])), 3),
            "evalWallS": round(time.perf_counter() - t0, 1), "seed": seed}


def dataset_key(demos: list[MarkDemo], steps: int, seed: int) -> str:
    h = hashlib.sha256()
    for d in demos:
        h.update(d.demo_id.encode())
        h.update(np.round(d.tip, 5).tobytes())
    h.update(f"{steps}:{seed}:{BUDGET}:v1".encode())
    return h.hexdigest()[:16]


def cached_policy(demos: list[MarkDemo], cache_dir, steps: int = 3000, seed: int = 0) -> tuple[Policy, dict]:
    """Train once per exact dataset; later calls load the weights."""
    key = dataset_key(demos, steps, seed)
    path = cache_dir / f"mark-policy-{key}.pt"
    meta = cache_dir / f"mark-policy-{key}.json"
    if path.exists() and meta.exists():
        st = torch.load(path, weights_only=False)
        return Policy.from_state(st), {**json.loads(meta.read_text()), "cached": True}
    policy, info = train_policy(demos, seed=seed, steps=steps)
    cache_dir.mkdir(parents=True, exist_ok=True)
    torch.save(policy.state_dict(), path)
    info = {**info, "key": key, "demos": len(demos)}
    meta.write_text(json.dumps(info))
    return policy, {**info, "cached": False}


def learning_curve(ns: list[int], seeds: int = 2, k: int = 30, steps: int = 3000) -> dict:
    """Success vs number of demos. Demos here are SYNTHETIC (no headset demos exist yet); say so wherever shown."""
    from .demos import synthetic_demo, synthetic_set

    robot = MarkRobot()
    rng = np.random.default_rng(123)
    raw = [synthetic_demo(rng).grade()["success"] for _ in range(500)]
    rows = []
    t0 = time.perf_counter()
    for n in ns:
        per_seed, errs = [], []
        for s in range(seeds):
            pool = synthetic_set(n, seed=s)
            policy, info = train_policy(pool, seed=s, steps=steps)
            ev = evaluate(policy, robot, k=k)
            per_seed.append(ev)
            if ev["medianPathErrorMm"] is not None:
                errs.append(ev["medianPathErrorMm"])
        wins = sum(e["successes"] for e in per_seed)
        total = sum(e["rollouts"] for e in per_seed)
        lo, hi = wilson(wins, total)
        rows.append({"demos": n, "humanDemos": 0, "syntheticDemos": n, "successRate": round(wins / total, 3), "ci95": [round(lo, 3), round(hi, 3)],
                     "rollouts": total, "perSeed": [e["successRate"] for e in per_seed],
                     "medianPathErrorMm": round(float(np.median(errs)), 2) if errs else None,
                     "failedPredicates": {f: sum(e["failedPredicates"].get(f, 0) for e in per_seed) for f in {f for e in per_seed for f in e["failedPredicates"]}}})
        print(f"robot-curve: N={n} success {rows[-1]['successRate']} {rows[-1]['ci95']}, path {rows[-1]['medianPathErrorMm']} mm")
    return {"schema": "scalpal.robot_mark_curve.v1", "step": "mark_incision", "data": "synthetic demos only (0 headset demos)",
            "rolloutsPerSeed": k, "seeds": seeds, "trainSteps": steps, "episodesPerTrainingSet": BUDGET,
            "syntheticRawGraderPassRate": round(float(np.mean(raw)), 3),
            "evaluation": "held-out seed 10000: right ASIS +/-3 cm, patient pose +/-5 cm and +/-15 deg, start +/-1.5 cm; graded with the mark_incision predicates",
            "rows": rows, "wallS": round(time.perf_counter() - t0, 1)}


def replay_commands(robot: MarkRobot, demo: MarkDemo, patient: PatientPose) -> dict:
    """Open-loop: drive the arm through a demo's tip path (embodiment check, not a policy)."""
    robot.reset(patient)
    state = Stroke.empty()
    errs = []
    for p, trig in zip(demo.tip, demo.trigger):
        errs.append(robot.ik(clamp_to_skin(p), iters=200 if not errs else 30))
        state.add(robot.tip_patient(), trig > 0.5)
    facts = measure_marks(state.strokes(), demo.landmarks)
    ok, failed = check(facts)
    return {"success": ok, "failed": failed, "facts": facts, "maxIkErrorMm": float(max(errs[1:]) * 1000)}
