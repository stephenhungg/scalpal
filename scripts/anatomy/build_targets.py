"""Blender entry point: author explicit teaching targets in the original atlas frame.

These additions are schematic teaching geometry, not segmented patient anatomy.
Upstream files stay untouched. Run after build.py and merge the emitted manifest.
"""
import hashlib
import json
from pathlib import Path
import bpy
import bmesh
from mathutils import Vector, Matrix

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / 'apps/quest/Assets/Scalpal/Anatomy'
SYSTEM = 'exercise-targets'


def bounds(objects):
    return [[min((o.matrix_world @ Vector(c))[a] for o in objects for c in (v.co for v in o.data.vertices)),
             max((o.matrix_world @ Vector(c))[a] for o in objects for c in (v.co for v in o.data.vertices))] for a in range(3)]


def center(o):
    return Vector([(a+b)/2 for a,b in bounds([o])])


def tube(name, points, radius):
    curve = bpy.data.curves.new(name, 'CURVE')
    curve.dimensions = '3D'
    curve.resolution_u = 8
    curve.bevel_depth = radius
    curve.bevel_resolution = 3
    curve.use_fill_caps = True
    spline = curve.splines.new('BEZIER')
    spline.bezier_points.add(len(points)-1)
    for p, co in zip(spline.bezier_points, points):
        p.co = co
        p.handle_left_type = 'AUTO'
        p.handle_right_type = 'AUTO'
    o = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(o)
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.convert(target='MESH')
    result = bpy.context.object
    # Curve cap vertices are coincident but disconnected after conversion. Weld
    # them so the authored target is a closed collider surface after FBX import.
    bm = bmesh.new()
    bm.from_mesh(result.data)
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=0.000001)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    if any(not e.is_manifold for e in bm.edges):
        raise ValueError('Authored tube must be watertight: '+name)
    bm.to_mesh(result.data)
    bm.free()
    return result


def ellipsoid(name, location, scale):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=32, ring_count=20, location=location)
    o = bpy.context.object
    o.name = name
    o.scale = scale
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return o


def duplicate_group(name, objects):
    bpy.ops.object.select_all(action='DESELECT')
    copies = []
    for source in objects:
        o = source.copy()
        o.data = source.data.copy()
        bpy.context.collection.objects.link(o)
        copies.append(o)
        o.select_set(True)
    bpy.context.view_layer.objects.active = copies[0]
    bpy.ops.object.join()
    o = bpy.context.object
    o.name = name
    return o


