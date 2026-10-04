"""Human finger motion -> task demonstrations -> sim-filtered training data.

What comes from the human: finger joint trajectories (retargeted MediaPipe, `motion.json`), cut
into grasp closures by a flex signal (open shape, closing profile and duration, closed shape,
opening profile). What is synthesized: the wrist path (min-jerk reach to the handle, lift,
carry, lower) and each demo's source object pose, because no clip logs wrist or object pose yet.
New episodes from `scalpal-motion import-episode` drop in as more `motion.json` files.

Generation follows MimicGen: a source demo's wrist path is re-expressed in the handle frame
(approach and grasp) and the tray frame (place and release) and mapped to a new scene. Rollouts
run in physics with DART noise, are labeled with the clean corrective action, and only those
that place the handle in the tray are kept.
"""

from __future__ import annotations

import json
from concurrent.futures import ProcessPoolExecutor
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .env import (
    ACT_DIM, DT, FINGER_ACTUATORS, HANDLE_RADIUS, MAX_DPOS, MAX_DYAW, MAX_STEPS, OBJ_BOX, OBJ_CENTER,
    OBJ_YAW, TRAY_HALF, Scene, TransferEnv, human_to_actuators, sample_scene, wrap, wrap_half,
)

FLEX_JOINTS = ["FFJ3", "FFJ2", "MFJ3", "MFJ2", "RFJ3", "RFJ2", "LFJ3", "LFJ2"]
GRASP_DZ = 0.005  # grip point 5 mm above the handle axis at grasp (from the scripted gate)
PRE_Z = 0.07
LIFT_Z = 0.08
CHUNK = 10
LOCAL_XY = 0.06  # a demo is transformed to handle poses within +/-6 cm of where it was recorded
LOCAL_YAW = np.deg2rad(20)

# Contact adaptation (see README): the clip's fist curls the coupled distal joints to ~2.8 rad,
# past what a 2.4 cm handle allows, and its thumb barely opposes (THJ1 0.24 rad). With raw human
# targets the scripted lift held 4/10; capping J0 and using a fixed thumb opposition, 10/10.
J0_CAP = 1.6
THUMB_OPPOSE = {"THJ5": 0.6, "THJ4": 1.2, "THJ3": 0.0, "THJ2": 0.0, "THJ1": 0.8}
PRESHAPE = np.zeros(len(FINGER_ACTUATORS))  # synthesized open hand for the approach

# Phase ids
APPROACH, DESCEND, CLOSE, LIFT, CARRY, LOWER, RELEASE, RETREAT = range(8)


# -- human motion ---------------------------------------------------------------------------
def load_motion(path: Path) -> dict:
    m = json.loads(Path(path).read_text())
    names = m["joint_names"]
    t = np.array([f["t_ms"] for f in m["frames"]], dtype=float) / 1000
    valid = np.array([bool(f["valid"]) for f in m["frames"]])
    q = np.array([f["qpos"] if f["valid"] else [np.nan] * len(names) for f in m["frames"]], dtype=float)
    return {"path": str(path), "names": names, "t": t, "valid": valid, "q": q, "summary": m.get("summary", {})}


def _resample(t: np.ndarray, q: np.ndarray, valid: np.ndarray, max_gap: float = 0.3) -> list[tuple[np.ndarray, np.ndarray]]:
    """Split at tracking gaps longer than max_gap and resample each valid run to the 20 Hz control rate.

    Invalid frames are never filled: a run ends at any gap over max_gap; shorter gaps are bridged
    by linear interpolation between real frames only.
    """
    runs, start = [], None
    idx = np.flatnonzero(valid)
    if len(idx) == 0:
        return []
    start = idx[0]
    for a, b in zip(idx[:-1], idx[1:]):
        if t[b] - t[a] > max_gap:
            runs.append(idx[(idx >= start) & (idx <= a)])
            start = b
    runs.append(idx[idx >= start])
    out = []
    for r in runs:
        if len(r) < 4:
            continue
        tt = np.arange(t[r[0]], t[r[-1]] + 1e-9, DT)
        qq = np.stack([np.interp(tt, t[r], q[r, j]) for j in range(q.shape[1])], 1)
        out.append((tt, qq))
    return out


