"""Quest controllers drive the simulated robot hand.

The headset's ControllerMotionCapture streams scalpal.controller_motion.v1 frames (tracked pose, grip,
trigger, held instrument) over UDP. `teleop` maps one controller onto the floating Shadow hand of the
instrument-transfer task (learning/env.py) with physics on: controller motion moves the grip point,
squeezing closes the fingers. Each attempt is saved as an episode, and successful episodes are human
demonstrations with a real wrist path for `scalpal-motion learn sweep --teleop`.

Controller tracking is not camera or hand-tracking data, but recording still waits for --consented.
Simulation only.
"""

from __future__ import annotations

import json
import socket
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .learning.demos import APPROACH, CARRY, CLOSE, PRESHAPE, RELEASE, RETREAT, Demo
from .learning.env import DT, FINGER_ACTUATORS, HANDLE_RADIUS, HOME_GRIP, MAX_STEPS, Scene, TransferEnv, sample_scene, wrap
from .paths import MOTION_ROOT

CONTROLLER_SCHEMA = "scalpal.controller_motion.v1"
PROFILES = MOTION_ROOT / "learning-results" / "human_profiles.json"
TELEOP_DIR = MOTION_ROOT / "out" / "teleop"
TELEOP_MAX_STEPS = 3 * MAX_STEPS  # people are slower than the scripted demos: 30 s per attempt


def unity_to_mujoco(p) -> np.ndarray:
    """Unity (x right, y up, z forward; left-handed) to MuJoCo world (x forward, y left, z up)."""
    return np.array([p[2], -p[0], p[1]], dtype=float)


def unity_forward(q) -> np.ndarray:
    """The controller's forward axis (Unity +z) rotated by quaternion (x, y, z, w)."""
    x, y, z, w = q
    return np.array([2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)], dtype=float)


def controller_yaw(q) -> float:
    f = unity_to_mujoco(unity_forward(q))
    return float(np.arctan2(f[1], f[0]))


# Finger mapping, matched to the gloved hands in the Quest build (Quest/Runtime/ControllerHandPose.cs):
#   trigger          -> index finger (FF) curl
#   grip             -> middle, ring and little finger (MF, RF, LF) curl
#   held instrument  -> full grasp: every finger, thumb included, at the closed shape
#   thumb            -> follows the harder of grip and trigger (the glove's thumb does the same)
# 0 is the open pre-shape, 1 the grasp validated in physics. The glove's 0.2 rest curl is cosmetic and
# is not copied: an idle controller leaves the robot hand open.
FINGER_OF_ACTUATOR = np.array([a[:2] for a in FINGER_ACTUATORS])


def finger_curls(sample: dict) -> dict[str, float]:
    """Per-finger curl (0 open .. 1 closed) from one controller sample, as the glove poses it."""
    grip = float(np.clip(sample.get("grip", 0.0) or 0.0, 0.0, 1.0))
    trigger = float(np.clip(sample.get("trigger", 0.0) or 0.0, 0.0, 1.0))
    if sample.get("heldInstrument"):
        return {"TH": 1.0, "FF": 1.0, "MF": 1.0, "RF": 1.0, "LF": 1.0}
    return {"TH": max(grip, trigger), "FF": trigger, "MF": grip, "RF": grip, "LF": grip}


def curl_vector(curls: dict[str, float]) -> np.ndarray:
    """Per-actuator blend weight (18,) in FINGER_ACTUATORS order."""
    return np.array([curls[f] for f in FINGER_OF_ACTUATOR], dtype=float)


def closure(sample: dict) -> float:
    """How closed the hand is overall (phase detection): 1 when holding an instrument, else the harder
    of grip and trigger."""
    return float(max(finger_curls(sample).values()))


def grasp_shapes() -> tuple[np.ndarray, np.ndarray]:
    """Open pre-shape and the closed grasp validated in physics by the learning experiment."""
    if PROFILES.exists():
        prof = json.loads(PROFILES.read_text())["profiles"][0]
        return np.array(prof["open"], float), np.array(prof["hold"], float)
    return PRESHAPE.copy(), PRESHAPE.copy() + 1.0


def pick_controller(frame: dict, hand: str) -> dict | None:
    for c in frame.get("controllers") or []:
        if c.get("hand") == hand.lower() and c.get("tracked"):
            return c
    return None


class ControllerMapper:
    """Controller pose -> grip command. The first tracked pose (or a re-anchor) maps to the hand's home."""

    def __init__(self, home: np.ndarray, scale: float = 1.0):
        self.home = np.asarray(home, float)
        self.scale = scale
        self.anchor: tuple[np.ndarray, float] | None = None

    def reanchor(self) -> None:
        self.anchor = None

    def __call__(self, sample: dict) -> np.ndarray:
        p = unity_to_mujoco(sample["position"])
        yaw = controller_yaw(sample["rotation"])
        if self.anchor is None:
            self.anchor = (p, yaw)
        p0, yaw0 = self.anchor
        grip = np.r_[self.home + self.scale * (p - p0), wrap(yaw - yaw0)]
        grip[2] = max(grip[2], -0.005)
        return grip


