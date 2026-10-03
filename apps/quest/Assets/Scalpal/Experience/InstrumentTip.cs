// Put on the trigger collider at an instrument prefab's working end (child "Tip", Instrument layer).
// Touching an anatomy collider scores through the CaseDirector; touching a port site places that port.
using Scalpal.Anatomy;
using UnityEngine;

namespace Scalpal.Experience
{
    [DisallowMultipleComponent]
    public sealed class InstrumentTip : MonoBehaviour
    {
        [Tooltip("Catalog id, e.g. hook_cautery. See InstrumentIds in Generated/ScalpalIds.cs.")]
        public string instrumentId = "";
        public CaseDirector director;

        void OnTriggerEnter(Collider other)
        {
            if (director == null || other == null) return;
            var port = other.GetComponent<PortTarget>();
            if (port != null)
            {
                director.PlacePort(port.portId);
                return;
            }
            var part = other.GetComponentInParent<AnatomyPart>();
            if (part == null || string.IsNullOrEmpty(part.stableId)) return;
            director.Focus(part.stableId);
            director.Touch(part.stableId, instrumentId);
        }

        void OnTriggerExit(Collider other)
        {
            if (director != null && other != null && other.GetComponentInParent<AnatomyPart>() != null) director.Focus("");
        }
    }
}
