# Abdominal Tissue Milestone

The next connected physics revision is tracked in [the full physics plan](physics-implementation-plan.md). It adds a generic three-layer tetrahedral wall with finite blade fracture, sampled organ contact and conserved vessel discharge. This page records the earlier 0.4.0 cage checkpoint; the newer components are not clinically calibrated or physically verified yet. See [material evidence](research/tissue-material-data.md) and [bleeding assumptions](research/bleeding-model.md).

The imported FBX parts use mesh-local units and a separate import transform. The new deformation/contact adapter explicitly converts that scale into source-atlas meters; treating raw vertex coordinates as meters produces incorrect motion limits and mesh-validity thresholds. Body-fit scale is a separate presentation transform. Numerical damping remains a stability parameter, not viscosity.

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


## Connected Volume, Contact and Vessel Revision

`NativeCaseSession` now owns a `NativeVolumeSimulation` and `NativeVesselSimulation` alongside the existing cage coordinator. All three use the existing practice/session/attempt/registration readiness signal. The generated wall is a sibling of practice anatomy in `AuthoredAnatomyToPatient`, not in the differently oriented `patientFrame`. Source coordinates are meters, +X anatomical left, +Y cranial, +Z inward from the anterior surface. The wall starts at source umbilicus/front `(0,1.017768,-0.115287)`; generic body-fit scale is a separate display transform.

The wall is a 16×10 cm educational region with authored skin2 mm/fat12 mm/peritoneum1 mm layers, 324 initial nodes and 1,200 conforming tetrahedra. These dimensions are not measured skin segmentation. Peritoneum's tensile modulus seed includes fascia in the source experiment; it is not a complete material calibration. Skin/fat values remain synthetic. Boundaries are authored perimeter attachments. Six positional energy iterations run at fixed90 Hz with a four-substep cap. Surface/collider commit carries the frame remainder at up to30 Hz.

Actual `CutStart`/`CutEnd` anchors produce two finite swept triangles between accepted tool samples. Degenerate, untracked, unheld, unactivated, non-cut, ambiguous, removed, remote and discontinuous/origin-changing paths are rejected. Fracture disconnects incident material fans, adds both interior faces and changes active node bindings. Earlier before/after checks of cached `TotalMass` and `ReferenceVolume` did not independently establish conservation; the audit identified that gap. Replacement checks reconstruct retained-cell geometry and nodal mass incidence (details below). It follows20 mm XY grid faces rather than arbitrary fine incision geometry. Budgets are512 cut faces/4096 nodes with atomic rejection. No `TrainingTarget`, procedure event or new incision scorer is added.

Gate loss hides generated wall render/collider, discards blade history, releases the handle and freezes velocity while preserving incisions. Retry restores intact topology. Maxwell histories are per material cell, so cutting duplicated nodes does not erase material memory. Optional passive spectra relax stress under held strain; numerical node damping remains separate. Default wall profiles are still uncalibrated elastic models. The generalized Green-strain bulk/shear relaxation is an approximation, not a measured multiplicative tissue law; published coefficients are not assigned to the wrong anatomical site.

The contact solver uses each body's current deformed surface, up to128 actual vertex/triangle-centroid probes and bounded two-way cage corrections. Source FBX factor100 is explicitly converted before mechanics and geometry thresholds. The artery's10 open12-edge terminal loops get a disclosed closed contact proxy with120 additional triangles; source audit computed maximum cap plane deviation0.70 micrometers from imported geometry. Render, scoring and imported source remain unchanged. Closed proxy volume is0.250293 mL, not a measured physiological volume. Sampled contacts and conservative sampling bounds do not guarantee continuous separation or unsampled intersections. Fixed per-probe allowances preserve authored rest attachments: the actual three-body audit reports3.544 mm original sampled overlap, zero rest corrections and zero excess residual. This is an authored attachment policy, not measured anatomical contact clearance. Later additional penetration still produces two-way correction. The conservative sampling bound is55.328 mm, so small missed contacts remain possible.

Finite blade triangles also intersect the currently rendered artery mesh to open a local effective orifice. `VesselBleeding` conserves source/lost/pool/suction volumes. Seal/clip requires a tracked activated owned tool, action-point proximity and actual owned tip penetration; suction requires pool-ellipsoid contact. The wound follows its material point when the artery deforms. Registration/session loss hides the pool and freezes the ledger; tool put-down alone does not stop flow. Retry restores fluid state. The ellipsoid is a volume indicator, not a free-surface fluid solver. Area/pressure/reservoir/suction parameters are disclosed teaching assumptions in [the model](research/bleeding-model.md). Vessel wall topology, pulsatility, clotting and viscosity-dependent lumen flow are still absent.

