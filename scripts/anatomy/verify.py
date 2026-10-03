"""Blender round-trip verification of every generated runtime FBX."""
import hashlib
import json
from pathlib import Path
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "apps/quest"
doc = json.loads((ASSETS / "Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json").read_text())
ids = [p["stableId"] for p in doc["parts"]]
assert len(ids) == len(set(ids)), "Duplicate stable IDs"
for system in doc["systems"]:
    path = ASSETS / system["assetPath"]
    assert hashlib.sha256(path.read_bytes()).hexdigest() == system["sha256"], path
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.ops.import_scene.fbx(filepath=str(path))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    expected = {p["objectName"]: p for p in doc["parts"] if p["system"] == system["id"]}
    assert len(meshes) == system["meshCount"], f"Mesh count mismatch: {system['id']}"
    assert {o.name for o in meshes} == set(expected), f"Name mismatch in {system['id']}"
    for o in meshes:
        o.data.calc_loop_triangles()
        assert len(o.data.loop_triangles) > 0, f"Nonrenderable marker exported: {o.name}"
        assert len(o.data.loop_triangles) == expected[o.name]["triangles"], o.name
        assert len(o.data.vertices) > 0, o.name
        if expected[o.name]["catalogId"]:
            assert expected[o.name]["sourceTriangles"] == expected[o.name]["triangles"], f"Surgery target simplified: {o.name}"
        if system["group"] == "body":
            assert not expected[o.name]["displayName"].lower().endswith((".i", ".j")), f"Guide in tissue export: {o.name}"
    assert sum(len(o.data.loop_triangles) for o in meshes) == system["triangles"], system["id"]
    bounds = [[min((o.matrix_world @ Vector(c))[a] for o in meshes for c in o.bound_box),
               max((o.matrix_world @ Vector(c))[a] for o in meshes for c in o.bound_box)] for a in range(3)]
    for axis in range(3):
        for side in range(2):
            assert abs(bounds[axis][side] - system["sourceBoundsBlenderXYZ"][axis][side]) < 0.001, f"Frame changed: {system['id']}"
    print(f"PASS {system['id']}: {len(meshes)} meshes, names/triangles/bounds/checksum", flush=True)
print(f"PASS atlas: {len(doc['systems'])} systems, {len(ids)} parts", flush=True)
