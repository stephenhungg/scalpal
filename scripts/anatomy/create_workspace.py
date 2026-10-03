"""Save a portable editable Blender workspace from the verified display exports."""
import json
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
atlas = json.loads((ROOT / "apps/quest/Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json").read_text())
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
body = bpy.context.scene
body.name = "Full body"
scenes = {"body": body}
palette = {"skeletal": (0.82, 0.77, 0.62, 1), "nervous": (0.85, 0.65, 0.15, 1),
           "cardiovascular": (0.7, 0.07, 0.09, 1), "visceral": (0.55, 0.18, 0.16, 1)}
for system in atlas["systems"]:
    if system["group"] == "body":
        scene = body
    else:
        scene = bpy.data.scenes.new(system["id"])
        scenes[system["id"]] = scene
    bpy.context.window.scene = scene
    previous = set(scene.objects)
    bpy.ops.import_scene.fbx(filepath=str(ROOT / "apps/quest" / system["assetPath"]))
    imported = set(scene.objects) - previous
    collection = bpy.data.collections.new(system["id"])
    scene.collection.children.link(collection)
    parts = {p["objectName"]: p for p in atlas["parts"] if p["system"] == system["id"]}
    for obj in imported:
        for old in list(obj.users_collection):
            old.objects.unlink(obj)
        collection.objects.link(obj)
        if obj.type != "MESH":
            continue
        part = parts[obj.name]
        for field in ("stableId", "displayName", "system", "catalogId"):
            obj[field] = part[field]
        obj.color = palette.get(system["id"], (0.65, 0.28, 0.23, 1))
        if "vein" in part["displayName"].lower():
            obj.color = (0.10, 0.27, 0.7, 1)
    # Hide covering systems initially; every structure remains editable in the Outliner.
    hidden = system["id"] in {"surface", "muscular", "joints", "lymphatic", "nervous"}
    collection.hide_viewport = hidden
    collection.hide_render = hidden

for scene in scenes.values():
    bpy.context.window.scene = scene
    meshes = [o for o in scene.objects if o.type == "MESH"]
    points = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    low = Vector(tuple(min(v[i] for v in points) for i in range(3)))
    high = Vector(tuple(max(v[i] for v in points) for i in range(3)))
    center = (low + high) * 0.5
    radius = max(high - low)
    bpy.ops.object.camera_add(location=center + Vector((0, -radius * 3, 0)))
    camera = bpy.context.object
    camera.rotation_euler = (center - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = radius * 1.2
    scene.camera = camera
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.render.resolution_x = 1200
    scene.render.resolution_y = 1200
    scene.render.resolution_percentage = 100
    scene.render.filepath = "//workspace-render.png"
    scene.display.shading.color_type = "OBJECT"
    scene.display.shading.show_cavity = True

bpy.context.window.scene = body
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type == "VIEW_3D":
            area.spaces.active.shading.color_type = "OBJECT"
            area.spaces.active.region_3d.view_perspective = "CAMERA"
notes = bpy.data.texts.new("START HERE")
notes.write("Scalpal editable display anatomy. Full body systems are Outliner collections; "
            "enable hidden collections to inspect covering layers. Detail organs have separate scenes "
            "because their source coordinates differ. Mesh custom properties preserve source labels "
            "and stable IDs. Full-resolution originals are in ../originals. This workspace contains "
            "the prepared display meshes, not tissue physics or participant registration. "
            "Re-running create_workspace.py overwrites this generated workspace; commit edits first.")
output = ROOT / "assets/anatomy/blender"
output.mkdir(exist_ok=True)
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=str(output / "atlas.blend"), compress=True)
# Prove that the saved artifact reopens with all geometry and embedded metadata.
bpy.ops.wm.open_mainfile(filepath=str(output / "atlas.blend"))
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
assert len(meshes) == len(atlas["parts"])
assert {o["stableId"] for o in meshes} == {p["stableId"] for p in atlas["parts"]}
assert not bpy.data.libraries, "Workspace must not require linked external libraries"
print(f"PASS saved workspace: {len(meshes)} meshes, {len(bpy.data.scenes)} scenes", flush=True)
