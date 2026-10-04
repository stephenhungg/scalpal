# Case package format (`scalpal.case-package.v1`)

## Contents
- Layout
- case.json
- plan (CasePlan)
- encounter (Encounter)
- content/interview.json (PatientInterview)
- content/patient.md
- content/patient_status.md
- fixture/ (new subjects only)
- Id rules

A package is a folder. `assets/example-priya/` is a complete, passing example; read it next to this file.

## Layout

```
<package>/
  case.json                 manifest: subject, plan, encounter
  content/
    patient.md              persona the patient voice plays
    interview.json          the office rounds
    patient_status.md       clinical summary the coach reads in the OR
  dossier.md                optional long-form notes (docs/patients/<subject>.md)
  fixture/                  only for a subject the repo has no fixture for
    record.json             FinchNode GET /users/<subject>/records snapshot
    scenario.json           its GET /scenarios row
```

## case.json

```json
{
  "schema": "scalpal.case-package.v1",
  "subject": "patient-demo-multi-source",
  "scenarioId": "multi-source-overlap",
  "title": "Priya Ramaswamy: Acute appendicitis",
  "notes": "free text: provenance, FinchNode version and fetch time",
  "objectives": ["Elicit and act on a penicillin allergy", "..."],
  "sources": ["WSES Jerusalem guidelines 2020, doi:10.1186/s13017-020-00306-3", "..."],
  "review": ["Authored stress glucose value needs clinician review", "..."],
  "plan": { },
  "encounter": { }
}
```

`objectives` (3 or 4 measurable learning objectives), `sources` (one citation per management claim) and `review` (claims a clinician must confirm) stay in the package as provenance; `install` does not write them anywhere. `check` warns when `objectives` or `sources` is missing.

## plan (CasePlan)

Installed into `CASE_PLANS[subject]` in `services/preop/src/catalog/cases.ts`.

| Field | Type | Rule |
| --- | --- | --- |
| `procedureId` | string | a catalog procedure. Headset-playable: `open_appendectomy`, `lap_appendectomy`. Catalog only: `lap_cholecystectomy`, `lap_sigmoid_colectomy` |
| `urgency` | `elective` \| `urgent` \| `emergency` | must equal `encounter.urgency` |
| `indication` | string | one line, e.g. "Acute appendicitis" |
| `presentation` | string | the authored acute story in clinical prose. No claims about chart contents |
| `chartNotes` | `{text, whenFlags?, whenGaps?, minSources?}[]` | optional sentences appended only when all conditions hold. `whenFlags` are flag types, `whenGaps` are gap codes ([finchnode.md](finchnode.md)) |

## encounter (Encounter)

Installed into `ENCOUNTERS` in `services/preop/src/catalog/encounters.ts`. It still drives the explore board, the persona, the voice, demographics and the legacy scorer, so it is required even though the Quest plays `interview.json`. Authoring rules: [office.md](office.md).

```ts
{
  planSubject: string;                 // = subject
  persona: {
    speaker: "patient" | "parent";
    name: string;                      // who talks
    patientName: string;               // who is sick; = name for a patient speaker
    age: number;                       // the sick patient's chart age (integer 0..120)
    sex: "female" | "male";            // the sick patient's chart sex
    speakerAge?: number;               // parent only, 18..120
    speakerSex?: "female" | "male";    // parent only
    chartDemographics?: "not_shared";  // only when the chart truly has no demographics
    voiceKey: "adult_female" | "parent_female" | "adult_male" | "mature_female" | "middle_female" | "older_female" | "senior_female" | "middle_male";
    demeanor: string;                  // how they talk
    character?: string;                // personality and life only; no symptoms, meds, allergies, history or results
    opener: string;                    // first line; must not give away the diagnosis
  };
  history: { [topic]: string };        // first person, plain words; HISTORY_TOPICS keys
  exam: { [maneuver]: { reaction: string; finding: string } };   // reaction is spoken ("" for none); finding is screen only
  tests: { [test]: { result: string; abnormal: boolean } };       // screen only; the patient never states results
  diagnosis: { label: string; keywords: string[][]; partial?: { label: string; keywords: string[][] } };
  procedureKeywords: string[][];       // AND of ORs, lowercase
  urgency: "elective" | "urgent" | "emergency";
  differential: { id: string; label: string; keywords: string[] }[];  // at least 5, unique ids
  critical: RubricItem[];              // at least 1; missed first in feedback; weight 2
  expected: RubricItem[];              // weight 1
  testNotes?: { [test]: string };      // teaching note when that test is ordered
}
RubricItem = { kind: "history" | "exam" | "test"; id: string; why: string }
```

