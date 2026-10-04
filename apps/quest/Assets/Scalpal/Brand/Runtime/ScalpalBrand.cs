using System;
using TMPro;
using UnityEngine;

namespace Scalpal.Brand
{
    // Display: Instrument Serif. Label: Geist Mono Medium, white. Body: Geist Mono Regular, 70% white.
    // Caption: Geist Mono Regular, 50% white (never a primary reading surface).
    public enum ScalpalTextRole { Wordmark, Title, Label, Body, Caption }

    /// <summary>
    /// The single in-headset brand source, matching the landing site (apps/site: layout.tsx fonts,
    /// globals.css ink, BrandMark/DitherLogo mark, the AsciiAdam spark accent). Shell, office,
    /// handoff, recap and any new surface (for example a dialogue box) take fonts, materials and
    /// colours from <see cref="Active"/> instead of loading their own.
    /// </summary>
    public sealed class ScalpalBrand : ScriptableObject
    {
        public const string ResourcePath = "ScalpalBrand";
        [Header("Static SDF font assets (no runtime atlas population)")]
        public TMP_FontAsset display;
        public TMP_FontAsset body;
        public TMP_FontAsset label;
        [Header("Depth-tested text materials (render after glass)")]
        public Material displayText;
        public Material bodyText;
        public Material labelText;
        [Header("Overlay text materials (ZTest Always, above the stereo fade)")]
        public Material displayOverlay;
        public Material bodyOverlay;
        [Header("Surfaces")]
        public Material glass;
        public Material button;
        public Material accent;
        public Material ray;
        public Material mark;
        public Texture2D markTexture;

        // Site palette: white ink on near-black; body copy at 70% ink; one warm accent from the spark.
        public static readonly Color Ink = Color.white;
        public static readonly Color Ink70 = new Color(1, 1, 1, .70f);
        public static readonly Color Ink50 = new Color(1, 1, 1, .50f);
        public static readonly Color InkDisabled = new Color(1, 1, 1, .36f);
        public static readonly Color AccentOrange = new Color32(242, 77, 20, 255);
        public static readonly Color AccentGold = new Color32(255, 219, 56, 255);
        // Flat uses (icons, chips) take the spark's midpoint; rims and rays use the full gradient.
        public static readonly Color Accent = Color.Lerp(AccentOrange, AccentGold, .45f);
        public static readonly Color GlassTint = new Color(.016f, .016f, .019f, .95f);
        public static readonly Color Hairline = new Color(1, 1, 1, .16f);
        public static readonly Color ButtonTint = new Color(.055f, .055f, .062f, .96f);
        public static readonly Color ButtonHoverTint = new Color(.115f, .072f, .052f, .95f);
        public static readonly Color ButtonSelectedTint = new Color(.090f, .085f, .085f, .94f);
        public static readonly Color ButtonDisabledTint = new Color(.040f, .040f, .044f, .70f);
        // Secondary (ghost) actions such as "Skip to surgery": outline only, nearly clear fill.
        public static readonly Color GhostTint = new Color(.02f, .02f, .024f, .28f);
        public static readonly Color GhostRim = new Color(1, 1, 1, .45f);

        // Readability floor: line extent (ascender to descender) per metre of viewing distance.
        public const float LabelMinimumMmAt1m = 32, BodyMinimumMmAt1m = 24;
        // Instrument Serif display tracking from globals.css: -3px at 110px.
        public const float DisplayTracking = -3f / 110f * 100f;

        static ScalpalBrand active;
        public static ScalpalBrand Active
        {
            get
            {
                if (active) return active;
                active = Resources.Load<ScalpalBrand>(ResourcePath);
                if (!active) throw new InvalidOperationException("Resources/" + ResourcePath + " is missing. Run Scalpal/Brand/Prepare Brand Assets.");
                return active;
            }
        }

        public TMP_FontAsset Font(ScalpalTextRole role) => role == ScalpalTextRole.Wordmark || role == ScalpalTextRole.Title ? display : role == ScalpalTextRole.Label ? label : body;
        public Material TextMaterial(ScalpalTextRole role, bool overlay = false)
        {
            if (role == ScalpalTextRole.Wordmark || role == ScalpalTextRole.Title) return overlay ? displayOverlay : displayText;
            if (overlay) return bodyOverlay;
            return role == ScalpalTextRole.Label ? labelText : bodyText;
        }
        public static Color InkFor(ScalpalTextRole role) => role == ScalpalTextRole.Body ? Ink70 : role == ScalpalTextRole.Caption ? Ink50 : Ink;
        public static bool IsLabel(ScalpalTextRole role) => role != ScalpalTextRole.Body && role != ScalpalTextRole.Caption;

