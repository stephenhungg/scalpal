"""Create an editable Blender library scene from independently rigged sources."""
import argparse
import json
import sys
from pathlib import Path
import bpy
from mathutils import Vector

p=argparse.ArgumentParser();p.add_argument('--root',type=Path,required=True)
args=p.parse_args(sys.argv[sys.argv.index('--')+1:]);root=args.root.resolve()
inventory=json.loads((root/'assets/instruments/generated-inventory.json').read_text())['instruments']
bpy.ops.object.select_all(action='SELECT');bpy.ops.object.delete(use_global=False)
for i,tool in enumerate(inventory):
    with bpy.data.libraries.load(str(root/'assets/instruments'/tool['source']),link=False) as (source,target):
        target.objects=[name for name in source.objects if name not in ('PreviewCamera','SoftboxKey','SoftboxRim','SoftboxFill')]
    collection=bpy.data.collections.new(tool['id']);bpy.context.scene.collection.children.link(collection)
    for obj in target.objects:
        if obj is not None:collection.objects.link(obj)
    model=bpy.data.objects[tool['prefab']]
    model.location=Vector(((i%5)*.20,0,(2-i//5)*.62))
bpy.ops.object.camera_add(location=(1.0,-3.0,1.5))
camera=bpy.context.object;center=Vector((.4,0,.9));camera.rotation_euler=(center-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO';camera.data.ortho_scale=2.05;bpy.context.scene.camera=camera
for position,power,size in (((1,-1,2),100,2),((-1,-.3,1),80,1.5)):
    bpy.ops.object.light_add(type='AREA',location=position);light=bpy.context.object
    light.data.energy=power;light.data.size=size
    light.rotation_euler=(center-light.location).to_track_quat('-Z','Y').to_euler()
scene=bpy.context.scene;scene.render.engine='CYCLES';scene.cycles.samples=24;scene.cycles.use_denoising=True
scene.render.resolution_x=1600;scene.render.resolution_y=1600;scene.render.resolution_percentage=100
scene.world.use_nodes=True;scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(.016,.024,.04,1)
scene.world.node_tree.nodes['Background'].inputs['Strength'].default_value=.4
for screen in bpy.data.screens:
    for area in screen.areas:
        if area.type=='VIEW_3D':
            area.spaces.active.region_3d.view_distance=2.3
            area.spaces.active.region_3d.view_location=center
            area.spaces.active.shading.type='MATERIAL'
scene['description']='Scalpal tool library: 14 authored case tool IDs plus scalpel. Separate moving joints, meters. MIT LapGym-derived parts plus original generic geometry.'
bpy.ops.wm.save_as_mainfile(filepath=str(root/'assets/instruments/source/scalpal-instrument-library.blend'),compress=True)
print('SCALPAL_LIBRARY_OK tools='+str(len(inventory)),flush=True)
