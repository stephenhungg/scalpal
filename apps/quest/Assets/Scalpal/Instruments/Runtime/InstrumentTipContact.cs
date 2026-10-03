using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Instruments
{
    // Adapter for Matthew's touch events. Registration owner must explicitly provide the validity function.
    public sealed class InstrumentTipContact : MonoBehaviour
    {
        public Func<bool> RegistrationIsValid;
        public event Action<string, string> TouchApplied;
        readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        InstrumentBehaviour instrument;

        void Awake() => instrument = GetComponentInParent<InstrumentBehaviour>();
        void OnTriggerStay(Collider other) => TryReportContact(other);
        // Exiting/reentering does not constitute a new activation, including multi-collider anatomy.
        void OnTriggerExit(Collider other) { }
        public void ResetContactCycle() => reported.Clear();
        void OnDisable() => reported.Clear();

        public bool TryReportContact(Collider other)
        {
            if (instrument == null) instrument = GetComponentInParent<InstrumentBehaviour>();
            bool valid = instrument != null && instrument.Held && instrument.TrackingValid && instrument.Activation >= 0.7f && RegistrationIsValid != null && RegistrationIsValid();
            if (!valid)
            {
                if (instrument == null || !instrument.Held || !instrument.TrackingValid || instrument.Activation <= 0.2f) reported.Clear();
                return false;
            }
            if (other == null) return false;
            Transform structure = other.transform;
            while (structure != null && !structure.name.StartsWith("anat_", StringComparison.Ordinal)) structure = structure.parent;
            if (structure == null) return false;
            string structureId = structure.name.Substring(5);
            if (!reported.Add(structureId)) return false;
            TouchApplied?.Invoke(structureId, instrument.instrumentId);
            return true;
        }
    }
}
