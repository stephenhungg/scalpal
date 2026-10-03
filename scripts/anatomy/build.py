"""Run inside Blender: --background --factory-startup --python scripts/anatomy/build.py."""
import hashlib
import json
import re
from pathlib import Path

import bpy
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "apps/quest/Assets/Scalpal/Anatomy"
SOURCES = json.loads((ROOT / "assets/anatomy/sources.json").read_text())["sources"]

# Explicit source-name equivalences only. Missing structures remain missing.
ALIASES = {
    "liver": "liver", "gallbladder": "gallbladder", "cystic duct": "cystic_duct",
    "cystic artery": "cystic_artery", "common hepatic duct": "common_hepatic_duct",
    "common bile duct": "common_bile_duct", "right hepatic artery": "right_hepatic_artery",
    "stomach": "stomach", "duodenum": "duodenum", "pancreas": "pancreas",
    "transverse colon": "transverse_colon", "caecum": "cecum", "cecum": "cecum",
    "vermiform appendix": "appendix", "appendix": "appendix", "mesoappendix": "mesoappendix",
    "meso-appendix": "mesoappendix",
    "appendicular artery": "appendicular_artery", "ureter.r": "right_ureter",
    "ureter.l": "left_ureter", "descending colon": "descending_colon",
    "sigmoid colon": "sigmoid_colon", "rectum": "rectum", "urinary bladder": "urinary_bladder",
    "kidney.r": "right_kidney", "kidney.l": "left_kidney", "heart": "heart",
    "greater omentum": "greater_omentum", "inferior mesenteric artery": "inferior_mesenteric_artery",
    "umbilicus": "umbilicus", "sigmoid mesocolon": "sigmoid_mesocolon",
}


def slug(s):
    return re.sub(r"[^a-z0-9]+", "_", s.lower()).strip("_")


def triangles(mesh):
    mesh.calc_loop_triangles()
    return len(mesh.loop_triangles)