def main():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for system in ('visceral', 'cardiovascular'):
        bpy.ops.import_scene.fbx(filepath=str(BASE / 'Models' / (system+'.fbx')))
    source = {o.name:o for o in bpy.context.scene.objects if o.type == 'MESH'}
    authored = []
    parts = []
    def add(id, o, landmarks, provenance='authored schematic teaching geometry', radius=0, points=None):
        o.name = 'anat_'+id
        o.data.name = o.name+'_mesh'
        world = o.matrix_world.copy()
        o.data.transform(world)
        o.parent = None
        o.matrix_world = Matrix.Identity(4)
        o.data.materials.clear()
        for p in o.data.polygons: p.use_smooth = True
        o.data.calc_loop_triangles()
        n = len(o.data.loop_triangles)
        part = dict(stableId=id, catalogId=id, displayName=id.replace('_',' ').title(),
            objectName=o.name, system=SYSTEM, sourceTriangles=n, triangles=n,
            reflectionCorrected=False, provenance=provenance, sourceLandmarks=landmarks,
            sourceBoundsBlenderXYZ=bounds([o]), colliderRadiusMeters=radius,
            anatomicalValidation='unreviewed generic teaching approximation; not patient anatomy')
        if points: part['centerlineBlenderXYZ']=[list(p) for p in points]
        parts.append(part)
        authored.append(o)
        return o
    asc = bounds([source['atlas_visceral__ascending_colon']])
    c = Vector([(asc[0][0]+asc[0][1])/2, (asc[1][0]+asc[1][1])/2, asc[2][0]+.018])
    add('cecum', ellipsoid('cecum',c,(.026,.025,.033)), ['atlas_visceral__ascending_colon','anat_appendix'])
    ileum_path = [c+Vector((.006,0,.009)),c+Vector((.035,-.004,.008)),c+Vector((.067,-.009,.028)),c+Vector((.072,-.006,.065))]
    ileum = add('terminal_ileum',tube('terminal_ileum',ileum_path,.010),['atlas_visceral__ascending_colon','anat_appendix'], radius=.010, points=ileum_path)
    gall = bounds([source['anat_gallbladder']])
    bile = bounds([source['atlas_visceral__bile_duct']])
    # The source bile duct is ambiguous. It is only a positional landmark; no
    # original source vertices are relabeled as a specific named duct.
    junction = Vector([(bile[0][0]+bile[0][1])/2, (bile[1][0]+bile[1][1])/2, bile[2][1]-.027])
    neck = Vector([gall[0][1]-.005,gall[1][1]-.004,gall[2][1]-.010])
    hepatic = junction+Vector((-.007,.004,.033))
    distal = Vector([bile[0][1]-.005,junction.y-.004,bile[2][0]+.009])
    paths = {
        'cystic_duct':[neck,(neck+junction)/2+Vector((.004,0,.005)),junction],
        'common_bile_duct':[junction,junction.lerp(distal,.5)+Vector((.003,0,0)),distal],
        'common_hepatic_duct':[junction,junction.lerp(hepatic,.5),hepatic],
        'right_hepatic_artery':[hepatic+Vector((.012,.010,-.012)),hepatic+Vector((-.014,.010,-.004)),hepatic+Vector((-.042,.010,.008))],
    }
    paths['cystic_artery']=[paths['right_hepatic_artery'][1],neck+Vector((-.006,.004,.006)),neck+Vector((-.018,-.008,-.012))]
    for id in ('cystic_duct','cystic_artery','common_bile_duct','common_hepatic_duct','right_hepatic_artery'):
        add(id,tube(id,paths[id],.006),['anat_gallbladder','atlas_visceral__bile_duct','atlas_cardiovascular__proper_hepatic_artery'],
            'authored schematic teaching path; 12 mm diameter intentionally enlarged for touch',.006,paths[id])
    sigmoid = bounds([source['anat_sigmoid_colon']])
    rectum_start = Vector([(sigmoid[0][0]+sigmoid[0][1])/2,(sigmoid[1][0]+sigmoid[1][1])/2,sigmoid[2][0]+.014])
    rectum_path = [rectum_start,rectum_start+Vector((-.003,.010,-.020)),rectum_start+Vector((-.003,.004,-.051))]
    add('rectum',tube('rectum',rectum_path,.015),['anat_sigmoid_colon','anat_urinary_bladder'],radius=.015,points=rectum_path)
    meso_center = center(source['anat_sigmoid_colon'])+Vector((.012,.023,.006))
    add('sigmoid_mesocolon',ellipsoid('sigmoid_mesocolon',meso_center,(.038,.007,.043)),['anat_sigmoid_colon'],
        'authored schematic flattened mesenteric sheet; not extracted from generic mesocolon')
    gonad_names = ['atlas_cardiovascular__left_testicular_artery','atlas_cardiovascular__left_testicular_vein']
    add('left_gonadal_vessels',duplicate_group('left_gonadal_vessels',[source[n] for n in gonad_names]),gonad_names,
        'source-derived grouped male left testicular artery and vein; source duplicates retained')
    jejunum = [o for n,o in source.items() if 'jejunum' in n]
    if not jejunum: raise ValueError('No source jejunum for representative small_bowel target')
    bowel_sources = [source['anat_duodenum'],*jejunum,ileum]
    add('small_bowel',duplicate_group('small_bowel',bowel_sources),[o.name for o in bowel_sources],
        'source-derived representative aggregate: duodenum and jejunum plus authored terminal ileum; not a complete traced small bowel')
    # Batch 4 adds canonical whole-organ/context targets without renaming any
    # existing atlas substructure. These are separate addressable aggregates.
    chambers = ['atlas_cardiovascular__left_atrium', 'atlas_cardiovascular__right_atrium',
                'atlas_cardiovascular__left_ventricle', 'atlas_cardiovascular__right_ventricle']
    add('heart', duplicate_group('heart', [source[n] for n in chambers]), chambers,
        'source-derived aggregate of four cardiac chamber meshes; source substructures retained')
    lobes = ['atlas_visceral__superior_lobe_of_left_lung', 'atlas_visceral__inferior_lobe_of_left_lung',
             'atlas_visceral__superior_lobe_of_right_lung', 'atlas_visceral__middle_lobe_of_right_lung',
             'atlas_visceral__inferior_lobe_of_right_lung']
    add('lungs', duplicate_group('lungs', [source[n] for n in lobes]), lobes,
        'source-derived aggregate of five lung lobes; source substructures retained')
    abdomen = bounds([source['anat_liver'], source['anat_sigmoid_colon'], source['anat_transverse_colon']])
    wall_center = Vector([(abdomen[0][0]+abdomen[0][1])/2, abdomen[1][0]-.018,
                          (abdomen[2][0]+abdomen[2][1])/2])
    wall_scale = ((abdomen[0][1]-abdomen[0][0])*.56, .009,
                  (abdomen[2][1]-abdomen[2][0])*.54)
    add('abdominal_wall', ellipsoid('abdominal_wall', wall_center, wall_scale),
        ['anat_liver', 'anat_sigmoid_colon', 'anat_transverse_colon'],
        'authored schematic anterior abdominal wall panel; not layered muscle segmentation')
    navel = wall_center + Vector((0, -.012, -.015))
    add('umbilicus', ellipsoid('umbilicus', navel, (.009,.004,.009)),
        ['anat_abdominal_wall'], 'authored schematic touchable umbilicus landmark; not measured torso registration')
    bpy.ops.object.select_all(action='DESELECT')
    for o in authored: o.select_set(True)
    out = BASE/'Models/exercise-targets.fbx'
    bpy.ops.export_scene.fbx(filepath=str(out),use_selection=True,object_types={'MESH'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,bake_anim=False,add_leaf_bones=False)
    n = sum(p['triangles'] for p in parts)
    system = dict(id=SYSTEM,group='body',assetPath='Assets/Scalpal/Anatomy/Models/exercise-targets.fbx',sourceTriangles=n,triangles=n,
        meshCount=len(parts),sourceBoundsBlenderXYZ=bounds(authored),sha256=hashlib.sha256(out.read_bytes()).hexdigest())
    result = dict(schemaVersion=1,system=system,parts=parts,excludedGuides=[],
        inputHashes={s:hashlib.sha256((BASE/'Models'/f'{s}.fbx').read_bytes()).hexdigest() for s in ['visceral','cardiovascular']})
    dest=ROOT/'assets/anatomy/build/exercise-targets.json'
    dest.parent.mkdir(exist_ok=True)
    dest.write_text(json.dumps(result,indent=2)+'\n')
    print('AUTHORED TARGETS',len(parts),n,flush=True)
    # Export a focused three-region visual check from the actual generated meshes.
    render_preview(source,authored)


def render_preview(source, authored):
    for o in list(bpy.context.scene.objects): o.hide_render=True
    groups=[(['cecum','terminal_ileum'],['anat_appendix','atlas_visceral__ascending_colon']),
        (['cystic_duct','cystic_artery','common_bile_duct','common_hepatic_duct','right_hepatic_artery'],['anat_gallbladder','atlas_visceral__bile_duct']),
        (['rectum','sigmoid_mesocolon'],['anat_sigmoid_colon','anat_urinary_bladder'])]
    for i,(ids,context) in enumerate(groups):
        objects=[o for o in authored if o.name[5:] in ids]+[source[n] for n in context]
        b=bounds(objects)
        origin=Vector([(a+z)/2 for a,z in b])
        for original in objects:
            o=original.copy();o.data=original.data.copy();bpy.context.collection.objects.link(o)
            o.hide_render=False
            o.matrix_world=Matrix.Translation(Vector(((i-1)*.25,0,0))-origin)@original.matrix_world
            id=original.name[5:]
            o.color=(.90,.22,.16,1) if 'artery' in id else ((.95,.68,.12,1) if 'duct' in id else ((.55,.75,.94,1) if id in ids else (.63,.53,.50,1)))
    bpy.ops.object.camera_add(location=(0,-2,.15))
    camera=bpy.context.object
    camera.rotation_euler=(Vector((0,0,0))-camera.location).to_track_quat('-Z','Y').to_euler()
    camera.data.type='ORTHO';camera.data.ortho_scale=.80
    scene=bpy.context.scene;scene.camera=camera
    scene.render.engine='BLENDER_WORKBENCH'
    shading=scene.display.shading;shading.light='STUDIO';shading.color_type='OBJECT';shading.show_shadows=True
    shading.show_cavity=True;shading.cavity_type='BOTH';shading.background_type='WORLD';scene.world.color=(.035,.035,.045)
    scene.render.resolution_x=1800;scene.render.resolution_y=800;scene.render.resolution_percentage=100
    scene.render.filepath=str(ROOT/'assets/anatomy/targets-preview.png')
    bpy.ops.render.render(write_still=True)


if __name__ == '__main__': main()
