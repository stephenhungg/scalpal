# patient-demo-rate-limited: no dossier (explore-page note)

Synthetic patient. Illustrative teaching content, not clinical guidance.

## What exists

- **FinchNode scenario:** `rate-limited` (`kind: behavior`, `behavior: rate_limited`, `slotSeconds: 2`, `retryAfterMax: 2`). Reads fail with 429 plus `Retry-After` during even 2-second slots and succeed during odd slots. The subject is not in `GET /patients` (that list only returns `kind: record` scenarios).
- **There is a real chart.** Verified 2026-10-03: `GET /patients/patient-demo-rate-limited/records` returned 200 on about half of 10 rapid calls and 429 on the rest. Contents:
  - Samir Haddad (synthetic), male, DOB 1979-09-14 (age 47)
  - Problems: essential hypertension, hyperlipidemia
  - Medications: lisinopril 10 mg, atorvastatin 20 mg
  - Allergy: penicillin (rash)
  - This is the same template chart as Ingrid Solano and the consent-partial subject.
- **Scalpal:** no authored `CASE_PLANS` entry, so `fallbackPlan(47)` produces a generated elective lap cholecystectomy ("Recurrent biliary colic ... (Generated scenario.)"). No encounter: it's listed in `ENCOUNTER_EXCLUSIONS`.

## What the explore page should show

1. **A patient card that tells the truth about the behavior:** "Samir Haddad (synthetic), 47 M," with a badge "Rate-limited source: demonstrates Retry-After handling."
2. **Chart preview available.** `finchnode.ts` already retries once after `Retry-After` (≤5 s) for GETs, so the chart normally loads after at most a 1 to 2 second pause. Show a brief "Waiting for the record source (retrying in N s)..." state, not an error. If the retry also fails, show the `case-builder.ts` retry state ("The health record is temporarily unavailable. Try again in N seconds.") with a Try again button. Never show it as "no data."
3. **No diagnosis office.** Disable "Start interview" with the explanation "No authored interview for this patient. This scenario exists to exercise rate limiting." Do not invent a persona, symptoms or a voice agent from the generated fallback plan, and do not present the generated chole case as authored teaching content. If the team wants the OR reachable, label it "Generated scenario: surgery practice only, no diagnosis scoring."

## Consistency issue

`ENCOUNTER_EXCLUSIONS["patient-demo-rate-limited"]` says "FinchNode always rate-limits this record (429), so no chart or case can be built." That's inaccurate. The 429 alternates by 2-second slot, the client's single retry normally succeeds, and the record is a full chart. The exclusion itself is reasonable (there's no authored persona), but the reason should read something like "Behavior scenario for rate limiting; no authored encounter. The chart loads after Retry-After." Also, `docs/diagnosis-office.md` still says "The service has three authored encounters." `encounters.ts` now has seven.
