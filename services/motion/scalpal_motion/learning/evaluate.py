"""End-to-end experiment: human profiles -> generated data -> BC policies -> held-out evaluation.

Stages (each writes to `out/learning/` for bulky files and `learning-results/` for small results):
  extract   motion.json files -> human_profiles.json (grasp closures found in the human finger motion)
  sweep     for each N: N source demos -> MimicGen-style generation with DART noise and a success
            filter -> train 3 seeds -> evaluate policy and baselines on held-out scenes
  report    results.json -> chart.png
  video     side-by-side replay vs policy on one shoved held-out scene
"""

from __future__ import annotations

import json
import math
import time
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

from ..paths import MOTION_ROOT
from .demos import (
    CHUNK, Demo, extract_profiles, generate, load_motion, make_demos, nearest_demo, run_open_loop, transform_reference,
)
from .env import ACT_DIM, DT, FINGER_ACTUATORS, MAX_DPOS, MAX_DYAW, MAX_STEPS, TransferEnv, sample_scene

RESULTS = MOTION_ROOT / "learning-results"
WORK = MOTION_ROOT / "out" / "learning"
SAMPLE_MOTION = Path("/private/tmp/claude-501/live-ep/robot/motion.json")
EVAL_SEED, SHOVE_SEED, DEMO_SEED = 10_000, 20_000, 0
SHOVE_STEP = int(round(1.0 / DT))  # t = 1.0 s, before the hand reaches the handle


def wilson(k: int, n: int, z: float = 1.96) -> list[float]:
    if n == 0:
        return [0.0, 0.0]
    p = k / n
    den = 1 + z * z / n
    c = (p + z * z / (2 * n)) / den
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / den
    return [round(max(0.0, c - h), 3), round(min(1.0, c + h), 3)]


def summarize(flags: list[bool]) -> dict:
    k, n = int(sum(flags)), len(flags)
    return {"success": k, "n": n, "rate": round(k / max(n, 1), 3), "ci95": wilson(k, n)}


# -- scenes ---------------------------------------------------------------------------------
def eval_scenes(n_in: int = 80, n_ood: int = 20) -> list[tuple[object, bool]]:
    rng = np.random.default_rng(EVAL_SEED)
    return [(sample_scene(rng), False) for _ in range(n_in)] + [(sample_scene(rng, ood=True), True) for _ in range(n_ood)]


def shove_scenes(n: int = 50) -> list:
    rng = np.random.default_rng(SHOVE_SEED)
    return [sample_scene(rng) for _ in range(n)]


# -- rollouts -------------------------------------------------------------------------------
def rollout_policy(env: TransferEnv, policy, scene, shove_rng=None, record=None) -> dict:
    from .policy import Ensembler

    obs = env.reset(scene)
    ens = Ensembler(CHUNK)
    shoved = None
    for t in range(MAX_STEPS):
        if shove_rng is not None and t == SHOVE_STEP:
            shoved = env.shove(shove_rng).tolist()
            obs = env.obs()
        a = ens(t, policy.predict(obs))
        obs = env.step(a)
        if record is not None:
            record(env)
        if env.success():
            break
    return {"success": env.success(), "grasped": env.grasped(), "steps": env.t, "shove": shoved}


def rollout_random(env: TransferEnv, scene, rng, shove_rng=None) -> dict:
    env.reset(scene)
    for t in range(MAX_STEPS):
        if shove_rng is not None and t == SHOVE_STEP:
            env.shove(shove_rng)
        a = np.r_[rng.uniform(-MAX_DPOS, MAX_DPOS, 3), rng.uniform(-MAX_DYAW, MAX_DYAW), rng.uniform(env.ctrl_lo, env.ctrl_hi)]
        env.step(a)
        if env.success():
            break
    return {"success": env.success(), "grasped": env.grasped(), "steps": env.t}


