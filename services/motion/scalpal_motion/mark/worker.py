"""`scalpal-motion robot-serve`: headset demos in the coach -> dataset -> BC policy -> graded rollouts -> replay -> result.

Coach routes used (services/preop/src/robot-routes.ts):
  GET  /coach/robot/demos?after=<demoId>        new demos, oldest first
  GET  /coach/robot/demos/:demoId               one demo (ControllerMotionCapture frames)
  PUT  /robot/replays/<file>.mp4                replay video (served back with Range support)
  POST /coach/robot/result                      synthetic-only baseline, shown until a session has its own result
  POST /coach/sessions/:sid/robot-result        the session's result for its newest demo

Every cycle: retarget the demo to the task space, grade the learner's own stroke with the shared predicates, add it
to the dataset when it passes, retrain (cached per exact dataset), evaluate K rollouts on the full arm + hand sim at
randomized patients, run the policy on this learner's patient for the replay, and post. Sim only.
"""

from __future__ import annotations

import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import numpy as np

from ..paths import MOTION_ROOT
from .demos import START_OFFSET, DemoRejected, MarkDemo, from_capture, synthetic_set
from .learn import cached_policy, evaluate, rollout
from .lerobot import export_mark_lerobot
from .render import render_rollout
from .scene import PATIENT_BASE_POS, MarkRobot, PatientPose

ROBOT_DIR = MOTION_ROOT / "out" / "robot"
CURVE_FILE = MOTION_ROOT / "learning-results" / "robot_mark_curve.json"
STEP_ID = "mark_incision"
STEP_TITLE = "Mark McBurney incision"
RESULT_SCHEMA = "scalpal.robot_result.v1"
NOTES = [
    "Simulation only (MuJoCo, kinematic arm IK, no contact dynamics); no physical robot.",
    "Synthetic demos are scripted, perturbed human-like strokes, not recordings of people.",
    "Graded with a Python port of the Quest's mark measurement and the catalog's mark_incision predicates.",
]


class Coach:
    def __init__(self, base: str, timeout: float = 10.0):
        self.base = base.rstrip("/")
        self.timeout = timeout

    def _req(self, method: str, path: str, body: bytes | None = None, ctype: str = "application/json") -> dict:
        req = urllib.request.Request(self.base + path, data=body, method=method, headers={"content-type": ctype, "accept": "application/json"})
        with urllib.request.urlopen(req, timeout=self.timeout) as r:
            raw = r.read()
            return json.loads(raw) if raw else {}

    def get(self, path: str) -> dict:
        return self._req("GET", path)

    def post(self, path: str, doc: dict) -> dict:
        return self._req("POST", path, json.dumps(doc).encode())

    def put_video(self, name: str, path: Path) -> str:
        return self._req("PUT", f"/robot/replays/{name}", path.read_bytes(), "video/mp4")["videoUrl"]


def result_doc(*, success: bool, path_error: float | None, ev: dict | None, human: int, synthetic: int, video_url: str | None,
               demo_id: str | None, details: dict, step_id: str = STEP_ID) -> dict:
    """The body for POST .../robot-result (the coach adds status and the step title)."""
    return {"schema": RESULT_SCHEMA, "stepId": step_id, "stepTitle": STEP_TITLE if step_id == STEP_ID else step_id,
            "success": bool(success), "pathErrorMm": None if path_error is None else round(float(path_error), 2),
            "policySuccessRate": None if ev is None else ev["successRate"], "demos": {"human": human, "synthetic": synthetic},
            "synthetic": human == 0, "videoUrl": video_url, "demoId": demo_id, "details": details}


