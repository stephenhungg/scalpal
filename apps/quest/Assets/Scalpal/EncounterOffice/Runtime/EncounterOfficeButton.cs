using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    public sealed class EncounterOfficeButton : MonoBehaviour, IScalpalPressable
    {
        public EncounterOfficePanel panel;
        public string command, argument;
        public bool enabledAction = true;
        public TextMeshPro label;
        Renderer surface;
        float hovered;
        int appearance = -1;
        MaterialPropertyBlock colors;
        public bool Pressable => enabledAction && panel && gameObject.activeInHierarchy;
        void Awake() { surface = GetComponent<Renderer>(); colors = new MaterialPropertyBlock(); }
        public void Highlight() { hovered = Time.unscaledTime + .08f; }
        public void Hover(Vector3 point) => Highlight();
        public void Press() { if (enabledAction && panel && gameObject.activeInHierarchy) panel.Act(command, argument); }
        void Update()
        {
            if (!surface) return;
            bool selected = panel && (command == "page" && panel.Page == argument || command == "field" && panel.Field == argument ||
                command == "patient" && panel.session.State?.patientId == argument ||
                command == "option" && panel.Page == "assessment" && panel.Field == "differential" && System.Array.IndexOf(panel.session.Draft.differential ?? new string[0], argument) >= 0);
            bool hover = Time.unscaledTime < hovered;
            int next = !enabledAction ? 0 : hover ? 3 : selected ? 2 : 1;
            if (next == appearance) return;
            appearance = next;
            // Hover takes the spark accent; the selected tab keeps a lighter fill with the accent hairline.
            ScalpalBrand.Tint(surface, colors, next == 0 ? ScalpalBrand.ButtonDisabledTint : next == 3 ? ScalpalBrand.ButtonHoverTint : next == 2 ? ScalpalBrand.ButtonSelectedTint : ScalpalBrand.ButtonTint, next >= 2);
            if (label) label.color = next == 0 ? ScalpalBrand.InkDisabled : ScalpalBrand.Ink;
        }
    }
}
