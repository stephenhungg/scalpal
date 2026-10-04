"""Live robot hand mirroring from the headset's MediaPipe joint stream, plus episode import.

The Quest app (Assets/Scalpal/Hands) sends one scalpal.hand_joints.v1 JSON datagram per camera frame
over UDP. `live` retargets each frame onto the Shadow hand and shows the robot next to the 2D joints.
`send` is a stand-in headset: it runs MediaPipe on a clip or webcam and sends the same datagrams.
`import-episode` turns a recorded headset episode (frames.jsonl) into robot motion for training.

Finger motion only, like the offline pipeline: the robot wrist stays fixed. Joints computed from
camera images are Meta Device User Data; record only with participant consent.
"""

from __future__ import annotations

import json
import socket
import sys
import time
from pathlib import Path

import cv2
import mujoco
import numpy as np

from .replay import VIEWS, VISUAL_GROUP, load_model, render_replay
from .retarget import LIMIT_EPS, bone_length_scales, build_retargeter, ref_vectors, retarget_frames, to_mano

HAND_JOINTS_SCHEMA = "scalpal.hand_joints.v1"
BONES = [
    (0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8), (9, 10), (10, 11), (11, 12),
    (13, 14), (14, 15), (15, 16), (0, 17), (17, 18), (18, 19), (19, 20), (5, 9), (9, 13), (13, 17),
]
CALIBRATION_FRAMES = 20


def pick_hand(frame: dict, want: str) -> dict | None:
    """The observation for the requested physical hand ("right"/"left"), else the most confident one."""
    hands = frame.get("hands") or []
    match = [h for h in hands if h.get("hand") == want.lower()]
    pool = match or hands
    return max(pool, key=lambda h: h.get("presence", 0.0)) if pool else None


class LiveRetargeter:
    """Per-frame retargeting with bone-length scales calibrated from the first frames of the stream."""

    def __init__(self, hand: str = "Right"):
        self.hand = hand
        self.retargeting, self.cfg = build_retargeter("shadow", hand, unit_scale=True)
        self.indices = self.retargeting.optimizer.target_link_human_indices
        self.limits = self.retargeting.optimizer.robot.joint_limits
        self.names = list(self.retargeting.joint_names)
        self.fixed = np.zeros(len(self.retargeting.optimizer.idx_pin2fixed))
        # Until calibrated, use the config's single scale; then per-vector bone-length scales.
        self.scales = np.full(len(self.cfg["target_task_link_names"]), 1.2)
        self.samples: list[dict] = []
        self.calibrated = False

    def __call__(self, world: np.ndarray) -> np.ndarray | None:
        """Robot joint targets, or None for an unusable frame (the caller holds the last pose).
        dex-retargeting's optimizer never returns on NaN input, so bad frames must not reach it."""
        world = np.asarray(world, float)
        if world.shape != (21, 3) or not np.all(np.isfinite(world)) or np.linalg.norm(world[9] - world[0]) < 0.02:
            return None
        mano = to_mano(world, self.hand)
        if not np.all(np.isfinite(mano)):
            return None
        if not self.calibrated:
            self.samples.append({"valid": True, "world": world})
            if len(self.samples) >= CALIBRATION_FRAMES:
                self.scales = bone_length_scales(self.samples, self.retargeting, self.cfg)
                self.calibrated = True
        ref = ref_vectors(mano, self.indices) * self.scales[:, None]
        qpos = self.retargeting.retarget(ref, fixed_qpos=self.fixed)
        return np.clip(qpos, self.limits[:, 0], self.limits[:, 1])

    def at_limit(self, qpos: np.ndarray) -> list[str]:
        return [n for n, q, (lo, hi) in zip(self.names, qpos, self.limits) if q <= lo + LIMIT_EPS or q >= hi - LIMIT_EPS]


def _draw_joints(panel: np.ndarray, obs: dict | None, width: int, height: int) -> None:
    if not obs or not width or not height:
        return
    h, w = panel.shape[:2]
    s = min(w / width, h / height)
    ox, oy = (w - width * s) / 2, (h - height * s) / 2
    px = np.asarray(obs["pixels"], float).reshape(21, 2)
    pts = [(int(ox + x * s), int(oy + y * s)) for x, y in px]
    good = obs.get("presence", 0) >= 0.9
    color = (120, 255, 160) if good else (40, 210, 255)
    for a, b in BONES:
        cv2.line(panel, pts[a], pts[b], color, 2, cv2.LINE_AA)
    for i, p in enumerate(pts):
        cv2.circle(panel, p, 5 if i == 0 else 3, (255, 255, 255), -1, cv2.LINE_AA)


