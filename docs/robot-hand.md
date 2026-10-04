# Robot Hand: Handoff for Stephen

The robot side of the demo is a simulated Shadow hand driven by the Quest controllers only. No hand camera, no passthrough video. What the gloved hand does in VR is what the robot hand does in sim, and every recorded attempt becomes robot-hand training data labeled with the surgery step the coach says the learner is on.

```
Quest (OR scene)                          demo laptop
ControllerMotionCapture  --UDP 9124-->    scalpal-motion teleop --coach http://127.0.0.1:8787
  pose rel. PatientRoot, grip,              Shadow hand in MuJoCo, per-finger glove mapping
  trigger, held instrument                  each attempt -> out/teleop/*.json (step-labeled)
                                            summary -> POST coach /robot-attempts (optional)
                                          scalpal-motion export-lerobot -> LeRobot-style dataset
```

## 1. Add the capture to the OR scene

1. Open `apps/quest/Assets/Scalpal/Quest/Scenes/NativeSession.unity` (and `NativeWorkbench.unity` if you demo from there).
2. Menu **Scalpal > Robotics > Add Controller Motion Capture**. It creates `Controller Motion Capture`, sets **Pose Root** to the scene's `PatientRoot` when one exists, and turns on the Android internet permission the UDP stream needs. Save the scene.
3. It finds both `XRInstrumentInput`s at enable time, so it reads the same tracking origin, grip, trigger and held instrument the gloves (`ControllerHandPose`) use. Nothing else to wire.

## 2. Poses relative to the registered patient

With **Pose Root** set, every controller and head pose is sent relative to `PatientRoot` (`space: "patient"` in each frame), so the robot sees motion in the patient's frame and not in wherever the headset booted. If `NativeBodyRegistration` moves a different transform than `PatientRoot` in your build (it exposes `patientFrame`), point Pose Root at that one instead. Without a Pose Root the frames say `space: "world"` (tracking origin) or `"tracking"`.

The laptop side anchors the first tracked pose of each attempt to the robot hand's home, so only relative motion matters; the patient frame keeps yaw meaningful (heading toward the patient = robot forward).

## 3. Point the stream at the demo laptop

- On the laptop: `ipconfig getifaddr en0` gives the LAN IP. Same Wi-Fi as the headset.
- On the capture component: **Host** = that IP, **Port** = `9124` (the teleop default), **Streaming** on.
- A missing laptop never interrupts surgery: the first failed send logs a warning and turns streaming off for the session.
- On-device episode recording (`frames.jsonl` under `persistentDataPath/scalpal-controller/`) only happens with **Operator Confirmed Consent** ticked. For the demo the laptop records, so leave it off unless you want on-headset copies.

## 4. Disable the passthrough clip capture for the demo

The camera path from `349206e` (`Capture/Runtime/HandCaptureRecorder.cs`, started from `NativeCaseSession.BeginRecapRun`) records raw passthrough hand clips and queues motion jobs. It is not part of the robot-hand demo. The no-code way to keep it off: on the Theatre setup operator card, do not press **Learner agreed to hand recording** (it is off by default; the button flips to "Withdraw hand-recording consent" when on). `ConsentGranted` then refuses, nothing is recorded or uploaded, and the recap says "Replay skipped". If you'd rather remove the recorder from the demo build entirely, that is a change in `NativeCaseSession.BeginRecapRun` (the `HandCaptureRecorder.Ensure()` call); your call, I did not touch it.

## 5. Verify end to end

Laptop, from the repo root:

```sh
scripts/demo-up.sh                       # or however the coach is started; it serves :8787
cd services/motion && uv sync
# one-time: the Shadow hand model (see scalpal_motion/learning/README.md)
git clone --depth 1 --filter=blob:none --sparse https://github.com/google-deepmind/mujoco_menagerie.git models/menagerie \
  && git -C models/menagerie sparse-checkout set shadow_hand
uv run scalpal-motion teleop --consented --coach http://127.0.0.1:8787
```

1. **Without the headset:** in a second terminal `uv run scalpal-motion send-controller`. The window's hand reaches, grasps and places the handle; stderr prints `attempt 1: SUCCESS` and a file lands in `services/motion/out/teleop/`.
2. **With the headset:** start a case so the coach has a session. Put the controllers on: the window's top line flips from `LOST (holding pose)` to `tracked`, and the third line shows the coach's current step title. Pull the trigger: only the index (and thumb) curls, like the glove. Squeeze grip: middle/ring/pinky curl. Pick up an instrument in VR: the robot fist closes fully.
3. **Labels:** `python3 -c "import json,glob; d=json.load(open(sorted(glob.glob('out/teleop/*.json'))[-1])); print({(c or {}).get('stepTitle') for c in d['coach']})"` lists the step titles stamped on the last attempt.
4. **Dataset:** `uv run --with pyarrow scalpal-motion export-lerobot out/teleop` writes `out/lerobot/scalpal_robot_hand/` (tasks = step titles).

Keys in the teleop window: `n` new scene (saves the current attempt), `a` re-anchor the controller to the hand's home, `q` quit.

## Status

- Measured locally (laptop, no headset): stand-in sender over UDP, real coach on :8787 bound to a `patient-demo-sparse` session, attempt saved with all 212 frames labeled `mark_incision` / "Mark McBurney incision", successful placement, exported to Parquet.
- Not yet run: a real Quest session. The capture compiles in the .NET check (`services/preop`, `npm run test:unity`) but has not run on a headset.
- The coach does not have the `robot-attempts` route yet; the POST fails quietly (404) and the attempt is still saved locally.
- Claims: "the simulated robot hand replays the learner's controller motion" and the sim learning numbers in `services/motion/scalpal_motion/learning/README.md`. No physical robot, no autonomous surgery.
