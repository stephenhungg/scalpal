"""Presentation must preserve evidence, provenance and continuous contact."""
from types import SimpleNamespace

import numpy as np

from scalpal_motion.mark.render import FPS, frame_plan, grade_rows, source_caption


def test_grade_uses_midpoint_fact_and_never_invents_pass():
    ro = SimpleNamespace(success=True, failed=[], path_error_mm=999,
                         facts={'markErrorMm': 2.4, 'markLengthMm': 63.2, 'markAngleDegrees': 4.5})
    rows = grade_rows(ro)
    assert [r[1] for r in rows] == [2.4, 63.2, 4.5]
    assert all(r[-1] for r in rows)
    ro.success = False
    ro.failed = ['marked']
    assert not any(r[-1] for r in grade_rows(ro))
    ro.facts = {}
    assert all(r[1] is None and not r[-1] for r in grade_rows(ro))


def test_provenance_distinguishes_stand_in_and_held_out():
    assert 'Held-out synthetic' in source_caption('synthetic-held-out')
    assert '0 headset' in source_caption('synthetic-held-out')
    assert 'not a headset recording' in source_caption('stand-in')
    assert 'retargeted' in source_caption('headset')
    assert 'unverified' in source_caption('unknown')


def test_timing_keeps_contact_runs_and_two_second_result_hold():
    ro = SimpleNamespace(success=True, arm_q=np.zeros((150, 7)), drawing=[False]*40+[True]*60+[False]*50)
    plan, start, finish = frame_plan(ro)
    assert 8 <= len(plan)/FPS <= 10
    action = plan[round(start*FPS):round(finish*FPS)]
    assert action[0] < 40 and action[-1] > 99
    assert np.all(np.diff(action) > 0)
    assert np.max(np.diff(action)) <= 1  # no skipped control tick during this normal stroke
    assert np.all(plan[-2*FPS:] == action[-1])


def test_failed_late_lift_keeps_complete_rollout_tail():
    ro = SimpleNamespace(success=False, arm_q=np.zeros((260, 7)),
                         drawing=[False]*40+[True]*60+[False]*160)
    plan, start, finish = frame_plan(ro)
    assert plan[0] == 0 and plan[-1] == 259
    assert len(plan)/FPS <= 10
    assert np.all(np.diff(plan) >= 0)
    ro.success = True
    assert frame_plan(ro)[0][-1] == 259  # milestone success does not prove lift completion