        /// <summary>Applies the role's font, material, ink and tracking to any TextMeshPro.</summary>
        public void Style(TMP_Text text, ScalpalTextRole role, bool overlay = false)
        {
            text.font = Font(role);
            text.fontSharedMaterial = TextMaterial(role, overlay);
            text.color = InkFor(role);
            text.richText = false;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.characterSpacing = role == ScalpalTextRole.Wordmark || role == ScalpalTextRole.Title ? DisplayTracking : 0;
            text.lineSpacing = role == ScalpalTextRole.Body || role == ScalpalTextRole.Caption ? 8 : 0;
            text.fontStyle = FontStyles.Normal;
            text.extraPadding = false;
            text.isOrthographic = false;
        }

        /// <summary>
        /// World-space text: <paramref name="em"/> is the em height in the parent's local metres;
        /// <paramref name="maximumWidth"/>/<paramref name="maximumHeight"/> are world metres the text must fit.
        /// The anchor is the text box corner/centre at <paramref name="position"/>.
        /// </summary>
        public TextMeshPro Text(Transform parent, string name, string value, ScalpalTextRole role, Vector3 position, float em, float maximumWidth, float maximumHeight, TextAnchor anchor = TextAnchor.UpperLeft, bool overlay = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshPro>();
            Style(text, role, overlay);
            Anchor(text, anchor);
            text.rectTransform.localPosition = position;
            text.text = value ?? "";
            var renderer = text.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            var fit = go.AddComponent<ScalpalTextFit>();
            fit.preferredSize = em; fit.maximumWidth = maximumWidth; fit.maximumHeight = maximumHeight; fit.role = role;
            fit.Fit();
            return text;
        }

        public static void Anchor(TMP_Text text, TextAnchor anchor)
        {
            var pivot = new Vector2(anchor == TextAnchor.UpperLeft || anchor == TextAnchor.MiddleLeft || anchor == TextAnchor.LowerLeft ? 0 : anchor == TextAnchor.UpperRight || anchor == TextAnchor.MiddleRight || anchor == TextAnchor.LowerRight ? 1 : .5f,
                anchor == TextAnchor.UpperLeft || anchor == TextAnchor.UpperCenter || anchor == TextAnchor.UpperRight ? 1 : anchor == TextAnchor.LowerLeft || anchor == TextAnchor.LowerCenter || anchor == TextAnchor.LowerRight ? 0 : .5f);
            text.rectTransform.pivot = pivot;
            text.alignment = anchor switch
            {
                TextAnchor.UpperLeft => TextAlignmentOptions.TopLeft,
                TextAnchor.UpperCenter => TextAlignmentOptions.Top,
                TextAnchor.UpperRight => TextAlignmentOptions.TopRight,
                TextAnchor.MiddleLeft => TextAlignmentOptions.Left,
                TextAnchor.MiddleCenter => TextAlignmentOptions.Center,
                TextAnchor.MiddleRight => TextAlignmentOptions.Right,
                TextAnchor.LowerLeft => TextAlignmentOptions.BottomLeft,
                TextAnchor.LowerCenter => TextAlignmentOptions.Bottom,
                _ => TextAlignmentOptions.BottomRight,
            };
        }

        /// <summary>Tints a brand glass/button surface: fill, hairline rim and an accent rim when focused.</summary>
        public static void Tint(Renderer surface, MaterialPropertyBlock block, Color fill, bool accentRim, float rimStrength = 1)
        {
            if (!surface) return;
            block ??= new MaterialPropertyBlock();
            surface.GetPropertyBlock(block);
            block.SetColor("_Color", fill);
            block.SetColor("_RimColor", accentRim ? AccentOrange : Hairline);
            block.SetColor("_RimColorB", accentRim ? AccentGold : Hairline);
            block.SetFloat("_RimStrength", rimStrength);
            block.SetFloat("_Glow", accentRim ? 1 : 0);
            surface.SetPropertyBlock(block);
        }

        /// <summary>The dither mark from the site (DitherLogo) as a world quad, white on the glass.</summary>
        public Renderer Mark(Transform parent, Vector3 position, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "ScalpalMark";
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = new Vector3(size, size, 1);
            var renderer = go.GetComponent<Renderer>();
            renderer.sharedMaterial = mark;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; renderer.receiveShadows = false;
            return renderer;
        }
    }
}