def replay_commands(demo: Demo, scene, transformed: bool) -> tuple[np.ndarray, np.ndarray]:
    """Raw replay: the demo's recorded wrist/finger commands as is. Transformed: mapped to this
    scene's handle and tray poses at t = 0 (MimicGen transform), then run open loop."""
    ref = demo.ref
    if not transformed:
        return ref["grip"], ref["finger"]
    grip = transform_reference(ref, demo.scene, (*scene.obj_xy, scene.obj_yaw), scene.tray_xy, start=scene.home)
    return grip, ref["finger"]


_ENV = None


def _env() -> TransferEnv:
    global _ENV
    if _ENV is None:
        _ENV = TransferEnv()
    return _ENV


def _eval_job(job) -> list[dict]:
    import torch

    torch.set_num_threads(1)
    kind, payload, items = job
    env = _env()
    out = []
    policy = None
    if kind == "policy":
        from .policy import Policy

        policy = Policy.from_state(payload)
    for idx, scene, shove_seed in items:
        srng = np.random.default_rng(shove_seed) if shove_seed is not None else None
        if kind == "policy":
            r = rollout_policy(env, policy, scene, srng)
        elif kind == "random":
            r = rollout_random(env, scene, np.random.default_rng(idx + 7), srng)
        else:
            demos, transformed = payload
            demo = nearest_demo(demos, scene)
            g, f = replay_commands(demo, scene, transformed)
            r = run_open_loop(env, scene, g, f, shove_rng=srng, shove_step=SHOVE_STEP if srng is not None else None)
        r["idx"] = idx
        out.append(r)
    return out


def run_eval(kind: str, payload, scenes: list, shove: bool, workers: int) -> list[dict]:
    items = [(i, sc, SHOVE_SEED + 1 + i if shove else None) for i, sc in enumerate(scenes)]
    parts = [items[i::workers] for i in range(workers)]
    res = []
    with ProcessPoolExecutor(workers) as ex:
        for r in ex.map(_eval_job, [(kind, payload, p) for p in parts if p]):
            res.extend(r)
    return sorted(res, key=lambda r: r["idx"])


def evaluate_method(kind: str, payload, workers: int, n_shove: int = 50) -> dict:
    sc = eval_scenes()
    clean = run_eval(kind, payload, [s for s, _ in sc], False, workers)
    ood = [s for _, s in sc]
    sh = run_eval(kind, payload, shove_scenes(n_shove), True, workers)
    return {
        "in_distribution": summarize([r["success"] for r, o in zip(clean, ood) if not o]),
        "ood_ring": summarize([r["success"] for r, o in zip(clean, ood) if o]),
        "shove": summarize([r["success"] for r in sh]),
        "grasp_rate_in_distribution": round(float(np.mean([r["grasped"] for r, o in zip(clean, ood) if not o])), 3),
        "_flags": {"clean": [bool(r["success"]) for r in clean], "shove": [bool(r["success"]) for r in sh]},
    }


def pool_seeds(per_seed: list[dict]) -> dict:
    out = {}
    for key in ("in_distribution", "ood_ring", "shove"):
        k = sum(p[key]["success"] for p in per_seed)
        n = sum(p[key]["n"] for p in per_seed)
        rates = [p[key]["rate"] for p in per_seed]
        out[key] = {"success": k, "n": n, "rate": round(k / n, 3), "ci95": wilson(k, n),
                    "per_seed": rates, "seed_std": round(float(np.std(rates)), 3)}
    out["grasp_rate_in_distribution"] = round(float(np.mean([p["grasp_rate_in_distribution"] for p in per_seed])), 3)
    return out


# -- stages ---------------------------------------------------------------------------------
def stage_extract(motion_paths: list[Path]) -> dict:
    profiles, reports = [], []
    for p in motion_paths:
        prof, rep = extract_profiles(load_motion(p))
        profiles += prof
        reports.append(rep)
    out = {
        "schema": "scalpal.human_grasp_profiles/0",
        "note": "finger actuator targets (rad) at 20 Hz from retargeted MediaPipe hand motion; wrist and object "
                "poses are not in this data",
        "sources": reports, "profiles": profiles,
    }
    RESULTS.mkdir(parents=True, exist_ok=True)
    (RESULTS / "human_profiles.json").write_text(json.dumps(out, indent=1))
    print(f"{len(profiles)} usable grasp closure(s) from {len(motion_paths)} file(s)")
    for r in reports:
        print(" ", r["source"], [(e["t_onset_s"], e["amplitude"], e["used"]) for e in r["events"]])
    return out