def extract_profiles(motion: dict, min_amplitude: float = 0.6) -> tuple[list[dict], dict]:
    """Find grasp-like closures in a human finger trajectory, with no hand labels.

    Flex = mean of the J2/J3 finger joints. Onset is the upward crossing of 40% of the clip's
    flex range (p5 to p95), plateau the first frame within 90% of the event peak, release the
    downward crossing of 40%. Events whose rise is below `min_amplitude` of the range are
    reported but not used (partial curls, not grasps).
    """
    names = motion["names"]
    fi = [names.index(j) for j in FLEX_JOINTS]
    valid_frac = float(motion["valid"].mean())
    report = {"source": motion["path"], "valid_fraction": round(valid_frac, 3), "events": []}
    profiles = []
    flex_all = np.nanmean(motion["q"][:, fi], 1)
    lo, hi = np.nanpercentile(flex_all, 5), np.nanpercentile(flex_all, 95)
    rng_ = max(hi - lo, 1e-6)
    thr = lo + 0.4 * rng_
    report.update(flex_p5=round(float(lo), 3), flex_p95=round(float(hi), 3), threshold=round(float(thr), 3))
    for tt, qq in _resample(motion["t"], motion["q"], motion["valid"]):
        flex = qq[:, fi].mean(1)
        above = flex > thr
        k = 1
        while k < len(flex):
            if above[k] and not above[k - 1]:
                onset = k
                end = onset
                while end < len(flex) and above[end]:
                    end += 1
                if end >= len(flex):
                    break  # closure runs past the clip end; no release seen
                peak = onset + int(np.argmax(flex[onset:end]))
                plateau = onset + int(np.argmax(flex[onset:end] >= flex[onset] + 0.9 * (flex[peak] - flex[onset])))
                pre_lo = max(0, onset - int(1.0 / DT))
                start = pre_lo + int(np.argmin(flex[pre_lo:onset]))
                post_hi = min(len(flex), end + int(1.0 / DT))
                stop = end + int(np.argmin(flex[end:post_hi]))
                last_hold = end - 1 - int(np.argmax(flex[end - 1:plateau - 1:-1] >= flex[onset] + 0.9 * (flex[peak] - flex[onset])))
                amp = (flex[peak] - flex[start]) / rng_
                ev = {"t_onset_s": round(float(tt[onset]), 3), "t_plateau_s": round(float(tt[plateau]), 3),
                      "t_release_s": round(float(tt[end]), 3), "amplitude": round(float(amp), 3),
                      "peak_flex": round(float(flex[peak]), 3), "used": bool(amp >= min_amplitude)}
                report["events"].append(ev)
                if ev["used"]:
                    act = np.stack([human_to_actuators(names, q) for q in qq])
                    raw = {
                        "close": act[start:plateau + 1], "hold": np.median(act[plateau:last_hold + 1], 0),
                        "release": act[last_hold:stop + 1],
                    }
                    prof = adapt_to_contact(raw)
                    prof.update(source=motion["path"], event=len(report["events"]) - 1,
                                t_start_s=round(float(tt[start]), 3), t_stop_s=round(float(tt[stop]), 3),
                                actuators=FINGER_ACTUATORS,
                                raw_human={k: np.round(v, 4).tolist() for k, v in raw.items()})
                    profiles.append(prof)
                k = end
            k += 1
    return profiles, report


