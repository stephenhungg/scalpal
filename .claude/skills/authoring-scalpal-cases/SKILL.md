---
name: authoring-scalpal-cases
description: Authors Scalpal case packages, which turn a synthetic FinchNode patient chart into a playable Scalpal case. A package covers the case plan, the voiced diagnosis-office interview with its multiple-choice rounds, the patient's persona and story, and the OR context for the coach, and it is checked against the repo's own validators and installed into the preop service. Use when someone wants to create, edit, clone, validate or install a Scalpal patient case, scenario, encounter, interview or patient, write interview.json / patient.md / patient_status.md, re-plan a FinchNode subject, or asks what a new procedure would need to be playable in the headset.
---

# Authoring Scalpal cases

A Scalpal case is one synthetic patient carried through the whole run: explore board, diagnosis office (voiced patient plus "what next" rounds), operating room (the Scalpal coach, the simulated body and monitor), recap. You write it as a **case package**, check it with the bundled tool (which imports the repo's real validators), install it, and run the repo gates.

Read [references/architecture.md](references/architecture.md) once if you have not seen how FinchNode data becomes a case. The rest of this file is the procedure.

## Ground rules (non-negotiable)

1. **Patients are FinchNode subjects.** Scalpal cannot invent a patient the live service will list. You re-author an existing demo subject, or use a subject FinchNode added. A hand-made fixture works offline only. See [references/finchnode.md](references/finchnode.md).
2. **Two layers, never mixed.** The chart (meds, allergies, problems, labs, demographics) is FinchNode's and is not edited. The acute story (presentation, history answers, exam findings, test results, diagnosis) is authored teaching content and is labeled as such. Never present authored facts as chart data, and never strip synthetic markers or disclaimers.
3. **The chart wins.** Persona name, age and sex must equal the chart at its `dataAsOf` date. Every charted allergy and active drug must appear in the authored history answers. Every chart risk flag must be elicitable in the interview.
4. **The patient never knows the diagnosis.** `patient.md`, the opener and the chief complaint must not name or imply today's diagnosis or procedure.
5. **The OR always runs the case plan's procedure.** The interview's plan round scores the learner; it never changes the surgery.
6. **Only appendectomy is playable in the headset today** (`open_appendectomy`, `lap_appendectomy`). Other catalog procedures interview fine but stop at "Surgery coming soon". A new procedure is engineering work, not a data package. See [references/procedures.md](references/procedures.md).
7. **House style:** no em or en dashes in any authored text; plain spoken language for the patient; one to three sentence patient replies.
8. **Clinical claims need a source.** Management choices, thresholds and antibiotics follow published guidance ([references/clinical.md](references/clinical.md)). If you cannot source a claim, flag it for human review; do not invent it.

## The tool

Run from `services/preop` (it imports the repo's TypeScript and its deps). Install deps once with `npm ci`.

```sh
cd services/preop
T=../../.claude/skills/authoring-scalpal-cases/scripts/scalpal-case.mts
npx tsx $T list                       # every FinchNode subject: plan, procedure, headset or interview-only
npx tsx $T chart <subject>            # the chart Scalpal builds: age, sex, meds, allergies, flags, gaps
npx tsx $T export <subject> <dir>     # an existing case as a package (best starting template)
npx tsx $T check <dir>                # validate the package; touches nothing
npx tsx $T trial <dir>                # install into a throwaway worktree, run every preop test, list failures
npx tsx $T install <dir>              # dry run: lists files it would write
npx tsx $T install <dir> --write      # apply: catalogs, content files, QA list, fixture if any
npx tsx $T gates                      # validate, typecheck, tests, gen:unity, offline bundle, C# check
```

`check` merges the package into the catalogs in memory and runs `validateCatalog`, `buildCase`, `validateInterview`, the chart-risk coverage rules and the test-suite rules (demographics, allergy and drug mentions, diagnosis leaks, voice groups). Errors block install. Warnings explain what degrades. `--force` on install only when you are also making the code change an error asks for (for example adding a voice key).

The tool never calls FinchNode or a model provider. Charts come from `services/preop/test/fixtures/`.

## Workflow

Copy this checklist into your reply and tick it off.

```
- [ ] 1. Scope: tier 1 (patient on an existing procedure) or tier 2 (new procedure)?
- [ ] 2. Pick the subject and read its chart (list, chart)
- [ ] 3. Start from a template (export the closest existing case)
- [ ] 4. Write the case plan
- [ ] 5. Write the encounter (persona, history, exam, tests, diagnosis, rubric)
- [ ] 6. Write content/patient.md
- [ ] 7. Write content/interview.json (7 to 9 rounds)
- [ ] 8. Write content/patient_status.md
- [ ] 9. check until 0 errors; read every warning; trial until the test suite passes
- [ ] 10. install (dry run, then --write)
- [ ] 11. gates until green; record anything not run
- [ ] 12. Report: what changed, what was verified, what needs Unity or a human
```

### 1. Scope

Establish what the requester wants to happen in the headset. If you cannot ask, assume tier 1 on `open_appendectomy` and say so. If they need the surgery itself to run, the procedure must be `open_appendectomy` (the main path) or `lap_appendectomy`. Anything else is tier 2: write the package for the office and coach, and hand back the engineering list from [references/procedures.md](references/procedures.md). Say this plainly up front.

### 2. Pick the subject

`list` shows the ten demo subjects. Six are real patient records; the rest exercise error paths (rate limit, revoked consent, partial consent, missing source) and are poor patients. `chart <subject>` prints exactly what the brief will show. Pick a chart whose age, sex and history fit the story. Re-authoring a subject keeps its `voiceKey` (every premade voice is taken, and the voice-group test pins who shares). Useful hooks: penicillin allergy (`patient-demo-001`), apixaban plus aspirin and a contrast allergy (`patient-demo-polypharmacy`), the only child (`patient-demo-pediatric-asthma`, a parent speaks), latex allergy and two disagreeing sources (`patient-demo-multi-source`), an empty chart for a previously healthy adult (`patient-demo-sparse`). Re-authoring a subject replaces its current case.

### 3. Template

`export` the existing case closest to yours (`assets/example-priya/` is a complete, passing open appendectomy package). Copy its structure, not its item style: several of its correct choices are the longest option, which `check` now flags. Edit in place. Keep `schema`, set `subject`, `scenarioId`, `title`, and fill `objectives`, `sources` and `review`.

### 4. Case plan (`case.json` -> `plan`)

`procedureId`, `urgency` (elective, urgent, emergency), `indication` (one line), `presentation` (the authored acute story in clinical prose, no claims about chart contents), optional `chartNotes` that only fire when the chart supports them (`whenFlags`, `whenGaps`, `minSources`). Field rules: [references/package-format.md](references/package-format.md).

### 5. Encounter (`case.json` -> `encounter`)

The scored fact base: persona, first-person history for each topic, exam reactions and findings, test results, diagnosis and procedure keyword groups, at least five differentials, critical and expected rubric items. Topics, maneuvers and tests are closed vocabularies. Rules and vocabularies: [references/office.md](references/office.md).

### 6. `content/patient.md`

Second person, the patient's (or parent's) knowledge only: who they are, how they talk, how they feel now, what they think is going on (lay worries, never the diagnosis), their story, background from the chart in lay words, what examination feels like, how to behave. The voice agent reads this; `check` rejects diagnosis and procedure words.

### 7. `content/interview.json`

Fixed rounds in stage order (history, exam, tests, diagnosis, plan). Each round: a situational prompt ending "What do you do next?", four choices A to D, exactly one correct, at most one partial, weights summing to 100, diagnosis and plan weighted heaviest, correct letters varied. Every choice has `feedback` and a `patientCue`; correct exam and test picks carry a `finding`; `covers` tags drive the Time-Out chips, and correct picks must cover every chart risk. Item-writing rules (NBME) and the full schema: [references/office.md](references/office.md).

### 8. `content/patient_status.md`

Third-person clinical summary the coach reads in the OR: identity, presentation, key findings, working diagnosis, procedure id and urgency, each chart risk and the OR step it changes, data gaps, what the learner should have elicited. Copy the structure from the example.

### 9. Check

Run `check` and fix every error. Treat each warning as a decision: fix it or state why it is acceptable. Typical warnings: tier 2 procedure, chart without a name (hidden on the board), choices too similar for the word-match fallback, correct option longest, a re-plan of an existing subject. `check` also prints the chart risk to OR step pins you need for `patient_status.md`.

Then run `trial`. It installs the package into a throwaway git worktree and runs the whole preop test suite. A re-planned subject often breaks tests written for its old case (for example an urgency assertion). Those tests are part of your change: update them to the new case in the same commit, or fix the package. `check` separately lists Unity code that names the old case id, which no test runs.

### 10 and 11. Install and gates

`install` (dry run) then `install --write`. Re-authoring a subject must also replace or delete `docs/patients/<subject>.md` (ship a `dossier.md`). Then update any tests `trial` flagged, then `gates`. All must pass except steps you cannot run (the C# playthrough needs the .NET SDK; Unity editor checks and a headset run need a person). For a brand-new subject, also expect the Unity Shell gates that count patients and the voice-group test; [references/integration.md](references/integration.md) lists every hard-coded list.

### 12. Report

State what changed (files), which checks ran and passed, which did not run, and anything left for Unity or a clinician reviewer. Do not claim a headset playthrough you did not see.

## Reference files

| File | Read when |
| --- | --- |
| [references/architecture.md](references/architecture.md) | First time: how a chart becomes the whole run |
| [references/finchnode.md](references/finchnode.md) | Choosing a subject, reading a chart, flags and gaps, new subjects |
| [references/package-format.md](references/package-format.md) | Every field of case.json and the content files |
| [references/office.md](references/office.md) | Writing the encounter, patient.md and interview rounds |
| [references/procedures.md](references/procedures.md) | Procedure schema, open-body model, what a new procedure costs |
| [references/clinical.md](references/clinical.md) | Sim design standards, history frameworks, MCQ rules, Time-Out, ATLS, vitals by age |
| [references/integration.md](references/integration.md) | Ids that must line up, hard-coded lists, gates, what needs an APK |
