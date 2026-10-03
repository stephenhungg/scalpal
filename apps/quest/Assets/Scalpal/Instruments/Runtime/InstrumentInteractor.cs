using UnityEngine;

namespace Scalpal.Instruments
{
    [DisallowMultipleComponent]
    public sealed class InstrumentInteractor : MonoBehaviour
    {
        [Min(0.01f)] public float pickupRadius = 0.14f;
        public LayerMask pickupLayers = ~0;
        public InstrumentBehaviour HeldInstrument { get; private set; }
        public bool TrackingValid { get; private set; }
        readonly Collider[] contacts = new Collider[48];
        Transform previousParent;
        Rigidbody heldBody;
        bool previousKinematic, previousGravity;
        bool gripPressed, requireGripRelease;

        public void SetTrackedPose(Vector3 position, Quaternion rotation, bool valid)
        {
            if (!valid && (gripPressed || HeldInstrument != null)) requireGripRelease = true;
            TrackingValid = valid;
            if (valid) transform.SetPositionAndRotation(position, rotation);
            if (HeldInstrument != null) HeldInstrument.SetTrackingValid(valid);
            // Lost tracking immediately drops simulation actions; no stale-pose cutting or scoring.
            if (!valid) Release();
        }

        public void SetGrip(float value)
        {
            if (!TrackingValid) { gripPressed = false; return; }
            if (requireGripRelease)
            {
                if (value < 0.25f) requireGripRelease = false;
                gripPressed = false;
                return;
            }
            bool pressed = value >= (gripPressed ? 0.25f : 0.65f);
            if (pressed && !gripPressed && TrackingValid) TryPickupNearest();
            if (!pressed && gripPressed) Release();
            gripPressed = pressed;
        }

        public void SetActivation(float value)
        {
            if (HeldInstrument != null) HeldInstrument.SetActivation(TrackingValid ? value : 0);
        }

        public bool TryPickupNearest()
        {
            if (!TrackingValid || HeldInstrument != null) return false;
            int count = Physics.OverlapSphereNonAlloc(transform.position, pickupRadius, contacts, pickupLayers, QueryTriggerInteraction.Ignore);
            InstrumentBehaviour best = null;
            float distance = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var candidate = contacts[i].GetComponentInParent<InstrumentBehaviour>();
                if (candidate == null || candidate.Held) continue;
                Vector3 grip = candidate.gripAnchor != null ? candidate.gripAnchor.position : candidate.transform.position;
                float d = (grip - transform.position).sqrMagnitude;
                if (d <= pickupRadius * pickupRadius && d < distance) { distance = d; best = candidate; }
            }
            return best != null && TryPickup(best);
        }

        public bool TryPickup(InstrumentBehaviour instrument)
        {
            if (!TrackingValid || HeldInstrument != null || instrument == null || instrument.Held) return false;
            Vector3 gripPosition = instrument.gripAnchor != null ? instrument.gripAnchor.position : instrument.transform.position;
            if ((gripPosition - transform.position).sqrMagnitude > pickupRadius * pickupRadius) return false;
            HeldInstrument = instrument;
            previousParent = instrument.transform.parent;
            heldBody = instrument.GetComponent<Rigidbody>();
            if (heldBody != null)
            {
                previousKinematic = heldBody.isKinematic;
                previousGravity = heldBody.useGravity;
                if (!heldBody.isKinematic) { heldBody.linearVelocity = Vector3.zero; heldBody.angularVelocity = Vector3.zero; }
                heldBody.isKinematic = true;
                heldBody.useGravity = false;
            }
            instrument.transform.SetParent(transform, true);
            var anchor = instrument.gripAnchor != null ? instrument.gripAnchor : instrument.transform;
            instrument.transform.rotation = transform.rotation * Quaternion.Inverse(Quaternion.Inverse(instrument.transform.rotation) * anchor.rotation);
            instrument.transform.position += transform.position - anchor.position;
            instrument.SetTrackingValid(true);
            instrument.SetHeld(true);
            return true;
        }

        public void Release()
        {
            if (HeldInstrument == null) return;
            HeldInstrument.SetHeld(false);
            HeldInstrument.SetTrackingValid(false);
            HeldInstrument.transform.SetParent(previousParent, true);
            if (heldBody != null) { heldBody.isKinematic = previousKinematic; heldBody.useGravity = previousGravity; }
            HeldInstrument = null;
            heldBody = null;
        }

        void OnDisable() => Release();
    }
}
