"""mark_incision robot: grader parity with the Quest + coach, IK reach, demo ingestion, result schema, rollouts."""

import json
import re
import shutil
import subprocess
from pathlib import Path

import numpy as np
import pytest

from scalpal_motion.mark.demos import DemoRejected, from_capture, synthetic_demo, synthetic_set, to_capture_frames
from scalpal_motion.mark.grader import MARK_INCISION_PREDICATES, Landmarks, check, measure_marks, measure_stroke
from scalpal_motion.mark.scene import FETCH_HINT, MENAGERIE_PANDA
from scalpal_motion.learning.env import MENAGERIE_HAND

GOLDEN = Path(__file__).resolve().parents[2] / "preop" / "test" / "fixtures" / "robot-mark-strokes.json"
needs_models = pytest.mark.skipif(not (MENAGERIE_PANDA.exists() and MENAGERIE_HAND.exists()), reason=f"fetch models: {FETCH_HINT}")


def test_wound_frame_matches_the_quest():
    # OpenSurgerySession: wound at Lerp(rightAsis, umbilicus, 1/3), rotation Euler(90, 0, -35). Its local +x (the
    # reference line) lands on patient (cos35, 0, -sin35) and local +z (inward normal) on patient -Y (posterior).
    lm = Landmarks.authored()
    assert np.allclose(lm.mcburney, [-0.13 * 2 / 3, -0.01, -0.14 * 2 / 3])
    assert np.allclose(lm.axis, [np.cos(np.radians(35)), 0, -np.sin(np.radians(35))])
    assert np.allclose(lm.normal, [0, -1, 0])


def test_grader_reaches_the_same_numbers_and_verdicts_as_the_golden_strokes():
    # The same file drives services/preop/test/robot-mark-golden.test.ts through the TS reducer and catalog.
    golden = json.loads(GOLDEN.read_text())
    assert [(p["fact"], p["op"], p["value"]) for p in golden["predicates"]] == MARK_INCISION_PREDICATES
    for case in golden["cases"]:
        lm = Landmarks(np.array(case["landmarks"]["rightAsis"]), np.array(case["landmarks"]["umbilicus"]))
        facts = measure_marks([np.array(case["points"])], lm)
        for k, v in case["expected"].items():
            assert facts[k] == pytest.approx(v, abs=0.05), (case["name"], k)
        ok, failed = check(facts)
        assert (ok, failed) == (case["passes"], case["failed"]), case["name"]


def test_the_last_stroke_is_graded_and_no_stroke_never_passes():
    lm = Landmarks.authored()
    good = [lm.mcburney + lm.axis * s for s in np.linspace(-0.03, 0.03, 20)]
    stub = [lm.mcburney + lm.axis * s for s in np.linspace(0, 0.01, 5)]
    assert check(measure_marks([np.array(good)], lm))[0]
    assert check(measure_marks([np.array(good), np.array(stub)], lm))[1] == ["markLengthMm"]  # a re-touch overwrites the mark
    assert check({})[1] == ["marked", "markErrorMm", "markLengthMm", "markAngleDegrees"]
    assert measure_stroke(good[:1], lm) == {}


def test_capture_frames_become_the_same_stroke_and_grade():
    # Frames carry no landmarks: like the Quest without registered ASIS/umbilicus, the authored ones apply.
    demo = synthetic_demo(np.random.default_rng(3), Landmarks.authored())
    frames = to_capture_frames(demo)
    got = from_capture({"demoId": "demo-abc123", "sessionId": "coach-x", "stepId": "mark_incision", "frames": frames})
    assert got.source == "stand-in" and got.synthetic and got.meta["hand"] == "right"
    real = from_capture({"frames": [dict(f, sessionId="quest-1a2b") for f in frames]})
    assert real.source == "headset" and not real.synthetic
    assert len(got.tip) == len(demo.tip)
    assert np.abs(got.tip[:, [0, 2]] - demo.tip[:, [0, 2]]).max() < 1e-4  # x/z exact; y via the fitted marker offset
    assert got.grade()["facts"] == pytest.approx(demo.grade()["facts"], abs=0.5)
    assert got.grade()["success"] == demo.grade()["success"]


def test_capture_ingestion_uses_the_marker_hand_and_never_bridges_long_gaps():
    demo = synthetic_demo(np.random.default_rng(4))
    frames = to_capture_frames(demo)
    for f in frames:  # marker in the LEFT hand this time
        f["controllers"][0], f["controllers"][1] = ({**f["controllers"][1], "hand": "left"}, {**f["controllers"][0], "hand": "right"})
    assert from_capture({"frames": frames}).meta["hand"] == "left"
    gap = [dict(f, unityTime=f["unityTime"] + (2.0 if i >= 40 else 0)) for i, f in enumerate(frames)]
    cut = from_capture({"frames": gap})
    assert cut.meta["truncatedAtGap"] and len(cut.tip) == 40
    with pytest.raises(DemoRejected):
        from_capture({"frames": [dict(f, space="world") for f in frames]})


