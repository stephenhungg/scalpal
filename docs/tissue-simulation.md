# Abdominal Tissue Milestone

The user selected appendectomy/abdomen as the first region for more anatomical layers, tissue motion and surface appearance. This milestone adds bounded reference geometry and local grasp deformation to the shared MR/full-VR experience. It does not establish realistic surgical forces or tissue viscosity.

## What Is Implemented

The selection and automatic-body-fit overview contains **81 structures / 120,125 triangles**, up from 42 / 110,511. The 39 added structures share the existing Z-Anatomy meter coordinate frame: 12 abdominal exterior regions, 12 abdominal wall muscle/fascia structures, pelvis/sacrum/five lumbar vertebrae, and seven named arterial/venous references. These remain render-only references. X during selection cycles organs, exterior surface, abdominal wall, skeleton and vessels; X during practice still identifies a target. Surface/wall/bones start hidden to preserve organ visibility. The automatic fit overview stays at the default organ view.

This is abdominal coverage, not a complete body-wide layered model. Exterior region meshes have no measured skin thickness. Fat, layered dermis and parietal peritoneum are missing. The ileocolic vein in the independent HRA detail asset is not silently placed into the shared body frame. Major vessel reference exports are deliberately simplified. Cecum and terminal ileum remain explicitly schematic teaching meshes.

Three existing scored anatomy parts receive separate mechanics presets:

| Part | Display triangles | Preset | Authored displacement limit |
| --- | ---: | --- | ---: |
| `appendix` | 442 | Bowel | 18 mm |
| `mesoappendix` | 468 | Mesentery | 22 mm |
| `appendicular_artery` | 2,006 | Artery | 8 mm |

Limits are in source/local meters; world displacement follows the body-fit scale. They are demo bounds, not measured strain limits or anatomical motion ranges. No physics is added to the other organs, surface, skeleton or vascular references in this milestone.

Each target uses an original eight-node cage with 28 XPBD distance constraints and five zero-compliance tetrahedral volume constraints, trilinearly embedded into the original display mesh. Four posterior corners are fixed authored attachments. These are generic cage pins, not reconstructed biological attachments. Volume preservation is approximate under finite iterations and a driven handle. Inverted/nonfinite solutions reset rather than render misleading geometry. Constraints solve at fixed 90 Hz with eight iterations and a four-substep catch-up cap; there is no gravity, respiration, circulation or inter-organ contact.

A tracked, held, activated grasp/retrieval tool must penetrate the actual enabled tip/target colliders to acquire a handle. Moving it deforms the mesh locally instead of parenting the whole organ onto the tool. Trigger release, tool loss or hiding releases the grasp; elastic return and exponential velocity damping settle the tissue. The tissue does not detach or disappear into a retrieval bag. This is local mechanical feedback, not a new authored procedure outcome.

Runtime meshes are private copies. Render and existing contact MeshCollider update together at up to 30 Hz on these 2,916 total triangles; the imported asset is untouched. MeshCollider cooking is an explicit Quest performance risk. `SCALPAL_NATIVE_TISSUE_TIMING` reports local peak surface-commit CPU time during valid practice without body coordinates or media. It does not measure total CPU/GPU frame time, dropped frames or compositor performance.

An object-space procedural Standard surface shader adds mild mottling/fiber appearance and distinct wetness/roughness presets without requiring source UVs or participant photography. These are teaching appearance presets. There are no scanned PBR maps, actual microstructure, calibrated hydration or anatomical texture validation. Existing ghost/highlight paths retain their materials/property blocks.

## Shared Routing and Reset

`NativeCaseSession.Start` creates `NativeTissueSimulation`, binds only the three stable IDs from its practice controller, and supplies `NativeProcedureInput.InteractionReady` as its gate. That gate already requires the current authored attempt, coach synchronization, native readiness, valid registration, the practicing phase and an unfinished exercise. `NativeProcedureInput` remains the only scored event producer. Mechanics neither emit case events nor change the rubric, backend payloads, attempt version or coach authority.

