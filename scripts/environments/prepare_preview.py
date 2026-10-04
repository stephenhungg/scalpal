"""Blender entry point: prepare static CC0 room/patient art, not a surgery simulation."""
import argparse
import json
import math
from pathlib import Path
import sys
import bpy
from mathutils import Vector, Matrix

parser = argparse.ArgumentParser()
parser.add_argument("--root", type=Path, required=True)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
root = args.root.resolve()
assets = root / "assets/environments"
runtime = root / "apps/quest/Assets/Scalpal/Environment/Models"
runtime.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.scene.unit_settings.system = "METRIC"
bpy.context.scene.unit_settings.scale_length = 1

def bounds(objects):
    points = [o.matrix_world @ Vector(v) for o in objects for v in o.bound_box]
    return Vector([min(p[i] for p in points) for i in range(3)]), Vector([max(p[i] for p in points) for i in range(3)])

def detach(meshes):
    for o in meshes:
        matrix = o.matrix_world.copy()
        o.parent = None
        o.matrix_world = matrix

def export(objects, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects: o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.fbx(filepath=str(runtime / (name + ".fbx")), use_selection=True,
        object_types={"MESH"}, add_leaf_bones=False, bake_anim=False,
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True, use_mesh_modifiers=True)

bpy.ops.import_scene.gltf(filepath=str(assets / "operating-theatre/model.glb"))
room = [o for o in bpy.context.scene.objects if o.type == "MESH"]
detach(room)
groups = {}
for o in room:
    if len(o.data.materials) != 1: raise RuntimeError("Expected one source material per primitive")
    groups.setdefault(o.data.materials[0].name, []).append(o)
merged = []
for material, objects in groups.items():
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects: o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    o = bpy.context.object
    o.name = "Room_" + material
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    merged.append(o)
export(merged, "OperatingTheatre")

before = set(bpy.context.scene.objects)
bpy.ops.import_scene.gltf(filepath=str(assets / "supine-patient/human_posed.glb"))
patient = [o for o in bpy.context.scene.objects if o not in before and o.type == "MESH"]
detach(patient)
if len(patient) != 1: raise RuntimeError("Expected one static patient mesh")
p = patient[0]
bpy.ops.object.select_all(action="DESELECT"); p.select_set(True); bpy.context.view_layer.objects.active = p
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
lo, hi = bounds(patient)
if hi.z - lo.z < 1: raise RuntimeError("Expected standing-axis source mesh")
# The baked mesh faces Blender +X; upstream build_blend.py confirms this.
# Rotate about Y onto its back, then Z so its head points along +Y.
p.data.transform(Matrix.Rotation(-math.pi/2, 4, "Z") @ Matrix.Rotation(-math.pi/2, 4, "Y") @ Matrix.Scale(1.75/(hi.z-lo.z), 4))
p.data.update()
bpy.context.view_layer.update()
lo, hi = bounds(patient)
p.location -= (lo + hi) / 2
bpy.context.view_layer.update()
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
p.name = "SupinePatient"
mat = p.data.materials[0].copy()
mat.name = "TrainingMannequin"
mat.diffuse_color = (0.46, 0.30, 0.22, 1)
bsdf = mat.node_tree.nodes.get("Principled BSDF")
bsdf.inputs["Base Color"].default_value = mat.diffuse_color
bsdf.inputs["Metallic"].default_value = 0
bsdf.inputs["Roughness"].default_value = 0.8
p.data.materials[0] = mat
export(patient, "SupinePatient")
lo, hi = bounds(patient)
patient_extent = list(hi - lo)
p.location.z = 0.98 - lo.z
bpy.context.view_layer.update()
# Remove empty glTF roots after detaching meshes; preserve only art and authored preview setup.
for o in list(bpy.context.scene.objects):
    if o.type == "EMPTY": bpy.data.objects.remove(o, do_unlink=True)

scene = bpy.context.scene
scene.world = bpy.data.worlds.new("PreviewWorld")
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.12, 0.15, 0.19, 1)
scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.15
for name, position, energy, size in [("CeilingSoftbox",(0,0,2.8),180,4),("Fill",(2,-2,2.4),90,3)]:
    data=bpy.data.lights.new(name,"AREA"); data.energy=energy; data.shape="DISK"; data.size=size
    o=bpy.data.objects.new(name,data); scene.collection.objects.link(o); o.location=position
    o.rotation_euler=(Vector((0,0,0.9))-o.location).to_track_quat("-Z","Y").to_euler()
data=bpy.data.cameras.new("PreviewCamera"); camera=bpy.data.objects.new("PreviewCamera",data)
scene.collection.objects.link(camera); camera.location=(-2.6,-3.0,2.4)
camera.rotation_euler=(Vector((0,0,1.1))-camera.location).to_track_quat("-Z","Y").to_euler(); data.lens=29
scene.camera=camera
scene.render.engine="CYCLES"; scene.cycles.samples=32; scene.cycles.use_denoising=True; scene.cycles.device="CPU"
scene.render.resolution_x=1200; scene.render.resolution_y=900; scene.render.resolution_percentage=100
scene.view_settings.view_transform="AgX"
scene.render.filepath="//previews/operating-room-preview.png"
(assets/"previews").mkdir(exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(assets/"operating-room-preview.blend"))
bpy.ops.render.render(write_still=True)
triangles = lambda objects: sum(sum(len(poly.vertices)-2 for poly in o.data.polygons) for o in objects)
report={"roomTriangles":triangles(merged),"roomRenderMeshes":len(merged),"patientTriangles":triangles(patient),
    "patientSizeMetresBlenderXYZ":patient_extent,"patientBodyAxisBlender":"+Y","patientFaceAxisBlender":"+Z",
    "previewPatientBackHeightMetres":0.98,"runtimeExports":[str((runtime/(n+".fbx")).relative_to(root)) for n in ["OperatingTheatre","SupinePatient"]],
    "status":"Static scenery only; no native XR, organs, clinical fidelity, MR registration or surgical behavior."}
(assets/"prepared-inventory.json").write_text(json.dumps(report,indent=2)+"\n")
print("SCALPAL_ENVIRONMENT_PREPARED",json.dumps(report))
