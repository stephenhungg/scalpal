# Anatomy, cases, and Jarvis integration

Updated October 3, 2026. The first demo is **lap_appendectomy**, using the adult `patient-demo-multi-source` fixture. Gallbladder and sigmoid colectomy follow. This describes implemented code boundaries and explicitly separates them from unbuilt headset/robotics integration.

## Repository map

| Location | Implemented role |
| --- | --- |
| `services/preop/src/server.ts`, `app.ts` | Hono service entry, patient/case/catalog routes and offline bundle endpoint |
| `services/preop/src/catalog/` | Canonical anatomy IDs, tools, procedures, authored checks and mistakes |
| `services/preop/src/coach*.ts` | Jarvis tools, in-memory coach sessions, commands, events and SSE |
| `services/preop/scripts/` | Generate C# IDs, bundle, asset manifest and Jarvis setup |
| `apps/quest/Assets/Scalpal/Exercises/` | DTOs, offline-capable service client, pure case engine, coach transport |
| `apps/quest/Assets/Scalpal/Anatomy/Runtime/` | Anatomy identity, visibility, colliders-to-case input, service case selection and coach highlighting |
| `apps/quest/Assets/Scalpal/Anatomy/Editor/` | Atlas prefab and desktop preview/demo scene generators |
| `assets/anatomy/` | LFS originals and editable Blender workspace; source/teaching previews and provenance |
| `scripts/anatomy/` | Reproducible geometry pipeline and C# behavior/integration checks |

Main now contains the complete Unity project and native XR session, companion, API/realtime services, motion processor and robot assets. The [native session](native-session.md) is the current headset assembly and verification source. Torso registration and native video recording remain absent. The desktop anatomy components described below are alternate authoring/testing components, not additional active scorers in that scene.

The live catalog is TypeScript. C# DTOs and generated IDs consume its JSON; the packaged bundle provides offline cases. Tests now compare packaged procedure/anatomy/instrument catalogs with the live definitions and require collider-addressable atlas IDs for every interactive success and mistake target.

## Target coverage

The supplemental `exercise-targets.fbx` adds 15 separate objects, each named `anat_<stableId>`. The atlas builder creates an `AnatomyPart` with that exact ID and a non-trigger static `MeshCollider` referencing its mesh.

| Priority | Added stable IDs |
| --- | --- |
| Appendectomy | `cecum`, `terminal_ileum` |
| Gallbladder | `cystic_duct`, `cystic_artery`, `common_bile_duct`, `common_hepatic_duct`, `right_hepatic_artery` |
| Colectomy | `rectum`, `sigmoid_mesocolon`, `left_gonadal_vessels`, `small_bowel` |

The five gallbladder tubes are deliberately enlarged to 12 mm diameter. The additions are schematic teaching geometry anchored to source atlas landmarks, not newly segmented or anatomically validated originals. Gonadal vessels group the male atlas's left testicular artery and vein. `small_bowel` groups the source duodenum/jejunum with the authored terminal ileum as a representative target. Per-part provenance is recorded in the manifest and Blender object properties.

No interaction target is missing for any of the three procedures. All 33 canonical catalog IDs now have mappings, including the batch 4 abdominal wall/umbilicus proxies and source-derived heart/lungs aggregates. Noninteractive port/confirmation context does not require placeholder colliders. Source heart/lung substructures still exist. Port placement uses a distinct authored port ID, not an anatomy ID.

Case selection displays only required targets and available procedure context. Their mesh totals are 93,399 triangles for appendectomy, 120,474 for gallbladder, and 119,209 for colectomy. These are geometry counts, not measured Quest frame time. A full atlas remains substantially larger.

## Try the connected desktop slice

