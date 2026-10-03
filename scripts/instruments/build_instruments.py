"""Parametric instrument assets plus adapted MIT LapGym parts, built with Blender 4.5 LTS.

Run: Blender --background --python scripts/instruments/build_instruments.py -- --root .
Generic simulator geometry, not manufacturer CAD or clinically validated instruments.
Logical source coordinates: meters, +Z distal, +Y top, origin at handle grip.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--only', default='')
parser.add_argument('--no-render', action='store_true')
args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else [])
ROOT = args.root.resolve()
SOURCE = ROOT / 'assets/instruments/source'
PREVIEWS = ROOT / 'assets/instruments/previews'
EXPORTS = ROOT / 'assets/instruments/exports'
MODELS = ROOT / 'apps/quest/Assets/Scalpal/Instruments/Models'
for directory in (SOURCE, PREVIEWS, EXPORTS, MODELS):
    directory.mkdir(parents=True, exist_ok=True)
SPECS = {s['id']: s for s in json.loads((ROOT/'assets/instruments/tool-specs.json').read_text())['instruments']}
VENDOR = ROOT/'assets/instruments/third-party/sofa_env'
OPEN_JAWS = {'grasper':'atraumatic_forceps','maryland':'maryland_dissector','scissors':'scissors'}

TOOLS = [
    ('trocar_5mm', '5 mm trocar', 'access', .100, .005, 'cyan'),
    ('trocar_12mm', '12 mm trocar', 'access', .100, .012, 'cyan'),
    ('laparoscope_30', '30 degree laparoscope', 'scope', .310, .010, 'cyan'),
    ('atraumatic_grasper', 'Atraumatic grasper', 'grasper', .330, .005, 'green'),
    ('maryland_dissector', 'Maryland dissector', 'maryland', .360, .005, 'blue'),
    ('hook_cautery', 'Monopolar hook', 'hook', .330, .005, 'yellow'),
    ('vessel_sealer', 'Bipolar vessel sealer', 'sealer', .370, .005, 'violet'),
    ('clip_applier', 'Clip applier', 'clip', .330, .010, 'gold'),
    ('lap_scissors', 'Laparoscopic scissors', 'scissors', .310, .005, 'red'),
    ('endo_stapler', 'Endoscopic linear stapler', 'stapler', .160, .012, 'blue'),
    ('circular_stapler', 'Circular stapler', 'circular', .220, .016, 'blue'),
    ('suction_irrigator', 'Suction irrigator', 'suction', .330, .005, 'cyan'),
    ('retrieval_bag', 'Specimen retrieval bag', 'bag', .270, .010, 'green'),
    ('fascial_closure', 'Fascial closure device', 'closure', .150, .003, 'gold'),
    ('scalpel', 'Scalpel', 'scalpel', .120, .006, 'red'),
]
COLORS = {'cyan': (.02,.56,.62,1), 'green': (.10,.55,.29,1),
          'blue': (.025,.21,.56,1), 'red': (.70,.08,.055,1),
          'yellow': (.86,.58,.08,1), 'violet': (.32,.12,.56,1),
          'gold': (.73,.47,.10,1)}

def material(name, color, metal=0, rough=.35):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    shader = mat.node_tree.nodes.get('Principled BSDF')
    shader.inputs['Base Color'].default_value = color
    shader.inputs['Metallic'].default_value = metal
    shader.inputs['Roughness'].default_value = rough
    mat.diffuse_color = color
    return mat

def reset():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=False)
    for m in list(bpy.data.materials): bpy.data.materials.remove(m)
    return {
        'steel': material('Scalpal_Steel', (.55,.62,.67,1), .92,.23),
        'edge': material('Scalpal_PolishedEdge', (.72,.77,.81,1), 1,.13),
        'dark': material('Scalpal_BlackPolymer', (.013,.023,.030,1), .05,.29),
        'rubber': material('Scalpal_GripRubber', (.006,.012,.015,1), 0,.58),
        'ceramic': material('Scalpal_Ceramic', (.75,.78,.77,1), .05,.28),
        'glass': material('Scalpal_OpticalGlass', (.02,.13,.17,1), .65,.09),
        'bag': material('Scalpal_RetrievalMembrane', (.47,.68,.67,1), 0,.45),
    }

def empty(name, pos, parent=None):
    obj = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    obj.location = pos
    obj.empty_display_size = .008
    return obj

def finish(obj, name, parent, mat, bevel=0):
    obj.name = name
    # Geometry stays in logical root coordinates until converted to local parent space.
    bpy.context.view_layer.update()
    world = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_world = world
    if mat: obj.data.materials.append(mat)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    if obj.type == 'MESH':
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        if bevel:
            mod = obj.modifiers.new('Manufactured edge bevel', 'BEVEL')
            mod.width = bevel
            mod.segments = 3
            bpy.ops.object.modifier_apply(modifier=mod.name)
        for poly in obj.data.polygons: poly.use_smooth = True
        obj.select_set(False)
    return obj

def box(name, pos, size, parent, mat, bevel=.001):
    bpy.ops.mesh.primitive_cube_add(size=1, location=pos)
    obj = bpy.context.object
    obj.scale = size
    return finish(obj,name,parent,mat,min(bevel,min(size)*.2))

def rod(name, a, b, radius, parent, mat, vertices=32):
    a,b = Vector(a),Vector(b)
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius,
        depth=(b-a).length, location=(a+b)*.5)
    obj = bpy.context.object
    obj.rotation_euler = (b-a).to_track_quat('Z','Y').to_euler()
    return finish(obj,name,parent,mat,radius*.16)

def ball(name,pos,size,parent,mat):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=24, ring_count=12, location=pos)
    obj=bpy.context.object
    obj.scale=size
    return finish(obj,name,parent,mat)

def ring(name,pos,major,minor,parent,mat,scale=(1,1,1), plane='XY'):
    bpy.ops.mesh.primitive_torus_add(major_segments=40,minor_segments=8,
        location=pos,major_radius=major,minor_radius=minor)
    obj=bpy.context.object
    if plane=='YZ': obj.rotation_euler[1]=math.pi/2
    obj.scale=scale
    return finish(obj,name,parent,mat)

def path(name,points,radius,parent,mat):
    # Joined tapered mechanical curves, not runtime spline dependencies.
    parts=[]
    for i,(a,b) in enumerate(zip(points,points[1:])):
        parts.append(rod(name+str(i),a,b,radius,parent,mat,16))
    return parts

def flat_profile(name, outline, thickness, parent, mat, offset=(0,0,0),plane='YZ'):
    # Extrude a Y/Z blade silhouette across X.
    n=len(outline)
    verts=[(x+offset[0],y+offset[1],z+offset[2]) for x in (-thickness/2,thickness/2) for y,z in outline]
    if plane=='XZ': verts=[(y,x,z) for x,y,z in verts]
    faces=[tuple(range(n-1,-1,-1)),tuple(range(n,2*n))]
    faces += [(i,(i+1)%n,(i+1)%n+n,i+n) for i in range(n)]
    mesh=bpy.data.meshes.new(name)
    mesh.from_pydata(verts,[],faces);mesh.update()
    obj=bpy.data.objects.new(name,mesh);bpy.context.collection.objects.link(obj)
    return finish(obj,name,parent,mat,thickness*.15)

def pistol(root,m,accent,large=False):
    # Reuse the open contoured handle rather than inventing a rectangular housing.
    if VENDOR.exists():
        body=import_stl('instruments/laparoscopic_handle_body_only.stl',root,m['dark'])
        import_stl('instruments/laparoscopic_handle_body_screw.stl',root,m['steel'])
        trigger=empty('Trigger',(0,0,0),root)
        import_stl('instruments/laparoscopic_handle_lever.stl',trigger,accent)
        rod('RotationCollar',(0,0,.030),(0,0,.048),.013,root,m['dark'])
        ring('AccentCollar',(0,0,.034),.013,.0015,root,accent)
        root['adaptedOpenHandle']='LapGym MIT handle_body_only, body_screw, lever; authored assembly'
        return trigger
    f=1.15 if large else 1
    body=ball('HandleBody',(0,-.005,.002),(.018*f,.020*f,.032*f),root,m['dark'])
    box('PalmGrip',(0,-.041*f,-.014),(.028*f,.068*f,.028*f),root,m['dark'],.006)
    box('GripInset',(0,-.045*f,-.030),(.022*f,.043*f,.007),root,m['rubber'],.002)
    for i in range(6):
        box('GripRidge',(0,-.026*f-i*.007,-.034),(.024*f,.0017,.002),root,m['rubber'],.0005)
    trigger=empty('Trigger',(0,-.005,.019),root)
    # pivot at proximal finger lever
    box('TriggerLever',(0,-.035,.032),(.016,.055,.010),trigger,accent,.003)
    rod('TriggerPin',(-.016,-.005,.019),(.016,-.005,.019),.003,root,m['steel'])
    rod('RotationCollar',(0,0,.028),(0,0,.044),.014*f,root,m['dark'])
    ring('AccentCollar',(0,0,.035),.014*f,.0018,root,accent)
    for i in range(12):
        theta=2*math.pi*i/12
        box('CollarKnurl',(math.sin(theta)*.013*f,math.cos(theta)*.013*f,.036),(.002,.002,.010),root,m['rubber'],.0003)
    for x in (-.018*f,.018*f):
        rod('ShellScrew',(x-.001,0,.003),(x+.001,0,.003),.0024,root,m['steel'],16)
    return trigger

def rings_handle(root,m,accent):
    box('HandleBridge',(0,0,.007),(.013,.013,.025),root,m['dark'],.003)
    rod('FixedHandleArm',(0,0,.005),(0,-.044,-.041),.005,root,m['dark'])
    ring('FingerRingFixed',(0,-.050,-.047),.021,.004,root,m['dark'],scale=(1,1.1,.85),plane='YZ')
    trigger=empty('Trigger',(0,0,.003),root)
    rod('MovingHandleArm',(0,0,.003),(0,-.029,.037),.0045,trigger,m['dark'])
    ring('FingerRingMoving',(0,-.044,.039),.019,.004,trigger,accent,scale=(1,1.1,.9),plane='YZ')
    rod('HandleHingePin',(-.012,0,.006),(.012,0,.006),.003,root,m['steel'])
    rod('RotationCollar',(0,0,.029),(0,0,.047),.010,root,m['dark'])
    ring('CollarAccent',(0,0,.033),.010,.0014,root,accent)

def add_jaws(root,m,kind,start,diameter):
    if kind in OPEN_JAWS:
        # LapGym provides real separate articulated jaw geometry and +X hinge pivots.
        highest=start
        for side,name in (('left','JawUpper'),('right','JawLower')):
            parent=empty(name,(0,0,start),root)
            obj=import_stl('instruments/'+OPEN_JAWS[kind]+'_jaw_'+side+'.stl',parent,m['steel'],(0,0,start))
            bpy.context.view_layer.update()
            highest=max(highest,max((obj.matrix_world @ Vector(c)).z for c in obj.bound_box))
        return highest
    length={'grasper':.025,'maryland':.023,'sealer':.030,'clip':.022,'scissors':.027,'stapler':.045}.get(kind,.022)
    for upper,name in ((True,'JawUpper'),(False,'JawLower')):
        sign=1 if upper else -1
        jaw=empty(name,(0,0,start),root)
        if kind=='scissors':
            outline=[(sign*.001,start),(sign*.003,start+.007),(sign*.002,start+.022),(sign*.0003,start+length)]
            flat_profile('Blade',outline,.0013,jaw,m['edge'],offset=((.0008 if upper else -.0008),0,0))
        elif kind=='maryland':
            points=[(0,sign*.0012,start),(0,sign*.002,start+.007),(0,sign*.004,start+.015),(0,sign*.007,start+length)]
            path('CurvedJaw',points,.0013,jaw,m['steel'])
            for i in range(6):
                box('Serration',(0,sign*(.001+i*.00055),start+.005+i*.0026),(.0022,.0006,.00065),jaw,m['edge'],.00015)
        elif kind=='stapler':
            box('Anvil' if upper else 'Cartridge',(0,sign*.005,start+length/2),(.012,.006,length),jaw,m['steel'] if upper else m['accent'],.001)
            for row in (-1,0,1):
                for i in range(13):
                    box('StaplePocket',(row*.003,sign*.002,start+.004+i*.0028),(.001,.0004,.0015),jaw,m['dark'],.0001)
        else:
            w=.002 if kind not in ('clip','sealer') else .0035
            # Fenestrated grasper: two rails around an open window.
            if kind=='grasper':
                for x in (-w,w):rod('JawRail',(x,sign*.002,start),(x,sign*.002,start+length),.0008,jaw,m['steel'],16)
                for z in (start+.002,start+length-.001):rod('JawBridge',(-w,sign*.002,z),(w,sign*.002,z),.0008,jaw,m['steel'],16)
            elif kind=='clip':
                box('ClipRail',(0,sign*.002,start+length/2),(w*2,.002,length),jaw,m['steel'],.0005)
                box('ClipChannel',(0,sign*.0008,start+length*.55),(.002,.0005,length*.8),jaw,m['dark'],.0001)
            else:
                box('JawBody',(0,sign*.002,start+length/2),(w*2,.0025,length),jaw,m['steel'],.0007)
            for i in range(10):
                box('Serration',(0,sign*.0012,start+.003+i*(length-.004)/10),(w*1.8,.00065,.00065),jaw,m['edge'],.00012)
        rod('HingeCap',(-diameter*.6,0,start),(diameter*.6,0,start),.0015,root,m['steel'],16)
    return start+length

def import_stl(relative,parent,mat,position=(0,0,0),length_scale=1):
    bpy.ops.wm.stl_import(filepath=str(VENDOR/relative),global_scale=.001)
    obj=bpy.context.object
    # Sources use millimeter coordinates with a tip pivot. Preserve their documented hinge.
    if length_scale!=1:
        for v in obj.data.vertices:v.co.z*=length_scale
    obj.location=position
    obj['source']='ScheiklP/sofa_env@85bf7e05dd088b824794dda0046679df13b13e6e/'+relative
    obj['license']='MIT; see ThirdPartyNotices.txt'
    return finish(obj,Path(relative).stem,parent,mat)

def mesh_consolidate(root):
    # Keep moving assembly pivots; merge fixed meshes by direct parent.
    parents=[root]+[o for o in root.children_recursive if o.type=='EMPTY']
    for parent in parents:
        objects=[o for o in list(parent.children) if o.type=='MESH']
        if not objects:continue
        bpy.ops.object.select_all(action='DESELECT')
        for obj in objects:obj.select_set(True)
        bpy.context.view_layer.objects.active=objects[0]
        bpy.ops.object.join()
        obj=bpy.context.object;obj.name=parent.name+'_Geometry'
        # Origin at assembly pivot; geometry remains unchanged.
        bpy.context.scene.cursor.location=parent.matrix_world.translation
        bpy.ops.object.origin_set(type='ORIGIN_CURSOR')
        # Triangulated stable export; nonuniform scale applied before joining.
        mod=obj.modifiers.new('Export triangles','TRIANGULATE')
        bpy.ops.object.modifier_apply(modifier=mod.name)
        obj.select_set(False)

def build(tool):
    id,title,kind,length,diameter,color=tool
    dimensions=SPECS[id]['referenceDimensions']
    length=dimensions.get('shaftWorkingLengthM') or length
    diameter=dimensions.get('shaftDiameterM') or diameter
    m=reset();m['accent']=material('Scalpal_Accent_'+color,COLORS[color],.18,.3)
    root=empty('inst_'+id,(0,0,0));root['instrumentId']=id;root['units']='meters'
    root['fidelity']='Original generic visual model; approximate dimensions; not clinical CAD'
    if kind in OPEN_JAWS or kind in ('hook','scope'):
        root['adaptedOpenGeometry']='LapGym sofa_env MIT; separate jaws/electrode/optics'
    empty('GripAnchor',(0,0,0),root)
    empty('GripCollider',(0,-.024,0),root)
    tip=.04+length
    if kind in ('grasper','maryland','scissors','clip'):
        if kind=='clip':pistol(root,m,m['accent'])
        else:rings_handle(root,m,m['accent'])
        rod('Shaft',(0,0,.038),(0,0,tip),diameter*.5,root,m['dark'])
        rod('DistalMetalSleeve',(0,0,tip-.026),(0,0,tip),diameter*.5,root,m['steel'])
        tip=add_jaws(root,m,kind,tip,diameter)
    elif kind=='hook':
        pistol(root,m,m['accent'])
        tip=.04+length
        import_stl('instruments/dissection_electrode.stl',root,m['steel'],(0,0,tip),length/.34931)
        tip+=.015167*length/.34931
        rod('PowerSocket',(0,.019,-.010),(0,.027,-.014),.004,root,m['accent'])
    elif kind in ('sealer','stapler'):
        pistol(root,m,m['accent'],kind=='stapler')
        rod('InsulatedShaft',(0,0,.039),(0,0,tip),diameter*.5,root,m['dark'])
        rod('ShaftTipSleeve',(0,0,tip-.016),(0,0,tip),diameter*.48,root,m['steel'])
        if kind=='hook':
            rod('CeramicInsulator',(0,0,tip-.012),(0,0,tip),.0026,root,m['ceramic'])
            path('Electrode',[(0,0,tip),(0,0,tip+.008),(0,-.008,tip+.014),(0,-.010,tip+.010)],.001,root,m['edge'])
            tip+=.014
            rod('PowerSocket',(0,.019,-.010),(0,.027,-.014),.004,root,m['accent'])
        else:tip=add_jaws(root,m,kind,tip,diameter)
    elif kind=='access':
        ball('SealHousing',(0,0,.005),(.027,.027,.031),root,m['dark'])
        ring('SealRing',(0,0,-.022),.020,.004,root,m['accent'])
        rod('Cannula',(0,0,.026),(0,0,.026+length),diameter*.6,root,m['steel'])
        tip=.026+length
        rod('Lumen',(0,0,tip-.001),(0,0,tip+.0002),diameter*.36,root,m['dark'])
        rod('GasValve',(0,.021,.010),(0,.045,.010),.005,root,m['dark'])
        box('StopcockLever',(0,.040,.010),(.031,.004,.006),root,m['accent'],.001)
        for i in range(6):ring('CannulaGroove',(0,0,.044+i*.012),diameter*.61,.0005,root,m['steel'])
        obt=empty('Obturator',(0,0,0),root)
        ball('ObturatorCap',(0,0,-.033),(.023,.023,.013),obt,m['accent'])
    elif kind=='scope':
        tip=.04+length
        import_stl('endoscopes/laparoscope_optics_30_degree.stl',root,m['steel'],(0,0,tip),tip/.3805)
        rod('ProximalLens',(0,0,-.001),(0,0,.0005),.013,root,m['glass'])
        rod('ThirtyDegreeOptic',(0,0,tip-.002),(0,-.001,tip),.0042,root,m['glass'])
        optic=empty('OpticDirection',(0,0,tip),root);optic.rotation_euler[0]=math.radians(30)
    elif kind=='suction':
        box('ValveBody',(0,-.011,.005),(.032,.064,.042),root,m['dark'],.007)
        for x,c in ((-.009,m['accent']),(.009,m['ceramic'])):
            p=empty('Trigger' if x<0 else 'IrrigationButton',(x,.024,.005),root)
            rod('ValveButton',(x,.022,.005),(x,.031,.005),.006,p,c)
        rod('SuctionTube',(0,0,.027),(0,0,tip),diameter*.5,root,m['steel'])
        rod('TubeOpening',(0,0,tip-.001),(0,0,tip+.0001),.0017,root,m['dark'])
        for x in (-.010,.010):rod('HoseConnector',(x,-.024,-.011),(x,-.052,-.011),.004,root,m['accent'])
        for i in range(3):ring('SuctionPort',(0,0,tip-.005-i*.005),.00255,.00035,root,m['dark'])
    elif kind=='bag':
        box('BagDeliveryHandle',(0,0,.006),(.034,.026,.060),root,m['dark'],.006)
        rod('DeliveryTube',(0,0,.033),(0,0,tip),diameter*.5,root,m['steel'])
        bag=empty('Bag',(0,0,tip),root)
        # Authored deployed membrane; folded state provided separately for runtime.
        ring('BagMouth',(0,0,tip+.017),.039,.0014,bag,m['steel'],scale=(1,.68,1))
        rings=18;segments=32;verts=[];faces=[]
        for i in range(rings):
            t=i/(rings-1);r=.038*(1-t*.85)
            for j in range(segments):
                a=2*math.pi*j/segments
                wrinkle=1+.035*math.sin(7*a+t*12)
                verts.append((r*math.cos(a)*wrinkle,r*.68*math.sin(a)*wrinkle,tip+.017+t*.075))
        for i in range(rings-1):
            for j in range(segments):
                n=(j+1)%segments;a=i*segments+j;b=i*segments+n
                faces.append((a,b,b+segments,a+segments))
        mesh=bpy.data.meshes.new('RetrievalMembrane');mesh.from_pydata(verts,[],faces);mesh.update()
        obj=bpy.data.objects.new('RetrievalMembrane',mesh);bpy.context.collection.objects.link(obj);finish(obj,obj.name,bag,m['bag'])
        folded=empty('BagFolded',(0,0,tip),root)
        rod('FoldedMembrane',(0,0,tip),(0,0,tip+.022),.003,folded,m['bag'])
        tip+=.030
    elif kind=='circular':
        pistol(root,m,m['accent'],True)
        # Slightly curved shaft and separable anvil head.
        path('CurvedShaft',[(0,0,.040),(0,.002,.10),(0,.008,.17),(0,.020,.04+length)],diameter*.5,root,m['steel'])
        z=.04+length
        rod('StapleHead',(0,.020,z-.035),(0,.020,z),.014,root,m['dark'])
        ring('StapleFace',(0,.020,z),.012,.0016,root,m['steel'])
        anvil=empty('Anvil',(0,.020,z),root)
        rod('AnvilStem',(0,.020,z),(0,.020,z+.015),.003,anvil,m['steel'])
        rod('AnvilPlate',(0,.020,z+.015),(0,.020,z+.020),.014,anvil,m['steel'])
        for i in range(24):
            a=2*math.pi*i/24
            box('StapleWell',(.010*math.cos(a),.020+.010*math.sin(a),z+.0002),(.001,.001,.0008),root,m['dark'],.0001)
        tip=z+.02
    elif kind=='closure':
        ball('ClosureHandle',(0,0,0),(.026,.015,.020),root,m['accent'])
        box('FingerWings',(0,0,0),(.065,.014,.012),root,m['dark'],.003)
        rod('NeedleShaft',(0,0,.015),(0,0,tip),diameter*.5,root,m['steel'])
        flat_profile('NeedleTip',[(.001,tip-.009),(.001,tip),(-.001,tip-.004)],.0018,root,m['edge'])
        jaw=empty('JawUpper',(0,0,tip-.011),root)
        rod('CaptureJaw',(0,.001,tip-.011),(0,.001,tip-.002),.0008,jaw,m['steel'],16)
        p=empty('Trigger',(0,0,0),root);rod('Plunger',(0,0,-.03),(0,0,-.007),.004,p,m['steel'])
        ball('PlungerButton',(0,0,-.03),(.015,.015,.004),p,m['dark'])
    elif kind=='scalpel':
        box('ScalpelHandle',(0,0,.017),(.010,.004,.118),root,m['steel'],.0015)
        for i in range(25):
            for side in (-1,1):box('HandleGripGroove',(0,side*.0021,-.020+i*.002),(.008,.00035,.0006),root,m['dark'],.0001)
        # Generic curved blade silhouette, independently addressable for slicing tests.
        blade=empty('Blade',(0,0,.068),root)
        profile=[(.002,.064),(.008,.075),(.010,.094),(.007,.111),(.002,.120),(-.001,.109),(-.002,.080)]
        flat_profile('ScalpelBlade',profile,.0008,blade,m['edge'],plane='XZ')
        rod('BladeMount',(0,0,.061),(0,0,.075),.0025,root,m['steel'])
        tip=.120
    empty('Tip',(0,0,tip),root)
    empty('ActionPoint',(0,.020 if kind=='circular' else 0,tip-.005),root)
    empty('CutStart',(0,0,tip-.020),root)
    empty('CutEnd',(0,0,tip),root)
    mesh_consolidate(root)
    # Source meters, fully applied mesh transforms; keep articulation empties.
    return root,m,title

def export(root,id):
    objects=[root]+list(root.children_recursive)
    bpy.ops.object.select_all(action='DESELECT')
    for obj in objects:obj.select_set(True)
    bpy.context.view_layer.objects.active=root
    # High-detail GLB is useful for review; FBX is the separately optimized Quest asset.
    bpy.ops.export_scene.gltf(filepath=str(EXPORTS/('inst_'+id+'.glb')),
        export_format='GLB',use_selection=True,export_extras=True,
        export_yup=True,export_animations=False)
    originals={obj:obj.data for obj in objects if obj.type=='MESH'}
    source_triangles=sum(len(mesh.polygons) for mesh in originals.values())
    ratio=min(1,1900/max(source_triangles,1))
    for obj,mesh in originals.items():
        obj.data=mesh.copy()
        bpy.context.view_layer.objects.active=obj
        if ratio<1:
            mod=obj.modifiers.new('Quest silhouette optimization','DECIMATE');mod.ratio=ratio
            bpy.ops.object.modifier_apply(modifier=mod.name)
    runtime_triangles=sum(sum(len(face.vertices)-2 for face in obj.data.polygons) for obj in originals)
    bpy.ops.export_scene.fbx(filepath=str(MODELS/('inst_'+id+'.fbx')),
        use_selection=True,object_types={'MESH','EMPTY'},global_scale=1,
        apply_unit_scale=True,apply_scale_options='FBX_SCALE_UNITS',
        axis_forward='-Z',axis_up='Y',use_space_transform=True,
        bake_space_transform=False,bake_anim=False,add_leaf_bones=False,
        mesh_smooth_type='FACE',use_mesh_modifiers=True,path_mode='AUTO')
    for obj,mesh in originals.items():obj.data=mesh
    return objects,runtime_triangles

def studio(root,m,title,id):
    scene=bpy.context.scene
    scene.render.engine='CYCLES';scene.cycles.samples=24
    scene.cycles.use_denoising=True
    scene.render.resolution_x=1200;scene.render.resolution_y=1200
    scene.render.resolution_percentage=100
    scene.world.color=(.18,.18,.18)
    scene.view_settings.view_transform='AgX'
    coordinates=[o.matrix_world @ Vector(c) for o in root.children_recursive if o.type=='MESH' for c in o.bound_box]
    lo=Vector([min(v[i] for v in coordinates) for i in range(3)])
    hi=Vector([max(v[i] for v in coordinates) for i in range(3)])
    center=(lo+hi)*.5;size=hi-lo
    bpy.ops.object.camera_add(location=center+Vector((.38,-.72,.30)))
    cam=bpy.context.object;cam.name='PreviewCamera';cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler()
    cam.data.type='ORTHO';cam.data.ortho_scale=max(size.z,size.x,size.y)*1.30
    scene.camera=cam
    def area(name,pos,power,size,color):
        bpy.ops.object.light_add(type='AREA',location=center+Vector(pos))
        obj=bpy.context.object;obj.name=name;obj.data.energy=power;obj.data.shape='DISK';obj.data.size=size;obj.data.color=color
        obj.rotation_euler=(center-obj.location).to_track_quat('-Z','Y').to_euler()
    area('SoftboxKey',(.3,-.55,.5),35,.65,(.78,.87,1))
    area('SoftboxRim',(-.3,.25,.35),50,.5,(.2,.8,1))
    area('SoftboxFill',(-.35,-.2,-.1),18,.35,(1,.83,.63))
    # Matte blue-black background with physically lit reflections on metal.
    scene.render.film_transparent=False
    scene.world.use_nodes=True
    scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.016,.024,.040,1)
    scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.4
    scene.render.image_settings.file_format='PNG'
    scene.render.filepath='//../previews/inst_'+id+'.png'
    bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE/('inst_'+id+'.blend')),compress=True)
    if not args.no_render:bpy.ops.render.render(write_still=True)

inventory=[]
for tool in TOOLS:
    if args.only and tool[0] not in args.only.split(','):continue
    root,m,title=build(tool)
    bpy.context.view_layer.update()
    objects,runtime_triangles=export(root,tool[0])
    meshes=[o for o in objects if o.type=='MESH']
    vertices=[o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    size=[max(v[i] for v in vertices)-min(v[i] for v in vertices) for i in range(3)]
    inventory.append({'id':tool[0],'prefab':'inst_'+tool[0], 'sourceTriangles':sum(len(o.data.polygons) for o in meshes),'runtimeTriangles':runtime_triangles,
        'meshAssemblies':len(meshes),'sourceBoundsMeters':size,
        'parts':[o.name for o in objects if o.type=='EMPTY'],
        'source':'source/inst_'+tool[0]+'.blend','fbx':'../../apps/quest/Assets/Scalpal/Instruments/Models/inst_'+tool[0]+'.fbx',
        'glb':'exports/inst_'+tool[0]+'.glb','preview':'previews/inst_'+tool[0]+'.png'})
    studio(root,m,title,tool[0])
    print('SCALPAL_TOOL_COMPLETE '+json.dumps(inventory[-1]),flush=True)
if not args.only:
    (ROOT/'assets/instruments/generated-inventory.json').write_text(json.dumps({'schemaVersion':1,'blender':bpy.app.version_string,'units':'meters','instruments':inventory},indent=2)+'\n')
