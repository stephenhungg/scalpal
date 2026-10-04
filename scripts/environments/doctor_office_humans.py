"""Replace office prototype characters with seated CC0 MakeHuman/MPFB humans.

Requires MPFB source at the recorded source revision. This is authoring-time use;
the GPL addon is not distributed with the native runtime. Donor graphical assets
and outputs are CC0. Keep the original floral room intact.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector

parser = argparse.ArgumentParser()
parser.add_argument("--root", type=Path, required=True)
parser.add_argument("--mpfb", type=Path, required=True)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
root = args.root.resolve()
source = root / "assets/environments/doctor-office"
donor = source / "third-party/MakeHuman"
art = root / "apps/quest/Assets/Scalpal/EncounterOffice/Art"
textures = art / "Textures"
textures.mkdir(parents=True, exist_ok=True)
sys.path.insert(0, str(args.mpfb.resolve() / "src"))
import mpfb

# Keep MPFB's authoring preferences/cache in the isolated task, not global prefs.
scratch = root.parent / "mpfb-office-cache"
scratch.mkdir(exist_ok=True)
bpy.utils.extension_path_user = lambda package, path="", create=False: str(scratch)
if "mpfb" not in bpy.context.preferences.addons:
    bpy.context.preferences.addons.new().module = "mpfb"
mpfb.register()
from mpfb.services.humanservice import HumanService
from mpfb.services.targetservice import TargetService
from mpfb.services.rigservice import RigService

bpy.ops.wm.open_mainfile(filepath=str(source / "doctor-office.blend"))
bpy.context.preferences.filepaths.save_version = 0
scene = bpy.context.scene
inventory = json.loads((source / "inventory.json").read_text())
for obj in list(scene.objects):
    if obj.get("ScalpalCharacter") or obj.name.startswith(("PatientRoot", "HeadPivot", "JawPivot", "NoseTip")) or any(
        p.name.startswith("PatientRoot") for p in [obj.parent, obj.parent.parent if obj.parent else None] if p):
        bpy.data.objects.remove(obj, do_unlink=True)
# All old patient meshes were children of those roots; remove any remaining orphan
# prototype character geometry without touching room objects or preview lights.
for obj in list(scene.objects):
    if obj.type == "MESH" and any(m and m.name.startswith(("PatientFemale", "PatientMale")) for m in obj.data.materials):
        bpy.data.objects.remove(obj, do_unlink=True)
for mat in list(bpy.data.materials):
    if mat.name.startswith(("PatientFemale", "PatientMale")):
        bpy.data.materials.remove(mat, do_unlink=True)

all_patients = {}
new_materials = {}


def assign_material(obj, name, image_path, alpha_clip=False, tint=(1, 1, 1)):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.diffuse_color = (*tint, 1)
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Roughness"].default_value = 0.65
    bsdf.inputs["Base Color"].default_value = (*tint, 1)
    image = bpy.data.images.load(str(image_path), check_existing=False)
    # Runtime textures are bounded to 1024px; preserve donor originals separately.
    if max(image.size) > 1024:
        factor = 1024 / max(image.size)
        image.scale(round(image.size[0] * factor), round(image.size[1] * factor))
    if "Outfit" in name:
        # Core outfits include ripped-denim alpha islands. Fill them from nearby
        # authored denim colors to produce fully covered opaque office clothes;
        # covered body faces are masked by the donor clothing asset.
        import numpy as np
        pixels = np.array(image.pixels[:], dtype=np.float32).reshape(image.size[1], image.size[0], 4)
        valid = pixels[:, :, 3] > 0.4
        for _ in range(48):
            if valid.all():
                break
            colors = np.zeros_like(pixels[:, :, :3])
            counts = np.zeros_like(valid, dtype=np.float32)
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                adjacent = np.roll(valid, (dy, dx), axis=(0, 1))
                if dy == 1: adjacent[0, :] = False
                if dy == -1: adjacent[-1, :] = False
                if dx == 1: adjacent[:, 0] = False
                if dx == -1: adjacent[:, -1] = False
                colors += np.roll(pixels[:, :, :3], (dy, dx), axis=(0, 1)) * adjacent[:, :, None]
                counts += adjacent
            fill = (~valid) & (counts > 0)
            pixels[:, :, :3][fill] = colors[fill] / counts[fill, None]
            valid |= fill
        pixels[:, :, :3][~valid] = (0.4, 0.45, 0.5)
        pixels[:, :, 3] = 1
        image.pixels.foreach_set(pixels.ravel())
    path = textures / (name + ".png")
    image.filepath_raw = str(path)
    image.file_format = "PNG"
    image.save()
    image.pack()
    tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = image
    multiply = mat.node_tree.nodes.new("ShaderNodeMixRGB")
    multiply.blend_type = "MULTIPLY"
    multiply.inputs[0].default_value = 1
    # Shader tint is linear; metadata tint is sRGB for the native material.
    linear = tuple(v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in tint)
    multiply.inputs[2].default_value = (*linear, 1)
    mat.node_tree.links.new(tex.outputs["Color"], multiply.inputs[1])
    mat.node_tree.links.new(multiply.outputs[0], bsdf.inputs["Base Color"])
    if alpha_clip:
        cut = mat.node_tree.nodes.new("ShaderNodeMath")
        cut.operation = "GREATER_THAN"
        cut.inputs[1].default_value = 0.4
        mat.node_tree.links.new(tex.outputs["Alpha"], cut.inputs[0])
        mat.node_tree.links.new(cut.outputs[0], bsdf.inputs["Alpha"])
        mat.surface_render_method = "DITHERED"
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    new_materials[name] = {"srgb": list(tint), "roughness": 0.65, "metallic": 0,
        "diffuseTexture": str(path.relative_to(root / "apps/quest")), "alphaClip": alpha_clip}


def point_bone(rig, name, direction):
    bone = rig.pose.bones[name]
    current = bone.tail - bone.head
    q = current.rotation_difference(Vector(direction))
    bone.matrix = Matrix.Translation(bone.head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-bone.head) @ bone.matrix
    bpy.context.view_layer.update()


def make_patient(kind, gender, age, skin, outfit, hair):
    macro = TargetService.get_default_macro_info_dict()
    macro.update(gender=gender, age=age, muscle=0.40, weight=0.46, height=0.58)
    macro["race"] = {"asian": 0.65, "caucasian": 0.20, "african": 0.15} if gender == 0 else {
        "asian": 0.15, "caucasian": 0.15, "african": 0.70}
    human = HumanService.create_human(macro_detail_dict=macro)
    human.name = kind + "_Body"
    rig = HumanService.add_builtin_rig(human, "default")
    rig.name = kind + "_MakeHumanRig"
    assets = {}
    for category, filename, label in [
        ("clothes", outfit, "Outfit"), ("clothes", "shoes01", "Shoes"),
        ("hair", hair, "Hair"), ("eyebrows", "eyebrow001", "Brows"),
        ("eyes", "low-poly", "Eyes")]:
        obj = HumanService.add_mhclo_asset(str(donor / category / filename / (filename + ".mhclo")), human,
            asset_type={"hair": "Hair", "eyes": "Eyes", "eyebrows": "Eyebrows"}.get(category, "Clothes"),
            subdiv_levels=0, set_up_rigging=True, interpolate_weights=True, import_subrig=False)
        obj.name = kind + "_" + label
        assets[label] = obj
    skin_texture = next((donor / "skins" / skin).glob("*diffuse*.png"))
    assign_material(human, kind + "_Skin", skin_texture)
    for label, folder, filename in [
        ("Outfit", "clothes/" + outfit, outfit + "_diffuse.png"),
        ("Shoes", "clothes/shoes01", "shoes01_diffuse.png"),
        ("Hair", "hair/" + hair, hair + "_diffuse.png"),
        ("Brows", "eyebrows/eyebrow001", "eyebrow001.png"),
        ("Eyes", "eyes/materials", "brown_eye.png")]:
        assign_material(assets[label], kind + "_" + label, donor / folder / filename,
            alpha_clip=label in ("Hair", "Brows"), tint=(0.20, 0.13, 0.09) if label == "Hair" else (1, 1, 1))
    # Set actual thigh/shin directions in world coordinates. Continuous weighted
    # meshes and clothes follow the real donor rig rather than separate cylinders.
    for side in ("L", "R"):
        point_bone(rig, "upperleg01." + side, (0, -1, -0.05))
        point_bone(rig, "lowerleg01." + side, (0, -0.08, -1))
        point_bone(rig, "upperarm01." + side, (0.04 if side == "L" else -0.04, 0, -1))
        point_bone(rig, "lowerarm01." + side, (-0.30 if side == "L" else 0.30, -1, -0.17))
    rig.location.z = 0.55 - rig.pose.bones["upperleg01.L"].head.z
    rig.location.y = 0.025 - rig.pose.bones["upperleg01.L"].head.y
    bpy.context.view_layer.update()
    RigService.apply_pose_as_rest_pose(rig)
    # Apply all helper/clothing masks so runtime exports never reveal covered body
    # topology. Retain the armature modifier and the genuine head/jaw weights.
    meshes = [human, *assets.values()]
    for obj in meshes:
        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        for mod in list(obj.modifiers):
            if mod.type != "ARMATURE":
                bpy.ops.object.modifier_apply(modifier=mod.name)
        for poly in obj.data.polygons:
            poly.use_smooth = True
    bpy.context.view_layer.update()
    evaluated_shoes = assets["Shoes"].evaluated_get(bpy.context.evaluated_depsgraph_get())
    shoe_geometry = evaluated_shoes.to_mesh()
    lowest_shoe = min((evaluated_shoes.matrix_world @ v.co).z for v in shoe_geometry.vertices)
    evaluated_shoes.to_mesh_clear()
    if lowest_shoe < 0.012:
        rig.location.z += 0.012 - lowest_shoe
        bpy.context.view_layer.update()
    # Names are real deforming donor bones, not decorative empty nodes.
    for old, new in (("head", "HeadPivot"), ("jaw", "JawPivot")):
        rig.data.bones[old].name = new
        for obj in meshes:
            group = obj.vertex_groups.get(old)
            if group:
                group.name = new
    root_obj = bpy.data.objects.new("PatientRoot", None)
    scene.collection.objects.link(root_obj)
    rig.parent = root_obj
    bpy.context.view_layer.update()
    # Bind a real facial direction locator to the actual head bone.
    nose_world = min((human.matrix_world @ v.co for v in human.data.vertices
        if (human.matrix_world @ v.co).z > rig.location.z + rig.data.bones["HeadPivot"].head_local.z), key=lambda p: p.y)
    nose = bpy.data.objects.new("NoseTip", None)
    scene.collection.objects.link(nose)
    nose.location = nose_world
    bpy.context.view_layer.update()
    world = nose.matrix_world.copy()
    nose.parent = rig
    nose.parent_type = "BONE"
    nose.parent_bone = "HeadPivot"
    bpy.context.view_layer.update()
    nose.matrix_world = world
    objects = [root_obj, rig, nose, *meshes]
    for obj in objects:
        obj["ScalpalCharacter"] = kind
    return objects


all_patients["PatientFemale"] = make_patient("PatientFemale", 0, 0.6154, "middleage_asian_female", "female_casualsuit01", "bob02")
all_patients["PatientMale"] = make_patient("PatientMale", 1, 0.5385, "young_african_male", "male_casualsuit01", "short04")
for obj in all_patients["PatientMale"]:
    obj.hide_render = True
    obj.hide_set(True)
scene["art_provenance"] = "Original Scalpal floral office; seated characters derived from MakeHuman/MPFB CC0 graphical assets, actual deforming rigs and donor skin/clothing/hair textures."
bpy.ops.wm.save_as_mainfile(filepath=str(source / "doctor-office.blend"))
# Also retain a standalone editable human library used by the office rebuild.
bpy.data.libraries.write(str(source / "patients.blend"), set(o for objects in all_patients.values() for o in objects), fake_user=True)


def export_patient(kind, objects):
    root_obj = objects[0]
    # Match the stable room export rotation. Native measured import correction
    # rotates the whole office/patients 180 degrees back to clinician +Z.
    root_obj.rotation_euler.z = math.pi
    bpy.context.view_layer.update()
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.hide_set(False)
        obj.select_set(True)
    # Canonical Empty names are scoped per FBX, not suffixed by the other source
    # character. The two armatures already have independent HeadPivot/JawPivot bones.
    rename = {o: o.name for o in scene.objects if o.type == "EMPTY"}
    for obj in rename:
        obj.name = obj.name + "_source"
    for obj in objects:
        if obj.type == "EMPTY":
            obj.name = "NoseTip" if rename[obj].startswith("NoseTip") else "PatientRoot"
    path = art / "Models" / (kind + ".fbx")
    bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True, object_types={"MESH", "EMPTY", "ARMATURE"},
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True, global_scale=1, use_mesh_modifiers=True,
        add_leaf_bones=False, bake_anim=False, path_mode="STRIP", use_armature_deform_only=True)
    for i, obj in enumerate(rename):
        obj.name = "SourceEmptyRestore" + str(i)
    for obj, original in rename.items():
        obj.name = original
    meshes = [o for o in objects if o.type == "MESH"]
    triangles = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in meshes)
    assert triangles < 50000, (kind, triangles)
    points = [o.matrix_world @ Vector(v) for o in meshes for v in o.bound_box]
    info = {"triangles": triangles, "renderMeshes": len(meshes), "materials": [m.name for o in meshes for m in o.data.materials],
        "boundsBlenderXYZMetres": [[min(p[i] for p in points) for i in range(3)], [max(p[i] for p in points) for i in range(3)]],
        "path": str(path.relative_to(root)), "bytes": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "rig": "MakeHuman default, seated pose baked as rest; head/jaw remain genuinely weighted deforming bones",
        "headBone": "HeadPivot", "jawBone": "JawPivot", "texturesMaxDimension": 1024}
    root_obj.rotation_euler.z = 0
    bpy.context.view_layer.update()
    return info


for kind, objects in all_patients.items():
    inventory["assets"][kind] = export_patient(kind, objects)
inventory["materials"] = {k: v for k, v in inventory["materials"].items() if k.startswith("Office_")}
inventory["materials"].update(new_materials)
inventory["unityMaterials"] = [{"name": name, **data} for name, data in inventory["materials"].items()]
inventory["provenance"] = "Original floral office. Both patients derive from MakeHuman/MPFB CC0 base/targets/default rig, system clothes/hair/eyes/brows/skin textures. See third-party/MakeHuman and ATTRIBUTION.md."
inventory["exportAxes"]["UnityPatientForward"] = "-Z before native scene yaw correction"
inventory["exportAxes"]["UnityMeasuredSceneYawCorrectionDegrees"] = 180
inventory.pop("fbxReimportChecks", None)
inventory["limitations"] = ["Fictional adult avatars, artist-selected phenotype, not likenesses of real patients.",
    "Actual donor head/jaw bones support bounded motion; no calibrated viseme lip-sync or realistic facial performance is claimed.",
    "Skin/hair/clothing maps capped at 1024px; only one patient is active. Quest stereo appearance/frame time remain physical checks.",
    "Medical fixtures remain scenery; all findings and assessment use the encounter engine."]
(source / "inventory.json").write_text(json.dumps(inventory, indent=2) + "\n")
scene.render.resolution_x = 1600
scene.render.resolution_y = 1000
scene.render.filepath = str(source / "previews/office-conversation.png")
for obj in all_patients["PatientMale"]:
    obj.hide_render = True
for obj in all_patients["PatientFemale"]:
    obj.hide_render = False
bpy.ops.render.render(write_still=True)
camera = scene.camera
camera.location = (0.72, -2.32, 1.41)
camera.rotation_euler = (Vector((0, -0.08, 0.78)) - camera.location).to_track_quat("-Z", "Y").to_euler()
camera.data.lens = 52
scene.render.resolution_x = 900
scene.render.resolution_y = 1100
for obj in scene.objects:
    if obj.type == "MESH" and not obj.get("ScalpalCharacter"):
        obj.hide_render = True
for kind in all_patients:
    for other, objects in all_patients.items():
        for obj in objects:
            obj.hide_render = other != kind
    scene.render.filepath = str(source / "previews" / (kind + ".png"))
    bpy.ops.render.render(write_still=True)
print("SCALPAL_CC0_HUMANS_PREPARED", json.dumps(inventory["assets"]))
