using UnityEngine;

namespace Scalpal.Instruments
{
    [DisallowMultipleComponent]
    public sealed class InstrumentInteractor : MonoBehaviour
    {
        [Min(0.01f)] public float pickupRadius = 0.14f;
        public LayerMask pickupLayers = ~0;
        [Min(0)] public float trackingGraceSeconds = 0.2f;
        public InstrumentBehaviour HeldInstrument { get; private set; }
        public bool TrackingValid { get; private set; }
        readonly Collider[] contacts = new Collider[48];
        Transform previousParent;
        Rigidbody heldBody;
        bool previousKinematic, previousGravity;
        RigidbodyInterpolation previousInterpolation;
        bool gripPressed, requireGripRelease, requireTriggerRelease, trackingSuspended;
        double trackingLostAt;
        Vector3 heldLocalPosition;
        Vector3 heldLocalScale;
        Quaternion heldLocalRotation;

        void Update() => AdvanceTrackingLoss(Time.unscaledTimeAsDouble);

        public void SetTrackedPose(Vector3 position, Quaternion rotation, bool valid)
            => SetTrackedPose(position, rotation, valid, Time.unscaledTimeAsDouble);

        // The explicit clock also lets deterministic tests exercise the real loss/recovery path.
        public void SetTrackedPose(Vector3 position, Quaternion rotation, bool valid, double monotonicSeconds)
        {
            if (!valid)
            {
                if (!trackingSuspended)
                {
                    trackingLostAt = monotonicSeconds; trackingSuspended = true;
                    // World freeze also survives a tracking-origin/recenter change during the pause.
                    if (HeldInstrument) HeldInstrument.transform.SetParent(null, true);
                }
                if (!HeldInstrument) requireGripRelease = true;
                requireTriggerRelease = true;
                TrackingValid = false;
                if (HeldInstrument) HeldInstrument.SetTrackingValid(false);
                AdvanceTrackingLoss(monotonicSeconds);
                return;
            }
            AdvanceTrackingLoss(monotonicSeconds);
            // Recover even after a fast reach/recenter within grace. Trigger release
            // is required before any effect can run at the recovered pose.
            trackingSuspended = false;
            TrackingValid = valid;
            transform.SetPositionAndRotation(position, rotation);
            if (HeldInstrument != null)
            {
                HeldInstrument.transform.SetParent(transform, false);
                HeldInstrument.transform.SetLocalPositionAndRotation(heldLocalPosition, heldLocalRotation);
                HeldInstrument.transform.localScale = heldLocalScale;
                HeldInstrument.SetTrackingValid(valid);
            }
        }

        public void AdvanceTrackingLoss(double monotonicSeconds)
        {
            if (!trackingSuspended || monotonicSeconds - trackingLostAt < trackingGraceSeconds) return;
            requireGripRelease = true;
            ReturnHeldToRest();
        }

        public void ReturnHeldToRest()
        {
            var tool = HeldInstrument;
            requireGripRelease = true;
            requireTriggerRelease = true;
            gripPressed = false;
            Release();
            if (tool) tool.ReturnToRestPose();
        }

        public void SetGrip(float value)
        {
            // Invalid XR input reports zero; that is not evidence of a physical grip release.
            if (!TrackingValid) return;
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
            if (!TrackingValid) return;
            if (requireTriggerRelease)
            {
                if (value <= 0.2f) requireTriggerRelease = false;
                value = 0;
            }
            if (HeldInstrument != null) HeldInstrument.SetActivation(value);
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
            Attach(instrument);
            return true;
        }

        // A hand-over (the coach passing a tool on request): whatever this hand holds goes back to its rest pose, and the
        // new tool attaches exactly as a grip pickup would, wherever it lies. Grip state is kept, so a squeezed grip holds it.
        public bool TryHandOver(InstrumentBehaviour instrument)
        {
            if (!TrackingValid || instrument == null || instrument.Held) return false;
            var previous = HeldInstrument;
            requireTriggerRelease = true; // a tool that arrives never fires on a trigger already squeezed
            Release();
            if (previous) previous.ReturnToRestPose();
            Attach(instrument);
            return true;
        }

        void Attach(InstrumentBehaviour instrument)
        {
            HeldInstrument = instrument;
            instrument.CaptureRestPose();
            previousParent = instrument.transform.parent;
            heldBody = instrument.GetComponent<Rigidbody>();
            if (heldBody != null)
            {
                previousKinematic = heldBody.isKinematic;
                previousGravity = heldBody.useGravity;
                previousInterpolation = heldBody.interpolation;
                if (!heldBody.isKinematic) { heldBody.linearVelocity = Vector3.zero; heldBody.angularVelocity = Vector3.zero; }
                // A tracked transform owns the held pose. Physics interpolation
                // otherwise replays older fixed-step poses and trails the hand.
                heldBody.interpolation = RigidbodyInterpolation.None;
                // Kinematic bodies support only speculative continuous detection.
                heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                heldBody.isKinematic = true;
                heldBody.useGravity = false;
            }
            instrument.transform.SetParent(transform, true);
            var anchor = instrument.gripAnchor != null ? instrument.gripAnchor : instrument.transform;
            instrument.transform.rotation = transform.rotation * Quaternion.Inverse(Quaternion.Inverse(instrument.transform.rotation) * anchor.rotation);
            instrument.transform.position += transform.position - anchor.position;
            heldLocalPosition = instrument.transform.localPosition;
            heldLocalRotation = instrument.transform.localRotation;
            heldLocalScale = instrument.transform.localScale;
            instrument.SetTrackingValid(true);
            instrument.SetHeld(true);
        }

        public void Release()
        {
            if (HeldInstrument == null) return;
            HeldInstrument.SetHeld(false);
            HeldInstrument.SetTrackingValid(false);
            HeldInstrument.transform.SetParent(previousParent, true);
            if (heldBody != null)
            {
                heldBody.position = HeldInstrument.transform.position;
                heldBody.rotation = HeldInstrument.transform.rotation;
                heldBody.isKinematic = previousKinematic;
                heldBody.useGravity = previousGravity;
                heldBody.interpolation = previousInterpolation;
                // A dropped or tossed tool is small and fast: sweep it so it cannot tunnel through the table or patient.
                if (!heldBody.isKinematic) heldBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }
            HeldInstrument = null;
            heldBody = null;
        }

        void OnDisable() => ReturnHeldToRest();
    }
}
