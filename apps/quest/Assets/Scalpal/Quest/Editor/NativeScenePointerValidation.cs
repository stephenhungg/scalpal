using System;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Brand;
using Scalpal.Instruments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Scalpal.Quest.Editor
{
    // Actual imported meshes and instrument prefab; only XR device/head/focus inputs are synthetic.
    // The production aim sampling, physics ray, ownership gates, label and box rendering run here.
    public static class NativeScenePointerValidation
    {
        sealed class Input : IXRInputSource
        {
            public bool focused=true, running=true, head=true;
            public bool DisplayRunning=>running;
            public bool FloorTracking=>true;
            public bool HasFocus=>focused;
            public bool TryPose(XRNode node,out Pose pose){pose=Pose.identity;return node!=XRNode.Head||head;}
            public float Grip(XRNode node)=>0;
            public float Trigger(XRNode node)=>0;
            public bool Button(XRNode node,XRInputButton button)=>false;
        }
        static int checks;
        [MenuItem("Scalpal/Quest/Validate Scene Identity Pointer")]
        public static void Run()
        {
            checks=0;
            var scene=SceneManager.GetSceneByPath(NativeSessionBuild.ScenePath);
            bool close=!scene.IsValid()||!scene.isLoaded;
            if(close)scene=EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath,OpenSceneMode.Additive);
            var fixture=new GameObject("ScenePointerFixture");
            var inactive=new GameObject("ScenePointerInactiveBindings");inactive.SetActive(false);
            var oldInput=XRInput.Source;var oldAim=ScalpalAim.Override;
            Mesh modified=null;
            try
            {
                var source=scene.GetRootGameObjects().SelectMany(root=>root.GetComponentsInChildren<NativeCaseSession>(true)).Single();
                var rig=inactive.AddComponent<NativeWorkbench>();
                var owner=inactive.AddComponent<NativeCaseSession>();owner.workbench=rig;
                rig.trackingOrigin=new GameObject("TrackingOrigin").transform;rig.trackingOrigin.SetParent(fixture.transform,false);
                rig.trackingOrigin.SetPositionAndRotation(new Vector3(100,20,-100),Quaternion.Euler(12,39,-17));
                rig.trackingOrigin.localScale=new Vector3(1.1f,.9f,1.3f);
                rig.headCamera=new GameObject("Head").AddComponent<Camera>();rig.headCamera.transform.SetParent(fixture.transform,false);
                rig.inputs=new XRInstrumentInput[2];
                for(int hand=0;hand<2;hand++)
                {
                    var go=new GameObject("PointerController"+hand);go.transform.SetParent(fixture.transform,false);
                    var input=go.AddComponent<XRInstrumentInput>();input.controller=hand==0?XRNode.LeftHand:XRNode.RightHand;input.trackingOrigin=rig.trackingOrigin;
                    input.GetComponent<InstrumentInteractor>().SetTrackedPose(Vector3.zero,Quaternion.identity,true);
                    rig.inputs[hand]=input;
                }
                SetReady(rig,true);rig.tools=Array.Empty<InstrumentBehaviour>();
                var anatomyObject=new GameObject("CurrentPracticeAnatomy");anatomyObject.transform.SetParent(fixture.transform,false);
                owner.anatomy=anatomyObject.AddComponent<AnatomyController>();owner.anatomy.initiallyHiddenSystems=Array.Empty<string>();
                var pointer=fixture.AddComponent<NativeScenePointer>();pointer.Initialize(owner);
                var inputSource=new Input();XRInput.Source=inputSource;
                int aimHand=0;ScalpalPointerSample sample=default;
                ScalpalAim.Override=hand=>hand==aimHand?sample:default;
                Action Step=()=>{Physics.SyncTransforms();pointer.Simulate();};
                Action<Vector3,Vector3> Aim=(origin,direction)=>
                {
                    rig.headCamera.transform.position=origin;
                    sample=new ScalpalPointerSample{kind=ScalpalPointerKind.Controller,position=rig.trackingOrigin.InverseTransformPoint(origin),
                        rotation=Quaternion.Inverse(rig.trackingOrigin.rotation)*Quaternion.LookRotation(direction,Vector3.up)};
                };
                var sourceParts=source.anatomy.GetComponentsInChildren<AnatomyPart>(true);
                var longest=sourceParts.OrderByDescending(part=>(string.IsNullOrEmpty(part.displayName)?part.stableId:part.displayName).Length).First();
                foreach(var sourcePart in sourceParts)
                {
                    var sourceFilter=sourcePart.GetComponent<MeshFilter>();
                    if(!sourceFilter||!sourceFilter.sharedMesh)continue;
                    var mesh=sourceFilter.sharedMesh;
                    Require(AssetDatabase.GetAssetPath(mesh).EndsWith(".fbx",StringComparison.Ordinal),sourcePart.stableId+" is the actual imported FBX");
                    var go=new GameObject("ActualImported_"+sourcePart.stableId);go.transform.SetParent(anatomyObject.transform,false);
                    go.transform.SetPositionAndRotation(new Vector3(100,20,-100),Quaternion.Euler(31,67,18));
                    // Imported meshes retain their catalog's source-unit conversion in the
                    // actual transform (some FBX meshes are sub-millimetre before 100x import).
                    // Perturb that committed metric frame; do not assume millimetre vertices.
                    go.transform.localScale=Vector3.Scale(sourceFilter.transform.lossyScale,new Vector3(1.1f,.8f,1.4f));
                    go.AddComponent<MeshFilter>().sharedMesh=mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials=sourceFilter.GetComponent<Renderer>().sharedMaterials;
                    go.AddComponent<MeshCollider>().sharedMesh=mesh;
                    var part=go.AddComponent<AnatomyPart>();part.stableId=sourcePart.stableId;part.displayName=sourcePart.displayName;
                    owner.anatomy.RebuildIndex();owner.anatomy.SetRegistrationValid(true);
                    SurfaceRay(go.transform,mesh,out var origin,out var direction);Aim(origin,direction);Step();
                    Require(pointer.TryGetPointed(aimHand,out var hit)&&hit.id==part.stableId&&!hit.instrument,part.stableId+" identified by actual imported collider");
                    Require(Vector3.Distance(hit.ray.origin,origin)<.00001f&&Vector3.Angle(hit.ray.direction,direction)<.01f,"aim pose is transformed once, independently of grip pose");
                    AssertBounds(pointer,aimHand,part,mesh,hit);
                    int other=1-aimHand;Require(!pointer.TryGetPointed(other,out _),"untracked other controller has no stale box");
                    bool highlighted=part.IsHighlighted;
                    sample.select=1;Step();Require(part.IsHighlighted==highlighted,"pointing and trigger do not own coach highlight");
                    part.SetVisible(false);Step();Require(!pointer.TryGetPointed(aimHand,out _),"hidden anatomy cannot be identified");part.SetVisible(true);
                    part.GetComponent<Renderer>().enabled=false;Step();Require(!pointer.TryGetPointed(aimHand,out _),"geometry-disabled anatomy cannot be identified");part.GetComponent<Renderer>().enabled=true;
                    owner.anatomy.SetRegistrationValid(false);Step();Require(!pointer.TryGetPointed(aimHand,out _),"invalid anatomy registration hides the identity");owner.anatomy.SetRegistrationValid(true);
                    owner.presentation=inactive.AddComponent<NativePresentation>();owner.presentation.passthrough=true;
                    Step();Require(!pointer.TryGetPointed(aimHand,out _),"AR without accepted body fit hides identity even if anatomy visibility was accidentally retained");
                    UnityEngine.Object.DestroyImmediate(owner.presentation);owner.presentation=null;
                    SetReady(rig,false);Step();Require(!pointer.TryGetPointed(aimHand,out _),"XR readiness hides the identity");SetReady(rig,true);
                    inputSource.focused=false;Step();Require(!pointer.TryGetPointed(aimHand,out _),"focus loss hides the identity");inputSource.focused=true;
                    inputSource.head=false;Step();Require(!pointer.TryGetPointed(aimHand,out _),"head tracking loss hides the identity");inputSource.head=true;
                    rig.inputs[aimHand].GetComponent<InstrumentInteractor>().SetTrackedPose(origin,Quaternion.identity,false,100);
                    Step();Require(!pointer.TryGetPointed(aimHand,out _),"tracking grace freezes a tool but cannot invent a pointer pose");
                    rig.inputs[aimHand].GetComponent<InstrumentInteractor>().SetTrackedPose(origin,Quaternion.identity,true,100.1);
                    Step();Require(pointer.TryGetPointed(aimHand,out _),"pointer reacquires after valid controller pose");
                    sample.kind=ScalpalPointerKind.Hand;Step();Require(!pointer.TryGetPointed(aimHand,out _),"hand fallback is not reported as a Quest controller");sample.kind=ScalpalPointerKind.Controller;
                    // A non-target collider in front blocks names and boxes behind it, including cards.
                    var blocker=GameObject.CreatePrimitive(PrimitiveType.Cube);blocker.transform.SetParent(fixture.transform,false);
                    blocker.transform.position=origin+direction*.015f;blocker.transform.localScale=Vector3.one*.007f;
                    var foreign=blocker.AddComponent<AnatomyPart>();foreign.stableId=part.stableId;foreign.SetVisible(true);
                    Step();Require(!pointer.TryGetPointed(aimHand,out _),"foreground UI/foreign geometry occludes a scene target");UnityEngine.Object.DestroyImmediate(blocker);
                    Step();Require(pointer.TryGetPointed(aimHand,out _),"removing foreground occluder restores actual scene identity");
                    if(sourcePart==longest)
                    {
                        foreach(float viewerDistance in new[]{.4f,1f,3f})
                        {
                            rig.headCamera.transform.position=hit.worldBounds.center-direction*viewerDistance;
                            Step();Require(pointer.TryGetPointed(aimHand,out _),"longest imported organ label stays attached at "+viewerDistance+" m viewing distance");
                            var measured=ScalpalBrandLayout.Measure(pointer.LabelVisual(aimHand).transform,rig.headCamera.transform.position);
                            Require(measured.Count==1&&measured[0].Passes&&measured[0].mmAt1m>=ScalpalBrand.LabelMinimumMmAt1m-.05f,
                                "actual longest organ label meets 32 mm/m floor at "+viewerDistance+" m; measured="+(measured.Count==1?measured[0].mmAt1m:0));
                            Require(pointer.LabelVisual(aimHand).text.Split('\n').All(line=>line.Length<=24),"long labels use explicit bounded lines without shrinking");
                        }
                        Aim(origin,direction);
                    }
                    // Source mesh topology is unchanged. A runtime deformation updates actual bounds/collider.
                    if(part.stableId=="appendix")
                    {
                        modified=UnityEngine.Object.Instantiate(mesh);var vertices=modified.vertices;
                        var shift=Vector3.Scale(modified.bounds.size,new Vector3(.2f,.13f,-.05f));
                        for(int i=0;i<vertices.Length;i++)vertices[i]=vertices[i]*.7f+shift;
                        modified.vertices=vertices;modified.RecalculateBounds();go.GetComponent<MeshFilter>().sharedMesh=modified;
                        go.GetComponent<MeshCollider>().sharedMesh=null;go.GetComponent<MeshCollider>().sharedMesh=modified;
                        SurfaceRay(go.transform,modified,out origin,out direction);Aim(origin,direction);Step();
                        Require(pointer.TryGetPointed(aimHand,out hit),"runtime-deformed imported organ remains identifiable");AssertBounds(pointer,aimHand,part,modified,hit);
                    }
                    rig.ResetTools();Require(!pointer.TryGetPointed(aimHand,out _)&&!pointer.BoxVisual(aimHand).gameObject.activeInHierarchy,"actual workbench reset clears visible box and label immediately");
                    UnityEngine.Object.DestroyImmediate(go);if(modified){UnityEngine.Object.DestroyImmediate(modified);modified=null;}
                    aimHand=1-aimHand;
                }
                Require(checks>100,"imported tests actually ran");
                // Open surgery reuses the workbench scalpel; its additive kit contains
                // retractors/forceps, not a second copy of this shared instrument.
                var template=source.workbench.tools.Single(tool=>tool.instrumentId=="scalpel");
                Require(PrefabUtility.IsPartOfPrefabInstance(template),"shared scalpel is an actual committed prefab instance");
                var toolObject=UnityEngine.Object.Instantiate(template.gameObject,fixture.transform);var scalpel=toolObject.GetComponent<InstrumentBehaviour>();
                toolObject.transform.SetPositionAndRotation(new Vector3(100,20,-100),Quaternion.Euler(0,57,0));
                rig.tools=new[]{scalpel};owner.anatomy.SetRegistrationValid(false);
                var handle=scalpel.GetComponentsInChildren<Collider>().First(shape=>!shape.isTrigger);
                Physics.SyncTransforms();var toolOrigin=handle.bounds.center-toolObject.transform.right*.3f;
                Aim(toolOrigin,(handle.bounds.center-toolOrigin).normalized);Step();
                Require(pointer.TryGetPointed(aimHand,out var toolHit)&&toolHit.instrument&&toolHit.id=="scalpel","unheld scene tool identified even while anatomy registration is invalid");
                Require(pointer.LabelVisual(aimHand).text=="scalpel","tool label comes from the scene instrument ID");
                var beforeToolBounds=toolHit.worldBounds;
                toolObject.transform.position+=new Vector3(.1f,.04f,-.02f);Physics.SyncTransforms();
                toolOrigin=handle.bounds.center-toolObject.transform.right*.3f;Aim(toolOrigin,(handle.bounds.center-toolOrigin).normalized);Step();
                Require(pointer.TryGetPointed(aimHand,out toolHit)&&Vector3.Distance(toolHit.worldBounds.center,beforeToolBounds.center)>.1f,"tool box follows actual transformed tool geometry");
                // This is an Editor fixture: OnDisable is not Play Mode lifecycle
                // evidence. Exercise the production disabled-input branch explicitly.
                pointer.enabled=false;pointer.Simulate();Require(!pointer.TryGetPointed(aimHand,out _),"disabled runtime input cycle clears scene identities");
                Debug.Log("SCALPAL_NATIVE_SCENE_POINTER_VALIDATION_OK checks="+checks+" actual imported mesh/prefab ray fixtures; not physical headset or Play Mode evidence");
            }
            finally
            {
                ScalpalAim.Override=oldAim;XRInput.Source=oldInput;
                UnityEngine.Object.DestroyImmediate(fixture);UnityEngine.Object.DestroyImmediate(inactive);if(modified)UnityEngine.Object.DestroyImmediate(modified);
                if(close)EditorSceneManager.CloseScene(scene,true);
            }
        }
        static void AssertBounds(NativeScenePointer pointer,int hand,AnatomyPart part,Mesh mesh,NativeScenePointer.PointedObject hit)
        {
            var rendered=part.GetComponent<Renderer>().bounds;
            Require(Vector3.Distance(rendered.center,hit.worldBounds.center)<.000001f&&Vector3.Distance(rendered.size,hit.worldBounds.size)<.000001f,"world bounds are the current renderer bounds in the rotated/scaled frame");
            var expanded=hit.worldBounds;expanded.Expand(.002f);
            foreach(var vertex in mesh.vertices)Require(expanded.Contains(part.transform.TransformPoint(vertex)),"box encloses every actual transformed imported vertex");
            var box=pointer.BoxVisual(hand);Require(box&&box.useWorldSpace&&box.positionCount==16,"actual box visual draws all twelve edges in world coordinates");
            for(int i=0;i<box.positionCount;i++)
            {
                var corner=box.GetPosition(i);var min=expanded.min;var max=expanded.max;
                Require((Math.Abs(corner.x-min.x)<.00001||Math.Abs(corner.x-max.x)<.00001)
                    &&(Math.Abs(corner.y-min.y)<.00001||Math.Abs(corner.y-max.y)<.00001)
                    &&(Math.Abs(corner.z-min.z)<.00001||Math.Abs(corner.z-max.z)<.00001),"visual corner belongs to the current world bounding box");
            }
            string expected=string.IsNullOrEmpty(part.displayName)?part.stableId.Replace('_',' '):part.displayName;
            Require(WithoutWhitespace(pointer.LabelVisual(hand).text)==WithoutWhitespace(expected),"organ label preserves actual scene identity/display name across explicit line breaks");
        }
        static void SurfaceRay(Transform transform,Mesh mesh,out Vector3 origin,out Vector3 direction)
        {
            var vertices=mesh.vertices;var triangles=mesh.triangles;
            float maximumArea=0;Vector3 center=default,normal=default;
            for(int i=0;i<triangles.Length;i+=3)
            {
                var a=transform.TransformPoint(vertices[triangles[i]]);var b=transform.TransformPoint(vertices[triangles[i+1]]);var c=transform.TransformPoint(vertices[triangles[i+2]]);
                var candidate=Vector3.Cross(b-a,c-a);float area=candidate.sqrMagnitude;
                if(!float.IsFinite(area)||area<=maximumArea)continue;
                maximumArea=area;normal=candidate;center=(a+b+c)/3;
            }
            if(maximumArea<=1e-20f)throw new InvalidOperationException("Imported pointer fixture has no positive-area triangle.");
            // Vector3.Normalize treats sub-1e-5 cross-product magnitudes as zero, even
            // though a sub-mm triangle is valid. Normalize explicitly in this fixture.
            normal/=Mathf.Sqrt(maximumArea);origin=center+normal*.04f;direction=-normal;
        }
        static void SetReady(NativeWorkbench rig,bool ready)=>typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(rig,new object[]{ready});
        static string WithoutWhitespace(string value)=>new string(value.Where(character=>!char.IsWhiteSpace(character)).ToArray());
        static void Require(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException("Scene pointer regression: "+message);}
    }
}
