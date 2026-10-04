"""Proof-of-learning experiment: env determinism, success detector, human closure extraction, data shapes, training."""

import numpy as np
import pytest

from scalpal_motion.learning.env import MENAGERIE_HAND

needs_model = pytest.mark.skipif(not MENAGERIE_HAND.exists(), reason="Menagerie Shadow hand not fetched (see learning README)")

JOINTS = ['WRJ2', 'WRJ1', 'FFJ4', 'FFJ3', 'FFJ2', 'FFJ1', 'LFJ5', 'LFJ4', 'LFJ3', 'LFJ2', 'LFJ1', 'MFJ4', 'MFJ3', 'MFJ2',
          'MFJ1', 'RFJ4', 'RFJ3', 'RFJ2', 'RFJ1', 'THJ5', 'THJ4', 'THJ3', 'THJ2', 'THJ1']


def _motion(flex_curve, dt=0.033):
    """A motion record whose finger J2/J3 follow flex_curve (rad); other joints at 0."""
    q = np.zeros((len(flex_curve), len(JOINTS)))
    for j in ("FFJ3", "FFJ2", "MFJ3", "MFJ2", "RFJ3", "RFJ2", "LFJ3", "LFJ2"):
        q[:, JOINTS.index(j)] = flex_curve
    return {"path": "synthetic", "names": JOINTS, "t": np.arange(len(q)) * dt, "valid": np.ones(len(q), bool), "q": q}


def test_extract_profiles_finds_one_full_closure_and_skips_partial_curl():
    from scalpal_motion.learning.demos import extract_profiles

    t = np.arange(300) * 0.033
    flex = 0.2 + 1.2 * np.exp(-((t - 6.0) / 0.5) ** 2) + 0.3 * np.exp(-((t - 2.0) / 0.3) ** 2)
    profiles, report = extract_profiles(_motion(flex))
    assert len(report["events"]) == 1 and report["events"][0]["used"]
    assert abs(report["events"][0]["t_onset_s"] - 5.6) < 0.4
    p = profiles[0]
    assert len(p["hold"]) == 18 and len(p["close"]) > 3 and len(p["release"]) > 3
    assert max(p["hold"]) <= 1.6 + 1e-9  # coupled distal flexion capped for contact


def test_gaps_split_runs_and_are_not_filled():
    from scalpal_motion.learning.demos import _resample

    t = np.arange(100) * 0.033
    valid = np.ones(100, bool)
    valid[40:55] = False  # ~0.5 s gap
    q = np.random.default_rng(0).normal(size=(100, 3))
    runs = _resample(t, q, valid)
    assert len(runs) == 2
    assert runs[0][0][-1] <= t[39] + 1e-9 and runs[1][0][0] >= t[55] - 1e-9


def test_transform_reference_moves_grasp_with_object():
    from scalpal_motion.learning.demos import DESCEND, source_reference, transform_reference
    from scalpal_motion.learning.env import Scene

    prof = {"open": [0.0] * 18, "close": [[0.0] * 18, [1.0] * 18], "hold": [1.0] * 18, "release": [[1.0] * 18, [0.0] * 18]}
    src = Scene(np.array([0.0, 0.0]), 0.0, np.array([0.0, 0.32]))
    ref = source_reference(src, prof)
    g = transform_reference(ref, src, (0.05, -0.03, 0.2), (0.02, 0.30), start=src.home)
    k = int(np.flatnonzero(ref["phase"] == DESCEND)[-1])
    assert np.allclose(g[k, :2], [0.05, -0.03], atol=1e-6) and np.isclose(g[k, 3], 0.2)
    assert np.allclose(g[0, :3], src.home, atol=1e-3)
    assert np.allclose(g[-1, :2] - ref["grip"][-1, :2], [0.02, -0.02], atol=1e-6)


@needs_model
def test_env_reset_is_deterministic_per_seed():
    from scalpal_motion.learning.env import OBS_DIM, TransferEnv, sample_scene

    env = TransferEnv()
    a = env.reset(sample_scene(np.random.default_rng(3)))
    b = env.reset(sample_scene(np.random.default_rng(3)))
    c = env.reset(sample_scene(np.random.default_rng(4)))
    assert a.shape == (OBS_DIM,) and np.array_equal(a, b) and not np.array_equal(a, c)


@needs_model
def test_success_detector():
    from scalpal_motion.learning.env import ACT_DIM, HANDLE_RADIUS, TRAY_HALF, Scene, TransferEnv

    env = TransferEnv()
    sc = Scene(np.array([0.0, 0.0]), 0.0, np.array([0.0, 0.32]))
    env.reset(sc)
    for _ in range(10):
        env.step(np.zeros(ACT_DIM))
    assert not env.success() and not env.grasped()
    env.reset(sc)
    env.d.qpos[env.obj_q:env.obj_q + 3] = [0.01, 0.33, HANDLE_RADIUS + 2 * TRAY_HALF[2] + 0.001]
    for _ in range(10):
        env.step(np.zeros(ACT_DIM))
    assert env.success()


@needs_model
def test_dataset_shapes_and_tiny_training_run():
    from scalpal_motion.learning.demos import CHUNK, chunk_labels, expert_episode, make_demos
    from scalpal_motion.learning.env import ACT_DIM, OBS_DIM, TransferEnv
    from scalpal_motion.learning.policy import Ensembler, train

    t = np.arange(300) * 0.033
    from scalpal_motion.learning.demos import extract_profiles

    profiles, _ = extract_profiles(_motion(0.2 + 1.2 * np.exp(-((t - 6.0) / 0.5) ** 2)))
    env = TransferEnv()
    demo = make_demos(profiles, 1, 0, env)[0]
    ep = expert_episode(env, demo, demo.scene, np.random.default_rng(0), dart=True)
    T = len(ep["obs"])
    assert ep["obs"].shape == (T, OBS_DIM) and ep["a0"].shape == (T, ACT_DIM)
    act = chunk_labels(ep["a0"], ep["inc"])
    assert act.shape == (T, CHUNK, ACT_DIM)
    policy, info = train({"obs": ep["obs"], "act": act}, steps=3, batch=32, threads=1)
    chunk = policy.predict(ep["obs"][0])
    assert chunk.shape == (CHUNK, ACT_DIM) and np.isfinite(chunk).all()
    ens = Ensembler()
    assert ens(0, chunk).shape == (ACT_DIM,)
