# Integration: ids, hard-coded lists and gates

## Contents
- Ids that must line up
- Version strings on the case path
- What ships as data and what needs an APK
- Hard-coded lists a case can hit
- Gates, in order
- Compatibility checklist

## Ids that must line up

| Id | Form | Must match in |
| --- | --- | --- |
| subject (patientId) | `patient-demo-...` | `CASE_PLANS` key, encounter `planSubject`, `content/patients/<subject>/`, interview `patientId`, fixture file and its `id`, scenarios.json `subject`, handoff ticket, coach snapshot, RunResult |
| scenarioId | `multi-source-overlap` | scenarios.json `id`, `SurgicalCase.scenarioId` |
| procedureId (= exerciseId) | `open_appendectomy` | plan, interview, `PROCEDURES`, `STEP_ROLES`, `STEP_COACHING`, `HandoffRun.Supported`, realtime attempts, RunResult |
| caseId | `case_<subject>_<procedureId>` | unique in the bundle; coach snapshot and preop-check must echo it |
| encounterId | `int-...` (interview) | handoff ticket, coach session, RunResult |
| runId | 32 hex | handoff ticket, coach session, RunResult |
| attemptId | `<sessionId>-a<n>` | SpacetimeDB attempt, ticket, RunResult |

The Quest re-checks at each stage that the case, interview scorecard, coach snapshot and preop-check all carry the same subject, procedure and caseId.

## Version strings on the case path

`openBody.version` 1, `scalpal.handoff.v1` with `contentVersion` `0.1.0`, `exerciseVersion` `0.1.0`, `scalpal.run_result.v1`. The bundle and `SurgicalCase` have no version; their shape is enforced by `test/unity-contract.test.ts` (every JSON key has a C# field and every C# field is filled) and the .NET `unity-check`.

## What ships as data and what needs an APK

Data only (takes effect when the preop service restarts): a new or changed plan, encounter and interview for an existing subject on an existing procedure; text changes to coaching, considerations and feedback; step tweaks that stay inside existing anatomy and step ids. The OR fetches the case live.

Needs a re-exported bundle and an APK: correct offline explore preview, any new anatomy, instrument, procedure or step id, new routes or DTO fields, any procedure the OR should run beyond the two appendectomies, any new open-body organ, briefing and baked speech.

Needs service work: a patient FinchNode does not list (live), voice handing of non-open-case instruments, live RunResult delivery.

## Hard-coded lists a case can hit

| Where | What | Effect on a new case |
| --- | --- | --- |
| `apps/quest/.../Handoff/Runtime/HandoffRun.cs` `Supported` | two appendectomies | other procedures stop at "Surgery coming soon" |
| `apps/quest/.../Shell/Runtime/ExplorePatientModel.cs` `ProcedureShort` | Appendix, Gallbladder, Colon labels | new procedure falls back to its title |
| `apps/quest/.../Shell/Editor/ShellValidation.cs` | exactly 11 cases, 10 patients, 7 visible | a new subject or extra case fails the editor gate and blocks `ShellBuild.Build` |
| `apps/quest/.../EncounterOffice/Runtime/EncounterData.cs` | `Site` per procedure; History, Exams, Tests lists | new procedure: "Site unavailable"; new vocabulary ids need both sides |
| `apps/quest/.../EncounterOffice/Resources/EncounterSpeech` | offline speech for Priya and Jonah only | others use live voice only; editing their opener or history breaks their clips |
| `apps/quest/.../EncounterOffice/Runtime/EncounterPatientPresentation.cs` | two generic avatars by sex, scaled by age; guarding pose on the right lower abdomen | no child model; a right upper quadrant patient still guards the right lower quadrant |
| `services/preop/test/qa-interview.test.ts` `PATIENTS` | QA patient list | install adds the subject |
| `services/preop/test/encounter.test.ts` | shared-voice groups | a new voice collision fails |
| `services/preop/test/unity-contract.test.ts` | endpoint subject list, bundle caseIds | consider adding a new subject |
| `services/preop/scripts/offline-advanced-case.ts` | needs `patient-demo-multi-source` on `open_appendectomy` | re-planning Priya breaks the export |
| `apps/quest/.../Anatomy/Runtime/AnatomyDemoPanel.cs` | three case ids for the desktop anatomy demo (incl. `case_patient-demo-001_lap_cholecystectomy`) | re-planning one of those subjects leaves a dead demo button |
| `services/preop/test/encounter.test.ts` "elective credit for colic" | expects `patient-demo-001` to be elective | re-planning 001 to urgent fails it (found by `trial`) |
| `services/preop/test/qa-interview-live.test.ts` | per-subject round ids (live QA, `QA_LIVE=1`) | stale after a re-authored interview |
| `services/preop/test/brief.test.ts`, `routes.test.ts`, `qa-or.test.ts`, `coach.test.ts` | subject ids and their flags | chart-derived, so usually unaffected; `trial` confirms |
| `services/realtime/src/index.ts` `OPEN_CASE_INSTRUMENTS` | scrub-nurse instrument allowlist | other instruments cannot be handed by voice |

## Gates, in order

Before installing, `trial <dir>` runs the whole preop suite with the package installed in a throwaway git worktree and lists failures. After installing, from `services/preop` (the `gates` command runs these):

1. `npm run validate`: the catalog validator (53 rules).
2. `npm run typecheck`.
3. `npx vitest run`: catalog playthroughs, interview QA per patient, encounter rules, Unity DTO contract, routes, coach, open body, physiology parity.
4. `npm run gen:unity`: regenerates `ScalpalIds.cs` and `docs/unity-asset-manifest.md` (commit both).
5. `npm run export:unity -- --offline`: regenerates `scalpal_bundle.json` from fixtures (never without `--offline` unless you mean to hit FinchNode). Needs at least 6 playable cases.
6. `npm run test:unity`: the .NET 10 playthrough of every bundled case (skip and say so if dotnet is missing).
7. Not scriptable: Unity editor validations (`python3 scripts/quest/verify_session.py --suite unity`, needs Unity 6000.0.66f2), and a person playing it on a headset.

## Compatibility checklist

- [ ] Subject is a FinchNode demo subject with a repo fixture, or the package carries `fixture/` and you accept offline-only.
- [ ] `check` reports 0 errors; every warning is fixed or explained.
- [ ] Plan procedure is headset-playable, or the requester knows it is interview-only.
- [ ] Persona matches the chart; voice does not collide (or the voice change is made).
- [ ] Every chart risk is covered by a correct pick.
- [ ] `install --write` done; `gates` green except steps that cannot run here.
- [ ] Generated files (`ScalpalIds.cs`, manifest, bundle) committed with the change.
- [ ] Report lists what was verified and what still needs Unity, a headset or a clinician.
