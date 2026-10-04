using System;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Immutable interaction metadata, not case goals, damage state or a second physiology ledger.
    // Fiber direction is in the owning tissue's local material frame. Authored properties need provenance.
    public readonly struct TissueInteractionProperties
    {
        public readonly string id, layer, provenance;
        public readonly int order;
        public readonly bool cuttable, splittable, tentable, hasFibers;
        // Missing semantic binding is unknown, not evidence that a structure is nonperfused or safe.
        public readonly bool? perfused, hollow, critical;
        public bool HasConsequenceProperties => perfused.HasValue && hollow.HasValue && critical.HasValue;
        public readonly Vector3 fiberDirection;
        public bool IsValid => !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(layer)
            && !string.IsNullOrWhiteSpace(provenance) && order >= -1;

        public TissueInteractionProperties(string id, string layer, int order, bool cuttable, bool splittable,
            bool tentable, bool? perfused, bool? hollow, bool? critical, Vector3 fiberDirection, string provenance)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(layer) || order < -1 ||
                string.IsNullOrWhiteSpace(provenance) || !TissueCage.Finite(fiberDirection))
                throw new ArgumentException("Tissue interaction properties require identity, order, finite fibers and provenance");
            this.id = id; this.layer = layer; this.order = order; this.provenance = provenance;
            this.cuttable = cuttable; this.splittable = splittable; this.tentable = tentable;
            this.perfused = perfused; this.hollow = hollow; this.critical = critical;
            // Normalize without overflowing for finite but large authored vectors.
            float largest = Mathf.Max(Mathf.Abs(fiberDirection.x), Mathf.Max(Mathf.Abs(fiberDirection.y), Mathf.Abs(fiberDirection.z)));
            hasFibers = largest > 0;
            this.fiberDirection = hasFibers ? (fiberDirection / largest).normalized : Vector3.zero;
        }

        // Generic 3D unsigned axis angle. Wall callers project into their own incision plane first.
        public bool TryFiberAngle(Vector3 localDirection, out float degrees)
        {
            degrees = float.NaN;
            if (!IsValid || !hasFibers || !TissueCage.Finite(localDirection)) return false;
            float largest = Mathf.Max(Mathf.Abs(localDirection.x), Mathf.Max(Mathf.Abs(localDirection.y), Mathf.Abs(localDirection.z)));
            if (largest <= 0) return false;
            var direction = (localDirection / largest).normalized;
            degrees = Mathf.Acos(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(direction, fiberDirection)))) * Mathf.Rad2Deg;
            return true;
        }
    }
}