def adapt_to_contact(raw: dict) -> dict:
    """Human closure -> actuator rows the handle allows.

    Fingers: the human closing/opening trajectory, with the start offset blended out so the
    closure begins from the open pre-shape, and coupled J0 capped at J0_CAP. Thumb: a fixed
    opposition pose, reached on the human closure's own progress curve (so timing stays human).
    """
    th = [FINGER_ACTUATORS.index(a) for a in THUMB_OPPOSE]
    j0 = [i for i, a in enumerate(FINGER_ACTUATORS) if a.endswith("J0")]
    fl = [i for i, a in enumerate(FINGER_ACTUATORS) if a[2:] in ("J3", "J0") and not a.startswith("TH")]
    opp = np.array(list(THUMB_OPPOSE.values()))

    def progress(rows, a, b):
        f = rows[:, fl].mean(1)
        return np.clip((f - a) / max(b - a, 1e-6), 0, 1)

    close, hold, rel = np.array(raw["close"], float), np.array(raw["hold"], float), np.array(raw["release"], float)
    f0, fh = close[0, fl].mean(), hold[fl].mean()
    pc = progress(close, f0, fh)
    c = close - (1 - pc)[:, None] * (close[0] - PRESHAPE)
    c[:, th] = PRESHAPE[th] + pc[:, None] * (opp - PRESHAPE[th])
    h = hold.copy()
    h[th] = opp
    pr = progress(rel, rel[-1, fl].mean(), fh)
    r = rel.copy()
    r[:, th] = PRESHAPE[th] + pr[:, None] * (opp - PRESHAPE[th])
    for a in (c, r):
        a[:, j0] = np.minimum(a[:, j0], J0_CAP)
    h[j0] = np.minimum(h[j0], J0_CAP)
    return {"open": PRESHAPE.tolist(), "close": c.tolist(), "hold": h.tolist(), "release": r.tolist()}


# -- reference trajectories -----------------------------------------------------------------
def _minjerk(n: int) -> np.ndarray:
    s = np.arange(1, n + 1) / n
    return 10 * s**3 - 15 * s**4 + 6 * s**5


def _resample_rows(rows: np.ndarray, n: int) -> np.ndarray:
    rows = np.asarray(rows)
    if len(rows) == 1:
        return np.repeat(rows, n, 0)
    x = np.linspace(0, 1, len(rows))
    xn = np.linspace(0, 1, n)
    return np.stack([np.interp(xn, x, rows[:, j]) for j in range(rows.shape[1])], 1)


def source_reference(scene: Scene, profile: dict, warp: float = 1.0, inhand=(0.0, 0.0)) -> dict:
    """World-frame reference for one demo: synthesized wrist path, human finger trajectory.

    Returns grip (T,4), finger (T,18), phase (T,), and per-step frame weights (T,3) over
    (world/start, handle, tray) used by the object-relative transform.
    """
    def steps(sec):
        return max(1, int(round(sec * warp / DT)))

    obj = np.r_[scene.obj_xy, HANDLE_RADIUS, scene.obj_yaw]
    tray_top = 2 * TRAY_HALF[2]
    grasp = obj + [0, 0, GRASP_DZ, 0]
    pre = grasp + [0, 0, PRE_Z, 0]
    lift = grasp + [0, 0, LIFT_Z, 0]
    # the handle sits `inhand` (xy) from the grip point once held; aim so the handle lands on the tray
    place = np.r_[scene.tray_xy - np.asarray(inhand), HANDLE_RADIUS + tray_top + GRASP_DZ + 0.025, 0.0]  # drop ~2.5 cm
    above_tray = place + [0, 0, 0.06, 0]
    retreat = place + [0, 0, 0.07, 0]
    open_f = np.array(profile["open"])
    close_f = _resample_rows(profile["close"], steps(len(profile["close"]) * DT))
    hold_f = np.array(profile["hold"])
    rel_f = _resample_rows(profile["release"], steps(len(profile["release"]) * DT))

    grip, finger, phase, w = [], [], [], []

    def move(a, b, n, f, ph, wa, wb):
        s = _minjerk(n)
        for k, sk in enumerate(s):
            g = a + sk * (b - a)
            g[3] = a[3] + sk * wrap(b[3] - a[3])
            grip.append(g)
            finger.append(f[k] if f.ndim == 2 else f)
            phase.append(ph)
            w.append(np.asarray(wa) + sk * (np.asarray(wb) - np.asarray(wa)))

    home = np.r_[scene.home, 0.0]
    W0, WO, WT = [1, 0, 0], [0, 1, 0], [0, 0, 1]
    move(home, pre, steps(1.3), open_f, APPROACH, W0, WO)
    move(pre, grasp, steps(0.6), open_f, DESCEND, WO, WO)
    move(grasp, grasp, len(close_f), close_f, CLOSE, WO, WO)
    move(grasp, grasp, steps(0.25), hold_f, CLOSE, WO, WO)
    move(grasp, lift, steps(0.6), hold_f, LIFT, WO, WO)
    move(lift, above_tray, steps(1.2), hold_f, CARRY, WO, WT)
    move(above_tray, place, steps(0.5), hold_f, LOWER, WT, WT)
    move(place, place, len(rel_f), rel_f, RELEASE, WT, WT)
    move(place, place, steps(0.2), rel_f[-1], RELEASE, WT, WT)
    move(place, retreat, steps(0.4), rel_f[-1], RETREAT, WT, WT)
    return {"grip": np.array(grip), "finger": np.array(finger), "phase": np.array(phase), "w": np.array(w),
            "inhand": np.asarray(inhand, float)}


