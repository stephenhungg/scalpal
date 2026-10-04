"""Quest controller frames (scalpal.controller_motion.v1) -> simulated robot hand -> training demos."""

import json

import numpy as np

from scalpal_motion.learning.demos import make_demos
from scalpal_motion.learning.env import TransferEnv
from scalpal_motion.learning.evaluate import load_profiles
from scalpal_motion.teleop import (
    ControllerMapper, TeleopSession, closure, controller_yaw, curl_vector, episode_to_demo, finger_curls,
    load_teleop_demos, pick_controller, scripted_controller_frames, unity_to_mujoco,
)
from scalpal_motion.learning.env import FINGER_ACTUATORS


def test_unity_axes_map_to_mujoco_z_up():
    assert np.allclose(unity_to_mujoco([1, 2, 3]), [3, -1, 2])  # forward -> x, right -> -y, up -> z
    assert np.isclose(controller_yaw([0, 0, 0, 1]), 0.0)
    a = np.deg2rad(30)
    assert np.isclose(controller_yaw([0, np.sin(-a / 2), 0, np.cos(-a / 2)]), a)


def test_mapper_anchors_first_pose_to_home_and_follows_motion():
    home = np.array([-0.08, 0.0, 0.15])
    m = ControllerMapper(home)
    q = [0, 0, 0, 1]
    assert np.allclose(m({"position": [0.1, 1.2, 0.3], "rotation": q})[:3], home)
    moved = m({"position": [0.1, 1.25, 0.4], "rotation": q})  # 5 cm up, 10 cm forward
    assert np.allclose(moved[:3], home + [0.10, 0.0, 0.05])


def test_finger_mapping_matches_the_quest_gloves():
    # Trigger curls only the index; grip curls middle, ring and little; the thumb follows the harder one.
    trig = finger_curls({"grip": 0.0, "trigger": 1.0, "heldInstrument": ""})
    assert trig == {"TH": 1.0, "FF": 1.0, "MF": 0.0, "RF": 0.0, "LF": 0.0}
    grip = finger_curls({"grip": 0.6, "trigger": 0.0})
    assert grip == {"TH": 0.6, "FF": 0.0, "MF": 0.6, "RF": 0.6, "LF": 0.6}
    # A held instrument closes the full grasp whatever the analog inputs say.
    held = finger_curls({"grip": 0.0, "trigger": 0.0, "heldInstrument": "inst_scalpel"})
    assert set(held.values()) == {1.0}
    assert finger_curls({}) == {"TH": 0.0, "FF": 0.0, "MF": 0.0, "RF": 0.0, "LF": 0.0}
    w = curl_vector(trig)
    for name, weight in zip(FINGER_ACTUATORS, w):
        assert weight == (1.0 if name[:2] in ("FF", "TH") else 0.0), name


def test_trigger_moves_only_index_actuators_in_the_sim():
    session = TeleopSession("right", seed=2)
    frame = {"controllers": [{"hand": "right", "tracked": True, "position": [0, 0, 0], "rotation": [0, 0, 0, 1],
                              "grip": 0.0, "trigger": 1.0, "heldInstrument": ""}]}
    session.step(frame)
    cmd = session.ep.finger[-1]
    for name, value, lo, hi in zip(FINGER_ACTUATORS, cmd, session.open, session.closed):
        expected = hi if name[:2] in ("FF", "TH") else lo
        assert np.isclose(value, expected), name


def test_closure_and_controller_pick():
    assert closure({"grip": 0.3, "trigger": 0.8}) == 0.8
    assert closure({"grip": 0.0, "trigger": 0.0, "heldInstrument": "inst_needle_driver"}) == 1.0
    frame = {"controllers": [{"hand": "left", "tracked": True}, {"hand": "right", "tracked": False}]}
    assert pick_controller(frame, "right") is None
    assert pick_controller(frame, "left")["hand"] == "left"


def test_scripted_controller_places_the_handle_and_becomes_a_demo(tmp_path):
    demo = make_demos(load_profiles(), 1, seed=0)[0]
    session = TeleopSession("right", seed=0)
    session.new_scene(demo.scene)
    frames = scripted_controller_frames(TransferEnv(), demo)
    for f in frames:
        if session.step(f)["done"]:
            break
    # A person holds still after letting go; give the handle time to settle in the tray.
    for _ in range(30):
        if session.env.success():
            break
        session.step(frames[-1])
    ep = session.finish(tmp_path)
    assert ep.success
    data = json.loads(next(tmp_path.glob("*.json")).read_text())
    assert data["schema"] == "scalpal.teleop_episode.v1" and data["success"]
    d = episode_to_demo(data, 0)
    T = len(data["grip"])
    assert d.ref["grip"].shape == (T, 4) and d.ref["finger"].shape == (T, 18) and d.ref["w"].shape == (T, 3)
    assert np.allclose(d.ref["w"].sum(1), 1.0)
    assert len(load_teleop_demos(tmp_path)) == 1


def test_lost_tracking_holds_the_last_command():
    session = TeleopSession("right", seed=1)
    before = session.env.grip_cmd.copy()
    status = session.step({"controllers": []})
    assert not status["tracked"]
    assert np.allclose(session.env.grip_cmd, before)
