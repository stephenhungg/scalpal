using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.Shell
{
    // The single place the dialogue box takes its fonts, materials and colours from.
    // Points at the Scalpal brand (Resources/ScalpalBrand): static SDF Geist Mono / Instrument Serif,
    // dark glass, white ink and the spark accent. DialogueBox reads nothing else.
    [CreateAssetMenu(menuName = "Scalpal/Dialogue Box Style")]
    public sealed class DialogueBoxStyle : ScriptableObject
    {
        public const string ResourcePath = "DialogueBoxStyle";
        public TMP_FontAsset bodyFont, nameFont;
        // Card and role rim use the brand glass shader (tinted per renderer); the role chip uses the brand unlit accent.
        // Choice buttons, when shown, use the brand button material with ScalpalBrand.Tint (accent on focus).
        public Material glass, bodyText, nameText, chip, button;
        public Color cardTint = ScalpalBrand.GlassTint;
        public Color ink = ScalpalBrand.Ink, mutedInk = ScalpalBrand.Ink70, chipInk = new Color(.04f, .04f, .045f);
        public Color you = new Color(1, 1, 1, .85f), patient = ScalpalBrand.AccentGold, parent = new Color(1f, .78f, .45f);
        public Color attending = ScalpalBrand.AccentOrange, coach = new Color(1f, .55f, .26f), warning = new Color(1f, .42f, .28f);
        [Tooltip("Measured ascender-to-descender height of one body line, in metres at the authored distance.")]
        public float bodyLineHeight = .026f;
        public float width = .80f, distance = 1f, pitchDegrees = 22f;

        public static DialogueBoxStyle Load() => Resources.Load<DialogueBoxStyle>(ResourcePath);
    }
}
