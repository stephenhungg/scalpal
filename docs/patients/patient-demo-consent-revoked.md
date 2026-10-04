# patient-demo-consent-revoked: no dossier (explore-page note)

Synthetic patient. Illustrative teaching content, not clinical guidance.

## What exists

- **FinchNode scenario:** `consent-revoked` (`behavior: consent_inactive`). The scenario metadata names "Lena Forsberg (synthetic), female, born 1991." Every record read returns **410 `consent_inactive`**: "The patient revoked sharing for this application. Stop reading this subject until a new consent is granted." Verified 2026-10-03.
- **No chart may be read**, so there is no patient record to build a dossier from. Scalpal lists it in `ENCOUNTER_EXCLUSIONS` (accurate reason). `case-builder.ts` maps 410 to `status: "blocked"`, with the message "This patient revoked consent, so I can't open their chart. Pick another patient."

## What the explore page should show

- **A locked card with no chart-derived data:** "Consent revoked. This patient stopped sharing their record with Scalpal." Don't show a name, age or sex pulled from FinchNode scenario metadata. Using a revoked patient's identity in the UI undermines the point of the scenario. A neutral label such as "Revoked patient" is enough.
- **Actions:** "Pick another patient" and "Check consent again" (the existing `case-builder.ts` actions). Don't auto-retry: a 410 is final until consent is re-granted.
- **No interview, no generated case, no OR.** Don't fall back to `fallbackPlan` for this subject in any learner-facing surface.
- **Optional teaching line:** "Failing closed on revoked consent is the correct clinical-data behavior."