Anatomy visibility/registration is checked as well. Loss of the shared gate resets deformation; retry resets synchronously before changing attempts. Mode changes invoke the same retry and restore authored transforms. No participant geometry or frame is persisted. Preview layers never supply scoring colliders. The full-VR and MR paths share this exact simulation component.

`AnatomyAtlasBuilder.BuildOrganOverview` owns the bounded reference subset. `NativeTissueBuild.Prepare` refreshes overview and practice prefabs/materials without regenerating native scene identities. `NativeSessionBuild.Prepare` includes the same builders; `Build` verifies the prepared scene before packaging; the repeatable verification gate includes mechanics/contact regressions.

## Research and Fidelity Roadmap

The [original XPBD paper](https://matthias-research.github.io/pages/publications/XPBD.pdf) provides compliant positional constraints. This implementation uses its distance-constraint formulation plus hard signed-volume projection; it does not copy a third-party solver or fit a tissue constitutive model. Mesh topology, a small cage and damping alone cannot establish surgical realism.

[Human abdominal-wall tensile experiments](https://pmc.ncbi.nlm.nih.gov/articles/PMC10604332/) characterize different layers and document variability within and between tissue samples. [Human linea-alba experiments](https://pubmed.ncbi.nlm.nih.gov/21130459/) report nonlinear anisotropic response. Those findings motivate distinct layers and material calibration; they do not justify assigning one universal viscosity or transferring a tensile modulus directly to this cage's normalized compliance. The current preset damping is exponential velocity decay, not a measured viscoelastic stress-relaxation law.

[SOFA](https://www.sofa-framework.org/about-sofa/) supports more elaborate deformable/rigid/fluid interactions and surgical operations. Its existence does not prove a Quest/Android integration or frame budget. Keep it as a separate mechanics prototype candidate rather than replacing the working native app without a measured deployment path.

The next coherent additions are:

1. Profile this grasp/mesh-cooking path during actual Quest practice and check contact alignment, settling and frame time; reduce/update a low-resolution collision proxy only with a tested visual/contact error bound.
2. Author a reviewed local abdominal-wall volume with skin, subcutaneous fat, fascia/muscle and peritoneum; distinguish educational cutaway geometry from source-derived segmentation. Keep explicit thickness/attachment provenance.
3. Add tissue-specific force-displacement/relaxation calibration, nonlinear and anisotropic material response, sliding/self-contact and organ attachments. Quest controllers do not provide surgical resistance simply because a visual solver estimates forces.
4. Add localized cutting with real topology changes, an interior surface, new collision geometry and reset-safe fragments. Existing seam-split patches and authored touch/count checks remain symbolic; this milestone does not cut organs.
5. Add a vascular graph, wall/lumen mechanics, flow/bleeding, compression/occlusion and validated energy/clip injury rules. A red or blue vessel shader does not simulate blood.

Pin measurement sources, units, tissue/sample conditions and fit residuals before describing a preset as material realism. Acceptance remains engineering behavior and reviewed educational content, not clinical competence.

## Verification

The repeatable `python3 scripts/quest/verify_session.py --suite unity` gate passed, including 5,739 tissue assertions (many per-vertex preservation checks), imported geometry and all five preview layers. Existing input/case/attempt/coach tests also passed. Editor teardown uses explicit idempotent source restoration; it does not prove native lifecycle callbacks. Android ARM64 IL2CPP build `0.4.0-tissue` / code8 succeeded; the 63.47 MiB APK installed over USB. `native_smoke.py --seconds 10` was blocked by Quest's controllers-required prompt (exit2), so startup XR, new visuals and mechanics on the headset are not verified. Local coach/gateway/pose returned200; USB routes/private configuration were restored. No participant capture was started. Synthetic mechanics fixtures and actual imported-asset inspection are distinct from physical headset testing. Automatic reclining-person body fit, live voice, clinically accurate materials and the complete session remain unverified.
