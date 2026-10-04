using UnityEngine;

namespace Scalpal.Shell
{
    // The single place the dialogue box takes its fonts, materials and colours from.
    // Repoint this asset (Resources/DialogueBoxStyle) at a brand style; DialogueBox reads nothing else.
    [CreateAssetMenu(menuName = "Scalpal/Dialogue Box Style")]
    public sealed class DialogueBoxStyle : ScriptableObject
    {
        public const string ResourcePath = "DialogueBoxStyle";
        public Font bodyFont, nameFont;
        // Card, role-tinted rim and chip use the office EncounterGlass shader (tinted per renderer); text uses its World Text shader.
        public Material glass, bodyText, nameText;
        public Color cardTint = new Color(.040f, .058f, .064f, .91f);
        public Color ink = new Color(.96f, .97f, .95f), mutedInk = new Color(.74f, .79f, .77f), chipInk = new Color(.07f, .07f, .09f);
        public Color you = new Color(.62f, .80f, .97f), patient = new Color(.95f, .66f, .74f), parent = new Color(.97f, .82f, .52f);
        public Color attending = new Color(.78f, .69f, .97f), coach = new Color(.52f, .88f, .78f), warning = new Color(1f, .64f, .48f);
        [Tooltip("Measured ascender-to-descender height of one body line, in metres at the authored distance.")]
        public float bodyLineHeight = .026f;
        public float width = .80f, distance = 1f, pitchDegrees = 22f;

        public static DialogueBoxStyle Load() => Resources.Load<DialogueBoxStyle>(ResourcePath);
    }
}
