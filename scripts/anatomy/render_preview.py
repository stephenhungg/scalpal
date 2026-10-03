"""Render the prepared geometry in Blender, not a screenshot or performance test of Quest."""
import json
from pathlib import Path
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
models = ROOT / "apps/quest/Assets/Scalpal/Anatomy/Models"
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)
colors = {"skeletal": (0.82, 0.77, 0.62, 1), "visceral": (0.55, 0.18, 0.16, 1),
          "cardiovascular": (0.7, 0.07, 0.09, 1), "nervous": (0.85, 0.65, 0.15, 1)}
for column, systems in enumerate([("skeletal", "visceral"), ("visceral", "cardiovascular"), ("cardiovascular", "nervous")]):
    for system in systems:
        previous = set(bpy.context.scene.objects)
        bpy.ops.import_scene.fbx(filepath=str(models / (system + ".fbx")))
        imported = set(bpy.context.scene.objects) - previous
        for obj in imported:
            if obj.type != "MESH":
                continue
            # World-space geometry was baked by the pipeline.
            obj.location.x += (column - 1) * 0.9
            color = colors[system]
            if "vein" in obj.name.lower():
                color = (0.10, 0.27, 0.7, 1)
            elif "liver" in obj.name.lower():
                color = (0.35, 0.08, 0.06, 1)
            elif "lung" in obj.name.lower():
                color = (0.75, 0.42, 0.45, 1)
            obj.color = color
scene = bpy.context.scene
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "OBJECT"
scene.display.shading.show_shadows = True
scene.display.shading.show_cavity = True
scene.display.shading.cavity_type = "BOTH"
scene.display.shading.background_type = "WORLD"
scene.world.color = (0.055, 0.065, 0.085)
bpy.ops.object.camera_add(location=(0, -6, 0.95))
camera = bpy.context.object
camera.rotation_euler = (Vector((0, 0, 0.95)) - camera.location).to_track_quat("-Z", "Y").to_euler()
camera.data.type = "ORTHO"
camera.data.ortho_scale = 3.25
scene.camera = camera
scene.render.resolution_x = 1600
scene.render.resolution_y = 1100
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.render.filepath = str(ROOT / "assets/anatomy/preview.png")
bpy.ops.render.render(write_still=True)
