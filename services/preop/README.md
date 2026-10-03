# Pre-op Service (FinchNode)

Owner: Matthew.

Turns [FinchNode](https://finchnode.com/) synthetic health records into pre-op briefs and Unity-ready surgical cases. Every synthetic patient gets a realistic, authored surgical scenario that fits their chart: a 78-year-old on apixaban with CKD gets urgent cholecystectomy for cholecystitis, a 9-year-old with asthma gets an appendectomy, a chart with uncoded meds gets an elective sigmoid colectomy that requires reconciliation first. The chart is real API data; the acute presentation is authored fiction and labeled as such. Illustrative teaching content, not clinical guidance.

This is Scalpal's FinchNode sponsor integration: a live API read that changes what the learner sees, hears, and is scored on.

## Rules for this component

- **Everything renders in Unity.** Every payload is JsonUtility-safe (no nulls, no dictionaries, no nested arrays, `{x,y,z}` vectors) and maps 1:1 onto the C# DTOs in `apps/quest/Assets/Scalpal/Exercises/Data/`. Tests enforce both.
- **No dead routes or buttons.** Every response carries `actions` (`{id, label, method, route}`). Every route resolves, failures included: revoked consent, rate limits, unknown patients, and failed connections all render a state with a way onward. A crawler test walks every action from `/`.
- **Risks are deterministic.** Flags come from rules over coded data (RxNorm, LOINC, SNOMED), each with the record entries behind it. No model decides what counts as a risk. Missing data is reported as unknown, never as none.
- **Synthetic only.** The demo API is public and keyless. An optional sandbox key (`ck_test_`) unlocks FinchNode's real Connect flow, but sandbox keys can only ever see synthetic patients. Nothing here touches real health data.

## Two FinchNode paths

| Path | Needs | What happens |
| --- | --- | --- |
| Demo records | nothing | `GET /patients/:id/case` reads `api.finchnode.com/demo/v1` directly |
| Connect admission | `FINCHNODE_API_KEY=ck_test_...` in `.env` | `POST /admit/:scenarioId` creates a real Connect session on `api/v1` and returns its hosted `connectUrl`. Open it (laptop or QR), choose the scenario's synthetic health system under "FinchNode Scenario Sandbox", and approve sharing. `GET /admissions/:sessionId` polls until FinchNode exposes the app-scoped subject (`u_...`); that subject's case reads from `api/v1` and shows its consent receipt |

Patients who consent on the hosted page appear in `GET /patients` automatically (from FinchNode's `GET /users`) with `kind: sandbox`. Their surgical case plan comes from the scenario title in their source label ("Northstar Health System (Synthetic) · Baseline adult, age 38"). Every admission response also offers "Use the demo record instead", so a slow sandbox never blocks the learner. Record reads retry once after `Retry-After` on a 429, per FinchNode's contract. Keys stay in `.env` on the server, never in Unity builds.

Verified Oct 3: a patient consented on the hosted page (`u_115958ef4e58c641`, receipt `rcpt_A6505A1C2C57`) reads end to end through `/patients/:id/case` with `dataSource: sandbox`. FinchNode's `simulate` shortcut does not complete: the simulated source withholds `documents` and `claims`, sync stays `partial`, and `simulation.state` stays `syncing`. `createApp({ simulateAdmissions: true })` re-enables it once FinchNode fixes that.

## API

| Method | Route | Returns (C# DTO) |
| --- | --- | --- |
| GET | `/` | `ServiceIndex`: entry actions |
| GET | `/patients` | `PatientList`: every FinchNode scenario, with procedure, urgency, status |
| GET | `/patients/:id/case` | `SurgicalCase`: everything to render one case (see below) |
| GET | `/patients/:id/brief` | `PreopBrief`: flags, chart panel lines, gaps, Jarvis line |
| POST | `/patients/:id/preop-check` | `PreopCheckResult`: body `{"selected": ["bleeding", ...]}` |
| GET | `/procedures`, `/procedures/:id` | `ProcedureList`, `Procedure` |
| GET | `/anatomy`, `/instruments` | `AnatomyList`, `InstrumentList` |
| POST | `/connect/:scenarioId` | `ConnectResult`: runs a demo Connect session scenario (cancelled/failed cases) |
| POST | `/admit/:scenarioId` | `AdmissionStatus`: sandbox Connect admission (needs the key; otherwise `unavailable` plus demo fallback) |
| GET | `/admissions/:sessionId` | `AdmissionStatus`: poll until `state` is `completed`, then follow its open-case action |
| GET | `/unity/bundle` | `ScalpalBundle`: catalogs plus every case, for offline use |
| GET | `/health` | `HealthStatus` |

`:id` accepts a FinchNode subject (`patient-demo-polypharmacy`) or scenario id (`polypharmacy-senior`). Errors return `ErrorResponse` with `actions`.

### Case statuses

| Status | Meaning | What Unity shows |
| --- | --- | --- |
| `ready` | Complete chart | Brief, then the pre-op check, then the procedure |
| `needs_review` | Chart has gaps (missing categories, uncoded meds, empty labs, a down source) | Same flow; the gaps are an `incomplete_chart` flag the learner must catch |
| `blocked` | Patient revoked consent (FinchNode 410) | Reason plus "Choose another patient" |
| `retry` | Rate limited or upstream down | Reason, `retryAfterSeconds`, plus "Try again" |

### What a `SurgicalCase` contains

- `patient`, `bodyScale` (children scale port positions down), `urgency`, `indication`, `presentation`
- `brief`: risk `flags` (each with `severity`, `spoken`, `detail`, `structures` to highlight, `evidence`), `chart` lines for the floating chart panel, `dataGaps`, and `say` for Jarvis
- `procedure`: `ports` with torso-frame positions, ordered `steps` (action, instrument, target structures, ports, success check, mistakes with feedback, hints, `next`)
- `considerations`: chart risks pinned to specific steps (anticoagulation on the liver-bed dissection, contrast allergy at the critical view)
- `checklistOptions`: the pre-op safety check, real risks mixed with at least two distractors
- `anatomy` and `instruments`: exactly the meshes and prefabs this case needs

## Unity integration

Setup steps for the Unity project and Blender assets are in [docs/unity-handoff.md](../../docs/unity-handoff.md); every required mesh, prefab, and port is in the generated [docs/unity-asset-manifest.md](../../docs/unity-asset-manifest.md).

Code lives in `apps/quest/Assets/Scalpal/Exercises/`:

| Path | What |
| --- | --- |
| `Data/ScalpalCaseData.cs` | `[Serializable]` DTOs for every payload |
| `Engine/CaseRunner.cs` | Step engine: feed it `PlacePort`, `Touch(structure, instrument)`, `Identify`, `Confirm` events; it raises `StepStarted`, `StepCompleted`, `MistakeMade`, `CaseCompleted`. `PerfectEvents(step)` drives a demo autoplay |
| `Engine/PreopScorer.cs` | Offline pre-op check scoring (same rules as the service) |
| `Preop/ScalpalPreopService.cs` | MonoBehaviour client. Bind UI buttons to returned `ScalpalAction`s and call `Dispatch(action)`. Falls back to the offline bundle when the network drops |
| `Preop/TorsoFrame.cs` | `Vec3` to `Vector3`, port placement under the torso root |
| `Editor/ScalpalAssetValidator.cs` | Menu **Scalpal > Validate Selected Anatomy Rig**: names, colliders, axes, triangle budget |
| `Generated/ScalpalIds.cs` | Generated constants for anatomy, instruments, procedures, steps, flag types, and the route resolver |
| `Resources/scalpal_bundle.json` | Offline bundle: every case, loads with `Resources.Load` |

Conventions the scene must follow:

- **Anatomy meshes** are named `anat_<id>` (`AnatomyUnityNames`), one separately addressable object per structure. Highlight `brief.highlightStructures` and the current step's `targets`.
- **Instrument prefabs** are named `inst_<id>` (`InstrumentPrefabs`).
- **Torso frame:** meters, origin at the umbilicus on the skin. Local `+Z` toward the head, `+Y` out of the abdomen, so `+X` is the participant's left. Registration owns the torso root's transform; ports are its children at `TorsoFrame.PortLocalPosition(port, case.bodyScale)`.
- **Mistakes:** when an instrument collider touches a structure, call `runner.Handle(CaseEvent.Touch(structureId, instrumentId))`. Wrong-structure touches come back as mistakes with feedback text for Jarvis to say.

### Quest networking

- `localhost` on the headset is the headset. Set `baseUrl` to the Mac's LAN IP (`http://192.168.x.x:8787`) with both on the same network, or deploy the service over HTTPS.
- Android blocks cleartext HTTP unless Player Settings → Other Settings → **Allow downloads over HTTP** is enabled for development builds. That is a global Unity setting (Stephen owns ProjectSettings); otherwise use an HTTPS deployment.
- If the service is unreachable, the client answers from the bundled JSON, so the demo still runs end to end.

## ElevenLabs (Jarvis) server tools

Create each as a **webhook (server) tool**, method POST, JSON body, pointed at the deployed service URL. Every tool returns HTTP 200 with a `say` field, including failures, so Jarvis can always speak a result.

| Tool | URL | Body | Use |
| --- | --- | --- | --- |
| `list_patients` | `/tools/list_patients` | `{}` | "Who's on the board today?" |
| `get_case` | `/tools/get_case` | `{"patientId": string}` | Briefs a patient: procedure, indication, flags, gaps |
| `check_preop` | `/tools/check_preop` | `{"patientId": string, "selected": string[]}` | Scores the learner's spoken safety check |
| `get_step` | `/tools/get_step` | `{"patientId": string, "stepId"?: string}` | Explains a step with this patient's notes; omit `stepId` for the first step |

Suggested system prompt fragment: *"Only state patient facts returned by tools. Never invent medications, labs, or allergies. If a tool reports a gap, say it is unknown."*

## Jarvis live coach

Real-time voice coach on top of the same catalogs. See [docs/jarvis-handoff.md](../../docs/jarvis-handoff.md) for the Unity, Blender, and SpacetimeDB side.

- `src/catalog/coach-knowledge.ts`: anatomy facts for every structure and why/look-here coaching for every step (validated by `npm run validate`).
- `src/coach.ts`: `CoachSession` wraps the step engine and tracks time on step, off-target attempts, wrong instruments, gaze on danger structures, and tracking loss, escalating hints from why to where-to-look to the explicit move.
- `src/coach-prompt.ts`: per-case system prompt containing only that case's patient, steps, and anatomy.
- `src/coach-routes.ts`: `/coach/sessions` (create, events, hint, explain, commands/ack, simulate, SSE stream), `/coach/current` for the headset, `/jarvis` voice page, `/jarvis/connection` signed URL.
- `scripts/setup-jarvis-agent.ts` (`npm run jarvis:setup`): creates or updates the ElevenLabs agent and its client tools.

```bash
cp .env.example .env && npm run jarvis:setup && npm run dev   # then open http://localhost:8787/jarvis
```

## Data

`test/fixtures/` holds recorded responses from the public FinchNode demo API. They are fictional synthetic records, not participant or patient data.
