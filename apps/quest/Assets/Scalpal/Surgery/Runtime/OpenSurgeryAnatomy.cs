using System;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Source-atlas authoring metadata. These landmarks never describe a real participant's organs.
    // Prefer explicit owned references. The fallback is a reproducible, unreviewed teaching axis.
    [DisallowMultipleComponent]
    public sealed class OpenSurgeryAnatomy : MonoBehaviour
    {
        public string provenance = "unreviewed teaching approximation";
        public string sourceMesh = "";
        public string referenceMesh = "";
        public string status = "unbound";
        public float sourceGapMm, axisLengthMm;
        public bool Bound { get; private set; }
        const string BaseName = "GenericTeaching_Base_NearestCecumSurface";
        const string TipName = "GenericTeaching_End_FarthestSourceVertex";
        static readonly string[] TargetIds = { "appendix", "mesoappendix", "appendicular_artery" };

        // Does not move organs, modify source assets, or acquire a deformation handle.
        public static bool Bind(AnatomyController anatomy, OpenBodyInteraction interaction)
        {
            if (!anatomy || !interaction || !anatomy.TryGetPart("cecum", out var cecum) || !TryMesh(cecum, out var cecumFilter, out var cecumMesh)) return false;
            if (!WorldVertices(cecumFilter, cecumMesh, out var cecumVertices)) return false;
            int[] cecumTriangles = cecumMesh.triangles;
            if (cecumTriangles.Length == 0 || cecumTriangles.Length % 3 != 0 || cecumTriangles.Length > 60000) return false;
            foreach (int index in cecumTriangles) if (index < 0 || index >= cecumVertices.Length) return false;
            bool allBound = true;
            foreach (string id in TargetIds)
            {
                SurgeryTissueTarget target = null; bool duplicate = false;
                foreach (var candidate in interaction.Targets)
                {
                    if (!candidate || candidate.tissueId != id) continue;
                    if (target) duplicate = true;
                    target = candidate;
                }
                if (!target || duplicate || !anatomy.TryGetPart(id, out var part) || target.GetComponent<AnatomyPart>() != part)
                { allBound = false; continue; }
                var metadata = target.GetComponent<OpenSurgeryAnatomy>() ?? target.gameObject.AddComponent<OpenSurgeryAnatomy>();
                metadata.Bound = false;
                if (!TryMesh(part, out var filter, out var mesh) || !WorldVertices(filter, mesh, out var vertices))
                { metadata.status = "missing, ambiguous, unreadable, or nonfinite source mesh"; target.basePoint = target.endPoint = null; allBound = false; continue; }
                metadata.sourceMesh = mesh.name; metadata.referenceMesh = cecumMesh.name;
                // Explicit scene references take precedence. Existing generated references are refreshed
                // from the committed rest mesh on retry, not from a possibly deformed runtime mesh.
                bool existing = target.basePoint && target.endPoint;
                bool generated = existing && target.basePoint.name == BaseName && target.endPoint.name == TipName;
                if (existing && !generated)
                {
                    bool owned = target.basePoint.IsChildOf(part.transform) && target.endPoint.IsChildOf(part.transform);
                    float length = Vector3.Distance(target.basePoint.position, target.endPoint.position);
                    if (owned && Finite(target.basePoint.position) && Finite(target.endPoint.position) && SaneLength(length))
                    {
                        metadata.provenance = "explicit source-scene references; clinical review not established";
                        metadata.axisLengthMm = length * 1000; metadata.status = "bound authored references"; metadata.Bound = true;
                        continue;
                    }
                    metadata.status = "invalid or foreign authored references"; target.basePoint = target.endPoint = null; allBound = false; continue;
                }
                // A lone reference is an authoring error; do not silently invent its partner.
                if ((target.basePoint != null) != (target.endPoint != null))
                { metadata.status = "incomplete authored references"; target.basePoint = target.endPoint = null; allBound = false; continue; }
                if ((long)vertices.Length * (cecumTriangles.Length / 3) > 8000000)
                { metadata.status = "source pair exceeds bounded authoring search; supply reviewed references"; target.basePoint = target.endPoint = null; allBound = false; continue; }
                int nearest = -1; float best = float.PositiveInfinity;
                for (int v = 0; v < vertices.Length; v++)
                {
                    for (int t = 0; t < cecumTriangles.Length; t += 3)
                    {
                        Vector3 point = ClosestOnTriangle(vertices[v], cecumVertices[cecumTriangles[t]], cecumVertices[cecumTriangles[t + 1]], cecumVertices[cecumTriangles[t + 2]]);
                        float distance = (vertices[v] - point).sqrMagnitude;
                        if (distance < best) { best = distance; nearest = v; }
                    }
                }
                if (nearest < 0 || !float.IsFinite(best) || best > .08f * .08f)
                { metadata.status = "source structure is too far from cecum for a teaching reference"; target.basePoint = target.endPoint = null; allBound = false; continue; }
                Vector3 start = vertices[nearest], end = start; float farthest = 0;
                foreach (Vector3 vertex in vertices)
                {
                    float distance = (vertex - start).sqrMagnitude;
                    if (distance > farthest) { farthest = distance; end = vertex; }
                }
                float axisLength = Mathf.Sqrt(farthest);
                if (!SaneLength(axisLength) || !Finite(start) || !Finite(end))
                { metadata.status = "source axis outside 2–300 mm teaching bounds"; target.basePoint = target.endPoint = null; allBound = false; continue; }
                var baseTransform = UniqueChild(part.transform, BaseName, out bool badBase);
                var endTransform = UniqueChild(part.transform, TipName, out bool badEnd);
                if (badBase || badEnd)
                { metadata.status = "ambiguous generated reference transforms"; target.basePoint = target.endPoint = null; allBound = false; continue; }
                if (!baseTransform) { baseTransform = new GameObject(BaseName).transform; baseTransform.SetParent(part.transform, false); }
                if (!endTransform) { endTransform = new GameObject(TipName).transform; endTransform.SetParent(part.transform, false); }
                // TransformPoint handles the FBX import scale and common registration transform;
                // distances above are Unity world metres. Stored child positions remain source-local.
                baseTransform.localPosition = part.transform.InverseTransformPoint(start);
                endTransform.localPosition = part.transform.InverseTransformPoint(end);
                target.basePoint = baseTransform; target.endPoint = endTransform;
                metadata.provenance = "unreviewed teaching approximation";
                metadata.sourceGapMm = Mathf.Sqrt(best) * 1000; metadata.axisLengthMm = axisLength * 1000;
                metadata.status = "nearest cecum surface / farthest source vertex; not a reviewed surgical base";
                metadata.Bound = true;
            }
            return allBound;
        }

        // Conservative reach bound only. A negative margin establishes impossibility with this cage;
        // a positive margin does not prove that grasp/contact constraints allow the motion.
        public static bool DeliveryReachBound(AnatomyController anatomy, Transform wound, out float requiredMm, out float maximumMm)
        {
            requiredMm = maximumMm = 0;
            if (!anatomy || !wound || !anatomy.TryGetPart("appendix", out var part) || !TryMesh(part, out var filter, out var mesh)
                || !WorldVertices(filter, mesh, out var vertices)) return false;
            var tissue = part.GetComponent<DeformableTissue>();
            if (!tissue || tissue.Cage == null || tissue.SourceUnitScale <= 0) return false;
            float depth = float.PositiveInfinity;
            foreach (Vector3 vertex in vertices) depth = Mathf.Min(depth, Vector3.Dot(vertex - wound.position, wound.forward));
            float physicalScale = Mathf.Max(tissue.transform.TransformVector(Vector3.right / tissue.SourceUnitScale).magnitude,
                Mathf.Max(tissue.transform.TransformVector(Vector3.up / tissue.SourceUnitScale).magnitude,
                tissue.transform.TransformVector(Vector3.forward / tissue.SourceUnitScale).magnitude));
            requiredMm = Mathf.Max(0, depth + .015f) * 1000;
            maximumMm = tissue.Cage.Preset.maxDisplacement * physicalScale * 1000;
            return float.IsFinite(requiredMm) && float.IsFinite(maximumMm);
        }
        static bool SaneLength(float metres) => float.IsFinite(metres) && metres >= .002f && metres <= .3f;
        static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        static Transform UniqueChild(Transform parent, string name, out bool ambiguous)
        {
            Transform found = null; ambiguous = false;
            foreach (Transform child in parent)
            {
                if (child.name != name) continue;
                if (found) ambiguous = true;
                found = child;
            }
            return found;
        }
        static bool TryMesh(AnatomyPart part, out MeshFilter filter, out Mesh mesh)
        {
            filter = null; mesh = null;
            foreach (var candidate in part.GetComponentsInChildren<MeshFilter>(true))
            {
                if (candidate.GetComponentInParent<AnatomyPart>(true) != part || !candidate.sharedMesh ||
                    candidate.GetComponentInParent<SurgicalVisualGeometry>(true)) continue;
                if (filter) return false;
                filter = candidate;
            }
            if (!filter) return false;
            var deformation = filter.GetComponent<DeformableTissue>();
            mesh = deformation && deformation.SourceMesh ? deformation.SourceMesh : filter.sharedMesh;
            return mesh && mesh.isReadable && mesh.vertexCount >= 3 && mesh.vertexCount <= 20000;
        }
        static bool WorldVertices(MeshFilter filter, Mesh mesh, out Vector3[] vertices)
        {
            vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = filter.transform.TransformPoint(vertices[i]);
                if (!Finite(vertices[i])) return false;
            }
            return true;
        }
        static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 edge = b - a; float length = edge.sqrMagnitude;
            return length < 1e-14f ? a : a + edge * Mathf.Clamp01(Vector3.Dot(p - a, edge) / length);
        }
        static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a;
            if (Vector3.Cross(ab, ac).sqrMagnitude < 1e-16f)
            {
                Vector3 x = ClosestOnSegment(p, a, b), y = ClosestOnSegment(p, a, c), z = ClosestOnSegment(p, b, c);
                return (p - x).sqrMagnitude < (p - y).sqrMagnitude ? ((p - x).sqrMagnitude < (p - z).sqrMagnitude ? x : z)
                    : ((p - y).sqrMagnitude < (p - z).sqrMagnitude ? y : z);
            }
            Vector3 ap = p - a; float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float inverse = 1 / (va + vb + vc);
            return a + ab * (vb * inverse) + ac * (vc * inverse);
        }
    }
}