def _se2(pose_xyyaw) -> tuple[np.ndarray, float]:
    return np.asarray(pose_xyyaw[:2], dtype=float), float(pose_xyyaw[2])


def transform_reference(ref: dict, src: Scene, obj_pose, tray_xy, start=None) -> np.ndarray:
    """MimicGen-style transform of a world-frame grip path to a new scene.

    obj_pose = (x, y, yaw) of the handle in the new scene. Each step is mapped through the handle
    frame and the tray frame (yaw-only rigid transforms) and blended by the reference's frame
    weights; the world weight keeps the start pose (`start`, default the source start).
    """
    g = ref["grip"]
    ps, ys = _se2([*src.obj_xy, src.obj_yaw])
    pn, yn = _se2(obj_pose)
    dyaw = float(wrap_half(yn - ys))  # the capsule is symmetric under 180 deg
    c, s = np.cos(dyaw), np.sin(dyaw)
    R = np.array([[c, -s], [s, c]])
    g_obj = g.copy()
    g_obj[:, :2] = (g[:, :2] - ps) @ R.T + pn
    g_obj[:, 3] = g[:, 3] + dyaw
    g_tray = g.copy()
    g_tray[:, :2] = g[:, :2] - src.tray_xy + np.asarray(tray_xy)
    g_world = g.copy()
    if start is not None:
        g_world[:, :3] = g[:, :3] - g[0, :3] + np.asarray(start)[:3]
    w = ref["w"]
    out = w[:, :1] * g_world + w[:, 1:2] * g_obj + w[:, 2:3] * g_tray
    out[:, 3] = w[:, 0] * g_world[:, 3] + w[:, 1] * g_obj[:, 3] + w[:, 2] * g_tray[:, 3]
    return out


# -- human demos ----------------------------------------------------------------------------
@dataclass
class Demo:
    """One human demonstration: a finger profile plus the (synthesized) scene it was recorded in."""

    demo_id: int
    profile_id: int
    scene: Scene
    warp: float
    ref: dict


def make_demos(profiles: list[dict], n: int, seed: int = 0, env: TransferEnv | None = None) -> list[Demo]:
    """N source demos. Fingers: human profile (round-robin over detected closures). Scene: synthesized.

    Each demo is executed once in its own scene to measure where the handle sits in the hand,
    and that offset is baked into its place segment, as a recorded demo would carry it.
    """
    env = env or TransferEnv()
    rng = np.random.default_rng(seed)
    demos = []
    for i in range(n):
        sc = sample_scene(rng)
        warp = float(rng.uniform(0.9, 1.1))
        pid = i % len(profiles)
        ref = source_reference(sc, profiles[pid], warp)
        demos.append(Demo(i, pid, sc, warp, source_reference(sc, profiles[pid], warp, measure_inhand(env, sc, ref))))
    return demos


def measure_inhand(env: TransferEnv, scene: Scene, ref: dict) -> np.ndarray:
    """Handle xy minus grip xy at the end of the lift, from a clean execution (0 if not lifted)."""
    env.reset(scene, ref["finger"][0])
    for t in range(len(ref["grip"])):
        env.set_command(ref["grip"][t], ref["finger"][t])
        if ref["phase"][t] == CARRY:
            break
    if not env.grasped():
        return np.zeros(2)
    return env.obj_pose()[:2] - env.grip_pose()[:2]