def test_synthetic_demos_are_labelled_and_pass_the_grader():
    demos = synthetic_set(5, seed=1)
    assert all(d.synthetic and d.source == "synthetic" and d.demo_id.startswith("syn-") for d in demos)
    assert all(d.grade()["success"] for d in demos)


@needs_models
def test_ik_reaches_the_incision_line_at_randomized_patient_poses():
    from scalpal_motion.mark.scene import MarkRobot, onto_skin, sample_patient_pose, skin_normal_out

    robot = MarkRobot()
    lm = Landmarks.authored()
    rng = np.random.default_rng(0)
    for _ in range(8):
        robot.reset(sample_patient_pose(rng))
        robot.ik(lm.mcburney + np.array([0, 0.1, 0]), iters=200)
        for s in np.linspace(-0.035, 0.035, 15):
            p = onto_skin(lm.mcburney + lm.axis * s)
            assert robot.ik(p) < 1e-3
            marker = robot.d.site_xmat[robot.tip].reshape(3, 3)[:, 2]
            inward = -(robot.patient.R @ skin_normal_out(p))
            assert np.degrees(np.arccos(np.clip(marker @ inward, -1, 1))) < 2.0  # marker held into the skin


@needs_models
def test_the_arm_executing_a_good_stroke_passes_and_a_bad_one_fails():
    from scalpal_motion.mark.learn import replay_commands
    from scalpal_motion.mark.scene import PATIENT_BASE_POS, MarkRobot, PatientPose

    robot = MarkRobot()
    good = synthetic_set(1, seed=2)[0]
    r = replay_commands(robot, good, PatientPose(PATIENT_BASE_POS.copy(), 0.2))
    assert r["success"] and r["maxIkErrorMm"] < 1.0
    bad = synthetic_set(1, seed=2)[0]
    bad.tip = bad.tip + np.array([0.03, 0, 0])  # the whole stroke 3 cm toward the patient's left
    assert replay_commands(robot, bad, PatientPose(PATIENT_BASE_POS.copy()))["failed"] == ["markErrorMm"]


@needs_models
def test_short_rollout_and_worker_cycle_produce_a_coach_result(tmp_path):
    from scalpal_motion.mark.worker import RobotWorker

    worker = RobotWorker(None, root=tmp_path, n_synthetic=3, rollouts=2, steps=150)
    demo = synthetic_demo(np.random.default_rng(9), Landmarks.authored())
    doc = {"schema": "scalpal.robot_demo.v1", "demoId": "demo-test0001", "sessionId": "coach-abcdef", "stepId": "mark_incision",
           "frames": to_capture_frames(demo)}
    result = worker.process(doc)
    # Exactly what services/preop/src/robot-routes.ts accepts on POST /coach/sessions/:sid/robot-result.
    assert result["stepId"] == "mark_incision" and isinstance(result["success"], bool) and isinstance(result["synthetic"], bool)
    assert set(result["demos"]) == {"human", "synthetic"}
    # to_capture_frames is the stand-in headset: its stroke is synthetic and must never be counted as human.
    assert result["details"]["humanDemo"]["source"] == "stand-in"
    assert result["demos"] == {"human": 0, "synthetic": 3 + int(demo.grade()["success"])} and result["synthetic"] is True
    assert result["demoId"] == "demo-test0001"
    assert result["pathErrorMm"] is None or result["pathErrorMm"] >= 0
    assert 0 <= result["policySuccessRate"] <= 1
    assert result["videoUrl"] is None  # no coach to upload to; the file is local
    ev = result["details"]["evaluation"]
    assert ev["rollouts"] == 2 and ev["medianIkErrorMm"] < 1.0
    assert result["details"]["humanDemo"]["grade"]["success"] == demo.grade()["success"]
    video = Path(result["details"]["video"]["path"])
    assert video.exists() and result["details"]["video"]["seconds"] <= 15
    if shutil.which("ffprobe"):
        probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "stream=codec_name,pix_fmt,width,height", "-of", "csv=p=0", str(video)],
                               capture_output=True, text=True).stdout
        assert probe.strip() == "h264,1280,720,yuv420p"
    other = worker.process({**doc, "demoId": "demo-test0002", "stepId": "incise_skin"})
    assert other["details"]["supported"] is False and other["success"] is False and other["videoUrl"] is None
    assert re.fullmatch(r"[a-z0-9_]+", other["stepId"])
