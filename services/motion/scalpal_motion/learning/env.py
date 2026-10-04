"""Instrument-transfer task in MuJoCo: a floating Shadow hand moves a capsule handle into a tray.

The hand is MuJoCo Menagerie's right Shadow hand (position actuators, tuned contacts). Its
forearm gets a free joint welded to a mocap body, so the wrist is driven as a 4-DoF pose
(grip-point position plus yaw about vertical, palm facing down) and the fingers by their own
18 position actuators. The two wrist joints are held at 0. Physics, contact, and gravity are on.

Everything here is simulation. Nothing in this file touches video or a real robot.
"""

from __future__ import annotations

from dataclasses import dataclass, field

import mujoco
import numpy as np

from ..paths import MOTION_ROOT

MENAGERIE_HAND = MOTION_ROOT / "models" / "menagerie" / "shadow_hand" / "right_hand.xml"
FETCH_HINT = (
    "git clone --depth 1 --filter=blob:none --sparse https://github.com/google-deepmind/mujoco_menagerie.git "
    "models/menagerie && git -C models/menagerie sparse-checkout set shadow_hand"
)

DT = 0.05  # control period (20 Hz)
PHYS_DT = 0.002
SUBSTEPS = int(round(DT / PHYS_DT))
MAX_STEPS = 200  # 10 s episode limit

HANDLE_RADIUS = 0.012
HANDLE_HALF = 0.05  # 12.4 cm long capsule overall, a scalpel-handle stand-in
HANDLE_MASS = 0.03
TRAY_HALF = (0.07, 0.09, 0.003)  # tray floor half-extents; rim walls 1.5 cm high around it
TRAY_RIM = 0.015

# Grip point (where the closed fingers and thumb meet, palm down) relative to the forearm origin
# at yaw 0, found by the scripted-lift gate sweep (see README).
GRIP_OFFSET = np.array([0.36, 0.0, -0.064])
PALM_DOWN = np.array([0.0, 1.0, 0.0, 0.0])  # mocap quat at yaw 0: 180 deg about x, so the palm faces -z
FOREARM_Q0 = np.array([0.0, 1.0, 0.0, 1.0]) / np.sqrt(2)  # Menagerie's forearm mount rotation (fingers along +x)

# Initial-condition distribution (meters, radians).
OBJ_CENTER = np.array([0.0, 0.0])
OBJ_BOX = 0.10  # training box: +/-10 cm (20 x 20 cm)
OOD_RING = 0.05  # out-of-distribution ring: 10 to 15 cm from center
OBJ_YAW = np.deg2rad(30)
TRAY_CENTER = np.array([0.0, 0.32])
TRAY_BOX = 0.05
HOME_GRIP = np.array([-0.08, 0.0, 0.15])
HOME_JITTER = 0.02

# Finger actuators in Menagerie order (wrist actuators rh_A_WRJ2/1 excluded, held at 0).
FINGER_ACTUATORS = [
    "THJ5", "THJ4", "THJ3", "THJ2", "THJ1", "FFJ4", "FFJ3", "FFJ0", "MFJ4", "MFJ3", "MFJ0",
    "RFJ4", "RFJ3", "RFJ0", "LFJ5", "LFJ4", "LFJ3", "LFJ0",
]
FINGER_JOINTS = [  # the 22 finger joints observed (same names as motion.json minus WRJ1/2)
    "FFJ4", "FFJ3", "FFJ2", "FFJ1", "LFJ5", "LFJ4", "LFJ3", "LFJ2", "LFJ1", "MFJ4", "MFJ3", "MFJ2",
    "MFJ1", "RFJ4", "RFJ3", "RFJ2", "RFJ1", "THJ5", "THJ4", "THJ3", "THJ2", "THJ1",
]
OBS_DIM = 22 + 3 + 2 + 4 + 3 + 2 + 3 + 2 + 1
ACT_DIM = 4 + len(FINGER_ACTUATORS)
MAX_DPOS = 0.015  # wrist translation per control step (m)
MAX_DYAW = np.deg2rad(4)  # wrist yaw per control step