class RobotWorker:
    def __init__(self, coach: Coach | None, root: Path = ROBOT_DIR, n_synthetic: int = 10, rollouts: int = 30, steps: int = 3000):
        self.coach = coach
        self.root = root
        self.n_synthetic = n_synthetic
        self.rollouts = rollouts
        self.steps = steps
        self.robot = MarkRobot()
        self.synthetic = synthetic_set(n_synthetic, seed=0)
        for d in ("human", "replays", "policies"):
            (root / d).mkdir(parents=True, exist_ok=True)
        self.state_file = root / "state.json"
        self.state = json.loads(self.state_file.read_text()) if self.state_file.exists() else {"cursor": "", "done": []}

    # -- dataset --------------------------------------------------------------------------
    def human_demos(self) -> list[tuple[MarkDemo, dict]]:
        out = []
        for f in sorted((self.root / "human").glob("*.json")):
            d = json.loads(f.read_text())
            out.append((MarkDemo.from_dict(d), d.get("grade", {})))
        return out

    def training_set(self) -> tuple[list[MarkDemo], int, int]:
        """Synthetic demos + every ingested demo that passed the grader. Returns (demos, headset count, synthetic count)."""
        passed = [d for d, g in self.human_demos() if g.get("success")]
        n_headset = sum(not d.synthetic for d in passed)
        return self.synthetic + passed, n_headset, len(self.synthetic) + len(passed) - n_headset

    def curve(self) -> dict | None:
        return json.loads(CURVE_FILE.read_text()) if CURVE_FILE.exists() else None

    # -- one cycle ------------------------------------------------------------------------
    def _train_eval(self, synthetic_only: bool = False) -> tuple:
        demos, n_human, n_synthetic = (self.synthetic, 0, len(self.synthetic)) if synthetic_only else self.training_set()
        t0 = time.perf_counter()
        policy, info = cached_policy(demos, self.root / "policies", steps=self.steps)
        train_s = time.perf_counter() - t0
        ev = evaluate(policy, self.robot, k=self.rollouts)
        return policy, info, ev, (n_human, n_synthetic), train_s

    def _publish_video(self, ro, ghost, name: str) -> tuple[str | None, dict]:
        path = self.root / "replays" / name
        t0 = time.perf_counter()
        info = render_rollout(self.robot, ro, ghost, path)
        info["renderS"] = round(time.perf_counter() - t0, 1)
        url = None
        if self.coach:
            try:
                url = self.coach.put_video(name, path)
            except (urllib.error.URLError, OSError) as err:
                print(f"robot-serve: replay upload failed ({err}); keeping {path}", file=sys.stderr)
        return url, info

    def baseline(self) -> dict:
        """Synthetic-only result so the headset panel is never empty."""
        t0 = time.perf_counter()
        policy, info, ev, (n_human, n_synthetic), train_s = self._train_eval(synthetic_only=True)
        ghost = synthetic_set(self.n_synthetic + 1, seed=0)[-1]  # held out of training
        ro = rollout(policy, self.robot, ghost.landmarks, PatientPose(PATIENT_BASE_POS.copy()), START_OFFSET)
        url, vid = self._publish_video(ro, ghost.stroke_points(), f"baseline-{info['key']}.mp4")
        details = {"showcase": {**ro.summary(), "ghost": "synthetic held-out demo"}, "evaluation": ev, "training": info,
                   "video": vid, "curve": self.curve(), "notes": NOTES, "cycleS": round(time.perf_counter() - t0, 1),
                   "trainS": round(train_s, 1)}
        doc = result_doc(success=ro.success, path_error=ro.path_error_mm, ev=ev, human=n_human, synthetic=n_synthetic,
                         video_url=url, demo_id=None, details=details)
        if self.coach:
            self.coach.post("/coach/robot/result", doc)
        (self.root / "baseline-result.json").write_text(json.dumps(doc, indent=1))
        return doc

    def process(self, doc: dict) -> dict:
        """One coach demo -> the session's result."""
        t0 = time.perf_counter()
        demo_id, sid, step = doc.get("demoId"), doc.get("sessionId"), doc.get("stepId")
        if step != STEP_ID:
            return result_doc(success=False, path_error=None, ev=None, human=0, synthetic=0, video_url=None, demo_id=demo_id, step_id=step,
                              details={"supported": False, "note": f"The simulated robot only learns {STEP_ID} so far.", "notes": NOTES})
        try:
            human = from_capture(doc)
        except DemoRejected as err:
            return result_doc(success=False, path_error=None, ev=None, human=0, synthetic=len(self.synthetic), video_url=None,
                              demo_id=demo_id, details={"rejected": str(err), "notes": NOTES})
        grade = human.grade()
        (self.root / "human" / f"{demo_id}.json").write_text(json.dumps({**human.to_dict(), "grade": grade}))
        policy, info, ev, (n_human, n_synthetic), train_s = self._train_eval()
        # The replay: the policy on this learner's patient (authored landmarks unless the frames say otherwise).
        ro = rollout(policy, self.robot, human.landmarks, PatientPose(PATIENT_BASE_POS.copy()), START_OFFSET)
        url, vid = self._publish_video(ro, human.stroke_points(), f"{demo_id}.mp4")
        self.export()
        details = {"humanDemo": {"source": human.source, "grade": grade, "usedForTraining": bool(grade["success"]), "retarget": human.meta},
                   "showcase": ro.summary(), "evaluation": ev, "training": info, "video": vid, "curve": self.curve(),
                   "notes": NOTES, "cycleS": round(time.perf_counter() - t0, 1), "trainS": round(train_s, 1)}
        return result_doc(success=ro.success, path_error=ro.path_error_mm, ev=ev, human=n_human, synthetic=n_synthetic,
                          video_url=url, demo_id=demo_id, details=details)

    def export(self) -> dict:
        demos = [(d, g.get("success", False)) for d, g in self.human_demos()] + [(d, True) for d in self.synthetic]
        return export_mark_lerobot(demos, self.root.parent / "lerobot" / "scalpal_robot_mark")

    def poll_once(self) -> list[dict]:
        assert self.coach
        listing = self.coach.get(f"/coach/robot/demos?after={self.state['cursor']}" if self.state["cursor"] else "/coach/robot/demos")
        posted = []
        for meta in listing.get("demos", []):
            demo_id = meta["demoId"]
            self.state["cursor"] = demo_id
            if demo_id in self.state["done"]:
                continue
            doc = self.coach.get(f"/coach/robot/demos/{demo_id}")
            result = self.process(doc)
            try:
                self.coach.post(f"/coach/sessions/{meta['sessionId']}/robot-result", result)
            except urllib.error.HTTPError as err:
                print(f"robot-serve: session {meta['sessionId']} gone ({err.code}); result kept locally", file=sys.stderr)
            (self.root / f"result-{demo_id}.json").write_text(json.dumps(result, indent=1))
            self.state["done"].append(demo_id)
            self.state_file.write_text(json.dumps(self.state))
            ev = result["details"].get("evaluation") or {}
            print(f"robot-serve: {demo_id} ({meta.get('stepId')}): robot {'SUCCESS' if result['success'] else 'miss'}, "
                  f"policy {ev.get('successRate')}, path {result['pathErrorMm']} mm, {result['details'].get('cycleS')} s", file=sys.stderr)
            posted.append(result)
        self.state_file.write_text(json.dumps(self.state))
        return posted


def serve(coach_url: str, interval: float = 2.0, once: bool = False, **kw) -> None:
    coach = Coach(coach_url)
    worker = RobotWorker(coach, **kw)
    while True:  # the coach may be restarting; the baseline must land before the panel needs it
        try:
            base = worker.baseline()
            print(f"robot-serve: baseline (synthetic only) policy {base['policySuccessRate']}, video {base['videoUrl']}, "
                  f"{base['details']['cycleS']} s", file=sys.stderr)
            break
        except (urllib.error.URLError, OSError) as err:
            print(f"robot-serve: coach unreachable for the baseline ({err}); retrying", file=sys.stderr)
            time.sleep(interval)
    last_baseline = time.time()
    while True:
        try:
            worker.poll_once()
            if time.time() - last_baseline > 60:  # re-post: a restarted coach forgets everything but the file
                coach.post("/coach/robot/result", json.loads((worker.root / "baseline-result.json").read_text()))
                last_baseline = time.time()
        except (urllib.error.URLError, OSError) as err:
            print(f"robot-serve: coach unreachable ({err})", file=sys.stderr)
        if once:
            return
        time.sleep(interval)
