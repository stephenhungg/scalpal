using UnityEngine;

namespace Scalpal.EncounterOffice
{
    public sealed class EncounterOfficeButton : MonoBehaviour
    {
        public EncounterOfficePanel panel;
        public string command, argument;
        public bool enabledAction = true;
        public TextMesh label;
        public Color normalColor = new Color(.26f,.36f,.33f,.78f), hoverColor = new Color(.39f,.53f,.47f,.94f);
        Renderer surface;
        float hovered;
        MaterialPropertyBlock colors;
        void Awake() { surface = GetComponent<Renderer>(); colors = new MaterialPropertyBlock(); }
        public void Highlight() { hovered = Time.unscaledTime + .08f; }
        public void Press() { if (enabledAction && panel && gameObject.activeInHierarchy) panel.Act(command, argument); }
        void Update()
        {
            if (!surface) return;
            bool selected = panel && (command == "page" && panel.Page == argument || command == "field" && panel.Field == argument ||
                command == "patient" && panel.session.State?.patientId == argument ||
                command == "option" && panel.Page == "assessment" && panel.Field == "differential" && System.Array.IndexOf(panel.session.Draft.differential ?? new string[0], argument) >= 0);
            colors.SetColor("_Color", Time.unscaledTime < hovered ? hoverColor : selected ? new Color(.35f,.49f,.42f,.94f) : normalColor);
            surface.SetPropertyBlock(colors);
        }
    }
}
