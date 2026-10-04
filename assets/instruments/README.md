# Scalpal Instrument Kit

Fifteen generic simulator tools matching Matthew's current instrument catalog, including the scalpel used in the cutting sandbox. This is the simulator's bounded kit, not an exhaustive inventory for every surgical specialty.

- [Importable Unity package](scalpal-instruments.unitypackage): use **Assets → Import Package → Custom Package** in Unity, then open `InstrumentSandbox`.
- [Blender library](source/scalpal-instrument-library.blend): all fifteen editable tools in separate collections.
- [Individual sources](source/): full-detail `.blend` files with named moving parts and studio cameras.
- [Individual renders](previews/): 1200 × 1200 PNG previews; [overview](previews/instrument-kit-overview.jpg).
- [High-detail GLB exports](exports/): PBR geometry for review; native Unity uses the FBX assets instead.
- [Unity runtime and optimized models](../../apps/quest/Assets/Scalpal/Instruments/): metric FBX meshes, generated pickup prefabs, controller input, authored training patch and sandbox scene.
- [Specifications](tool-specs.json), [generated inventory](generated-inventory.json), [research](../../docs/surgical-tools-research.md), [runtime contract](../../docs/instrument-runtime.md).

## Open Geometry Reused

We adapted MIT-licensed LapGym `sofa_env` jaw geometry for the grasper, Maryland dissector and scissors; the hook electrode; the 30-degree scope optics; and contoured handle/lever parts for several instruments. [Attribution](ATTRIBUTION.md) and [unmodified sources, hashes and upstream references](third-party/sofa_env/README.md) are retained. Other geometry and material recipes are original generic simulator artwork. Dimensions, assembly and material choices are approximate; no manufacturer CAD, trademarks or reference photographs are distributed.

## Detail and Runtime Budget

Full-detail Blender sources and GLBs preserve mechanical details for editing and previews. The FBX export separately decimates fixed meshes while preserving named articulation roots; each instrument's total runtime mesh budget is below 2,000 triangles, following Matthew's current handoff. Materials are reconstructed in Unity from the imported color and named metal/polymer recipes. Profile rendering and collision behavior on Quest; this budget is not a measured headset frame-rate result.

Models include grip, distal tip/action and moving-part transforms. Unity normalizes import axes using named anchors; the resulting tool points along prefab local +Z with its grip at the origin. Collider geometry is deliberately simpler than the visual geometry. Generated prefab customization should use separate variants because the builder regenerates its outputs.

## Rebuild

From the repository root, with Blender 4.5 LTS available:

```bash
blender --background --python scripts/instruments/build_instruments.py -- --root .
blender --background --python scripts/instruments/assemble_library.py -- --root .
```

Open the committed `apps/quest` project in Unity 6000.0.66f2, or import the complete Instruments folder into another configured project preserving `.meta` files, then use **Scalpal → Instruments → Build Prefabs and Sandbox**. The Quest application still needs its configured native XR rig, build settings and registration owner; this package does not initialize the whole project or connect Jarvis automatically.

Large source/render binaries use Git LFS. Run `git lfs install` before adding or checking out them on another machine. Generated Blender backups are ignored.

## Cutting and CV Scope

The scalpel/scissors runtime traces an authored virtual seam and separates its existing pieces. This is a real visible state change with an action event, but it is not arbitrary deformable-organ mesh slicing. Other tool effects and unimplemented physical mechanics are listed in the runtime document.

Virtual tools are known Unity objects; the projection helper produces rendered-view geometry bounds. Raw passthrough cannot see virtual geometry. A reproducible synthetic dataset renderer produces masks and COCO visible-pixel boxes:

```bash
blender --background --python scripts/instruments/render_cv_dataset.py -- --root . --out /absolute/path/to/dataset --views 2
```

This renders controlled synthetic images with camera metadata. It does not train a model, validate physical-tool recognition or establish camera registration. Real props require labeled real images and held-out evaluation. Participant footage is not included.
