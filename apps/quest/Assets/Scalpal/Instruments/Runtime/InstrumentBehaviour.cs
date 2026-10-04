using System;
using UnityEngine;

namespace Scalpal.Instruments
{
    public enum InstrumentAction { None, Cut, Grasp, Seal, Clip, Staple, Suction, Retrieve, PlacePort, Close }

    [Serializable]
    public struct InstrumentActionRecord
    {
        public string instrumentId;
        public string targetId;
        public string action;
        public string outcome;
        public Vector3 worldPoint;
        public double unityMonotonicSeconds;
    }

    [DisallowMultipleComponent]
    public sealed class InstrumentBehaviour : MonoBehaviour
    {
        public string instrumentId;
        public InstrumentAction action;
        public Transform gripAnchor;
        public Transform actionPoint;
        public Transform upperJaw;
        public Transform lowerJaw;
        public Transform triggerPart;
        public bool triggerSlides;
        public Vector3 triggerSlide = new Vector3(0, 0, 0.008f);
        public Transform deployedBag;
        public Transform foldedBag;
        public Transform anvil;
        public Transform obturator;
        [Range(0, 60)] public float jawOpenDegrees = 22;
        [Min(0.001f)] public float contactRadius = 0.012f;
        public LayerMask targetLayers = ~0;
        public bool Held { get; private set; }
        public bool TrackingValid { get; private set; }
        public float Activation { get; private set; }
        public event Action<InstrumentActionRecord> ActionApplied;

        readonly Collider[] contacts = new Collider[32];
        Quaternion upperRest, lowerRest, triggerRest;
        Vector3 triggerPositionRest, bagScaleRest, anvilPositionRest, obturatorPositionRest;
        bool articulationInitialized, armed = true;
        TrainingTarget grasped;
        Vector3 previousPoint;
        bool previousPointValid;
        Transform restParent;
        Vector3 restPosition;
        Quaternion restRotation;
        bool restPoseCaptured;

        public void CaptureRestPose()
        {
            if (restPoseCaptured) return;
            restPoseCaptured = true;
            restParent = transform.parent;
            restPosition = transform.position;
            restRotation = transform.rotation;
        }

        // A case layout that moves a tool to a new resting place makes that its rest pose.
        public void RecaptureRestPose() { restPoseCaptured = false; CaptureRestPose(); }

        public void ReturnToRestPose()
        {
            if (!restPoseCaptured) return;
            transform.SetParent(restParent, true);
            transform.SetPositionAndRotation(restPosition, restRotation);
            var body = GetComponent<Rigidbody>();
            if (!body) return;
            body.position = restPosition; body.rotation = restRotation;
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
        }

        void Awake() => InitializeArticulation();
        void Update() => SimulateStep(Time.deltaTime);
        void OnDisable() => SetHeld(false);

        void InitializeArticulation()
        {
            if (articulationInitialized) return;
            if (upperJaw != null) upperRest = upperJaw.localRotation;
            if (lowerJaw != null) lowerRest = lowerJaw.localRotation;
            if (triggerPart != null) { triggerRest = triggerPart.localRotation; triggerPositionRest = triggerPart.localPosition; }
            if (deployedBag != null) bagScaleRest = deployedBag.localScale;
            if (anvil != null) anvilPositionRest = anvil.localPosition;
            if (obturator != null) obturatorPositionRest = obturator.localPosition;
            articulationInitialized = true;
        }

        public void SetHeld(bool held)
        {
            Held = held;
            if (!held) { SetActivation(0); ReleaseGrasp(); previousPointValid = false; }
        }

        public void SetTrackingValid(bool valid)
        {
            TrackingValid = valid;
            if (!valid) { SetActivation(0); ReleaseGrasp(); previousPointValid = false; }
        }

