# Presage: The Patient Channel

Proposal, October 4, 2026, for the MLH "Best Use of Presage" track. **Nothing here is built yet.** It is parked until one real AR run on a volunteer shows whether the overlay drifts with breathing; build only if the user approves and a Presage API key is available.

## Thesis

**Robots can't learn surgery from hands alone. They have to learn from the patient too.**

Surgical robot datasets record the surgeon: tool paths, video, kinematics. They do not record the patient. Real surgery happens on a body that breathes and has a pulse; surgeons time actions to respiration and check vitals before irreversible steps. Scalpal adds the patient channel: Presage reads the real reclining volunteer through one mounted phone camera.

1. **The anatomy breathes with the patient.** The volunteer's real breathing waveform drives the AR overlay so the generic teaching organs rise and fall with the chest between body-fit updates. This is respiratory motion compensation, a known problem in image-guided surgery and surgical robotics.
2. **Every demonstration is physiology-synced.** Each practice run records hand motion (raw passthrough capture), every tool event, and the patient's breathing and pulse on one clock, so demonstration data carries *when* an action happened relative to respiration, not only *how*.
3. **Consent and safety for human data.** If robot training uses real volunteers, a contactless safety layer should watch them. Presage flags distress (pulse spike, erratic breathing) and prompts the operator to pause.

> Pitch: "Presage gives our robot training data a pulse. Every rep in Scalpal captures what the surgeon's hands did and what the patient's body was doing, so the next generation of surgical robots learns to work with a living patient, not on a mannequin."

## What is real vs aspirational

- Real (buildable): breathing-synced overlay motion; breathing/pulse in the capture manifest and event log on the session clock; a comfort guard with thresholds.
- Aspirational: no robot policy is trained on this data; the demo shows the dataset structure plus kinematic replay.
- Never claim: the volunteer's real vitals do not respond to the virtual surgery. The OR monitor therefore shows **simulated vitals anchored to a measured baseline** (below), never bare "real vitals" during surgery. Generic teaching anatomy is not the volunteer's organs. Presage SDK metrics are wellness information, not diagnosis.

## OR Monitor: Measured Baseline + Simulated Delta

**Displayed vitals = real baseline (Presage) + simulated delta (surgery state).**

1. **Baseline (real).** At Time-Out, Presage spot-measures the volunteer (pulse, breathing rate, with confidence). That becomes this patient's starting physiology. If confidence is low, fall back to an authored baseline, labeled as authored.
2. **Delta (simulated).** A small deterministic physiology model maps open-body state (blood lost, active bleeds and rates, contamination, critical injuries) to vitals changes, using hemorrhage classes (ATLS) against an estimated blood volume of about 70 mL/kg (assumed weight, or the FinchNode chart weight if present):

| Blood loss (% of EBV) | Pulse | Breathing | Blood pressure |
| --- | --- | --- | --- |
| < 15% | baseline to +10 | baseline | normal |
| 15–30% | ~100–120 | 20–30 | narrowing pulse pressure |
| 30–40% | ~120–140 | 30–40 | falling |
| > 40% | > 140 | > 35 | crashing |

   Active bleeding pushes toward the next class; hemostasis lets values recover with 10–30 s smoothing; a critical injury adds an acute spike. The model is a pure function of logged body state and time, so the recap and Jarvis can reproduce it exactly.
3. **Display.** The OR monitor shows the combined values and a pulse trace drawn at the simulated rate, labeled for example "HR 118 · simulated from baseline 72 (measured)". The AR overlay's breathing motion still follows the volunteer's **real** breathing waveform; only the monitor numbers carry the simulated delta.

Why: the monitor agrees with the simulation (an uncontrolled bleed makes the patient tachycardic; control brings recovery), every run starts from a real person's physiology, Jarvis coaches from model facts ("HR 118 and rising, blood loss 600 mL, find the bleeder"), and the grader can score response time to hemodynamic deterioration. The physiology model is authored teaching content, not validated clinical simulation.

## How Presage works (short)

Remote photoplethysmography: each heartbeat slightly changes the color of facial skin; averaging many skin pixels over time recovers a pulse waveform (12 s average pulse rate, plus HRV). Breathing comes mainly from chest/shoulder motion over a 30 s window. Every value has a confidence. It needs a still subject, a **fixed camera** (not handheld or head-mounted), face and upper chest visible and well lit, no flicker, and no talking for breathing. A reclining, quiet volunteer is close to the ideal case; the Quest's head-mounted camera is close to the worst, so it is not the sensor.

## Architecture

```
phone on a stand ──(Continuity Camera)──▶ Mac: services/vitals (Node + @smartspectra/node-sdk)
                                              │ breathing + pulse waveforms, rates, confidence (~10 Hz)
                     ┌────────────────────────┼──────────────────────────┐
                     ▼                        ▼                          ▼
            preop coach / event log     /vitals over USB reverse     SpacetimeDB row
          (session clock, recap track)  (Quest overlay + guard)     (companion / judges)
```

- Phone: iPhone as a macOS Continuity Camera (no phone app). Fallbacks: Android phone as a USB webcam (Android 14+), or the Mac webcam. Mount 0.5–1 m from the volunteer, landscape, face and upper chest framed, front lighting.
- Only publish values above a confidence threshold; otherwise "measuring" with the SDK's reason (motion, lighting, talking). Label every display "volunteer · real vitals · Presage".
- No footage stored by Scalpal; confirm whether the SDK processes on-device or in Presage's cloud before the demo, and cover it in volunteer consent.

## Build plan (if approved, ~2–3 h)

1. `services/vitals`: Node service around `@smartspectra/node-sdk` (`npm install @smartspectra/node-sdk`; `requestedMetrics: [...breathingMetrics, ...cardioMetrics]`), with a labeled no-key demo mode that is never presented as real.
2. Breathing phase drives a small, bounded vertical motion of the AR anatomy root between registration updates; disabled in VR and when confidence is low.
3. Write breathing/pulse samples into the event log and the hand-capture manifest on the same clock; recap shows a breathing track under the surgery timeline.
4. Comfort guard: operator pause prompt on sustained pulse spike or erratic breathing.
5. Physiology model (`baseline + delta`) as a pure function of body state, mirrored in Unity and the coach, with golden tests per hemorrhage class and recovery; buildable before the API key using an authored baseline.
6. Validations that can fail: low-confidence values never displayed or logged as real; demo mode always labeled; overlay motion bounded and off in VR.

## Needed from the team

- A Presage API key (developer portal at physiology.presagetech.com) in `services/vitals/.env` as `PRESAGE_API_KEY` (never committed).
- A phone stand and a volunteer who consents to vitals measurement.

## Sources

- SDK and platforms: https://smartspectra.presagetech.com/ and https://github.com/Presage-Security/SmartSpectra
- Requirements and limits: https://smartspectra.presagetech.com/docs/
- Node.js SDK: https://smartspectra.presagetech.com/docs/nodejs/
- FDA 510(k) K254169 (spot measurement of pulse and respiratory rate, static-mounted phone, healthcare oversight): https://www.accessdata.fda.gov/cdrh_docs/pdf25/K254169.pdf
- Telemetry and privacy: https://github.com/presage-security/smartspectra/blob/b8e819464770920960d1b581a007eb35f230c217/docs/telemetry-and-privacy.md
