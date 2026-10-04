using System;
using System.Collections.Generic;
using Scalpal.Brand;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Shell
{
    // Shared cheap world-space primitives in the Scalpal brand: dark glass, hairlines, static SDF type.
    public static class ShellView
    {
        public static ScalpalBrand Brand { get; private set; }
        public static bool Configured => Brand;
        public static Material Glass => Brand ? Brand.glass : null;
        public static Material ButtonMaterial => Brand ? Brand.button : null;
        public static Material Accent => Brand ? Brand.accent : null;
        public static readonly Color Ink = ScalpalBrand.Ink;
        // Rounded geometry contract shared with Scalpal/Brand/Glass: radius = min(cap, height * fraction).
        public const float RadiusCap = .05f, CardRadiusFraction = .16f, ButtonRadiusFraction = .5f;
        // Legacy "height" parameters mean the old glyph height; em is 1.2x that, keeping layouts in place.
        const float EmPerHeight = 1.2f;
        static readonly Dictionary<Vector3, Mesh> meshes = new Dictionary<Vector3, Mesh>();
        public static void Configure(ScalpalBrand brand) { Brand = brand; }
        public static void SetText(TMP_Text text,string value)
        {
            if(!text)return;
            text.text=value??"";text.GetComponent<ScalpalTextFit>().Fit();
        }
        // Fixed physical line extent: trim width instead of silently shrinking accessibility text.
        public static TextMeshPro FixedText(Transform parent,string value,Vector3 position,float height,float width,ScalpalTextRole role=ScalpalTextRole.Body)
        {
            var text=Text(parent,value,position,height,width,TextAnchor.UpperLeft,role);
            var fit=text.GetComponent<ScalpalTextFit>();
            var face=text.font.faceInfo;
            // Em size whose ascender-to-descender line extent is exactly the requested height.
            fit.preferredSize=height*face.pointSize/(face.ascentLine-face.descentLine)/face.scale;
            fit.maximumHeight=1000;fit.maximumWidth=1000;fit.Fit();
            string original=value??"";
            float scale=Mathf.Max(.0001f,Mathf.Abs(text.transform.lossyScale.x));
            for(int remaining=original.Length;remaining>=1;remaining--)
            {
                text.text=remaining==original.Length?original:original.Substring(0,remaining).TrimEnd()+"…";
                if(text.GetPreferredValues(Mathf.Infinity,Mathf.Infinity).x*scale<=width)break;
            }
            fit.maximumWidth=width;fit.maximumHeight=height*1.02f;fit.Fit();
            return text;
        }
        public static Transform Panel(Transform parent, string name, Vector3 position, Vector2 size) => Surface(parent,name,position,size,Glass,CardRadiusFraction);
        static Transform Surface(Transform parent, string name, Vector3 position, Vector2 size, Material material, float fraction)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false); go.transform.localPosition = position;
            go.GetComponent<MeshFilter>().sharedMesh = Rounded(size,fraction);
            var renderer = go.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            return go.transform;
        }
        static Mesh Rounded(Vector2 size,float fraction)
        {
            var key=new Vector3(size.x,size.y,fraction);
            if (meshes.TryGetValue(key, out var cached) && cached) return cached;
            const int segments = 8, count = 4 * (segments + 1);
            float radius = Mathf.Min(RadiusCap, size.y * fraction, size.x * .5f);
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
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); meshes[key] = mesh; return mesh;
        }
        public static TextMeshPro Text(Transform parent, string value, Vector3 position, float height, float width, TextAnchor anchor = TextAnchor.UpperLeft, ScalpalTextRole role = ScalpalTextRole.Body, bool overlay = false)
        {
            int lines = Mathf.Max(1, (value ?? "").Split('\n').Length);
            var text = Brand.Text(parent, "Text", value, role, position, height * EmPerHeight, width, height * 1.6f * lines, anchor, overlay);
            text.GetComponent<ScalpalTextFit>().enabled = false;
            return text;
        }
        /// <summary>Brand button. primary: spark-accent outline at rest (one per surface). ghost: secondary outline-only action.</summary>
        public static ShellButton Button(Transform parent, string label, Vector3 position, Vector2 size, Action action, bool enabled = true, bool primary = false, bool ghost = false)
        {
            var panel = Surface(parent, "Button_" + label, position, size, ButtonMaterial, ButtonRadiusFraction);
            panel.gameObject.AddComponent<BoxCollider>().size = new Vector3(size.x, size.y, .009f);
            var button = panel.gameObject.AddComponent<ShellButton>(); button.action = action; button.interactable = enabled; button.primary = primary; button.ghost = ghost;
            button.label = Text(panel, label, new Vector3(0, 0, -.006f), .027f, size.x - .03f, TextAnchor.MiddleCenter, ScalpalTextRole.Label);
            // A label may use the button's full inner height so the 32 mm/m label floor is reachable.
            var fit = button.label.GetComponent<ScalpalTextFit>(); fit.maximumHeight = size.y * .9f; fit.Fit();
            button.Refresh();
            return button;
        }
        public static void Clear(Transform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            { var child = parent.GetChild(i).gameObject; child.SetActive(false); if (Application.isPlaying) UnityEngine.Object.Destroy(child); else UnityEngine.Object.DestroyImmediate(child); }
        }
    }
}
