using UnityEngine;

namespace Scalpal.Recap
{
    public sealed class RecapButton : MonoBehaviour
    {
        public RecapController controller;
        public TextMesh label;
        public string action;
        public int index;
        float hoverUntil, seekFraction;
        MaterialPropertyBlock colors;
        public void Highlight() { hoverUntil = Time.unscaledTime + .1f; }
        public void Highlight(Vector3 point) { Highlight(); seekFraction = Mathf.Clamp01(transform.InverseTransformPoint(point).x + .5f); }
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
            if (colors == null) colors = new MaterialPropertyBlock();
            colors.SetColor("_Color", Time.unscaledTime < hoverUntil ? new Color(.47f,.59f,.52f,.96f) : new Color(.23f,.34f,.31f,.9f));
            GetComponent<Renderer>().SetPropertyBlock(colors);
        }
    }
}
