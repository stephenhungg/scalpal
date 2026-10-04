# Surgery Procedure Build Spec

Updated October 3, 2026. Target design for the operating room procedure, starting with laparoscopic appendectomy, in both OR modes (AR on a real reclining person, or full VR). Evidence and full tables: [surgery procedure research](research/surgery-procedure.md). Handoff into the OR: [office to OR handoff](office-to-or-handoff.md). UX rules: [experience UX](experience-ux.md#5-operating-room-ar-or-vr).

## Where It Stands (origin/main, audited)

Appendectomy is the only procedure that runs. `procedures.ts` authors 10 steps, but every step completes on a trigger-activated tool tip touching a named mesh (`touch_target`, `identify_targets`, `confirm`). Ports are floating spheres; nothing is inserted and instruments do not pivot at ports. Nothing is actually clipped, divided, stapled or bagged. The laparoscope renders to a monitor but no step uses it. Real today: holding 15 instruments, tip contact, grasp deformation of appendix/mesoappendix/artery, an abdominal wall patch the blade can cut, and a vessel bleed that sealing stops and suction drains (unscored). Coach alerts and hint escalation exist server-side (`coach.ts`) but never reach the Quest. `NativeCaseSession` is hardcoded to one patient and procedure (the handoff thread is fixing that). Duplicates: two tip-contact scripts, two port builders, a leftover sandbox `TrainingPatch`, and `CaseRunner.cs` copying `engine.ts`.

## Interaction Model: Hybrid Ports + See-Through Window + Scope Monitor

1. **Real ports.** Trocars dock on port markers on the patient (registered real body in AR, virtual patient in VR) and stay there.
2. **Port-constrained instruments.** The controller is the handle; the tip is computed through the port pivot, so the shaft visibly pivots in the belly and the fulcrum effect is real.
3. **See-through window.** A framed opening in the abdominal wall around the operative field shows the anatomy directly with depth cues; no flat overlay on skin.
4. **Scope monitor.** A fixed monitor shows the 30° laparoscope view. A virtual assistant holds and aims the camera (auto-follows the active step). A later "scope-only" mode hides the window.

Why: novices perform far worse with the inverted laparoscopic view; direct-view practice first cuts task time; the headset already adds heavy cognitive load; a monitor in a headset still reads as real laparoscopy. Sources in the research doc §2.

## Appendectomy Steps (8 learner steps, ~3–5 min)

| # | Step | Tool (port) | Learner action | Success check | Mistake → feedback | Coach line |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Entry + camera | Auto | Watch | Auto | — | "Camera's in. Let's place your working ports." |
| 1 | Working ports | 5 mm trocars (LLQ, suprapubic) | Dock and push in along the axis | Both seated within 3 cm / 30°, tip seen on monitor | Pushing toward bladder → red flash + buzz | "Two ports: left lower, then suprapubic." |
| 2 | Expose | Graspers | Tilt table (one button), sweep bowel out of the RLQ zone | Bowel-clear zone empty; cecum in view | Hard grasp on bowel → blanch, "gentle" pulse | "Head down, left tilt. Sweep the bowel out." |
| 3 | Find appendix | Grasper (suprapubic) | Follow the taenia to the base; lift by the mesoappendix | Tip in elevated zone; taenia + ileum in scope frame | Grabbing inflamed body → perforation warning | "Follow the taenia down. Lift by the mesoappendix." |
| 4 | **Decision: true base** | — | Pick the true base among 3 marked points | Correct point | Wrong → camera shows taeniae convergence; logged | "Before we secure anything: where's the true base?" |
| 5 | Window mesoappendix | Maryland dissector (LLQ) | Push through at the base, spread twice | Window ≥ authored size, ≤ 1 cm from base | Too far from base → amber; artery touched → ooze | "Make a window right at the base." |
| 6 | Seal + divide | Vessel sealer (LLQ) | Close jaws across the artery zone, hold ~1.5 s until the ring fills, divide | Sealed and divided, no active bleed | Early release → bleed on divide; active jaws on cecum → burn | "Seal the mesoappendix. Hold until done." |
| 7 | Staple base | Endo stapler (12 mm port) | Slide across the base band, close, check, fire | Within 5 mm of cecum, no ileum in jaws, fired | Too high → "stump too long"; ileum/cecum in jaws → block fire once | "Flush with the cecum. Check the jaws, then fire." |
| 8 | Bag + extract | Retrieval bag, grasper | Drop appendix into the bag, cinch, withdraw | Specimen in closed bag, out through the port | Specimen touches wall outside bag → contamination flag | "Into the bag. Out through the camera port." |
| 9 | Suction + inspect | Suction-irrigator (LLQ) | Suction the pool; dwell on staple line and mesoappendix | Pool below threshold; both dwells done | Irrigating in the perforated variant → recap note | "Suction the fluid. Check the staple line is dry." |
| 10 | Close | Auto | — | Auto | — | "Ports out under vision. Nice work." |

Judge fast path: pre-exposed field (skip 2) and skip 9.

## Mechanics Without Haptics

- Snap only on commit actions (trocar, clip, stapler, bag), never on dissection.
- Force proxy: tissue deforms → blanches → red strain; the tip lags the hand slightly under tension; hand speed over ~10 cm/s on tissue counts as rough handling.
- Controller vibration on jaw close, seal complete, clip/staple fire and errors.
- Target zones and a ghost-instrument demo appear only after a stall.
- Seal, divide, staple and bag are authored state changes driven by these checks; true cutting topology is not needed for the demo (the existing wall cut and bleed models stay as extras).
- Feel: sound first (insufflator hum, trocar click, sealer sizzle, stapler double-click, suction slurp, monitor beeps), then visibly pivoting shafts, then smoke and lens fog on sealing and a bleed on an incomplete seal.

## Scoring and Coaching

Metrics: time per step, path length per hand, wrong-structure contacts, max tissue stretch, instrument out of the scope view, burns to non-target tissue, step order, decision answers, hints used. Proposed weighting: safety 50%, decisions 20%, economy 20%, tissue respect 10%, shown as a proficiency band in the recap. Coaching escalates on stall at ~15/25/40/60 s, ending with an offer to auto-complete the step. Route the existing server alerts, hint escalation and pre-rendered reflex clips to the Quest instead of only the browser.

## Catalog Changes (`services/preop/src/catalog/procedures.ts`)

- Bind the stapler to a 12 mm suprapubic port (not the camera's umbilical port).
- Add the base-confirmation decision before `staple_base`.
- Replace touch-only checks with the stronger checks above (zone, dwell, hold, seated, in-bag) and add the new check types to the engine and the Unity DTO mirror.
- Recap note: endoloops and clips are equally valid for the stump (WSES 2020).

## Ownership

The surgery Codex thread owns instrument mechanics, port pivoting, the window and scope view, step checks, the catalog step changes and coach delivery to the Quest. The main Codex thread owns tissue physics, AR body registration and scene composition in `NativeSessionBuild`. The handoff thread owns the run context into the OR. Thresholds are design guesses to tune on the headset; keeping controllers at least 10 cm above a real participant's skin is an untested safety precaution.
