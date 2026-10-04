# Demo Environment Art

Reusable CC0 room and patient assets for the proposed full-VR demo. The accepted two-mode design is in [environment modes](../../docs/environment-modes.md), with [candidate research](../../docs/research/surgery-environments.md).

- [Editable Blender preview](operating-room-preview.blend) and [render](previews/operating-room-preview.png).
- [Prepared Unity art and preview scene](../../apps/quest/Assets/Scalpal/Environment/): FBXs, materials, room/patient prefabs and `Samples/OperatingRoomPreview.unity`.
- Unmodified source GLBs, license notices and upstream patient README are preserved here. See [attribution](ATTRIBUTION.md), [source hashes](sources.json) and [prepared inventory](prepared-inventory.json).

Open `apps/quest` with Unity 6000.0.66f2, then open `Assets/Scalpal/Environment/Samples/OperatingRoomPreview.unity`. The preview is intentionally static: no XR initialization, organs, tool controls, procedure logic or participant registration is supplied. The existing instrument sandbox remains the enabled build scene.

## Rebuild

From the repository root:

```bash
blender --background --disable-autoexec --python scripts/environments/prepare_preview.py -- --root .
```

Then use **Scalpal → Environment → Build Static Operating Room Preview** in Unity. This regenerates the prefabs, materials and preview scene. Use separate prefab variants for customizations.

The prepared room has **12,168 triangles and eight renderers**; the patient has **1,578 triangles**, with body length 1.75 m. Those are imported geometry measurements, not Quest FPS measurements. The original room has many instanced pieces; consolidation reduces renderer count. The original GLB triangle count differs slightly from Blender's imported count because the import/conversion does not retain every source primitive triangle.

The real-person overlay will consume Matthew's selected anatomy assets. This mannequin provides only the visible surface of the VR patient; it is not a replacement for the organ pipeline or a source of clinically accurate proportions.
