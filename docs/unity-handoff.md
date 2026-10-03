# Unity and Blender Handoff

Updated October 3, 2026. Living checklist for Stephen (Unity project, scene, registration, settings) and whoever builds the 3D assets in Blender. The exercise code, generated ids, and offline case bundle already exist in `apps/quest/Assets/Scalpal/Exercises/` on branch `matthew/preop-finchnode`. The exact list of required objects is generated in [unity-asset-manifest.md](unity-asset-manifest.md). Treat that file as authoritative over any list here.

Repo rule from matt: **everything must render and run in Unity on the Quest, and no button or route may be a dead end.**

## Stephen: Unity project

1. **Create the project** at `apps/quest/` with Unity `6000.0.66f2`, the version from the [hardware baseline](hardware-baseline.md). Use URP and the Meta XR packages that worked in the camera sample. Target Android, IL2CPP, ARM64. Commit `.meta` files; the existing `Assets/Scalpal/Exercises/` scripts will get theirs on first import. Commit those too and keep their GUIDs stable.
2. **Check the scripts compile.** They only use UnityEngine, UnityEngine.Networking, UnityEditor (in `Editor/`), LINQ, and Regex. There are no assembly definitions. If you add asmdefs, the Exercises assembly needs no references beyond Unity's defaults. Outside Unity, `npm run test:unity` in `services/preop` compiles the same files under .NET with warnings as errors.
3. **Player Settings** (global, so yours):
   - Internet Access: **Require**, so the headset can reach the case service.
   - Allow downloads over HTTP: **Allowed in development builds**, unless we deploy the service over HTTPS. Without one of the two, every request fails on the headset and the app silently falls back to the offline bundle.
4. **Bootstrap scene:** add one GameObject with `ScalpalPreopService` and set `baseUrl` to the Mac's LAN IP, e.g. `http://192.168.x.x:8787`. `localhost` on the headset is the headset itself. The Mac runs `npm run dev` in `services/preop`, and both devices must be on the same network.
5. **Torso root** (registration output): expose one Transform whose local axes are **+Z toward the participant's head, +Y out of the abdomen**. +X then lands on the participant's left with no mirroring. Origin is the umbilicus on the skin, units are meters. The anatomy rig and the procedure ports are parented under it; ports use `TorsoFrame.PortLocalPosition(port, case.bodyScale)`.
6. **Registration state:** expose current validity (`Unaligned` / `Valid` / `Uncertain`) as a property plus a change event. While it is not `Valid`, the experience hides anatomy and stops feeding events to `CaseRunner`, per the architecture doc. Matthew's side consumes the signal; nothing writes back into registration.
7. **Physics layers:** create `Anatomy` and `Instrument` layers and let them collide with each other. The scene layer turns instrument tip triggers entering anatomy colliders into `CaseRunner.Handle(CaseEvent.Touch(structureId, instrumentId))`.
8. **Validate the imported rig:** select the anatomy root and run **Scalpal > Validate Selected Anatomy Rig**. It reports missing or misnamed meshes, missing colliders, flipped axes, and an over-budget triangle count. Fix everything it lists before building scenes on top.

## Blender: anatomy

