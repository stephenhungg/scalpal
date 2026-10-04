# Doctor Office Art

The botanical office has a rose/lilac flower arch, bouquets, sage upholstery, blush walls, warm oak and plants. It interprets the requested flowery MHacks theme without using an official event logo. Original office geometry includes a clinician desk/chair, patient/companion chairs, examination couch, privacy rail, sink/cabinet, medical trolley, window shades and practical lamp. The desk leaves a clear conversational sightline.

The patient prototypes were replaced with continuous, clothed MakeHuman humans after visual review. Both characters have natural skin/hair/eye/clothing textures and actual weighted rigs. See [asset provenance](ATTRIBUTION.md) for the CC0 data licenses, exact donor files, hashes and source pin.

## Deliverables

- `doctor-office.blend`: editable floral office, two seated patients, actual rigs, packed textures, lighting and render camera. The male source is hidden by default; toggle his `ScalpalCharacter` objects to inspect him.
- `patients.blend`: standalone editable character library used by the office rebuild.
- `previews/office-conversation.png`, `previews/PatientFemale.png`, `previews/PatientMale.png`: actual Blender Cycles component renders, not headset screenshots.
- `inventory.json`: measured triangles/renderer counts, export hashes, material/texture mapping and verification evidence.
- Unity `Art/Models/{DoctorOffice,PatientFemale,PatientMale}.fbx` and `Art/Textures/`: optimized exports and 1024px diffuse maps.
- `third-party/MakeHuman/`: actual CC0 donor subset, source notices and hashes.

## Rebuild

Use Blender 4.5.7 LTS. The accepted office rebuild appends the CC0 character library; it never generates the retired primitive people:

```sh
blender --background --disable-autoexec --python scripts/environments/doctor_office.py -- --root .
```

To rebuild the humans, obtain MPFB v2.0.17 source at commit `afb9f530a7c2741dedb8df0ebae2e0b183caec21` and pass its directory. The committed donor subset supplies the actual clothing, hair, eyes, skin and shoe files. MPFB is an authoring-time dependency; no global addon installation is required by this script.

```sh
blender --background --disable-autoexec --python scripts/environments/doctor_office_humans.py -- \
  --root . --mpfb /absolute/path/to/mpfb2
```

If the human library is missing, the office script fails with a clear prerequisite message. For a deliberate fresh source build, `--office-only` creates the floral room first, then the human command above supplies the patients. It does not silently fall back to prototype people.

## Native Lighting

The Unity office is fully baked for Quest: no realtime lights, shadow maps or post-processing. `EncounterOfficeBuild.Prepare` calls `EncounterOfficeLighting` to add a closed ceiling, a warm window "sun" spot with a procedural blinds cookie, a cool window-sky area light, two ceiling-diffuser area lights and a warm floor-lamp point light, then bakes with the Progressive GPU lightmapper (40 texels/m, AO, 4 bounces, non-directional, one 1024 atlas). Patients are dynamic and lit by a baked light-probe group; one box-projected baked reflection probe gives the floor and metal their highlights. Prepare needs a graphics device (run it without `-nographics`); the bake output under `Scenes/DiagnosisOffice/` is committed. `EncounterOfficeLightingValidation` (part of `Verify`) fails if the bake, probes or lightmap UVs are missing or collapsed.

The office FBX carries Blender-authored lightmap UVs in its second UV set (`Lightmap`, Smart UV Project). Unity's import-time unwrapper is left off because it collapsed the bevelled window frame into a black sliver. `previews/office-lighting-before.png`, `office-lighting-after.png` and `office-lighting-after-room.png` (UI hidden) are mono Editor renders from the seated learner eye, made with `Scalpal/Encounter Office/Capture Lighting Preview`.

## Native Contract

`PatientFemale` presents the fictional Priya Ramaswamy, age 40, `patient-demo-multi-source`; `PatientMale` presents fictional Jonah Okoye, age 30, `patient-demo-sparse`. Their artist-selected visual appearances do not establish additional case demographics or match real people.

Each patient FBX has a `PatientRoot` object, a seated MakeHuman armature, genuinely weighted `HeadPivot` and `JawPivot` bones, and `NoseTip` attached to the head bone. There are six skinned render meshes per patient. Native code may animate those real bones within bounded angles. No calibrated viseme lip sync or realistic facial performance is claimed.

The authoring scene is metric Blender +Z up, with patients facing -Y. The export matches the stable office export's 180-degree Blender Z turn. Actual Unity import measured the patient's nose facing -Z; the native builder applies a measured 180-degree yaw to the whole office and both patients, giving the clinician-facing +Z arrangement. Native validation checks both imported nose markers before saving. Clinician eye suggestion after correction is Unity `(0, 1.2, 1.75)`, facing -Z.

`unityMaterials` entries provide exact names, sRGB tint, roughness, metallic, optional `diffuseTexture` Unity asset path and `alphaClip`. Hair/brows need alpha testing; patient diffuse maps need their authored texture and tint. The room's original palette remains unchanged. Texture maps are packed in both Blender files and shipped separately in Unity.

## Actual Component Checks

The office is 42,600 triangles / 17 render meshes. Priya is 27,180 triangles / six render meshes; Jonah is 36,906 triangles / six render meshes. Render only the active patient. Those are geometry counts, not measured Quest frame times.

```sh
blender --background --disable-autoexec --python scripts/environments/doctor_office.py -- --root . --verify-only
```

The independent FBX check passed exact triangle/mesh counts, complete material assignments, genuine rig modifiers, canonical node names, both nose markers, and actual weighted head/jaw deformation. At five degrees, head rotation moved skin vertices about 12.5/13.6mm and jaw rotation about 7.7/9.1mm for female/male respectively. This verifies the exported rigs deform the mesh; it does not establish realistic facial motion or provider-driven audio.

All medical fixtures remain scenery. Findings, examination results, tests and assessment must come from the encounter engine. Physical Quest stereo appearance, frame time, headset audio and live provider interaction remain separate checks.
