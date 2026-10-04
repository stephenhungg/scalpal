using System.Collections.Generic;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Engine;
using UnityEngine;

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
        readonly float[] depths = { 0, .004f, .014f, .019f, .027f };
        readonly Color[] colors = { new Color(.52f,.26f,.20f), new Color(.72f,.48f,.12f), new Color(.69f,.62f,.50f), new Color(.38f,.055f,.047f), new Color(.58f,.28f,.27f) };
        readonly float[] previousWidths = { -1,-1,-1,-1,-1 };
        readonly float[] previousLifts = { -1,-1,-1,-1,-1 };
        GameObject surfaces, decisions;
        LineRenderer mark;
        Material inkMaterial;
        bool built, registered = true, marked, showDecision;
        string[] decisionChoices = System.Array.Empty<string>();
        public Transform BasePoint { get; private set; }
        public void Build()
        {
            if (built) return;
            built = true;
            surfaces = new GameObject("MeasuredWoundSurfaces"); surfaces.transform.SetParent(transform, false);
            for (int layer = 0; layer < 5; layer++) for (int side = 0; side < 2; side++)
            {
                var go = new GameObject(layerIds[layer] + (side == 0 ? "LeftLip" : "RightLip"));
                go.transform.SetParent(surfaces.transform, false);
                var mesh = new Mesh { name = go.name + "AuthoredSurface" }; mesh.MarkDynamic(); meshes.Add(mesh); lips[layer,side] = mesh;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var material = TissueRuntimeMaterial.Create(go.name, colors[layer]);
                material.SetFloat("_Glossiness", layer == 0 ? .32f : .62f); material.SetFloat("_Metallic", 0); materials.Add(material);
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                Shape(mesh, layer, side, 0, 0);
            }
            var ink = new GameObject("MeasuredMarkerInk"); ink.transform.SetParent(surfaces.transform, false);
            mark = ink.AddComponent<LineRenderer>(); mark.useWorldSpace = false; mark.positionCount = 0;
            mark.startWidth = mark.endWidth = .0012f; mark.numCapVertices = 3;
            inkMaterial = TissueRuntimeMaterial.Create("SurgicalMarker", new Color(.2f,.05f,.55f)); materials.Add(inkMaterial);
            mark.sharedMaterial = inkMaterial; mark.enabled = false;
            decisions = new GameObject("BaseDecisionLocations"); decisions.transform.SetParent(surfaces.transform, false);
            var basePoint = new GameObject("AnatomicalBasePoint"); basePoint.transform.SetParent(surfaces.transform, false);
            basePoint.transform.localPosition = new Vector3(-.022f,0,-.024f); BasePoint = basePoint.transform;
            decisions.SetActive(false);
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
            mark.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) mark.SetPosition(i, points[i] + Vector3.back*.0007f);
        }
        static readonly int WoundWorldToLocal = Shader.PropertyToID("_ScalpalWoundWorldToLocal"), WoundWindow = Shader.PropertyToID("_ScalpalWoundWindow");
        public void SetRegistrationValid(bool valid)
        {
            registered = valid; if (surfaces) surfaces.SetActive(valid);
            // The VR patient skin opens over this wound only while its wall is live (Scalpal/PatientSkin).
            Shader.SetGlobalMatrix(WoundWorldToLocal, transform.worldToLocalMatrix);
            Shader.SetGlobalFloat(WoundWindow, valid ? 1 : 0);
        }
        void OnDisable() => Shader.SetGlobalFloat(WoundWindow, 0);
        public void Apply(BodyState body)
        {
            Build(); surfaces.SetActive(registered && body != null); if (body == null) return;
            for (int layer = 0; layer < 5; layer++)
            {
                string id = layerIds[layer];
                bool closed = body.Get(id, "closed") > 0;
                float width = closed || body.Get(id,"opened") <= 0 ? 0 :
                    Mathf.Clamp(Mathf.Max((float)body.Get(id,"splitWidthMm")*.001f, .034f-layer*.005f), 0, .055f);
                float lift = !closed && body.Get(id,"tented") > 0 ? Mathf.Clamp((float)body.Get(id,"liftMm")*.001f, 0, .025f) : 0;
                if (Mathf.Abs(previousWidths[layer]-width) < .00001f && Mathf.Abs(previousLifts[layer]-lift) < .00001f) continue;
                for (int side = 0; side < 2; side++) Shape(lips[layer,side], layer, side, width, lift);
                previousWidths[layer] = width; previousLifts[layer] = lift;
            }
            marked = body.Get(layerIds[0],"marked") > 0 && body.Get(layerIds[0],"closed") == 0;
            mark.enabled = marked && mark.positionCount >= 2;
            bool poorMark = body.Get(layerIds[0],"markErrorMm") > 20 || body.Get(layerIds[0],"markAngleDegrees") > 25;
            inkMaterial.color = poorMark ? new Color(1,.55f,.06f) : new Color(.2f,.05f,.55f);
            bool answered = false;
            foreach (var choice in decisionChoices) if (body.Get(decisionTissueId,"decision_"+choice) > 0) answered = true;
            showDecision = decisionChoices.Length > 0 && body.Get(decisionTissueId,"delivered") > 0 && body.Get(decisionTissueId,"removed") == 0 && !answered;
            decisions.SetActive(showDecision);
        }
        public string DecisionAt(Vector3 localPoint)
        {
            if (!registered || !decisions || !showDecision) return "";
            foreach (Transform candidate in decisions.transform)
                if (candidate.gameObject.activeSelf && Vector3.Distance(localPoint, transform.InverseTransformPoint(candidate.position)) < .009f) return candidate.name;
            return "";
        }
        void Shape(Mesh mesh, int layer, int side, float width, float lift)
        {
            const int segments = 64, rows = 5;
            var vertices = new Vector3[(segments+1)*rows]; var uv = new Vector2[vertices.Length];
            var triangles = new List<int>(segments*(rows-1)*6); float sign = side == 0 ? -1 : 1;
            float thickness = layer == 1 ? .007f : .003f;
            for (int i = 0; i <= segments; i++)
            {
                float x = Mathf.Lerp(-.06f,.06f,i/(float)segments);
                float arch = Mathf.Sqrt(Mathf.Max(0,1-x*x/(.04f*.04f)));
                float edge = width*.5f*arch;
                float outer = .045f*Mathf.Sqrt(Mathf.Max(.08f,1-Mathf.Pow(x/.064f,8)));
                // Rounded wound edges, with a small original lobulated fat contour. The outer
                // coupon remains closed beyond the measured teaching incision's end points.
                float lobule = layer == 1 && width > 0 ? Mathf.Sin(i*1.75f)*.00065f*arch : 0;
                float z = depths[layer]-lift*arch;
                float bevel = width > 0 ? arch*.0013f : 0;
                vertices[i*rows] = new Vector3(x,sign*(edge+lobule),z+thickness);
                vertices[i*rows+1] = new Vector3(x,sign*(edge+lobule),z);
                vertices[i*rows+2] = new Vector3(x,sign*(edge+.003f*arch+lobule),z-bevel);
                vertices[i*rows+3] = new Vector3(x,sign*Mathf.Lerp(edge,outer,.35f),depths[layer]-.0007f*arch);
                vertices[i*rows+4] = new Vector3(x,sign*outer,depths[layer]);
                for (int j=0;j<rows;j++) uv[i*rows+j] = new Vector2(i/(float)segments,j/(float)(rows-1));
                if (i < segments) for(int j=0;j<rows-1;j++)
                {
                    int a=i*rows+j, b=a+rows, c=b+1, d=a+1;
                    // One consistently oriented face per surface. Reversed triangles sharing
                    // vertices cancel normals and create the bright broken ribbon artifact.
                    if(side==1) triangles.AddRange(new[]{a,c,b,a,d,c});
                    else triangles.AddRange(new[]{a,b,c,a,c,d});
                }
            }
            mesh.Clear(); mesh.vertices=vertices; mesh.uv=uv; mesh.SetTriangles(triangles,0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
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
