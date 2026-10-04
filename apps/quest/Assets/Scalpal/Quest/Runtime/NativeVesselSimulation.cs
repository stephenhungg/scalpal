using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // Geometry-driven local bleeding demonstration; no scored event or physiological calibration.
    [DefaultExecutionOrder(120)]
    public sealed class NativeVesselSimulation : MonoBehaviour
    {
        public VesselBleeding Fluid {get;private set;}
        NativeVolumeSimulation cutting;
        NativeWorkbench rig;
        AnatomyController anatomy;
        AnatomyPart artery;
        MeshFilter vessel;
        MeshCollider vesselContact;
        DeformableTissue deformable;
        Vector3[] restVertices, staticVertices;
        int[] surfaceTriangles;
        readonly Dictionary<InstrumentBehaviour, InstrumentTipContact[]> tipCache = new Dictionary<InstrumentBehaviour, InstrumentTipContact[]>();
        public const float MaximumPoolIndicatorRadius = .04f;
        public const float PoolIndicatorDepth = .003f;
        Func<bool> ready;
        GameObject pool;
        Material blood;
        Vector3 injuryPoint;
        Vector3 injuryRestPoint;
        bool injured;
        float sourceUnitScale=1;
        const double OpeningArea=.000000785398; // Authored 0.5mm-radius opening, NOT measured cut area.
        const double SuctionMillilitersPerSecond=10; // Teaching assumption, not pump calibration.
        public void Initialize(AnatomyController controller,NativeWorkbench workbench,NativeVolumeSimulation volume,Func<bool> canInteract)
        {
            if(cutting)cutting.BladeSwept-=BladeSweep;
            DisposePool();tipCache.Clear();injured=false;injuryPoint=Vector3.zero;artery=null;vessel=null;vesselContact=null;
            anatomy=controller;rig=workbench;cutting=volume;ready=canInteract;Fluid=new VesselBleeding();
            if(!controller||!workbench||!controller.TryGetPart("appendicular_artery",out artery)||!volume)return;
            vessel=artery.GetComponent<MeshFilter>();vesselContact=artery.GetComponent<MeshCollider>();
            if(!vessel||!vesselContact||!vessel.sharedMesh||!vessel.sharedMesh.isReadable)return;
            deformable=artery.GetComponent<DeformableTissue>();
            restVertices=deformable&&deformable.SourceMesh?deformable.RestVertices:vessel.sharedMesh.vertices;
            staticVertices=restVertices; surfaceTriangles=deformable&&deformable.SourceMesh?deformable.SurfaceTriangles:vessel.sharedMesh.triangles;
            Vector3 scale=artery.transform.lossyScale,reference=controller.transform.lossyScale;
            sourceUnitScale=scale.x/reference.x;
            if(!TissueCage.Finite(scale)||!TissueCage.Finite(reference)||!(sourceUnitScale>0)||float.IsInfinity(sourceUnitScale)||
                Mathf.Abs(scale.y/reference.y-sourceUnitScale)>sourceUnitScale*.001f||Mathf.Abs(scale.z/reference.z-sourceUnitScale)>sourceUnitScale*.001f)return;
            pool=GameObject.CreatePrimitive(PrimitiveType.Sphere);pool.name="UnscoredBloodPool";pool.transform.SetParent(artery.transform,false);
            var collider=pool.GetComponent<Collider>();collider.enabled=false;Dispose(collider);
            blood=TissueRuntimeMaterial.Create("TeachingBlood_Uncalibrated",new Color(.32f,.006f,.009f));
            blood.SetFloat("_Glossiness",.8f);pool.GetComponent<Renderer>().sharedMaterial=blood;pool.SetActive(false);
            cutting.BladeSwept+=BladeSweep;
        }
        bool Valid=>isActiveAndEnabled&&rig&&ready!=null&&ready()&&anatomy&&anatomy.RegistrationValid&&artery&&artery.IsVisible&&artery.HasVisibleGeometry&&vesselContact&&vesselContact.enabled;
        void BladeSweep(Vector3 a,Vector3 b,Vector3 c)
        {
            if(!Valid||!cutting.Wall||!vessel||!vessel.sharedMesh)return;
            Transform frame=cutting.Wall.transform;
            a=artery.transform.InverseTransformPoint(frame.TransformPoint(a))*sourceUnitScale;
            b=artery.transform.InverseTransformPoint(frame.TransformPoint(b))*sourceUnitScale;
            c=artery.transform.InverseTransformPoint(frame.TransformPoint(c))*sourceUnitScale;
            if(!TissueCage.Finite(a)||!TissueCage.Finite(b)||!TissueCage.Finite(c)||Vector3.Cross(b-a,c-a).sqrMagnitude<1e-12f)return;
            var mesh=vessel.sharedMesh;var bounds=new Bounds(a,Vector3.zero);bounds.Encapsulate(b);bounds.Encapsulate(c);bounds.Expand(.001f);
            if(!bounds.Intersects(new Bounds(mesh.bounds.center*sourceUnitScale,mesh.bounds.size*sourceUnitScale)))return;
            var vertices=deformable&&deformable.Cage!=null?deformable.SurfaceVertices:staticVertices;var triangles=surfaceTriangles;
            for(int i=0;i<triangles.Length;i+=3)
            {
                var p=vertices[triangles[i]]*sourceUnitScale;var q=vertices[triangles[i+1]]*sourceUnitScale;var r=vertices[triangles[i+2]]*sourceUnitScale;
                if(!TissueVolume.BladeIntersectsFace(a,b,c,p,q,r,.0005f))continue;
                if(!injured)
                {
                    injuryPoint=TissueVolume.ClosestTriangle((a+b+c)/3,p,q,r);
                    Vector3 ab=q-p,ac=r-p,ap=injuryPoint-p;
                    float d00=Vector3.Dot(ab,ab),d01=Vector3.Dot(ab,ac),d11=Vector3.Dot(ac,ac),d20=Vector3.Dot(ap,ab),d21=Vector3.Dot(ap,ac),denominator=d00*d11-d01*d01;
                    if(Mathf.Abs(denominator)<1e-20f)continue;
                    float u=(d11*d20-d01*d21)/denominator,v=(d00*d21-d01*d20)/denominator;
                    injuryRestPoint=restVertices[triangles[i]]*(1-u-v)+restVertices[triangles[i+1]]*u+restVertices[triangles[i+2]]*v;
                }
                injured=true;Fluid.OpenInjury(OpeningArea);return;
            }
        }
        void LateUpdate()=>Simulate(Time.deltaTime);
        public void Simulate(float seconds)
        {
            if(Fluid==null||!pool)return;
            if(!Valid||!(seconds>0)||float.IsInfinity(seconds)){pool.SetActive(false);return;}
            float dt=Mathf.Min(seconds,.05f);
            if(injured&&deformable&&deformable.Cage!=null)injuryPoint=deformable.DeformSurfacePoint(injuryRestPoint)*sourceUnitScale;
            foreach(var tool in rig.tools??Array.Empty<InstrumentBehaviour>())
            {
                if(!tool||!tool.isActiveAndEnabled||!tool.Held||!tool.TrackingValid||!(tool.Activation>=.7f)||!tool.actionPoint)continue;
                Vector3 point=artery.transform.InverseTransformPoint(tool.actionPoint.position)*sourceUnitScale;
                if(!injured)continue;
                if((tool.action==InstrumentAction.Seal||tool.action==InstrumentAction.Clip)&&(point-injuryPoint).magnitude<=.012f&&TouchesVessel(tool))Fluid.SetOccluded(true);
                if(tool.action==InstrumentAction.Suction&&pool.activeSelf&&TouchesPool(tool.actionPoint.position,tool.contactRadius))
                    Fluid.RemovePool(SuctionMillilitersPerSecond*dt);
            }
            Fluid.Step(dt);
            bool show=injured&&Fluid.PooledMilliliters>.001;
            pool.SetActive(show);
            if(show)
            {
                // Bounded teaching indicator: the SI ledger remains authoritative; this
                // deliberately ceases to be volume preserving above its 4 cm radius cap.
                float radius=Mathf.Min(MaximumPoolIndicatorRadius,Mathf.Sqrt((float)(3*Fluid.PooledMilliliters*1e-6/(2*Math.PI*PoolIndicatorDepth))));
                Vector3 normal=Physics.gravity.sqrMagnitude>1e-10f?-Physics.gravity.normalized:Vector3.up;
                Vector3 worldInjury=artery.transform.TransformPoint(injuryPoint/sourceUnitScale);
                float worldScale=artery.transform.lossyScale.x/sourceUnitScale;
                pool.transform.position=worldInjury+normal*(PoolIndicatorDepth*.5f*worldScale);
                pool.transform.rotation=Quaternion.FromToRotation(Vector3.forward,normal);
                pool.transform.localScale=new Vector3(2*radius,2*radius,PoolIndicatorDepth)/sourceUnitScale;
            }
        }
        bool TouchesVessel(InstrumentBehaviour tool)
        {
            if(!tipCache.TryGetValue(tool,out var tips))
            { tips=tool.GetComponentsInChildren<InstrumentTipContact>(true);tipCache.Add(tool,tips); }
            foreach(var tip in tips)
            {
                if(!tip)continue;
                var shape=tip.GetComponent<Collider>();
                if(tip.GetComponentInParent<InstrumentBehaviour>()!=tool||!shape||!shape.enabled||!shape.isTrigger||!tip.isActiveAndEnabled)continue;
                if(Vector3.Distance(tip.transform.position,tool.actionPoint.position)>tool.contactRadius)continue;
                if(Physics.ComputePenetration(shape,shape.transform.position,shape.transform.rotation,vesselContact,vesselContact.transform.position,vesselContact.transform.rotation,out _,out _))return true;
            }
            return false;
        }
        bool TouchesPool(Vector3 worldPoint,float radius)
        {
            if(!TissueCage.Finite(worldPoint)||!(radius>0)||float.IsInfinity(radius)||radius>.05f)return false;
            Vector3 half=pool.transform.lossyScale*.5f+Vector3.one*radius;
            Vector3 delta=Quaternion.Inverse(pool.transform.rotation)*(worldPoint-pool.transform.position);
            if(half.x<=0||half.y<=0||half.z<=0)return false;
            return delta.x*delta.x/(half.x*half.x)+delta.y*delta.y/(half.y*half.y)+delta.z*delta.z/(half.z*half.z)<=1;
        }
        public void ResetTissues(){Fluid?.Reset();injured=false;injuryPoint=Vector3.zero;if(pool)pool.SetActive(false);}
        void OnDisable(){if(pool)pool.SetActive(false);}
        void DisposePool(){if(pool){pool.SetActive(false);Dispose(pool);}if(blood)Dispose(blood);pool=null;blood=null;}
        static void Dispose(UnityEngine.Object item){if(Application.isPlaying)Destroy(item);else DestroyImmediate(item);}
        void OnDestroy(){if(cutting)cutting.BladeSwept-=BladeSweep;DisposePool();}
    }
}
