# Operation Flow (final vision)

Decided by Matthew on October 4, 2026 after a Q&A with the team's coding agents. This is the shared picture of the whole run, especially the operating room. It takes precedence over older flow descriptions (including the attending phase, passthrough-video robot input and live POV streaming to the web). Build details live in the linked component docs; this page says what we are building and who owns which piece.

## The Run

**Launch → explore patients → patient interview (office) → interview scorecard → operating room (AR or VR): Jarvis briefing and surgery flythrough → surgery → recap (success or death) → robot hand replay.**

1. **Office: patient interview.** A 1:1 conversation with the patient voice, then rounds of four "what do you do next?" choices (tap or say it). Jarvis is not present. Spec: [office interview](office-interview.md).
2. **Interview scorecard.** On screen only, per round: your pick, the best pick, why.
3. **Operating room entry.**
   - Jarvis connects for the first time and runs the Time-Out.
   - He breaks down the surgery over a short Blender flythrough: a transparent body with the organs in place, then a motion zoom into each step of the procedure.
   - Then the case starts.
4. **Surgery.** Full freedom (below). Jarvis coaches live.
5. **End.** The case ends when the goals are reached or the patient dies. The recap shows what happened and why.
6. **Robot replay.** The recorded controller motion drives the simulated robot hand.

## The Operating Room

### Freedom and the checklist

- The body is a physics sandbox. The learner can cut anywhere with any tool, do steps out of order, and kill the patient. Succeeding is on them.
- A **checklist of the procedure's steps** sits at the top left of the view as guidance. Steps tick off automatically when their measured goals are met. The checklist never blocks an action.

### Body model

