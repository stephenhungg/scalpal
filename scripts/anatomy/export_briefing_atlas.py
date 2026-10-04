"""Build the single-mesh pre-surgery briefing atlas (open appendectomy abdomen).

Run from the repository root (Blender 4.5 LTS, git lfs pull first):

    /path/to/Blender -b --factory-startup --python-exit-code 1 --python scripts/anatomy/export_briefing_atlas.py

Outputs:
    apps/quest/Assets/Scalpal/Briefing/Models/briefing_atlas.fbx
    apps/quest/Assets/Scalpal/Briefing/Resources/briefing_parts.json
    assets/anatomy/briefing-preview.png

One mesh object (`briefing_atlas`), triangles ordered by part, UV1 (Unity uv2)
x = part index. Geometry comes from the pinned Z-Anatomy originals plus the
authored exercise targets (cecum, terminal ileum). The subcutaneous fat and
parietal peritoneum have no source mesh; they are offset shells and are marked
"synthetic": true. The external oblique aponeurosis is split from the Z-Anatomy
external oblique by a positional rule ("derived").

Frames: Blender source is Z up, anterior -Y, patient right -X. The FBX is
written with forward -Z / up Y and the space transform baked into vertices, so
Unity sees (x, y, z)_unity = (-x, z, -y)_blender: Y up, anterior +Z, patient
right +X. Manifest coordinates are in that Unity mesh space.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
ORIGINALS = ROOT / "assets/anatomy/originals"
TARGETS_FBX = ROOT / "apps/quest/Assets/Scalpal/Anatomy/Models/exercise-targets.fbx"
OUT_FBX = ROOT / "apps/quest/Assets/Scalpal/Briefing/Models/briefing_atlas.fbx"
OUT_JSON = ROOT / "apps/quest/Assets/Scalpal/Briefing/Resources/briefing_parts.json"
OUT_PNG = ROOT / "assets/anatomy/briefing-preview.png"

Z_TOP, Z_BOTTOM = 1.16, 0.785   # crop: lower costal margin .. below pubis (source metres)
MAX_DIMENSION = 0.6             # final largest bounding-box side, metres
TRIANGLE_BUDGET = 200_000
FAT_OFFSET = 0.002              # fat shell this far inside skin (source metres)
PERITONEUM_OFFSET = 0.0015      # parietal peritoneum this far inside transversalis fascia

SKIN = (0.93, 0.74, 0.62); FAT = (0.98, 0.84, 0.36); APONEUROSIS = (0.93, 0.92, 0.87)
MUSCLE = (0.72, 0.19, 0.17); FASCIA = (0.90, 0.86, 0.80); PERITONEUM = (0.96, 0.70, 0.72)
BOWEL = (0.92, 0.64, 0.60); COLON = (0.86, 0.58, 0.55); ARTERY = (0.80, 0.08, 0.08)
VEIN = (0.15, 0.27, 0.72); BONE = (0.92, 0.88, 0.78); URETER = (0.95, 0.88, 0.60)
KIDNEY = (0.55, 0.18, 0.15); OMENTUM = (0.98, 0.86, 0.50)


def part(pid, name, system, layer, color, sources, target=4000, tissue="", crop=True, kind="mesh"):
    return dict(id=pid, name=name, system=system, layer=layer, color=color, sources=sources,
                target=target, tissue=tissue, crop=crop, kind=kind)


M, V, C, S, X = "muscular", "visceral", "cardiovascular", "skeletal", "exercise-targets"
SKIN_REGIONS = ["Epigastric region", "Hypochondriac region", "Umbilical region", "Lateral region of abdomen",
                "Hypogastric region", "Inguinal region", "Lumbar region", "Sacral region", "Vertebral region"]
both = lambda base: [base + ".r", base + ".l"]

# Order = triangle order = part index. Ids reuse anatomy-atlas.json stable ids where one exists.
PARTS = [
    part("skin", "Skin (abdominal wall)", "integumentary", 0, SKIN,
         [("surface", n) for r in SKIN_REGIONS for n in both(r)], 14000, "skin"),
    part("umbilicus", "Umbilicus", "integumentary", 0, (0.82, 0.58, 0.50),
         [("surface", n) for n in both("Umbilicus")], 600, "skin"),
    part("subcutaneous_fat", "Subcutaneous fat", "integumentary", 1, FAT, [], 14000, "fat", kind="fat"),
    part("external_oblique_aponeurosis_r", "External oblique aponeurosis (right)", "muscular", 2, APONEUROSIS,
         [(M, "External abdominal oblique muscle.r")], 5000, "fascia", kind="apo"),
    part("external_oblique_aponeurosis_l", "External oblique aponeurosis (left)", "muscular", 2, APONEUROSIS,
         [(M, "External abdominal oblique muscle.l")], 3000, "fascia", kind="apo"),
    part("muscular__external_abdominal_oblique_muscle_r", "External oblique muscle (right)", "muscular", 2, MUSCLE,
         [(M, "External abdominal oblique muscle.r")], 4000, "muscle", kind="eo"),
    part("muscular__external_abdominal_oblique_muscle_l", "External oblique muscle (left)", "muscular", 2, MUSCLE,
         [(M, "External abdominal oblique muscle.l")], 3000, "muscle", kind="eo"),
    part("muscular__inguinal_ligament_r", "Inguinal ligament (right)", "muscular", 2, APONEUROSIS,
         [(M, "Inguinal ligament.r")], 800, "fascia"),
    part("muscular__inguinal_ligament_l", "Inguinal ligament (left)", "muscular", 2, APONEUROSIS,
         [(M, "Inguinal ligament.l")], 600, "fascia"),
    part("muscular__linea_alba", "Linea alba", "muscular", 2, APONEUROSIS, [(M, "Linea alba")], 600, "fascia"),
    part("muscular__internal_abdominal_oblique_muscle_r", "Internal oblique muscle (right)", "muscular", 3,
         (0.70, 0.18, 0.17), [(M, "Internal abdominal oblique muscle.r")], 7000, "muscle"),
    part("muscular__internal_abdominal_oblique_muscle_l", "Internal oblique muscle (left)", "muscular", 3,
         (0.70, 0.18, 0.17), [(M, "Internal abdominal oblique muscle.l")], 4500, "muscle"),
    part("muscular__rectus_abdominis_muscle_r", "Rectus abdominis (right)", "muscular", 3, (0.76, 0.22, 0.20),
         [(M, "Rectus abdominis muscle.r")], 4000, "muscle"),
    part("muscular__rectus_abdominis_muscle_l", "Rectus abdominis (left)", "muscular", 3, (0.76, 0.22, 0.20),
         [(M, "Rectus abdominis muscle.l")], 4000, "muscle"),
    part("muscular__transversus_abdominis_muscle_r", "Transversus abdominis (right)", "muscular", 4,
         (0.64, 0.16, 0.16), [(M, "Transversus abdominis muscle.r")], 7000, "muscle"),
    part("muscular__transversus_abdominis_muscle_l", "Transversus abdominis (left)", "muscular", 4,
         (0.64, 0.16, 0.16), [(M, "Transversus abdominis muscle.l")], 4500, "muscle"),
    part("muscular__transversalis_fascia", "Transversalis fascia", "muscular", 5, FASCIA,
         [(M, "Transversalis fascia")], 6000, "peritoneum"),
    part("peritoneum", "Parietal peritoneum", "peritoneal", 6, PERITONEUM, [], 8000, "peritoneum", kind="peritoneum"),
    part("greater_omentum", "Greater omentum", "digestive", 7, OMENTUM, [(V, "Greater omentum")], 7000),
    part("cecum", "Cecum", "digestive", 8, COLON, [(X, "anat_cecum")], 99999, "cecum", crop=False),
    part("appendix", "Vermiform appendix", "digestive", 8, (0.90, 0.48, 0.46), [(V, "Vermiform appendix")],
         99999, "appendix", crop=False),
    part("mesoappendix", "Mesoappendix", "digestive", 8, (0.98, 0.80, 0.58), [(V, "Meso-appendix")],
         99999, "mesoappendix", crop=False),
    part("appendicular_artery", "Appendicular artery", "vascular", 8, ARTERY, [(C, "Appendicular artery")],
         99999, "appendicular_artery", crop=False),
    part("terminal_ileum", "Terminal ileum", "digestive", 8, BOWEL, [(X, "anat_terminal_ileum")], 99999,
         "terminal_ileum", crop=False),
    part("visceral__ascending_colon", "Ascending colon", "digestive", 8, COLON, [(V, "Ascending colon")], 9000),
    part("visceral__free_taenia", "Taenia coli (free taenia)", "digestive", 8, (0.96, 0.88, 0.80),
         [(V, "Free taenia")], 3500),
    part("visceral__jejunum", "Small bowel (jejunum and ileum)", "digestive", 8, BOWEL, [(V, "Jejunum")], 10000),
    part("transverse_colon", "Transverse colon", "digestive", 8, COLON, [(V, "Transverse colon")], 5000),
    part("descending_colon", "Descending colon", "digestive", 8, COLON, [(V, "Descending colon")], 5000),
    part("sigmoid_colon", "Sigmoid colon", "digestive", 8, COLON, [(V, "Sigmoid colon")], 3000),
    part("cardiovascular__ileocolic_artery", "Ileocolic artery", "vascular", 8, ARTERY,
         [(C, "Ileocolic artery"), (C, "Colic branch of ileocolic artery"), (C, "Ileal branch of ileocolic artery")], 1800),
    part("cardiovascular__superior_mesenteric_artery", "Superior mesenteric artery", "vascular", 8, ARTERY,
         [(C, "Superior mesenteric artery")], 3500),
    part("cardiovascular__superior_mesenteric_vein", "Superior mesenteric vein", "vascular", 8, VEIN,
         [(C, "Superior mesenteric vein")], 2500),
    part("right_ureter", "Right ureter", "urinary", 9, URETER, [(V, "Ureter.r")], 2500, "ureter"),
    part("left_ureter", "Left ureter", "urinary", 9, URETER, [(V, "Ureter.l")], 1500),
    part("right_kidney", "Right kidney", "urinary", 9, KIDNEY, [(V, "Kidney.r")], 3000),
    part("left_kidney", "Left kidney", "urinary", 9, KIDNEY, [(V, "Kidney.l")], 3000),
    part("cardiovascular__abdominal_aorta", "Abdominal aorta", "vascular", 9, ARTERY, [(C, "Abdominal aorta")], 1600),
    part("cardiovascular__inferior_vena_cava_abdominal_part", "Inferior vena cava", "vascular", 9, VEIN,
         [(C, "Inferior vena cava (abdominal part)")], 800),
    part("cardiovascular__common_iliac_artery_r", "Right common iliac artery", "vascular", 9, ARTERY,
         [(C, "Common iliac artery.r")], 800, "iliac_vessels"),
    part("cardiovascular__external_iliac_artery_r", "Right external iliac artery", "vascular", 9, ARTERY,
         [(C, "External iliac artery.r")], 800, "iliac_vessels"),
    part("cardiovascular__internal_iliac_artery_r", "Right internal iliac artery", "vascular", 9, ARTERY,
         [(C, "Internal iliac artery.r")], 600, "iliac_vessels"),
    part("cardiovascular__common_iliac_vein_r", "Right common iliac vein", "vascular", 9, VEIN,
         [(C, "Common iliac vein.r")], 600, "iliac_vessels"),
    part("cardiovascular__external_iliac_vein_r", "Right external iliac vein", "vascular", 9, VEIN,
         [(C, "External iliac vein.r")], 600, "iliac_vessels"),
    part("left_iliac_arteries", "Left iliac arteries", "vascular", 9, ARTERY,
         [(C, "Common iliac artery.l"), (C, "External iliac artery.l"), (C, "Internal iliac artery.l")], 1200),
    part("left_iliac_veins", "Left iliac veins", "vascular", 9, VEIN,
         [(C, "Common iliac vein.l"), (C, "External iliac vein.l"), (C, "Internal iliac vein.l")], 1000),
    part("muscular__psoas_major_r", "Psoas major (right)", "muscular", 9, MUSCLE, [(M, "Psoas major.r")], 3500),
    part("muscular__psoas_major_l", "Psoas major (left)", "muscular", 9, MUSCLE, [(M, "Psoas major.l")], 3000),
    part("muscular__iliacus_muscle_r", "Iliacus (right)", "muscular", 9, MUSCLE, [(M, "Iliacus muscle.r")], 3000),
    part("muscular__iliacus_muscle_l", "Iliacus (left)", "muscular", 9, MUSCLE, [(M, "Iliacus muscle.l")], 2500),
    part("skeletal__hip_bone_r", "Right hip bone (ASIS)", "skeletal", 10, BONE, [(S, "Hip bone.r")], 7000),
    part("skeletal__hip_bone_l", "Left hip bone", "skeletal", 10, BONE, [(S, "Hip bone.l")], 6000),
    part("skeletal__sacrum", "Sacrum", "skeletal", 10, BONE, [(S, "Sacrum")], 4000),
    part("lumbar_vertebrae", "Lumbar vertebrae L1-L5", "skeletal", 10, BONE,
         [(S, "Vertebra L%d" % i) for i in range(1, 6)], 9000),
    part("lower_ribs_r", "Lower ribs (right)", "skeletal", 10, BONE,
         [(S, n) for n in ("Tenth rib.r", "Eleventh rib.r", "Twelfth rib.r", "Costal cartilage of ninth rib.r",
                           "Costal cartilage of tenth rib.r", "Ninth rib.r")], 4000),
    part("lower_ribs_l", "Lower ribs (left)", "skeletal", 10, BONE,
         [(S, n) for n in ("Tenth rib.l", "Eleventh rib.l", "Twelfth rib.l", "Costal cartilage of ninth rib.l",
                           "Costal cartilage of tenth rib.l", "Ninth rib.l")], 3500),
]

# Suggested briefing beats for services/preop/src/catalog/open-appendectomy.ts steps.
# peelThroughLayer: hide every part with layer <= this value (-1 = nothing peeled).
STEPS = [
    ("mark_incision", -1, ["umbilicus", "skeletal__hip_bone_r"]),
    ("incise_skin", -1, ["skin", "subcutaneous_fat"]),
    ("open_fascia", 1, ["external_oblique_aponeurosis_r"]),
    ("split_muscle", 2, ["muscular__internal_abdominal_oblique_muscle_r", "muscular__transversus_abdominis_muscle_r"]),
    ("open_peritoneum", 5, ["peritoneum"]),
    ("deliver_appendix", 7, ["cecum", "appendix"]),
    ("divide_mesoappendix", 7, ["mesoappendix", "appendicular_artery"]),
    ("ligate_base", 7, ["appendix", "cecum"]),
    ("inspect_clean", 7, ["mesoappendix", "appendix"]),
    ("close", -1, []),
]
REQUIRED = ["skin", "subcutaneous_fat", "external_oblique_aponeurosis_r", "muscular__internal_abdominal_oblique_muscle_r",
            "muscular__transversus_abdominis_muscle_r", "peritoneum", "cecum", "appendix", "mesoappendix",
            "appendicular_artery", "terminal_ileum", "visceral__ascending_colon", "visceral__jejunum",
            "cardiovascular__common_iliac_artery_r", "cardiovascular__external_iliac_artery_r", "right_ureter",
            "skeletal__hip_bone_r", "umbilicus"]


def to_unity(v):
    return [-v[0], v[2], -v[1]]


def clear_scene():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)


def load_sources():
    """Import each needed source FBX once; key objects by (system, source name)."""
    needed = sorted({s for p in PARTS for s, _ in p["sources"]})
    found = {}
    for system in needed:
        path = TARGETS_FBX if system == X else ORIGINALS / (system + ".fbx")
        before = set(bpy.data.objects)
        bpy.ops.import_scene.fbx(filepath=str(path))
        for o in set(bpy.data.objects) - before:
            if o.type == "MESH":
                found[(system, o.name)] = o
        for o in set(bpy.data.objects) - before:   # free names before the next import
            o.name = system + "::" + o.name
    return found


def bm_from_objects(objects):
    bm = bmesh.new()
    for o in objects:
        mesh = o.data.copy()
        world = o.matrix_world.copy()
        mesh.transform(world)
        if world.determinant() < 0:
            mesh.flip_normals()
        bm.from_mesh(mesh)
        bpy.data.meshes.remove(mesh)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    return bm


def crop(bm):
    for co, no in (((0, 0, Z_TOP), (0, 0, 1)), ((0, 0, Z_BOTTOM), (0, 0, -1))):
        geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
        bmesh.ops.bisect_plane(bm, geom=geom, plane_co=co, plane_no=no, clear_outer=True, dist=1e-6)
    bmesh.ops.triangulate(bm, faces=bm.faces[:])


def offset_inward(bm, distance):
    """Move every vertex horizontally toward the body's vertical axis.

    The merged surface regions are open, patchy shells whose normals are not
    reliably consistent, so a radial inset is more robust than a normal offset.
    """
    ys = [v.co.y for v in bm.verts]
    axis_y = (min(ys) + max(ys)) / 2
    for v in bm.verts:
        radial = Vector((v.co.x, v.co.y - axis_y, 0))
        if radial.length > 1e-6:
            v.co -= radial.normalized() * distance
    bm.normal_update()


def decimate(bm, target):
    tris = len(bm.faces)
    if tris <= target:
        return bm
    mesh = bpy.data.meshes.new("tmp")
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new("tmp", mesh)
    bpy.context.scene.collection.objects.link(obj)
    mod = obj.modifiers.new("dec", "DECIMATE")
    mod.ratio = target / tris
    mod.use_collapse_triangulate = True
    deps = bpy.context.evaluated_depsgraph_get()
    out = bmesh.new()
    out.from_mesh(obj.evaluated_get(deps).to_mesh())
    bpy.data.objects.remove(obj)
    bpy.data.meshes.remove(mesh)
    bmesh.ops.triangulate(out, faces=out.faces[:])
    return out


def drop_degenerate(bm):
    bad = [f for f in bm.faces if f.calc_area() < 1e-12]
    bmesh.ops.delete(bm, geom=bad, context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")


def asis(bm):
    """Anterior superior iliac spine: most anterior hip-bone point above the pubis."""
    candidates = [v.co for v in bm.verts if v.co.z > 0.94]
    return min(candidates, key=lambda c: c.y).copy()


def split_external_oblique(bm, asis_z, keep_aponeurosis):
    """Aponeurosis: medial to the rectus lateral border or below the ASIS level."""
    rectus_edge = 0.085
    drop = []
    for f in bm.faces:
        c = f.calc_center_median()
        is_apo = abs(c.x) < rectus_edge or c.z < asis_z
        if is_apo != keep_aponeurosis:
            drop.append(f)
    bmesh.ops.delete(bm, geom=drop, context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")


def build_parts(sources):
    built = {}
    hip = bm_from_objects([sources[(S, "Hip bone.r")]])
    asis_r = asis(hip)
    hip.free()
    hip_l = bm_from_objects([sources[(S, "Hip bone.l")]])
    asis_l = asis(hip_l)
    hip_l.free()
    for p in PARTS:
        if p["kind"] in ("fat", "peritoneum"):
            base = built["skin"] if p["kind"] == "fat" else built["muscular__transversalis_fascia"]
            bm = base.copy()
            bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=3e-4)
            offset_inward(bm, FAT_OFFSET if p["kind"] == "fat" else PERITONEUM_OFFSET)
        else:
            objs = [sources[s] for s in p["sources"]]
            bm = bm_from_objects(objs)
            if p["id"] == "skin":
                bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=3e-4)
                # Surface regions are coarse; one smooth subdivision gives clean shells.
                bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=1, use_grid_fill=True, smooth=0.5)
                bmesh.ops.triangulate(bm, faces=bm.faces[:])
            if p["kind"] in ("apo", "eo"):
                split_external_oblique(bm, (asis_r if p["id"].endswith("_r") else asis_l).z, p["kind"] == "apo")
            if p["crop"]:
                crop(bm)
        bm = decimate(bm, p["target"])
        drop_degenerate(bm)
        if not bm.faces:
            raise ValueError("Part has no geometry after cropping: " + p["id"])
        built[p["id"]] = bm
    return built, asis_r, asis_l


def main():
    clear_scene()
    sources = load_sources()
    missing = [s for p in PARTS for s in p["sources"] if s not in sources]
    if missing:
        raise ValueError("Missing source meshes: %s" % missing)
    built, asis_r, asis_l = build_parts(sources)

    total = sum(len(bm.faces) for bm in built.values())
    if total > TRIANGLE_BUDGET:
        raise ValueError("Triangle budget exceeded: %d" % total)

    lo = Vector([min(v.co[a] for bm in built.values() for v in bm.verts) for a in range(3)])
    hi = Vector([max(v.co[a] for bm in built.values() for v in bm.verts) for a in range(3)])
    center = (lo + hi) / 2
    scale = MAX_DIMENSION / max(hi - lo)
    xf = lambda co: (co - center) * scale

    verts, faces, part_of_face, parts_json = [], [], [], []
    for index, p in enumerate(PARTS):
        bm = built[p["id"]]
        bm.verts.index_update()
        base = len(verts)
        pts = [xf(v.co) for v in bm.verts]
        verts.extend(pts)
        tri_start = len(faces)
        for f in bm.faces:
            faces.append(tuple(base + v.index for v in f.verts))
            part_of_face.append(index)
        u = [to_unity(c) for c in pts]
        mn = [min(c[a] for c in u) for a in range(3)]
        mx = [max(c[a] for c in u) for a in range(3)]
        entry = {"index": index, "id": p["id"], "name": p["name"], "system": p["system"], "layer": p["layer"],
                 "center": [round((mn[a] + mx[a]) / 2, 5) for a in range(3)],
                 "min": [round(x, 5) for x in mn], "max": [round(x, 5) for x in mx],
                 "triStart": tri_start, "triCount": len(faces) - tri_start,
                 "color": [round(c, 3) for c in p["color"]],
                 "synthetic": p["kind"] in ("fat", "peritoneum"),
                 "tissueId": p["tissue"],
                 "source": ["%s/%s" % s for s in p["sources"]] or
                           ["synthetic offset of " + ("skin" if p["kind"] == "fat" else "transversalis fascia")]}
        if p["kind"] == "apo":
            entry["derived"] = "split from Z-Anatomy external oblique: medial to rectus border or below ASIS"
        if p["kind"] == "eo":
            entry["derived"] = "fleshy remainder of Z-Anatomy external oblique after aponeurosis split"
        parts_json.append(entry)
        bm.free()

    mesh = bpy.data.meshes.new("briefing_atlas")
    mesh.from_pydata([tuple(v) for v in verts], [], faces)
    mesh.validate(clean_customdata=False)
    if len(mesh.polygons) != len(faces):
        raise ValueError("validate() removed faces; triangle ranges would shift")
    uv0 = mesh.uv_layers.new(name="UVMap")
    uv1 = mesh.uv_layers.new(name="PartIndex")
    loop_part = np.repeat(np.array(part_of_face, dtype=np.float32), 3)
    zeros = np.zeros(len(mesh.loops) * 2, dtype=np.float32)
    uv0.data.foreach_set("uv", zeros)
    packed = np.zeros(len(mesh.loops) * 2, dtype=np.float32)
    packed[0::2] = loop_part
    uv1.data.foreach_set("uv", packed)
    mesh.shade_smooth()
    material = bpy.data.materials.new("briefing_atlas")
    mesh.materials.append(material)

    clear_scene()
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    obj = bpy.data.objects.new("briefing_atlas", mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    OUT_FBX.parent.mkdir(parents=True, exist_ok=True)
    OUT_JSON.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(filepath=str(OUT_FBX), use_selection=True, object_types={"MESH"},
                             axis_forward="-Z", axis_up="Y", bake_space_transform=True,
                             apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
                             mesh_smooth_type="OFF", use_mesh_modifiers=False, colors_type="NONE",
                             bake_anim=False, add_leaf_bones=False, path_mode="STRIP")

    umb = next(e for e in parts_json if e["id"] == "umbilicus")["center"]
    asis_u = to_unity(xf(asis_r))
    landmarks = {"asis_r": [round(x, 5) for x in asis_u], "asis_l": [round(x, 5) for x in to_unity(xf(asis_l))],
                 "umbilicus": umb,
                 "mcburney_point": [round(asis_u[a] + (umb[a] - asis_u[a]) / 3, 5) for a in range(3)]}
    doc = {"version": 1,
           "frame": {"units": "meters", "up": "+Y", "anterior": "+Z", "patientRight": "+X",
                     "space": "Unity mesh space after FBX import (left-handed)",
                     "scaleFromSource": round(scale, 6),
                     "sourceCenterBlender": [round(c, 6) for c in center],
                     "crop": "Z-Anatomy source z %.3f..%.3f m (lower costal margin to below pubis)" % (Z_BOTTOM, Z_TOP)},
           "landmarks": landmarks,
           "steps": [{"id": s, "peelThroughLayer": layer, "focus": focus} for s, layer, focus in STEPS],
           "attribution": "Z-Anatomy (Lluis Vinent), BodyParts3D (c) DBCLS, CC BY-SA 4.0; see assets/anatomy/attribution/",
           "parts": parts_json}
    OUT_JSON.write_text(json.dumps(doc, indent=1) + "\n")
    print("EXPORTED", len(parts_json), "parts", len(faces), "triangles", flush=True)

    validate()
    render_preview()


def validate():
    doc = json.loads(OUT_JSON.read_text())
    parts = doc["parts"]
    clear_scene()
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    bpy.ops.import_scene.fbx(filepath=str(OUT_FBX))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    errors = []
    if len(meshes) != 1 or meshes[0].name != "briefing_atlas":
        errors.append("expected one mesh named briefing_atlas, got %s" % [o.name for o in meshes])
    obj = meshes[0]
    me = obj.data
    tri_total = sum(len(p.vertices) - 2 for p in me.polygons)
    if any(len(p.vertices) != 3 for p in me.polygons):
        errors.append("non-triangle polygons after import")
    if len(me.uv_layers) < 2:
        errors.append("missing second UV channel")
    uv = np.zeros(len(me.loops) * 2, dtype=np.float32)
    me.uv_layers[1].data.foreach_get("uv", uv)
    loop_part = np.rint(uv[0::2]).astype(int)
    face_part = loop_part[[p.loop_start for p in me.polygons]]
    if np.any(np.abs(uv[1::2]) > 1e-6):
        errors.append("uv2.y is not zero")
    distinct = sorted(set(face_part.tolist()))
    if distinct != list(range(len(parts))):
        errors.append("distinct uv2 indices %d != json parts %d" % (len(distinct), len(parts)))
    for p in me.polygons:   # every vertex of a triangle carries the same part
        ids = set(loop_part[p.loop_start:p.loop_start + 3].tolist())
        if len(ids) != 1:
            errors.append("mixed part ids in one triangle")
            break
    cursor = 0
    for e in parts:
        if e["triStart"] != cursor:
            errors.append("gap/overlap before part %d" % e["index"])
        cursor = e["triStart"] + e["triCount"]
        if not np.all(face_part[e["triStart"]:cursor] == e["index"]):
            errors.append("triangle range of part %d has other part ids" % e["index"])
    if cursor != tri_total:
        errors.append("ranges cover %d of %d triangles" % (cursor, tri_total))
    if tri_total > TRIANGLE_BUDGET:
        errors.append("over triangle budget: %d" % tri_total)
    ids = {e["id"] for e in parts}
    step_ids = {f for s in doc["steps"] for f in s["focus"]}
    for rid in sorted(set(REQUIRED) | step_ids):
        if rid not in ids:
            errors.append("missing required part " + rid)
    if len(ids) != len(parts):
        errors.append("duplicate part ids")
    # Bounds: re-imported Blender coordinates converted to Unity space must match the manifest.
    co = np.zeros(len(me.vertices) * 3, dtype=np.float32)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3) @ np.array(obj.matrix_world.to_3x3()).T + np.array(obj.matrix_world.translation)
    unity = np.stack([-co[:, 0], co[:, 2], -co[:, 1]], axis=1)
    tris = np.array([p.vertices[:] for p in me.polygons])
    for e in parts:
        vids = np.unique(tris[e["triStart"]:e["triStart"] + e["triCount"]])
        mn, mx = unity[vids].min(axis=0), unity[vids].max(axis=0)
        if np.abs(mn - e["min"]).max() > 1e-3 or np.abs(mx - e["max"]).max() > 1e-3:
            errors.append("bounds mismatch for %s" % e["id"])
        if np.abs(np.concatenate([mn, mx])).max() > 0.4:
            errors.append("part %s outside 0.8 m cube" % e["id"])
    lo, hi = unity.min(axis=0), unity.max(axis=0)
    if np.abs((lo + hi) / 2).max() > 1e-3:
        errors.append("model not centered: %s" % ((lo + hi) / 2))
    print("VALIDATION parts=%d distinct_uv2=%d triangles=%d extent=%s center=%s" % (
        len(parts), len(distinct), tri_total, np.round(hi - lo, 4).tolist(), np.round((lo + hi) / 2, 5).tolist()))
    if errors:
        for e in errors:
            print("VALIDATION ERROR:", e)
        raise SystemExit(1)
    print("VALIDATION OK", flush=True)


def render_preview():
    """Three panels from the re-imported FBX: full model, wall layers peeled, viscera with focus organs."""
    doc = json.loads(OUT_JSON.read_text())
    parts = doc["parts"]
    obj = bpy.data.objects["briefing_atlas"]
    me = obj.data
    colors = np.array([e["color"] + [1.0] for e in parts], dtype=np.float32)
    uv = np.zeros(len(me.loops) * 2, dtype=np.float32)
    me.uv_layers[1].data.foreach_get("uv", uv)
    loop_part = np.rint(uv[0::2]).astype(int)
    attr = me.color_attributes.new("part", "FLOAT_COLOR", "CORNER")
    attr.data.foreach_set("color", colors[loop_part].ravel())
    me.color_attributes.active_color = attr
    me.color_attributes.render_color_index = me.color_attributes.active_color_index
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "VERTEX"
    scene.display.shading.show_cavity = True
    scene.render.resolution_x, scene.render.resolution_y = 700, 800
    scene.render.film_transparent = False
    scene.world = scene.world or bpy.data.worlds.new("w")
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = 0.68
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    scene.camera = cam
    # Anterior is -Y in Blender. Slight oblique from the patient's right.
    cam.location = Vector((-0.35, -1.2, 0.12))
    cam.rotation_euler = (Vector((0, 0, 0)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    panels = []
    face_layer = np.array([parts[i]["layer"] for i in loop_part[[p.loop_start for p in me.polygons]]])
    for peel in (-1, 2, 7):
        keep = face_layer > peel
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.faces.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[bm.faces[i] for i in np.nonzero(~keep)[0]], context="FACES")
        view = bpy.data.meshes.new("view")
        bm.to_mesh(view)
        bm.free()
        view.color_attributes.active_color = view.color_attributes["part"]
        view.color_attributes.render_color_index = view.color_attributes.active_color_index
        vo = bpy.data.objects.new("view", view)
        vo.matrix_world = obj.matrix_world   # importer puts the Y-up to Z-up conversion on the object
        scene.collection.objects.link(vo)
        obj.hide_render = True
        path = OUT_PNG.with_name("_briefing_panel_%d.png" % (peel + 1))
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        img = bpy.data.images.load(str(path))
        panels.append(np.array(img.pixels[:], dtype=np.float32).reshape(img.size[1], img.size[0], 4))
        bpy.data.images.remove(img)
        path.unlink()
        bpy.data.objects.remove(vo)
        bpy.data.meshes.remove(view)
    combined = np.concatenate(panels, axis=1)
    out = bpy.data.images.new("preview", combined.shape[1], combined.shape[0], alpha=True)
    out.pixels.foreach_set(combined.ravel())
    out.filepath_raw = str(OUT_PNG)
    out.file_format = "PNG"
    out.save()
    print("PREVIEW", OUT_PNG, flush=True)


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        import traceback
        traceback.print_exc()
        sys.exit(1)
