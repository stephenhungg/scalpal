# Patient content for the pre-op office

One folder per FinchNode demo subject (`<subjectId>/`), each with three files. All patients are synthetic; this is illustrative teaching content, not clinical guidance.

| File | Read by | What it is |
|---|---|---|
| `patient.md` | the ElevenLabs patient voice agent | Second-person persona: who the patient is, how they talk and feel, and everything they know about their own story in plain language. Never today's diagnosis, exam findings, test results or the answer key. |
| `patient_status.md` | Scalpal, in the operating room | Third-person clinical summary: presentation, key findings, working diagnosis, procedure and urgency, chart risk flags and how they change the operation, data gaps, and what the learner should have elicited. |
| `interview.json` | the office runtime | The fixed choice-based interview (`PatientInterview` in `src/interview-types.ts`). Same rounds in the same order every run, so scores compare. `validateInterview` must return no problems. |

## Sources

- FinchNode demo record (`src/finchnode.ts`, falling back to `test/fixtures/`) and the case plan from `buildCase` (`src/catalog/cases.ts`): procedure, urgency, chart risk flags.
- Authored encounter (`src/catalog/encounters.ts`): the patient's story, exam reactions and findings, test results, diagnosis, critical items. The interview's findings use these verbatim.
- Stephen's dossiers (`docs/patients/<subjectId>.md`). Where a dossier names a different procedure, `cases.ts` wins.

## Regenerating

```sh
cd services/preop
npm run patients:build                      # every subject with an authored encounter
npm run patients:build -- patient-demo-001  # just these subjects
```

The script (`scripts/build-patient-files.ts`) drafts each file with Claude (`ANTHROPIC_API_KEY` from `services/preop/.env`), retries the interview until it validates and every chart risk is covered by a correct pick, and rejects a `patient.md` that names today's diagnosis or operation. Output is not deterministic.

**The committed files are the source of truth.** They were reviewed and hand-edited after generation. Regenerating overwrites those edits, so review the diff before committing a rerun.
