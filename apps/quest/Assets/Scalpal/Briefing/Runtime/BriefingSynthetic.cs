using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>
    /// A small right-lower-quadrant model in the briefing atlas contract (merged mesh, part index in uv2.x, triangles
    /// ordered by part, metres, +Y up, +Z anterior, +X patient right). Used until the real atlas lands, and by validation.
    /// </summary>
    public static class BriefingSynthetic
    {
        struct Shape
        {
            public string id, name, system;
            public int layer;
            public bool box;
            public Vector3 center, size;
            public float roll;
            public Color color;
        }

        // Ids and layers follow the real atlas (briefing_parts.json), so the same steps drive both.
        static readonly Shape[] Shapes =
        {
            Box("skin", "Skin (abdominal wall)", "integumentary", 0, new Vector3(0, 0, .100f), new Vector3(.32f, .28f, .008f), new Color(.87f, .70f, .62f)),
            Blob("umbilicus", "Umbilicus", "integumentary", 0, new Vector3(0, .040f, .108f), new Vector3(.016f, .016f, .008f), 0, new Color(.70f, .50f, .45f)),
            Box("subcutaneous_fat", "Subcutaneous fat", "integumentary", 1, new Vector3(0, 0, .089f), new Vector3(.30f, .26f, .012f), new Color(.96f, .86f, .55f)),
            Box("external_oblique_aponeurosis_r", "External oblique aponeurosis (right)", "muscular", 2, new Vector3(.07f, 0, .080f), new Vector3(.15f, .25f, .004f), new Color(.90f, .90f, .86f)),
            Box("muscular__internal_abdominal_oblique_muscle_r", "Internal oblique muscle (right)", "muscular", 3, new Vector3(.07f, 0, .072f), new Vector3(.14f, .24f, .008f), new Color(.72f, .30f, .30f)),
            Box("muscular__transversus_abdominis_muscle_r", "Transversus abdominis (right)", "muscular", 4, new Vector3(.07f, 0, .063f), new Vector3(.14f, .23f, .008f), new Color(.64f, .26f, .28f)),
            Box("peritoneum", "Parietal peritoneum", "peritoneal", 6, new Vector3(0, 0, .056f), new Vector3(.26f, .22f, .003f), new Color(.90f, .82f, .78f)),
            Box("greater_omentum", "Greater omentum", "digestive", 7, new Vector3(-.02f, .03f, .046f), new Vector3(.20f, .14f, .006f), new Color(.95f, .85f, .55f)),
            Blob("visceral__jejunum", "Small bowel (jejunum and ileum)", "digestive", 8, new Vector3(-.03f, -.02f, .034f), new Vector3(.16f, .10f, .020f), 0, new Color(.82f, .62f, .56f)),
            Blob("cecum", "Cecum", "digestive", 8, new Vector3(.070f, -.050f, .010f), new Vector3(.070f, .080f, .060f), 0, new Color(.78f, .55f, .50f)),
            Blob("appendix", "Vermiform appendix", "digestive", 8, new Vector3(.055f, -.115f, .016f), new Vector3(.016f, .070f, .016f), 25, new Color(.86f, .45f, .40f)),
            Blob("mesoappendix", "Mesoappendix", "digestive", 8, new Vector3(.032f, -.105f, .012f), new Vector3(.036f, .060f, .006f), 20, new Color(.95f, .80f, .55f)),
            Blob("appendicular_artery", "Appendicular artery", "vascular", 8, new Vector3(.028f, -.100f, .018f), new Vector3(.005f, .060f, .005f), 20, new Color(.80f, .16f, .16f)),
            Blob("terminal_ileum", "Terminal ileum", "digestive", 8, new Vector3(-.005f, -.060f, .000f), new Vector3(.120f, .028f, .028f), 0, new Color(.80f, .60f, .55f)),
            Blob("skeletal__hip_bone_r", "Right hip bone (ASIS)", "skeletal", 10, new Vector3(.120f, -.090f, .040f), new Vector3(.040f, .060f, .050f), 0, new Color(.92f, .90f, .84f)),
        };

        static Shape Box(string id, string name, string system, int layer, Vector3 center, Vector3 size, Color color) =>
            new Shape { id = id, name = name, system = system, layer = layer, box = true, center = center, size = size, color = color };
        static Shape Blob(string id, string name, string system, int layer, Vector3 center, Vector3 size, float roll, Color color) =>
            new Shape { id = id, name = name, system = system, layer = layer, box = false, center = center, size = size, roll = roll, color = color };

        public static void Build(out Mesh mesh, out BriefingPartManifest manifest)
        {
            var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var parts = new List<Vector2>(); var triangles = new List<int>();
            var infos = new BriefingPartInfo[Shapes.Length];
            for (int i = 0; i < Shapes.Length; i++)
            {
                var shape = Shapes[i];
                int firstVertex = vertices.Count, firstTriangle = triangles.Count / 3;
                if (shape.box) AddBox(shape, vertices, normals, triangles); else AddEllipsoid(shape, vertices, normals, triangles);
                for (int v = firstVertex; v < vertices.Count; v++) parts.Add(new Vector2(i, 0));
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                for (int v = firstVertex; v < vertices.Count; v++) { lo = Vector3.Min(lo, vertices[v]); hi = Vector3.Max(hi, vertices[v]); }
                var center = (lo + hi) * .5f;
                infos[i] = new BriefingPartInfo
                {
                    index = i, id = shape.id, name = shape.name, system = shape.system, layer = shape.layer, synthetic = true,
                    center = new[] { center.x, center.y, center.z }, min = new[] { lo.x, lo.y, lo.z }, max = new[] { hi.x, hi.y, hi.z },
                    triStart = firstTriangle, triCount = triangles.Count / 3 - firstTriangle, color = new[] { shape.color.r, shape.color.g, shape.color.b },
                };
            }
            mesh = new Mesh { name = "BriefingAtlasSynthetic", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(1, parts); mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            manifest = new BriefingPartManifest { version = 1, parts = infos };
        }

        static void AddBox(Shape shape, List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            var half = shape.size * .5f;
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    var n = Vector3.zero; n[axis] = sign;
                    var u = Vector3.zero; u[(axis + 1) % 3] = 1;
                    var w = Vector3.Cross(n, u);
                    int start = vertices.Count;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        float a = corner == 0 || corner == 3 ? -1 : 1, b = corner < 2 ? -1 : 1;
                        var local = n + u * a + w * b;
                        vertices.Add(shape.center + Vector3.Scale(local, half)); normals.Add(n);
                    }
                    triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
                    triangles.Add(start); triangles.Add(start + 3); triangles.Add(start + 2);
                }
        }

        static void AddEllipsoid(Shape shape, List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            const int rings = 10, segments = 14;
            var radius = shape.size * .5f; var roll = Quaternion.Euler(0, 0, shape.roll);
            int start = vertices.Count;
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s <= segments; s++)
                {
                    float theta = 2 * Mathf.PI * s / segments;
                    var unit = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                    vertices.Add(shape.center + roll * Vector3.Scale(unit, radius));
                    normals.Add((roll * new Vector3(unit.x / Mathf.Max(1e-4f, radius.x), unit.y / Mathf.Max(1e-4f, radius.y), unit.z / Mathf.Max(1e-4f, radius.z))).normalized);
                }
            }
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = start + r * (segments + 1) + s, b = a + segments + 1;
                    triangles.Add(a); triangles.Add(a + 1); triangles.Add(b);
                    triangles.Add(a + 1); triangles.Add(b + 1); triangles.Add(b);
                }
        }
    }
}