Source models from [Z-Anatomy](https://github.com/Z-Anatomy/Models-of-human-anatomy). Check each component's license and keep the attribution in `assets/anatomy/`. Some components carry noncommercial terms.

- **The Codex anatomy atlas is the source of truth** (`Assets/Scalpal/Anatomy/`, built by `AnatomyAtlasBuilder` from Z-Anatomy). Each structure is a GameObject with an `AnatomyPart` whose **`stableId` equals the catalog id** (`liver`, `cystic_duct`, `common_bile_duct`, ...); the builder enforces that. Code addresses anatomy through `AnatomyController.TryGetPart(id)` / `Highlight(id)`, not by GameObject name. The manifest's `anat_<id>` names are only the fallback for hand-built placeholder rigs.
- **Coverage gap (Oct 3):** the atlas maps 18 of the 33 catalog structures. Still missing: abdominal_wall, umbilicus, cystic_duct, cystic_artery, common_hepatic_duct, common_bile_duct, right_hepatic_artery, small_bowel, terminal_ileum, cecum, sigmoid_mesocolon, left_gonadal_vessels, rectum, heart, lungs. The cholecystectomy's critical-view and clipping steps need the duct and artery ids before they can be touched.
- Ducts and arteries must be separate parts, not merged into the liver mesh, because steps highlight, touch, and quiz on them individually.
- **Scale:** real size, in meters (Unit System: Metric, Unit Scale 1.0). Apply all transforms (Ctrl+A, All Transforms) before export.
- **Placement:** parent everything under one empty named `anatomy_root` at the umbilicus skin point. The model is a supine adult. The liver and gallbladder sit on the participant's right and toward the head; the appendix and cecum on the right, toward the feet; the sigmoid colon on the left, toward the feet. The validator checks these.
- **Object origins** go at each object's own center (Set Origin > Origin to Geometry), so highlights and labels anchor sensibly.
- **Quest budget:** the validator fails at 150k triangles for the whole rig.
  - Large organs (liver, bowel): about 3k to 8k triangles each
  - Ducts and vessels: about 0.5k to 2k each
  - Background structures (abdominal wall, omentum, lungs): as low as they still read
  - Decimate with the Decimate modifier and check the silhouette at arm's length.
- **Materials:** one shared URP Lit material plus per-organ color via vertex color or a small atlas. No transparency on large organs: transparent overdraw is expensive on Quest. The experience highlights with emission instead.
- **Export:** FBX, Selected Objects, Apply Scalings "FBX All", Forward **-Z Forward**, Up **Y Up**, Apply Transform checked, no animation. In Unity's model importer, check **Bake Axis Conversion**. Then drop the model under the torso root with an identity local transform and run the validator. If it says the liver is on the wrong side, the axes are mirrored, not the model.
- **Colliders:** add them in Unity, not Blender. Use a convex MeshCollider or primitive colliders under each `AnatomyPart` on the `Anatomy` layer. `InstrumentTip` resolves a touch with `GetComponentInParent<AnatomyPart>()`, so colliders can sit on child objects. Thin ducts and arteries need slightly fattened capsule colliders or they are nearly impossible to touch.

## Blender: instruments

14 prefabs, named in the manifest (`inst_hook_cautery`, `inst_clip_applier`, and so on). Simple shapes are fine. They need to be recognizable and to scale: laparoscopic instruments are about 0.33 to 0.45 m long with a 5 or 10 mm shaft.

- Each prefab has a child named **`Tip`** at the working end with a small trigger collider on the `Instrument` layer. That trigger is the only thing that registers touches.
- Budget: under about 2k triangles per instrument. At most two are visible at once.

## What already works without any of this

The service, risk rules, eight patient cases, step engine, pre-op scoring, and offline bundle are built and tested. That includes 105 TypeScript tests, a live run against the FinchNode API, and a .NET compile of Exercises, the anatomy runtime, the Jarvis relay, and the Experience layer that plays every case through the C# engine. Until the atlas covers a structure, a debug sphere with an `AnatomyPart` (`stableId` = catalog id) and a collider stands in for it.

## Scene wiring (Matthew's Experience layer)

Code is in `Assets/Scalpal/Experience/`. One practice scene needs:

| Object | Components | Set |
| --- | --- | --- |
| Services | `ScalpalPreopService`, `CoachRelay` | Both `baseUrl` to the Mac's LAN IP |
| Practice anatomy (child of the torso root) | `AnatomyController` (preview mode off), atlas parts | Registration calls `SetRegistrationValid(bool)` |
| Case | `CaseDirector`, `PortLayout` | director: service, anatomy, relay, ports. ports: `torsoRoot` |
| Each instrument prefab's `Tip` child | trigger collider, `InstrumentTip` | `instrumentId` (catalog id), `director` |
| Coach binding | `AnatomyCoachBinding` | anatomy, relay |

Flow: `OpenCase(patientId)` → `CaseReady` (render brief and chart, bind buttons to the payload's actions) → `SubmitPreopCheck(selected)` → `PreopChecked` → `BeginProcedure()` → `StepStarted(step, patientNotes)` per step → `CaseCompleted`. Gaze or voice identification calls `Identify(id)`, confirm buttons call `Confirm()`, and `AutoplayStep()` performs the current step for demos. While registration is invalid, input is refused and `InputBlockedByTracking` fires.

## Open items

- Who models the anatomy. Per the [team plan](team-plan.md), anatomy assets are Matthew's lane; the Unity scene and settings are Stephen's. Agree before both start.
- Whether the service runs on the Mac over LAN or is deployed over HTTPS for the demo.
- The Quest display frame time with the rig loaded has not been measured. Profile once the first scene is up.
