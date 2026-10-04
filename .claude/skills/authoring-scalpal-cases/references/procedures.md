# Procedures: what exists and what a new one costs

## Contents
- Playability today
- Procedure schema
- Laparoscopic step mode
- Open-body mode (open appendectomy)
- Body facts, outcomes and grading
- Coaching data that goes with a procedure
- Tier 2: what a new procedure needs
- Worked estimate: cholecystectomy in the headset

Paths: `S/` = `services/preop/src/`, `Q/` = `apps/quest/Assets/Scalpal/`.

## Playability today

| Procedure | Coach, laptop, C# engine | Quest operating room |
| --- | --- | --- |
| `open_appendectomy` | yes | yes, the main path (AR and VR) |
| `lap_appendectomy` | yes | yes, as the offline "advanced" variant |
| `lap_cholecystectomy` | yes | no ("Surgery coming soon") |
| `lap_sigmoid_colectomy` | yes | no |

The gate is `HandoffRun.Supported` in `Q/Handoff/Runtime/HandoffRun.cs`, and the native OR only contains appendix-region anatomy. A tier 1 package (new patient on an appendectomy) needs no code.

## Procedure schema

`S/types.ts`, built with helpers in `S/catalog/procedures.ts` (`procedure`, `chain`, `check`, `mistake`, `port`).

```ts
Procedure {
  id; title; shortTitle; approach; summary; typicalMinutes;   // typicalMinutes is informational
  structures: string[];        // anatomy ids in the case
  focusStructures: string[];   // subset, manifest only
  ports: Port[];               // [] for open surgery
  steps: ProcedureStep[];      // array order is the order; no branching
  firstStep;                   // derived
  openBody?: OpenBodyCase;     // present = open-body mode
}
ProcedureStep { id; title; instruction; action; instrumentId; targets; portIds; check; mistakes; hints; next }
SuccessCheck { type: place_ports | touch_target | identify_targets | apply_count | confirm | body_predicate; targets; count }
StepMistake  { id; trigger: touch_structure | wrong_identification | wrong_order | excess_energy | guardrail; structure; severity; feedback }
Port { id; label; sizeMm; position: {x,y,z}; instrumentIds }   // metres from the umbilicus: +x patient left, +y anterior, +z cranial
```

Anatomy ids (33) live in `S/catalog/anatomy.ts`; Unity matches meshes named `anat_<id>`. Instruments (23) live in `S/catalog/instruments.ts`; Unity spawns `inst_<id>` prefabs. `npm run validate` checks 53 referential rules (ids, ports, step chain, coaching, roles, encounters).

## Laparoscopic step mode

Each step passes when its check is met by events: `place_port` for ports, `touch` with the step's instrument on targets, `identify` for identification, a count for `apply_count`, or `confirm`. A mistake (touching or misidentifying a listed structure) is recorded and the step does not advance. A perfect run is generated from the checks, so new lap steps need no fixture code.

## Open-body mode (open appendectomy)

`S/catalog/open-appendectomy.ts`. The body is a set of tissues; a case-agnostic reducer (`S/open-body.ts`, mirrored in `Q/Exercises/Engine/BodyState.cs`) applies measured tool actions and writes facts.

- **Tissues:** skin, fat, fascia (fibers), muscle (splittable, perfused 0.3 ml/s), peritoneum (tentable), then organs behind the wall (order < 0): appendix (hollow), mesoappendix (2 ml/s), appendicular artery (2 ml/s), cecum and terminal ileum (hollow, critical), iliac vessels (critical, 8 ml/s), ureter (critical). An organ can only be touched once every wall layer is opened.
- **Milestones = steps:** `mark_incision, incise_skin, open_fascia, split_muscle, open_peritoneum, deliver_appendix, divide_mesoappendix, ligate_base, inspect_clean, close`. Each is a list of fact predicates (`gte`, `lte`, `eq`), for example `divide_mesoappendix`: cut between clamps, two ties, tied both sides, no active bleeds.
- **Guardrails:** off_mark, deep_skin_cut, mark_far, bowel_injury, critical_injury, cut_before_control, split_dont_cut, lift_first, fiber_direction, rough_handling. Each becomes a mistake with spoken feedback.
- **Decision:** "Where is the true base?" (true_base).
- Steps are soft guidance: the learner can do anything, and the current step is the first unmet milestone.
- **Tool verbs:** scalpel and scissors cut, marker marks, forceps grasp and retract, retractor retracts, hemostat and right-angle clamp clamp and release, suture ties, cautery and sealer seal, suction suctions and inspects.

