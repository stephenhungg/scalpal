# Surgery Procedure Build Spec

Updated October 3, 2026. **Decision (Stephen): the operating room procedure is an open appendectomy**: scalpel through the abdominal wall, work directly in the wound. It runs in both OR modes (AR on a real reclining person, or full VR). Laparoscopic appendectomy is kept as a later "advanced" mode; its design and evidence are in [surgery procedure research](research/surgery-procedure.md). Handoff into the OR: [office to OR handoff](office-to-or-handoff.md). UX rules: [experience UX](experience-ux.md#5-operating-room-ar-or-vr).

Why open: in AR the learner cuts into the belly they can see and works with their hands in the wound, which is more legible to a first-time viewer than watching a camera monitor through ports. It needs no port pivoting, scope monitor or virtual camera assistant. It turns the existing layered abdominal-wall volume, blade cutting and vessel bleeding (`Anatomy/Runtime/Tissue/**`, `NativeVolumeSimulation`, `NativeVesselSimulation`) from unscored extras into the main path. Open appendectomy is a real, still-taught operation (preferred when laparoscopy is unavailable or converted); most appendectomies today are laparoscopic, which the recap can say.

## Architecture: Open Body, Cases on Top (Stephen, latest)

The body is not a scripted step sequence. The learner may do anything to it with any tool, and the simulation responds the same way in every case. Cases only define goals, guardrails and guidance; the coach and grader watch what actually happened.

1. **Body simulation (case-agnostic).** Every tool is a small set of verbs: cut, grasp/retract, clamp, tie, cauterize/seal, suction, mark, place. Every tissue has properties: layer and order (skin, fat, fascia with fiber direction, muscle that splits, peritoneum, bowel, cecum, appendix, mesoappendix, vessels), cuttable/splittable, perfused (bleeds when cut, stops when clamped/tied/sealed), hollow (cutting bowel leaks and contaminates), critical (iliac vessels, ureter). Tool × tissue rules produce the outcome: cutting a perfused vessel bleeds at a rate until it is clamped; cutting the appendix base without a tie leaks; splitting muscle along fibers is clean, cutting across it bleeds more. The learner can cut in the wrong place, open the wrong layer, nick bowel, or skip a step, and sees the real consequence. No case code decides what a tool does.
2. **Event log and body state.** Every action emits a structured event (time, tool, verb, tissue, layer, location in the registered torso frame, speed/force proxy, outcome), and the body keeps queryable state (layers opened, incision line and length, structures clamped/tied/divided, organs delivered, active bleeds and blood lost, contamination, specimen removed).
3. **Case = goals + guardrails + guidance (data, not code).** A case declares milestones as predicates over body state (appendix removed; stump tied within 5 mm of the cecum; mesoappendix vessels secured; no active bleed; wound closed), safety guardrails as predicates over events (bowel or cecum injured, iliac vessel cut, cutting before clamping, blade below the peritoneum without tenting), an expected order as a soft reference rather than a gate, and decision prompts (true base). Adding a case means authoring data, not new step code.
4. **Coach (Jarvis voice) and grader.** Deterministic detectors evaluate milestones and guardrails from the event log in real time; Jarvis receives those structured facts and gives guidance ("you're cutting across the muscle fibers; split them instead") and Socratic prompts, never inventing events or executing scene code. The grader scores at the end from the same log: milestones reached, guardrail violations, blood loss, order deviations, decisions, economy, hints. The LLM writes the recap feedback only from those facts.

Consequence of this design: the 10-row table below is the *expected* open appendectomy path that drives milestones, coaching lines and the fast path. It does not gate what the learner may do. A learner who goes off-path is coached and graded, not blocked; only physically impossible actions (cutting a layer you have not exposed) are prevented by the simulation itself.

## Where It Stands (origin/main, audited)

Appendectomy is the only procedure that runs, authored as laparoscopic in `services/preop/src/catalog/procedures.ts`. Every step completes on a trigger-activated tool tip touching a named mesh; ports are floating spheres; nothing is actually clipped, divided, ligated or removed. Real today: holding 15 mostly laparoscopic instruments, tip contact, grasp deformation of appendix/mesoappendix/artery, a layered abdominal-wall patch the blade can cut (now projected over the appendix field), and a vessel bleed that sealing stops and suction drains (unscored). Coach alerts and hint escalation exist server-side but never reach the Quest. `NativeCaseSession` is hardcoded to one patient and procedure (the handoff thread is fixing that). Duplicates to remove: two tip-contact scripts, two port builders, a leftover sandbox `TrainingPatch`, `CaseRunner.cs` copying `engine.ts`.

## Open Appendectomy Technique (source: StatPearls, Appendectomy, NBK580514, 2025)

McBurney incision one-third of the way from the anterior superior iliac spine to the umbilicus along Langer lines (or a transverse Rockey-Davis incision near it). Divide skin and subcutaneous fat; open the external oblique aponeurosis along its fibers; split (not cut) the internal oblique and transversus abdominis along their fibers; grasp the peritoneum with forceps and incise it with a scalpel. Locate the cecum and follow the taeniae coli to the appendix; deliver it into the wound. Dissect the mesoappendix, divide the appendiceal vessels between clamps and ligate them. Crush the base with a right-angle clamp, move the clamp distally, ligate the base, and excise the appendix with a blade; optionally invert the stump with a purse-string. Confirm hemostasis, close peritoneum and fascia, close skin. Keep the stump under 5 mm. Most common complication: surgical site infection.

## Expected Path (milestones and guidance, not a script; 9 learner steps, ~4–6 min)

| # | Step | Tool | Learner action | Success check | Mistake → feedback | Coach line |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Landmarks + mark | Skin marker | Touch the hip bone (ASIS) and the umbilicus, then draw the incision line at McBurney's point | Line within 2 cm of McBurney's point, oblique along Langer lines, 5–8 cm | Line midline or too high → line glows amber, ghost line shown | "Find the hip bone and belly button. Mark a third of the way." |
| 1 | Skin incision | Scalpel | Draw the blade along the marked line in one controlled stroke | Skin layer divided along ≥ 80% of the line, depth only through skin + fat | Stroke off the line or too deep (blade below fascia) → red flash, buzz | "One smooth stroke along your line. Skin only." |
| 2 | Open the fascia | Scalpel or scissors | Nick the external oblique aponeurosis and extend along its fibers | Aponeurosis opened along the fiber direction (± 25°) | Cutting across fibers → amber, recap note | "Open the aponeurosis along its fibers." |
| 3 | Split the muscle | Retractors (two hands) | Insert both retractors and pull apart to split internal oblique and transversus | Split gap ≥ authored width; no blade used on muscle | Using the blade on muscle → "split, don't cut" | "Now split the muscle. Pull, don't cut." |
| 4 | Open the peritoneum | Forceps + scalpel | Tent the peritoneum with forceps, then nick it | Peritoneum tented before the nick; opening made | Nicking without tenting → bowel flashes red, "lift first" | "Lift the peritoneum first, then nick it." |
| 5 | Deliver the appendix | Babcock / grasper | Find the cecum, follow the taenia to the appendix, lift it out into the wound | Appendix delivered above the wound plane | Grabbing the inflamed body hard → perforation warning | "Follow the taenia to the appendix. Lift it out gently." |
| 6 | Divide the mesoappendix | Clamps ×2 + scissors + tie | Clamp the mesoappendix vessels twice, cut between, tie each side | Two clamps placed, cut between them, both tied; no active bleed | Cutting before clamping → arterial bleed, pool grows, coach alert | "Clamp twice, cut between, then tie." |
| 7 | **Decision + base** | Right-angle clamp + tie + scalpel | Answer "where is the true base?", crush the base, move the clamp up, tie the base, cut above the tie | Correct base; tie within 5 mm of the cecum; cut between tie and clamp | Tie too high → "stump too long" (stump appendicitis risk); cut below the tie → leak, red | "Crush, tie at the base, cut above your tie." |
| 8 | Check + clean | Suction / sponge | Suction or sponge the field, dwell on the stump and the mesoappendix | Pool below threshold; both dwells done | Leaving blood in the field → amber, recap note | "Dry field? Check the stump and the vessels." |
| 9 | Close | Auto (or confirm) | — | Auto: layers close in sequence, skin closes | — | "Close in layers. Nice work." |

Judge fast path: pre-marked line (skip 0) and auto-close; keep the skin incision, split, delivery and base ligation as the live beats.

## What Exists vs What to Build

| Need | Reuse | Build |
| --- | --- | --- |
| Layered wall (skin, fat, fascia, muscle, peritoneum) | `Tissue/Volume/*` three-layer volume and blade cutting | Add fascia/muscle/peritoneum layer identities and per-layer success checks; fiber direction for fascia; split (retract) behavior for muscle instead of cut; tenting for peritoneum |
| Incision placement | AR body registration landmarks (ASIS/umbilicus proxies), VR mannequin fit | Skin marker tool, McBurney landmark computation, line-quality check |
| Wound + retraction | Grasp/deform cage | Retractor tool that holds the wound open and stays put when released |
| Appendix delivery | Grasp deformation on appendix/mesoappendix | Lift-out-of-wound check; Babcock (reuse grasper with open-instrument look) |
| Clamps and ties | Clip applier logic (`apply_count`), vessel bleed model | Hemostat/right-angle clamp that stays clamped; "tie" action (tap clamp with suture tool) |
| Cut between clamps / base excision | Blade cut and vessel bleed | Authored division state changes driven by clamp + tie order |
| Bleeding | `NativeVesselSimulation` (opens on blade, sealing stops, suction drains) | Score it: active bleed blocks step success; counts as a safety error |
| Coaching | Server alerts, hint escalation, reflex clips | Deliver to the Quest; stall escalation ~15/25/40/60 s ending in auto-complete offer |

Open-surgery instruments to add (simple original models, same prefab pipeline as `InstrumentAssetBuilder`): skin marker, toothed forceps, two retractors (Army-Navy style), Babcock, two hemostats, right-angle clamp, Metzenbaum scissors, suture tie. Reuse scalpel, suction and electrocautery (hook cautery as a cautery pen).

## Mechanics Without Haptics

- Blade cuts follow the stroke; depth comes from how far the tip goes below the surface, shown by which layer opens. Snap only on commit actions (clamp lock, tie, stapler/bag in the lap mode).
- Force proxy: tissue deforms → blanches → red strain; hand speed over ~10 cm/s on tissue counts as rough handling.
- Controller vibration on blade contact, clamp lock, tie, and errors.
- Target zones and a ghost-instrument demo appear only after a stall.
- Feel, in order of payoff: sound (blade on skin, clamp ratchet click, suction slurp, monitor beeps), layered wound visuals with wet specular shading and fat texture, bleeding that responds to clamps, the existing grasp deformation.
- AR safety: keep controllers and virtual instruments at least 10 cm above a real participant's skin (untested precaution); the wound is virtual.

## Scoring and Coaching

Metrics: incision placement error, layers divided in order, step order, wrong-structure contacts (bowel, cecum, iliac vessels), max tissue stretch, active-bleed time and blood volume lost, stump length, decision answer, time per step, path length per hand, hints used. Proposed weighting: safety 50%, decisions 20%, economy 20%, tissue respect 10%, shown as a proficiency band. Coaching speaks one line under 12 words at step start, again only after a stall or an error; critique waits for the recap.

## Catalog Changes (`services/preop/src/catalog/procedures.ts`)

Add `open_appendectomy` as the OR procedure for appendicitis cases (keep `lap_appendectomy` for the advanced mode). Add check types for the new mechanics (line placement, layer divided, split width, tented-then-cut, delivered, clamp-cut-tie order, tie distance, dwell) to the engine, `CaseRunner.cs` and the Unity DTO mirror, with tests. Recap note: laparoscopic is the modern default; open is valid and preferred when laparoscopy is unavailable.

## Ownership

The surgery Codex thread owns open-surgery instruments, the step mechanics and checks, the catalog `open_appendectomy` procedure and engine check types, bleeding scoring and coach delivery to the Quest. The main Codex thread owns the tissue volume/layer physics and AR body registration; the surgery thread requests layer behaviors (fascia fibers, muscle split, peritoneum tenting) through it or coordinates small focused edits. The handoff thread owns the run context into the OR. Thresholds are design guesses to tune on the headset.

## Open-Body Build Checkpoint — October 3

Built: `open_appendectomy` now contains ten state-predicate milestones, event guardrails and the true-base decision as data. `engine.ts` and `CaseRunner` reduce the same case-independent tool verbs into persistent body facts and an immutable action history. Actions carry registered-torso coordinates, an active-practice monotonic clock, measured geometry and tool instance IDs; there is no step ID in physical evidence. All milestones are evaluated after every action, off-path harms remain effective, and a later bleed invalidates the live no-bleed predicate. Missing measurements do not satisfy threshold predicates. Two distinct retractors, two clamps and ties on both sides, a distal moved clamp, stump length and the base answer have explicit checks. Expected order is soft guidance and recorded separately.

Component evidence: service suite244 passing / two live-provider skips and TypeScript typecheck; Unity batch65 synthetic body-state assertions; instrument build311 Editor assertions. The old scripted checks were removed. The catalog's older laparoscopic cases remain available while native open-body interaction/handoff integration is built. This checkpoint does not yet establish a physical tool-to-wound session, existing-vessel measurement exchange, AR registration, sound playback or complete OR run. The semantic reducer's authored bleeding rates and lumped tissue-level control are teaching approximations, not calibrated physiology.