Keyword groups are AND of ORs: every inner list must match at least once (lowercase substring, negation aware). `[["appendicitis","appendix"],["perforat","ruptur"]]` means perforated appendicitis.

## content/interview.json (PatientInterview)

```ts
{
  version: 1;
  patientId: string;        // = subject
  procedureId: string;      // = plan.procedureId
  openingLine: string;      // what the patient says first (usually = persona.opener)
  rounds: {
    id: string;             // short snake_case, unique
    stage: "history" | "exam" | "tests" | "diagnosis" | "plan";   // non-decreasing in this order
    prompt: string;         // situation, then "What do you do next?"
    weight: number;         // > 0; all rounds sum to exactly 100
    choices: {              // exactly 4, keys A, B, C, D in order
      key: "A" | "B" | "C" | "D";
      text: string;         // what the clinician says or does
      grade: "correct" | "partial" | "wrong";   // exactly 1 correct, at most 1 partial
      feedback: string;     // one sentence for the scorecard
      patientCue: string;   // what the patient conveys back, from patient.md facts only
      covers?: string[];    // "history:<topic>", "exam:<maneuver>", "test:<id>"
      finding?: { label: string; text: string; abnormal?: boolean };   // exam/test result on screen; required on correct exam and tests picks
    }[];
  }[];
  closingLine?: string;     // the patient's reaction to the plan
}
```

Validator rules (`validateInterview` plus the build and QA rules): 6 to 10 rounds (aim for 7 to 9), a diagnosis round and a plan round, at least 3 distinct correct letters when there are 4 or more rounds, every chart risk covered by the `covers` of correct picks, `covers` ids from the real vocabularies.

## content/patient.md

Second person, addressed to the speaker. Sections used by every existing case:

```
# <Name> (or "# <Child> (spoken for by <parent relation>, <Parent name>)")
## Who you are
## Personality and how you talk
## How you feel right now
## What you think is going on        (lay worries; never today's diagnosis)
## Your story                        (onset, timeline, symptoms in lay words)
## Your health background            (chart facts in lay words: conditions, meds, allergies, surgeries, last meal)
## What you feel when examined       (sensations only, never findings)
## How to behave                     (never name a diagnosis, never give results or numbers, 1 to 3 sentences)
```

Bracketed delivery tags like `[wince]` are allowed; the Quest strips them from captions. Must be non-empty, or the office is skipped.

## content/patient_status.md

Third person, for Scalpal in the OR. Include: identity line, presentation, key findings (exam and tests), working diagnosis, procedure id and urgency, each chart risk with the OR step id it changes (from the case considerations), data gaps, the critical items the learner should have elicited, and any source conflicts. Keep it factual; it is injected verbatim into the coach prompt.

## fixture/ (new subjects only)

`record.json` is the raw FinchNode record with `id` equal to the subject. `scenario.json` is `{id, kind: "record", title, summary, subject}`. Installing them adds the patient to the offline bundle and tests, not to the live service. See [finchnode.md](finchnode.md).

## Id rules

| Id | Pattern |
| --- | --- |
| subject | `^[a-z0-9][a-z0-9_-]{0,79}$` and, for the content folder, `^[a-z0-9-]{3,80}$` (no underscores). Use the `patient-demo-` prefix for demo data |
| procedureId | `^[a-z0-9_]+$` |
| round ids, differential ids | short snake_case |
| caseId (derived) | `case_<subject>_<procedureId>` |
