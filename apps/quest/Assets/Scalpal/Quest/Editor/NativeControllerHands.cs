using System;
using System.Linq;
using System.Reflection;
using Scalpal.Instruments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Scalpal.Quest.Editor
{
    // Replaces the old grip-marker spheres with gloved Meta XR Core SDK hand meshes
    // (OculusHand_L/R.fbx, Oculus SDK License, already a dependency of MR Utility Kit).
    // The meshes are referenced from the package; nothing is copied into Assets.
    public static class NativeControllerHands
    {
        public const string GloveMaterialPath = "Assets/Scalpal/Quest/Materials/NitrileGlove.mat";
        const string HandName = "ControllerHand";
        const int TriangleBudget = 3000;
        static readonly string[] Scenes = { NativeQuestBuild.ScenePath, NativeSessionBuild.ScenePath };
        static readonly string[] Fingers = { "index", "middle", "ring", "pinky", "thumb" };

        static string ModelPath(XRNode node) => "Packages/com.meta.xr.sdk.core/Meshes/HandTracking/OculusHand_" + (node == XRNode.LeftHand ? "L" : "R") + ".fbx";

        // OpenXR grip pose in Unity axes: +Z runs through the closed fist from little finger to thumb,
        // +X leaves the left palm and enters the right palm. Open fingers therefore point along -Y.
        static Vector3 PalmFacing(XRNode node) => node == XRNode.LeftHand ? Vector3.right : Vector3.left;

        [MenuItem("Scalpal/Quest/Install Controller Hands")]
        public static void Apply()
        {
            foreach (var path in Scenes)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var workbench = UnityEngine.Object.FindFirstObjectByType<NativeWorkbench>();
                if (!workbench) throw new InvalidOperationException("Native workbench rig is missing: " + path);
                foreach (var input in workbench.inputs) Install(input);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save " + path);
            }
            AssetDatabase.SaveAssets();
            Validate();
        }

        public static ControllerHandPose Install(XRInstrumentInput input)
        {
            foreach (Transform child in input.transform.Cast<Transform>().ToArray())
                if (child.name == "ControllerGripMarker" || child.name == HandName) UnityEngine.Object.DestroyImmediate(child.gameObject);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath(input.controller));
            if (!model) throw new InvalidOperationException("Meta XR Core SDK hand mesh is missing: " + ModelPath(input.controller));
            var root = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = HandName;
            foreach (var animator in root.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(animator);
            var palm = root.GetComponentsInChildren<Transform>(true).Single(bone => bone.name.EndsWith("_palm_center_marker", StringComparison.Ordinal));
            foreach (var marker in root.GetComponentsInChildren<Transform>(true).Where(bone => bone != palm && bone.name.EndsWith("_marker", StringComparison.Ordinal)).ToArray())
                UnityEngine.Object.DestroyImmediate(marker.gameObject);
            var bones = root.GetComponentsInChildren<Transform>(true);
            Transform Bone(string suffix) => bones.Single(bone => bone.name.EndsWith(suffix, StringComparison.Ordinal) && bone.name.StartsWith("b_", StringComparison.Ordinal));

            var hand = root.AddComponent<ControllerHandPose>();
            hand.input = input;
            hand.palm = palm;
            hand.skin = root.GetComponentInChildren<SkinnedMeshRenderer>(true);
            hand.skin.sharedMaterial = GloveMaterial();
            hand.skin.shadowCastingMode = ShadowCastingMode.Off;
            hand.skin.receiveShadows = false;
            hand.skin.lightProbeUsage = LightProbeUsage.Off;
            hand.skin.reflectionProbeUsage = ReflectionProbeUsage.Off;
            hand.skin.updateWhenOffscreen = false;
            hand.skin.enabled = false;
            hand.index = new[] { Bone("_index1"), Bone("_index2"), Bone("_index3"), Bone("_index_null") };
            hand.middle = new[] { Bone("_middle1"), Bone("_middle2"), Bone("_middle3"), Bone("_middle_null") };
            hand.ring = new[] { Bone("_ring1"), Bone("_ring2"), Bone("_ring3"), Bone("_ring_null") };
            hand.pinky = new[] { Bone("_pinky1"), Bone("_pinky2"), Bone("_pinky3"), Bone("_pinky_null") };
            hand.thumb = new[] { Bone("_thumb0"), Bone("_thumb1"), Bone("_thumb2"), Bone("_thumb3"), Bone("_thumb_null") };

            // Map the open skeleton (finger direction, palm normal) onto the grip frame, then put the
            // palm surface just outside the fist axis so a held handle sits inside the closed fingers.
            Vector3 knuckles = (hand.index[0].position + hand.middle[0].position + hand.ring[0].position + hand.pinky[0].position) * 0.25f;
            Vector3 along = (knuckles - hand.index[0].parent.position).normalized;
            Vector3 thumbSide = (hand.index[0].position - hand.pinky[0].position).normalized;
            bool left = input.controller == XRNode.LeftHand;
            Vector3 facing = (left ? Vector3.Cross(thumbSide, along) : Vector3.Cross(along, thumbSide)).normalized;
            var meshFrame = Quaternion.LookRotation(along, facing);
            var gripFrame = Quaternion.LookRotation(Vector3.down, PalmFacing(input.controller));
            root.transform.SetParent(input.transform, false);
            root.transform.localRotation = gripFrame * Quaternion.Inverse(meshFrame);
            root.transform.localPosition = Vector3.zero;
            Vector3 palmLocal = input.transform.InverseTransformPoint(palm.position);
            root.transform.localPosition = -palmLocal - PalmFacing(input.controller) * 0.02f;
            return hand;
        }

        static Material GloveMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(GloveMaterialPath);
            bool create = !material;
            var shader = Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("Standard shader is missing.");
            if (create) material = new Material(shader);
            material.shader = shader;
            material.name = "NitrileGlove";
            // Untextured blue-violet nitrile with a soft sheen.
            material.color = new Color(0.36f, 0.42f, 0.86f);
            material.SetFloat("_Glossiness", 0.42f);
            material.SetFloat("_Metallic", 0f);
            if (create)
            {
                if (!AssetDatabase.IsValidFolder("Assets/Scalpal/Quest/Materials")) AssetDatabase.CreateFolder("Assets/Scalpal/Quest", "Materials");
                AssetDatabase.CreateAsset(material, GloveMaterialPath);
            }
            else EditorUtility.SetDirty(material);
            return material;
        }

        sealed class Source : IXRInputSource
        {
            public float grip, trigger;
            public bool DisplayRunning => true;
            public bool FloorTracking => true;
            public bool HasFocus => true;
            public bool TryPose(XRNode node, out Pose pose) { pose = Pose.identity; return true; }
            public float Grip(XRNode node) => grip;
            public float Trigger(XRNode node) => trigger;
            public bool Button(XRNode node, XRInputButton button) => false;
        }

        static int checks;
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Controller hands: " + message);
            checks++;
        }

        // Fails on the former sphere markers, on any collider/rigidbody in a hand, on a non-skinned or
        // over-budget hand, and on fingers that do not follow grip, trigger and a held instrument.
        [MenuItem("Scalpal/Quest/Validate Controller Hands")]
        public static void Validate()
        {
            checks = 0;
            var oldSource = XRInput.Source;
            try
            {
                foreach (var path in Scenes) ValidateScene(path);
            }
            finally { XRInput.Source = oldSource; }
            Debug.Log("SCALPAL_CONTROLLER_HANDS_OK scenes=" + Scenes.Length + " checks=" + checks);
        }

        static void ValidateScene(string path)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var workbench = UnityEngine.Object.FindFirstObjectByType<NativeWorkbench>();
            Require(workbench && workbench.inputs != null && workbench.inputs.Length == 2, path + " has two controller inputs");
            var sphere = Resources.GetBuiltinResource<Mesh>("New-Sphere.fbx");
            foreach (var input in workbench.inputs)
            {
                var name = path + " " + input.controller;
                Require(!input.GetComponentsInChildren<MeshFilter>(true).Any(filter => filter.sharedMesh == sphere)
                    && !input.GetComponentsInChildren<Transform>(true).Any(t => t.name == "ControllerGripMarker"), name + " has no sphere grip marker");
                var hands = input.GetComponentsInChildren<ControllerHandPose>(true);
                Require(hands.Length == 1 && hands[0].input == input && hands[0].transform.parent == input.transform, name + " has one bound hand");
                var hand = hands[0];
                var skins = hand.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Require(skins.Length == 1 && hand.skin == skins[0] && hand.GetComponentsInChildren<Renderer>(true).Length == 1, name + " is one skinned mesh");
                var mesh = hand.skin.sharedMesh;
                Require(mesh && AssetDatabase.GetAssetPath(mesh) == ModelPath(input.controller) && hand.skin.bones.Length == 17
                    && mesh.triangles.Length / 3 <= TriangleBudget, name + " uses the rigged Meta hand within " + TriangleBudget + " triangles");
                Require(hand.skin.sharedMaterial && AssetDatabase.GetAssetPath(hand.skin.sharedMaterial) == GloveMaterialPath
                    && hand.skin.sharedMaterial.shader.name == "Standard" && hand.skin.shadowCastingMode == ShadowCastingMode.Off, name + " wears the nitrile glove");
                // No collider at all: the scene pointer ray, pickup overlap and tip contacts query every layer.
                Require(hand.GetComponentsInChildren<Collider>(true).Length == 0 && hand.GetComponentsInChildren<Rigidbody>(true).Length == 0
                    && hand.GetComponentsInChildren<Animator>(true).Length == 0, name + " has no collider, rigidbody or animator");
                Require(!hand.skin.enabled, name + " starts hidden until tracking is valid");
                ValidateCurl(workbench, input, hand, name);
            }
        }

        static float Reach(ControllerHandPose hand, Transform[] chain) => Vector3.Distance(chain[chain.Length - 1].position, hand.palm.position);

        static void ValidateCurl(NativeWorkbench workbench, XRInstrumentInput input, ControllerHandPose hand, string name)
        {
            var interactor = input.GetComponent<InstrumentInteractor>();
            var source = new Source();
            XRInput.Source = source;
            var lateUpdate = typeof(ControllerHandPose).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
            input.enabled = true;
            interactor.SetTrackedPose(input.transform.position, input.transform.rotation, true);
            try
            {
                // The production LateUpdate route reads XR grip/trigger and the interactor's held tool.
                lateUpdate.Invoke(hand, null);
                Require(hand.skin.enabled, name + " shows while tracked");
                float openIndex = Reach(hand, hand.index), openMiddle = Reach(hand, hand.middle), openPinky = Reach(hand, hand.pinky);
                source.grip = 1; lateUpdate.Invoke(hand, null);
                Require(Reach(hand, hand.middle) < openMiddle - 0.03f && Reach(hand, hand.pinky) < openPinky - 0.02f, name + " grip curls middle/ring/pinky");
                Require(Mathf.Abs(Reach(hand, hand.index) - openIndex) < 0.001f, name + " grip leaves the trigger finger");
                source.grip = 0; source.trigger = 1; lateUpdate.Invoke(hand, null);
                Require(Reach(hand, hand.index) < openIndex - 0.025f && Mathf.Abs(Reach(hand, hand.middle) - openMiddle) < 0.001f, name + " trigger curls only the index");
                source.trigger = 0; lateUpdate.Invoke(hand, null);
                Require(Mathf.Abs(Reach(hand, hand.middle) - openMiddle) < 0.001f && Mathf.Abs(Reach(hand, hand.index) - openIndex) < 0.001f, name + " release reopens the hand");

                var tool = workbench.tools.First(t => t && t.gripAnchor);
                var anchor = tool.gripAnchor.position;
                interactor.SetTrackedPose(anchor, input.transform.rotation, true);
                Require(interactor.TryPickup(tool) && interactor.HeldInstrument == tool, name + " picks up " + tool.instrumentId);
                lateUpdate.Invoke(hand, null);
                Require(Reach(hand, hand.middle) < openMiddle - 0.03f && Reach(hand, hand.index) < openIndex - 0.015f, name + " closes around a held instrument without grip input");
                Require(hand.GetComponentsInChildren<Collider>(true).Length == 0, name + " held pose adds no collider");

                input.enabled = false; lateUpdate.Invoke(hand, null);
                Require(!hand.skin.enabled, name + " hides when the input is gated");
            }
            finally
            {
                if (interactor.HeldInstrument) interactor.ReturnHeldToRest();
                input.enabled = false;
            }
        }
    }
}
