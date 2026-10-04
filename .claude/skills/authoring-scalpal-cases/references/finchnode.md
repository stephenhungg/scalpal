# FinchNode: choosing and reading a patient

## Contents
- The two APIs
- The demo subjects
- What Scalpal reads from a record
- Risk flags (every rule)
- Data gaps
- New subjects and sandbox patients
- Gotchas

## The two APIs

| | Demo API | Authenticated API |
| --- | --- | --- |
| Base | `https://api.finchnode.com/demo/v1` | `https://api.finchnode.com/api/v1` |
| Auth | none | `Bearer ck_test_...` (sandbox) or `ck_live_...` (real PHI; never use) |
| Data | 12 fixed synthetic scenarios | sandbox: synthetic patients from simulated Connect sessions (max 25, removable after 7 days) |
| Rate limit | 120 req/min per address | 300 req/min per key |

Errors are `{error: {type, code, message, requestId}}`; branch on `code`. 429 carries `Retry-After` in seconds. Every demo response is marked synthetic (`synthetic: true`, `x-finchnode-data: synthetic`, a `meta.disclaimer`, "(Synthetic)" in org names). Keep those markers.

Scalpal's service only uses the demo API unless `FINCHNODE_API_KEY` is set. Source: <https://finchnode.com/docs>, <https://finchnode.com/docs/get-started/demo-api>, <https://finchnode.com/docs/resources/rate-limits>.

## The demo subjects

Run `npx tsx $T list` for the live state of each in the repo, and `chart <subject>` for the exact brief.

| Subject | Scenario | Chart | Good for |
| --- | --- | --- | --- |
| `patient-demo-001` | `baseline-adult` | Morgan Rivera, 38 F. T2 diabetes, hypertension; metformin, lisinopril; penicillin allergy (mild) | adult with an antibiotic-choice hook |
| `patient-demo-polypharmacy` | `polypharmacy-senior` | Harriet Lindqvist, 78 F. AF on apixaban plus aspirin, CKD 3, heart failure, 14 meds; sulfonamide (high) and contrast (low) allergies | anticoagulation, renal and imaging decisions |
| `patient-demo-pediatric-asthma` | `pediatric-asthma` | Theo Abernathy, 9 M, 26.8 kg. Asthma, rhinitis, eczema; inhalers; peanut allergy (high) | the only child; a parent speaks |
| `patient-demo-multi-source` | `multi-source-overlap` | Priya Ramaswamy, 40 F. Hypothyroid, migraine, iron-deficiency anemia; latex allergy; two sources disagree | latex OR setup, reconciling sources |
| `patient-demo-sparse` | `sparse-record` | Jonah Okoye, 30 M. Demographics and one visit, every category empty | previously healthy adult; missing-data gaps |
| `patient-demo-messy-coding` | `messy-coding` | Dolores Marchetti, 63 F. Free-text meds without codes, weight in pounds, glucose in mmol/L; amoxicillin allergy | reconciliation and unit hygiene |
| `patient-demo-consent-partial` | `consent-partial` | only meds and allergies shared, no demographics | hidden on the Quest board (no name) |
| `patient-demo-source-unavailable` | `source-unavailable` | partial sync with a `source_unavailable` warning | gap handling |
| `patient-demo-rate-limited` | `rate-limited` | 429 on alternating 2 s slots | error path only; excluded |
| `patient-demo-consent-revoked` | `consent-revoked` | 410 `consent_inactive` | error path only; excluded |

None of the charts holds an acute surgical problem. Every case is chart plus an authored acute presentation.

Names, ages and sexes above are what the repo fixtures yield at their `dataAsOf`; always confirm with `chart`.

## What Scalpal reads from a record

| Category | Read | Notes |
| --- | --- | --- |
| demographics | name, birthDate, gender | age at `meta.dataAsOf`; missing name gives "Unnamed patient (limited chart)" |
| medications | name, status, codes | live statuses: null, active, unknown, on-hold, intended; deduped by RxNorm code or name |
| conditions | name, status | matched by name; SNOMED codes used only for dedupe |
| labs | name, LOINC code, value, unit, date, referenceRange, interpretation | latest per kind: eGFR, creatinine, hemoglobin, A1c, platelets |
| allergies | substance or name, status, severity | "No known ..." dropped; severity words mix FHIR severity and criticality, so "high" is not automatically anaphylaxis |
| vitals | latest HR, RR, BP, SpO2, weight | only for the coach's VR baseline vitals |
| consent | status, receiptIds | shown as the Consent chart line |
| ignored | documents, claims, immunizations, encounters, notes | |

Only labs match on codes. Medications, conditions and allergies match on names, so a brand name like "Eliquis" will not raise the bleeding flag.

## Risk flags (every rule)

One flag per type (`flag_<type>`), sorted high then moderate. These become the pre-op checklist, the Time-Out chips and the per-step considerations.

| Type | Trigger | Severity |
| --- | --- | --- |
| `bleeding` | live anticoagulant (apixaban, rivaroxaban, edoxaban, dabigatran, warfarin, enoxaparin, heparin, fondaparinux) or antiplatelet (aspirin, clopidogrel, prasugrel, ticagrelor, dipyridamole, cilostazol) | high with an anticoagulant, else moderate |
| `latex` | allergy matching latex | high |
| `contrast` | allergy matching contrast or iodine | moderate |
| `allergy` | any other live allergy | high if severity says high or severe |
| `renal` | eGFR < 60, high creatinine, or CKD | high if eGFR < 30 |
| `metformin_renal` | metformin and eGFR < 45 | high |
| `diabetes` | diabetes condition or A1c >= 6.5 | high if A1c >= 8.5 |
| `anemia` | low hemoglobin or anemia condition | high if Hb < 8 |
| `cardiac` | AF, heart failure, coronary disease or MI | high for heart failure |
| `airway` | asthma, COPD or sleep apnea | moderate |
| `polypharmacy` | 10 or more live meds | moderate |
| `pediatric` | age under 18 | moderate (chart only, never "missed") |
| `elderly` | age 75 or over | moderate (chart only) |
| `incomplete_chart` | any data gap | high if meds, allergies or demographics are missing |

## Data gaps

`not_shared_<category>`, `missing_<category>` (medications, allergies, conditions, labs), `no_demographics`, `source_unavailable`, `uncoded_medication`, `medication_status_unknown`, `lab_no_value`, `no_kidney_function`, `identity_mismatch`. Any gap makes the case `needs_review` and raises `incomplete_chart`. Use these exact codes in `chartNotes.whenGaps`; a typo silently never fires (`check` warns).

## New subjects and sandbox patients

- **New demo subject FinchNode added:** fetch and snapshot it into the package (`fixture/record.json` from `GET /demo/v1/users/<subject>/records`, `fixture/scenario.json` from its `GET /demo/v1/scenarios` row). Record the `finchnode-version` header and fetch time in `case.json` `notes`.
- **Invented subject:** only works offline (the exported bundle and the tests). Live `/patients` never lists it. `check` warns. Unity's Shell gates count patients exactly and will need updating.
- **Sandbox `u_...` subjects** map back to a demo scenario by the scenario title in their source label and reuse that demo subject's plan and encounter. Do not author against a `u_` id.

## Gotchas

- Units: messy-coding stores weight in `[lb_av]` and glucose in mmol/L. Convert before any calculation.
- The fixture clock: tests freeze time at 2026-10-03, and ages come from `dataAsOf`. A persona age that is off by one fails the demographics check.
- `patient-demo-multi-source` must keep an `open_appendectomy` plan: the offline export derives the bundled advanced laparoscopic variant from it.