def _text(img: np.ndarray, lines: list[str], color=(235, 235, 235)) -> None:
    for i, line in enumerate(lines):
        cv2.putText(img, line, (10, 24 + 22 * i), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 3, cv2.LINE_AA)
        cv2.putText(img, line, (10, 24 + 22 * i), cv2.FONT_HERSHEY_SIMPLEX, 0.55, color, 1, cv2.LINE_AA)


def run_live(
    port: int = 9123,
    hand: str = "Right",
    size: tuple[int, int] = (640, 480),
    record: Path | None = None,
    show: bool = True,
    max_frames: int | None = None,
    timeout_s: float | None = None,
) -> dict:
    """Window: headset joints (left) and the retargeted Shadow hand (right). q quits.
    show=False runs headless (for checks) and stops after max_frames datagrams or timeout_s."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", port))
    sock.setblocking(False)
    live = LiveRetargeter(hand)
    model = load_model(live.cfg["urdf_path"])
    data = mujoco.MjData(model)
    qadr = {mujoco.mj_id2name(model, mujoco.mjtObj.mjOBJ_JOINT, j): model.jnt_qposadr[j] for j in range(model.njnt)}
    order = [qadr[name] for name in live.names]
    w, h = size
    # The renderer needs a GL context, which background (headless) runs don't have.
    renderer = mujoco.Renderer(model, h, w) if show else None
    opt = mujoco.MjvOption()
    opt.geomgroup[:] = 0
    opt.geomgroup[VISUAL_GROUP] = 1
    cam = mujoco.MjvCamera()
    mujoco.mj_forward(model, data)
    cam.lookat[:] = data.xpos[1:].mean(axis=0)
    cam.azimuth, cam.elevation = VIEWS["back"]
    cam.distance = 0.45

    out = None
    if record:
        record.mkdir(parents=True, exist_ok=True)
        out = (record / "frames.jsonl").open("w")
    print(f"listening for {HAND_JOINTS_SCHEMA} on udp://0.0.0.0:{port} (q to quit)", file=sys.stderr, flush=True)
    frame, obs, received, last_rx = None, None, 0, 0.0
    rates: list[float] = []
    retargeted, began = 0, time.monotonic()
    try:
        while True:
            latest = None
            while True:  # drain the socket; render only the newest frame
                try:
                    payload, _ = sock.recvfrom(65535)
                except BlockingIOError:
                    break
                try:
                    latest = json.loads(payload)
                except json.JSONDecodeError:
                    continue
                received += 1
                if out:
                    out.write(payload.decode("utf-8") + "\n")
            if latest is not None:
                now = time.monotonic()
                if last_rx:
                    rates = (rates + [1.0 / max(now - last_rx, 1e-3)])[-30:]
                last_rx = now
                frame = latest
                obs = pick_hand(frame, hand)
                qpos = live(np.asarray(obs["worldModelMeters"], float).reshape(21, 3)) if obs is not None else None
                if qpos is not None:
                    data.qpos[order] = qpos
                    retargeted += 1
            if not show:
                if (max_frames and received >= max_frames) or (timeout_s and time.monotonic() - began > timeout_s):
                    break
                time.sleep(0.002)
                continue
            mujoco.mj_forward(model, data)
            renderer.update_scene(data, cam, scene_option=opt)
            robot = cv2.cvtColor(renderer.render(), cv2.COLOR_RGB2BGR)
            panel = np.full((h, w, 3), 24, np.uint8)
            if frame:
                _draw_joints(panel, obs, frame.get("imageWidth", 0), frame.get("imageHeight", 0))
            stale = not last_rx or time.monotonic() - last_rx > 1.0
            fps = float(np.median(rates)) if rates else 0.0
            _text(panel, [
                "headset MediaPipe joints" if not stale else "waiting for headset stream...",
                f"{fps:4.1f} frames/s  received {received}",
                f"{obs['hand']} hand  presence {obs['presence']:.2f}  {obs.get('source', '')}" if obs and not stale else "no hand",
            ])
            limits = live.at_limit(data.qpos[order]) if obs else []
            _text(robot, [
                f"Shadow hand ({hand.lower()}), kinematic mirror",
                "scales calibrated" if live.calibrated else f"calibrating scales {len(live.samples)}/{CALIBRATION_FRAMES}",
                *([f"at limit: {', '.join(limits[:4])}"] if limits else []),
                *(["RECORDING " + str(record)] if out else []),
            ])
            cv2.imshow("scalpal live hands", np.hstack([panel, robot]))
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break
    finally:
        if out:
            out.close()
        sock.close()
        if show:
            cv2.destroyAllWindows()
    return {
        "received": received,
        "retargeted": retargeted,
        "median_fps": float(np.median(rates)) if rates else 0.0,
        "calibrated": live.calibrated,
        "last_qpos": [float(q) for q in data.qpos[order]],
        "joint_names": live.names,
    }


def run_sender(source: str, host: str = "127.0.0.1", port: int = 9123, mirrored: bool = False, loop: bool = True) -> None:
    """Stand-in headset: MediaPipe on a clip (or webcam index) sent as scalpal.hand_joints.v1 datagrams."""
    import mediapipe as mp
    from mediapipe.tasks.python import BaseOptions, vision

    from .perception import ensure_model

    landmarker = vision.HandLandmarker.create_from_options(vision.HandLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=str(ensure_model())),
        running_mode=vision.RunningMode.VIDEO, num_hands=2,
    ))
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    cap = cv2.VideoCapture(int(source) if source.isdigit() else source)
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    index, start, t_ms = 0, time.monotonic(), 0
    print(f"sending {source} to udp://{host}:{port} (ctrl-c to stop)")
    while True:
        ok, bgr = cap.read()
        if not ok:
            if loop and not source.isdigit():
                cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
                continue
            break
        h, w = bgr.shape[:2]
        t_ms += int(1000 / fps)
        res = landmarker.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)), t_ms)
        hands = []
        for lm, world, handed in zip(res.hand_landmarks, res.hand_world_landmarks, res.handedness):
            label = handed[0].category_name
            # MediaPipe labels assume a mirrored image; flip for unmirrored footage (Quest passthrough, clips).
            physical = label.lower() if mirrored else ("right" if label == "Left" else "left")
            hands.append({
                "hand": physical,
                "handednessRaw": handed[0].score if label == "Left" else 1 - handed[0].score,
                "presence": handed[0].score,
                "source": "mediapipe-python",
                "depthMeters": 0.0,
                "pixels": [v for p in lm for v in (p.x * w, p.y * h)],
                "worldModelMeters": [v for p in world for v in (p.x, p.y, p.z)],
                "jointsWorld": [0.0] * 63,
            })
        frame = {
            "schema": HAND_JOINTS_SCHEMA, "sessionId": "sender", "frameIndex": index, "cameraTimestampMs": -1,
            "unityTime": time.monotonic() - start, "imageWidth": w, "imageHeight": h,
            "cameraPosition": [0, 0, 0], "cameraRotation": [0, 0, 0, 1], "inferenceMs": 0, "hands": hands,
        }
        sock.sendto(json.dumps(frame).encode(), (host, port))
        index += 1
        if not source.isdigit():
            time.sleep(max(0.0, start + index / fps - time.monotonic()))


def episode_frames(path: Path, hand: str) -> tuple[list[dict], dict]:
    """frames.jsonl (headset or `live --record`) -> the frame list retarget_frames takes."""
    lines = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    if not lines:
        raise SystemExit(f"{path} has no frames")
    t0 = lines[0].get("unityTime", 0.0)
    frames = []
    for f in lines:
        obs = pick_hand(f, hand)
        rec = {"frame": f["frameIndex"], "t_ms": round(1000 * (f.get("unityTime", 0.0) - t0), 1), "valid": obs is not None}
        if obs is not None:
            rec["world"] = np.asarray(obs["worldModelMeters"], float).reshape(21, 3).tolist()
            rec["image"] = np.asarray(obs["pixels"], float).reshape(21, 2).tolist()
            rec["presence"] = obs.get("presence")
        else:
            rec["reason"] = "no_hand_in_frame"
        frames.append(rec)
    meta = {"source": str(path), "frames": len(lines), "valid": sum(f["valid"] for f in frames)}
    return frames, meta


def import_episode(episode: Path, hand: str = "Right", out: Path | None = None, render: bool = True) -> Path:
    episode = Path(episode)
    jsonl = episode / "frames.jsonl" if episode.is_dir() else episode
    frames, meta = episode_frames(jsonl, hand)
    if not meta["valid"]:
        raise SystemExit(f"no {hand.lower()} hand in {jsonl}")
    motion = retarget_frames(frames, hand=hand)
    motion["source_episode"] = meta
    out = Path(out) if out else jsonl.parent / "robot"
    out.mkdir(parents=True, exist_ok=True)
    (out / "motion.json").write_text(json.dumps(motion))
    print(f"{meta['valid']}/{meta['frames']} frames with a {hand.lower()} hand -> {out / 'motion.json'}")
    if render:
        print("wrote", render_replay(motion, out / "replay.mp4"))
    return out
