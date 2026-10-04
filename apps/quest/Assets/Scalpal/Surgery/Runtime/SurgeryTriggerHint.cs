using Scalpal.Instruments;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Surgery
{
    // First-use affordance: tool effects need the trigger held, so a tool resting on tissue without it
    // shows "Hold trigger to <verb>" at its tip and taps the holding controller. Presentation only.
    [DisallowMultipleComponent]
    public sealed class SurgeryTriggerHint : MonoBehaviour
    {
        public float seconds = 2.5f;
        TextMesh label;
        Transform tip, viewer;
        float until;
        public string Text => label && label.gameObject.activeSelf ? label.text : "";

        public static string Phrase(string verb)
        {
            switch (verb)
            {
                case "mark": return "Hold trigger to draw";
                case "cut": return "Hold trigger to cut";
                case "grasp": return "Hold trigger to grasp";
                case "retract": return "Hold trigger to retract";
                case "clamp": return "Hold trigger to clamp";
                case "tie": return "Hold trigger to tie";
                case "seal": return "Hold trigger to seal";
                case "suction": return "Hold trigger for suction";
                default: return "Hold trigger to use";
            }
        }

        public void Show(InstrumentBehaviour tool, string verb, XRNode? hand, Transform head)
        {
            if (!tool || !tool.actionPoint) return;
            if (!label)
            {
                var go = new GameObject("TriggerHint"); go.transform.SetParent(transform, false);
                label = go.AddComponent<TextMesh>();
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                label.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
                label.fontSize = 48; label.characterSize = .006f;
                label.anchor = TextAnchor.LowerCenter; label.alignment = TextAlignment.Center;
                label.color = new Color(1, .92f, .55f);
            }
            label.text = Phrase(verb);
            label.gameObject.SetActive(true);
            tip = tool.actionPoint; viewer = head; until = Time.unscaledTime + seconds;
            Place();
            if (!hand.HasValue) return;
            var device = InputDevices.GetDeviceAtXRNode(hand.Value);
            if (device.isValid && device.TryGetHapticCapabilities(out var capability) && capability.supportsImpulse)
                device.SendHapticImpulse(0, .2f, .05f);
        }

        void LateUpdate()
        {
            if (!label || !label.gameObject.activeSelf) return;
            if (!tip || Time.unscaledTime >= until) { label.gameObject.SetActive(false); return; }
            Place();
        }

        void Place()
        {
            label.transform.position = tip.position + Vector3.up * .03f;
            if (viewer) label.transform.rotation = Quaternion.LookRotation(label.transform.position - viewer.position, Vector3.up);
        }

        public void Hide() { if (label) label.gameObject.SetActive(false); }
    }
}