def nearest_demo(demos: list[Demo], scene: Scene) -> Demo:
    d = [np.linalg.norm(x.scene.obj_xy - scene.obj_xy) + 0.05 * abs(wrap_half(x.scene.obj_yaw - scene.obj_yaw)) for x in demos]
    return demos[int(np.argmin(d))]


def local_scene(demo: Demo, rng: np.random.Generator) -> Scene:
    """A new scene near the demo's recorded handle pose (within the training box)."""
    src = demo.scene
    xy = np.clip(src.obj_xy + rng.uniform(-LOCAL_XY, LOCAL_XY, 2), OBJ_CENTER - OBJ_BOX, OBJ_CENTER + OBJ_BOX)
    yaw = float(np.clip(src.obj_yaw + rng.uniform(-LOCAL_YAW, LOCAL_YAW), -OBJ_YAW, OBJ_YAW))
    full = sample_scene(rng)
    return Scene(obj_xy=xy, obj_yaw=yaw, tray_xy=full.tray_xy, home=full.home)


# -- rollouts -------------------------------------------------------------------------------
def run_open_loop(env: TransferEnv, scene: Scene, grip: np.ndarray, finger: np.ndarray, shove_rng=None, shove_step=None, frames=None) -> dict:
    """Replay a fixed command sequence open loop (no feedback), then hold the last command."""
    env.reset(scene, finger[0])
    shoved = None
    for t in range(MAX_STEPS):
        if shove_rng is not None and t == shove_step:
            shoved = env.shove(shove_rng).tolist()
        k = min(t, len(grip) - 1)
        env.set_command(grip[k], finger[k])
        if frames is not None:
            frames.append(env.d.qpos.copy())
        if env.success():
            break
    return {"success": env.success(), "grasped": env.grasped(), "steps": env.t, "shove": shoved}


def expert_episode(env: TransferEnv, demo: Demo, scene: Scene, rng: np.random.Generator, dart: bool = True,
                   shove: bool = False) -> dict:
    """Track the demo transformed to `scene` in physics, with DART noise; record clean labels.

    The expert re-anchors the approach to the handle's current pose until the fingers start
    closing (so a shoved handle is re-targeted), and pauses its clock while the wrist lags the
    reference by more than 1 cm. Labels: chunk[0] is the clean correction from the commanded
    pose to the next reference step; later chunk entries are the clean reference increments.
    """
    ref = demo.ref
    T = len(ref["grip"])
    obs0 = env.reset(scene, ref["finger"][0])
    start = scene.home
    obj = env.obj_pose()

    def plan(o):
        return transform_reference(ref, demo.scene, (o[0], o[1], o[3]), scene.tray_xy, start=start)

    path = plan(obj)
    corr = None
    sig = np.r_[0.006, 0.006, 0.006, np.deg2rad(2.0)]
    noise = np.zeros(4)
    k = 0
    shove_step = int(rng.integers(10, 32)) if shove else None
    obs_l, a0_l, inc_l = [], [], []
    obs = obs0
    for t in range(MAX_STEPS):
        if shove_step is not None and t == shove_step:
            env.shove(rng, 0.02, 0.05)
            obs = env.obs()
        ph = ref["phase"][min(k, T - 1)]
        if ph <= DESCEND:
            path = plan(env.obj_pose())
        elif ph >= CARRY and corr is None:  # re-aim the place at where the handle actually sits in hand
            corr = env.obj_pose()[:2] - env.grip_pose()[:2] - ref["inhand"]
            path = path.copy()
            path[:, :2] -= ref["w"][:, 2:3] * corr
        kn = min(k + 1, T - 1)
        cmd = env.grip_cmd
        a0 = np.r_[path[kn, :3] - cmd[:3], wrap(path[kn, 3] - cmd[3]), ref["finger"][kn]]
        n = np.linalg.norm(a0[:3])
        if n > MAX_DPOS:
            a0[:3] *= MAX_DPOS / n
        a0[3] = np.clip(a0[3], -MAX_DYAW, MAX_DYAW)
        k2 = min(kn + 1, T - 1)
        inc = np.r_[path[k2, :3] - path[kn, :3], wrap(path[k2, 3] - path[kn, 3]), ref["finger"][k2]]
        obs_l.append(obs)
        a0_l.append(a0)
        inc_l.append(inc)
        act = a0.copy()
        if dart:
            noise = 0.8 * noise + 0.6 * sig * rng.standard_normal(4)
            act[:4] += noise
            act[4:] += 0.05 * rng.standard_normal(len(FINGER_ACTUATORS))
        obs = env.step(act)
        lag = np.linalg.norm(env.grip_cmd[:3] - path[kn, :3])
        if lag < 0.01 or ref["phase"][min(k, T - 1)] > DESCEND:
            k = kn
        if env.success():
            break
    return {"obs": np.array(obs_l, np.float32), "a0": np.array(a0_l, np.float32), "inc": np.array(inc_l, np.float32),
            "success": env.success(), "grasped": env.grasped(), "steps": env.t}


