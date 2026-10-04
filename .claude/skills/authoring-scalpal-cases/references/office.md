# Writing the diagnosis office

## Contents
- How the office plays
- Closed vocabularies
- Encounter rules
- Writing patient.md
- Writing the interview rounds
- Chart-risk coverage (Time-Out chips)
- Scoring
- Voices
- Speech recognition of choices
- Common failures

## How the office plays

The learner faces one patient (or a parent speaking for a child). Each round the patient speaks, then the learner picks one of four clinician moves by tap or voice. The server tells the voice agent what to convey (`patientCue`) and what the clinician did (`text`); exam and test picks also show a `finding` card. The same rounds run in the same order every time so scores compare across runs. Scalpal is not present; it reads the result and `patient_status.md` later in the OR.

Two engines share the data: the current `/interviews` engine plays `interview.json`; the older `/encounters` engine (and the explore board, voice, demographics and build script) uses the encounter in `case.json`. Write both, consistent with each other.

## Closed vocabularies

Adding an id means code, Unity and ElevenLabs tool changes. Use these exactly.

- **History topics:** `chief_complaint, onset, location, migration, character, severity, aggravating_relieving, nausea_vomiting, appetite, fever, bowel, urinary, menstrual_pregnancy, last_meal, past_medical, past_surgical, medications, allergies, social, family, recent_illness`
- **Exam maneuvers:** `general_appearance, vitals, abdomen_inspection, abdomen_palpation, mcburney_point, rebound, guarding_rigidity, rovsing, psoas, obturator, murphy, cva_tenderness, chest_lungs, genitourinary, pelvic`
- **Tests:** `cbc, crp, bmp, lactate, lipase, urinalysis, pregnancy_test, lfts, ultrasound, ct_abdomen_pelvis, type_and_screen`
- **Voice keys:** `adult_female, parent_female, adult_male, mature_female, middle_female, older_female, senior_female, middle_male`

The vocabulary is abdominal. A non-abdominal case needs new ids everywhere (engineering work).

## Encounter rules

Persona
- Patient speaker: `name` equals `patientName`, no `speakerAge`/`speakerSex`. Parent speaker: a different name, `speakerAge` 18 to 120, `speakerSex`.
- `age` and `sex` are the **sick patient's** chart values, checked against the chart (`check` prints the chart). Name must match exactly.
- `character`: life, personality, mood arc, lay worries. Facts reach the learner only by being asked, so two mechanical rules apply. (1) No word longer than five letters from the chart's Problems, Medications or Allergies lines, including generic tokens like "tablet" or "mellitus". (2) No five-word phrase copied from any history answer, exam reaction or finding, or test result; write character and history in different words.
- `opener` and `history.chief_complaint`: describe the complaint in lay words. If saying them as a diagnosis would score, `check` rejects them.