1. Follow the [asset setup](../assets/anatomy/README.md), including `git lfs pull`.
2. In `apps/quest`, which uses built-in rendering, run **Scalpal > Anatomy > Build Appendectomy Demo Scene**. Open generated `Assets/Scalpal/Anatomy/Scenes/AppendectomyDemo.unity`, then Play.
3. The scene generator assigns the atlas, `AnatomyExerciseBinding`, `AnatomyCaseSource`, `ScalpalPreopService`, `CoachRelay`, `AnatomyCoachBinding`, and the desktop panel. The panel offers appendectomy first and explicit review acknowledgement for review-required synthetic fixtures.
4. Local buttons use the packaged case bundle. Enable **simulated** registration, choose tools, identify structures, simulate collider contacts, place ports, and confirm steps. These inputs all pass through the same guarded binding. Tracking loss stops scoring. This desktop panel is excluded from Android players.
5. To exercise the service path, run `npm run dev` in `services/preop` and choose the service-load button. `AnatomyCaseSource` consumes `CaseLoaded`, stops the old attempt during loading/failure, and requires review acknowledgement where appropriate. The relay uses the same configured endpoint as the case client.
6. For live Jarvis, start a fresh untouched session for the same patient on the existing Jarvis page, then choose the live-session load button. Unity is the sole scoring writer for this attempt; do not simultaneously use laptop simulation controls.

Opening a generated desktop scene is not a Quest test. Scene generation, Unity physics, shaders and rendering still require a real Unity editor run; the local checks use doubles.

## Alternate Desktop Scene Boundaries

The native scene instead uses `NativeProcedureInput` and its single coordinator. Its relay includes event identities, rejection-preserving retries and immutable terminal command acknowledgements; the older desktop flow below is not the current native transport contract.

All tool contacts go through `AnatomyInstrumentTip` and `AnatomyExerciseBinding.TouchCollider`. A tip needs a trigger collider and an appropriate kinematic Rigidbody; configure Anatomy/Instrument physics layers. Set its exact instrument ID and assign the exercise binding. Call `SelectInstrument` from tool selection. Trigger entry handles one contact; for clip/stapler activation use `activateOnEntry = false` and call `ActivateContact` once per deliberate action. Do not also dispatch the same contact elsewhere.

Port placement, voice identification and UI confirmation call `Submit(CaseEvent)` on the binding. Do not create a second independent `CaseRunner` in scene code. `StepStarted`, `MistakeMade`, `CaseCompleted`, and `EventHandled` drive feedback. The binding forwards accepted events once in live mode, so do not also subscribe another `coach.Forward` handler.

Patient selection goes through `AnatomyCaseSource.LoadPatient`. Subscribe to its `StatusChanged`, `CasePending`, and `CaseStarted` for UI, and call `AcknowledgeReviewAndStart` after review. The service has no request correlation IDs, so failures on the assigned client conservatively stop the attempt; keep unrelated UI traffic on a separate client if needed.

The tracker must drive `AnatomyController.SetRegistrationValid`. Preview mode never scores. **The atlas uses its upright source origin; the participant torso uses umbilicus origin, +Z toward the head and +Y anterior.** Fit and validate a separate transform between these frames. Parenting the atlas at identity under `TorsoFrame` is incorrect. Do not use the old whole-catalog torso validator as proof the source atlas is registered.

Live adoption checks patient, procedure, initial step and untouched server state. Queued input/commands cannot cross session changes. Delivery failure explicitly pauses synchronization; counted actions are not blindly retried. Progress restoration, concurrent writers, and server-side session ownership/idempotency are not implemented. Start a fresh session and reselect after a synchronization failure.

## Verification

```sh
python3 scripts/anatomy/prepare.py --targets-only --blender /path/to/blender
dotnet run --project scripts/anatomy/runtime-check
dotnet run --project scripts/anatomy/integration-check
dotnet run --project scripts/anatomy/relay-check
cd services/preop
npm run typecheck
npm test
npm run test:unity
```

The geometry checks reimport all 12 FBXs and reopen the saved 4,035-part Blender workspace. The integration harness plays all three actual procedures through the binding using IDs from the real atlas manifest. Relay tests use controlled transport/coroutine doubles, not a live headset network. No cutting, deformation, bleeding, robot policy, clinical accuracy, or headset performance is established by these checks.
