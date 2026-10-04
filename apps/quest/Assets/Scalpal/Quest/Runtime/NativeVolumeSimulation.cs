using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // Local mechanics, deliberately unscored: the existing case has no incision rubric.
    [DefaultExecutionOrder(115)]
    public sealed class NativeVolumeSimulation : MonoBehaviour
    {
        struct Blade {public InstrumentBehaviour tool;public Transform start,end;public Vector3 previousStart,previousEnd;public bool previous;}
        readonly List<Blade> blades=new List<Blade>();
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
        // Triangle points use the shared source atlas frame in meters, not patientFrame axes.
        public event Action<Vector3,Vector3,Vector3> BladeSwept;
        public void Initialize(Transform sourceFrame,NativeWorkbench rig,Func<bool> canInteract)
        {
            if(!sourceFrame||!rig) return;
            if(Wall){Wall.SetVisible(false);if(Application.isPlaying)Destroy(Wall.gameObject);else DestroyImmediate(Wall.gameObject);}
            var wall=new GameObject("GenericAbdominalWall_Unscored");wall.transform.SetParent(sourceFrame,false);
            Wall=wall.AddComponent<VolumetricTissue>();Wall.Initialize(TissueVolumeFactory.AbdominalWall(AppendixProjection(sourceFrame)));
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
            bool valid=isActiveAndEnabled&&ready!=null&&ready()&&seconds>0&&!float.IsNaN(seconds)&&!float.IsInfinity(seconds);
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
                    ApplyBlade(blade.previousStart,blade.previousEnd,end);
                    ApplyBlade(blade.previousStart,end,start);
                }
                blade.previousStart=start;blade.previousEnd=end;blade.previous=true;blades[i]=blade;
            }
            if(grasper&&(!ValidTool(grasper)||!grasper.actionPoint))ReleaseHandle();
            if(!grasper)FindHandle();
            clock=Mathf.Min(clock+Mathf.Clamp(seconds,0,.05f),.05f);
            int steps=0;
            while(clock>=1f/90&&steps++<4)
            {
                Wall.Volume.Step(1f/90,grasper?Wall.transform.InverseTransformPoint(grasper.actionPoint.position)+handleOffset:Vector3.zero,Vector3.zero);
                clock-=1f/90;
            }
            // Numerical rejection retains the material handle and retries next tick.
            // Detach only on explicit loss/release or a topology cut invalidating its fan.
            if(grasper&&Wall.Volume.Handle<0)ReleaseHandle();
            surfaceClock+=Mathf.Clamp(seconds,0,.05f);
            if(surfaceClock>=1f/30){Wall.CommitSurface();surfaceClock%=1f/30;}
            peakFrameMs=Mathf.Max(peakFrameMs,(float)((Time.realtimeSinceStartupAsDouble-begin)*1000));
            if(Application.isPlaying&&Time.realtimeSinceStartupAsDouble>=nextTiming)
            {
                Debug.Log($"SCALPAL_NATIVE_VOLUME_TIMING cells={Wall.Volume.Cells.Length} nodes={Wall.Volume.NodeCount} cuts={Wall.Volume.CutFaceCount} graspNodes={Wall.Volume.HandleNodeCount} stepRetries={Wall.Volume.LastStepRetries} stepBacktracks={Wall.Volume.LastStepBacktracks} stepAccepted={Wall.Volume.LastStepAccepted} peakCpuMs={peakFrameMs:F2}");
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
        void ApplyBlade(Vector3 a,Vector3 b,Vector3 c)
        {
            if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f)return;
            Wall.Volume.CutSweep(a,b,c,.0005f);BladeSwept?.Invoke(a,b,c);
        }
        void ReleaseHandle(){Wall?.Volume.ReleaseHandle();grasper=null;}
        void ClearTransient()
        {
            for(int i=0;i<blades.Count;i++){var blade=blades[i];blade.previous=false;blades[i]=blade;}
            ReleaseHandle();Wall?.Volume.Freeze();clock=surfaceClock=0;frameValid=false;
        }
        public void ResetTissues(){ClearTransient();if(Wall)Wall.ResetTissue();}
        void OnDisable(){ClearTransient();if(Wall)Wall.SetVisible(false);}
        void OnDestroy(){if(Wall){if(Application.isPlaying)Destroy(Wall.gameObject);else DestroyImmediate(Wall.gameObject);}}
    }
}