def load_profiles() -> list[dict]:
    path = RESULTS / "human_profiles.json"
    if not path.exists():
        raise SystemExit(f"{path} missing; run `scalpal-motion learn extract` first")
    profiles = json.loads(path.read_text())["profiles"]
    if not profiles:
        raise SystemExit("no usable grasp closures in human_profiles.json")
    return profiles


def stage_sweep(ns: list[int], seeds: int, budget: int, steps: int, workers: int, tag: str = "") -> dict:
    import torch

    from .policy import train

    WORK.mkdir(parents=True, exist_ok=True)
    profiles = load_profiles()
    all_demos = make_demos(profiles, max(ns), DEMO_SEED)
    res_path = RESULTS / f"results{tag}.json"
    results = json.loads(res_path.read_text()) if res_path.exists() else {}
    results.setdefault("by_n", {})
    t_all = time.perf_counter()
    if "random" not in results:
        print("baseline: random actions")
        results["random"] = evaluate_method("random", None, workers)
    for n in ns:
        t0 = time.perf_counter()
        demos = all_demos[:n]
        data, gstats = generate(demos, budget, seed=1000 + n, workers=workers)
        print(f"N={n}: generated {gstats['attempted']} episodes, kept {gstats['kept']} (yield {gstats['yield']}), "
              f"{gstats.get('samples', 0)} samples, {time.perf_counter() - t0:.0f}s")
        np.savez_compressed(WORK / f"data{tag}_N{n}.npz", **data)
        entry = {"generation": gstats, "demo_scenes": [d.scene.to_dict() for d in demos],
                 "demo_inhand_xy": [np.round(d.ref["inhand"], 4).tolist() for d in demos]}
        per_seed, train_info = [], []
        for s in range(seeds):
            pol, info = train(data, seed=s, steps=steps, threads=workers)
            torch.save(pol.state_dict(), WORK / f"policy{tag}_N{n}_s{s}.pt")
            ev = evaluate_method("policy", pol.state_dict(), workers)
            train_info.append(info)
            per_seed.append(ev)
            print(f"  seed {s}: loss {info['final_loss']} ({info['train_s']}s)  in-dist {ev['in_distribution']['rate']}  "
                  f"ood {ev['ood_ring']['rate']}  shove {ev['shove']['rate']}")
        entry["training"] = train_info
        entry["policy"] = pool_seeds(per_seed)
        entry["policy_flags"] = [p.pop("_flags") for p in per_seed]
        for name, transformed in (("raw_replay", False), ("transformed_replay", True)):
            ev = evaluate_method("replay", (demos, transformed), workers)
            entry[name + "_flags"] = ev.pop("_flags")
            entry[name] = ev
            print(f"  {name}: in-dist {ev['in_distribution']['rate']}  ood {ev['ood_ring']['rate']}  shove {ev['shove']['rate']}")
        entry["wall_s"] = round(time.perf_counter() - t0, 1)
        results["by_n"][str(n)] = entry
        results["random"].pop("_flags", None)
        results.update(meta(budget, steps, seeds))
        res_path.write_text(json.dumps(results, indent=1))
    print(f"sweep done in {time.perf_counter() - t_all:.0f}s -> {res_path}")
    return results


