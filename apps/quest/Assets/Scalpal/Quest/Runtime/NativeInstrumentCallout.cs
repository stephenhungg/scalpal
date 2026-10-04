using Scalpal.Brand;
using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // The coach pointing at the tool the learner needs: one instrument on the stand gets a pulsing orange outline
    // (never the green identification box) until it is picked up or the callout times out. Presentation only.
    [DisallowMultipleComponent]
    public sealed class NativeInstrumentCallout : MonoBehaviour
    {
        public static readonly Color Orange = new Color(1f, .55f, .1f);
        public float seconds = 8;
        public InstrumentBehaviour Target { get; private set; }
        LineRenderer box;
        double startedAt;
        readonly Vector3[] corners = new Vector3[16];
        static readonly int[] EdgeWalk = { 0,1,2,3,0,4,5,1,5,6,2,6,7,3,7,4 };

        void Update() => Advance(Time.unscaledTimeAsDouble);

        // A new callout replaces the old one.
        public void Show(InstrumentBehaviour tool) => Show(tool, Time.unscaledTimeAsDouble);
        public void Show(InstrumentBehaviour tool, double now)
        {
            Target = tool; startedAt = now;
            if (!box)
            {
                box = new GameObject("InstrumentCallout").AddComponent<LineRenderer>(); box.transform.SetParent(transform, false);
                box.useWorldSpace = true; box.positionCount = EdgeWalk.Length; box.loop = false;
                box.sharedMaterial = ScalpalBrand.Active ? ScalpalBrand.Active.ray : null;
                box.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; box.receiveShadows = false;
            }
            Advance(now);
        }

        public void Hide() { Target = null; if (box) box.enabled = false; }

        // The explicit clock lets Editor validation run the real timeout and pickup path.
        public void Advance(double now)
        {
            if (!Target) { Hide(); return; }
            if (Target.Held || !Target.gameObject.activeInHierarchy || now - startedAt >= seconds) { Hide(); return; }
            bool any = false; var bounds = default(Bounds);
            foreach (var renderer in Target.GetComponentsInChildren<Renderer>())
            {
                if (renderer is LineRenderer || !renderer.enabled) continue;
                if (!any) { bounds = renderer.bounds; any = true; } else bounds.Encapsulate(renderer.bounds);
            }
            if (!any) { bounds = new Bounds(Target.transform.position, Vector3.one * .03f); }
            bounds.Expand(.012f);
            var min = bounds.min; var max = bounds.max;
            for (int i = 0; i < EdgeWalk.Length; i++)
            {
                int corner = EdgeWalk[i];
                corners[i] = new Vector3((corner == 1 || corner == 2 || corner == 5 || corner == 6) ? max.x : min.x,
                    (corner == 2 || corner == 3 || corner == 6 || corner == 7) ? max.y : min.y, corner >= 4 ? max.z : min.z);
            }
            box.SetPositions(corners);
            float pulse = .5f + .5f * Mathf.Sin((float)(now - startedAt) * Mathf.PI * 2 * 1.5f);
            box.startWidth = box.endWidth = Mathf.Lerp(.002f, .005f, pulse);
            box.startColor = box.endColor = Color.Lerp(Orange * .7f, Orange, pulse);
            box.enabled = true;
        }

        public bool Visible => box && box.enabled && Target;
    }
}