@dataclass
class Episode:
    scene: Scene
    grip: list
    finger: list
    closure: list
    obj: list
    success: bool = False

    def to_dict(self) -> dict:
        return {
            "schema": "scalpal.teleop_episode.v1",
            "scene": self.scene.to_dict(),
            "control_dt_s": DT,
            "grip": np.round(self.grip, 5).tolist(),
            "finger": np.round(self.finger, 4).tolist(),
            "closure": np.round(self.closure, 3).tolist(),
            "object": np.round(self.obj, 5).tolist(),
            "success": bool(self.success),
            "source": "Quest controller teleoperation (grip pose from controller pose, fingers from grip/trigger)",
        }


class TeleopSession:
    """One controller drives the hand; call step() once per control period with the newest frame."""

    def __init__(self, hand: str = "right", seed: int = 0, env: TransferEnv | None = None):
        self.hand = hand
        self.env = env or TransferEnv()
        self.rng = np.random.default_rng(seed)
        self.open, self.closed = grasp_shapes()
        self.episodes: list[Episode] = []
        self.new_scene()

    def new_scene(self, scene: Scene | None = None) -> None:
        self.scene = scene or sample_scene(self.rng)
        self.env.reset(self.scene, self.open)
        # The controller's starting pose maps to this scene's hand home.
        self.mapper = ControllerMapper(self.scene.home)
        self.ep = Episode(self.scene, [], [], [], [])

    def step(self, frame: dict | None) -> dict:
        sample = pick_controller(frame, self.hand) if frame else None
        if sample is None:
            # Lost tracking: hold the last command rather than inventing motion.
            grip = self.env.grip_cmd.copy()
            c = self.ep.closure[-1] if self.ep.closure else 0.0
            finger = self.ep.finger[-1].copy() if self.ep.finger else self.open.copy()
        else:
            grip = self.mapper(sample)
            c = closure(sample)
            finger = self.open + curl_vector(finger_curls(sample)) * (self.closed - self.open)
        self.env.set_command(grip, finger)
        self.ep.grip.append(grip)
        self.ep.finger.append(finger)
        self.ep.closure.append(c)
        self.ep.obj.append(self.env.obj_pose())
        done = self.env.success() or len(self.ep.grip) >= TELEOP_MAX_STEPS
        return {"tracked": sample is not None, "closure": c, "success": self.env.success(), "done": done,
                "lift_m": float(self.env.obj_pose()[2] - HANDLE_RADIUS), "steps": len(self.ep.grip)}

    def finish(self, out_dir: Path | None = None) -> Episode:
        self.ep.success = bool(self.env.success())
        self.episodes.append(self.ep)
        if out_dir:
            out_dir.mkdir(parents=True, exist_ok=True)
            path = out_dir / f"{time.strftime('%Y%m%dT%H%M%S')}-{len(self.episodes):03d}.json"
            path.write_text(json.dumps(self.ep.to_dict()))
        ep = self.ep
        self.new_scene()
        return ep


def episode_to_demo(data: dict, demo_id: int) -> Demo:
    """A successful teleop episode as a learning Demo: the real wrist path and finger commands, with
    phases and object/tray frame weights from when the hand closed and opened (for the MimicGen transform)."""
    sc = data["scene"]
    scene = Scene(np.array(sc["obj_xy"]), float(sc["obj_yaw"]), np.array(sc["tray_xy"]), np.array(sc["home"]))
    grip, finger = np.array(data["grip"], float), np.array(data["finger"], float)
    c, obj = np.array(data["closure"], float), np.array(data["object"], float)
    T = len(grip)
    closed = np.flatnonzero(c > 0.5)
    t_close = int(closed[0]) if len(closed) else T // 3
    after = np.flatnonzero(c[t_close:] < 0.5)
    t_rel = t_close + int(after[0]) if len(after) else T - 1
    phase = np.full(T, RETREAT)
    phase[:t_close] = APPROACH
    phase[t_close:t_rel] = CARRY
    phase[t_close:min(t_close + 5, t_rel)] = CLOSE
    phase[t_rel:min(t_rel + 5, T)] = RELEASE
    w = np.zeros((T, 3))
    for t in range(T):
        if t < t_close:
            s = t / max(t_close, 1)
            w[t] = [1 - s, s, 0]
        elif t < t_rel:
            s = (t - t_close) / max(t_rel - t_close, 1)
            w[t] = [0, 1 - s, s]
        else:
            w[t] = [0, 0, 1]
    k = max(t_rel - 1, 0)
    inhand = obj[k, :2] - grip[k, :2]
    ref = {"grip": grip, "finger": finger, "phase": phase, "w": w, "inhand": inhand}
    return Demo(demo_id, -1, scene, 1.0, ref)


def load_teleop_demos(folder: Path) -> list[Demo]:
    files = sorted(Path(folder).glob("*.json"))
    good = [json.loads(f.read_text()) for f in files]
    good = [d for d in good if d.get("schema") == "scalpal.teleop_episode.v1" and d.get("success")]
    if not good:
        raise SystemExit(f"no successful teleop episodes in {folder}")
    return [episode_to_demo(d, i) for i, d in enumerate(good)]


