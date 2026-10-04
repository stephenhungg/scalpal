using System;
using System.Collections.Generic;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Engine;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scalpal.Surgery
{
    // Original teaching surfaces, not segmentation or a second simulation. +Z points inward;
    // all visibility/deformation reads measured body facts, never a case step or milestone.
    public sealed class OpenWoundView : MonoBehaviour
    {
        public string[] layerIds = { "skin", "fat", "fascia", "muscle", "peritoneum" };
        public string decisionTissueId = "appendix";
        readonly List<Material> materials = new List<Material>();
        readonly List<Mesh> meshes = new List<Mesh>();
        readonly Mesh[,] lips = new Mesh[5, 2];
        readonly float[] depths = { 0, .002f, .014f, .019f, .027f };
        readonly GameObject[,] layerObjects = new GameObject[5,2];
        readonly bool[] visibleLayers = new bool[5];
        Vector2 incisionCenter, incisionAxis = Vector2.right, tentCenter;
        float halfLength = .03f;
        bool skinOpened, hasTentPoint;
        Collider skinSurface;
        Matrix4x4 previousSkinToWound;
        bool previousSkinAvailable, skinBindingChanged;
        // Presentation-only projection onto the virtual patient's physics copy. Never moves
        // the registered wound/volume or queries a real participant surface in AR.
        public void BindSkinSurface(Collider surface)
        {
            if (skinSurface == surface) return;
            skinSurface = surface; skinBindingChanged = true;
        }
        bool SkinAvailable => skinSurface && skinSurface.enabled && skinSurface.gameObject.activeInHierarchy;
        bool SkinFrameChanged()
        {
            bool available = SkinAvailable;
            Matrix4x4 relative = available ? transform.worldToLocalMatrix * skinSurface.transform.localToWorldMatrix : Matrix4x4.identity;
            bool changed = skinBindingChanged || available != previousSkinAvailable;
            for (int i = 0; i < 16 && !changed; i++) changed = Mathf.Abs(relative[i] - previousSkinToWound[i]) > 1e-5f;
            previousSkinToWound = relative; previousSkinAvailable = available; skinBindingChanged = false;
            return changed;
        }
        float SkinDepth(Vector2 local)
        {
            if (!SkinAvailable) return 0;
            Vector3 inward = transform.forward, world = transform.TransformPoint(new Vector3(local.x, local.y, 0));
            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            try
            {
                if (skinSurface.Raycast(new Ray(world - inward * .25f, inward), out var hit, .5f))
                {
                    float depth = transform.InverseTransformPoint(hit.point).z;
                    if (!float.IsNaN(depth) && !float.IsInfinity(depth)) return depth;
                }
            }
            finally { Physics.queriesHitBackfaces = backfaces; }
            return 0;
        }
        public bool HasSkinOpening => skinOpened;
        public bool RenderingWound => isActiveAndEnabled && registered && !concealed && skinOpened && surfaces && surfaces.activeInHierarchy;
        public Vector4 SkinOpeningLocal => new Vector4(incisionCenter.x,incisionCenter.y,halfLength+.002f,Mathf.Max(0,previousWidths[0])*.5f+.0035f);
        public Vector2 IncisionAxisLocal => incisionAxis;
        readonly Color[] colors = { new Color(.65f,.44f,.34f), new Color(.94f,.72f,.30f), new Color(.84f,.80f,.72f), new Color(.50f,.13f,.105f), new Color(.79f,.65f,.60f) };
        readonly float[] previousWidths = { -1,-1,-1,-1,-1 };
        readonly float[] previousLifts = { -1,-1,-1,-1,-1 };
        readonly float[] previousOuterWidths = { -1,-1,-1,-1,-1 };
        Vector2 previousCenter,previousAxis,previousTent;
        float previousLength=-1;
        BodyState previousBody;
        int lastBodyLog;
        GameObject surfaces, decisions, cavity, bowel, film;
        Mesh cavityMesh, bowelMesh, filmMesh;
        float previousCavityWidth = -1, previousFilm = -1;
        public bool RenderingCavity => RenderingWound && cavity && cavity.activeInHierarchy;
        // Bowel loops seen through the opened peritoneum, and blood in the field (0..1) that suction clears.
        public bool BowelVisible => RenderingCavity && bowel && bowel.activeInHierarchy;
        public float FieldWetness { get; private set; }
        public bool FieldBloodVisible => RenderingWound && film && film.activeInHierarchy;
        public float LayerWidth(int layer) => layer >= 0 && layer < previousWidths.Length ? Mathf.Max(0, previousWidths[layer]) : 0;
        double wetFromLog;
        readonly HashSet<string> bledFrom = new HashSet<string>();
        LineRenderer mark;
        Material inkMaterial;
        bool built, registered = true, marked, showDecision, concealed;
        string[] decisionChoices = System.Array.Empty<string>();
        public Transform BasePoint { get; private set; }
        // Intact skin: the closed teaching wall stays out of sight (no skin window, no layer coupon) until the
        // marking presenter reveals it. Presentation only; contact and measurement are unaffected.
        public bool Concealed { get => concealed; set { if (concealed == value) return; concealed = value; SetRegistrationValid(registered); } }
        public void Build()
        {
            if (built) return;
            built = true;
            surfaces = new GameObject("MeasuredWoundSurfaces"); surfaces.transform.SetParent(transform, false);
            for (int layer = 0; layer < 5; layer++) for (int side = 0; side < 2; side++)
            {
                var go = new GameObject(layerIds[layer] + (side == 0 ? "LeftLip" : "RightLip"));layerObjects[layer,side]=go;
                go.transform.SetParent(surfaces.transform, false);
                var mesh = new Mesh { name = go.name + "AuthoredSurface" }; mesh.MarkDynamic(); meshes.Add(mesh); lips[layer,side] = mesh;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var template=Resources.Load<Material>("OpenTissueSurface");
                if(!template||!template.shader)throw new InvalidOperationException("Missing serialized OpenTissueSurface material/shader");
                var material=new Material(template){name=go.name+"Surface",color=colors[layer]};
                material.SetFloat("_Layer",layer);material.SetFloat("_Glossiness",layer==0?.20f:layer==4?.84f:layer<=2?.35f:.46f);materials.Add(material);
                var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
                renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
                Shape(mesh, layer, side, 0, 0, 0);go.SetActive(false);
            }
            // A finite illustrative cavity prevents seeing the room through the patient.
            // It is presentation only: no collider, tissue identity, event or hidden-organ
            // estimate. Existing organ geometry remains in front of its deep lining.
            cavity = new GameObject("IllustrativeCavityLining");cavity.transform.SetParent(surfaces.transform,false);
            cavityMesh = new Mesh { name = "IllustrativeClosedCavity" };cavityMesh.MarkDynamic();meshes.Add(cavityMesh);
            cavity.AddComponent<MeshFilter>().sharedMesh = cavityMesh;
            var cavityTemplate = Resources.Load<Material>("OpenTissueSurface");
            var cavityMaterial = new Material(cavityTemplate) { name = "IllustrativeWetCavity", color = new Color(.16f,.038f,.032f) };
            cavityMaterial.SetFloat("_Layer",4);cavityMaterial.SetFloat("_Glossiness",.8f);materials.Add(cavityMaterial);
            var cavityRenderer = cavity.AddComponent<MeshRenderer>();cavityRenderer.sharedMaterial = cavityMaterial;
            cavityRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;cavity.SetActive(false);
            // Glistening small-bowel loops just below the opened peritoneum, ringed around the opening so the deeper
            // caecum and appendix stay visible to grasp. Presentation only: no collider, identity or event.
            bowel = Surface("IllustrativeBowelLoops", new Color(.86f,.56f,.50f), .86f, out bowelMesh);
            // Blood in the field from the incisions and divisions, pooled at the wound's dependent side.
            film = Surface("FieldBlood", new Color(.55f,.03f,.03f), .95f, out filmMesh);
            var ink = new GameObject("MeasuredMarkerInk"); ink.transform.SetParent(surfaces.transform, false);
            mark = ink.AddComponent<LineRenderer>(); mark.useWorldSpace = false; mark.positionCount = 0;
            mark.startWidth = mark.endWidth = .0012f; mark.numCapVertices = 3;
            inkMaterial = TissueRuntimeMaterial.Create("SurgicalMarker", new Color(.2f,.05f,.55f)); materials.Add(inkMaterial);
            mark.sharedMaterial = inkMaterial; mark.enabled = false;
            decisions = new GameObject("BaseDecisionLocations"); decisions.transform.SetParent(surfaces.transform, false);
            var basePoint = new GameObject("AnatomicalBasePoint"); basePoint.transform.SetParent(surfaces.transform, false);
            basePoint.transform.localPosition = new Vector3(-.022f,0,-.024f); BasePoint = basePoint.transform;
            decisions.SetActive(false);PublishOpening();
        }
        GameObject Surface(string name, Color color, float gloss, out Mesh mesh)
        {
            var go = new GameObject(name); go.transform.SetParent(surfaces.transform, false);
            mesh = new Mesh { name = name }; mesh.MarkDynamic(); meshes.Add(mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // The same retained wet tissue surface as the layers and cavity (Scalpal/OpenTissueSurface, membrane relief).
            var material = new Material(Resources.Load<Material>("OpenTissueSurface")) { name = name, color = color };
            material.SetFloat("_Layer", 4); material.SetFloat("_Glossiness", gloss); materials.Add(material);
            var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; go.SetActive(false);
            return go;
        }
        // Each first division of a layer or vessel bleeds a little into the field; suction (any dwell) clears it.
        static double Wet(string tissueId)
        {
            switch (tissueId)
            {
                case "skin": return .3; case "fat": return .1; case "muscle": return .15; case "peritoneum": return .1;
                case "mesoappendix": case "appendicular_artery": return .3; case "appendix": return .1;
                default: return 0;
            }
        }
        // The active case supplies the labels; the wound renderer never decides the correct answer.
        public void SetDecisionChoices(string[] choices)
        {
            Build(); decisionChoices = choices == null ? System.Array.Empty<string>() : (string[])choices.Clone();
            for (int i=decisions.transform.childCount-1;i>=0;i--)
            { var old=decisions.transform.GetChild(i); old.gameObject.SetActive(false); Release(old.gameObject); }
            for (int i=0; i<decisionChoices.Length; i++)
            {
                var point = GameObject.CreatePrimitive(PrimitiveType.Sphere); point.name = decisionChoices[i]; point.transform.SetParent(decisions.transform,false);
                point.transform.localPosition = new Vector3(-.022f+i*.022f,0,-.024f); point.transform.localScale=Vector3.one*.006f;
                var material=TissueRuntimeMaterial.Create("DecisionLocation",new Color(.25f,.65f,.7f)); materials.Add(material);
                point.GetComponent<Renderer>().sharedMaterial=material; Release(point.GetComponent<Collider>());
            }
        }
        // Marker geometry comes from the actual tracked stroke transformed into this wound's frame.
        // A premarked fast path must explicitly supply its authored registered points through this API.
        public void SetMarker(IReadOnlyList<Vector3> points)
        {
            Build();
            if (points == null || points.Count < 2) { mark.positionCount = 0; return; }
            foreach (var point in points) if (!OpenSurgeryStroke.Finite(point)) return;
            Vector2 start=new Vector2(points[0].x,points[0].y),end=new Vector2(points[points.Count-1].x,points[points.Count-1].y);
            if((end-start).sqrMagnitude>1e-8f){incisionCenter=(start+end)*.5f;incisionAxis=(end-start).normalized;halfLength=Mathf.Clamp(Vector2.Distance(start,end)*.5f,.003f,.045f);}
            mark.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) mark.SetPosition(i, points[i] + Vector3.back*.0007f);
        }
        static readonly int WoundWorldToLocal = Shader.PropertyToID("_ScalpalWoundWorldToLocal"), WoundWindow = Shader.PropertyToID("_ScalpalWoundWindow"),
            WoundOpening=Shader.PropertyToID("_ScalpalWoundOpening"),WoundAxis=Shader.PropertyToID("_ScalpalWoundAxis");
        void PublishOpening()
        {
            Shader.SetGlobalMatrix(WoundWorldToLocal,transform.worldToLocalMatrix);
            Shader.SetGlobalVector(WoundOpening,SkinOpeningLocal);
            Shader.SetGlobalVector(WoundAxis,new Vector4(incisionAxis.x,incisionAxis.y,0,0));
            Shader.SetGlobalFloat(WoundWindow,RenderingWound?1:0);
        }
        public void SetRegistrationValid(bool valid)
        {
            registered = valid; if (surfaces) surfaces.SetActive(valid && !concealed);
            // The VR patient skin opens over this wound only while its wall is live (Scalpal/PatientSkin).
            PublishOpening();
        }
        void OnDisable() => Shader.SetGlobalFloat(WoundWindow, 0);
        public void Apply(BodyState body)
        {
            Build();surfaces.SetActive(registered&&body!=null&&!concealed);
            if(body==null){skinOpened=false;PublishOpening();return;}
            if(previousBody!=null&&!ReferenceEquals(previousBody,body))
            {incisionCenter=Vector2.zero;incisionAxis=Vector2.right;halfLength=.03f;mark.positionCount=0;}
            skinOpened=body.Get(layerIds[0],"opened")>0&&body.Get(layerIds[0],"closed")<=0;
            float measuredLength=(float)body.Get(layerIds[0],"cutLengthMm")*.001f;
            if(measuredLength>0)halfLength=Mathf.Clamp(measuredLength*.5f,.003f,.045f);
            float retraction=Mathf.Clamp((float)body.Get(layerIds[3],"splitWidthMm")*.001f,0,.045f);
            // Authored resting presentation aperture; not a measurement of a cut's
            // physical separation. Measured retraction expands it during later work.
            float parentWidth=skinOpened?Mathf.Clamp(retraction+.018f,.018f,.049f):0;
            bool exposed=skinOpened;
            // Only inspect newly appended authoritative records; do not scan or
            // rebuild every surface on each headset frame when the facts are unchanged.
            if(!ReferenceEquals(previousBody,body)||lastBodyLog>body.Log.Count)
            {previousBody=body;lastBodyLog=0;hasTentPoint=false;wetFromLog=0;bledFrom.Clear();}
            for(int i=lastBodyLog;i<body.Log.Count;i++)
            {
                var action=body.Log[i].action;
                if(action!=null&&Array.IndexOf(body.Log[i].outcomes,"not_exposed")<0)
                {
                    if(action.verb=="cut"&&bledFrom.Add(action.tissueId))wetFromLog+=Wet(action.tissueId);
                    if(action.verb=="retract"&&action.tissueId==layerIds[3]&&action.separationMm>=15&&bledFrom.Add("muscle_split"))wetFromLog+=.15;
                    if(action.verb=="suction")wetFromLog-=action.durationMs/1200;
                    wetFromLog=Math.Max(0,Math.Min(1,wetFromLog));
                }
                if(action==null||action.tissueId!=layerIds[4]||(action.verb!="grasp"&&action.verb!="retract")||action.coordinateFrame!="registered_torso_m"||!transform.parent)continue;
                Vector3 local=transform.InverseTransformPoint(transform.parent.TransformPoint(new Vector3(action.position.x,action.position.y,action.position.z)));
                if(OpenSurgeryStroke.Finite(local)){tentCenter=new Vector2(local.x,local.y);hasTentPoint=true;}
            }
            lastBodyLog=body.Log.Count;
            if(!hasTentPoint)tentCenter=incisionCenter;
            bool skinFrameChanged=SkinFrameChanged();
            bool frameChanged=(incisionCenter-previousCenter).sqrMagnitude>1e-10f||(incisionAxis-previousAxis).sqrMagnitude>1e-10f||
                Mathf.Abs(halfLength-previousLength)>1e-5f||(tentCenter-previousTent).sqrMagnitude>1e-10f;
            // Each intact layer is the floor of the preceding aperture. Opened layers
            // become local lips, exposing the next material. There is no full-size coupon.
            for(int layer=0;layer<5;layer++)
            {
                string id=layerIds[layer];bool closed=body.Get(id,"closed")>0;
                bool opened=body.Get(id,"opened")>0&&!closed;
                float width=opened?Mathf.Max(.002f,parentWidth-(layer==0?0:.003f)):0;
                if(layer==3&&opened)width=Mathf.Min(parentWidth-.002f,Mathf.Max(.002f,retraction));
                if(layer==0)width=skinOpened?parentWidth:0;
                float lift=layer==4&&!closed&&body.Get(id,"tented")>0?Mathf.Clamp((float)body.Get(id,"liftMm")*.001f,0,.025f):0;
                visibleLayers[layer]=exposed;
                float outer=layer==0?parentWidth+.007f:parentWidth;
                bool changed=frameChanged||(layer==0&&skinFrameChanged)||Mathf.Abs(previousWidths[layer]-width)>1e-5f||Mathf.Abs(previousLifts[layer]-lift)>1e-5f||Mathf.Abs(previousOuterWidths[layer]-outer)>1e-5f;
                for(int side=0;side<2;side++)
                {
                    if(layerObjects[layer,side].activeSelf!=exposed)layerObjects[layer,side].SetActive(exposed);
                    if(changed)Shape(lips[layer,side],layer,side,width,lift,outer);
                }
                previousWidths[layer]=width;previousLifts[layer]=lift;previousOuterWidths[layer]=outer;
                parentWidth=width;exposed=exposed&&opened&&width>0;
            }
            // 'exposed' now includes the peritoneum's actual opened/not-closed fact.
            if(cavity.activeSelf!=exposed)cavity.SetActive(exposed);
            if(bowel.activeSelf!=exposed)bowel.SetActive(exposed);
            if(exposed&&(frameChanged||Mathf.Abs(previousCavityWidth-parentWidth)>1e-5f)){ShapeCavity(parentWidth);ShapeBowel(parentWidth);}
            previousCavityWidth=parentWidth;
            // The field's blood pools on the deepest visible floor: the first closed layer, else the bowel.
            FieldWetness=skinOpened?Mathf.Clamp01((float)(wetFromLog+Math.Max(0,body.Get("","poolMl"))/4)):0;
            int floorLayer=0;while(floorLayer<5&&LayerWidth(floorLayer)>0)floorLayer++;
            float floorDepth=floorLayer<5?depths[floorLayer]:.031f,floorWidth=floorLayer<5?(floorLayer==0?0:LayerWidth(floorLayer-1)):parentWidth;
            bool wet=skinOpened&&FieldWetness>.05f&&floorWidth>0;
            if(film.activeSelf!=wet)film.SetActive(wet);
            float filmKey=wet?Mathf.Round(FieldWetness*20)+floorLayer*100+floorWidth*1e5f:-1;
            if(wet&&(frameChanged||Mathf.Abs(filmKey-previousFilm)>1e-3f))ShapeFilm(floorDepth,floorWidth,floorLayer==5);
            previousFilm=filmKey;
            previousCenter=incisionCenter;previousAxis=incisionAxis;previousLength=halfLength;previousTent=tentCenter;
            PublishOpening();
            marked = body.Get(layerIds[0],"marked") > 0 && body.Get(layerIds[0],"closed") == 0;
            mark.enabled = marked && !skinOpened && mark.positionCount >= 2;
            bool poorMark = body.Get(layerIds[0],"markErrorMm") > 20 || body.Get(layerIds[0],"markAngleDegrees") > 25;
            inkMaterial.color = poorMark ? new Color(1,.55f,.06f) : new Color(.2f,.05f,.55f);
            bool answered = false;
            foreach (var choice in decisionChoices) if (body.Get(decisionTissueId,"decision_"+choice) > 0) answered = true;
            showDecision = skinOpened && decisionChoices.Length > 0 && body.Get(decisionTissueId,"delivered") > 0 && body.Get(decisionTissueId,"removed") == 0 && !answered;
            decisions.SetActive(showDecision);
        }
        public string DecisionAt(Vector3 localPoint)
        {
            if (!registered || !decisions || !showDecision) return "";
            foreach (Transform candidate in decisions.transform)
                if (candidate.gameObject.activeSelf && Vector3.Distance(localPoint, transform.InverseTransformPoint(candidate.position)) < .009f) return candidate.name;
            return "";
        }
        public bool IsLayerVisible(int layer)=>layer>=0&&layer<visibleLayers.Length&&visibleLayers[layer]&&RenderingWound;
        void Shape(Mesh mesh,int layer,int side,float width,float lift,float outerWidth)
        {
            // 64 x 5 compatibility layout; a thin incision-local rim replaces the old
            // 120 x 90 mm slab. Closed current layers fill only their parent's aperture.
            const int segments=64,rows=5;
            var vertices=new Vector3[(segments+1)*rows];var uv=new Vector2[vertices.Length];
            var triangles=new List<int>(segments*(rows-1)*6);float sign=side==0?-1:1;
            float extent=halfLength+(layer==0?.002f:0),halfOuter=Mathf.Max(0,outerWidth)*.5f;
            Vector2 across=new Vector2(-incisionAxis.y,incisionAxis.x);
            float thickness=layer==0?.002f:layer==1?.009f:layer==2?.003f:layer==3?.006f:.0007f;
            for(int i=0;i<=segments;i++)
            {
                float t=i/(float)segments,x=Mathf.Lerp(-extent,extent,t),arch=Mathf.Sqrt(Mathf.Max(0,1-x*x/(extent*extent)));
                float innerArch=layer==0?Mathf.Sqrt(Mathf.Max(0,1-x*x/(halfLength*halfLength))):arch;
                float edge=width*.5f*innerArch,outer=Mathf.Max(edge,halfOuter*arch);
                for(int row=0;row<rows;row++)
                {
                    float y=row<2?edge:Mathf.Lerp(edge,outer,(row-1)/3f);
                    Vector2 q=incisionCenter+incisionAxis*x+across*(sign*y);
                    float z=depths[layer]-.00045f;
                    if(row==0)z+=width>0?thickness:0;
                    if(row==2&&width>0)z-=arch*.0008f;
                    // Fat has visible original lobules, fascia/muscle retain aligned
                    // fiber relief, and membrane tenting affects a 12 mm local patch.
                    float relief=layer==1?Mathf.Sin(x*750)*Mathf.Sin(y*610)*.0007f:
                        layer==2?Mathf.Sin(y*2200)*.00010f:layer==3?Mathf.Sin(y*1400)*.0003f:0;
                    if(row>0)z-=relief*arch;
                    if(layer==4&&lift>0)z-=lift*Mathf.Exp(-(q-tentCenter).sqrMagnitude/(.012f*.012f));
                    // Only the outer skin rim joins the curved visible patient. Its inner
                    // incision edge and all deeper layers stay in the registered mechanics
                    // frame, so projection cannot introduce a floor across the opening.
                    if(layer==0&&row>=2)z+=SkinDepth(q)*(row-1)/3f;
                    vertices[i*rows+row]=new Vector3(q.x,q.y,z);
                    uv[i*rows+row]=new Vector2(q.x,q.y);
                }
                if(i<segments)for(int row=0;row<rows-1;row++)
                {
                    int a=i*rows+row,b=a+rows,c=b+1,d=a+1;
                    if(side==1)triangles.AddRange(new[]{a,c,b,a,d,c});else triangles.AddRange(new[]{a,b,c,a,c,d});
                }
            }
            mesh.Clear();mesh.vertices=vertices;mesh.uv=uv;mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateTangents();mesh.RecalculateBounds();
        }
        // Loops: a bumpy ring under the peritoneal opening, from inside its edge to beneath the membrane.
        void ShapeBowel(float width)
        {
            const int segments=64,rings=6;
            var vertices=new List<Vector3>();var uv=new List<Vector2>();var triangles=new List<int>();
            Vector2 across=new Vector2(-incisionAxis.y,incisionAxis.x);
            for(int ring=0;ring<=rings;ring++)
            {
                float radius=Mathf.Lerp(.5f,1f,ring/(float)rings);
                for(int i=0;i<=segments;i++)
                {
                    float angle=2*Mathf.PI*i/segments;
                    Vector2 q=incisionCenter+incisionAxis*(halfLength*radius*Mathf.Cos(angle))+across*(width*.5f*radius*Mathf.Sin(angle));
                    // Rounded loops: tubes side by side around the opening, each a few millimetres proud.
                    float loop=Mathf.Pow(Mathf.Abs(Mathf.Sin(angle*5+radius*2.5f)),.6f)*Mathf.Sin(Mathf.PI*ring/rings);
                    vertices.Add(new Vector3(q.x,q.y,.038f-.007f*loop));uv.Add(q*40);
                    if(ring<rings&&i<segments){int a=ring*(segments+1)+i,b=a+segments+1;triangles.AddRange(new[]{a,b+1,b,a,a+1,b+1});}
                }
            }
            bowelMesh.Clear();bowelMesh.SetVertices(vertices);bowelMesh.SetUVs(0,uv);bowelMesh.SetTriangles(triangles,0);
            bowelMesh.RecalculateNormals();bowelMesh.RecalculateBounds();
        }
        // A wet pool on the floor's downhill side, shrinking as the field is suctioned dry.
        void ShapeFilm(float depth,float width,bool onBowel)
        {
            const int segments=32;
            float scale=.12f+.2f*FieldWetness;
            Vector3 down=transform.InverseTransformDirection(Vector3.down);
            Vector2 across=new Vector2(-incisionAxis.y,incisionAxis.x);
            float side=Vector2.Dot(new Vector2(down.x,down.y),across)>=0?1:-1;
            // On a closed floor the pool lies at its downhill edge; with the peritoneum open it lies among the loops.
            Vector2 centre=incisionCenter+across*(side*width*.5f*(onBowel?.9f:(1-scale)*.85f));
            float along=onBowel?.5f*scale:scale,acrossScale=onBowel?.3f*scale:scale;
            var vertices=new List<Vector3>{new Vector3(centre.x,centre.y,depth-.0012f)};var uv=new List<Vector2>{centre};var triangles=new List<int>();
            for(int i=0;i<=segments;i++)
            {
                float angle=2*Mathf.PI*i/segments;
                Vector2 q=centre+incisionAxis*(halfLength*along*Mathf.Cos(angle))+across*(width*.5f*acrossScale*Mathf.Sin(angle));
                vertices.Add(new Vector3(q.x,q.y,depth-.0009f));uv.Add(q);
                if(i<segments)triangles.AddRange(new[]{0,i+2,i+1});
            }
            filmMesh.Clear();filmMesh.SetVertices(vertices);filmMesh.SetUVs(0,uv);filmMesh.SetTriangles(triangles,0);
            filmMesh.RecalculateNormals();filmMesh.RecalculateBounds();
        }
        void ShapeCavity(float width)
        {
            // Concave lining: almost-vertical sides leave authored organs readable;
            // its closed bowl bottom is behind the surgical field, never a tissue floor.
            const int segments=64,rings=8;
            var vertices=new Vector3[1+rings*(segments+1)];var uv=new Vector2[vertices.Length];
            var triangles=new List<int>(segments*3+(rings-1)*segments*6);
            Vector2 across=new Vector2(-incisionAxis.y,incisionAxis.x);
            vertices[0]=new Vector3(incisionCenter.x,incisionCenter.y,.12f);uv[0]=incisionCenter;
            for(int ring=0;ring<rings;ring++)
            {
                float radius=(ring+1)/(float)rings;
                float z=.12f-(.12f-.028f)*Mathf.Pow(radius,6);
                for(int i=0;i<=segments;i++)
                {
                    float angle=2*Mathf.PI*i/segments;
                    Vector2 q=incisionCenter+incisionAxis*(halfLength*radius*Mathf.Cos(angle))+across*(width*.5f*radius*Mathf.Sin(angle));
                    int at=1+ring*(segments+1)+i;vertices[at]=new Vector3(q.x,q.y,z);uv[at]=q;
                    if(i==segments)continue;
                    if(ring==0)triangles.AddRange(new[]{0,at+1,at});
                    else
                    {
                        int a=at-(segments+1),b=at,c=b+1,d=a+1;
                        triangles.AddRange(new[]{a,c,b,a,d,c});
                    }
                }
            }
            cavityMesh.Clear();cavityMesh.vertices=vertices;cavityMesh.uv=uv;cavityMesh.SetTriangles(triangles,0);
            cavityMesh.RecalculateNormals();cavityMesh.RecalculateTangents();cavityMesh.RecalculateBounds();
        }
        // Only transient objects created by this view are released. Never delete an imported or
        // serialized asset if a caller accidentally supplies one during an Editor audit.
        static void Release(Object value)
        {
            if (!value) return;
#if UNITY_EDITOR
            if (UnityEditor.EditorUtility.IsPersistent(value)) return;
#endif
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
        void OnDestroy()
        {
            foreach (var mesh in meshes) if (mesh) Release(mesh);
            foreach (var material in materials) if (material) Release(material);
        }
    }
}