def meta(budget: int, steps: int, seeds: int) -> dict:
    return {
        "schema": "scalpal.proof_of_learning/0",
        "embodiment": "MuJoCo Menagerie Shadow Hand (right), floating: forearm free joint welded to a mocap body "
                      "(grip xyz + yaw, palm down), 18 finger position actuators; wrist joints held at 0",
        "task": "pick a 12.4 x 2.4 cm capsule handle (30 g) from a random table pose and place it in a rimmed tray",
        "success": "handle center inside the 14 x 18 cm tray floor, resting (<1 cm/s), no hand contact, for 0.25 s, within 10 s",
        "from_human_data": [
            "finger closing trajectory and its timing (onset to plateau), closed finger shape, opening trajectory: "
            "retargeted MediaPipe joints of one public 13 s sample clip (dex-retargeting example, not a Quest task clip)",
        ],
        "synthesized": [
            "wrist path (min-jerk reach, descend, lift, carry, lower, retreat): no wrist log exists yet",
            "each source demo's handle and tray pose (seeded), since no clip records object pose",
            "open-hand pre-shape before the closure, and the thumb's closed pose (fixed opposition): the clip's thumb "
            "barely opposes; coupled distal finger flexion capped at 1.6 rad (the bare fist curls past the handle)",
            "all generated episodes: MimicGen-style transform + DART noise, physics-filtered for success",
        ],
        "config": {"generation_budget_per_n": budget, "train_steps": steps, "train_seeds": seeds, "chunk": CHUNK,
                   "eval": "80 in-distribution + 20 OOD-ring held-out scenes (seed 10000), 50 shove scenes (seed 20000)",
                   "shove": "handle displaced 3 to 5 cm, random direction, at t = 1.0 s (before contact)",
                   "local_augmentation": "each demo is transformed to handle poses within +/-6 cm, +/-20 deg of its own",
                   "shove_augmentation": "30% of generated episodes include a 2 to 5 cm shove before contact; the "
                                         "expert re-targets"},
    }


def stage_report(tag: str = "") -> Path:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    res = json.loads((RESULTS / f"results{tag}.json").read_text())
    ns = sorted(int(n) for n in res["by_n"])
    fig, axes = plt.subplots(1, 2, figsize=(11, 4.2), gridspec_kw={"width_ratios": [1.6, 1]})
    ax = axes[0]
    styles = [("policy", "BC policy (closed loop, 3 seeds)", "#2563eb", "-o"),
              ("transformed_replay", "object-transformed replay (open loop)", "#d97706", "--s"),
              ("raw_replay", "nearest-demo raw replay", "#6b7280", ":^")]
    for key, label, color, fmt in styles:
        r = [res["by_n"][str(n)][key]["in_distribution"] for n in ns]
        y = np.array([x["rate"] for x in r])
        lo = y - np.array([x["ci95"][0] for x in r])
        hi = np.array([x["ci95"][1] for x in r]) - y
        ax.errorbar(ns, y, yerr=[lo, hi], fmt=fmt, color=color, label=label, capsize=3, lw=1.8, ms=5)
    ax.axhline(res["random"]["in_distribution"]["rate"], color="#9ca3af", lw=1, label="random actions")
    ax.set_xscale("log")
    ax.set_xticks(ns)
    ax.set_xticklabels([str(n) for n in ns])
    ax.set_ylim(-0.02, 1.02)
    ax.set_xlabel("human demos (N)")
    ax.set_ylabel("success, held-out scenes (95% CI)")
    ax.set_title("Held-out success vs number of human demos")
    ax.legend(fontsize=8, loc="center right")
    ax.grid(alpha=0.25)
    ax = axes[1]
    nmax = str(max(ns))
    e = res["by_n"][nmax]
    keys = [("policy", "BC policy", "#2563eb"), ("transformed_replay", "transformed\nreplay", "#d97706"),
            ("raw_replay", "raw replay", "#6b7280")]
    x = np.arange(len(keys))
    for j, (cond, lab, alpha) in enumerate([("in_distribution", "clean", 1.0), ("shove", "shoved 3-5 cm", 0.45)]):
        ys = [e[k][cond]["rate"] for k, _, _ in keys]
        err = [[y - e[k][cond]["ci95"][0] for (k, _, _), y in zip(keys, ys)], [e[k][cond]["ci95"][1] - y for (k, _, _), y in zip(keys, ys)]]
        ax.bar(x + (j - 0.5) * 0.38, ys, 0.36, yerr=err, capsize=3, color=[c for _, _, c in keys], alpha=alpha, label=lab)
    ax.set_xticks(x)
    ax.set_xticklabels([lab for _, lab, _ in keys], fontsize=8)
    ax.set_ylim(0, 1.02)
    ax.set_title(f"Clean vs shoved, N = {nmax}")
    ax.legend(fontsize=8)
    ax.grid(alpha=0.25, axis="y")
    fig.suptitle("Sim only: floating Shadow hand, instrument transfer (MuJoCo)", fontsize=9, color="#555")
    fig.tight_layout()
    out = RESULTS / f"chart{tag}.png"
    fig.savefig(out, dpi=130)
    print("wrote", out)
    return out