def human_to_actuators(names: list[str], qpos: np.ndarray) -> np.ndarray:
    """Map retargeted Shadow joint angles (motion.json naming) to the 18 finger actuator targets.

    J1 and J2 of FF/MF/RF/LF are tendon-coupled in Menagerie, so their target is J1 + J2.
    """
    q = dict(zip(names, np.asarray(qpos, dtype=float)))
    out = []
    for a in FINGER_ACTUATORS:
        if a.endswith("J0"):
            out.append(q[a[:2] + "J1"] + q[a[:2] + "J2"])
        else:
            out.append(q[a])
    return np.array(out)


def yaw_quat(yaw: float) -> np.ndarray:
    return np.array([np.cos(yaw / 2), 0.0, 0.0, np.sin(yaw / 2)])


def _quat_mul(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    out = np.zeros(4)
    mujoco.mju_mulQuat(out, a, b)
    return out


def _rot_z(yaw: float) -> np.ndarray:
    c, s = np.cos(yaw), np.sin(yaw)
    return np.array([[c, -s, 0.0], [s, c, 0.0], [0.0, 0.0, 1.0]])


def wrap(a):
    return (np.asarray(a) + np.pi) % (2 * np.pi) - np.pi


def wrap_half(a):
    """Capsule yaw is symmetric under 180 deg; wrap to [-pi/2, pi/2)."""
    return (np.asarray(a) + np.pi / 2) % np.pi - np.pi / 2


def build_model() -> mujoco.MjModel:
    if not MENAGERIE_HAND.exists():
        raise SystemExit(f"Menagerie Shadow hand not found at {MENAGERIE_HAND}. From services/motion run:\n  {FETCH_HINT}")
    spec = mujoco.MjSpec.from_file(str(MENAGERIE_HAND))
    spec.option.timestep = PHYS_DT
    # spec.option.integrator = mujoco.mjtIntegrator.mjINT_IMPLICITFAST
    forearm = spec.body("rh_forearm")
    forearm.add_freejoint(name="hand_free")
    for b in spec.bodies:
        if b.name.startswith("rh_"):
            b.gravcomp = 1.0
    for g in forearm.geoms:  # the forearm is a mount, not part of the grasp
        g.contype = 0
        g.conaffinity = 0
    R = np.zeros(9)
    mujoco.mju_quat2Mat(R, _quat_mul(PALM_DOWN, FOREARM_Q0))
    forearm.add_site(name="grip", pos=list(R.reshape(3, 3).T @ GRIP_OFFSET), size=[0.004, 0, 0], rgba=[0, 1, 0, 0])
    spec.add_texture(name="grid", type=mujoco.mjtTexture.mjTEXTURE_2D, builtin=mujoco.mjtBuiltin.mjBUILTIN_CHECKER,
                     rgb1=[0.82, 0.84, 0.86], rgb2=[0.76, 0.78, 0.80], width=300, height=300)
    spec.add_material(name="table", textures=["", "grid"], texrepeat=[8, 8], texuniform=True)
    w = spec.worldbody
    w.add_light(pos=[0, 0, 1.5], dir=[0, 0, -1], type=mujoco.mjtLightType.mjLIGHT_DIRECTIONAL)
    w.add_light(pos=[0.5, -0.5, 1.0], dir=[-0.5, 0.5, -1])
    w.add_geom(name="table", type=mujoco.mjtGeom.mjGEOM_PLANE, size=[1, 1, 0.05], material="table", friction=[1.0, 0.01, 0.001])
    tray = w.add_body(name="tray", mocap=True)
    tray.add_geom(name="tray", type=mujoco.mjtGeom.mjGEOM_BOX, size=list(TRAY_HALF), pos=[0, 0, TRAY_HALF[2]],
                  rgba=[0.25, 0.55, 0.85, 1], friction=[1.0, 0.01, 0.001])
    hx, hy, t = TRAY_HALF[0], TRAY_HALF[1], 0.003
    for name, size, pos in [("rim_n", [hx + t, t, TRAY_RIM / 2], [0, hy + t, TRAY_RIM / 2]),
                            ("rim_s", [hx + t, t, TRAY_RIM / 2], [0, -hy - t, TRAY_RIM / 2]),
                            ("rim_e", [t, hy, TRAY_RIM / 2], [hx + t, 0, TRAY_RIM / 2]),
                            ("rim_w", [t, hy, TRAY_RIM / 2], [-hx - t, 0, TRAY_RIM / 2])]:
        tray.add_geom(name=name, type=mujoco.mjtGeom.mjGEOM_BOX, size=size, pos=pos, rgba=[0.2, 0.45, 0.75, 1])
    w.add_body(name="wrist_target", mocap=True)
    handle = w.add_body(name="handle", pos=[0, 0, HANDLE_RADIUS])
    handle.add_freejoint(name="handle_free")
    handle.add_geom(name="handle", type=mujoco.mjtGeom.mjGEOM_CAPSULE, size=[HANDLE_RADIUS, HANDLE_HALF, 0],
                    quat=[np.cos(np.pi / 4), np.sin(np.pi / 4), 0, 0], mass=HANDLE_MASS, friction=[1.5, 0.02, 0.002],
                    condim=4, rgba=[0.85, 0.32, 0.22, 1])
    eq = spec.add_equality(type=mujoco.mjtEq.mjEQ_WELD, name1="wrist_target", name2="rh_forearm", objtype=mujoco.mjtObj.mjOBJ_BODY)
    eq.solref = [0.02, 1.0]
    eq.data = np.r_[0, 0, 0, 0, 0, 0, FOREARM_Q0, 1.0]  # forearm pose = mocap pose * FOREARM_Q0
    model = spec.compile()
    model.vis.global_.offwidth = max(model.vis.global_.offwidth, 960)
    model.vis.global_.offheight = max(model.vis.global_.offheight, 720)
    return model


@dataclass
class Scene:
    """One initial condition: handle pose, tray position, and starting grip pose."""

    obj_xy: np.ndarray
    obj_yaw: float
    tray_xy: np.ndarray
    home: np.ndarray = field(default_factory=lambda: HOME_GRIP.copy())

    def to_dict(self) -> dict:
        return {"obj_xy": [round(float(v), 4) for v in self.obj_xy], "obj_yaw": round(float(self.obj_yaw), 4),
                "tray_xy": [round(float(v), 4) for v in self.tray_xy], "home": [round(float(v), 4) for v in self.home]}


def sample_scene(rng: np.random.Generator, ood: bool = False) -> Scene:
    if ood:  # uniform in the 5 cm ring just outside the training box
        while True:
            xy = rng.uniform(-(OBJ_BOX + OOD_RING), OBJ_BOX + OOD_RING, 2)
            if np.abs(xy).max() > OBJ_BOX:
                break
    else:
        xy = rng.uniform(-OBJ_BOX, OBJ_BOX, 2)
    return Scene(
        obj_xy=OBJ_CENTER + xy,
        obj_yaw=float(rng.uniform(-OBJ_YAW, OBJ_YAW)),
        tray_xy=TRAY_CENTER + rng.uniform(-TRAY_BOX, TRAY_BOX, 2),
        home=HOME_GRIP + np.r_[rng.uniform(-HOME_JITTER, HOME_JITTER, 3)],
    )


class TransferEnv:
    """Gym-like wrapper. Action: [dx, dy, dz, dyaw, 18 finger targets]; the wrist command integrates."""

    def __init__(self, model: mujoco.MjModel | None = None):
        self.m = model if model is not None else build_model()
        self.d = mujoco.MjData(self.m)
        m = self.m
        self.hand_q = m.jnt_qposadr[m.joint("hand_free").id]
        self.hand_v = m.jnt_dofadr[m.joint("hand_free").id]
        self.obj_q = m.jnt_qposadr[m.joint("handle_free").id]
        self.obj_v = m.jnt_dofadr[m.joint("handle_free").id]
        self.finger_qadr = np.array([m.jnt_qposadr[m.joint("rh_" + j).id] for j in FINGER_JOINTS])
        self.finger_act = np.array([m.actuator("rh_A_" + a).id for a in FINGER_ACTUATORS])
        self.wrist_act = np.array([m.actuator("rh_A_WRJ2").id, m.actuator("rh_A_WRJ1").id])
        self.ctrl_lo = m.actuator_ctrlrange[self.finger_act, 0]
        self.ctrl_hi = m.actuator_ctrlrange[self.finger_act, 1]
        self.handle_geom = m.geom("handle").id
        self.table_geom = m.geom("table").id
        self.tray_geom = m.geom("tray").id
        self.hand_bodies = {i for i in range(m.nbody) if m.body(i).name.startswith("rh_")}
        self.th_body = m.body("rh_thdistal").id
        self.ff_body = m.body("rh_ffdistal").id
        self.mocap_hand = m.body("wrist_target").mocapid[0]
        self.mocap_tray = m.body("tray").mocapid[0]
        self.grip_site = m.site("grip").id
        self.open_ctrl = np.clip(np.zeros(len(FINGER_ACTUATORS)), self.ctrl_lo, self.ctrl_hi)

    # -- state ------------------------------------------------------------------------------
    def reset(self, scene: Scene, finger_ctrl: np.ndarray | None = None) -> np.ndarray:
        m, d = self.m, self.d
        mujoco.mj_resetData(m, d)
        self.scene = scene
        self.t = 0
        self.grip_cmd = np.r_[scene.home, 0.0]  # commanded grip pose (x, y, z, yaw)
        self.max_lift = 0.0
        self.settled = 0
        d.qpos[self.obj_q:self.obj_q + 3] = [scene.obj_xy[0], scene.obj_xy[1], HANDLE_RADIUS]
        d.qpos[self.obj_q + 3:self.obj_q + 7] = yaw_quat(scene.obj_yaw)
        d.mocap_pos[self.mocap_tray] = [scene.tray_xy[0], scene.tray_xy[1], 0.0]
        fc = self.open_ctrl if finger_ctrl is None else np.clip(finger_ctrl, self.ctrl_lo, self.ctrl_hi)
        d.ctrl[self.finger_act] = fc
        self._set_finger_qpos(fc)
        self._apply_wrist(teleport=True)
        mujoco.mj_forward(m, d)
        return self.obs()

    def _set_finger_qpos(self, ctrl: np.ndarray) -> None:
        q = dict(zip(FINGER_ACTUATORS, ctrl))
        for j, adr in zip(FINGER_JOINTS, self.finger_qadr):
            if j[2:] in ("J1", "J2") and j[:2] != "TH":
                self.d.qpos[adr] = q[j[:2] + "J0"] / 2
            else:
                self.d.qpos[adr] = q[j]

    def _forearm_pose(self, grip: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """Mocap pose for a grip pose; the weld keeps the forearm at mocap * FOREARM_Q0."""
        pos = grip[:3] - _rot_z(grip[3]) @ GRIP_OFFSET
        quat = _quat_mul(yaw_quat(grip[3]), PALM_DOWN)
        return pos, quat

    def _apply_wrist(self, teleport: bool = False) -> None:
        pos, quat = self._forearm_pose(self.grip_cmd)
        self.d.mocap_pos[self.mocap_hand] = pos
        self.d.mocap_quat[self.mocap_hand] = quat
        if teleport:
            self.d.qpos[self.hand_q:self.hand_q + 3] = pos
            self.d.qpos[self.hand_q + 3:self.hand_q + 7] = _quat_mul(quat, FOREARM_Q0)
            self.d.qvel[self.hand_v:self.hand_v + 6] = 0.0

    def grip_pose(self) -> np.ndarray:
        """Measured grip pose (x, y, z, yaw) of the physical hand."""
        p = self.d.site_xpos[self.grip_site].copy()
        R = self.d.xmat[self.m.body("rh_forearm").id].reshape(3, 3)
        yaw = np.arctan2(R[1, 2], R[0, 2])  # forearm z axis points along the fingers, kept horizontal
        return np.r_[p, yaw]

    def obj_pose(self) -> np.ndarray:
        q = self.d.qpos[self.obj_q:self.obj_q + 7]
        w, x, y, z = q[3:]
        yaw = np.arctan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z))
        return np.r_[q[:3], yaw]

    def contacts(self) -> tuple[bool, bool, bool]:
        """(thumb tip on handle, index tip on handle, any hand geom on handle)."""
        th = ff = anyc = False
        for c in self.d.contact[: self.d.ncon]:
            g = (c.geom1, c.geom2)
            if self.handle_geom not in g:
                continue
            other = g[1] if g[0] == self.handle_geom else g[0]
            b = self.m.geom_bodyid[other]
            if b in self.hand_bodies:
                anyc = True
                th |= b == self.th_body
                ff |= b == self.ff_body
        return th, ff, anyc

    def obs(self) -> np.ndarray:
        grip = self.grip_pose()
        obj = self.obj_pose()
        th, ff, _ = self.contacts()
        rel = _rot_z(-grip[3]) @ (obj[:3] - grip[:3])  # handle in the hand's yaw frame
        dyaw = wrap_half(obj[3] - grip[3])
        tray = np.r_[self.scene.tray_xy, HANDLE_RADIUS + 2 * TRAY_HALF[2]] - obj[:3]
        return np.r_[
            self.d.qpos[self.finger_qadr], grip[:3], np.sin(grip[3]), np.cos(grip[3]),
            self.grip_cmd[:3] - grip[:3], wrap(self.grip_cmd[3] - grip[3]), rel,
            np.sin(2 * dyaw), np.cos(2 * dyaw), tray, float(th), float(ff), obj[2] - HANDLE_RADIUS,
        ].astype(np.float32)

    # -- dynamics ---------------------------------------------------------------------------
    def step(self, action: np.ndarray) -> np.ndarray:
        a = np.asarray(action, dtype=float)
        dpos = a[:3]
        n = np.linalg.norm(dpos)
        if n > MAX_DPOS:
            dpos = dpos * (MAX_DPOS / n)
        self.grip_cmd[:3] += dpos
        self.grip_cmd[2] = max(self.grip_cmd[2], -0.005)
        self.grip_cmd[3] += np.clip(a[3], -MAX_DYAW, MAX_DYAW)
        self.set_command(self.grip_cmd, a[4:])
        return self.obs()

    def set_command(self, grip: np.ndarray, finger: np.ndarray) -> None:
        """Absolute command, used by open-loop replay; step() routes through here too."""
        self.grip_cmd = np.asarray(grip, dtype=float).copy()
        self.d.ctrl[self.finger_act] = np.clip(finger, self.ctrl_lo, self.ctrl_hi)
        self.d.ctrl[self.wrist_act] = 0.0
        self._apply_wrist()
        for _ in range(SUBSTEPS):
            mujoco.mj_step(self.m, self.d)
        self.t += 1
        self.max_lift = max(self.max_lift, self.obj_pose()[2] - HANDLE_RADIUS)
        self.settled = self.settled + 1 if self._placed() else 0

    def shove(self, rng: np.random.Generator, lo: float = 0.03, hi: float = 0.05) -> np.ndarray:
        """Displace the handle 3 to 5 cm horizontally in a random direction (a 'bump')."""
        ang = rng.uniform(0, 2 * np.pi)
        dist = rng.uniform(lo, hi)
        delta = dist * np.array([np.cos(ang), np.sin(ang)])
        self.d.qpos[self.obj_q:self.obj_q + 2] += delta
        self.d.qvel[self.obj_v:self.obj_v + 6] = 0.0
        mujoco.mj_forward(self.m, self.d)
        return delta

    # -- outcome ----------------------------------------------------------------------------
    def _placed(self) -> bool:
        obj = self.obj_pose()
        off = np.abs(obj[:2] - self.scene.tray_xy)
        near = off[0] < TRAY_HALF[0] and off[1] < TRAY_HALF[1]
        low = obj[2] < HANDLE_RADIUS + 2 * TRAY_HALF[2] + 0.012
        slow = np.linalg.norm(self.d.qvel[self.obj_v:self.obj_v + 3]) < 0.01
        released = not self.contacts()[2]
        return bool(near and low and slow and released)

    def success(self) -> bool:
        """Handle center inside the tray floor, resting on it (<1 cm/s), hand released, for 0.25 s."""
        return self.settled >= 5

    def grasped(self) -> bool:
        return self.max_lift >= 0.05

    def done(self) -> bool:
        return self.success() or self.t >= MAX_STEPS