def run_teleop(port: int = 9124, hand: str = "right", out_dir: Path | None = None, show: bool = True,
               max_seconds: float | None = None, seed: int = 0) -> list[Episode]:
    """Window: the robot hand following the controller. n = new scene (saves the attempt), a = re-anchor, q = quit."""
    import cv2
    import mujoco

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", port))
    sock.setblocking(False)
    session = TeleopSession(hand, seed)
    renderer = cam = None
    if show:
        renderer = mujoco.Renderer(session.env.m, 480, 640)
        cam = mujoco.MjvCamera()
        cam.lookat[:] = [0.0, 0.16, 0.05]
        cam.distance, cam.azimuth, cam.elevation = 0.9, 200.0, -35.0
    print(f"listening for {CONTROLLER_SCHEMA} on udp://0.0.0.0:{port} ({hand} controller)", file=sys.stderr, flush=True)
    latest, began, next_tick = None, time.monotonic(), time.monotonic()
    status = {}
    try:
        while True:
            while True:
                try:
                    payload, _ = sock.recvfrom(65535)
                    latest = json.loads(payload)
                except BlockingIOError:
                    break
                except json.JSONDecodeError:
                    continue
            now = time.monotonic()
            if max_seconds and now - began > max_seconds:
                break
            if now < next_tick:
                time.sleep(min(0.005, next_tick - now))
                continue
            next_tick += DT
            status = session.step(latest)
            if status["done"]:
                ep = session.finish(out_dir)
                print(f"attempt {len(session.episodes)}: {'SUCCESS' if ep.success else 'no success'} ({len(ep.grip)} steps)", file=sys.stderr)
            if show:
                renderer.update_scene(session.env.d, cam)
                img = cv2.cvtColor(renderer.render(), cv2.COLOR_RGB2BGR)
                lines = [
                    f"controller {'tracked' if status['tracked'] else 'LOST (holding pose)'}   grip {status['closure']:.2f}",
                    f"lift {100 * status['lift_m']:.1f} cm   attempt {len(session.episodes) + 1}   successes {sum(e.success for e in session.episodes)}",
                    "n new scene   a re-anchor   q quit",
                ]
                for i, line in enumerate(lines):
                    cv2.putText(img, line, (10, 24 + 22 * i), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 3, cv2.LINE_AA)
                    cv2.putText(img, line, (10, 24 + 22 * i), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (235, 235, 235), 1, cv2.LINE_AA)
                cv2.imshow("scalpal teleop", img)
                key = cv2.waitKey(1) & 0xFF
                if key == ord("q"):
                    break
                if key == ord("n"):
                    session.finish(out_dir)
                if key == ord("a"):
                    session.mapper.reanchor()
    finally:
        sock.close()
        if show:
            cv2.destroyAllWindows()
    return session.episodes


def scripted_controller_frames(env: TransferEnv, demo: Demo, hand: str = "right") -> list[dict]:
    """Controller frames that would reproduce a reference demo through ControllerMapper (inverse mapping).
    Used by `send-controller --scripted` and the tests to drive teleop without a headset."""
    grip = demo.ref["grip"]
    fing = demo.ref["finger"]
    open_, closed = grasp_shapes()
    span = np.maximum(np.abs(closed - open_), 1e-6)
    frames = []
    for t, (g, f) in enumerate(zip(grip, fing)):
        d = g[:3] - HOME_GRIP
        p_unity = [-d[1], d[2], d[0]]  # inverse of unity_to_mujoco, anchored at the origin
        yaw = g[3]
        q = [0.0, np.sin(-yaw / 2), 0.0, np.cos(-yaw / 2)]  # yaw about Unity's up axis maps to MuJoCo yaw
        c = float(np.clip(np.median((f - open_) / span), 0, 1))
        frames.append({"schema": CONTROLLER_SCHEMA, "frameIndex": t, "unityTime": t * DT,
                       "controllers": [{"hand": hand, "tracked": True, "position": p_unity, "rotation": q,
                                        "grip": c, "trigger": c, "heldInstrument": ""}]})
    return frames


def run_controller_sender(host: str = "127.0.0.1", port: int = 9124, source: str | None = None, seed: int = 0) -> None:
    """Stand-in headset: replay a recorded controller frames.jsonl, or (no source) a scripted reach-and-place."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    if source:
        frames = [json.loads(l) for l in Path(source).read_text().splitlines() if l.strip()]
    else:
        from .learning.demos import make_demos
        from .learning.evaluate import load_profiles

        demo = make_demos(load_profiles(), 1, seed)[0]
        frames = scripted_controller_frames(TransferEnv(), demo)
        print("scripted demo scene:", json.dumps(demo.scene.to_dict()), file=sys.stderr)
    start = time.monotonic()
    for i, f in enumerate(frames):
        sock.sendto(json.dumps(f).encode(), (host, port))
        time.sleep(max(0.0, start + (i + 1) * DT - time.monotonic()))
    print(f"sent {len(frames)} frames", file=sys.stderr)
