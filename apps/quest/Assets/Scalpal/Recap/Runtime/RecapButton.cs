using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.Recap
{
    public sealed class RecapButton : MonoBehaviour, IScalpalPressable
    {
        public RecapController controller;
        public TextMeshPro label;
        public string action;
        float hoverUntil;
        int appearance = -1;
        MaterialPropertyBlock colors;
        public bool Pressable => controller && gameObject.activeInHierarchy;
        public void Highlight() { hoverUntil = Time.unscaledTime + .1f; }
        public void Hover(Vector3 point) => Highlight();
        public void Press()
        {
            if (!controller || !gameObject.activeInHierarchy) return;
            if (action == "explore") controller.Navigate(false);
            else if (action == "retry") controller.Navigate(true);
        }
        void Update()
        {
            int next = Time.unscaledTime < hoverUntil ? 1 : 0;
            if (next == appearance) return;
            appearance = next;
            colors ??= new MaterialPropertyBlock();
            // Every control takes the grey accent glow on focus.
            ScalpalBrand.Tint(GetComponent<Renderer>(), colors, next == 1 ? ScalpalBrand.ButtonHoverTint : ScalpalBrand.ButtonTint, next == 1);
        }
    }
}
