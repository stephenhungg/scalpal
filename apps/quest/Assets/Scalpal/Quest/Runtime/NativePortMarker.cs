using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // Authored virtual port target. This represents a simulated placement only.
    [DisallowMultipleComponent]
    public sealed class NativePortMarker : MonoBehaviour
    {
        public string portId = "";
        public NativeProcedureInput input;

        void OnTriggerStay(Collider other)
        {
            if (!input || !isActiveAndEnabled || !other) return;
            var tool = other.GetComponentInParent<InstrumentBehaviour>();
            if (tool) input.TryPlacePort(portId, tool, out _);
        }

        public bool Owns(Collider collider) => collider && collider.enabled && collider.gameObject.activeInHierarchy &&
            collider.GetComponentInParent<NativePortMarker>(true) == this;
    }
}