History
- First person, plain language, one topic per key. Use SOCRATES for the pain story (site, onset, character, radiation or migration, associated symptoms, timing, exacerbating and relieving, severity) and SAMPLE for the pre-op essentials (allergies, medications, past history, **last oral intake**, events).
- `medications` must mention every charted active drug and `allergies` every charted allergy (lay or brand words are fine; `check` uses the test suite's alias table).
- `menstrual_pregnancy` for any female patient of reproductive age; it is a critical item for abdominal pain.

Exam and tests
- `reaction` is what the patient says or does when examined ("" if nothing). `finding` is the clinician's result on screen.
- Tests: `result` plus `abnormal`. Results must be internally consistent with the story and the chart (INACSL conceptual fidelity: vitals match the diagnosis; fever tachycardia is not hemorrhage).
- Every rubric `exam` or `test` item needs an authored finding or result.

Diagnosis and rubric
- `diagnosis.keywords` and `procedureKeywords`: AND of OR groups, lowercase. Include synonyms and partial stems a learner would say.
- `partial`: an adjacent diagnosis that earns partial credit (for example perforated versus uncomplicated).
- `differential`: at least 5, each a real must-not-miss alternative with lowercase keywords. Short stems over-match ("stone" matches "gallstone").
- `critical`: the moves that change safety (allergies with a relevant allergy, pregnancy test, last meal, anticoagulant review). Each with a one-line `why`.

## Writing patient.md

The voice agent sees only this file plus per-round directions. Write what the person knows and how they talk, in second person. Include past diagnoses a doctor told them (in lay words), never today's. Include the exam sensations, not findings. End with behavior rules: one to three short sentences, answer only what was asked, never name a diagnosis for today's problem, never give test results or numbers, say "I'm not sure" for anything not covered. For a child, write to the parent and include what the child says. `check` rejects diagnosis and procedure keywords unless the patient's own authored history already uses them (a prior diagnosis).

## Writing the interview rounds

Shape (from every passing case): 7 to 9 rounds. 3 or 4 history, 1 or 2 exam, 1 tests, 1 diagnosis, 1 plan, in that order. Diagnosis and plan weigh most (existing: diagnosis 18 to 20, plan 14 to 15, tests 12 to 17, history and exam 7 to 10).

Prompts: one or two sentences of situation that follow from the previous round, ending "What do you do next?". The prompt must not give away the answer.

Choices, following the NBME item-writing rules:
- Exactly one best answer. Pass the **cover-the-options test**: a skilled learner reading only the situation should be able to name the right move.
- Homogeneous options: all history questions, or all exam moves, or all diagnoses, or all plans. Plausible distractors that represent real mistakes (anchoring, skipping a safety check, wrong imaging first, wrong timing).
- No "none of the above", no "except" or "not", no absolutes ("always", "never"), no grammatical cues, and the correct answer must not be the longest or most detailed.
- At most one partial: reasonable but not best (right idea, incomplete; or right operation, wrong timing).
- Vary the correct letter: at least three different letters across rounds, never the same letter more than three times (`check` warns).
- The correct choice must not be the longest option (`check` warns).
- `feedback`: one sentence. For the key, why it is right for this patient. For a distractor, why it is wrong for this patient and what mistake it represents.
- `patientCue`: what the patient conveys in reply, in their voice, from `patient.md` facts. Wrong picks still get a natural reply (confusion, a true but unhelpful answer, pushback).
- Exam and tests choices (right or wrong) carry `finding` with the encounter's text verbatim (`check` warns when a finding does not contain the encounter text for what it covers). Correct exam and tests picks must.
- `covers` on correct and useful partial picks: the topics, maneuvers and tests that move actually elicits.

Diagnosis round: correct is the encounter diagnosis, partial is `encounter.diagnosis.partial` if any, wrong are differential items. Exactly one defensible key given what has been revealed.

Plan round: correct is the case procedure with the right urgency and the safety specifics the chart demands (for example "latex-free room", "hold apixaban, check timing"). Partial: right operation with wrong timing or approach, or right operation missing or mishandling one safety specific (for example an unnecessarily broad antibiotic switch for a low-risk penicillin label). Wrong: plausible wrong management. `closingLine`: the patient's reaction to the plan.

## Chart-risk coverage (Time-Out chips)

Every chart risk (except `pediatric`, `elderly`, `incomplete_chart` in QA) must be found by the `covers` of correct picks alone. A risk only becomes a chip if the procedure pins it to a step.

| Flag | Covered by |
| --- | --- |
| bleeding, polypharmacy | `history:medications` |
| allergy, latex, contrast | `history:allergies` |
| renal | `history:past_medical` or `test:bmp` |
| metformin_renal | `history:medications` and (`history:past_medical` or `test:bmp`) |
| diabetes | `history:past_medical` or `history:medications` or `test:bmp` |
| anemia | `history:past_medical` or `test:cbc` |
| cardiac | `history:past_medical` |
| airway | `history:past_medical` or `history:recent_illness` |
| incomplete_chart | `history:past_medical` and `history:medications` and `history:allergies` |
| pediatric, elderly | chart only, never missed |

`check` names the missing cover when a risk is uncovered.

## Scoring

Interview: correct earns the round weight, partial half, wrong zero; total out of 100. Grade bands: 85 Excellent, 70 Solid, 50 Developing, else Needs work. Feedback lists misses first ("Missed: ... The best move was ..."), then partials, then a line naming the real surgery if the plan was wrong, then "Good:" lines. A perfect run must score 100.

Legacy encounter scorer (still tested): history 25, exam 15, workup 15 (critical items weigh double), diagnosis 20 (partial 12), plan 10 (procedure keywords 6, urgency words 4), differential 15 (three named for full marks).

## Voices

One patient agent, one voice per session from `voiceKey`. Re-authoring an existing subject keeps that subject's `voiceKey`; nothing changes. All premade voices are already in use, and `encounter.test.ts` pins the exact shared-voice groups. A new patient needs a new voice key (add it to `PATIENT_VOICE_KEYS` in `catalog/encounters.ts` and `DEFAULT_PATIENT_VOICES` in `encounter.ts` with an ElevenLabs voice id on the account) or an update to that test. `check` reports a new collision as an error.

## Speech recognition of choices

With `ANTHROPIC_API_KEY`, a Haiku classifier maps the spoken answer to a letter. Without it, a word-match fallback needs at least two shared content words and a clear winner. `check` warns when reading a card aloud would not land on that card under the fallback. Give each choice distinct content words.

## Common failures

- Persona age off by one (chart age is at `dataAsOf`).
- Character text mentions a chart drug.
- A correct exam pick without a `finding`.
- The allergy never asked in a correct pick, so the allergy chip is "missed".
- `patient.md` saying "appendix" when the patient has never had one diagnosed.
- Encounter urgency different from the plan's.
- Editing `encounters.ts` after generating content: content does not update itself; keep both consistent.
