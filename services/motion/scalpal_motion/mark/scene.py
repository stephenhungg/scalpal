"""MuJoCo embodiment for `mark_incision`: a Franka Panda carrying the Shadow hand, which holds a skin marker,
over a curved abdominal skin patch in the patient frame.

- Arm: MuJoCo Menagerie franka_emika_panda (panda_nohand.xml), 7 joints.
- Hand: MuJoCo Menagerie Shadow hand (right) attached at the Panda flange (attachment_site). Its fingers use the
  same glove mapping as teleop (teleop.finger_curls): holding an instrument closes every finger on the grasp shape.
- Marker: a 13 cm capsule fixed in the fist (dagger grip, tip out of the little-finger side). The tip site is
  the end effector the IK drives.
- Patient: a skin patch y = -K x^2 in the patient frame (+X patient left, +Y anterior, +Z cranial, umbilicus at the
  origin) on a mocap body, so each rollout can place the patient at a different pose in front of the robot.

Kinematic simulation: damped-least-squares IK sets the arm joints every control tick; there is no contact
dynamics. The marker "draws" when its tip is within DRAW_MM of the skin while the trigger is closed, and the
commanded tip is never allowed below the skin. Simulation only; no physical robot.
"""

from __future__ import annotations

from dataclasses import dataclass

import mujoco
import numpy as np

from ..learning.env import FINGER_ACTUATORS, FINGER_JOINTS, FOREARM_Q0, GRIP_OFFSET, MENAGERIE_HAND, PALM_DOWN, _quat_mul
from ..paths import MOTION_ROOT
from ..teleop import curl_vector, finger_curls, grasp_shapes

MENAGERIE_PANDA = MOTION_ROOT / "models" / "menagerie" / "franka_emika_panda" / "panda_nohand.xml"
FETCH_HINT = (
    "git clone --depth 1 --filter=blob:none --sparse https://github.com/google-deepmind/mujoco_menagerie.git "
    "models/menagerie && git -C models/menagerie sparse-checkout set shadow_hand franka_emika_panda"
)

K_SKIN = 0.89  # skin curvature: y = -K x^2, so the right ASIS (x = -0.13) sits 1.5 cm below the umbilicus
PATCH_X = (-0.20, 0.20)
PATCH_Z = (-0.26, 0.14)
DRAW_MM = 2.0
MARKER_HALF = 0.065
MARKER_TIP = 0.085  # tip distance from the grip point along the marker axis
ROT_WEIGHT = 0.2  # metres per radian in the IK error: tip position first, marker attitude second
ARM_JOINTS = [f"joint{i}" for i in range(1, 8)]
ARM_HOME = np.array([-0.221, 0.7, -0.078, -1.674, 2.071, 2.066, -0.222])  # solved once: marker over McBurney, max joint margin
# Patient pose in the robot world: patient +Y (anterior) is world up, +Z (cranial) faces the robot (world -x).
PATIENT_BASE_POS = np.array([0.85, 0.0, 0.05])
PATIENT_BASE_R = np.array([[0.0, 0.0, -1.0], [-1.0, 0.0, 0.0], [0.0, 1.0, 0.0]])  # columns: patient X, Y, Z in world


def skin_y(x):
    return -K_SKIN * np.asarray(x) ** 2


def skin_normal_out(p) -> np.ndarray:
    """Outward (anterior-ish) unit normal of the skin at patient-frame point p."""
    n = np.array([2 * K_SKIN * p[0], 1.0, 0.0])
    return n / np.linalg.norm(n)


def skin_distance(p) -> float:
    """Signed height of patient-frame point p above the skin (vertical in the patient frame; fine at this curvature)."""
    return float(p[1] - skin_y(p[0]))


def onto_skin(p) -> np.ndarray:
    q = np.array(p, float)
    q[1] = skin_y(q[0])
    return q


