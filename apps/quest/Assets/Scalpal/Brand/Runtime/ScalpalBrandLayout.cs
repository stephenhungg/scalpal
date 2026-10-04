using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Scalpal.Brand
{
    /// <summary>
    /// Readability arithmetic shared by builders and validations. Sizes are line extents
    /// (ascender to descender) per metre of viewing distance: labels 32 mm/m, body 24 mm/m.
    /// </summary>
    public static class ScalpalBrandLayout
    {
        // Headroom over the floor so fitting rounding never lands a string just under it.
        public const float Headroom = 1.06f;

        /// <summary>Line extent of one em for this face (Geist Mono ~1.30, Instrument Serif ~1.30).</summary>
        public static float ExtentPerEm(TMP_FontAsset font)
        {
            var face = font.faceInfo;
            return (face.ascentLine - face.descentLine) / face.pointSize * face.scale;
        }

        public static float MinimumMmAt1m(ScalpalTextRole role) => ScalpalBrand.IsLabel(role) ? ScalpalBrand.LabelMinimumMmAt1m : ScalpalBrand.BodyMinimumMmAt1m;

        /// <summary>World em (metres) meeting the role's floor at <paramref name="distance"/> metres.</summary>
        public static float Em(ScalpalBrand brand, ScalpalTextRole role, float distance) =>
            MinimumMmAt1m(role) / 1000f * distance * Headroom / ExtentPerEm(brand.Font(role));

        /// <summary>Sets every fitted text under <paramref name="root"/> to its role's floor for its actual distance from <paramref name="viewer"/>, then refits.</summary>
        public static void SizeForViewer(Transform root, Vector3 viewer)
        {
            var brand = ScalpalBrand.Active;
            foreach (var fit in root.GetComponentsInChildren<ScalpalTextFit>(true))
            {
                float distance = Vector3.Distance(fit.transform.position, viewer);
                float scale = Mathf.Max(.0001f, Mathf.Abs(fit.transform.lossyScale.y));
                // preferredSize is an em in the text's own local units; convert from world metres.
                fit.preferredSize = Mathf.Max(fit.preferredSize, Em(brand, fit.role, distance) / scale);
                fit.Fit();
            }
        }

        public struct Measurement { public ScalpalTextFit fit; public float mmAt1m, minimum; public bool Passes => mmAt1m >= minimum - .05f; }

        /// <summary>Measured line extent per metre for every visible, non-empty fitted text under <paramref name="root"/>.</summary>
        public static List<Measurement> Measure(Transform root, Vector3 viewer, bool includeInactive = false)
        {
            var result = new List<Measurement>();
            foreach (var fit in root.GetComponentsInChildren<ScalpalTextFit>(includeInactive))
            {
                if (!fit.Text || string.IsNullOrWhiteSpace(fit.Text.text)) continue;
                fit.Fit();
                float distance = Mathf.Max(.05f, Vector3.Distance(fit.transform.position, viewer));
                result.Add(new Measurement { fit = fit, mmAt1m = fit.LineExtent() / distance * 1000, minimum = MinimumMmAt1m(fit.role) });
            }
            return result;
        }
    }
}
