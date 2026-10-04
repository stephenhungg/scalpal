using System;
using System.Collections.Generic;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Engine;
using Scalpal.Quest;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Accepted body events -> render-only ligatures and divided source anatomy. Contact, cage
    // meshes, IDs and the event log stay untouched. The separated specimen is an assisted
    // display, not a newly scored/grabbable body. No geometry is rebuilt on idle frames.
    [DisallowMultipleComponent, DefaultExecutionOrder(255)]
    public sealed class SurgicalActionAppearance : MonoBehaviour
    {
        sealed class Division
        {
            public AnatomyPart part; public MeshRenderer original, proximal, distal;
            public GameObject root; public Mesh proximalMesh, distalMesh; public Material material,cutMaterial;
            public bool suppressed; public TextMesh label;public float station;
            public GameObject ooze, highlight;
        }
        sealed class Tie {public AnatomyPart part;public GameObject root;public Material material;public string tissue;public float station;public GameObject highlight;}
        // The inspection highlight stays this long (attempt clock) after a dwell first completes on the tissue.
        public const double HighlightMs=3000;
        readonly Dictionary<string,double> inspectedAt=new Dictionary<string,double>();
        Material highlightMaterial,oozeMaterial;
        Mesh oozeMesh;
        readonly Dictionary<string,Division> divisions=new Dictionary<string,Division>();
        readonly Dictionary<string,Tie> ties=new Dictionary<string,Tie>();
        NativeCaseSession session;OpenBodyInteraction input;Transform wound;BodyState body;int cursor;
        public int TieCount=>ties.Count;
        public int VisibleTieCount {get{int count=0;foreach(var tie in ties.Values)if(tie.root&&tie.root.activeInHierarchy)count++;return count;}}
        public int DivisionCount=>divisions.Count;
        public int GeometryBuilds {get;private set;}
        public string Status {get;private set;}="Waiting for accepted surgery actions";
        public Mesh ProximalMesh(string id)=>divisions.TryGetValue(id,out var d)?d.proximalMesh:null;
        public Mesh DistalMesh(string id)=>divisions.TryGetValue(id,out var d)?d.distalMesh:null;
        public Transform DistalView(string id)=>divisions.TryGetValue(id,out var d)&&d.distal?d.distal.transform:null;
        public bool DivisionVisible(string id)=>divisions.TryGetValue(id,out var d)&&d.proximal&&d.proximal.enabled;
        // A divided mesoappendix oozes from its cut until both sides are tied (BodyState tiedBothSides).
        public bool OozeVisible {get{foreach(var d in divisions.Values)if(d.ooze&&d.ooze.activeInHierarchy)return true;return false;}}
        // The stump (appendix) or the tied vessels (mesoappendix) glow briefly once inspected.
        public bool HighlightVisible(string id)
        {
            if(divisions.TryGetValue(id,out var d)&&d.highlight&&d.highlight.activeInHierarchy)return true;
            foreach(var tie in ties.Values)if(tie.tissue==id&&tie.highlight&&tie.highlight.activeInHierarchy)return true;
            return false;
        }

        public void Initialize(NativeCaseSession owner,OpenBodyInteraction interaction,Transform incision)
        {Dispose();session=owner;input=interaction;wound=incision;GeometryBuilds=0;Refresh();}
        public void Refresh()
        {
            var current=session&&session.exercise?session.exercise.Body:null;
            if(current!=body){ClearViews();body=current;cursor=0;}
            if(body==null||input==null||!wound)return;
            while(cursor<body.Log.Count)
            {
                var record=body.Log[cursor++];
                var inspect=record?.action;
                if(inspect!=null&&inspect.verb=="inspect"&&inspect.durationMs>=1000&&!inspectedAt.ContainsKey(inspect.tissueId))inspectedAt[inspect.tissueId]=inspect.timeMs;
                if(!Accepted(record)||!Resolve(record.action.tissueId,out var target,out var part))continue;
                if(record.action.verb=="tie")BuildTie(record.action,target,part);
                else if(record.action.verb=="cut"&&(record.action.tissueId=="mesoappendix"||record.action.tissueId=="appendix"))
                    BuildDivision(record.action,target,part);
            }
            bool closed=body.Get("skin","closed")>0;
            double now=input.TimeMs;
            foreach(var d in divisions.Values)
            {
                if(!d.original||!d.proximal||!d.distal)continue;
                bool visible=Visible(d.part)&&!closed;
                d.proximal.enabled=d.distal.enabled=visible;
                string id=d.part?d.part.stableId:"";
                if(d.ooze)d.ooze.SetActive(visible&&body.Get(id,"tiedBothSides")==0);
                if(d.highlight)d.highlight.SetActive(visible&&Recent(id,now));
                d.original.forceRenderingOff=visible||d.suppressed;
                if(d.label){d.label.gameObject.SetActive(visible);var head=session.workbench?session.workbench.headCamera:null;if(head)d.label.transform.rotation=Quaternion.LookRotation(d.label.transform.position-head.transform.position,Vector3.up);}
            }
            foreach(var tie in ties.Values)
            {
                if(tie.root)tie.root.SetActive(Visible(tie.part)&&!closed);
                if(tie.highlight)tie.highlight.SetActive(Recent(tie.tissue,now));
            }
        }
        bool Recent(string id,double now)=>inspectedAt.TryGetValue(id,out var at)&&now>=at&&now-at<HighlightMs;
        Material Highlight()
        {
            // Drawn over the delivered caecum (Scalpal/InspectionHighlight), so the inspected stump and vessels stay findable.
            if(!highlightMaterial){var template=Resources.Load<Material>("InspectionHighlight");
                highlightMaterial=template&&template.shader?new Material(template){name="InspectionHighlight"}:TissueRuntimeMaterial.Create("InspectionHighlight",new Color(1f,.84f,.22f));}
            return highlightMaterial;
        }
        // A flat band around a section (inner radii ru/rv, given width), in the part's local space.
        void Band(Transform parent,Vector3 center,Vector3 u,Vector3 v,float ru,float rv,float width)
        {
            const int segments=48;var vertices=new Vector3[segments*2];var triangles=new int[segments*6];
            for(int i=0;i<segments;i++)
            {
                float angle=i*Mathf.PI*2/segments;Vector3 dir=u*Mathf.Cos(angle)*ru+v*Mathf.Sin(angle)*rv,unit=dir.normalized;
                vertices[i*2]=center+dir;vertices[i*2+1]=center+dir+unit*width;
                int a=i*2,b=((i+1)%segments)*2;triangles[i*6]=a;triangles[i*6+1]=b;triangles[i*6+2]=a+1;triangles[i*6+3]=a+1;triangles[i*6+4]=b;triangles[i*6+5]=b+1;
            }
            var mesh=new Mesh{name="InspectionHighlightBand",vertices=vertices,triangles=triangles};mesh.RecalculateBounds();bands.Add(mesh);
            var go=new GameObject("Band");go.transform.SetParent(parent,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=Highlight();renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        readonly List<Mesh> bands=new List<Mesh>();
        bool Visible(AnatomyPart part)=>isActiveAndEnabled&&session&&session.anatomy&&session.anatomy.RegistrationValid
            &&part&&part.IsVisible&&part.gameObject.activeInHierarchy&&!part.IsGhosted;
        static bool Accepted(BodyRecord record)
        {
            if(record?.action==null||!record.action.registered||record.action.choice=="longitudinal_unmeasured")return false;
            foreach(var outcome in record.outcomes??Array.Empty<string>())
                if(outcome=="not_exposed"||outcome=="no_cut"||outcome=="not_cuttable"||outcome=="missing_instance")return false;
            return record.action.verb=="tie"||record.action.verb=="cut";
        }
        bool Resolve(string id,out SurgeryTissueTarget target,out AnatomyPart part)
        {
            target=null;part=null;
            foreach(var candidate in input.Targets)if(candidate&&candidate.tissueId==id){if(target)return false;target=candidate;}
            return target&&target.HasBase&&session.anatomy.TryGetPart(id,out part)&&part.GetComponent<MeshFilter>()&&part.GetComponent<MeshRenderer>();
        }
        static bool Source(AnatomyPart part,out Mesh mesh,out Vector3[] vertices,out int[] triangles)
        {
            mesh=part.GetComponent<MeshFilter>().sharedMesh;vertices=null;triangles=null;
            if(!mesh||!mesh.isReadable||mesh.vertexCount>65536)return false;
            vertices=mesh.vertices;triangles=mesh.triangles;
            return triangles.Length>0&&triangles.Length<=32768*3;
        }
        void Plane(SurgeryTissueTarget target,Scalpal.Exercises.Data.BodyAction action,out Vector3 point,out Vector3 normal)
        {
            Vector3 world=session.patientFrame.TransformPoint(new Vector3(action.position.x,action.position.y,action.position.z));
            // DistanceFromBase reads the current cage-embedded base reference, so derive the
            // station from that measurement rather than using the un-deformed anchor position.
            if(target.DistanceFromBase(world,out float measured))world+=target.LongitudinalWorld*((float)action.distanceMm-measured)*.001f;
            else world=target.basePoint.position+target.LongitudinalWorld*(float)action.distanceMm*.001f;
            point=target.transform.InverseTransformPoint(world);
            normal=target.transform.localToWorldMatrix.transpose.MultiplyVector(target.LongitudinalWorld).normalized;
        }
        static List<Vector3> Section(Vector3[] v,int[] triangles,Vector3 point,Vector3 normal,float epsilon)
        {
            var result=new List<Vector3>();
            for(int i=0;i<triangles.Length;i+=3)for(int edge=0;edge<3;edge++)
            {
                Vector3 a=v[triangles[i+edge]],b=v[triangles[i+(edge+1)%3]];
                float da=Vector3.Dot(a-point,normal),db=Vector3.Dot(b-point,normal);
                if((da>0&&db>0)||(da<0&&db<0)||Mathf.Abs(da-db)<1e-12f)continue;
                Vector3 hit=Vector3.LerpUnclamped(a,b,da/(da-db));bool duplicate=false;
                foreach(var prior in result)if((prior-hit).sqrMagnitude<epsilon*epsilon){duplicate=true;break;}
                if(!duplicate)result.Add(hit);
                if(result.Count>1024)return new List<Vector3>();
            }
            return result;
        }
        static void Axes(Vector3 normal,out Vector3 u,out Vector3 v)
        {u=Vector3.Cross(normal,Mathf.Abs(normal.y)<.9f?Vector3.up:Vector3.right).normalized;v=Vector3.Cross(normal,u);}
        void BuildTie(Scalpal.Exercises.Data.BodyAction action,SurgeryTissueTarget target,AnatomyPart part)
        {
            string key=action.tissueId+":"+Mathf.RoundToInt((float)action.distanceMm);
            if(ties.ContainsKey(key)||ties.Count>=16||!Source(part,out _,out var vertices,out var triangles))return;
            Plane(target,action,out var point,out var normal);
            float scale=part.transform.TransformVector(Vector3.right).magnitude;if(scale<=0)return;
            var section=Section(vertices,triangles,point,normal,.0001f/scale);if(section.Count<3){Status="Ligature section unavailable: "+action.tissueId;return;}
            Axes(normal,out var u,out var v);Vector3 center=Vector3.zero;foreach(var x in section)center+=x;center/=section.Count;
            float ru=0,rv=0;foreach(var x in section){ru=Mathf.Max(ru,Mathf.Abs(Vector3.Dot(x-center,u)));rv=Mathf.Max(rv,Mathf.Abs(Vector3.Dot(x-center,v)));}
            ru=Mathf.Clamp(ru+.00025f/scale,.0008f/scale,.04f/scale);rv=Mathf.Clamp(rv+.00025f/scale,.0008f/scale,.04f/scale);
            var root=new GameObject("AcceptedLigature_"+key);root.transform.SetParent(part.transform,false);
            if(divisions.TryGetValue(action.tissueId,out var divided)&&action.distanceMm>divided.station)root.transform.localPosition=divided.distal.transform.localPosition;
            var material=TissueRuntimeMaterial.Create("SutureLigature",new Color(.82f,.77f,.61f));material.SetFloat("_Glossiness",.24f);
            var loop=new Vector3[49];for(int i=0;i<loop.Length;i++){float angle=i*Mathf.PI*2/48;loop[i]=center+u*(Mathf.Cos(angle)*ru)+v*(Mathf.Sin(angle)*rv);}
            Line(root.transform,"LigatureLoop",loop,material,.00065f/scale);
            Vector3 knot=center+u*ru;
            Line(root.transform,"SquareKnot",new[]{knot,knot+v*.001f/scale,knot+u*.001f/scale,knot-v*.001f/scale,knot},material,.0008f/scale);
            Line(root.transform,"TailA",new[]{knot,knot+(u+v)*.003f/scale},material,.00055f/scale);
            Line(root.transform,"TailB",new[]{knot,knot+(u-v)*.003f/scale},material,.00055f/scale);
            // Vessel ties glow when the mesoappendix is inspected; the base is marked by its stump instead.
            GameObject glow=null;
            if(action.tissueId!="appendix"){glow=new GameObject("InspectionHighlight");glow.transform.SetParent(root.transform,false);glow.AddComponent<SurgicalVisualGeometry>();
                Band(glow.transform,center,u,v,ru+.0025f/scale,rv+.0025f/scale,.0025f/scale);glow.SetActive(false);}
            ties.Add(key,new Tie{part=part,root=root,material=material,tissue=action.tissueId,station=(float)action.distanceMm,highlight=glow});GeometryBuilds++;Status="Accepted ligature rendered";
        }
        static void Line(Transform parent,string name,Vector3[] points,Material material,float width)
        {
            var go=new GameObject(name);go.transform.SetParent(parent,false);var line=go.AddComponent<LineRenderer>();
            line.useWorldSpace=false;line.positionCount=points.Length;line.SetPositions(points);line.startWidth=line.endWidth=width;
            line.numCapVertices=3;line.numCornerVertices=3;line.sharedMaterial=material;
        }
        void BuildDivision(Scalpal.Exercises.Data.BodyAction action,SurgeryTissueTarget target,AnatomyPart part)
        {
            if(!Source(part,out var source,out var vertices,out var triangles))return;
            Plane(target,action,out var point,out var normal);
            float scale=part.transform.TransformVector(Vector3.right).magnitude;if(scale<=0)return;
            var section=Section(vertices,triangles,point,normal,.0001f/scale);
            var sourceNormals=source.normals;
            var proximal=Clip(vertices,triangles,point,normal,true,section,sourceNormals);
            var distal=Clip(vertices,triangles,point,normal,false,section,sourceNormals);
            if(proximal.vertexCount<3||distal.vertexCount<3||section.Count<3)
            {Release(proximal);Release(distal);Status="Division section unavailable: "+action.tissueId;return;}
            if(divisions.TryGetValue(action.tissueId,out var old)){Remove(old);divisions.Remove(action.tissueId);}
            var root=new GameObject("AcceptedDivision_"+action.tissueId);root.transform.SetParent(part.transform,false);root.AddComponent<SurgicalVisualGeometry>();
            var original=part.GetComponent<MeshRenderer>();Color color=new Color(.7f,.37f,.33f);
            if(original.sharedMaterial&&original.sharedMaterial.HasProperty("_Color"))color=original.sharedMaterial.color;
            // Keep a presentation tint (the inflamed appendix) on both divided pieces.
            var tint=new MaterialPropertyBlock();original.GetPropertyBlock(tint);
            if(!tint.isEmpty&&tint.GetColor("_BaseColor").a>0)color=tint.GetColor("_BaseColor");
            var material=TissueRuntimeMaterial.Create("Divided_"+action.tissueId,color);material.SetFloat("_Glossiness",.57f);
            var cutMaterial=TissueRuntimeMaterial.Create("FreshCutFace",new Color(.52f,.14f,.15f));cutMaterial.SetFloat("_Glossiness",.64f);
            var near=Draw(root.transform,"RetainedSourceStump",proximal,material,cutMaterial);var far=Draw(root.transform,"DetachedSourceSpecimen",distal,material,cutMaterial);
            far.transform.localPosition=part.transform.InverseTransformVector(-wound.forward*.02f);
            foreach(var tie in ties.Values)if(tie.tissue==action.tissueId)tie.root.transform.localPosition=tie.station>action.distanceMm?far.transform.localPosition:Vector3.zero;
            TextMesh label=null;
            if(action.tissueId=="appendix")
            {
                var caption=new GameObject("AssistedSpecimenNotice");caption.transform.SetParent(far.transform,false);
                caption.transform.localPosition=distal.bounds.center+part.transform.InverseTransformVector(Vector3.up*.025f);
                label=caption.AddComponent<TextMesh>();label.text="Specimen\nAssisted display";label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                // Match the native panel's metric text sizing. A single unscaled 26-character
                // caption covered the incision field in the actual learner-eye render.
                label.GetComponent<MeshRenderer>().sharedMaterial=label.font.material;label.fontSize=48;
                label.characterSize=.004f*14f/label.fontSize/scale;label.anchor=TextAnchor.MiddleCenter;label.color=Color.white;
            }
            // The retained cut face: a small ooze until tied (mesoappendix) and a ring for the brief inspection highlight.
            Vector3 capCenter=Vector3.zero;foreach(var p in section)capCenter+=p;capCenter/=section.Count;
            Axes(normal,out var cu,out var cv);float cru=0,crv=0;
            foreach(var p in section){cru=Mathf.Max(cru,Mathf.Abs(Vector3.Dot(p-capCenter,cu)));crv=Mathf.Max(crv,Mathf.Abs(Vector3.Dot(p-capCenter,cv)));}
            GameObject ooze=null;
            if(action.tissueId=="mesoappendix")
            {
                ooze=new GameObject("CutOoze");ooze.transform.SetParent(near.transform,false);
                ooze.transform.localPosition=capCenter;ooze.transform.localScale=Vector3.one*(.0065f/scale);
                ooze.AddComponent<MeshFilter>().sharedMesh=Drop();
                if(!oozeMaterial){oozeMaterial=TissueRuntimeMaterial.Create("CutOoze",new Color(.52f,.01f,.01f));oozeMaterial.SetFloat("_Glossiness",.94f);}
                ooze.AddComponent<MeshRenderer>().sharedMaterial=oozeMaterial;ooze.SetActive(false);
            }
            GameObject highlight=null;
            if(action.tissueId=="appendix"){highlight=new GameObject("InspectionHighlight");highlight.transform.SetParent(near.transform,false);
                Band(highlight.transform,capCenter,cu,cv,cru+.003f/scale,crv+.003f/scale,.0025f/scale);highlight.SetActive(false);}
            divisions.Add(action.tissueId,new Division{part=part,original=original,root=root,proximal=near,distal=far,proximalMesh=proximal,distalMesh=distal,material=material,cutMaterial=cutMaterial,suppressed=original.forceRenderingOff,label=label,station=(float)action.distanceMm,ooze=ooze,highlight=highlight});
            GeometryBuilds++;Status="Accepted source-mesh division rendered; specimen placement assisted";
        }
        // A unit-radius flattened bead of blood (scaled per use).
        Mesh Drop()
        {
            if(oozeMesh)return oozeMesh;
            const int around=12,up=5;var vertices=new List<Vector3>();var triangles=new List<int>();
            for(int j=0;j<=up;j++)for(int i=0;i<=around;i++)
            {float a=i*Mathf.PI*2/around,b=j*Mathf.PI*.5f/up;vertices.Add(new Vector3(Mathf.Cos(a)*Mathf.Cos(b),Mathf.Sin(a)*Mathf.Cos(b),-.6f*Mathf.Sin(b)));}
            for(int j=0;j<up;j++)for(int i=0;i<around;i++){int a=j*(around+1)+i,b=a+around+1;triangles.AddRange(new[]{a,b,b+1,a,b+1,a+1});}
            oozeMesh=new Mesh{name="CutOozeBead"};oozeMesh.SetVertices(vertices);oozeMesh.SetTriangles(triangles,0);oozeMesh.RecalculateNormals();oozeMesh.RecalculateBounds();
            return oozeMesh;
        }
        static MeshRenderer Draw(Transform parent,string name,Mesh mesh,Material material,Material cutMaterial)
        {var go=new GameObject(name);go.transform.SetParent(parent,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterials=new[]{material,cutMaterial};return renderer;}
        public static Mesh Clip(Vector3[] source,int[] faces,Vector3 point,Vector3 normal,bool proximal,IReadOnlyList<Vector3> section,Vector3[] sourceNormals=null)
        {
            var vertices=new List<Vector3>();var triangles=new List<int>();var caps=new List<int>();var normals=new List<Vector3>();
            bool smooth=sourceNormals!=null&&sourceNormals.Length==source.Length;
            for(int i=0;i<faces.Length;i+=3)
            {
                var polygon=new List<Vector3>();var polygonNormals=new List<Vector3>();
                for(int edge=0;edge<3;edge++)
                {
                    Vector3 a=source[faces[i+edge]],b=source[faces[i+(edge+1)%3]];
                    float da=Vector3.Dot(a-point,normal),db=Vector3.Dot(b-point,normal);
                    bool inside=proximal?da<=0:da>=0,next=proximal?db<=0:db>=0;
                    if(inside){polygon.Add(a);if(smooth)polygonNormals.Add(sourceNormals[faces[i+edge]]);}
                    if(inside!=next)
                    {
                        float fraction=da/(da-db);polygon.Add(Vector3.LerpUnclamped(a,b,fraction));
                        if(smooth)polygonNormals.Add(Vector3.LerpUnclamped(sourceNormals[faces[i+edge]],sourceNormals[faces[i+(edge+1)%3]],fraction).normalized);
                    }
                }
                int start=vertices.Count;vertices.AddRange(polygon);if(smooth)normals.AddRange(polygonNormals);
                for(int j=1;j<polygon.Count-1;j++)triangles.AddRange(new[]{start,start+j,start+j+1});
            }
            if(section!=null&&section.Count>=3)
            {
                Vector3 center=Vector3.zero;foreach(var p in section)center+=p;center/=section.Count;
                Axes(normal,out var u,out var v);var ring=new List<Vector3>(section);
                ring.Sort((a,b)=>Mathf.Atan2(Vector3.Dot(a-center,v),Vector3.Dot(a-center,u)).CompareTo(Mathf.Atan2(Vector3.Dot(b-center,v),Vector3.Dot(b-center,u))));
                int pole=vertices.Count;vertices.Add(center);vertices.AddRange(ring);
                if(smooth)for(int i=0;i<=ring.Count;i++)normals.Add(proximal?normal:-normal);
                for(int i=0;i<ring.Count;i++)
                {int a=pole+1+i,b=pole+1+(i+1)%ring.Count;caps.AddRange(proximal?new[]{pole,a,b}:new[]{pole,b,a});}
            }
            var mesh=new Mesh{name=proximal?"ActualSourceProximalWithCap":"ActualSourceDistalWithCap"};
            if(vertices.Count>65535)mesh.indexFormat=UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);mesh.subMeshCount=2;mesh.SetTriangles(triangles,0);mesh.SetTriangles(caps,1);
            if(smooth)mesh.SetNormals(normals);else mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
        void LateUpdate()=>Refresh();
        void OnDisable(){foreach(var d in divisions.Values){if(d.ooze)d.ooze.SetActive(false);if(d.highlight)d.highlight.SetActive(false);if(d.proximal)d.proximal.enabled=false;if(d.distal)d.distal.enabled=false;if(d.original)d.original.forceRenderingOff=d.suppressed;if(d.label)d.label.gameObject.SetActive(false);}foreach(var t in ties.Values)if(t.root)t.root.SetActive(false);}
        void OnDestroy()=>Dispose();
        public void Dispose(){ClearViews();body=null;cursor=0;session=null;input=null;wound=null;Release(highlightMaterial);Release(oozeMaterial);Release(oozeMesh);highlightMaterial=oozeMaterial=null;oozeMesh=null;}
        void ClearViews(){inspectedAt.Clear();foreach(var band in bands)Release(band);bands.Clear();foreach(var d in divisions.Values)Remove(d);divisions.Clear();foreach(var t in ties.Values){if(t.root)t.root.SetActive(false);Release(t.root);Release(t.material);}ties.Clear();}
        static void Remove(Division d){if(d.original)d.original.forceRenderingOff=d.suppressed;if(d.root)d.root.SetActive(false);Release(d.root);Release(d.proximalMesh);Release(d.distalMesh);Release(d.material);Release(d.cutMaterial);}
        static void Release(UnityEngine.Object item){if(!item)return;if(Application.isPlaying)Destroy(item);else DestroyImmediate(item);}
    }
}
