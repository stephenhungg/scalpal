using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Brand;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Surgery;
using Scalpal.Surgery.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest.Editor
{
    // Actual native mannequin, wall and indexed anatomy; only device/aim samples and
    // prior semantic wall-exposure events are synthetic. No cut topology or headset claim.
    public static class NativeOpenWoundPointerValidation
    {
        sealed class Input:IXRInputSource
        {
            public bool DisplayRunning=>true;public bool FloorTracking=>true;public bool HasFocus=>true;
            public bool TryPose(XRNode node,out Pose pose){pose=Pose.identity;return true;}
            public float Grip(XRNode node)=>0;public float Trigger(XRNode node)=>0;
            public bool Button(XRNode node,XRInputButton button)=>false;
        }
        struct Witness {public Ray ray;public RaycastHit skin,organ;public AnatomyPart part;}
        static int checks;
        static void Require(bool value,string message){checks++;if(!value)throw new InvalidOperationException("Open wound pointer: "+message);}
        [MenuItem("Scalpal/Quest/Validate Open Wound Pointer Occlusion")]
        public static void Run()
        {
            checks=0;
            if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())throw new InvalidOperationException("Save/discard scene edits before pointer regression");
            var scenes=EditorSceneManager.GetSceneManagerSetup();var oldSource=XRInput.Source;var oldAim=ScalpalAim.Override;
            NativeTissueSimulation tissue=null;GameObject foreign=null;
            float oldWindow=Shader.GetGlobalFloat("_ScalpalWoundWindow");var oldOpening=Shader.GetGlobalVector("_ScalpalWoundOpening");
            var oldAxis=Shader.GetGlobalVector("_ScalpalWoundAxis");var oldMatrix=Shader.GetGlobalMatrix("_ScalpalWoundWorldToLocal");
            try
            {
                var adapter=OpenSurgeryBuild.ConfigureSceneAttempt(out var session,out tissue);
                session.presentation.passthrough=false;session.presentation.Apply();session.anatomy.SetRegistrationValid(true);
                typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(session.workbench,new object[]{true});
                var skin=session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true).Single(c=>c.name=="PatientCollision");
                Require(skin.enabled&&skin.gameObject.activeInHierarchy&&skin.sharedMesh,"native imported mannequin collision is active");
                string skinPath=AssetDatabase.GetAssetPath(skin.sharedMesh);
                ValidatePatientGeometry(session,skin,skinPath);
                var wound=adapter.Wound;var body=session.exercise.Body;
                foreach(var step in session.exercise.SelectedCase.procedure.steps.Take(5))foreach(var e in CaseRunner.PerfectEvents(step))
                    Require(session.exercise.Submit(e,out _,out var why),"disclosed prior semantic wall-exposure fixture: "+why);
                Require(body.Get("peritoneum","opened")==1,"prior exposure fixture reaches the open membrane");
                wound.SetRegistrationValid(true);wound.Apply(body);
                var wall=session.GetComponent<NativeVolumeSimulation>().Wall;wall.SetVisible(true);
                Require(wall.GetComponent<MeshCollider>().enabled,"actual unfractured wall remains a foreground collider; fixture never hides topology to manufacture an organ hit");
                var pointer=session.GetComponent<NativeScenePointer>();Require(pointer,"actual native scene contains the pointer consumer");pointer.Initialize(session);
                var hand=session.workbench.inputs.Single(i=>i.controller==XRNode.LeftHand);hand.enabled=true;
                hand.GetComponent<InstrumentInteractor>().SetTrackedPose(hand.transform.position,hand.transform.rotation,true);
                XRInput.Source=new Input();ScalpalPointerSample aim=default;ScalpalAim.Override=i=>i==0?aim:default;
                var witness=FindWitness(session,wound,skin);var frame=session.workbench.trackingOrigin;
                Require(witness.organ.distance>witness.skin.distance+.003f&&witness.part&&witness.part.IsVisible,"actual indexed organ collider lies beneath the actual aperture witness");
                var mesh=witness.part.GetComponent<DeformableTissue>()?.SourceMesh??witness.part.GetComponent<MeshFilter>().sharedMesh;
                Require(AssetDatabase.GetAssetPath(mesh).EndsWith(".fbx",StringComparison.OrdinalIgnoreCase),"under-aperture anatomy witness uses its imported source mesh");
                aim=new ScalpalPointerSample{kind=ScalpalPointerKind.Controller,position=frame.InverseTransformPoint(witness.ray.origin),rotation=Quaternion.Inverse(frame.rotation)*Quaternion.LookRotation(witness.ray.direction,Vector3.up)};
                session.workbench.headCamera.transform.position=witness.ray.origin;
                void Step(){Physics.SyncTransforms();pointer.Simulate();}
                void SkinBlocks(string reason)
                {
                    Step();Require(!pointer.TryGetPointed(0,out _),reason+": opaque skin cannot label the deeper organ");
                    var beam=pointer.LaserVisual(0);Require(beam&&beam.enabled&&Vector3.Distance(beam.GetPosition(1),witness.skin.point)<.0002f,reason+": laser ends on actual PatientCollision");
                }
                Physics.SyncTransforms();var remaining=Physics.RaycastAll(witness.ray,NativeScenePointer.MaximumDistance,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Collide)
                    .Where(h=>!(h.collider.transform.IsChildOf(session.presentation.virtualMannequin.transform)&&wound.ClipsSkinAt(h.point)))
                    .Where(h=>!h.collider.GetComponent<InstrumentTipContact>()).OrderBy(h=>h.distance).ToArray();
                Require(remaining.Length>0,"a real foreground surface remains after clipped skin is excluded");
                var expected=remaining[0];Step();var laser=pointer.LaserVisual(0);
                Require(laser&&laser.enabled&&Vector3.Distance(laser.GetPosition(1),expected.point)<.0002f&&expected.distance>witness.skin.distance+.0001f,"native pointer laser passes through clipped PatientCollision and terminates on the actual remaining foreground surface");
                var firstPart=expected.collider.GetComponentInParent<AnatomyPart>();
                if(firstPart&&session.anatomy.TryGetPart(firstPart.stableId,out var indexed)&&indexed==firstPart)
                    Require(pointer.TryGetPointed(0,out var pointed)&&!pointed.instrument&&pointed.id==firstPart.stableId,"actual first indexed anatomy under the aperture is named accurately");
                else
                {
                    Require(expected.collider.transform.IsChildOf(wall.transform),"non-anatomy foreground is the real wall, not an unrelated fixture blocker: "+expected.collider.name);
                    Require(!pointer.TryGetPointed(0,out _),"real unfractured wall occludes deeper organ identity despite semantic body exposure");
                }
                Debug.Log("SCALPAL_OPEN_WOUND_POINTER_WITNESS skin="+skinPath+" organ="+witness.part.stableId+" foreground="+expected.collider.name+" skinDepthMm="+wound.transform.InverseTransformPoint(witness.skin.point).z*1000+" skinDistance="+witness.skin.distance+" foregroundDistance="+expected.distance+" physicalWallHidden=false");
                body.Set("skin","opened",0);wound.Apply(body);SkinBlocks("intact skin");body.Set("skin","opened",1);wound.Apply(body);
                body.Set("skin","closed",1);wound.Apply(body);SkinBlocks("closed wound");body.Set("skin","closed",0);wound.Apply(body);
                wound.SetRegistrationValid(false);SkinBlocks("lost wound registration");wound.SetRegistrationValid(true);wound.Apply(body);
                wound.Concealed=true;SkinBlocks("concealed wound");wound.Concealed=false;wound.Apply(body);
                var pose=new Pose(wound.transform.position,wound.transform.rotation);
                var local=wound.transform.InverseTransformPoint(witness.skin.point);var opening=wound.SkinOpeningLocal;var axis=wound.IncisionAxisLocal;
                foreach(float targetDepth in new[]{-.0121f,.0451f})
                {
                    wound.transform.position=pose.position+pose.rotation*Vector3.forward*(local.z-targetDepth);wound.Apply(body);
                    Require(!wound.ClipsSkinAt(witness.skin.point),"actual skin hit outside shader depth slab remains opaque at"+targetDepth);
                    // Moving only the shader frame also moves its child wall. Aim from
                    // just outside the unchanged skin so that an upstream shifted wall
                    // cannot substitute for the opaque-skin negative control.
                    aim.position=frame.InverseTransformPoint(witness.skin.point-witness.ray.direction*.004f);
                    SkinBlocks("depth outside -12..45 mm");wound.transform.SetPositionAndRotation(pose.position,pose.rotation);wound.Apply(body);
                    aim.position=frame.InverseTransformPoint(witness.ray.origin);
                }
                wound.transform.position=pose.position+wound.transform.TransformDirection(new Vector3(axis.x,axis.y,0))*(opening.z*2.5f);wound.Apply(body);
                Require(!wound.ClipsSkinAt(witness.skin.point),"actual skin witness outside the finite incision ellipse remains opaque");SkinBlocks("off ellipse");
                wound.transform.SetPositionAndRotation(pose.position,pose.rotation);wound.Apply(body);
                // Same real imported mesh, but outside the mannequin ownership subtree.
                foreign=new GameObject("ForeignImportedSkinCollision");foreign.transform.SetPositionAndRotation(skin.transform.position,skin.transform.rotation);foreign.transform.localScale=skin.transform.lossyScale;
                foreign.AddComponent<MeshCollider>().sharedMesh=skin.sharedMesh;Step();
                Require(wound.ClipsSkinAt(witness.skin.point),"foreign negative control remains inside the active shader aperture");
                Require(!pointer.TryGetPointed(0,out _)&&Vector3.Distance(pointer.LaserVisual(0).GetPosition(1),witness.skin.point)<.0002f,"clip predicate never bypasses an identical imported collider outside the mannequin subtree");
                UnityEngine.Object.DestroyImmediate(foreign);foreign=null;Step();
                Require(Vector3.Distance(pointer.LaserVisual(0).GetPosition(1),expected.point)<.0002f,"removing foreign collision restores the real aperture ray endpoint");
                var centre=new Vector3(opening.x,opening.y,0);
                Require(wound.ClipsSkinAt(wound.transform.TransformPoint(centre)),"active aperture centre passes the shader-aligned predicate");
                foreach(var invalid in new[]{new Vector3(opening.x,opening.y,-.013f),new Vector3(opening.x,opening.y,.046f),new Vector3(float.NaN,0,0)})
                    Require(!wound.ClipsSkinAt(wound.transform.TransformPoint(invalid)),"nonfinite/out-of-depth coordinates cannot turn skin transparent");
                Debug.Log("SCALPAL_NATIVE_OPEN_WOUND_POINTER_OK checks="+checks+" actualImportedSkin=true actualUnderlyingOrgan=true actualWallPreserved=true syntheticPriorExposure=true headset=false");
            }
            finally
            {
                ScalpalAim.Override=oldAim;XRInput.Source=oldSource;if(foreign)UnityEngine.Object.DestroyImmediate(foreign);if(tissue)tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(scenes);Shader.SetGlobalFloat("_ScalpalWoundWindow",oldWindow);Shader.SetGlobalVector("_ScalpalWoundOpening",oldOpening);Shader.SetGlobalVector("_ScalpalWoundAxis",oldAxis);Shader.SetGlobalMatrix("_ScalpalWoundWorldToLocal",oldMatrix);
            }
        }
        static void ValidatePatientGeometry(NativeCaseSession session,MeshCollider skin,string skinPath)
        {
            const float tolerance=.00001f; // 10 micrometres, bounded float world-transform roundoff.
            var patient=session.presentation.virtualMannequin;
            var shading=session.GetComponent<NativePatientSurfaceShading>();
            var filter=patient.GetComponent<MeshFilter>();
            Require(shading&&shading.SourceMesh&&shading.RenderMesh&&filter.sharedMesh==shading.RenderMesh,"actual patient renderer uses its owned shading clone");
            string sourcePath=AssetDatabase.GetAssetPath(shading.SourceMesh);
            Require(skinPath=="Assets/Scalpal/Environment/Models/SupinePatientCollision.asset"&&sourcePath.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase),"committed patient collision and actual FBX source provenance: collision="+skinPath+" source="+sourcePath);
            Require(shading.SourceMesh.vertices.SequenceEqual(shading.RenderMesh.vertices)&&shading.SourceMesh.triangles.SequenceEqual(shading.RenderMesh.triangles),"angle-limited shading preserves source patient positions and topology");
            var rendered=patient.GetComponentsInChildren<MeshFilter>(true).Where(f=>f.sharedMesh&&f.GetComponent<MeshRenderer>()).SelectMany(f=>f.sharedMesh.vertices.Select(f.transform.TransformPoint)).ToArray();
            var collision=skin.sharedMesh.vertices.Select(skin.transform.TransformPoint).ToArray();
            Require(rendered.Length>0&&collision.Length>0&&rendered.All(Finite)&&collision.All(Finite),"actual skin collision and rendered geometry have finite world positions");
            var renderedIndex=WorldIndex(rendered,tolerance);var collisionIndex=WorldIndex(collision,tolerance);
            int collisionMatches=0,renderMatches=0;float worstCollision=0,worstRender=0;
            foreach(var p in collision){float distance=Nearest(p,renderedIndex,tolerance);if(distance<=tolerance)collisionMatches++;worstCollision=Mathf.Max(worstCollision,distance);}
            foreach(var p in rendered){float distance=Nearest(p,collisionIndex,tolerance);if(distance<=tolerance)renderMatches++;worstRender=Mathf.Max(worstRender,distance);}
            var cb=new Bounds(collision[0],Vector3.zero);foreach(var p in collision)cb.Encapsulate(p);
            var rb=new Bounds(rendered[0],Vector3.zero);foreach(var p in rendered)rb.Encapsulate(p);
            float boundsError=Mathf.Max(Vector3.Distance(cb.min,rb.min),Vector3.Distance(cb.max,rb.max));
            Debug.Log("SCALPAL_PATIENT_POINTER_GEOMETRY source="+sourcePath+" collision="+skinPath+" collisionWorldMatches="+collisionMatches+"/"+collision.Length+" renderWorldMatches="+renderMatches+"/"+rendered.Length+" worstCollisionMm="+worstCollision*1000+" worstRenderMm="+worstRender*1000+" boundsErrorMm="+boundsError*1000+" toleranceMm="+tolerance*1000);
            Require(collisionMatches==collision.Length,"every actual committed collision vertex corresponds to visible patient world geometry within 10 micrometres; see measured coverage");
            Require(renderMatches==rendered.Length,"every actual rendered patient vertex corresponds to collision world geometry within 10 micrometres; index ordering/duplicate corners are irrelevant");
            Require(boundsError<=tolerance,"actual collision and rendered patient world bounds correspond within 10 micrometres");
        }
        static bool Finite(Vector3 p)=>!float.IsNaN(p.x)&&!float.IsInfinity(p.x)&&!float.IsNaN(p.y)&&!float.IsInfinity(p.y)&&!float.IsNaN(p.z)&&!float.IsInfinity(p.z);
        static Vector3Int WorldKey(Vector3 p,float scale)=>new Vector3Int(Mathf.RoundToInt(p.x/scale),Mathf.RoundToInt(p.y/scale),Mathf.RoundToInt(p.z/scale));
        static Dictionary<Vector3Int,List<Vector3>> WorldIndex(Vector3[] points,float scale)
        {
            var result=new Dictionary<Vector3Int,List<Vector3>>();
            foreach(var p in points){var key=WorldKey(p,scale);if(!result.TryGetValue(key,out var bucket))result[key]=bucket=new List<Vector3>();bucket.Add(p);}
            return result;
        }
        static float Nearest(Vector3 p,Dictionary<Vector3Int,List<Vector3>> index,float scale)
        {
            var key=WorldKey(p,scale);float best=float.PositiveInfinity;
            for(int x=-1;x<=1;x++)for(int y=-1;y<=1;y++)for(int z=-1;z<=1;z++)
                if(index.TryGetValue(key+new Vector3Int(x,y,z),out var points))foreach(var q in points)best=Mathf.Min(best,Vector3.Distance(p,q));
            return best;
        }
        static Witness FindWitness(NativeCaseSession session,OpenWoundView wound,MeshCollider skin)
        {
            var opening=wound.SkinOpeningLocal;var axis=wound.IncisionAxisLocal;var across=new Vector2(-axis.y,axis.x);
            int clippedSkin=0,organBelow=0,foregroundBeforeSkin=0;bool backfaces=Physics.queriesHitBackfaces;Physics.queriesHitBackfaces=true;
            try
            {
                for(int x=-12;x<=12;x++)for(int y=-8;y<=8;y++)
                {
                    float u=x/13f,v=y/9f;if(u*u+v*v>.9f)continue;
                    var q=new Vector2(opening.x,opening.y)+axis*(u*opening.z)+across*(v*opening.w);
                    var ray=new Ray(wound.transform.TransformPoint(new Vector3(q.x,q.y,-.25f)),wound.transform.forward);
                    if(!skin.Raycast(ray,out var skinHit,.6f)||!wound.ClipsSkinAt(skinHit.point))continue;clippedSkin++;
                    var foreground=Physics.RaycastAll(ray,NativeScenePointer.MaximumDistance,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Collide)
                        .Where(h=>!(h.collider.transform.IsChildOf(session.presentation.virtualMannequin.transform)&&wound.ClipsSkinAt(h.point)))
                        .Where(h=>!h.collider.GetComponent<InstrumentTipContact>()).OrderBy(h=>h.distance).ToArray();
                    if(foreground.Length==0||foreground[0].distance<=skinHit.distance+.0001f){foregroundBeforeSkin++;continue;}
                    foreach(var part in session.anatomy.Parts.Where(p=>p&&p.IsVisible&&p.HasVisibleGeometry))
                        foreach(var shape in part.GetComponentsInChildren<MeshCollider>(true).Where(c=>c&&c.enabled&&c.gameObject.activeInHierarchy))
                            if(shape.Raycast(ray,out var organHit,NativeScenePointer.MaximumDistance)&&organHit.distance>skinHit.distance+.003f)
                            {organBelow++;return new Witness{ray=ray,skin=skinHit,organ=organHit,part=part};}
                }
            }
            finally{Physics.queriesHitBackfaces=backfaces;}
            throw new InvalidOperationException("No actual indexed organ collider under the finite aperture; clippedSkinRays="+clippedSkin+" actualOrganWitnesses="+organBelow+" foregroundBeforeSkin="+foregroundBeforeSkin+". Do not move anatomy or hide wall collision to pass this test.");
        }
    }
}