        public void SetActivation(float value)
        {
            float previousActivation = Activation;
            Activation = Mathf.Clamp01(value);
            if (previousActivation > 0.2f && Activation <= 0.2f)
                foreach (var contact in GetComponentsInChildren<InstrumentTipContact>(true)) contact.ResetContactCycle();
            if (Activation <= 0.2f) { armed = true; ReleaseGrasp(); }
            InitializeArticulation();
            float opening = jawOpenDegrees * (1 - Activation);
            if (upperJaw != null) upperJaw.localRotation = upperRest * Quaternion.AngleAxis(-opening, Vector3.right);
            if (lowerJaw != null) lowerJaw.localRotation = lowerRest * Quaternion.AngleAxis(opening, Vector3.right);
            if (triggerPart != null)
            {
                if (triggerSlides) triggerPart.localPosition = triggerPositionRest + triggerSlide * Activation;
                else triggerPart.localRotation = triggerRest * Quaternion.AngleAxis(-24 * Activation, Vector3.right);
            }
            if (deployedBag != null)
            {
                deployedBag.gameObject.SetActive(Activation > 0.2f);
                deployedBag.localScale = bagScaleRest * Mathf.Lerp(0.1f, 1, Activation);
            }
            if (foldedBag != null) foldedBag.gameObject.SetActive(Activation <= 0.2f);
            if (anvil != null) anvil.position = anvil.parent.TransformPoint(anvilPositionRest) - transform.forward * (0.012f * Activation);
            if (obturator != null) obturator.position = obturator.parent.TransformPoint(obturatorPositionRest) - transform.forward * (0.04f * Activation);
        }

        // Public so the same contact rules can be exercised in the editor validation, without a headset.
        public void SimulateStep(float deltaSeconds)
        {
            Vector3 point = actionPoint != null ? actionPoint.position : transform.position;
            if (!Held || !TrackingValid || Activation < 0.7f || action == InstrumentAction.None)
            {
                previousPoint = point;
                previousPointValid = true;
                return;
            }
            bool continuous = action == InstrumentAction.Cut || action == InstrumentAction.Seal || action == InstrumentAction.Suction;
            if (!continuous && !armed) return;
            int count = Physics.OverlapSphereNonAlloc(point, contactRadius, contacts, targetLayers, QueryTriggerInteraction.Collide);
            TrainingTarget nearest = null;
            float nearestDistance = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var target = contacts[i].GetComponentInParent<TrainingTarget>();
                if (target == null || !target.RegistrationValid || !target.Accepts(action)) continue;
                float distance = (contacts[i].ClosestPoint(point) - point).sqrMagnitude;
                if (distance < nearestDistance) { nearest = target; nearestDistance = distance; }
            }
            if (nearest != null)
            {
                if (action == InstrumentAction.Grasp || action == InstrumentAction.Retrieve)
                {
                    if (grasped == null && nearest.TryGrasp(this, actionPoint != null ? actionPoint : transform))
                    {
                        grasped = nearest;
                        Publish(nearest, action.ToString().ToLowerInvariant(), "applied", point);
                    }
                }
                else
                {
                    string outcome = nearest.Apply(action, previousPointValid ? previousPoint : point, point, Mathf.Clamp(deltaSeconds, 0, 0.1f));
                    if (outcome != null) Publish(nearest, action.ToString().ToLowerInvariant(), outcome, point);
                }
                if (!continuous) armed = false;
            }
            previousPoint = point;
            previousPointValid = true;
        }

        void ReleaseGrasp()
        {
            if (grasped != null) { grasped.ReleaseGrasp(this); grasped = null; }
        }

        void Publish(TrainingTarget target, string name, string outcome, Vector3 point)
        {
            ActionApplied?.Invoke(new InstrumentActionRecord {
                instrumentId = instrumentId, targetId = target.targetId, action = name, outcome = outcome,
                worldPoint = point, unityMonotonicSeconds = Time.realtimeSinceStartupAsDouble
            });
        }
    }
}
