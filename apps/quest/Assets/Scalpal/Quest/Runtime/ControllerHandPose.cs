using Scalpal.Instruments;
using UnityEngine;

namespace Scalpal.Quest
{
    // Gloved hand driven by a Touch controller. There is no hand tracking: the grip pose places the
    // hand, analog grip curls middle/ring/pinky, trigger curls the index, and a held instrument closes
    // the fist around its handle. Render-only: the hierarchy has no colliders, so pointer rays,
    // pickup overlaps and instrument tip contacts never see it.
    [DisallowMultipleComponent]
    public sealed class ControllerHandPose : MonoBehaviour
    {
        public XRInstrumentInput input;
        public SkinnedMeshRenderer skin;
        public Transform palm;
        // Proximal to distal. Each chain's last entry is the unskinned tip end, not a rotated joint.
        public Transform[] index, middle, ring, pinky, thumb;

        // Fist joint angles (degrees) for MCP, PIP, DIP, plus thumb CMC, MCP, IP, distal.
        static readonly float[] FingerFist = { 72, 95, 55 };
        static readonly float[] ThumbFist = { 10, 22, 30, 35 };
        const float RestCurl = 0.2f;

        InstrumentInteractor interactor;
        Quaternion[][] rest;
        Vector3[][] axes;

        public float IndexCurl { get; private set; }
        public float GripCurl { get; private set; }

        void LateUpdate()
        {
            if (!input) return;
            if (!interactor) interactor = input.GetComponent<InstrumentInteractor>();
            bool live = input.enabled && interactor && interactor.TrackingValid;
            if (skin) skin.enabled = live;
            if (!live) return;
            Apply(XRInput.Grip(input.controller), XRInput.Trigger(input.controller), interactor.HeldInstrument);
        }

        // A held tool keeps the fist closed around its handle; the index still squeezes with the trigger.
        public void Apply(float grip, float trigger, bool holding)
        {
            grip = Mathf.Clamp01(grip); trigger = Mathf.Clamp01(trigger);
            GripCurl = holding ? 0.9f : Mathf.Lerp(RestCurl, 1f, grip);
            IndexCurl = holding ? Mathf.Lerp(0.55f, 0.85f, trigger) : Mathf.Lerp(RestCurl, 0.8f, trigger);
            float thumbCurl = holding ? 0.8f : Mathf.Lerp(RestCurl, 0.7f, Mathf.Max(grip, trigger));
            Bind();
            Pose(0, IndexCurl, FingerFist); Pose(1, GripCurl, FingerFist); Pose(2, GripCurl, FingerFist);
            Pose(3, GripCurl, FingerFist); Pose(4, thumbCurl, ThumbFist);
        }

        void Pose(int chain, float curl, float[] fist)
        {
            var bones = Chain(chain);
            for (int i = 0; i < bones.Length - 1; i++)
                bones[i].localRotation = rest[chain][i] * Quaternion.AngleAxis(curl * fist[Mathf.Min(i, fist.Length - 1)], axes[chain][i]);
        }

        Transform[] Chain(int chain) => chain == 0 ? index : chain == 1 ? middle : chain == 2 ? ring : chain == 3 ? pinky : thumb;

        // Flexion axes come from the authored open pose: each joint turns its segment toward the palm
        // (the thumb toward the palm centre), so the same code serves the mirrored left and right meshes.
        void Bind()
        {
            if (rest != null) return;
            rest = new Quaternion[5][]; axes = new Vector3[5][];
            Vector3 palmFacing = PalmFacing();
            for (int chain = 0; chain < 5; chain++)
            {
                var bones = Chain(chain);
                rest[chain] = new Quaternion[bones.Length]; axes[chain] = new Vector3[bones.Length];
                for (int i = 0; i < bones.Length - 1; i++)
                {
                    rest[chain][i] = bones[i].localRotation;
                    Vector3 along = (bones[i + 1].position - bones[i].position).normalized;
                    Vector3 toward = chain == 4 ? palm.position + palmFacing * 0.03f - bones[i].position : palmFacing;
                    Vector3 axis = Vector3.Cross(along, Vector3.ProjectOnPlane(toward, along)).normalized;
                    axes[chain][i] = Quaternion.Inverse(bones[i].rotation) * axis;
                }
            }
        }

        // Palm-facing direction from the skeleton's knuckle line and finger direction; mirrored per hand.
        Vector3 PalmFacing()
        {
            Vector3 knuckles = (index[0].position + middle[0].position + ring[0].position + pinky[0].position) * 0.25f;
            Vector3 along = (knuckles - index[0].parent.position).normalized;
            Vector3 thumbSide = (index[0].position - pinky[0].position).normalized;
            bool left = input && input.controller == UnityEngine.XR.XRNode.LeftHand;
            return (left ? Vector3.Cross(thumbSide, along) : Vector3.Cross(along, thumbSide)).normalized;
        }
    }
}