def _rot_x(a):
    c, s = np.cos(a), np.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def _rot_z(a):
    c, s = np.cos(a), np.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


def _mat2quat(R) -> np.ndarray:
    q = np.zeros(4)
    mujoco.mju_mat2Quat(q, np.asarray(R, float).reshape(-1))
    return q


@dataclass
class PatientPose:
    """Where the patient frame sits in the robot world for one rollout."""

    pos: np.ndarray
    yaw: float = 0.0  # about world z

    @property
    def R(self) -> np.ndarray:
        return _rot_z(self.yaw) @ PATIENT_BASE_R

    def to_world(self, p) -> np.ndarray:
        return self.pos + self.R @ np.asarray(p, float)

    def to_patient(self, w) -> np.ndarray:
        return self.R.T @ (np.asarray(w, float) - self.pos)

    def to_dict(self) -> dict:
        return {"pos": [round(float(v), 4) for v in self.pos], "yawDeg": round(float(np.degrees(self.yaw)), 2)}


def sample_patient_pose(rng: np.random.Generator) -> PatientPose:
    return PatientPose(PATIENT_BASE_POS + rng.uniform([-0.05, -0.06, -0.03], [0.05, 0.06, 0.03]), float(rng.uniform(*np.deg2rad([-15, 15]))))


def _skin_mesh(spec: mujoco.MjSpec) -> None:
    xs = np.linspace(*PATCH_X, 21)
    zs = np.linspace(*PATCH_Z, 21)
    top = [[x, skin_y(x), z] for x in xs for z in zs]
    bottom = [[x, skin_y(x) - 0.04, z] for x in xs for z in zs]
    n = len(xs) * len(zs)
    faces = []
    for i in range(len(xs) - 1):
        for j in range(len(zs) - 1):
            a, b, c, d = i * len(zs) + j, (i + 1) * len(zs) + j, (i + 1) * len(zs) + j + 1, i * len(zs) + j + 1
            faces += [[a, d, b], [b, d, c], [a + n, b + n, d + n], [b + n, c + n, d + n]]
    mesh = spec.add_mesh(name="skin")
    mesh.uservert = np.array(top + bottom, float).reshape(-1).tolist()
    mesh.userface = np.array(faces).reshape(-1).tolist()
    mesh.inertia = mujoco.mjtMeshInertia.mjMESH_INERTIA_SHELL


