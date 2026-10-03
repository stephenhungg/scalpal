"""Render a small synthetic instrument dataset with visible-mask COCO boxes.

Blender --background --python scripts/instruments/render_cv_dataset.py -- --root . --out /absolute/dataset --views 2
This establishes an annotation pipeline, not a detector or real-camera accuracy.
"""
import argparse
import json
import math
import random
import sys
from pathlib import Path

import bpy
from mathutils import Vector

parser=argparse.ArgumentParser()
parser.add_argument('--root',type=Path,required=True)
parser.add_argument('--out',type=Path,required=True)
parser.add_argument('--views',type=int,default=2)
parser.add_argument('--size',type=int,default=384)
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:])
root=args.root.resolve();out=args.out.resolve()
for directory in (out/'images',out/'masks'):directory.mkdir(parents=True,exist_ok=True)
inventory=json.loads((root/'assets/instruments/generated-inventory.json').read_text())['instruments']
dataset={'info':{'description':'Scalpal synthetic geometry smoke dataset, not real-tool validation','seed':817,'units':'meters','bboxConvention':'COCO top-left x,y,width,height; visible mask pixels'},'images':[],'annotations':[],'categories':[]}
random.seed(817)
for category,tool in enumerate(inventory,1):
    id=tool['id'];dataset['categories'].append({'id':category,'name':id,'supercategory':'surgical_instrument'})
    for view in range(args.views):
        bpy.ops.wm.open_mainfile(filepath=str(root/'assets/instruments'/tool['source']))
        scene=bpy.context.scene
        scene.cycles.samples=8
        scene.render.resolution_x=scene.render.resolution_y=args.size
        scene.render.resolution_percentage=100
        obj=bpy.data.objects['inst_'+id]
        for mesh in obj.children_recursive:
            if mesh.type=='MESH':mesh.pass_index=1
        cam=scene.camera
        target=Vector((0,0,tool['sourceBoundsMeters'][2]*.30))
        phi=random.uniform(-math.pi*.65,math.pi*.65)
        cam.location=target+Vector((math.sin(phi)*.8,-math.cos(phi)*.8,random.uniform(-.3,.5)))
        cam.rotation_euler=(target-cam.location).to_track_quat('-Z','Y').to_euler()
        cam.data.ortho_scale=max(tool['sourceBoundsMeters'])*random.uniform(1.35,1.8)
        for light in [o for o in scene.objects if o.type=='LIGHT']:
            light.data.energy*=random.uniform(.65,1.6)
        scene.world.node_tree.nodes['Background'].inputs['Color'].default_value=(*[random.uniform(.008,.13) for _ in range(3)],1)
        scene.view_layers[0].use_pass_object_index=True
        scene.use_nodes=True
        nodes=scene.node_tree.nodes;nodes.clear()
        layers=nodes.new('CompositorNodeRLayers')
        composite=nodes.new('CompositorNodeComposite');scene.node_tree.links.new(layers.outputs['Image'],composite.inputs['Image'])
        mask=nodes.new('CompositorNodeIDMask');mask.index=1;mask.use_antialiasing=True
        scene.node_tree.links.new(layers.outputs['IndexOB'],mask.inputs['ID value'])
        file=nodes.new('CompositorNodeOutputFile');file.base_path=str(out/'masks');file.format.file_format='PNG';file.format.color_mode='BW'
        name=f'{id}_{view:03d}'
        file.file_slots[0].path=name+'_'
        scene.node_tree.links.new(mask.outputs['Alpha'],file.inputs[0])
        scene.render.filepath=str(out/'images'/(name+'.png'))
        bpy.ops.render.render(write_still=True)
        maskpath=out/'masks'/(name+'_0001.png')
        finalmask=out/'masks'/(name+'.png');maskpath.replace(finalmask)
        img=bpy.data.images.load(str(finalmask),check_existing=False)
        # Blender pixels are bottom-up; COCO coordinates are top-down.
        pixels=list(img.pixels);width,height=img.size
        xs=[];ys=[]
        for y in range(height):
            for x in range(width):
                if pixels[(y*width+x)*4]>.05:xs.append(x);ys.append(height-1-y)
        if not xs:raise RuntimeError('Empty instrument mask for '+name)
        x,y=max(0,min(xs)),max(0,min(ys));w,h=max(xs)-x+1,max(ys)-y+1
        imageid=len(dataset['images'])+1
        dataset['images'].append({'id':imageid,'file_name':'images/'+name+'.png','width':width,'height':height,'instrumentId':id,
            'mask_file':'masks/'+name+'.png','cameraWorldMatrix':[list(row) for row in cam.matrix_world],
            'projectionWorldToClip':[list(row) for row in cam.calc_matrix_camera(bpy.context.evaluated_depsgraph_get(),x=width,y=height) @ cam.matrix_world.inverted()],
            'cameraCoordinateSystem':'Blender camera local +X right, +Y up, -Z view','cameraType':'orthographic','synthetic':True})
        dataset['annotations'].append({'id':imageid,'image_id':imageid,'category_id':category,'bbox':[x,y,w,h],'area':len(xs),'iscrowd':0,
            'touchesImageBorder':x==0 or y==0 or x+w==width or y+h==height})
        print('SCALPAL_SYNTHETIC_IMAGE '+name+' '+str([x,y,w,h]),flush=True)
(out/'annotations.json').write_text(json.dumps(dataset,indent=2)+'\n')
print('SCALPAL_CV_DATASET_OK images='+str(len(dataset['images'])),flush=True)