def chunk_labels(a0: np.ndarray, inc: np.ndarray, H: int = CHUNK) -> np.ndarray:
    """(T, H, ACT_DIM): step t's chunk is [a0_t, inc_t, inc_{t+1}, ...], held after the episode ends."""
    T = len(a0)
    out = np.zeros((T, H, ACT_DIM), np.float32)
    for t in range(T):
        out[t, 0] = a0[t]
        for h in range(1, H):
            j = t + h - 1
            if j < T:
                out[t, h] = inc[j]
            else:
                out[t, h, :4] = 0.0
                out[t, h, 4:] = inc[-1, 4:]
    return out


_ENV = None


def _worker_env() -> TransferEnv:
    global _ENV
    if _ENV is None:
        _ENV = TransferEnv()
    return _ENV


def _gen_job(args) -> list[dict]:
    demos, jobs = args
    env = _worker_env()
    out = []
    for demo_idx, seed, dart, shove in jobs:
        rng = np.random.default_rng(seed)
        demo = demos[demo_idx]
        scene = local_scene(demo, rng)
        ep = expert_episode(env, demo, scene, rng, dart=dart, shove=shove)
        ep.update(demo_id=demo.demo_id, seed=seed, shove=shove)
        out.append(ep)
    return out


def generate(demos: list[Demo], budget: int, seed: int, workers: int = 8, shove_frac: float = 0.3,
             dart: bool = True, keep_failures: bool = False) -> tuple[dict, dict]:
    """Roll out `budget` generated episodes (split evenly over demos) and keep the successful ones."""
    rng = np.random.default_rng(seed)
    jobs = [(i % len(demos), int(rng.integers(1 << 31)), dart, bool(rng.random() < shove_frac)) for i in range(budget)]
    parts = [jobs[i::workers] for i in range(workers)]
    eps = []
    with ProcessPoolExecutor(workers) as ex:
        for r in ex.map(_gen_job, [(demos, p) for p in parts]):
            eps.extend(r)
    keep = [e for e in eps if e["success"] or keep_failures]
    per_demo = {}
    for e in eps:
        d = per_demo.setdefault(e["demo_id"], [0, 0])
        d[0] += 1
        d[1] += int(e["success"])
    stats = {
        "attempted": len(eps), "kept": len(keep), "yield": round(len(keep) / max(1, len(eps)), 3),
        "grasp_rate": round(float(np.mean([e["grasped"] for e in eps])), 3),
        "yield_per_demo": {int(k): round(v[1] / v[0], 3) for k, v in sorted(per_demo.items())},
        "shove_episodes_kept": int(sum(e["shove"] for e in keep)),
    }
    if not keep:
        return {}, stats
    data = {
        "obs": np.concatenate([e["obs"] for e in keep]),
        "act": np.concatenate([chunk_labels(e["a0"], e["inc"]) for e in keep]),
        "episode": np.concatenate([np.full(len(e["obs"]), i) for i, e in enumerate(keep)]),
        "demo": np.concatenate([np.full(len(e["obs"]), e["demo_id"]) for e in keep]),
    }
    stats["samples"] = int(len(data["obs"]))
    return data, stats
