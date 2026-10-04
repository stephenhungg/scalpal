using TMPro;
using UnityEngine;

namespace Scalpal.Brand
{
    /// <summary>
    /// Fits a world-space TextMeshPro into a region measured in world metres. Text starts at its
    /// preferred em size (parent-local metres) and only ever shrinks to fit; it never grows.
    /// Measurement uses TMP layout of the static SDF font, so nothing rebuilds an atlas.
    /// </summary>
    [RequireComponent(typeof(TextMeshPro))]
    [DisallowMultipleComponent]
    public sealed class ScalpalTextFit : MonoBehaviour
    {
        public float maximumWidth = 1.04f, maximumHeight = .12f;
        public float preferredSize = .03f;
        public ScalpalTextRole role = ScalpalTextRole.Body;
        string previousText;
        TextMeshPro text;
        public TextMeshPro Text => text ? text : text = GetComponent<TextMeshPro>();
        void OnEnable() => Fit();
        void LateUpdate() { if (Text && previousText != text.text) Fit(); }

        public void Fit()
        {
            var target = Text;
            if (!target || !target.font) return;
            previousText = target.text;
            var scale = transform.lossyScale;
            float sx = Mathf.Max(.0001f, Mathf.Abs(scale.x)), sy = Mathf.Max(.0001f, Mathf.Abs(scale.y));
            target.enableAutoSizing = false;
            // TextMeshPro (3D) renders one em as fontSize * 0.1 local units.
            target.fontSize = preferredSize * 10;
            target.rectTransform.sizeDelta = new Vector2(maximumWidth / sx, maximumHeight / sy);
            if (string.IsNullOrEmpty(target.text)) { target.ForceMeshUpdate(); return; }
            // TMP layout is not exactly linear in size (kerning/rounding), so converge in a few passes.
            for (int pass = 0; pass < 4; pass++)
            {
                var preferred = target.GetPreferredValues(Mathf.Infinity, Mathf.Infinity);
                float width = preferred.x * sx, height = preferred.y * sy;
                float fit = Mathf.Min(maximumWidth / Mathf.Max(.0001f, width), maximumHeight / Mathf.Max(.0001f, height));
                if (fit >= 1) break;
                target.fontSize *= fit * .995f;
            }
            target.ForceMeshUpdate();
        }

        /// <summary>World size of the laid-out text: advance width by line extents (ascender to descender per line).</summary>
        public Vector2 MeasuredSize()
        {
            var target = Text;
            if (!target || string.IsNullOrEmpty(target.text)) return Vector2.zero;
            var preferred = target.GetPreferredValues(Mathf.Infinity, Mathf.Infinity);
            var scale = transform.lossyScale;
            return new Vector2(Mathf.Abs(preferred.x * scale.x), Mathf.Abs(preferred.y * scale.y));
        }

        /// <summary>World line extent of one line at the current size (independent of which glyphs are shown).</summary>
        public float LineExtent()
        {
            var target = Text;
            if (!target || !target.font) return 0;
            var face = target.font.faceInfo;
            float emLocal = target.fontSize * .1f;
            return (face.ascentLine - face.descentLine) / face.pointSize * face.scale * emLocal * Mathf.Abs(transform.lossyScale.y);
        }

        public int LineCount => string.IsNullOrEmpty(Text ? Text.text : null) ? 0 : text.text.Split('\n').Length;

        /// <summary>Glyph bounds in this transform's local space after layout.</summary>
        public Bounds LocalBounds()
        {
            var target = Text;
            if (!target) return default;
            target.ForceMeshUpdate();
            return target.textBounds;
        }
    }
}
