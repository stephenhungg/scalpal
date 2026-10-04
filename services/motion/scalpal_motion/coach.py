"""Surgery-step labels from the Scalpal coach for robot-hand teleop recordings.

The coach service (services/preop, default http://127.0.0.1:8787) owns the live surgery state. While
teleop runs, CoachPoller asks it about every 250 ms which step the learner is on, which instruments are
held, and the case outcome, and teleop stamps each recorded control frame with the newest answer. The
coach is optional: when it is down or has no session, frames are stamped with no label and teleop keeps
running.

Routes read (services/preop/src/coach-routes.ts):
  GET /coach/current[?patientId=]   -> {sessionId, patientId, procedureId}  (newest live session)
  GET /coach/sessions/:sid          -> {snapshot: CoachSnapshot, ...}

Route written (proposed, not in services/preop yet; failures are ignored):
  POST /coach/sessions/:sid/robot-attempts   body: scalpal.robot_attempt.v1 (see attempt_summary)
"""

from __future__ import annotations

import json
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

POLL_S = 0.25
RESOLVE_S = 2.0  # how often an unpinned poller re-asks /coach/current for the newest session
STALE_S = 2.0
TIMEOUT_S = 0.5
ROBOT_ATTEMPT_SCHEMA = "scalpal.robot_attempt.v1"


def _get(url: str, timeout: float = TIMEOUT_S) -> dict:
    with urllib.request.urlopen(urllib.request.Request(url, headers={"accept": "application/json"}), timeout=timeout) as r:
        return json.loads(r.read())


def label_from_snapshot(snapshot: dict) -> dict:
    """The parts of a CoachSnapshot a robot-hand frame is labeled with."""
    step = snapshot.get("step") or {}
    outcome = ((snapshot.get("condition") or {}).get("outcome")) or {}
    return {
        "sessionId": snapshot.get("sessionId", ""),
        "procedureId": snapshot.get("procedureId", ""),
        "procedureTitle": snapshot.get("procedureTitle", ""),
        "stepId": step.get("id", ""),
        "stepTitle": step.get("title", ""),
        "stepNumber": snapshot.get("stepNumber"),
        "stepCount": snapshot.get("stepCount"),
        "status": snapshot.get("status", ""),
        "held": [{"hand": h.get("hand", ""), "instrumentId": h.get("instrumentId", ""), "name": h.get("name", "")}
                 for h in snapshot.get("held") or []],
        "outcome": outcome.get("result", ""),
        "outcomeCause": outcome.get("cause", ""),
        "mistakeCount": snapshot.get("mistakeCount"),
    }


