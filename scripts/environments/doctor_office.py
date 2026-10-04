"""Author original metric office/patients in Blender and export bounded native art.

Run: blender --background --disable-autoexec --python scripts/environments/doctor_office.py -- --root .
The office is original art; accepted humans are appended from the CC0 MakeHuman library. No real patient record, user portrait or provider data is used.
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
parser.add_argument("--office-only", action="store_true", help="Author only the floral room before installing CC0 human source")
parser.add_argument("--verify-only", action="store_true", help="Re-import existing FBX files and check saved inventory")
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
root = args.root.resolve()
source = root / "assets/environments/doctor-office"
runtime = root / "apps/quest/Assets/Scalpal/EncounterOffice/Art/Models"


def verify_exports(report):
    result = {}
    for name, info in report["assets"].items():
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.fbx(filepath=str(root / info["path"]))
        bpy.context.view_layer.update()
        objects = list(bpy.context.scene.objects)
        meshes = [o for o in objects if o.type == "MESH"]
        measured = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in meshes)
        assert measured == info["triangles"], (name, measured, info["triangles"])
        assert len(meshes) == info["renderMeshes"], (name, len(meshes))
        assert all(o.data.materials and all(m is not None for m in o.data.materials) for o in meshes)
        if "nodes" in info:
            assert sorted(o.name for o in objects) == info["nodes"], (name, [o.name for o in objects])
        if name.startswith("Patient"):
            nodes = {o.name: o for o in objects}
            assert "PatientRoot" in nodes and "NoseTip" in nodes
            rigs = [o for o in objects if o.type == "ARMATURE"]
            assert len(rigs) == 1
            rig = rigs[0]
            assert all(n in rig.pose.bones for n in ("HeadPivot", "JawPivot"))
            assert all(any(m.type == "ARMATURE" and m.object == rig for m in o.modifiers) for o in meshes)
            skin = next(o for o in meshes if "Body" in o.name)
            depsgraph = bpy.context.evaluated_depsgraph_get()
            def evaluated_positions():
                bpy.context.view_layer.update()
                evaluated = skin.evaluated_get(depsgraph)
                geometry = evaluated.to_mesh()
                positions = [evaluated.matrix_world @ v.co for v in geometry.vertices]
                evaluated.to_mesh_clear()
                return positions
            before = evaluated_positions()
            movement = {}
            for bone_name in ("HeadPivot", "JawPivot"):
                group = skin.vertex_groups.get(bone_name)
                assert group is not None
                assert sum(g.weight for v in skin.data.vertices for g in v.groups if g.group == group.index) > 0.1
                bone = rig.pose.bones[bone_name]
                original = bone.matrix_basis.copy()
                bone.rotation_mode = "XYZ"
                bone.rotation_euler.x = math.radians(5)
                after = evaluated_positions()
                movement[bone_name] = max((a - b).length for a, b in zip(after, before))
                assert movement[bone_name] > 0.002, (name, bone_name, movement[bone_name])
                bone.matrix_basis = original
                bpy.context.view_layer.update()
            result[name] = {"boneDeformationMetresAtFiveDegrees": movement}
            # Actual Unity import was measured facing -Z, corrected by the native
            # scene builder's whole-art yaw180. This check is Blender re-import.
            nose = nodes["NoseTip"].matrix_world.translation
            assert nose.y > 0.05 and nose.z > 1.0, (name, tuple(nose))
        result.setdefault(name, {}).update(triangles=measured, renderMeshes=len(meshes), hierarchyAndMaterials="passed")
    print("SCALPAL_OFFICE_FBX_VERIFIED", json.dumps(result))
    return result


if args.verify_only:
    verify_exports(json.loads((source / "inventory.json").read_text()))
    sys.exit(0)
source.mkdir(parents=True, exist_ok=True)
runtime.mkdir(parents=True, exist_ok=True)
(source / "previews").mkdir(exist_ok=True)
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.preferences.filepaths.save_version = 0
scene = bpy.context.scene
scene.unit_settings.system = "METRIC"
scene.unit_settings.scale_length = 1
palette = {}
groups = {"DoctorOffice": [], "PatientFemale": [], "PatientMale": []}
role = "DoctorOffice"
parent = None


def material(name, rgb, roughness=0.7, metallic=0.0):
    # Palette input is sRGB; Blender shader values are linear.
    linear = tuple(v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in rgb)
    m = bpy.data.materials.new(name)
    m.diffuse_color = (*linear, 1)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = m.diffuse_color
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    palette[name] = {"srgb": list(rgb), "roughness": roughness, "metallic": metallic}
    return m


wall = material("Office_WallWarm", (0.94, 0.88, 0.86))
white = material("Office_White", (0.94, 0.95, 0.92), 0.38)
teal = material("Office_Teal", (0.17, 0.39, 0.39))
wood = material("Office_Wood", (0.59, 0.39, 0.23))
oak = material("Office_OakLight", (0.77, 0.60, 0.40))
floor = material("Office_Floor", (0.73, 0.75, 0.69))
metal = material("Office_Metal", (0.58, 0.65, 0.65), 0.3, 0.5)
dark = material("Office_Charcoal", (0.15, 0.21, 0.23))
fabric = material("Office_Upholstery", (0.55, 0.68, 0.60))
plantmat = material("Office_Plant", (0.26, 0.46, 0.30))
accent = material("Office_Terracotta", (0.77, 0.43, 0.30))
glass = material("Office_WindowSky", (0.63, 0.79, 0.82), 0.4)
glow = material("Office_LampWarm", (1.0, 0.89, 0.67), 0.5)
rug = material("Office_Rug", (0.69, 0.71, 0.60))
rose = material("Office_FlowerRose", (0.94, 0.59, 0.65))
lilac = material("Office_FlowerLilac", (0.72, 0.62, 0.85))
butter = material("Office_FlowerButter", (1.0, 0.84, 0.55))


def record(obj, name, mat):
    obj.name = name
    obj.data.materials.append(mat)
    if parent:
        world = obj.matrix_world.copy()
        obj.parent = parent
        obj.matrix_world = world
    groups[role].append(obj)
    return obj


def finish(obj, bevel=0):
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel:
        mod = obj.modifiers.new("Soft edges", "BEVEL")
        mod.width = bevel
        mod.segments = 2
        bpy.ops.object.modifier_apply(modifier=mod.name)
        obj.modifiers.new("Weighted normals", "WEIGHTED_NORMAL")
    return obj


def box(name, at, size, mat, bevel=0.025, rot=(0, 0, 0)):
    bpy.ops.mesh.primitive_cube_add(size=1, location=at, rotation=rot)
    o = bpy.context.object
    o.scale = size
    finish(o, min(bevel, min(size) * 0.35))
    return record(o, name, mat)


def ellipsoid(name, at, size, mat, segments=16, rings=8):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=rings, radius=1, location=at)
    o = bpy.context.object
    o.scale = size
    finish(o)
    for p in o.data.polygons:
        p.use_smooth = True
    return record(o, name, mat)


def cylinder(name, at, radius, depth, mat, vertices=12):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=at)
    # Thin stems/rails do not benefit from bevel rings at headset scale.
    return record(finish(bpy.context.object, min(radius * 0.1, 0.009) if radius >= 0.05 else 0), name, mat)


def rod(name, a, b, radius, mat, vertices=10):
    vec = Vector(b) - Vector(a)
    o = cylinder(name, (Vector(a) + Vector(b)) / 2, radius, vec.length, mat, vertices)
    o.rotation_euler = vec.to_track_quat("Z", "Y").to_euler()
    return o


# Room is 5.8 x 6.0 m, floor at zero. Patient faces Blender -Y.
box("Floor slab", (0, -0.45, -0.065), (5.8, 6.0, 0.13), floor)
box("Rear wall", (0, 2.5, 1.5), (5.8, 0.12, 3), wall)
box("Left wall", (-2.9, -0.45, 1.5), (0.12, 6.0, 3), wall)
box("Right wall", (2.9, -0.45, 1.5), (0.12, 6.0, 3), wall)
box("Front wall", (0, -3.45, 1.5), (5.8, 0.12, 3), wall)
# Ceiling is intentionally an open source surface; actual native ambient/directional
# light is configured by the scene builder, not imported as unbounded FBX lights.
box("Rear teal feature wall", (-0.72, 2.41, 1.36), (3.75, 0.035, 2.55), teal, 0.004)
for x in [-2.72 + i * 0.095 for i in range(9)]:
    box("Oak wall batten", (x, 2.365, 1.35), (0.037, 0.045, 2.6), oak, 0.004)
for x in (-2.83, 2.83):
    box("Side skirting", (x, -0.45, 0.08), (0.035, 5.85, 0.15), white, 0.005)
box("Rear skirting", (0, 2.415, 0.08), (5.72, 0.035, 0.15), white, 0.005)
box("Conversation rug", (0, -0.6, 0.017), (2.9, 2.7, 0.025), rug, 0.006)
for x in (-1.39, 1.39):
    box("Rug edge", (x, -0.6, 0.033), (0.035, 2.56, 0.008), oak, 0)

# Window recess and slatted shade: opaque decorative glazing; no alpha overdraw.
box("Window frame", (1.62, 2.405, 1.87), (1.82, 0.12, 1.46), white)
box("Window glazing", (1.62, 2.335, 1.87), (1.65, 0.01, 1.28), glass, 0)
for z in [1.27 + i * 0.145 for i in range(9)]:
    box("Window shade slat", (1.62, 2.305, z), (1.63, 0.06, 0.035), oak, 0.004)
box("Window center mullion", (1.62, 2.275, 1.87), (0.03, 0.035, 1.29), white, 0.004)
box("Window sill", (1.62, 2.26, 1.11), (1.94, 0.29, 0.045), oak)

# Privacy door and coat hooks.
box("Door frame", (-2.817, -2.43, 1.14), (0.07, 1.04, 2.28), white)
box("Oak door", (-2.769, -2.43, 1.10), (0.034, 0.90, 2.14), wood, 0.006)
box("Door upper inset", (-2.744, -2.43, 1.69), (0.008, 0.66, 0.47), oak, 0.003)
rod("Door lever", (-2.716, -2.10, 1.02), (-2.716, -2.23, 1.02), 0.016, metal)
for y in (-1.45, -1.26, -1.07):
    rod("Coat hook", (-2.81, y, 1.7), (-2.70, y, 1.7), 0.012, metal)


def chair(name, x, y, turn=0, patient=False):
    # Patient faces -Y; clinician chair faces +Y.
    items = []
    before = len(groups[role])
    box(name + " seat", (x, y, 0.47), (0.63, 0.55, 0.10), fabric, 0.045)
    box(name + " back", (x, y + 0.23, 0.83), (0.64, 0.11, 0.60), fabric, 0.045,
        rot=(math.radians(-6), 0, 0))
    for dx in (-0.28, 0.28):
        rod(name + " back support", (x + dx, y + 0.21, 0.35), (x + dx, y + 0.27, 1.00), 0.018, metal)
        box(name + " armrest", (x + dx, y - 0.01, 0.68), (0.057, 0.42, 0.065), wood, 0.021)
        rod(name + " arm upright", (x + dx, y - 0.15, 0.40), (x + dx, y - 0.15, 0.65), 0.018, metal)
        for dy in (-0.19, 0.19):
            rod(name + " leg", (x + dx, y + dy, 0.42), (x + dx * 1.15, y + dy * 1.12, 0.07), 0.022, metal)
            cylinder(name + " foot", (x + dx * 1.15, y + dy * 1.12, 0.042), 0.033, 0.022, dark)
    if turn:
        from mathutils import Matrix
        rotation = Matrix.Translation((x, y, 0)) @ Matrix.Rotation(turn, 4, "Z") @ Matrix.Translation((-x, -y, 0))
        for o in groups[role][before:]:
            o.matrix_world = rotation @ o.matrix_world


chair("Patient chair", 0, 0)
chair("Clinician chair", 0, -1.85, math.pi)
chair("Companion chair", -1.15, 0.3, -0.16)

# Desk is beside, not between, the learner and patient.
box("Desk oak top", (-1.77, -0.87, 0.765), (1.62, 0.75, 0.09), oak, 0.035)
box("Desk pedestal", (-2.30, -0.87, 0.365), (0.43, 0.61, 0.71), white)
for z in (0.21, 0.42, 0.63):
    box("Drawer fascia", (-2.30, -1.181, z), (0.39, 0.018, 0.18), white, 0.006)
    rod("Drawer pull", (-2.38, -1.206, z + 0.035), (-2.22, -1.206, z + 0.035), 0.007, metal)
for y in (-1.16, -0.59):
    rod("Desk leg", (-1.11, y, 0.04), (-1.11, y, 0.72), 0.027, metal)
box("Closed laptop base", (-1.69, -0.89, 0.828), (0.39, 0.27, 0.025), dark, 0.008)
box("Laptop lid", (-1.69, -0.77, 1.008), (0.39, 0.019, 0.29), dark, 0.009, rot=(math.radians(-14), 0, 0))
box("Laptop neutral screen", (-1.69, -0.795, 1.01), (0.345, 0.01, 0.24), teal, 0.001, rot=(math.radians(-14), 0, 0))
box("Desk notebook", (-2.13, -0.89, 0.830), (0.21, 0.28, 0.022), accent, 0.004)
box("Notebook pages", (-2.128, -0.895, 0.842), (0.19, 0.26, 0.008), white, 0.002)
rod("Desk pen", (-2.10, -0.97, 0.856), (-2.08, -0.79, 0.856), 0.005, dark)
cylinder("Cup", (-1.24, -0.61, 0.885), 0.047, 0.13, white)

# Medical alcove: exam couch, paper roll, side step, kit/trolley and privacy rail.
box("Examination couch base", (2.01, 0.81, 0.46), (0.72, 1.90, 0.48), white)
box("Examination cushion", (2.01, 0.81, 0.73), (0.79, 1.98, 0.105), fabric, 0.042)
box("Couch headrest", (2.01, 1.47, 0.865), (0.77, 0.60, 0.16), fabric, 0.042,
    rot=(math.radians(13), 0, 0))
box("Examination paper", (2.01, 0.64, 0.789), (0.61, 1.62, 0.008), white, 0)
roll = cylinder("Paper roll", (2.01, 1.89, 0.72), 0.07, 0.71, white)
roll.rotation_euler.y = math.pi / 2
box("Couch step", (1.39, -0.09, 0.14), (0.39, 0.48, 0.24), metal)
box("Step nonslip top", (1.39, -0.09, 0.264), (0.35, 0.45, 0.018), dark, 0.008)
for x in (1.31, 2.62):
    rod("Privacy rail support", (x, 1.9, 0.02), (x, 1.9, 2.15), 0.018, metal)
rod("Privacy rail", (1.31, 1.9, 2.15), (2.62, 1.9, 2.15), 0.018, metal)
for x in [2.34 + i * 0.041 for i in range(7)]:
    box("Folded privacy curtain", (x, 1.905 + (int(x * 100) % 2) * 0.022, 1.25), (0.057, 0.052, 1.7), white, 0.011)
box("Rolling medical trolley", (2.2, -1.04, 0.68), (0.62, 0.49, 0.07), metal)
box("Trolley lower tray", (2.2, -1.04, 0.28), (0.61, 0.48, 0.048), white)
for x in (1.95, 2.45):
    for y in (-1.23, -0.85):
        rod("Trolley upright", (x, y, 0.1), (x, y, 0.71), 0.013, metal)
        ellipsoid("Trolley caster", (x, y, 0.07), (0.031, 0.018, 0.041), dark, 10, 5)
box("Medical kit", (2.16, -1.00, 0.81), (0.35, 0.28, 0.18), accent)
box("Kit cross vertical", (2.16, -1.145, 0.81), (0.035, 0.004, 0.10), white, 0)
box("Kit cross horizontal", (2.16, -1.15, 0.81), (0.10, 0.004, 0.035), white, 0)
box("Folded towels", (2.2, -1.04, 0.33), (0.33, 0.30, 0.04), fabric)

# Sink/cabinet fixtures are decorative and do not yield fabricated examination data.
box("Clinical cabinet", (-2.20, 1.76, 0.48), (1.07, 0.65, 0.91), white)
box("Counter top", (-2.20, 1.76, 0.96), (1.15, 0.72, 0.066), oak)
box("Sink inset", (-2.31, 1.75, 1.0), (0.57, 0.43, 0.018), metal, 0.05)
box("Sink bowl", (-2.31, 1.74, 1.015), (0.46, 0.32, 0.024), white, 0.055)
rod("Faucet upright", (-2.31, 2.00, 1.02), (-2.31, 2.00, 1.21), 0.021, metal)
rod("Faucet spout", (-2.31, 2.00, 1.21), (-2.31, 1.86, 1.21), 0.021, metal)
for x in (-2.47, -1.93):
    box("Cabinet door", (x, 1.426, 0.48), (0.49, 0.026, 0.79), white, 0.006)
    rod("Cabinet handle", (x + 0.15, 1.40, 0.45), (x + 0.15, 1.40, 0.62), 0.008, metal)
box("Soap dispenser", (-1.77, 1.79, 1.074), (0.10, 0.11, 0.17), teal)
rod("Soap pump", (-1.77, 1.79, 1.19), (-1.77, 1.72, 1.19), 0.012, metal)
box("Wall glove box", (-1.92, 2.31, 1.45), (0.30, 0.16, 0.15), white)
ellipsoid("Glove box opening", (-1.92, 2.215, 1.46), (0.09, 0.014, 0.025), dark)
cylinder("Waste bin", (-2.55, 0.99, 0.23), 0.16, 0.44, white)
cylinder("Waste bin lid", (-2.55, 0.99, 0.464), 0.167, 0.039, dark)

# Art, books, planted greenery and a warm practical lamp soften the medical room.
box("Art frame", (-0.64, 2.36, 1.88), (1.35, 0.055, 0.87), oak)
box("Art cream ground", (-0.64, 2.32, 1.88), (1.24, 0.015, 0.76), wall, 0)
ellipsoid("Art terracotta circle", (-0.88, 2.305, 1.97), (0.21, 0.008, 0.21), accent)
box("Art landscape", (-0.41, 2.29, 1.68), (0.66, 0.006, 0.13), teal, 0)
box("Art horizon", (-0.82, 2.285, 1.70), (0.39, 0.006, 0.035), oak, 0)
box("Book shelf", (-0.54, 2.08, 0.73), (1.26, 0.38, 0.045), wood)
for i, m in enumerate((white, accent, oak, white, teal, accent)):
    box("Shelf book", (-0.98 + i * 0.071, 2.05, 0.89), (0.052, 0.22, 0.26 + i % 2 * 0.042), m, 0.004)
for x, y, z, s in [(0.41, 2.07, 0.775, 0.6), (-2.44, -0.17, 0.05, 1.35), (2.47, -2.44, 0.05, 1.55)]:
    cylinder("Plant terracotta pot", (x, y, z + 0.10 * s), 0.11 * s, 0.2 * s, accent)
    cylinder("Plant pot soil", (x, y, z + 0.20 * s), 0.095 * s, 0.008, dark)
    for i in range(7):
        a = i * 2.4
        tip = Vector((x + math.cos(a) * 0.12 * s, y + math.sin(a) * 0.12 * s, z + (0.34 + i % 3 * 0.09) * s))
        rod("Plant stem", (x, y, z + 0.18 * s), tip, 0.005 * s, plantmat, 6)
        leaf = ellipsoid("Plant leaf", tip, (0.05 * s, 0.025 * s, 0.13 * s), plantmat, 8, 4)
        leaf.rotation_euler = (0.3, 0.7, a)
cylinder("Floor lamp base", (-1.08, 1.25, 0.044), 0.22, 0.06, metal)
rod("Floor lamp stem", (-1.08, 1.25, 0.08), (-1.08, 1.25, 1.72), 0.015, metal)
bpy.ops.mesh.primitive_cone_add(vertices=24, radius1=0.22, radius2=0.16, depth=0.25, location=(-1.08, 1.25, 1.66))
record(bpy.context.object, "Floor lamp shade", glow)
for x in (-1.65, 1.65):
    box("Ceiling panel light", (x, 0.15, 2.92), (1.05, 0.46, 0.04), white)
    box("Ceiling diffuser", (x, 0.15, 2.89), (0.97, 0.38, 0.015), glow)


def flower(name, center, radius, petal, wall_facing=False):
    """Five softly faceted petals; opaque original meshes batched by palette."""
    x, y, z = center
    for i in range(5):
        angle = i * math.tau / 5
        if wall_facing:
            o = ellipsoid(name + " petal", (x + math.cos(angle) * radius * 0.57, y,
                z + math.sin(angle) * radius * 0.57), (radius * 0.61, radius * 0.13, radius * 0.34), petal, 8, 4)
            o.rotation_euler.y = -angle
        else:
            o = ellipsoid(name + " petal", (x + math.cos(angle) * radius * 0.57,
                y + math.sin(angle) * radius * 0.57, z), (radius * 0.61, radius * 0.34, radius * 0.14), petal, 8, 4)
            o.rotation_euler.z = angle
    ellipsoid(name + " center", (x, y - radius * 0.12 if wall_facing else y,
        z if wall_facing else z + radius * 0.13), (radius * 0.25, radius * 0.18, radius * 0.25), butter, 8, 4)


# A lush floral installation frames the patient. Geometry stays on the rear wall;
# no flower cards, transparency, imported textures or interactive obstacles.
for side in (-1, 1):
    x0 = -0.63 + side * 0.91
    for i in range(11):
        z = 0.86 + i * 0.155
        x = x0 + math.sin(i * 0.85) * 0.12
        rod("Climbing floral vine", (x, 2.255, z), (x0 + math.sin((i + 1) * 0.85) * 0.12, 2.255, z + 0.155), 0.012, plantmat, 6)
        leaf = ellipsoid("Floral wall leaf", (x + side * 0.075, 2.25, z), (0.11, 0.025, 0.049), plantmat, 8, 4)
        leaf.rotation_euler.y = side * (0.5 if i % 2 else -0.3)
        flower("Wall blossom", (x, 2.21, z + 0.055), 0.085 + i % 3 * 0.014,
            (rose, lilac, white)[i % 3], True)
for i in range(13):
    x = -1.65 + i * 0.17
    z = 2.57 + math.sin(i * 0.5) * 0.08
    rod("Flower arch stem", (x, 2.24, z), (x + 0.17, 2.24, z + 0.03), 0.013, plantmat, 6)
    flower("Arch blossom", (x, 2.20, z), 0.10, (rose, lilac, white)[i % 3], True)
    ellipsoid("Arch leaf", (x + 0.075, 2.245, z + 0.09), (0.10, 0.025, 0.035), plantmat, 8, 4)


def bouquet(name, base, scale=1):
    x, y, z = base
    cylinder(name + " lilac vase", (x, y, z + 0.12 * scale), 0.09 * scale, 0.24 * scale, lilac)
    for i in range(9):
        angle = i * 2.4
        offset = (0.13 if i < 7 else 0.05) * scale
        tip = (x + math.cos(angle) * offset, y + math.sin(angle) * offset, z + (0.39 + i % 3 * 0.045) * scale)
        rod(name + " stem", (x, y, z + 0.19 * scale), tip, 0.004 * scale, plantmat, 6)
        flower(name + " bloom", tip, 0.08 * scale, (rose, white, lilac)[i % 3])
        leaf = ellipsoid(name + " leaf", (x + math.cos(angle) * offset * 0.65,
            y + math.sin(angle) * offset * 0.65, z + 0.30 * scale),
            (0.055 * scale, 0.025 * scale, 0.018 * scale), plantmat, 8, 4)
        leaf.rotation_euler.z = angle


bouquet("Desk bouquet", (-2.29, -0.60, 0.815), 0.78)
bouquet("Window bouquet", (1.07, 2.22, 1.14), 0.95)
bouquet("Shelf bouquet", (-0.07, 2.07, 0.775), 0.85)
bouquet("Welcome bouquet", (1.06, -2.52, 0.07), 1.30)
# Oversized geometric botanical motifs woven into the rug, flush with its surface.
for x, y in ((-1.13, -1.67), (1.13, 0.40)):
    flower("Rug floral motif", (x, y, 0.036), 0.19, lilac)


# The accepted patients are the CC0 MakeHuman library. Never recreate the retired
# primitive prototype characters from this room authoring path.
patient_library = source / "patients.blend"
previous_report = json.loads((source / "inventory.json").read_text()) if (source / "inventory.json").exists() else {}
if patient_library.exists():
    with bpy.data.libraries.load(str(patient_library), link=False) as (available, loaded):
        loaded.objects = available.objects
    for obj in loaded.objects:
        if obj is not None:
            scene.collection.objects.link(obj)
            tag = obj.get("ScalpalCharacter")
            if tag in ("PatientFemale", "PatientMale"):
                groups[tag].append(obj)
            if tag == "PatientMale":
                obj.hide_render = True
                obj.hide_set(True)
    for name, value in previous_report.get("materials", {}).items():
        if name.startswith("Patient"):
            palette[name] = value
elif not args.office_only:
    raise FileNotFoundError("Missing patients.blend. First author the floral room with --office-only, then run doctor_office_humans.py with the pinned MPFB source and CC0 donor assets. No prototype people are generated.")


# Studio quality source preview, independent of the low-cost native lighting setup.
scene.world = bpy.data.worlds.new("Office warm daylight")
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.70, 0.78, 0.81, 1)
scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.30
for name, pos, target, energy, size, color in [
    ("Daylight", (1.4, 2.12, 2.2), (0, -1, 0.8), 300, 1.7, (0.84, 0.94, 1)),
    ("Ceiling softbox", (0, -0.3, 2.78), (0, 0, 0), 420, 4, (1, 0.91, 0.78)),
    ("Conversation fill", (-0.6, -2.7, 2.4), (0, 0, 1.1), 150, 2.1, (1, 0.94, 0.83)),
    ("Practical warm lamp", (-1.08, 1.25, 1.6), (-0.7, 2.3, 1.3), 18, 0.35, (1, 0.71, 0.43)),
]:
    data = bpy.data.lights.new(name, "AREA")
    data.energy, data.size, data.color = energy, size, color
    obj = bpy.data.objects.new(name, data)
    scene.collection.objects.link(obj)
    obj.location = pos
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()
camdata = bpy.data.cameras.new("Office preview camera")
camera = bpy.data.objects.new("Office preview camera", camdata)
scene.collection.objects.link(camera)
camera.location = (-1.28, -3.18, 1.87)
camera.rotation_euler = (Vector((0.28, 0.36, 1.08)) - camera.location).to_track_quat("-Z", "Y").to_euler()
camdata.lens = 24
scene.camera = camera
scene.render.engine = "CYCLES"
scene.cycles.samples = 32
scene.cycles.use_denoising = True
scene.render.resolution_x = 1600
scene.render.resolution_y = 1000
scene.render.resolution_percentage = 100
scene.view_settings.view_transform = "AgX"
scene.render.filepath = str(source / "previews/office-conversation.png")
scene["art_provenance"] = "Original floral office; CC0 MakeHuman/MPFB graphical assets for fictional patients. See ATTRIBUTION.md."
scene["coordinates"] = "Metric Blender +Z up, patient source faces -Y. Actual Unity import faces -Z; native measured whole-art yaw180 corrects to +Z."
bpy.ops.wm.save_as_mainfile(filepath=str(source / "doctor-office.blend"))
bpy.ops.render.render(write_still=True)


def add_lightmap_uvs(obj):
    # Unity bakes the office into lightmaps and reads the FBX's second UV set as uv2.
    # Unity's import-time unwrapper collapsed large bevelled faces (window frame) into slivers,
    # so author non-overlapping lightmap charts here; UVMap stays the render set.
    render_uv = obj.data.uv_layers[0]
    lightmap = obj.data.uv_layers.new(name="Lightmap")
    obj.data.uv_layers.active = lightmap
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.03, area_weight=0.0,
                             correct_aspect=True, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    render_uv.active_render = True
    obj.data.uv_layers.active = render_uv


def export(name, objects):
    # Merge export duplicates by material AND animation parent, preserving source parts.
    duplicates = []
    mapping = {}
    for old in objects:
        new = old.copy()
        if old.type == "MESH":
            new.data = old.data.copy()
        scene.collection.objects.link(new)
        new.hide_render = False
        new.hide_set(False)
        mapping[old] = new
        duplicates.append(new)
    for old, new in mapping.items():
        if old.parent in mapping:
            new.parent = mapping[old.parent]
        new.name = old.name + "_export"
    # Blender FBX -> Unity handedness preserves source horizontal Y as Unity Z.
    # Rotate the complete export 180 degrees to put the patient toward Unity +Z
    # and the clinician on the agreed positive-Z conversation side. Editable
    # source/preview keeps its intuitive Blender-facing -Y camera arrangement.
    export_turn = Matrix.Rotation(math.pi, 4, "Z")
    for obj in duplicates:
        if obj.parent not in duplicates:
            obj.matrix_world = export_turn @ obj.matrix_world
    bpy.context.view_layer.update()
    batches = {}
    for obj in duplicates:
        if obj.type == "MESH":
            batches.setdefault((obj.parent, obj.data.materials[0].name), []).append(obj)
    exported = [o for o in duplicates if o.type == "EMPTY"]
    for (ancestor, mat), parts in batches.items():
        bpy.ops.object.select_all(action="DESELECT")
        for o in parts:
            o.select_set(True)
        bpy.context.view_layer.objects.active = parts[0]
        if len(parts) > 1:
            bpy.ops.object.join()
        obj = bpy.context.object
        bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
        obj.name = ("Head_" if ancestor and ancestor.name.startswith("HeadPivot") else
                    "Jaw_" if ancestor and ancestor.name.startswith("JawPivot") else "Body_") + mat
        if name == "DoctorOffice":
            add_lightmap_uvs(obj)
        exported.append(obj)
    for old, new in mapping.items():
        if old.type == "EMPTY":
            new.name = old.name.replace(".001", "") + "_export"
    # Temporarily free canonical names so exported node names are stable across patients.
    rename = {o: o.name for o in scene.objects if o.type == "EMPTY" and o not in exported}
    for old in rename:
        old.name = old.name + "_source"
    for obj in exported:
        if obj.type == "EMPTY":
            obj.name = obj.name.split(".")[0].replace("_export", "")
    bpy.ops.object.select_all(action="DESELECT")
    for obj in exported:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = exported[0]
    path = runtime / (name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True, object_types={"MESH", "EMPTY"},
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True, global_scale=1,
        use_mesh_modifiers=True, add_leaf_bones=False, bake_anim=False, path_mode="STRIP")
    triangle_count = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in exported if o.type == "MESH")
    points = [o.matrix_world @ Vector(v) for o in exported if o.type == "MESH" for v in o.bound_box]
    bounds = [[min(p[i] for p in points) for i in range(3)], [max(p[i] for p in points) for i in range(3)]]
    result = {"triangles": triangle_count, "renderMeshes": len(batches),
        "materials": sorted({m for _, m in batches}), "boundsBlenderXYZMetres": bounds,
        "path": str(path.relative_to(root)), "bytes": path.stat().st_size,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        "nodes": sorted(o.name for o in exported)}
    for obj in exported:
        bpy.data.objects.remove(obj, do_unlink=True)
    for obj, original in rename.items():
        obj.name = original
    return result


inventory = {"DoctorOffice": export("DoctorOffice", groups["DoctorOffice"])}
if patient_library.exists():
    for name in ("PatientFemale", "PatientMale"):
        inventory[name] = previous_report["assets"][name]
assert inventory["DoctorOffice"]["triangles"] < 60000, "Office exceeds authored geometry budget"
for name in ("PatientFemale", "PatientMale"):
    if name in inventory:
        assert inventory[name]["triangles"] < 50000, "CC0 human exceeds geometry budget"
report = {"schema": "scalpal.doctor_office_art.v1", "assets": inventory, "materials": palette,
    "unityMaterials": [{"name": name, **value} for name, value in palette.items()],
    "source": "assets/environments/doctor-office/doctor-office.blend", "units": "metres",
    "exportAxes": {"BlenderUp": "+Z", "BlenderSourcePatientForward": "-Y", "BlenderExportPatientForward": "+Y", "exportRotationBlenderZDegrees": 180, "UnityUp": "+Y", "UnityPatientForward": "-Z before native scene correction", "UnityMeasuredSceneYawCorrectionDegrees": 180},
    "patientRootUnity": [0, 0, 0], "patientSeatHeightMetres": 0.52,
    "headPivotUnity": [0, 1.285, -0.018], "jawPivotUnity": [0, 1.255, 0.007],
    "clinicianEyeSuggestionUnity": [0, 1.2, 1.75],
    "casePresentation": {"PatientFemale": {"name": "Priya Ramaswamy", "age": 40, "patientId": "patient-demo-multi-source"},
                         "PatientMale": {"name": "Jonah Okoye", "age": 30, "patientId": "patient-demo-sparse"}},
    "provenance": "Original floral office with CC0 MakeHuman/MPFB humans, clothes, skin/hair/eye textures and default rigs. See ATTRIBUTION.md. No real patient likeness or identifying clinical data.",
    "limitations": ["Fictional clothed adult avatars derived from CC0 MakeHuman assets; no real-patient likeness or clinical anatomy claim.",
        "Genuine weighted head/jaw rig bones permit bounded motion; no calibrated viseme lip sync or realistic facial performance is claimed.",
        "Geometry/renderer budgets measured in Blender; native import and physical Quest frame time require separate checks.",
        "Fixtures are scenery, not a source of authored findings or tests; runtime must use the encounter engine."],
    "nativeLightingSuggestion": {"ambientRGB": [0.65, 0.72, 0.72], "directionalIntensity": 0.9,
        "pointLights": 0, "shadowSuggestion": "One soft directional shadow or baked lighting; measure on Quest."}}
(source / "inventory.json").write_text(json.dumps(report, indent=2) + "\n")
# Patient close-ups retain real editable source; only temporary render visibility changes.
for obj in groups["DoctorOffice"]:
    obj.hide_render = True
scene.render.resolution_x = 900
scene.render.resolution_y = 1100
camera.location = (0.72, -2.32, 1.41)
camera.rotation_euler = (Vector((0, -0.07, 0.78)) - camera.location).to_track_quat("-Z", "Y").to_euler()
camdata.lens = 52
for name in (n for n in ("PatientFemale", "PatientMale") if groups[n]):
    for kind in ("PatientFemale", "PatientMale"):
        for obj in groups[kind]:
            obj.hide_render = kind != name
    scene.render.filepath = str(source / "previews" / (name + ".png"))
    bpy.ops.render.render(write_still=True)
print("SCALPAL_OFFICE_ART", json.dumps(report))
report["fbxReimportChecks"] = verify_exports(report)
(source / "inventory.json").write_text(json.dumps(report, indent=2) + "\n")