- **VR patient:** layered like a surgery game: skin, fat, fascia, muscle, bone, organs.
- **Surgical field (abdomen):** detailed layered tissue with real cutting, splitting, clamping, tying and bleeding (the open-body reducer in `services/preop/src/open-body.ts` and its C# mirror).
- **Rest of the body:** coarse damage regions with authored consequences.

  | Region hit with a cutting tool | Consequence |
  | --- | --- |
  | Neck | Major arterial bleed, rapid death without control |
  | Head | Catastrophic injury, case ends |
  | Chest | Large vitals drop (bleeding, breathing compromise) |
  | Limbs | Minor bleed |

- **Pointing:** aiming a Quest controller at a tool or organ shows a bounding box and its name. This comes from the scene graph, not a detector.

### Vitals

- **Baseline:**
  - AR: Presage reads the real volunteer's pulse and breathing through a phone camera.
  - VR: the patient's last charted vitals (FinchNode, or authored values when the chart has none).
- **Simulated deltas from blood loss:**
  - Every patient has a blood volume from their weight (about 70 ml/kg adult, 80 ml/kg child).
  - Bleed rate grows with incision size and the vessel or region injured.
  - Loss drives heart rate, blood pressure, then SpO2 and breathing, following ATLS hemorrhage classes.
  - The timing is **accelerated for the demo**: a large uncontrolled bleed turns dangerous in about 30 to 90 s.
- **Death:** crossing the death threshold (critical blood loss or a catastrophic region) flatlines the monitor. Jarvis reacts and the case ends as failed.
- **Honesty rule:**
  - The volunteer's real vitals never react to the virtual surgery.
  - Displays show "baseline (measured)" plus "simulated change". Simulated values are never presented as the volunteer's.
  - Presage values are wellness data, not diagnosis.
- **Where shown:** a vitals board rendered in the room (AR and VR), and the dashboard.

### State tracking (one event log)

Unity is the producer and local authority. Every interaction becomes a numbered event on the headset clock:
- tool picked up or put down per hand;
- tool tip touching a structure;
- each cut, split, clamp, tie or seal, with measurements;
- bleeding started or controlled, and blood lost;
- region damage;
- tracking validity;
- a 1 Hz tick while anything is time-driven.

The coach service replays the log through the same reducer. From it the tracker derives:
- which procedure steps are done and what the next one still needs, in plain words;
- what is in each hand;
- each body region's condition;
- blood loss and active bleeds;
- the simulated vitals;
- stall time.

Spec: [surgery state](surgery-state.md).

### Jarvis

Jarvis is the only AI that talks. He is fed by side analyzers that do not talk:

```
Unity events ──► state tracker ──────────┐
vitals model ────────────────────────────┤
anomaly rules ──► instant alarm clips ───┼──► JARVIS (voice LLM): explains, answers, hints
POV screenshots ──► vision side-agent ───┘
```

- **State tracker** (deterministic): step, tools, body condition, bleeding, history.
- **Anomaly rules** (deterministic, instant):
  - cutting a wrong region ("Hey, what are you doing? That's the patient's neck!");
  - a bleed with falling blood pressure;
  - rapid deterioration;
  - an uncontrolled bleed past 30 s;
  - a cutting tool on a critical structure.

  Each one plays a pre-recorded clip immediately; Jarvis then explains.
- **Stall detector:** no progress for a while escalates hints (about 15, 25, 40 and 60 s, ending with an offer to demonstrate).
- **Vision side-agent:** screenshots of the learner's view, summarized for context and on request ("what am I looking at?"). Vision supports the state but never decides it.
- **Questions:** Jarvis answers any question about the procedure from the state ("which step am I on", "am I holding the right tool", "why is the pressure dropping"). He never claims progress the state does not show.

### AR mode

- **Volunteer:** lies flat on a table. A phone camera on a stand runs Presage for the baseline vitals.
- **Body fit:** MediaPipe fits the body, so the teaching organs are placed to the person's proportions.
- **Invalid fit:** if the fit is invalid, the anatomy is hidden and scoring is paused.
- **In the room:** a virtual tool tray sits next to the table, and a virtual vitals board is rendered in the room.

### VR mode

- A virtual operating room with the layered virtual patient on the table.
- Same tools, state tracking, vitals model and Jarvis as AR.

## Dashboard (localhost, logs only)

The companion on the demo laptop shows **logs only**, live through SpacetimeDB:
- Jarvis transcript and alerts;
- state tracker events and the step checklist;
- vitals (measured baseline and simulated change);
- blood loss and active bleeds;
- Presage readings;
- the interview transcript and scores.

**No live Quest POV on the web.** The headset view stays on Stephen's laptop (casting). Everything runs on localhost; the Quest and phones join over the same wifi.

## Robot Hand

- **Input:** **VR controllers only.** There is no hand camera or passthrough video.
- **What is logged:** controller 6DoF poses and inputs (grip, trigger, buttons) with the tool actions, per surgery step. Hand motion (grasp, release, cut) is inferred from them.
- **What it drives:** the logged motion is retargeted onto the simulated robot hand for the replay.
- **Claims:**
  - Say "replays the learner's controller motion", or describe what was measured in sim.
  - Do not claim a learned autonomous policy unless the robotics results show one.

## Ownership

| Piece | Owner | Status |
| --- | --- | --- |
| Unity: layered body, physics, coarse regions, checklist UI, pointing bounding boxes, flythrough, AR body fit, tool tray, vitals board, event log with `seq` and tick | Stephen (and his surgery Codex thread) | Partly built (open-wall layers, open appendectomy, tick); rest to build |
| Office interview runtime, patient content, Jarvis, analyzers (state tracker, anomaly rules, stall hints, vision), alarm clips | Matthew | Office and most analyzers built; region anomalies and vitals-driven alarms to build |
| Vitals model (blood volume, accelerated deltas, death threshold) and Presage service | Silas (`services/vitals`), consumed by Matthew's coach | Presage scaffold, quota guard and physiology model on main; blood-loss coupling to build |
| SpacetimeDB routing and the logs dashboard | Nathan, with Silas on companion UI | Companion and vitals panel live; per-log views to extend |
| Robot hand from controller motion | Matthew (robotics session) | Controller capture and sim replay in progress |

## What This Replaces

- The attending phase and any Jarvis role in the office.
- Passthrough or hand-camera video as robot input (now VR controller motion only).
- Live POV video on the web dashboard.
- Step-gated surgery (the procedure is guidance; the body is a sandbox).
