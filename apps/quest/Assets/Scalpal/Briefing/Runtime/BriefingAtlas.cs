using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>Manifest row (briefing_parts.json). Geometry is recomputed from the mesh; the manifest names and colors parts.</summary>
    [Serializable]
    public sealed class BriefingPartInfo
    {
        public int index, triStart, triCount;
        public string id = "", name = "", system = "";
        public int layer; // depth order, 0 = skin (outermost)
        public float[] center, min, max, color;
        public bool synthetic;
    }

    [Serializable]
    public sealed class BriefingPartManifest
    {
        public int version;
        public BriefingPartInfo[] parts;
    }

    /// <summary>
    /// One merged mesh, one material, one draw call. Each vertex carries its part index in uv2.x; a point-filtered
    /// data texture (one column per part) holds every part's animated transform, alpha, glow and color, which the
    /// BriefingAtlas shader applies in the vertex stage. The CPU only rewrites the texture rows.
    /// Rows: 0 = offset.xyz + alpha, 1 = scale, glow, spin angle (rad), highlight, 2 = pivot (part center), 3 = color.
    /// The part transform (shader and picker): p' = Ry(angle) * ((p - center) * scale) + center + offset.
    /// </summary>
    public sealed class BriefingAtlas : MonoBehaviour
    {
        public const int Rows = 4;
        public const float VisibleAlpha = .05f, HiddenAlpha = .004f;
        public const string ManifestResource = "briefing_parts", SourceResource = "BriefingAtlasSource";

        public sealed class Part
        {
            public int index;
            public string id, name, system;
            public int layer;
            public Vector3 center, min, max;
            public Color color;
            public int[] triangles; // vertex indices of this part's triangles (3 per triangle)
            public Vector3 Size => max - min;
        }

        /// <summary>Animated state of one part. Offset is derived from the focus group pivot, lift and peel.</summary>
        public struct PartState
        {
            public float alpha, scale, glow, angle, highlight;
            public Vector3 lift, peel, pivot;
            public bool spin; // only when the lifted, scaled group can turn without swinging into the torso
        }

        public Part[] Parts { get; private set; } = Array.Empty<Part>();
        public PartState[] Current { get; private set; } = Array.Empty<PartState>();
        public PartState[] Target { get; private set; } = Array.Empty<PartState>();
        public Texture2D Data { get; private set; }
        public Mesh Mesh { get; private set; }
        public Material Material { get; private set; }
        public MeshRenderer Renderer { get; private set; }
        public bool Synthetic { get; private set; }
        public Bounds RestBounds { get; private set; }
        /// <summary>Mesh-local direction out through the abdominal wall (the atlas contract: +Z anterior).</summary>
        public Vector3 Anterior => Vector3.forward;
        public float spinRadiansPerSecond = .45f, smoothing = 5f;

        Vector3[] vertices;
        Color[] pixels;
        readonly Dictionary<string, int> byId = new Dictionary<string, int>();
        readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        /// <summary>Loads the real atlas (BriefingAtlasSource mesh + briefing_parts.json) or, until it lands, the synthetic one.</summary>
        public static BriefingAtlas Create(Transform parent, bool forceSynthetic = false)
        {
            var source = Resources.Load<BriefingAtlasSource>(SourceResource);
            var material = source ? source.material : null;
            Mesh mesh = null; BriefingPartManifest manifest = null; bool synthetic = true;
            if (!forceSynthetic && source && source.mesh)
            {
                var file = Resources.Load<TextAsset>(ManifestResource);
                try { manifest = file ? JsonUtility.FromJson<BriefingPartManifest>(file.text) : null; }
                catch (ArgumentException exception) { Debug.LogWarning("SCALPAL_BRIEFING_MANIFEST_INVALID " + exception.Message); }
                if (manifest?.parts != null && manifest.parts.Length > 0 && source.mesh.isReadable) { mesh = source.mesh; synthetic = false; }
                else Debug.LogWarning("SCALPAL_BRIEFING_ATLAS_FALLBACK manifest=" + (manifest?.parts?.Length ?? 0) + " readable=" + source.mesh.isReadable);
            }
            if (synthetic) BriefingSynthetic.Build(out mesh, out manifest);
            var go = new GameObject("BriefingAtlas", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            var atlas = go.AddComponent<BriefingAtlas>();
            atlas.Initialize(mesh, manifest, material, synthetic);
            return atlas;
        }

        public void Initialize(Mesh mesh, BriefingPartManifest manifest, Material template, bool synthetic)
        {
            Mesh = mesh; Synthetic = synthetic;
            if (synthetic) owned.Add(mesh);
            vertices = mesh.vertices;
            var uv2 = new List<Vector2>(); mesh.GetUVs(1, uv2);
            var indices = mesh.triangles;
            if (uv2.Count != vertices.Length) throw new InvalidOperationException("Briefing atlas mesh has no part index in uv2.");
            int count = 0;
            foreach (var info in manifest.parts) count = Mathf.Max(count, info.index + 1);
            foreach (var uv in uv2) count = Mathf.Max(count, Mathf.RoundToInt(uv.x) + 1);
            // Part membership comes from the vertices themselves, so import reordering or unit conversion cannot misassign triangles.
            var lists = new List<int>[count];
            for (int i = 0; i < count; i++) lists[i] = new List<int>();
            for (int t = 0; t < indices.Length; t += 3)
            {
                int part = Mathf.Clamp(Mathf.RoundToInt(uv2[indices[t]].x), 0, count - 1);
                lists[part].Add(indices[t]); lists[part].Add(indices[t + 1]); lists[part].Add(indices[t + 2]);
            }
            Parts = new Part[count]; byId.Clear();
            for (int i = 0; i < count; i++)
            {
                var part = new Part { index = i, id = "part_" + i, name = "Part " + i, system = "", layer = 99, color = new Color(.8f, .7f, .65f), triangles = lists[i].ToArray() };
                Parts[i] = part;
            }
            foreach (var info in manifest.parts)
            {
                if (info == null || info.index < 0 || info.index >= count) continue;
                var part = Parts[info.index];
                part.id = string.IsNullOrEmpty(info.id) ? part.id : info.id;
                part.name = string.IsNullOrEmpty(info.name) ? part.id : info.name;
                part.system = info.system ?? ""; part.layer = info.layer;
                if (info.color != null && info.color.Length >= 3) part.color = new Color(info.color[0], info.color[1], info.color[2]);
            }
            var all = new Bounds(); bool any = false;
            foreach (var part in Parts)
            {
                if (part.triangles.Length == 0) { part.min = part.max = part.center = Vector3.zero; continue; }
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                foreach (int v in part.triangles) { lo = Vector3.Min(lo, vertices[v]); hi = Vector3.Max(hi, vertices[v]); }
                part.min = lo; part.max = hi; part.center = (lo + hi) * .5f;
                if (!any) { all = new Bounds(part.center, hi - lo); any = true; } else all.Encapsulate(new Bounds(part.center, hi - lo));
                byId[part.id] = part.index;
            }
            RestBounds = all;
            Current = new PartState[count]; Target = new PartState[count];
            for (int i = 0; i < count; i++) Current[i] = Target[i] = new PartState { alpha = Parts[i].triangles.Length > 0 ? 1 : 0, scale = 1, pivot = Parts[i].center };
            Data = new Texture2D(count, Rows, TextureFormat.RGBAHalf, false, true) { name = "BriefingAtlasParts", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            owned.Add(Data);
            pixels = new Color[count * Rows];
            Material = template ? new Material(template) : new Material(Shader.Find("Scalpal/BriefingAtlas"));
            Material.name = "BriefingAtlas (instance)"; owned.Add(Material);
            Material.SetTexture("_PartTex", Data); Material.SetFloat("_PartCount", count);
            GetComponent<MeshFilter>().sharedMesh = mesh;
            Renderer = GetComponent<MeshRenderer>();
            Renderer.sharedMaterial = Material;
            Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; Renderer.receiveShadows = false;
            Renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off; Renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            // Animated parts move outside the rest bounds; keep the one renderer from being frustum-culled early.
            Renderer.localBounds = new Bounds(all.center, all.size * 3.5f + Vector3.one * .3f);
            Upload();
        }

        public bool TryFind(string token, out int index) => byId.TryGetValue(token ?? "", out index);

        /// <summary>Adds the parts named by id; unknown ids are reported by validation, never guessed.</summary>
        public void Resolve(IEnumerable<string> ids, ICollection<int> into)
        {
            if (ids == null) return;
            foreach (var id in ids)
                if (!string.IsNullOrEmpty(id) && byId.TryGetValue(id, out int index) && !into.Contains(index)) into.Add(index);
        }

        /// <summary>Adds every part at or above a wall depth (layer <= throughLayer); -1 adds none.</summary>
        public void PeelThrough(int throughLayer, ICollection<int> into)
        {
            foreach (var part in Parts) if (part.layer <= throughLayer && part.triangles.Length > 0 && !into.Contains(part.index)) into.Add(part.index);
        }

        public bool IsVisible(int index) => index >= 0 && index < Current.Length && Current[index].alpha > VisibleAlpha && Parts[index].triangles.Length > 0;

        public static Quaternion Spin(float angle) => Quaternion.AngleAxis(angle * Mathf.Rad2Deg, Vector3.up);

        /// <summary>Offset column of the data texture: group scale/spin about the pivot, plus lift and peel.</summary>
        public Vector3 Offset(int index) => Offset(Parts[index], Current[index]);
        static Vector3 Offset(Part part, in PartState state) =>
            Spin(state.angle) * ((part.center - state.pivot) * state.scale) + state.pivot - part.center + state.lift + state.peel;

        /// <summary>Mesh-local rest point to its animated mesh-local position (identical to the shader).</summary>
        public Vector3 Apply(int index, Vector3 rest)
        {
            var state = Current[index]; var part = Parts[index];
            return Spin(state.angle) * ((rest - part.center) * state.scale) + part.center + Offset(part, state);
        }

        /// <summary>Animated mesh-local position back to the part's rest space.</summary>
        public Vector3 Unapply(int index, Vector3 animated)
        {
            var state = Current[index]; var part = Parts[index];
            return Quaternion.Inverse(Spin(state.angle)) * ((animated - part.center - Offset(part, state)) / Mathf.Max(1e-4f, state.scale)) + part.center;
        }

        /// <summary>The 8 corners of a part's rest box under its current animated transform, in world space.</summary>
        public void Corners(int index, Vector3[] into)
        {
            var part = Parts[index];
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? part.min.x : part.max.x, (i & 2) == 0 ? part.min.y : part.max.y, (i & 4) == 0 ? part.min.z : part.max.z);
                into[i] = transform.TransformPoint(Apply(index, corner));
            }
        }

        /// <summary>
        /// Nearest visible part along a world ray. Broad phase: the ray in each part's rest space against its rest box.
        /// Narrow phase: ray-triangle over that part's own triangles. The inverse part transform keeps the ray parameter,
        /// so hits on different parts compare directly.
        /// </summary>
        public bool Raycast(Ray world, float maximumDistance, out int hitPart, out Vector3 hitPoint)
        {
            hitPart = -1; hitPoint = default;
            if (Parts.Length == 0 || vertices == null) return false;
            var origin = transform.InverseTransformPoint(world.origin);
            var direction = transform.InverseTransformVector(world.direction * maximumDistance);
            float best = 1;
            for (int i = 0; i < Parts.Length; i++)
            {
                if (!IsVisible(i)) continue;
                var part = Parts[i]; var state = Current[i];
                float scale = Mathf.Max(1e-4f, state.scale);
                var inverse = Quaternion.Inverse(Spin(state.angle));
                var o = Unapply(i, origin);
                var d = inverse * direction / scale;
                if (!Slab(o, d, part.min, part.max, out float enter) || enter >= best) continue;
                var triangles = part.triangles;
                for (int t = 0; t < triangles.Length; t += 3)
                    if (Triangle(o, d, vertices[triangles[t]], vertices[triangles[t + 1]], vertices[triangles[t + 2]], out float distance) && distance < best)
                    { best = distance; hitPart = i; }
            }
            if (hitPart < 0) return false;
            hitPoint = world.origin + world.direction * (best * maximumDistance);
            return true;
        }

        static bool Slab(Vector3 o, Vector3 d, Vector3 min, Vector3 max, out float enter)
        {
            enter = 0; float exit = 1;
            for (int axis = 0; axis < 3; axis++)
            {
                float origin = o[axis], direction = d[axis], lo = min[axis] - 1e-4f, hi = max[axis] + 1e-4f;
                if (Mathf.Abs(direction) < 1e-9f) { if (origin < lo || origin > hi) return false; continue; }
                float a = (lo - origin) / direction, b = (hi - origin) / direction;
                if (a > b) { var swap = a; a = b; b = swap; }
                enter = Mathf.Max(enter, a); exit = Mathf.Min(exit, b);
                if (enter > exit) return false;
            }
            return true;
        }

        // Möller–Trumbore, two-sided; t is in units of the (unnormalized) direction.
        static bool Triangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            var e1 = b - a; var e2 = c - a; var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            float inv = 1 / det; var s = o - a;
            float u = Vector3.Dot(s, p) * inv; if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv; if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t >= 0;
        }

        /// <summary>Eases the animated state toward the targets. Focused parts keep spinning; others unwind to rest.</summary>
        public void Step(float dt)
        {
            float k = 1 - Mathf.Exp(-smoothing * Mathf.Max(0, dt));
            const float TwoPi = Mathf.PI * 2;
            for (int i = 0; i < Current.Length; i++)
            {
                var c = Current[i]; var t = Target[i];
                c.alpha = Mathf.Lerp(c.alpha, t.alpha, k); if (Mathf.Abs(c.alpha - t.alpha) < .002f) c.alpha = t.alpha;
                c.scale = Mathf.Lerp(c.scale, t.scale, k); c.glow = Mathf.Lerp(c.glow, t.glow, k);
                c.highlight = Mathf.Lerp(c.highlight, t.highlight, Mathf.Min(1, k * 2.5f));
                c.lift = Vector3.Lerp(c.lift, t.lift, k); c.peel = Vector3.Lerp(c.peel, t.peel, k);
                c.pivot = t.pivot;
                if (t.glow > .5f && t.spin) c.angle = Mathf.Repeat(c.angle + spinRadiansPerSecond * dt, TwoPi);
                else
                {
                    float rest = c.angle > Mathf.PI ? TwoPi : 0;
                    c.angle = Mathf.Lerp(c.angle, rest, k); if (Mathf.Abs(c.angle - rest) < .001f) c.angle = 0;
                }
                Current[i] = c;
            }
            Upload();
        }

        /// <summary>Jumps every part to its target (validation, and skipping an animation).</summary>
        public void Snap()
        {
            for (int i = 0; i < Current.Length; i++) { var t = Target[i]; t.angle = Current[i].glow > .5f || t.glow > .5f ? Current[i].angle : 0; Current[i] = t; }
            Upload();
        }

        public void SetTarget(int index, PartState state) { if (index >= 0 && index < Target.Length) Target[index] = state; }

        public void SetHighlight(int index)
        {
            for (int i = 0; i < Target.Length; i++) Target[i].highlight = i == index ? 1 : 0;
        }

        /// <summary>Writes the texture rows from the current state (SetPixels + Apply, once per frame).</summary>
        public void Upload()
        {
            if (!Data) return;
            int w = Parts.Length;
            for (int i = 0; i < w; i++)
            {
                var s = Current[i]; var part = Parts[i]; var offset = Offset(part, s);
                pixels[i] = new Color(offset.x, offset.y, offset.z, part.triangles.Length > 0 ? s.alpha : 0);
                pixels[w + i] = new Color(s.scale, s.glow, s.angle, s.highlight);
                pixels[w * 2 + i] = new Color(part.center.x, part.center.y, part.center.z, 1);
                pixels[w * 3 + i] = part.color;
            }
            Data.SetPixels(pixels); Data.Apply(false, false);
        }

        void Update() => Step(Time.unscaledDeltaTime);

        void OnDestroy()
        {
            foreach (var value in owned) if (value) { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
            owned.Clear();
        }
    }
}
