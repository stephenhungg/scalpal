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
        public int index;
        float hoverUntil, seekFraction;
        int appearance = -1;
        MaterialPropertyBlock colors;
        public bool Pressable => controller && gameObject.activeInHierarchy;
        public void Highlight() { hoverUntil = Time.unscaledTime + .1f; }
        public void Highlight(Vector3 point) { Highlight(); seekFraction = Mathf.Clamp01(transform.InverseTransformPoint(point).x + .5f); }
        public void Hover(Vector3 point) => Highlight(point);
        public void Press()
        {
            if (!controller || !gameObject.activeInHierarchy) return;
            switch (action)
            {
                case "continue": controller.Advance(); break;
                case "play": controller.replay.TogglePlay(); break;
                case "back": controller.replay.Seek(controller.replay.Position - 5); break;
                case "forward": controller.replay.Seek(controller.replay.Position + 5); break;
                case "error": controller.replay.SeekError(index); break;
                case "errors_next": controller.panel.NextErrors(); break;
                case "scrub": controller.replay.Seek(controller.replay.WindowStart + seekFraction * (controller.replay.WindowEnd - controller.replay.WindowStart)); break;
                case "demo": controller.ToggleDemo(); break;
                case "explore": controller.Navigate(false); break;
                case "retry": controller.Navigate(true); break;
            }
        }
        void Update()
        {
            int next = Time.unscaledTime < hoverUntil ? 1 : 0;
            if (next == appearance) return;
            appearance = next;
            colors ??= new MaterialPropertyBlock();
            // Continue is the primary action: spark accent at rest; every control takes it on focus.
            ScalpalBrand.Tint(GetComponent<Renderer>(), colors, next == 1 ? ScalpalBrand.ButtonHoverTint : ScalpalBrand.ButtonTint, next == 1 || action == "continue");
        }
    }
}
