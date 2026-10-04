using System;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest.Editor
{
    // The real NativeWorkbench.Locomote route with a scripted thumbstick. Full-VR learners must be able to walk
    // around the table and turn, without leaving the room.
    public static class NativeLocomotionValidation
    {
        static int checks;

        [MenuItem("Scalpal/Quest/Validate Operating Room Locomotion")]
        public static void Run()
        {
            checks = 0;
            var priorStick = XRInput.StickSource;
            var root = new GameObject("LocomotionFixture");
            root.SetActive(false); // Awake would disable the unbound rig; Locomote needs only the origin and head.
            try
            {
                var origin = new GameObject("Origin").transform; origin.SetParent(root.transform);
                var head = new GameObject("Head").AddComponent<Camera>(); head.transform.SetParent(origin, false);
                head.transform.localPosition = new Vector3(.4f, 1.6f, 0); // off-centre in the play space, so the turn pivot matters
                var rig = root.AddComponent<NativeWorkbench>();
                rig.trackingOrigin = origin; rig.headCamera = head; rig.initialHeadFloorPosition = Vector3.zero;
                Vector2 left = Vector2.zero, right = Vector2.zero;
                XRInput.StickSource = node => node == XRNode.LeftHand ? left : right;

                rig.Locomote(1);
                Check(origin.position == Vector3.zero, "a resting stick does not drift the learner");
                left = new Vector2(0, 1); rig.Locomote(1);
                Check(Mathf.Abs(origin.position.z - rig.moveSpeed) < 1e-4f && Mathf.Abs(origin.position.y) < 1e-6f, "left stick forward walks along the gaze on the floor plane");
                for (int i = 0; i < 20; i++) rig.Locomote(1);
                Check(Vector3.ProjectOnPlane(head.transform.position, Vector3.up).magnitude <= rig.roamRadius + 1e-3f && origin.position.z > rig.roamRadius - 1, "walking stops at the room boundary");

                left = Vector2.zero; origin.position = Vector3.zero; origin.rotation = Quaternion.identity;
                var headBefore = head.transform.position;
                right = new Vector2(1, 0); rig.Locomote(.016f);
                Check(Mathf.Abs(Mathf.DeltaAngle(origin.eulerAngles.y, rig.snapDegrees)) < .01f, "right stick snap-turns once by the snap angle");
                Check((head.transform.position - headBefore).sqrMagnitude < 1e-8f, "a snap turn pivots about the head, not the room origin");
                rig.Locomote(.016f);
                Check(Mathf.Abs(Mathf.DeltaAngle(origin.eulerAngles.y, rig.snapDegrees)) < .01f, "holding the stick does not keep turning");
                right = Vector2.zero; rig.Locomote(.016f); right = new Vector2(-1, 0); rig.Locomote(.016f);
                Check(Mathf.Abs(Mathf.DeltaAngle(origin.eulerAngles.y, 0)) < .01f, "releasing and pushing the other way turns back");
                Debug.Log("SCALPAL_NATIVE_LOCOMOTION_VALIDATION_OK checks=" + checks);
            }
            finally
            {
                XRInput.StickSource = priorStick;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("SCALPAL_NATIVE_LOCOMOTION_VALIDATION_FAIL " + message);
            checks++;
        }
    }
}
