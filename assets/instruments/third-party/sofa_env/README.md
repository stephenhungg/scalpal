# LapGym Instrument Source Parts

Unmodified selected meshes from [ScheiklP/sofa_env](https://github.com/ScheiklP/sofa_env), pinned to commit `85bf7e05dd088b824794dda0046679df13b13e6e`, retrieved October 3, 2026. Root [MIT LICENSE](LICENSE) is retained. [SOURCE.json](SOURCE.json) records every exact source URL/path, SHA-256, size, triangle count, and bounding extent. Preserve this license and attribution with converted exports.

No distinct instrument/endoscope license exception was found at the pinned commit. Anatomy/robot assets were not downloaded; their neighboring source notices require separate provenance review. These STL meshes are source geometry, not Unity prefabs, material/texture packages, or validated medical equipment replicas.

## Part Mapping

| Scalpal ID | Parts in this folder |
| --- | --- |
| `atraumatic_grasper` | `instruments/instrument_shaft.stl`, `atraumatic_forceps_jaw_left.stl`, `atraumatic_forceps_jaw_right.stl`; optionally generic handle body and lever below |
| `maryland_dissector` | Same shaft, `maryland_dissector_jaw_left.stl`, `maryland_dissector_jaw_right.stl`, generic handle |
| `lap_scissors` | Same shaft, `scissors_jaw_left.stl`, `scissors_jaw_right.stl`, generic handle |
| `hook_cautery` | `instruments/dissection_electrode.stl` is a combined shaft/electrode candidate. Inspect its tip shape before treating it as a particular hook variant. Add an authored handle/activation control. |
| `laparoscope_30` | `endoscopes/laparoscope_optics_30_degree.stl` includes optics shaft/eyepiece geometry; optional body, cable, focus/zoom/coupler rings require authored assembly |

Handle candidates: `instruments/laparoscopic_handle_body.stl` or `laparoscopic_handle_body_only.stl` plus `laparoscopic_handle_body_screw.stl`, with `laparoscopic_handle_lever.stl` separate. Do not include the full body and its split body/screw duplicates together. Additional forceps and single-action forceps variants are retained as optional alternatives, not extra Scalpal catalog IDs.

## Coordinates and Articulation

STL does not encode units. The source dimensions and scene coordinates are millimeter scale. Apply uniform conversion **0.001 meters per source unit**. Instrument shaft bounds are approximately X ±2.23, Y ±2.15, Z −349.31 to +2.14 in source units. The distal jaw joint is near `(0,0,0)`, jaws extend along +Z, and the long shaft extends backwards along −Z.

Upstream [ArticulatedInstrument](https://github.com/ScheiklP/sofa_env/blob/85bf7e05dd088b824794dda0046679df13b13e6e/sofa_env/sofa_templates/rigid.py#L834) specifies `posOnParent=[0,0,0]`, `posOnChild=[0,0,0]`, default `rotation_axis=(1,0,0)`, and opposing `[+angle,-angle]` jaw rotations. Shaft and jaws share the source pose and load with the same scale. The [precision-cutting scene](https://github.com/ScheiklP/sofa_env/blob/85bf7e05dd088b824794dda0046679df13b13e6e/sofa_env/scenes/precision_cutting/scene_description.py#L190) selects the shaft/scissors jaw files without additional mesh translation. Its scene-specific angle limits are not certified device limits.

The inspected Python scenes never reference the handle body/lever or extra endoscope body/ring meshes. Consequently no exact upstream full-handle rig placement can be asserted. Shaft-relative placement, grip origin, and lever pivots need visual assembly checks. The handle body spans approximately Z −96.56 to +39.03 source units; the lever spans Z −67.49 to +9.05, with a different Y extent. Their origins should be preserved during import until assembly is checked.

The endoscope optics mesh extends backwards from the distal viewing point to approximately Z −380.5. Extra camera body/rings have their own coordinates near Z −113 to +51, so placing everything unshifted at the distal point is incorrect. The [upstream camera scene](https://github.com/ScheiklP/sofa_env/blob/85bf7e05dd088b824794dda0046679df13b13e6e/sofa_env/scenes/search_for_point/scene_description.py#L200) only loads the optics mesh and configures `oblique_viewing_angle=30.0` separately from `vertical_field_of_view=45.0`.

Inspect imported normals, smoothing, axis conversion, pivots, and materials before swapping a prefab. Rendering, pickup, cutting, and target state still belong to Scalpal runtime code; upstream SOFA physics does not transfer through an STL export.