def main():
    (OUTPUT / "Models").mkdir(parents=True, exist_ok=True)
    (OUTPUT / "Resources").mkdir(exist_ok=True)
    doc = {"schemaVersion": 1, "systems": [], "parts": [], "excludedGuides": [], "frame": {
        "units": "meters", "up": "+Y in Unity", "origin": "source atlas origin, NOT umbilicus",
        "registration": "unverified; preview only until fitted and validated",
        "detailModels": "independent HRA source frames; never composite over Z-Anatomy automatically"}}
    all_ids = set()
    cache = ROOT / "assets/anatomy/build"
    cache.mkdir(exist_ok=True)
    build_hash = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    for source in SOURCES:
        path = ROOT / "assets/anatomy/originals" / source["filename"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != source["sha256"]:
            raise ValueError(f"Source checksum mismatch: {path}")
        cached_path = cache / (source["id"] + ".json")
        if cached_path.exists():
            previous = json.loads(cached_path.read_text())
            exported = ROOT / "apps/quest" / previous["system"]["assetPath"]
            if (previous["buildHash"] == build_hash and previous["sourceHash"] == source["sha256"]
                    and exported.exists() and hashlib.sha256(exported.read_bytes()).hexdigest() == previous["system"]["sha256"]):
                doc["systems"].append(previous["system"])
                doc["parts"].extend(previous["parts"])
                doc["excludedGuides"].extend(previous.get("excludedGuides", []))
                all_ids.update(p["stableId"] for p in previous["parts"])
                print("REUSE", source["id"], flush=True)
                continue
        bpy.ops.object.select_all(action="SELECT")
        bpy.ops.object.delete(use_global=False)
        for data in list(bpy.data.meshes):
            if data.users == 0:
                bpy.data.meshes.remove(data)
        if path.suffix == ".fbx":
            bpy.ops.import_scene.fbx(filepath=str(path))
        else:
            bpy.ops.import_scene.gltf(filepath=str(path))
        # Z-Anatomy .i/.j helper meshes include thin cuboid label pointers and
        # region guides. Keep their labels in metadata, not the tissue display.
        # Originals remain available in the pinned source cache.
        meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
        guides = [o for o in meshes if not source["id"].startswith("detail-")
                  and o.name.lower().endswith((".i", ".j"))]
        doc["excludedGuides"].extend({"system": source["id"], "sourceName": o.name,
            "reason": "source marker/region guide suffix .i/.j"} for o in sorted(guides, key=lambda o: o.name))
        guide_names = {o.name for o in guides}
        objects = sorted([o for o in meshes if o.name not in guide_names and triangles(o.data) > 0], key=lambda o: o.name)
        if not objects:
            raise ValueError(f"No mesh geometry: {path}")
        original = sum(triangles(o.data) for o in objects)
        detail = source["id"].startswith("detail-")
        # Target, not a hard limit: tiny named structures must survive decimation.
        target = 150000 if detail else 60000
        ratio = min(1.0, target / max(1, original))
        world_matrices = {o.name: o.matrix_world.copy() for o in objects}
        bpy.ops.object.select_all(action="DESELECT")
        for o in objects:
            source_name = o.name
            world = world_matrices[source_name]
            o.parent = None
            o.data = o.data.copy()
            o.data.transform(world)
            # Once reflection is baked into vertices, preserve outward-facing winding.
            if world.determinant() < 0:
                o.data.flip_normals()
            o.matrix_world = Matrix.Identity(4)
            bpy.context.view_layer.objects.active = o
            o.select_set(True)
            before = triangles(o.data)
            catalog = "" if detail else ALIASES.get(source_name.lower(), "")
            if before > 32 and ratio < 1 and not catalog:
                mod = o.modifiers.new("Atlas display simplification", "DECIMATE")
                mod.ratio = max(ratio, 24 / before)
                mod.use_collapse_triangulate = True
                bpy.ops.object.modifier_apply(modifier=mod.name)
            # Collapse can create duplicate faces. FBX import validates these away;
            # validate here so the recorded counts describe the exported surface.
            o.data.validate(clean_customdata=False)
            stable = catalog or source["id"].replace("-", "_") + "__" + slug(source_name)
            if stable in all_ids and not catalog:
                stable += "_" + hashlib.sha256(source_name.encode()).hexdigest()[:8]
            if stable in all_ids:
                raise ValueError(f"Duplicate stable ID {stable}; resolve source mapping explicitly")
            all_ids.add(stable)
            o.name = "anat_" + catalog if catalog else "atlas_" + stable
            o.data.name = o.name + "_mesh"
            # Materials are generated by Unity; avoid importing hundreds of source variants.
            o.data.materials.clear()
            for p in o.data.polygons:
                p.use_smooth = True
            doc["parts"].append({"stableId": stable, "displayName": source_name,
                "system": source["id"], "objectName": o.name, "catalogId": catalog,
                "sourceTriangles": before, "triangles": triangles(o.data),
                "reflectionCorrected": world.determinant() < 0})
            o.select_set(False)
        bpy.ops.object.select_all(action="DESELECT")
        for o in objects:
            o.select_set(True)
        out = OUTPUT / "Models" / (source["id"] + ".fbx")
        bpy.ops.export_scene.fbx(filepath=str(out), use_selection=True, object_types={"MESH"},
            axis_forward="-Z", axis_up="Y", apply_unit_scale=True, bake_anim=False,
            use_mesh_modifiers=True, add_leaf_bones=False)
        after = sum(triangles(o.data) for o in objects)
        bounds = [[min((o.matrix_world @ Vector(c))[a] for o in objects for c in o.bound_box),
                   max((o.matrix_world @ Vector(c))[a] for o in objects for c in o.bound_box)] for a in range(3)]
        doc["systems"].append({"id": source["id"], "group": "detail" if detail else "body",
            "assetPath": "Assets/Scalpal/Anatomy/Models/" + out.name,
            "sourceTriangles": original, "triangles": after, "meshCount": len(objects),
            "sourceBoundsBlenderXYZ": bounds, "sha256": hashlib.sha256(out.read_bytes()).hexdigest()})
        cached_path.write_text(json.dumps({"buildHash": build_hash, "sourceHash": source["sha256"],
            "system": doc["systems"][-1], "parts": [p for p in doc["parts"] if p["system"] == source["id"]],
            "excludedGuides": [p for p in doc["excludedGuides"] if p["system"] == source["id"]]}))
        print(f"ATLAS {source['id']}: {len(objects)} meshes, {original:,} -> {after:,} triangles", flush=True)
    bundle = json.loads((ROOT / "apps/quest/Assets/Scalpal/Exercises/Resources/scalpal_bundle.json").read_text())
    mapped = {p["catalogId"] for p in doc["parts"] if p["catalogId"]}
    doc["unmappedCatalogIds"] = sorted(a["id"] for a in bundle["anatomy"] if a["id"] not in mapped)
    (OUTPUT / "Resources/anatomy-atlas.json").write_text(json.dumps(doc, indent=2) + "\n")
    print("UNMAPPED", doc["unmappedCatalogIds"], flush=True)


if __name__ == "__main__":
    main()