def build_model() -> mujoco.MjModel:
    for path in (MENAGERIE_PANDA, MENAGERIE_HAND):
        if not path.exists():
            raise SystemExit(f"Menagerie model missing at {path}. From services/motion run:\n  {FETCH_HINT}")
    arm = mujoco.MjSpec.from_file(str(MENAGERIE_PANDA))
    hand = mujoco.MjSpec.from_file(str(MENAGERIE_HAND))
    forearm = hand.body("rh_forearm")
    forearm.pos = [0, 0, 0]
    forearm.quat = [1, 0, 0, 0]  # fingers continue along the flange axis
    # Grip point and marker axis in forearm coordinates, from the transfer task's validated palm-down grasp.
    R_fa = np.zeros(9)
    mujoco.mju_quat2Mat(R_fa, _quat_mul(PALM_DOWN, FOREARM_Q0))
    R_fa = R_fa.reshape(3, 3)
    grip = R_fa.T @ GRIP_OFFSET
    tip_dir = R_fa.T @ np.array([0.0, -1.0, 0.0])  # out of the little-finger side
    fingers = np.array([0.0, 0.0, 1.0])
    site_R = np.c_[fingers, np.cross(tip_dir, fingers), tip_dir]  # site x = fingers, z = marker tip direction
    marker = forearm.add_body(name="marker", pos=list(grip), quat=list(_mat2quat(site_R)))
    marker.add_geom(name="marker_body", type=mujoco.mjtGeom.mjGEOM_CAPSULE, size=[0.007, MARKER_HALF, 0],
                    pos=[0, 0, MARKER_TIP - MARKER_HALF - 0.008], rgba=[0.32, 0.18, 0.55, 1], contype=0, conaffinity=0)
    marker.add_geom(name="marker_cap", type=mujoco.mjtGeom.mjGEOM_CYLINDER, size=[0.0072, 0.012, 0],
                    pos=[0, 0, MARKER_TIP - 2 * MARKER_HALF - 0.004], rgba=[0.95, 0.95, 0.95, 1], contype=0, conaffinity=0)
    marker.add_site(name="marker_tip", pos=[0, 0, MARKER_TIP], size=[0.002, 0, 0], rgba=[0.1, 0.05, 0.2, 1])
    hand.option.cone = arm.option.cone  # contact settings are irrelevant here (kinematic); avoids an attach conflict
    arm.site("attachment_site").attach_body(forearm, "", "")

    arm.option.timestep = 0.002
    w = arm.worldbody
    arm.add_texture(name="floor", type=mujoco.mjtTexture.mjTEXTURE_2D, builtin=mujoco.mjtBuiltin.mjBUILTIN_CHECKER,
                    rgb1=[0.86, 0.87, 0.89], rgb2=[0.80, 0.81, 0.83], width=300, height=300)
    arm.add_material(name="floor", textures=["", "floor"], texrepeat=[6, 6], texuniform=True)
    w.add_light(pos=[0.4, 0, 2.0], dir=[0, 0, -1], type=mujoco.mjtLightType.mjLIGHT_DIRECTIONAL, diffuse=[0.7, 0.7, 0.7])
    w.add_light(pos=[1.2, -1.0, 1.2], dir=[-0.6, 0.6, -0.6], diffuse=[0.4, 0.4, 0.4])
    w.add_geom(name="floor", type=mujoco.mjtGeom.mjGEOM_PLANE, size=[3, 3, 0.05], pos=[0, 0, -0.40], material="floor")
    _skin_mesh(arm)
    patient = w.add_body(name="patient", mocap=True)
    patient.add_geom(name="skin", type=mujoco.mjtGeom.mjGEOM_MESH, meshname="skin", rgba=[0.95, 0.78, 0.68, 1],
                     contype=0, conaffinity=0)
    patient.add_geom(name="drape", type=mujoco.mjtGeom.mjGEOM_BOX, size=[0.32, 0.02, 0.45], pos=[0, -0.075, -0.06],
                     rgba=[0.33, 0.55, 0.62, 1], contype=0, conaffinity=0)
    for name, p in [("right_asis", [-0.13, -0.015, -0.14]), ("umbilicus", [0, 0, 0])]:
        patient.add_site(name=name, pos=p, size=[0.005, 0, 0], rgba=[0.15, 0.35, 0.75, 1])
    w.add_geom(name="table", type=mujoco.mjtGeom.mjGEOM_BOX, size=[0.5, 0.4, 0.15], pos=[0.95, 0, -0.25],
               rgba=[0.55, 0.57, 0.6, 1], contype=0, conaffinity=0)
    model = arm.compile()
    model.vis.headlight.ambient[:] = [0.35, 0.35, 0.35]
    model.vis.headlight.diffuse[:] = [0.5, 0.5, 0.5]
    model.vis.global_.offwidth = max(model.vis.global_.offwidth, 1280)
    model.vis.global_.offheight = max(model.vis.global_.offheight, 720)
    return model


