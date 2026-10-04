# How a FinchNode chart becomes a Scalpal run

## Contents
- The pipeline in one picture
- Stage 1: chart to case (preop service)
- Stage 2: explore board
- Stage 3: diagnosis office
- Stage 4: handoff
- Stage 5: operating room
- Stage 6: recap and dashboard
- Where each piece of a case lives

Paths are repo-relative. `preop/` means `services/preop/`, `Q/` means `apps/quest/Assets/Scalpal/`.

## The pipeline in one picture

```
FinchNode demo API (api.finchnode.com/demo/v1)        or preop/test/fixtures/*.json offline
  GET /scenarios                -> scenario id + subject (patient id)
  GET /users/<subject>/records  -> normalized health record (synthetic)
        |
preop service (:8787)
  buildBrief(record)            -> PreopBrief: patient, risk flags, data gaps, chart lines, spoken summary
  buildCase(record, plan)       -> SurgicalCase: brief + CASE_PLANS[subject] + PROCEDURES[plan.procedureId]
  /patients  /patients/:id/case  /unity/bundle (also exported into the APK as scalpal_bundle.json)
        |
Quest explore board             lists patients with a case and an encounter
        |
Diagnosis office                POST /interviews: voiced patient (patient.md) + fixed rounds (interview.json)
        |                       scorecard: total, diagnosisResult, procedureChosenCorrectly, carryoverItems
Handoff (HandoffTicket)         scalpal.handoff.v1, runId; wrong plan -> challenge and consequence cards
        |
Operating room                  GET /patients/:id/case (live), POST /coach/sessions
                                coach prompt = case + patient_status.md + office carryover
                                body simulation, simulated monitor, Time-Out preop-check
        |
Recap                           RunResult scalpal.run_result.v1, exported and imported into the dashboard
```

## Stage 1: chart to case (preop service)

- `preop/src/finchnode.ts` reads the record. Any `patient-demo-*` subject (or any subject without an API key) reads the keyless demo API. Records cache for 60 s; a 429 retries once if `Retry-After` is 5 s or less.
- `preop/src/brief.ts` `buildBrief` turns the record into a `PreopBrief`. Age is computed at `meta.dataAsOf`, not today. It keeps live meds, conditions and allergies, the latest lab of each kind, and produces **14 risk flag types** by rule and **data gaps** for anything missing ("absence of data is unknown, never none"). Details in [finchnode.md](finchnode.md).
- `preop/src/case-builder.ts` `buildCase` picks the plan: `CASE_PLANS[subject]` (authored) or a fallback by age (under 30: open appendectomy, otherwise lap cholecystectomy). **The chart never picks the procedure.** It pins each flag to a procedure step through `STEP_ROLES` and `CONSIDERATION_NOTES`, builds the pre-op checklist (flags plus distractors), the instrument and anatomy lists, and `bodyScale` for children. Status is `ready`, or `needs_review` when the chart has gaps.
- Every payload is "Unity safe": no nulls, no nested arrays, keys that are C# identifiers, and every response carries `actions` (no dead routes). A test checks every endpoint against the C# DTOs field by field.

## Stage 2: explore board

`Q/Shell/Runtime/ExplorePatientModel.cs` lists a patient when the case status is `ready` or `needs_review`, `encounterAvailable` is true (an authored encounter exists), the brief is synthetic, and the chart has a name (charts without demographics show as "Unnamed" and are hidden). Without an encounter the card says "Interview coming soon".

## Stage 3: diagnosis office

- The Quest calls `GET /patients/:id/case`, starts a shared attempt in SpacetimeDB, then `POST /interviews {patientId}`.
- The patient voice is one ElevenLabs agent with no tools. Each session sends `patient.md` as the persona and the encounter's voice. The learner taps or says A to D; the server sends the patient a `[DIRECTION]` (the choice's `patientCue`) and a `[CLINICIAN]` turn (the choice text). Grades never leave the server.
- The scorecard: full weight for correct, half for partial. `diagnosisResult` comes from the diagnosis round, `procedureChosenCorrectly` from the plan round (partial does not count), and `carryoverItems` (Time-Out chips) from the `covers` of correct and partial picks against each chart risk.
- Scalpal (the coach) is not in the office. An older free-form `/encounters` engine still exists and still owns the persona, voice and demographics, which is why a package needs both an encounter and the content files. See [office.md](office.md).

## Stage 4: handoff

`Q/Handoff/Runtime/HandoffRun.cs` mints a `HandoffTicket` (`scalpal.handoff.v1`, `contentVersion 0.1.0`, a 32-hex `runId`) carrying patient, encounter, procedure, presentation mode (AR on a real volunteer or full VR) and the scorecard. If the diagnosis or plan pick was wrong, the learner sees a challenge card and a consequence card, then operates on the authored procedure anyway. `HandoffRun.Supported` admits only `open_appendectomy` and `lap_appendectomy`; anything else shows "Surgery coming soon".

## Stage 5: operating room

- `NativeCaseSession` re-fetches the case live (no bundle fallback) and verifies ids, then `POST /coach/sessions`. The coach prompt holds only this case: patient, urgency, indication, presentation, flags and gaps, `patient_status.md`, the office carryover, the procedure and its steps with coaching text and per-patient considerations, and facts for each structure.
- At Time-Out the learner confirms risks; `POST /patients/:id/preop-check` scores them. In AR, the coach can pull a measured baseline (heart and breathing rate) from the Presage vitals service.
- `open_appendectomy` runs the open-body simulation: a reducer turns measured tool actions into tissue facts; milestones (one per step) are predicates over facts; guardrails turn bad outcomes into mistakes; bleeding feeds the simulated monitor (ATLS classes) and can kill the patient. The same reducer exists in TypeScript and C# and must match number for number. See [procedures.md](procedures.md).

## Stage 6: recap and dashboard

The headset builds a `RunResult` (`scalpal.run_result.v1`) with the office scorecard and the surgery grade. The dashboard imports it by hand today. Urgency must be elective, urgent or emergency and `diagnosisResult` one of correct, partial, incorrect, missing, so authored values must stay inside those sets.

## Where each piece of a case lives

| Piece | File | Package field |
| --- | --- | --- |
| Chart | FinchNode, snapshot in `preop/test/fixtures/<subject>.json` | `fixture/record.json` (new subjects only) |
| Scenario row | `preop/test/fixtures/scenarios.json` | `fixture/scenario.json` (new subjects only) |
| Case plan | `preop/src/catalog/cases.ts` `CASE_PLANS` | `case.json` `plan` |
| Encounter | `preop/src/catalog/encounters.ts` `ENCOUNTERS` | `case.json` `encounter` |
| Patient persona | `preop/content/patients/<subject>/patient.md` | `content/patient.md` |
| Interview rounds | `preop/content/patients/<subject>/interview.json` | `content/interview.json` |
| OR patient context | `preop/content/patients/<subject>/patient_status.md` | `content/patient_status.md` |
| Dossier (optional) | `docs/patients/<subject>.md` | `dossier.md` |
| Procedure | `preop/src/catalog/procedures.ts` (+ coaching, step roles) | not installed; tier 2 work |
