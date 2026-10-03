using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Instruments
{
    [Flags]
    public enum TrainingActions
    {
        None = 0, Cut = 1 << 1, Grasp = 1 << 2, Seal = 1 << 3, Clip = 1 << 4,
        Staple = 1 << 5, Suction = 1 << 6, Retrieve = 1 << 7, PlacePort = 1 << 8, Close = 1 << 9
    }

    // An explicitly authored virtual patch. It never changes passthrough imagery or a participant.
    public sealed class TrainingTarget : MonoBehaviour
    {
        public string targetId = "training_patch";
        public TrainingActions allowedActions = TrainingActions.Cut | TrainingActions.Grasp | TrainingActions.Seal | TrainingActions.Clip | TrainingActions.Staple;
        [SerializeField] bool registrationValid;
        public bool RegistrationValid => registrationValid;
        [Min(0.01f)] public float seamWidth = 0.1f;
        [Min(0.001f)] public float seamTolerance = 0.012f;
        public Transform leftHalf;
        public Transform rightHalf;
        public Transform fluidVisual;
        public bool IsCut { get; private set; }
        public bool IsSealed { get; private set; }
        public int ClipCount { get; private set; }
        public int StapleCount { get; private set; }
        public float FluidRemaining { get; private set; } = 1;
        public bool PortPlaced { get; private set; }
        public bool IsClosed { get; private set; }

        Vector3 leftRest, rightRest, fluidRest;
        bool initialized;
        float sealProgress;
        int seamCoverage;
        InstrumentBehaviour owner;
        Transform originalParent;
        Rigidbody body;
        bool originalKinematic, originalGravity;
        readonly List<GameObject> markers = new List<GameObject>();

        void Awake() => Initialize();

        void Initialize()
        {
            if (initialized) return;
            leftRest = leftHalf != null ? leftHalf.localPosition : Vector3.zero;
            rightRest = rightHalf != null ? rightHalf.localPosition : Vector3.zero;
            fluidRest = fluidVisual != null ? fluidVisual.localScale : Vector3.one;
            initialized = true;
        }

        public void SetRegistrationValid(bool valid)
        {
            registrationValid = valid;
            if (!valid && owner != null) ReleaseGrasp(owner);
        }

        public bool Accepts(InstrumentAction action) => action != InstrumentAction.None && (allowedActions & (TrainingActions)(1 << (int)action)) != 0;

        public bool TryGrasp(InstrumentBehaviour instrument, Transform anchor)
        {
            if (!registrationValid || owner != null || anchor == null || !Accepts(instrument.action)) return false;
            Initialize();
            owner = instrument;
            originalParent = transform.parent;
            body = GetComponent<Rigidbody>();
            if (body != null)
            {
                originalKinematic = body.isKinematic;
                originalGravity = body.useGravity;
                body.isKinematic = true;
                body.useGravity = false;
            }
            transform.SetParent(anchor, true);
            return true;
        }

        public void ReleaseGrasp(InstrumentBehaviour instrument)
        {
            if (owner != instrument) return;
            transform.SetParent(originalParent, true);
            if (body != null) { body.isKinematic = originalKinematic; body.useGravity = originalGravity; }
            owner = null;
        }

        public string Apply(InstrumentAction action, Vector3 previousWorldPoint, Vector3 worldPoint, float deltaSeconds)
        {
            if (!registrationValid || !Accepts(action)) return null;
            Initialize();
            switch (action)
            {
                case InstrumentAction.Cut:
                    if (IsCut || !TraceSeam(previousWorldPoint, worldPoint)) return null;
                    IsCut = true;
                    if (leftHalf != null) leftHalf.localPosition = leftRest + Vector3.back * 0.012f;
                    if (rightHalf != null) rightHalf.localPosition = rightRest + Vector3.forward * 0.012f;
                    return "seam_separated";
                case InstrumentAction.Seal:
                    if (IsSealed) return null;
                    sealProgress += Mathf.Max(0, deltaSeconds);
                    if (sealProgress < 0.75f) return null;
                    IsSealed = true;
                    Tint(new Color(0.45f, 0.2f, 0.18f));
                    return "simulated_seal_marked";
                case InstrumentAction.Clip:
                    if (ClipCount >= 6) return null;
                    ClipCount++;
                    Mark(worldPoint, new Vector3(0.009f, 0.003f, 0.006f));
                    return "clip_marked";
                case InstrumentAction.Staple:
                    if (StapleCount >= 3) return null;
                    StapleCount++;
                    for (int i = -2; i <= 2; i++) Mark(worldPoint + transform.right * i * 0.008f, new Vector3(0.004f, 0.002f, 0.003f));
                    return "staple_row_marked";
                case InstrumentAction.Suction:
                    if (fluidVisual == null || FluidRemaining <= 0) return null;
                    FluidRemaining = Mathf.Max(0, FluidRemaining - Mathf.Max(0, deltaSeconds) * 0.6f);
                    fluidVisual.localScale = new Vector3(fluidRest.x, fluidRest.y * FluidRemaining, fluidRest.z);
                    return FluidRemaining <= 0 ? "fluid_demo_empty" : null;
                case InstrumentAction.PlacePort:
                    if (PortPlaced) return null;
                    PortPlaced = true;
                    Mark(worldPoint, new Vector3(0.02f, 0.003f, 0.02f));
                    return "port_demo_marked";
                case InstrumentAction.Close:
                    if (IsClosed || !IsCut) return null;
                    IsClosed = true;
                    if (leftHalf != null) leftHalf.localPosition = leftRest;
                    if (rightHalf != null) rightHalf.localPosition = rightRest;
                    return "seam_closed";
                default: return null;
            }
        }

        bool TraceSeam(Vector3 previous, Vector3 current)
        {
            Vector3 a = transform.InverseTransformPoint(previous), b = transform.InverseTransformPoint(current);
            if (Mathf.Abs(a.y) > seamTolerance || Mathf.Abs(b.y) > seamTolerance || Mathf.Abs(a.z) > seamTolerance || Mathf.Abs(b.z) > seamTolerance || Mathf.Abs(b.x) > seamWidth * 0.6f || (b - a).magnitude > 0.025f)
                return false;
            if ((b - a).magnitude < 0.001f) return false;
            float low = Mathf.Min(a.x, b.x), high = Mathf.Max(a.x, b.x);
            // Coverage bins avoid counting repeated motion over the same small part of the seam.
            for (int i = 0; i < 10; i++)
            {
                float cellCenter = -seamWidth * 0.5f + (i + 0.5f) * seamWidth / 10;
                if (cellCenter >= low && cellCenter <= high) seamCoverage |= 1 << i;
            }
            int covered = 0;
            for (int i = 0; i < 10; i++) if ((seamCoverage & (1 << i)) != 0) covered++;
            return covered >= 9;
        }

        void Mark(Vector3 point, Vector3 size)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "SimulatedFastener";
            marker.transform.SetParent(transform, false);
            marker.transform.SetPositionAndRotation(point, transform.rotation);
            marker.transform.localScale = size;
            var collider = marker.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider);
            markers.Add(marker);
        }

        void Tint(Color color)
        {
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", color);
            block.SetColor("_Color", color);
            foreach (var renderer in GetComponentsInChildren<Renderer>()) renderer.SetPropertyBlock(block);
        }

        public void ResetTeachingTarget()
        {
            Initialize();
            if (owner != null) ReleaseGrasp(owner);
            IsCut = IsSealed = PortPlaced = IsClosed = false;
            ClipCount = StapleCount = 0;
            sealProgress = 0;
            seamCoverage = 0;
            FluidRemaining = 1;
            if (leftHalf != null) leftHalf.localPosition = leftRest;
            if (rightHalf != null) rightHalf.localPosition = rightRest;
            if (fluidVisual != null) fluidVisual.localScale = fluidRest;
            foreach (var marker in markers) { if (Application.isPlaying) Destroy(marker); else DestroyImmediate(marker); }
            markers.Clear();
            foreach (var renderer in GetComponentsInChildren<Renderer>()) renderer.SetPropertyBlock(null);
        }

        void OnDisable() { if (owner != null) ReleaseGrasp(owner); }
    }
}