class MarkRobot:
    """Arm + hand + marker over the patient. Command the marker tip in the patient frame; IK does the rest."""

    def __init__(self, model: mujoco.MjModel | None = None):
        self.m = model if model is not None else build_model()
        self.d = mujoco.MjData(self.m)
        m = self.m
        self.arm_q = np.array([m.jnt_qposadr[m.joint(j).id] for j in ARM_JOINTS])
        self.arm_v = np.array([m.jnt_dofadr[m.joint(j).id] for j in ARM_JOINTS])
        self.lo = m.jnt_range[[m.joint(j).id for j in ARM_JOINTS], 0]
        self.hi = m.jnt_range[[m.joint(j).id for j in ARM_JOINTS], 1]
        self.tip = m.site("marker_tip").id
        self.mocap = m.body("patient").mocapid[0]
        self.finger_q = np.array([m.jnt_qposadr[m.joint("rh_" + j).id] for j in FINGER_JOINTS])
        self.open_shape, self.closed_shape = grasp_shapes()
        self.patient = PatientPose(PATIENT_BASE_POS.copy())
        self.reset(self.patient)

    # -- hand -----------------------------------------------------------------------------
    def set_fingers(self, sample: dict) -> None:
        """Glove mapping (teleop.finger_curls): actuator targets set directly as joint angles (kinematic)."""
        ctrl = self.open_shape + curl_vector(finger_curls(sample)) * (self.closed_shape - self.open_shape)
        q = dict(zip(FINGER_ACTUATORS, ctrl))
        for j, adr in zip(FINGER_JOINTS, self.finger_q):
            self.d.qpos[adr] = q[j[:2] + "J0"] / 2 if (j[2:] in ("J1", "J2") and j[:2] != "TH") else q[j]

    # -- world ----------------------------------------------------------------------------
    def reset(self, patient: PatientPose) -> None:
        mujoco.mj_resetData(self.m, self.d)
        self.patient = patient
        self.d.mocap_pos[self.mocap] = patient.pos
        self.d.mocap_quat[self.mocap] = _mat2quat(patient.R)
        self.d.qpos[self.arm_q] = ARM_HOME
        self.set_fingers({"grip": 1.0, "trigger": 1.0, "heldInstrument": "skin_marker"})
        self._kin()

    def _kin(self) -> None:
        mujoco.mj_kinematics(self.m, self.d)
        mujoco.mj_comPos(self.m, self.d)

    def tip_patient(self) -> np.ndarray:
        return self.patient.to_patient(self.d.site_xpos[self.tip])

    def ik(self, p_patient, iters: int = 30, tol_m: float = 3e-4) -> float:
        """Damped least squares on the 7 arm joints: marker tip at p_patient, marker axis along the inward skin normal.
        Returns the remaining position error (m)."""
        m, d = self.m, self.d
        target = self.patient.to_world(p_patient)
        z_des = -(self.patient.R @ skin_normal_out(p_patient))
        jacp, jacr = np.zeros((3, m.nv)), np.zeros((3, m.nv))
        err = np.inf
        for _ in range(iters):
            self._kin()
            dp = target - d.site_xpos[self.tip]
            # 5-DoF: only the marker axis is constrained (into the skin); roll about it is left to the null space.
            dr = np.cross(d.site_xmat[self.tip].reshape(3, 3)[:, 2], z_des)
            err = float(np.linalg.norm(dp))
            if err < tol_m and np.linalg.norm(dr) < 0.01:
                break
            mujoco.mj_jacSite(m, d, jacp, jacr, self.tip)
            J = np.r_[jacp[:, self.arm_v], ROT_WEIGHT * jacr[:, self.arm_v]]
            e = np.r_[dp, ROT_WEIGHT * dr]
            dq = J.T @ np.linalg.solve(J @ J.T + 1e-4 * np.eye(6), e)
            # Null-space pull toward the home posture keeps the elbow tidy.
            N = np.eye(7) - np.linalg.pinv(J, rcond=1e-3) @ J
            dq += N @ (0.1 * (ARM_HOME - d.qpos[self.arm_q]))
            step = np.abs(dq).max()
            if step > 0.15:
                dq *= 0.15 / step
            d.qpos[self.arm_q] = np.clip(d.qpos[self.arm_q] + dq, self.lo, self.hi)
        self._kin()
        return float(np.linalg.norm(target - d.site_xpos[self.tip]))
