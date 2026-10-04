// Draws the live MediaPipe joints on the learner's real hands: 21 joints and the 21 standard bones per hand,
// colored by tracking confidence so the learner sees when the robot's training data is good.
// Smoothing here is display-only; the stream and recorder keep the raw joints.
#if SCALPAL_HANDS
using UnityEngine;

namespace Scalpal.Hands
{
    [DisallowMultipleComponent]
    public sealed class HandSkeletonView : MonoBehaviour
    {
        // MediaPipe hand connections: thumb, index, middle, ring, pinky, and the palm.
        static readonly int[] Bones =
        {
            0, 1, 1, 2, 2, 3, 3, 4,
            0, 5, 5, 6, 6, 7, 7, 8,
            9, 10, 10, 11, 11, 12,
            13, 14, 14, 15, 15, 16,
            0, 17, 17, 18, 18, 19, 19, 20,
            5, 9, 9, 13, 13, 17,
        };

        [SerializeField] BlazeHandTracker tracker;
        [Tooltip("Unlit or emissive material for joints and bones. One shared material, colored per instance.")]
        [SerializeField] Material material;
        [SerializeField] float jointRadius = 0.007f;
        [SerializeField] float boneWidth = 0.004f;
        [SerializeField] Color confident = new Color(0.2f, 1f, 0.6f, 1f);
        [SerializeField] Color uncertain = new Color(1f, 0.8f, 0.1f, 1f);
        [Tooltip("Presence at or above this draws fully confident.")]
        [SerializeField] float confidentPresence = 0.9f;
        [SerializeField] bool smooth = true;

        Slot[] slots;
        MaterialPropertyBlock block;
        static readonly int ColorId = Shader.PropertyToID("_BaseColor");
        static readonly int LegacyColorId = Shader.PropertyToID("_Color");

        sealed class Slot
        {
            public GameObject root;
            public Transform[] joints;
            public LineRenderer[] bones;
            public OneEuroFilter[] filters;
            public string hand;
        }

        void Awake()
        {
            block = new MaterialPropertyBlock();
            slots = new Slot[2];
            for (var s = 0; s < slots.Length; s++) slots[s] = BuildSlot(s);
        }

        void OnEnable() { if (tracker != null) tracker.FrameReady += OnFrame; }
        void OnDisable() { if (tracker != null) tracker.FrameReady -= OnFrame; }

        Slot BuildSlot(int index)
        {
            var slot = new Slot
            {
                root = new GameObject("hand_skeleton_" + index),
                joints = new Transform[BlazeHandMath.NumJoints],
                bones = new LineRenderer[Bones.Length / 2],
                filters = new OneEuroFilter[BlazeHandMath.NumJoints * 3],
            };
            slot.root.transform.SetParent(transform, false);
            for (var i = 0; i < slot.joints.Length; i++)
            {
                var joint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(joint.GetComponent<Collider>());
                joint.name = "joint_" + i;
                joint.transform.SetParent(slot.root.transform, false);
                joint.transform.localScale = Vector3.one * (2f * jointRadius * (i == 0 ? 1.4f : 1f));
                if (material != null) joint.GetComponent<Renderer>().sharedMaterial = material;
                slot.joints[i] = joint.transform;
            }
            for (var b = 0; b < slot.bones.Length; b++)
            {
                var line = new GameObject("bone_" + b).AddComponent<LineRenderer>();
                line.transform.SetParent(slot.root.transform, false);
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.startWidth = boneWidth;
                line.endWidth = boneWidth;
                if (material != null) line.sharedMaterial = material;
                slot.bones[b] = line;
            }
            for (var f = 0; f < slot.filters.Length; f++) slot.filters[f] = new OneEuroFilter();
            slot.root.SetActive(false);
            return slot;
        }

        void OnFrame(HandJointsFrame frame)
        {
            for (var s = 0; s < slots.Length; s++)
            {
                var slot = slots[s];
                var observation = s < frame.hands.Length ? frame.hands[s] : null;
                if (observation == null)
                {
                    if (slot.root.activeSelf)
                    {
                        slot.root.SetActive(false);
                        foreach (var f in slot.filters) f.Reset();
                    }
                    continue;
                }
                // A hand swapping slots restarts its smoothing instead of sliding across the room.
                if (slot.hand != observation.hand) foreach (var f in slot.filters) f.Reset();
                slot.hand = observation.hand;
                slot.root.SetActive(true);
                var color = Color.Lerp(uncertain, confident, Mathf.InverseLerp(0.5f, confidentPresence, observation.presence));
                for (var i = 0; i < BlazeHandMath.NumJoints; i++)
                {
                    var p = new Vector3(observation.jointsWorld[3 * i], observation.jointsWorld[3 * i + 1], observation.jointsWorld[3 * i + 2]);
                    if (smooth)
                    {
                        p.x = slot.filters[3 * i].Filter(p.x, frame.unityTime);
                        p.y = slot.filters[3 * i + 1].Filter(p.y, frame.unityTime);
                        p.z = slot.filters[3 * i + 2].Filter(p.z, frame.unityTime);
                    }
                    slot.joints[i].position = p;
                    Tint(slot.joints[i].GetComponent<Renderer>(), color);
                }
                for (var b = 0; b < slot.bones.Length; b++)
                {
                    slot.bones[b].SetPosition(0, slot.joints[Bones[2 * b]].position);
                    slot.bones[b].SetPosition(1, slot.joints[Bones[2 * b + 1]].position);
                    slot.bones[b].startColor = color;
                    slot.bones[b].endColor = color;
                }
            }
        }

        void Tint(Renderer renderer, Color color)
        {
            renderer.GetPropertyBlock(block);
            block.SetColor(ColorId, color);
            block.SetColor(LegacyColorId, color);
            renderer.SetPropertyBlock(block);
        }
    }
}
#endif
