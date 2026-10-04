using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // Case-independent wall mechanics. Surgery consumes accepted geometry; this component never awards milestones.
    [DefaultExecutionOrder(115)]
    public sealed class NativeVolumeSimulation : MonoBehaviour
    {
        struct Blade {public InstrumentBehaviour tool;public Transform start,end;public Vector3 previousStart,previousEnd;public bool previous;}
        readonly List<Blade> blades=new List<Blade>();
        readonly int[] cutCounts=new int[OpenWallLayers.Count];
        sealed class LayerGrip
        {
            public string layer;
            public Vector3 baseline;
            public long stepAtAcquisition;
        }
        readonly Dictionary<int,LayerGrip> layerGrips=new Dictionary<int,LayerGrip>();
        readonly Dictionary<string,TissueInteractionProperties> layerProperties=new Dictionary<string,TissueInteractionProperties>(StringComparer.Ordinal);
        int nextGripToken;
        bool openLayers;
        public struct LayerHandleMeasurement
        {
            public string layerId;
            public Vector3 worldPosition, worldDisplacement;
            public float outwardLiftMillimeters;
            public bool hasAcceptedStep, lastStepAccepted;
            public int topologyRevision;
        }
        public struct LayerFracture
        {
            public string layerId, verb;
            public int newlyBrokenFaces, topologyRevision;
            public float fiberAngleDegrees;
        }
        // Mechanical facts only; the shared Surgery/CaseRunner path owns scoring.
        public event Action<LayerFracture> LayerFractured;
        NativeWorkbench workbench;
        Func<bool> ready;
        InstrumentBehaviour grasper;
        Vector3 handleOffset;
        Matrix4x4 lastFrame;
        bool frameValid;
        float clock,surfaceClock;
        double nextTiming;
        float peakFrameMs;
        public VolumetricTissue Wall {get;private set;}
        public float LastSolverMilliseconds {get;private set;}
        public float LastSurfaceMilliseconds {get;private set;}
        // Triangle points are Wall-local material metres: source atlas for the legacy coupon, wound-local for the open wall.
        public event Action<Vector3,Vector3,Vector3> BladeSwept;
        public void Initialize(Transform sourceFrame,NativeWorkbench rig,Func<bool> canInteract,bool openBodyLayers=false)
        {
            if(!sourceFrame||!rig) return;
            if(Wall){Wall.SetVisible(false);if(Application.isPlaying)Destroy(Wall.gameObject);else DestroyImmediate(Wall.gameObject);}
            openLayers=openBodyLayers;layerProperties.Clear();
            var wall=new GameObject(openLayers?"OpenAbdominalWall_TeachingPhysics":"GenericAbdominalWall_Unscored");wall.transform.SetParent(sourceFrame,false);
            Wall=wall.AddComponent<VolumetricTissue>();Wall.Initialize(openBodyLayers?TissueVolumeFactory.OpenAbdominalWall():TissueVolumeFactory.AbdominalWall(AppendixProjection(sourceFrame)));
            workbench=rig;ready=canInteract;blades.Clear();
            foreach(var tool in rig.tools??Array.Empty<InstrumentBehaviour>())
            {
                if(!tool||tool.action!=InstrumentAction.Cut)continue;
                Transform start=null,end=null;int starts=0,ends=0;
                foreach(var anchor in tool.GetComponentsInChildren<Transform>(true))
                {
                    if(anchor.GetComponentInParent<InstrumentBehaviour>()!=tool)continue;
                    if(anchor.name=="CutStart"){start=anchor;starts++;}
                    if(anchor.name=="CutEnd"){end=anchor;ends++;}
                }
                if(starts==1&&ends==1)blades.Add(new Blade{tool=tool,start=start,end=end});
                else Debug.LogWarning("SCALPAL_VOLUME_BLADE_UNAVAILABLE id="+tool.instrumentId);
            }
            ClearTransient();
        }
        // Wound-local volume is centered at its parent's McBurney origin supplied by Surgery.
        // Do not reproject it from atlas appendix bounds or claim measured ASIS landmarks.
        bool ValidLayerFrame(out float scale)
        {
            scale=0;
            if(!openLayers||!Wall||!isActiveAndEnabled||ready==null||!ready())return false;
            Matrix4x4 matrix=Wall.transform.localToWorldMatrix;
            Vector3 x=matrix.MultiplyVector(Vector3.right),y=matrix.MultiplyVector(Vector3.up),z=matrix.MultiplyVector(Vector3.forward);
            scale=x.magnitude;
            if(!TissueCage.Finite(Wall.transform.position)||!TissueCage.Finite(x)||!TissueCage.Finite(y)||!TissueCage.Finite(z)||scale<=1e-6f||
                Mathf.Abs(y.magnitude-scale)>scale*.001f||Mathf.Abs(z.magnitude-scale)>scale*.001f||
                Mathf.Abs(Vector3.Dot(x,y))>scale*scale*.001f||Mathf.Abs(Vector3.Dot(x,z))>scale*scale*.001f||
                Mathf.Abs(Vector3.Dot(y,z))>scale*scale*.001f||Vector3.Dot(Vector3.Cross(x,y),z)<=0)return false;
            if(frameValid&&matrix!=lastFrame)ClearTransient();
            lastFrame=matrix;frameValid=true;return true;
        }
        bool LayerReady(out float scale)
        {
            if(ValidLayerFrame(out scale))return true;
            ClearTransient();return false;
        }
        public bool TryContactLayer(string layerId,Vector3 worldPoint,float worldRadius,out Vector3 worldContact)
        {
            worldContact=Vector3.zero;
            if(!LayerReady(out float scale)||!TissueCage.Finite(worldPoint)||!OpenWallLayers.TryGet(layerId,out _)||
                !(worldRadius>0)||float.IsInfinity(worldRadius)||worldRadius>.01f)return false;
            if(!Wall.Volume.TryMaterialContact(layerId,Wall.transform.InverseTransformPoint(worldPoint),worldRadius/scale,out Vector3 local))return false;
            worldContact=Wall.transform.TransformPoint(local);return true;
        }
        public bool TryBeginLayerHandle(string layerId,Vector3 worldPoint,float worldRadius,out int token)
        {
            token=0;
            if(!TryContactLayer(layerId,worldPoint,worldRadius,out _)||nextGripToken==int.MaxValue)return false;
            int id=++nextGripToken;
            float scale=Wall.transform.localToWorldMatrix.MultiplyVector(Vector3.right).magnitude;
            // One authored material support footprint; exposure is determined by topology,
            // rather than shrinking the membrane grip to compensate for a welded interface.
            if(!Wall.Volume.BeginMaterialHandle(id,layerId,Wall.transform.InverseTransformPoint(worldPoint),worldRadius/scale)||
                !Wall.Volume.TryMaterialHandlePosition(id,out Vector3 local))return false;
            layerGrips.Add(id,new LayerGrip{layer=layerId,baseline=local,stepAtAcquisition=Wall.Volume.AcceptedStepSequence});token=id;return true;
        }
        public bool TrySetLayerHandleTarget(int token,Vector3 worldPoint)
        {
            if(!LayerReady(out _)||!layerGrips.ContainsKey(token)||!TissueCage.Finite(worldPoint))return false;
            return Wall.Volume.SetMaterialHandleTarget(token,Wall.transform.InverseTransformPoint(worldPoint));
        }
        public bool TryMeasureLayerHandle(int token,out LayerHandleMeasurement measurement)
        {
            measurement=default;
            if(!LayerReady(out _)||!layerGrips.TryGetValue(token,out var grip)||!Wall.Volume.TryMaterialHandlePosition(token,out Vector3 local))return false;
            Vector3 displacement=Wall.transform.TransformVector(local-grip.baseline);
            measurement=new LayerHandleMeasurement{layerId=grip.layer,worldPosition=Wall.transform.TransformPoint(local),
                worldDisplacement=displacement,outwardLiftMillimeters=Vector3.Dot(displacement,-Wall.transform.forward)*1000,
                hasAcceptedStep=Wall.Volume.AcceptedStepSequence>grip.stepAtAcquisition,lastStepAccepted=Wall.Volume.LastStepAccepted,topologyRevision=Wall.Volume.TopologyRevision};return true;
        }
        public void ReleaseLayerHandle(int token)
        { layerGrips.Remove(token);Wall?.Volume.ReleaseMaterialHandle(token); }
        // Metadata can be read before practice; it does not establish exposure or a valid registration.
        public bool TryGetLayerProperties(string layerId,out TissueInteractionProperties properties)
        {
            properties=default;
            if(!openLayers||!Wall||!OpenWallLayers.TryGet(layerId,out var layer))return false;
            properties=layerProperties.TryGetValue(layerId,out var bound)?bound:layer.properties;return true;
        }
        // Copy all five caller-owned semantic profiles atomically. No organ registry or default physiology.
        // A conflicting mechanical definition must be resolved by its owner instead of silently overriding the mesh.
        public bool TryBindLayerProperties(IReadOnlyList<TissueInteractionProperties> supplied,out string reason)
        {
            reason="";
            if(!openLayers||!Wall||supplied==null||supplied.Count!=OpenWallLayers.Count)
            {reason="Five initialized wall-layer profiles are required";return false;}
            var pending=new Dictionary<string,TissueInteractionProperties>(StringComparer.Ordinal);
            foreach(var properties in supplied)
            {
                if(!properties.IsValid||!properties.HasConsequenceProperties||!OpenWallLayers.TryGet(properties.id,out var layer)||
                    pending.ContainsKey(properties.id)||properties.layer!=layer.id||properties.order!=layer.index||
                    properties.cuttable!=layer.cuttable||properties.splittable!=layer.splittable||properties.tentable!=layer.tentable||
                    properties.hasFibers!=layer.hasFibers||layer.hasFibers&&Mathf.Abs(Vector3.Dot(properties.fiberDirection,layer.fiberDirection))<.9999f)
                {reason="Tissue properties must be complete and match each physical wall layer";return false;}
                pending.Add(properties.id,properties);
            }
            layerProperties.Clear();foreach(var pair in pending)layerProperties.Add(pair.Key,pair.Value);
            return true;
        }
        // Signed increase across the layer's fibers, measured from two accepted material points.
        public bool TryMeasureLayerSplit(int first,int second,out float increaseMillimeters)
        {
            increaseMillimeters=0;
            if(first==second||!TryMeasureLayerHandle(first,out var a)||!TryMeasureLayerHandle(second,out var b)||
                a.layerId!=b.layerId||!TryGetLayerProperties(a.layerId,out var properties)||!properties.splittable||
                !properties.hasFibers||!a.hasAcceptedStep||!b.hasAcceptedStep)return false;
            Vector3 localAxis=Vector3.Cross(Vector3.forward,properties.fiberDirection);
            if(localAxis.sqrMagnitude<1e-12f)return false;
            Vector3 axis=Wall.transform.TransformDirection(localAxis.normalized);
            Vector3 initial=Wall.transform.TransformVector(layerGrips[first].baseline-layerGrips[second].baseline);
            increaseMillimeters=(Mathf.Abs(Vector3.Dot(a.worldPosition-b.worldPosition,axis))-Mathf.Abs(Vector3.Dot(initial,axis)))*1000;return true;
        }
        // Compatibility with the existing consumer; neither method awards muscle milestones.
        public bool TryMeasureMuscleSplit(int first,int second,out float increaseMillimeters)
        {
            increaseMillimeters=0;
            return layerGrips.TryGetValue(first,out var a)&&a.layer=="muscle"&&
                layerGrips.TryGetValue(second,out var b)&&b.layer=="muscle"&&TryMeasureLayerSplit(first,second,out increaseMillimeters);
        }
        // Caller derives a finite material split surface from real paired tool contact/pull.
        public bool TrySplitLayer(string layerId,Vector3 worldA,Vector3 worldB,Vector3 worldC,out LayerFracture fracture)
        {
            fracture=default;
            if(!LayerReady(out float scale)||!TryGetLayerProperties(layerId,out var properties)||!properties.splittable||
                !properties.hasFibers||!TissueCage.Finite(worldA)||!TissueCage.Finite(worldB)||!TissueCage.Finite(worldC))return false;
            Vector3 a=Wall.transform.InverseTransformPoint(worldA),b=Wall.transform.InverseTransformPoint(worldB),c=Wall.transform.InverseTransformPoint(worldC);
            Vector3 normal=Vector3.Cross(b-a,c-a);
            Vector3 across=Vector3.Cross(Vector3.forward,properties.fiberDirection).normalized;
            // This wall separator supports planes along local fibers and wall depth, with authored geometric tolerance.
            if(normal.sqrMagnitude<1e-12f||across.sqrMagnitude<1e-12f||Mathf.Abs(Vector3.Dot(normal.normalized,across))<Mathf.Cos(25*Mathf.Deg2Rad))return false;
            return FractureLayer(layerId,"split",a,b,c,Vector3.Cross(Vector3.forward,normal),scale,out fracture);
        }
        public bool TrySplitMuscle(Vector3 worldA,Vector3 worldB,Vector3 worldC,out LayerFracture fracture)
            =>TrySplitLayer("muscle",worldA,worldB,worldC,out fracture);
        // Material-specific mechanical cut. Wrong-layer cutting is possible; cases own consequences.
        // Stroke direction is supplied separately from the swept triangle's blade edge.
        public bool TryCutLayer(string layerId,Vector3 worldA,Vector3 worldB,Vector3 worldC,Vector3 worldStrokeDirection,out LayerFracture fracture)
        {
            fracture=default;
            if(!LayerReady(out float scale)||!TryGetLayerProperties(layerId,out var properties)||!properties.cuttable||
                !TissueCage.Finite(worldA)||!TissueCage.Finite(worldB)||!TissueCage.Finite(worldC)||!TissueCage.Finite(worldStrokeDirection))return false;
            Vector3 a=Wall.transform.InverseTransformPoint(worldA),b=Wall.transform.InverseTransformPoint(worldB),c=Wall.transform.InverseTransformPoint(worldC);
            if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f)return false;
            return FractureLayer(layerId,"cut",a,b,c,Wall.transform.InverseTransformVector(worldStrokeDirection),scale,out fracture);
        }
        bool FractureLayer(string layerId,string verb,Vector3 a,Vector3 b,Vector3 c,Vector3 stroke,float scale,out LayerFracture fracture)
        {
            fracture=default;
            int count;
            if(verb=="split")
            {
                if(!OpenWallLayers.TryGet(layerId,out var layer)||layer.index+1>=OpenWallLayers.Count)return false;
                var underlying=OpenWallLayers.Get(layer.index+1);
                // Authored finite wound support margin in material metres, not measured
                // tissue adhesion. Release the underlying interface only with a real split;
                // the returned face count remains the split tissue's mechanical fact.
                count=Wall.Volume.SplitMaterialSweep(layerId,underlying.id,a,b,c,.0005f/scale,.035f);
            }
            else count=Wall.Volume.FractureMaterialSweep(layerId,a,b,c,.0005f/scale);
            if(count==0)return false;
            OpenWallLayers.TryFiberAngle(layerId,stroke,out float angle);
            fracture=new LayerFracture{layerId=layerId,verb=verb,newlyBrokenFaces=count,topologyRevision=Wall.Volume.TopologyRevision,fiberAngleDegrees=angle};
            LayerFractured?.Invoke(fracture);return true;
        }
        // This remains a generic anterior teaching coupon, not a laparoscopic port/incision model.
        static Vector2? AppendixProjection(Transform sourceFrame)
        {
            AnatomyPart appendix=null;
            foreach(var part in sourceFrame.GetComponentsInChildren<AnatomyPart>(true))
            {
                if(part.stableId!="appendix")continue;
                if(appendix)return null; // Ambiguous duplicate atlas: retain the standalone coupon position.
                appendix=part;
            }
            if(!appendix)return null;
            var filter=appendix.GetComponent<MeshFilter>();
            if(!filter||!filter.sharedMesh)return null;
            Vector3 center=sourceFrame.InverseTransformPoint(filter.transform.TransformPoint(filter.sharedMesh.bounds.center));
            return TissueCage.Finite(center)?new Vector2(center.x,center.y):(Vector2?)null;
        }
        bool ValidTool(InstrumentBehaviour tool)=>workbench&&tool&&Array.IndexOf(workbench.tools??Array.Empty<InstrumentBehaviour>(),tool)>=0&&
            tool.isActiveAndEnabled&&tool.Held&&tool.TrackingValid&&tool.Activation>=.7f;
        void LateUpdate()=>Simulate(Time.deltaTime);
        public void Simulate(float seconds)
        {
            double begin=Time.realtimeSinceStartupAsDouble;
            if(!Wall)return;
            LastSolverMilliseconds=LastSurfaceMilliseconds=0;
            bool valid=isActiveAndEnabled&&ready!=null&&ready()&&seconds>0&&!float.IsNaN(seconds)&&!float.IsInfinity(seconds);
            if(valid&&openLayers)valid=ValidLayerFrame(out _);
            Wall.SetVisible(valid);
            if(!valid){ClearTransient();return;}
            Matrix4x4 current=Wall.transform.localToWorldMatrix;
            if(frameValid&&current!=lastFrame)ClearTransient(); // A registration/origin change cannot become a knife sweep.
            lastFrame=current;frameValid=true;
            for(int i=0;i<blades.Count;i++)
            {
                var blade=blades[i];
                if(!ValidTool(blade.tool)||blade.tool.action!=InstrumentAction.Cut||!blade.start||!blade.end||!blade.start.gameObject.activeInHierarchy||!blade.end.gameObject.activeInHierarchy){blade.previous=false;blades[i]=blade;continue;}
                Vector3 start=Wall.transform.InverseTransformPoint(blade.start.position),end=Wall.transform.InverseTransformPoint(blade.end.position);
                float length=(end-start).magnitude;
                if(!TissueCage.Finite(start)||!TissueCage.Finite(end)||length<.002f||length>.06f){blade.previous=false;blades[i]=blade;continue;}
                if(blade.previous&&(start-blade.previousStart).magnitude<=.04f&&(end-blade.previousEnd).magnitude<=.04f)
                {
                    Vector3 stroke=(start+end-blade.previousStart-blade.previousEnd)*.5f;
                    ApplyBlade(blade.previousStart,blade.previousEnd,end,stroke);
                    ApplyBlade(blade.previousStart,end,start,stroke);
                }
                blade.previousStart=start;blade.previousEnd=end;blade.previous=true;blades[i]=blade;
            }
            if(grasper&&(!ValidTool(grasper)||!grasper.actionPoint))ReleaseHandle();
            if(!openLayers&&!grasper)FindHandle();
            clock=Mathf.Min(clock+Mathf.Clamp(seconds,0,.05f),.05f);
            int steps=0;
            double solverBegin=Time.realtimeSinceStartupAsDouble;
            while(clock>=1f/90&&steps++<4)
            {
                Wall.Volume.Step(1f/90,grasper?Wall.transform.InverseTransformPoint(grasper.actionPoint.position)+handleOffset:Vector3.zero,Vector3.zero);
                clock-=1f/90;
            }
            LastSolverMilliseconds=(float)((Time.realtimeSinceStartupAsDouble-solverBegin)*1000);
            // Numerical rejection retains the material handle and retries next tick.
            // Detach only on explicit loss/release or a topology cut invalidating its fan.
            if(grasper&&Wall.Volume.Handle<0)ReleaseHandle();
            surfaceClock+=Mathf.Clamp(seconds,0,.05f);
            LastSurfaceMilliseconds=0;
            if(surfaceClock>=1f/30)
            {double surfaceBegin=Time.realtimeSinceStartupAsDouble;Wall.CommitSurface();surfaceClock%=1f/30;
                LastSurfaceMilliseconds=(float)((Time.realtimeSinceStartupAsDouble-surfaceBegin)*1000);}
            peakFrameMs=Mathf.Max(peakFrameMs,(float)((Time.realtimeSinceStartupAsDouble-begin)*1000));
            if(Application.isPlaying&&Time.realtimeSinceStartupAsDouble>=nextTiming)
            {
                Debug.Log($"SCALPAL_NATIVE_VOLUME_TIMING cells={Wall.Volume.Cells.Length} nodes={Wall.Volume.NodeCount} cuts={Wall.Volume.CutFaceCount} graspNodes={Wall.Volume.HandleNodeCount} stepRetries={Wall.Volume.LastStepRetries} stepBacktracks={Wall.Volume.LastStepBacktracks} stepAccepted={Wall.Volume.LastStepAccepted} peakCpuMs={peakFrameMs:F2} solverCpuMs={LastSolverMilliseconds:F2} surfaceCpuMs={LastSurfaceMilliseconds:F2} burst={Wall.Volume.LastStepUsedBurst}");
                nextTiming=Time.realtimeSinceStartupAsDouble+5;peakFrameMs=0;
            }
        }
        void FindHandle()
        {
            foreach(var tool in workbench.tools??Array.Empty<InstrumentBehaviour>())
            {
                if(!ValidTool(tool)||!tool.actionPoint||(tool.action!=InstrumentAction.Grasp&&tool.action!=InstrumentAction.Retrieve))continue;
                Vector3 point=Wall.transform.InverseTransformPoint(tool.actionPoint.position);
                // Actual generated boundary/cut triangles; a distant bounding box is not contact.
                if(Wall.Volume.SurfaceDistanceSquared(point,.003f)>=.003f*.003f||!Wall.Volume.BeginHandle(point,.02f))continue;
                grasper=tool;handleOffset=Wall.Volume.HandlePosition-point;break;
            }
        }
        void ApplyBlade(Vector3 a,Vector3 b,Vector3 c,Vector3 stroke)
        {
            if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f)return;
            if(openLayers)for(int i=0;i<cutCounts.Length;i++)cutCounts[i]=Wall.Volume.CutFacesForMaterial(OpenWallLayers.Get(i).id);
            // Open-wall contact is a physical 0.5 mm tolerance in either presentation.
            // Legacy coupon retains its original material-coordinate calibration fixture.
            float tolerance=openLayers?.0005f/Wall.transform.localToWorldMatrix.MultiplyVector(Vector3.right).magnitude:.0005f;
            Wall.Volume.CutSweep(a,b,c,tolerance);BladeSwept?.Invoke(a,b,c);
            if(openLayers)for(int i=0;i<cutCounts.Length;i++)
            {
                var layer=OpenWallLayers.Get(i);int difference=Wall.Volume.CutFacesForMaterial(layer.id)-cutCounts[i];
                if(difference<=0)continue;
                OpenWallLayers.TryFiberAngle(layer.id,stroke,out float angle);
                LayerFractured?.Invoke(new LayerFracture{layerId=layer.id,verb="cut",newlyBrokenFaces=difference,
                    topologyRevision=Wall.Volume.TopologyRevision,fiberAngleDegrees=angle});
            }
        }
        void ReleaseHandle(){Wall?.Volume.ReleaseHandle();grasper=null;}
        void ClearTransient()
        {
            for(int i=0;i<blades.Count;i++){var blade=blades[i];blade.previous=false;blades[i]=blade;}
            ReleaseHandle();layerGrips.Clear();Wall?.Volume.Freeze();clock=surfaceClock=0;frameValid=false;
        }
        public void ResetTissues(){ClearTransient();if(Wall)Wall.ResetTissue();}
        void OnDisable(){ClearTransient();if(Wall)Wall.SetVisible(false);}
        void OnDestroy(){if(Wall){if(Application.isPlaying)Destroy(Wall.gameObject);else DestroyImmediate(Wall.gameObject);}}
    }
}
