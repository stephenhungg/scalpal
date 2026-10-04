using System;
using System.Linq;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Direct body facts drive the production presentation. This checks semantic teaching
    // geometry, not tool contact, fracture topology, medical anatomy or a headset render.
    public static class OpenWoundAppearanceValidation
    {
        static int checks;
        static void Require(bool value,string message)
        {checks++;if(!value)throw new InvalidOperationException("Wound appearance: "+message);}
        [MenuItem("Scalpal/Surgery/Validate Open Wound Appearance")]
        public static void Run()
        {
            checks=0;
            var template=Resources.Load<Material>("OpenTissueSurface");
            Require(template&&template.shader&&template.shader.name=="Scalpal/OpenTissueSurface","serialized Resources material references the dedicated retained shader");
            var asset=Resources.Load<TextAsset>("scalpal_bundle");var bundle=JsonUtility.FromJson<ScalpalBundle>(asset.text);
            var plan=bundle.procedures.Single(p=>p.id=="open_appendectomy").openBody;
            var body=new BodyState(plan.tissues);
            var root=new GameObject("SyntheticWoundAppearance");
            float oldWindow=Shader.GetGlobalFloat("_ScalpalWoundWindow");var oldOpening=Shader.GetGlobalVector("_ScalpalWoundOpening");
            var oldAxis=Shader.GetGlobalVector("_ScalpalWoundAxis");var oldMatrix=Shader.GetGlobalMatrix("_ScalpalWoundWorldToLocal");
            try
            {
                var wound=root.AddComponent<OpenWoundView>();wound.Build();wound.Apply(body);
                Require(!wound.RenderingWound&&!wound.HasSkinOpening&&Shader.GetGlobalFloat("_ScalpalWoundWindow")==0,"initial skin has no coupon or cutaway window");
                Require(root.GetComponentsInChildren<MeshRenderer>().Length==0,"initial incision layer surfaces are actually inactive");
                var centre=new Vector2(.005f,-.003f);var axis=new Vector2(.8f,.6f);
                wound.SetMarker(new[]{new Vector3(centre.x-axis.x*.03f,centre.y-axis.y*.03f,0),new Vector3(centre.x+axis.x*.03f,centre.y+axis.y*.03f,0)});
                body.Set("skin","marked",1);body.Set("skin","markLengthMm",60);wound.Apply(body);
                Require(!wound.RenderingWound&&!wound.HasSkinOpening&&Shader.GetGlobalFloat("_ScalpalWoundWindow")==0,"marking never opens skin or exposes the underlying full wall rectangle");
                Require((wound.IncisionAxisLocal-axis).magnitude<1e-5f,"registered actual marker direction controls incision orientation");
                body.Set("skin","opened",1);body.Set("skin","cutLengthMm",20);wound.Apply(body);
                Require(wound.RenderingWound&&wound.HasSkinOpening&&wound.IsLayerVisible(0)&&wound.IsLayerVisible(1)&&!wound.IsLayerVisible(2),"skin incision exposes yellow fat floor while fascia and deeper layers remain hidden");
                Require(Mathf.Abs(wound.SkinOpeningLocal.z-.012f)<1e-6f,"20 mm measured skin incision produces a local24 mm rim instead of a120 mm coupon");
                Require(Mathf.Abs(wound.SkinOpeningLocal.x-centre.x)<1e-5f&&Mathf.Abs(wound.SkinOpeningLocal.y-centre.y)<1e-5f,"actual marker centre is retained");
                Require(Shader.GetGlobalFloat("_ScalpalWoundWindow")==1&&(Shader.GetGlobalVector("_ScalpalWoundOpening")-wound.SkinOpeningLocal).magnitude<1e-6f,"patient skin cutaway exactly receives the current finite wound envelope");
                Require(Mathf.Abs(LayerGap(wound,"skin")-.018f)<1e-6f,"initial teaching incision has a readable18 mm aperture while cut length remains measured");
                Require(!wound.RenderingCavity,"an unopened membrane never reveals an illustrative cavity");
                MeshBounds(wound);
                var skin=Filter(wound,"skinRightLip").sharedMesh;var firstExtent=skin.bounds.size.magnitude;
                Require(skin.vertexCount==65*5,"compatibility mesh layout remains available to existing preview validators");
                var fat=Filter(wound,"fatRightLip");
                Require(fat.GetComponent<MeshRenderer>().sharedMaterial.color.r>.8f&&fat.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Layer")==1,"fat has a dedicated lobular yellow surface rather than skin color");
                body.Set("skin","cutLengthMm",60);body.Set("fat","opened",1);wound.Apply(body);
                Require(wound.IsLayerVisible(2)&&!wound.IsLayerVisible(3)&&skin.bounds.size.magnitude>firstExtent+ .02f,"longer measured cut expands actual geometry and exposes fascia only after fat opens");
                Require(LayerGap(wound,"fat")>=.0149f,"opening fat preserves at least15 mm for the pale fascia floor instead of a narrow yellow strip");
                body.Set("fascia","opened",1);wound.Apply(body);
                Require(wound.IsLayerVisible(3)&&!wound.IsLayerVisible(4),"fascia incision reveals red intact muscle and hides peritoneum");
                Require(LayerGap(wound,"fascia")>=.0119f,"fascia opening leaves at least12 mm of visible intact red muscle");
                var muscle=Filter(wound,"muscleRightLip");var intact=(Vector3[])muscle.sharedMesh.vertices.Clone();
                Require(muscle.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Layer")==3&&muscle.GetComponent<MeshRenderer>().sharedMaterial.color.r>muscle.GetComponent<MeshRenderer>().sharedMaterial.color.g*2,"muscle owns red longitudinal fiber appearance");
                body.Set("muscle","opened",1);body.Set("muscle","splitWidthMm",22);wound.Apply(body);
                Require(wound.IsLayerVisible(4)&&!intact.SequenceEqual(muscle.sharedMesh.vertices),"actual measured split changes muscle geometry and exposes a thin membrane");
                var membrane=Filter(wound,"peritoneumRightLip");var before=membrane.sharedMesh.vertices;
                body.Set("peritoneum","tented",1);body.Set("peritoneum","liftMm",10);wound.Apply(body);
                var tent=membrane.sharedMesh.vertices;
                float nearLift=0,farLift=0;
                for(int i=0;i<tent.Length;i++)
                {
                    float distance=Vector2.Distance(new Vector2(before[i].x,before[i].y),centre),delta=before[i].z-tent[i].z;
                    if(distance<.007f)nearLift=Mathf.Max(nearLift,delta);
                    if(distance>.025f)farLift=Mathf.Max(farLift,delta);
                }
                Require(nearLift>.006f&&farLift<.0002f,"10 mm measured tent lifts a local membrane patch instead of the whole layer");
                Require(membrane.GetComponent<MeshRenderer>().sharedMaterial.GetFloat("_Layer")==4,"membrane owns its wet thin-layer material");
                MeshBounds(wound);
                var stable=membrane.sharedMesh.vertices;
                wound.Apply(body);Require(stable.SequenceEqual(membrane.sharedMesh.vertices),"unchanged facts preserve the same presentation geometry");
                CurvedSkinProjection(wound,body);
                CavityPresentation(wound,body);
                wound.SetRegistrationValid(false);wound.Apply(body);
                Require(!wound.RenderingWound&&!wound.RenderingCavity&&Shader.GetGlobalFloat("_ScalpalWoundWindow")==0&&root.GetComponentsInChildren<MeshRenderer>().Length==0,"invalid body registration hides all layers and restores solid patient skin");
                root.transform.position=new Vector3(.4f,1.2f,-.3f);root.transform.rotation=Quaternion.Euler(40,-25,17);
                wound.SetRegistrationValid(true);wound.Apply(body);
                var point=new Vector3(centre.x,centre.y,.02f);
                var actual=Shader.GetGlobalMatrix("_ScalpalWoundWorldToLocal").MultiplyPoint3x4(root.transform.TransformPoint(point));
                Require((actual-point).magnitude<1e-5f,"patient aperture follows a rotated registered wound frame in metres");
                wound.Concealed=true;Require(!wound.RenderingWound&&!wound.RenderingCavity&&Shader.GetGlobalFloat("_ScalpalWoundWindow")==0,"concealment never leaves a floating skin window");wound.Concealed=false;
                foreach(var id in wound.layerIds)body.Set(id,"closed",1);wound.Apply(body);
                Require(!wound.RenderingWound&&!wound.RenderingCavity&&!wound.HasSkinOpening&&root.GetComponentsInChildren<MeshRenderer>().Length==0&&Shader.GetGlobalFloat("_ScalpalWoundWindow")==0,"complete closure removes the wound slab and restores intact skin");
                body=new BodyState(plan.tissues);wound.Apply(body);
                Require(!wound.RenderingWound&&!wound.RenderingCavity&&!wound.HasSkinOpening,"fresh body/reset removes the prior incision and tent");
                foreach(var filter in root.GetComponentsInChildren<MeshFilter>(true))
                    Require(filter.sharedMesh.vertices.All(OpenSurgeryStroke.Finite),"all inactive/reset mesh coordinates stay finite");
                Require(wound.IncisionAxisLocal==Vector2.right&&wound.SkinOpeningLocal.x==0&&wound.SkinOpeningLocal.y==0,"fresh attempt restores the authored incision frame instead of inheriting the prior offset marker");
                wound.Apply(null);Require(!wound.RenderingWound&&!wound.RenderingCavity&&Shader.GetGlobalFloat("_ScalpalWoundWindow")==0,"missing body does not retain an old patient skin cutaway");
                Debug.Log("SCALPAL_OPEN_WOUND_APPEARANCE_OK checks="+checks+" measuredFacts=true distinctLayerMaterials=true finiteIncisionBounds=true semanticTeachingSurface=true clinicalCalibration=false headset=false");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                Shader.SetGlobalFloat("_ScalpalWoundWindow",oldWindow);Shader.SetGlobalVector("_ScalpalWoundOpening",oldOpening);
                Shader.SetGlobalVector("_ScalpalWoundAxis",oldAxis);Shader.SetGlobalMatrix("_ScalpalWoundWorldToLocal",oldMatrix);
            }
        }
        static float LayerGap(OpenWoundView wound,string id)
        {
            var left=Filter(wound,id+"LeftLip").sharedMesh.vertices[32*5+1];
            var right=Filter(wound,id+"RightLip").sharedMesh.vertices[32*5+1];
            return Vector2.Dot(new Vector2(right.x-left.x,right.y-left.y),new Vector2(-wound.IncisionAxisLocal.y,wound.IncisionAxisLocal.x));
        }
        static void CavityPresentation(OpenWoundView wound,BodyState body)
        {
            var lining=Filter(wound,"IllustrativeCavityLining");
            Require(!wound.RenderingCavity&&!lining.gameObject.activeInHierarchy,"intact/tented peritoneum hides the cavity lining");
            int records=body.Log.Count;
            var unchanged=wound.layerIds.Take(4).Select(id=>Filter(wound,id+"RightLip").sharedMesh.vertices).ToArray();
            body.Set("peritoneum","opened",1);wound.Apply(body);
            Require(wound.RenderingCavity&&lining.gameObject.activeInHierarchy,"cavity becomes visible from the authoritative membrane-open fact");
            for(int i=0;i<4;i++)Require(unchanged[i].SequenceEqual(Filter(wound,wound.layerIds[i]+"RightLip").sharedMesh.vertices),"cavity presentation leaves existing layer geometry unchanged");
            Require(lining.GetComponent<Collider>()==null&&lining.GetComponent<Rigidbody>()==null&&body.Log.Count==records,"illustrative cavity has no physics, authority or emitted action");
            CavityBounds(wound,lining.sharedMesh);
            var mat=lining.GetComponent<MeshRenderer>().sharedMaterial;
            Require(mat.shader.name=="Scalpal/OpenTissueSurface"&&mat.color.r<.2f&&mat.color.g<.06f&&mat.GetFloat("_Glossiness")>=.75f,"cavity has a retained dark wet material rather than cyan room color");
            // Query a temporary, separate collision copy solely to prove the actual
            // rendered bowl closes the sightline. Runtime presentation has no collider.
            var queryObject=new GameObject("ValidationOnlyCavityRayQuery");queryObject.transform.SetParent(wound.transform,false);
            var query=queryObject.AddComponent<MeshCollider>();query.sharedMesh=lining.sharedMesh;
            bool oldBackfaces=Physics.queriesHitBackfaces;
            try
            {
                Physics.queriesHitBackfaces=true;Physics.SyncTransforms();
                var opening=wound.SkinOpeningLocal;var axis=wound.IncisionAxisLocal;var across=new Vector2(-axis.y,axis.x);
                float halfWidth=LayerGap(wound,"peritoneum")*.5f;
                foreach(float radius in new[]{0f,.25f,.55f,.85f})for(int i=0;i<8;i++)
                {
                    float angle=i*Mathf.PI/4;
                    var q=new Vector2(opening.x,opening.y)+axis*(Mathf.Cos(angle)*radius*(opening.z-.002f))+across*(Mathf.Sin(angle)*radius*halfWidth);
                    var origin=wound.transform.TransformPoint(new Vector3(q.x,q.y,-.03f));
                    Require(query.Raycast(new Ray(origin,wound.transform.forward),out var hit,.3f)&&wound.transform.InverseTransformPoint(hit.point).z>.028f,"actual rendered bowl closes interior camera rays beneath the exposed tissues");
                }
                var outside=new Vector2(opening.x,opening.y)+across*(halfWidth*1.1f);
                Require(!query.Raycast(new Ray(wound.transform.TransformPoint(new Vector3(outside.x,outside.y,-.03f)),wound.transform.forward),out _, .3f),"cavity does not extend outside its finite incision aperture");
            }
            finally {Physics.queriesHitBackfaces=oldBackfaces;UnityEngine.Object.DestroyImmediate(queryObject);}
            var stable=lining.sharedMesh.vertices;wound.Apply(body);
            Require(stable.SequenceEqual(lining.sharedMesh.vertices),"unchanged membrane facts preserve the cavity geometry");
            body.Set("peritoneum","closed",1);wound.Apply(body);
            Require(!wound.RenderingCavity&&!lining.gameObject.activeInHierarchy&&wound.RenderingWound,"membrane closure removes the cavity while upper wound layers remain visible");
            body.Set("peritoneum","closed",0);wound.Apply(body);Require(wound.RenderingCavity,"reopening the membrane restores the bounded cavity");
            MeshBounds(wound);
        }
        static void CavityBounds(OpenWoundView wound,Mesh mesh)
        {
            var opening=wound.SkinOpeningLocal;var axis=wound.IncisionAxisLocal;var across=new Vector2(-axis.y,axis.x);
            Require(mesh&&mesh.vertexCount==1+8*65&&mesh.triangles.Length==64*3+7*64*6,"cavity uses the finite closed-bowl mesh, not an unbounded plane");
            foreach(var vertex in mesh.vertices)
            {
                var q=new Vector2(vertex.x-opening.x,vertex.y-opening.y);
                float x=Vector2.Dot(q,axis)/opening.z,y=Vector2.Dot(q,across)/opening.w;
                Require(OpenSurgeryStroke.Finite(vertex)&&x*x+y*y<=1.0001f,"cavity remains within the published incision ellipse");
                Require(vertex.z>=.02799f&&vertex.z<=.12001f,"cavity stays behind wall tissues and within the illustrative120 mm depth");
            }
            Require(Mathf.Abs(mesh.vertices[0].z-.12f)<1e-6f&&mesh.normals.All(OpenSurgeryStroke.Finite)&&mesh.triangles.All(i=>i>=0&&i<mesh.vertexCount),"actual bowl bottom, normals and indices are valid");
        }
        // Bowel loops under the opened membrane and the field's pooled blood: the same finite-ellipse guard as every
        // other wound surface, behind the skin and no deeper than the illustrative cavity, on the retained material.
        static void OpeningSurfaceBounds(OpenWoundView wound,MeshFilter filter)
        {
            var mesh=filter.sharedMesh;var opening=wound.SkinOpeningLocal;var axis=wound.IncisionAxisLocal;var across=new Vector2(-axis.y,axis.x);
            Require(mesh&&mesh.vertexCount>0&&mesh.triangles.Length>0&&mesh.triangles.All(i=>i>=0&&i<mesh.vertexCount),filter.name+" has a finite valid mesh");
            foreach(var vertex in mesh.vertices)
            {
                var q=new Vector2(vertex.x-opening.x,vertex.y-opening.y);
                float x=Vector2.Dot(q,axis)/opening.z,y=Vector2.Dot(q,across)/opening.w;
                Require(OpenSurgeryStroke.Finite(vertex)&&x*x+y*y<=1.0001f,filter.name+" stays inside the published incision ellipse");
                Require(vertex.z>=0&&vertex.z<=.12001f,filter.name+" lies inside the wound, behind the skin and within the illustrative cavity depth");
            }
            Require(mesh.normals.All(OpenSurgeryStroke.Finite),filter.name+" normals are finite");
            var mat=filter.GetComponent<MeshRenderer>().sharedMaterial;Require(mat.shader.name=="Scalpal/OpenTissueSurface",filter.name+" uses the retained wound surface material");
        }
        static float CurvedDepth(Vector3 point)=>-.003f-3*point.x*point.x-5*point.y*point.y;
        static void CurvedSkinProjection(OpenWoundView wound,BodyState body)
        {
            // An actual curved MeshCollider exercises the same raycast as the imported
            // virtual patient, not a stub returning the desired projection coordinates.
            var skinObject=new GameObject("SyntheticCurvedSkinCollision");skinObject.transform.SetParent(wound.transform,false);
            var mesh=new Mesh();const int cells=24;
            var points=new Vector3[(cells+1)*(cells+1)];var indices=new int[cells*cells*6];
            for(int y=0;y<=cells;y++)for(int x=0;x<=cells;x++)
            {
                var p=new Vector3(Mathf.Lerp(-.08f,.08f,x/(float)cells),Mathf.Lerp(-.06f,.06f,y/(float)cells),0);
                p.z=CurvedDepth(p);points[y*(cells+1)+x]=p;
                if(x==cells||y==cells)continue;
                int a=y*(cells+1)+x,b=a+1,c=a+cells+1,d=c+1,at=(y*cells+x)*6;
                indices[at]=a;indices[at+1]=b;indices[at+2]=c;indices[at+3]=b;indices[at+4]=d;indices[at+5]=c;
            }
            mesh.vertices=points;mesh.triangles=indices;mesh.RecalculateBounds();
            var collider=skinObject.AddComponent<MeshCollider>();collider.sharedMesh=mesh;
            var deeper=Filter(wound,"peritoneumRightLip").sharedMesh.vertices;
            bool oldBackfaces=Physics.queriesHitBackfaces;
            try
            {
                Physics.SyncTransforms();wound.BindSkinSurface(collider);wound.Apply(body);
                var skin=Filter(wound,"skinRightLip").sharedMesh.vertices;
                float maximumRimError=0,maximumInnerError=0;
                for(int i=0;i<=64;i++)
                {
                    var rim=skin[i*5+4];maximumRimError=Mathf.Max(maximumRimError,Mathf.Abs(rim.z-(CurvedDepth(rim)-.00045f)));
                    var inner=skin[i*5+1];maximumInnerError=Mathf.Max(maximumInnerError,Mathf.Abs(inner.z+.00045f));
                }
                Require(maximumRimError<.0001f,"outer skin rim joins a real curved collider within100 micrometres, without a floating planar coupon");
                Require(maximumInnerError<1e-6f,"skin projection preserves the incision edge in the registered mechanics frame");
                Require(deeper.SequenceEqual(Filter(wound,"peritoneumRightLip").sharedMesh.vertices),"patient skin projection cannot move or cover the measured membrane tent");
                Require(Physics.queriesHitBackfaces==oldBackfaces,"skin projection restores the shared physics raycast setting");
                var across=new Vector2(-wound.IncisionAxisLocal.y,wound.IncisionAxisLocal.x);
                var right=skin[32*5+1];var left=Filter(wound,"skinLeftLip").sharedMesh.vertices[32*5+1];
                Require(Vector2.Dot(new Vector2(right.x-left.x,right.y-left.y),across)>.02f,"projected skin preserves at least20 mm of measured central opening rather than covering the wound");
                var beforeShift=skin;
                skinObject.transform.localPosition=Vector3.forward*.002f;Physics.SyncTransforms();wound.Apply(body);
                var afterShift=Filter(wound,"skinRightLip").sharedMesh.vertices;
                Require(Mathf.Abs(afterShift[32*5+4].z-beforeShift[32*5+4].z-.002f)<1e-6f,"relative patient-skin movement invalidates cached skin presentation");
                collider.enabled=false;wound.Apply(body);
                Require(Mathf.Abs(Filter(wound,"skinRightLip").sharedMesh.vertices[32*5+4].z+.00045f)<1e-6f,"unavailable virtual skin returns to the registered AR plane without a stale projected rim");
                wound.BindSkinSurface(null);wound.Apply(body);
                MeshBounds(wound);
            }
            finally
            {
                wound.BindSkinSurface(null);UnityEngine.Object.DestroyImmediate(skinObject);UnityEngine.Object.DestroyImmediate(mesh);
            }
        }
        static MeshFilter Filter(OpenWoundView wound,string name)=>wound.GetComponentsInChildren<MeshFilter>(true).Single(f=>f.name==name);
        static void MeshBounds(OpenWoundView wound)
        {
            var opening=wound.SkinOpeningLocal;var axis=wound.IncisionAxisLocal;var across=new Vector2(-axis.y,axis.x);
            foreach(var filter in wound.GetComponentsInChildren<MeshFilter>())
            {
                var mesh=filter.sharedMesh;
                if(filter.name=="IllustrativeCavityLining"){CavityBounds(wound,mesh);continue;}
                if(filter.name=="IllustrativeBowelLoops"||filter.name=="FieldBlood"){OpeningSurfaceBounds(wound,filter);continue;}
                Require(mesh&&mesh.vertexCount==325,"active layer uses its finite wound mesh");
                foreach(var vertex in mesh.vertices)
                {
                    var q=new Vector2(vertex.x-opening.x,vertex.y-opening.y);
                    float x=Vector2.Dot(q,axis)/opening.z,y=Vector2.Dot(q,across)/opening.w;
                    Require(OpenSurgeryStroke.Finite(vertex)&&x*x+y*y<=1.0001f,"every visible layer vertex stays inside the published incision ellipse, never the old rectangle");
                    Require(vertex.z>=-.026f&&vertex.z<=.029f,"surface stays within the finite wall/tent depth envelope");
                }
                var triangles=mesh.triangles;Require(triangles.Length==64*4*6&&triangles.All(t=>t>=0&&t<mesh.vertexCount),"actual mesh index bounds are valid");
                Require(mesh.normals.All(n=>OpenSurgeryStroke.Finite(n)),"actual layer normals remain finite without coincident reverse-winding cancellation");
                var tangent=mesh.tangents[32*5+3];
                Require(!float.IsNaN(tangent.w)&&OpenSurgeryStroke.Finite(new Vector3(tangent.x,tangent.y,tangent.z))&&new Vector3(tangent.x,tangent.y,tangent.z).sqrMagnitude>.5f,"interior tissue has a finite nonzero tangent basis for its procedural micro normal");
                var mat=filter.GetComponent<MeshRenderer>().sharedMaterial;Require(mat.shader.name=="Scalpal/OpenTissueSurface"&&mat.HasProperty("_Layer"),"every visible tissue uses a retained dedicated surface material");
            }
        }
    }
}
