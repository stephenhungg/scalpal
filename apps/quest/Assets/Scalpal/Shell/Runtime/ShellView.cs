using System;
using System.Collections.Generic;
using Scalpal.EncounterOffice;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Shell
{
    // Shared cheap world-space primitives. Assets come from the office; no blur or canvas per card.
    public static class ShellView
    {
        public static Font Font { get; private set; }
        public static Material Glass { get; private set; }
        public static Material ButtonMaterial { get; private set; }
        public static Material TextMaterial { get; private set; }
        public static Material Accent { get; private set; }
        public static readonly Color Ink = new Color(.94f, .95f, .97f);
        static readonly Dictionary<Vector2, Mesh> meshes = new Dictionary<Vector2, Mesh>();
        public static void Configure(Font font, Material glass, Material button, Material text, Material accent)
        { Font = font; Glass = glass; ButtonMaterial = button; TextMaterial = text; Accent = accent; }
        public static Transform Panel(Transform parent, string name, Vector3 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false); go.transform.localPosition = position;
            go.GetComponent<MeshFilter>().sharedMesh = Rounded(size);
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = Glass;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return go.transform;
        }
        static Mesh Rounded(Vector2 size)
        {
            if (meshes.TryGetValue(size, out var cached) && cached) return cached;
            const int segments = 6, count = 4 * (segments + 1);
            float radius = Mathf.Min(.022f, size.y * .16f);
            var vertices = new Vector3[count + 1]; var uv = new Vector2[count + 1]; var triangles = new int[count * 3];
            uv[0] = Vector2.one * .5f;
            for (int corner = 0; corner < 4; corner++)
            {
                float cx = (corner == 0 || corner == 3 ? 1 : -1) * (size.x / 2 - radius);
                float cy = (corner < 2 ? 1 : -1) * (size.y / 2 - radius);
                for (int j = 0; j <= segments; j++)
                {
                    int i = corner * (segments + 1) + j + 1;
                    float angle = (corner * 90 + j * 90f / segments) * Mathf.Deg2Rad;
                    vertices[i] = new Vector3(cx + Mathf.Cos(angle) * radius, cy + Mathf.Sin(angle) * radius, 0);
                    uv[i] = new Vector2(vertices[i].x / size.x + .5f, vertices[i].y / size.y + .5f);
                    triangles[(i - 1) * 3] = 0; triangles[(i - 1) * 3 + 1] = i == count ? 1 : i + 1; triangles[(i - 1) * 3 + 2] = i;
                }
            }
            var mesh = new Mesh { name = "Shell rounded " + size, vertices = vertices, uv = uv, triangles = triangles };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); meshes[size] = mesh; return mesh;
        }
        public static TextMesh Text(Transform parent, string value, Vector3 position, float height, float width, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            var text = new GameObject("Text").AddComponent<TextMesh>(); text.transform.SetParent(parent, false);
            text.transform.localPosition = position; text.text = value; text.anchor = anchor; text.font = Font;
            text.fontSize = 64; text.color = Ink; text.richText = false;
            text.GetComponent<Renderer>().sharedMaterial = TextMaterial;
            var fit = text.gameObject.AddComponent<EncounterOfficeText>();
            fit.preferredCharacterSize = height * .19f; fit.maximumWidth = width;
            fit.maximumHeight = height * 1.5f * Mathf.Max(1, value.Split('\n').Length); fit.Fit();
            return text;
        }
        public static ShellButton Button(Transform parent, string label, Vector3 position, Vector2 size, Action action, bool enabled = true)
        {
            var panel = Panel(parent, "Button_" + label, position, size);
            panel.GetComponent<Renderer>().sharedMaterial = ButtonMaterial;
            panel.gameObject.AddComponent<BoxCollider>().size = new Vector3(size.x, size.y, .009f);
            var button = panel.gameObject.AddComponent<ShellButton>(); button.action = action; button.interactable = enabled;
            button.label = Text(panel, label, new Vector3(0, 0, -.006f), .027f, size.x - .018f, TextAnchor.MiddleCenter);
            button.label.color=enabled?Ink:new Color(.60f,.62f,.67f);
            return button;
        }
        public static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            { var child = parent.GetChild(i).gameObject; child.SetActive(false); if (Application.isPlaying) UnityEngine.Object.Destroy(child); else UnityEngine.Object.DestroyImmediate(child); }
        }
    }
}