## Body facts, outcomes and grading

Per-tissue facts include `marked, markErrorMm, markLengthMm, opened, divided, cutCoverage, cutDepthMm, tentedBeforeCut, delivered, splitWidthMm, cutBetweenClamps, tieCount, tiedBothSides, crushed, tieDistanceMm, stumpLengthMm, cutAboveTie, removed, leaking, sealed, inspectionMs, decision_<choice>, closed, bleeding, openInjuries`. Global: `bloodLostMl, poolMl, activeBleeds, contamination`. A fact never written fails every predicate.

Outcomes a guardrail can match: `not_exposed, not_cuttable, no_cut, muscle_cut, across_fibers, untented_cut, cut_unsecured, hollow_leak, critical_injury, missing_instance, not_clamped, rebleed, rough_handling`.

Grade (illustrative, uncalibrated, out of 80): safety 50 (minus 10 per high and 5 per moderate mistake, blood loss, active bleeds, contamination), decisions 20, tissue handling 10.

Outside the field, Unity reports region injuries: head (instant death), neck (300 ml/min), chest (150), limbs (20). Bleeding runs 8x for the demo and death comes at 50% of blood volume (70 ml/kg adult, 80 child): an open iliac kills a 70 kg adult in about 38 s.

## Coaching data that goes with a procedure

- `STEP_COACHING[procedureId][stepId] = {why, lookHere}` for every step, and `STRUCTURE_FACTS[anatomyId]` for every anatomy id (`S/catalog/coach-knowledge.ts`).
- `STEP_ROLES[procedureId]` maps `entry, ports, critical, bleeding, hemostasis` to step ids; chart flags pin to steps through these roles (`S/catalog/cases.ts`).
- Briefing flythrough narration is authored only for `open_appendectomy` (`S/briefing.ts`).

## Tier 2: what a new procedure needs

Data (preop, validated by `npm run validate`):
1. The procedure in `PROCEDURES`, using existing anatomy and instrument ids where possible.
2. `STEP_COACHING` for every step, `STEP_ROLES` with all five roles, `STRUCTURE_FACTS` for new anatomy.
3. `procedureSite` in `S/encounter-carryover.ts` and its C# twin `EncounterContract.Site`.
4. `npm run gen:unity`, `npm run export:unity -- --offline`, `python3 scripts/anatomy/merge_targets.py` (atlas coverage).

Code (TypeScript and C#, must stay identical):
5. Open body only: ideal actions for every new step id in `S/open-body-fixtures.ts` and `CaseRunner.PerfectBodyActions`. Fixtures key on step id alone, so reusing `close` silently reuses the appendectomy's.
6. Branches on the literal `"open_appendectomy"`: stuck policy, hint style and keys (`S/coach.ts`), briefing (`S/briefing.ts`), laptop simulator (`S/open-body-sim.ts`), region alarm text ("the field is the lower right abdomen").
7. New verbs or reducer behavior: `open-body.ts` and `BodyState.cs`, regenerate the golden log. C# event predicates support only distance, depth, length, angle, speed and force.
8. `unity-check` assumes `open_appendectomy` is the first open-body procedure.

Unity (needs an APK):
9. `HandoffRun.Supported`, `ExplorePatientModel.ProcedureShort`.
10. OR anatomy for the region (the native OR bakes one appendix-region prefab), meshes with colliders for every target.
11. Open surgery: the five wall layers and McBurney wound placement are hard-coded; organ mobility, delivery, base references and appearance key on appendix ids; the open tool tray is a fixed list.
12. Briefing atlas and voice clips, robot demo step, realtime `OPEN_CASE_INSTRUMENTS`.

## Worked estimate: cholecystectomy in the headset

Exists: catalog anatomy (liver, gallbladder, cystic duct and artery, bile ducts), atlas meshes, all laparoscopic instrument prefabs, a lap cholecystectomy that completes in TypeScript and C#. Missing: the handoff gate, an OR anatomy prefab with liver and biliary parts, port placement for the right upper quadrant, and (for open) a subcostal wound, new wall layers, liver retraction, gallbladder mobility, cystic structure references, clip modeling, fixtures, coach branches, briefing. The critical view of safety should be a hard gate before any clip or cut (multi-society safe cholecystectomy guideline, 2020).