The earlier checkpoint reported volume18,172, volume runtime2,220, material/history866 and vessel arithmetic221/runtime261 assertions, plus the existing body/input/case/attempt/coach checks. Contact2,102 assertions cover source inspection, artificial caps, rest attachments, fit transforms and additional penetration. The reported counts include inadequate cached-total conservation assertions and must not be cited as proof of mass/volume conservation across cuts. New Android deployment, physical cutting/contact/bleeding/frame time and measured coupon fits remain unverified. See the [full goal checklist](physics-implementation-plan.md); none of these component checks establish complete surgical realism.

Reusable source-unit lesson: imported mesh-local coordinates are not automatically meters. The actual anatomy FBX uses a100× import transform; mechanics, topology tolerances and material-point coordinates now explicitly convert to meters. Area normals also use explicit finite magnitude normalization: Unity’s general `Vector3.normalized` cutoff incorrectly zeroed valid submillimeter vessel-cap normals. Both failures have geometry regressions.


## Material Force Calibration Interface

The connected volume now exposes finite prescribed boundary targets on constructor-pinned original nodes and read-only accepted material nodal forces in newtons. Cut fans retain grip identity, rejected steps preserve previous forces/history, and retry clears targets/cache. This adds laboratory coupon capability to the actual solver, without changing the runtime wall’s authored fixed attachments. Numerical velocity damping is excluded from the reported material force. See [the force/source-summary experiment](research/solver-calibration-experiment.md).

The Unity gate passed5,468 new force-coupon assertions and the actual skin-summary replay/refinement benchmark. Three distinct passive spectra satisfy the published sparse summaries, so the benchmark does not identify one measured viscosity or qualify a runtime skin material. Abdominal fat source coefficients are pinned separately; their printed bulk units and missing support data remain explicit. Physical interaction/performance and calibrated specimens remain unfinished.


## Native Packaging Checkpoint

Material-force runtime/tests/source-summary experiment commit `048ad57` is pushed. Android ARM64 IL2CPP `0.5.0-volume` / code9 built successfully after the full scene/mechanics/session gate. APK size is63.64 MiB; SHA256 `5e6e461b4217a28c5ecdd92372e24aab4fbb7739895c5e327df4c2736d8e4b25`. USB install succeeded and installed package reports the correct version/code and `arm64-v8a` ABI. Private configuration and8787/3000/8790 routes were restored without enabling capture. Launch smoke returned exit2: Quest intercepted startup with its controllers-required dialog. Wearer launch is requested; no new physical visual/cut/contact/bleeding/frame-time evidence is claimed. New packaging supersedes the earlier0.4.0 binary only; it does not supersede its missing physical acceptance.


The consolidation follow-up rebuilt the current main3203b29 source, including encounter-aware voice transport, as `0.5.1-volume` / code10. The full Unity gate passed inside the Android build; services and native voice-tool gates passed after syncing preop dependencies to the pulled lockfile. APK63.64 MiB, SHA256 `864e7d56a1e3f69483ae0068a2b08a0578064b64f5cdddde7518f60131011a90`; installed version/code/ARM64 ABI confirmed. Existing app was stopped before installation, private routes restored and no participant capture started. The new launch smoke again exited2 at Quest’s controllers-required prompt. This binary supersedes0.5.0; startup/mechanics/frame time still require wearer acceptance and testing.


## Cut-topology conservation audit correction

The former post-cut comparisons used constructor/reference totals that cannot detect incorrect corner binding or split-node mass. They have been replaced with live topology checks in `NativeVolumeValidation.TopologySnapshot`: every original cell retains its identity/material, each has four distinct bound corners with the correct reference positions, every active node has incident cells, each live rest tetrahedron reconstructs its independent baseline volume, and stored nodal mass (including pinned nodes) matches the sum of its actual bound-cell contributions. Pure cuts and atomic rejected cuts also compare each reconstructed deformed tetrahedron before/after; loaded elastic fixtures use a disclosed 10% total geometric-volume tolerance rather than claiming exact incompressibility.

Deliberate corruption tests alter the actual binding, stored nodal mass, rest geometry, retained cell identity and finite positive deformed geometry and require these checks to reject each fault. The viscoelastic cut/reset/rejection lifecycle uses the same invariants. These new assertions passed the Unity gate (volume111,494 and material/history867 assertions). Runtime cut checks use the same live invariants. The five real-array mutation cases are rejected; physical cut accuracy and clinical material conservation remain unverified.