def stage_video(n: int, seed: int = 0, tag: str = "", max_scenes: int = 2) -> Path:
    """Side by side on shoved held-out scenes: object-transformed open-loop replay vs the learned policy."""
    import cv2
    import mujoco
    import torch

    from ..replay import _label, _VideoOut
    from .policy import Policy

    res = json.loads((RESULTS / f"results{tag}.json").read_text())
    e = res["by_n"][str(n)]
    pf, rf = e["policy_flags"][seed]["shove"], e["transformed_replay_flags"]["shove"]
    picks = [i for i, (p, r) in enumerate(zip(pf, rf)) if p and not r][:max_scenes]
    if not picks:
        picks = [i for i, p in enumerate(pf) if p][:1] or [0]
    policy = Policy.from_state(torch.load(WORK / f"policy{tag}_N{n}_s{seed}.pt", weights_only=False))
    profiles = load_profiles()
    demos = make_demos(profiles, n, DEMO_SEED)
    scenes = shove_scenes()
    W, H = 480, 400
    env_a, env_b = TransferEnv(), TransferEnv()
    ra, rb = mujoco.Renderer(env_a.m, H, W), mujoco.Renderer(env_b.m, H, W)
    cam = mujoco.MjvCamera()
    cam.distance, cam.azimuth, cam.elevation = 0.75, 145, -32
    out = RESULTS / f"replay_vs_policy{tag}.mp4"
    vid = _VideoOut(out, 1 / DT, (2 * W, H))

    def frame(env, r, title, color, t, shoved_at):
        cam.lookat[:] = [0.0, 0.16, 0.03]
        r.update_scene(env.d, cam)
        img = cv2.cvtColor(r.render(), cv2.COLOR_RGB2BGR)
        lines = [(title, color), (f"t = {t * DT:4.1f} s", (235, 235, 235))]
        if shoved_at is not None and t >= shoved_at:
            lines.append(("handle shoved at 1.0 s", (80, 160, 255)))
        if env.success():
            lines.append(("PLACED IN TRAY", (90, 220, 90)))
        _label(img, lines)
        return img

    for i in picks:
        sc = scenes[i]
        demo = nearest_demo(demos, sc)
        g, f = replay_commands(demo, sc, True)
        env_a.reset(sc, f[0])
        env_b.reset(sc)
        from .policy import Ensembler

        ens = Ensembler(CHUNK)
        obs = env_b.obs()
        sa, sb = np.random.default_rng(SHOVE_SEED + 1 + i), np.random.default_rng(SHOVE_SEED + 1 + i)
        done_a = done_b = False
        for t in range(MAX_STEPS):
            if t == SHOVE_STEP:
                env_a.shove(sa)
                env_b.shove(sb)
                obs = env_b.obs()
            if not done_a:
                k = min(t, len(g) - 1)
                env_a.set_command(g[k], f[k])
                done_a = env_a.success()
            if not done_b:
                obs = env_b.step(ens(t, policy.predict(obs)))
                done_b = env_b.success()
            img = np.hstack([frame(env_a, ra, "open-loop replay (object-transformed)", (60, 170, 255), t, SHOVE_STEP),
                             frame(env_b, rb, f"learned policy (BC, N={n} human demos)", (255, 200, 90), t, SHOVE_STEP)])
            vid.write(img)
            if done_a and done_b:
                break
        for _ in range(10):
            vid.write(img)
    vid.close()
    print("wrote", out, "scenes", picks)
    return out