class CoachPoller:
    """Polls the coach on a background thread; latest() is the newest label (or None) plus its age."""

    def __init__(self, base_url: str, session_id: str | None = None, patient_id: str | None = None,
                 interval_s: float = POLL_S, clock=time.monotonic):
        self.base = base_url.rstrip("/")
        self.pinned = session_id
        self.session_id = session_id
        self.patient_id = patient_id
        self.interval_s = interval_s
        self.clock = clock
        self._label: dict | None = None
        self._at: float | None = None
        self._resolved_at = -1e9
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._thread: threading.Thread | None = None
        self._warned = False
        self._posts: list[threading.Thread] = []
        self.polls = 0
        self.failures = 0

    # -- polling -------------------------------------------------------------------------------
    def _resolve(self) -> None:
        q = f"?patientId={urllib.parse.quote(self.patient_id)}" if self.patient_id else ""
        self.session_id = _get(f"{self.base}/coach/current{q}")["sessionId"]
        self._resolved_at = self.clock()

    def poll_once(self) -> dict | None:
        """One request cycle. Returns the new label, or None when the coach had nothing (kept quiet)."""
        self.polls += 1
        try:
            if not self.pinned and (self.session_id is None or self.clock() - self._resolved_at > RESOLVE_S):
                self._resolve()
            snap = _get(f"{self.base}/coach/sessions/{urllib.parse.quote(self.session_id)}")["snapshot"]
        except (urllib.error.URLError, OSError, KeyError, ValueError, TypeError) as e:
            self.failures += 1
            if not self.pinned:
                self.session_id = None
            if not self._warned:
                print(f"coach at {self.base} unavailable ({e}); frames get no step label until it answers",
                      file=sys.stderr, flush=True)
                self._warned = True
            return None
        label = label_from_snapshot(snap)
        with self._lock:
            self._label, self._at = label, self.clock()
        if self._warned:
            print(f"coach answering: session {label['sessionId']} step {label['stepId'] or '-'}", file=sys.stderr, flush=True)
            self._warned = False
        return label

    def _run(self) -> None:
        while not self._stop.is_set():
            started = self.clock()
            self.poll_once()
            self._stop.wait(max(0.0, self.interval_s - (self.clock() - started)))

    def start(self) -> "CoachPoller":
        self._thread = threading.Thread(target=self._run, name="coach-poller", daemon=True)
        self._thread.start()
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(timeout=2)
        for t in self._posts:  # let a just-saved attempt's summary go out before the process exits
            t.join(timeout=2.5)

    # -- reading -------------------------------------------------------------------------------
    def latest(self) -> dict | None:
        """Newest label with age_s and stale (older than STALE_S); None before the first answer."""
        with self._lock:
            if self._label is None:
                return None
            age = self.clock() - self._at
            return {**self._label, "age_s": round(age, 3), "stale": age > STALE_S}

    # -- dashboard hook ------------------------------------------------------------------------
    def post_attempt(self, summary: dict) -> None:
        """Fire-and-forget POST of an attempt summary to the coach. Never raises, never blocks teleop."""
        sid = summary.get("sessionId") or self.session_id
        if not sid:
            return

        def send() -> None:
            url = f"{self.base}/coach/sessions/{urllib.parse.quote(sid)}/robot-attempts"
            req = urllib.request.Request(url, data=json.dumps(summary).encode(), method="POST",
                                         headers={"content-type": "application/json"})
            try:
                with urllib.request.urlopen(req, timeout=2.0) as r:
                    r.read()
            except (urllib.error.URLError, OSError, ValueError) as e:
                print(f"attempt summary not posted to the coach ({e}); the attempt is still saved locally",
                      file=sys.stderr, flush=True)

        t = threading.Thread(target=send, name="coach-attempt-post", daemon=True)
        t.start()
        self._posts = [p for p in self._posts if p.is_alive()] + [t]


def attempt_summary(episode: dict, attempt_id: str) -> dict:
    """scalpal.robot_attempt.v1: what the dashboard needs about one saved teleop attempt."""
    frames = episode.get("coach") or []
    labels = [f for f in frames if f]
    last = labels[-1] if labels else {}
    steps: list[dict] = []
    for f in labels:
        if f.get("stepId") and (not steps or steps[-1]["stepId"] != f["stepId"]):
            steps.append({"stepId": f["stepId"], "stepTitle": f.get("stepTitle", "")})
    held = sorted({h["instrumentId"] for f in labels for h in f.get("held", []) if h.get("instrumentId")})
    return {
        "schema": ROBOT_ATTEMPT_SCHEMA,
        "attemptId": attempt_id,
        "sessionId": last.get("sessionId", ""),
        "procedureId": last.get("procedureId", ""),
        "stepId": last.get("stepId", ""),
        "stepTitle": last.get("stepTitle", ""),
        "stepsDuringAttempt": steps,
        "heldInstruments": held,
        "caseOutcome": last.get("outcome", ""),
        "task": "instrument_transfer",
        "success": bool(episode.get("success")),
        "frames": len(episode.get("grip") or []),
        "durationS": round(len(episode.get("grip") or []) * float(episode.get("control_dt_s", 0.05)), 2),
        "labeledFraction": round(len(labels) / max(len(frames), 1), 3) if frames else 0.0,
        "maxLiftM": episode.get("max_lift_m", 0.0),
        "source": "simulated Shadow hand driven by Quest controller teleop",
        "createdAt": episode.get("started_at", ""),
    }
