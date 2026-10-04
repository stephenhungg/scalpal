"""Teleop frames stamped with the Scalpal coach's surgery step (fake coach over real HTTP)."""

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest

from scalpal_motion.coach import ROBOT_ATTEMPT_SCHEMA, CoachPoller, attempt_summary, label_from_snapshot
from scalpal_motion.learning.env import MENAGERIE_HAND

needs_model = pytest.mark.skipif(not MENAGERIE_HAND.exists(), reason="Menagerie Shadow hand not fetched")


def snapshot(sid, step_id, title, held=(), outcome="in_progress"):
    return {
        "sessionId": sid, "procedureId": "lap_appendectomy", "procedureTitle": "Laparoscopic appendectomy",
        "stepNumber": 3, "stepCount": 9, "status": "active", "mistakeCount": 0,
        "step": {"id": step_id, "title": title},
        "held": [{"hand": h, "instrumentId": i, "name": i, "touching": {"id": "", "name": ""}} for h, i in held],
        "condition": {"outcome": {"result": outcome, "cause": "", "at": ""}},
    }


class FakeCoach:
    """GET /coach/current, GET /coach/sessions/:sid, POST /coach/sessions/:sid/robot-attempts."""

    def __init__(self):
        self.sid = "coach-0123456789abcdef"
        self.snap = snapshot(self.sid, "expose_appendix", "Expose the appendix", held=[("right", "inst_atraumatic_grasper")])
        self.posted = []
        self.gets = 0
        fake = self

        class H(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def _json(self, code, obj):
                b = json.dumps(obj).encode()
                self.send_response(code)
                self.send_header("content-type", "application/json")
                self.send_header("content-length", str(len(b)))
                self.end_headers()
                self.wfile.write(b)

            def do_GET(self):
                fake.gets += 1
                if self.path.startswith("/coach/current"):
                    return self._json(200, {"sessionId": fake.sid, "patientId": "patient-demo-sparse", "procedureId": "lap_appendectomy"})
                if self.path == f"/coach/sessions/{fake.sid}":
                    return self._json(200, {"snapshot": fake.snap})
                return self._json(404, {"error": {"code": "coach_session_not_found"}})

            def do_POST(self):
                n = int(self.headers.get("content-length", 0))
                fake.posted.append((self.path, json.loads(self.rfile.read(n))))
                self._json(201, {"ok": True})

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), H)
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()


@pytest.fixture
def coach():
    c = FakeCoach()
    yield c
    c.close()


def test_label_from_snapshot_keeps_step_tools_and_outcome():
    lab = label_from_snapshot(snapshot("coach-x", "clip_artery", "Clip the appendiceal artery",
                                       held=[("left", "inst_clip_applier")], outcome="completed"))
    assert lab["stepId"] == "clip_artery" and lab["stepTitle"] == "Clip the appendiceal artery"
    assert lab["held"] == [{"hand": "left", "instrumentId": "inst_clip_applier", "name": "inst_clip_applier"}]
    assert lab["outcome"] == "completed" and lab["procedureId"] == "lap_appendectomy"
    assert label_from_snapshot({})["stepId"] == ""  # a completed or empty snapshot does not crash


def test_poller_resolves_current_session_and_follows_step_changes(coach):
    p = CoachPoller(coach.url)
    assert p.latest() is None
    assert p.poll_once()["stepId"] == "expose_appendix"
    assert p.session_id == coach.sid
    coach.snap = snapshot(coach.sid, "clip_artery", "Clip the appendiceal artery")
    p.poll_once()
    lab = p.latest()
    assert lab["stepTitle"] == "Clip the appendiceal artery" and not lab["stale"] and lab["held"] == []


def test_pinned_unknown_session_and_dead_coach_give_no_label():
    assert CoachPoller("http://127.0.0.1:9", session_id="coach-nope").poll_once() is None
    p = CoachPoller("http://127.0.0.1:9")
    assert p.poll_once() is None and p.latest() is None and p.failures == 1


def test_label_goes_stale_when_the_coach_stops_answering(coach):
    t = [100.0]
    p = CoachPoller(coach.url, clock=lambda: t[0])
    p.poll_once()
    t[0] += 3.0
    assert p.latest()["stale"]


def test_background_polling_runs_about_every_250_ms(coach):
    import time

    p = CoachPoller(coach.url, session_id=coach.sid).start()
    time.sleep(1.1)
    p.stop()
    assert 3 <= p.polls <= 6 and p.latest()["stepId"] == "expose_appendix"


@needs_model
def test_teleop_frames_and_attempt_are_stamped_and_summary_posted(coach, tmp_path):
    import numpy as np

    from scalpal_motion.teleop import TeleopSession

    poller = CoachPoller(coach.url)
    poller.poll_once()
    session = TeleopSession("right", seed=0, coach=poller)
    frame = {"controllers": [{"hand": "right", "tracked": True, "position": [0, 0, 0], "rotation": [0, 0, 0, 1],
                              "grip": 0.2, "trigger": 0.0, "heldInstrument": ""}]}
    for _ in range(3):
        session.step(frame)
    coach.snap = snapshot(coach.sid, "clip_artery", "Clip the appendiceal artery", held=[("right", "inst_clip_applier")])
    poller.poll_once()
    for _ in range(2):
        session.step(frame)
    session.finish(tmp_path)
    poller.stop()

    data = json.loads(next(tmp_path.glob("*.json")).read_text())
    steps = [f["stepId"] for f in data["coach"]]
    assert steps == ["expose_appendix"] * 3 + ["clip_artery"] * 2
    assert len(data["wrist"]) == len(data["joints"]) == len(data["t"]) == len(data["grip"]) == 5
    assert np.all(np.diff(data["t"]) >= 0)

    assert len(coach.posted) == 1
    path, body = coach.posted[0]
    assert path == f"/coach/sessions/{coach.sid}/robot-attempts"
    assert body["schema"] == ROBOT_ATTEMPT_SCHEMA and body["stepId"] == "clip_artery"
    assert [s["stepId"] for s in body["stepsDuringAttempt"]] == ["expose_appendix", "clip_artery"]
    assert body["heldInstruments"] == ["inst_atraumatic_grasper", "inst_clip_applier"]
    assert body["frames"] == 5 and body["success"] is False and body["labeledFraction"] == 1.0


def test_attempt_summary_without_coach_labels():
    s = attempt_summary({"grip": [[0, 0, 0, 0]] * 4, "coach": [None] * 4, "success": True, "control_dt_s": 0.05}, "a1")
    assert s["stepId"] == "" and s["labeledFraction"] == 0.0 and s["durationS"] == 0.2 and s["success"]
