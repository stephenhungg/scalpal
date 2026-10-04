// One click: Scalpal > Hands > Set Up Live Hands In Scene. Adds the hand tracker, overlay, stream, and
// recorder to the open scene, wires the MediaPipe models and the passthrough camera, creates a glowing
// joint material, and enables the Android internet permission the UDP stream needs.
#if SCALPAL_HANDS
using Meta.XR;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Hands.EditorTools
{
    static class ScalpalHandsSetup
    {
        const string Root = "Assets/Scalpal/Hands";
        const string MaterialPath = Root + "/Materials/HandJoint.mat";

        [MenuItem("Scalpal/Hands/Set Up Live Hands In Scene")]
        static void SetUp()
        {
            var detector = AssetDatabase.LoadAssetAtPath<ModelAsset>(Root + "/Models/hand_detector.onnx");
            var landmarker = AssetDatabase.LoadAssetAtPath<ModelAsset>(Root + "/Models/hand_landmarks_detector.onnx");
            var anchors = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Models/hand_anchors.csv");
            if (detector == null || landmarker == null || anchors == null)
            {
                EditorUtility.DisplayDialog("Scalpal Hands", "Models are missing under " + Root + "/Models. Run `git lfs pull` and let Unity import them.", "OK");
                return;
            }

            var camera = Object.FindFirstObjectByType<PassthroughCameraAccess>();
            if (camera == null)
            {
                var cameraObject = new GameObject("Passthrough Camera Access");
                Undo.RegisterCreatedObjectUndo(cameraObject, "Scalpal Hands");
                camera = cameraObject.AddComponent<PassthroughCameraAccess>();
                Debug.LogWarning("[Scalpal.Hands] Added a PassthroughCameraAccess. Prefer the one from Meta's camera samples if the scene has their rig.");
            }

            var existing = Object.FindFirstObjectByType<BlazeHandTracker>();
            var root = existing != null ? existing.gameObject : new GameObject("Scalpal Hands");
            if (existing == null) Undo.RegisterCreatedObjectUndo(root, "Scalpal Hands");
            var tracker = GetOrAdd<BlazeHandTracker>(root);
            var view = GetOrAdd<HandSkeletonView>(root);
            var streamer = GetOrAdd<HandStreamer>(root);
            var recorder = GetOrAdd<HandEpisodeRecorder>(root);

            Assign(tracker, ("cameraAccess", camera), ("handDetector", detector), ("handLandmarker", landmarker), ("anchorsCsv", anchors));
            Assign(view, ("tracker", tracker), ("material", JointMaterial()));
            Assign(streamer, ("tracker", tracker));
            Assign(recorder, ("tracker", tracker), ("cameraAccess", camera));

            // The live stream sends UDP to the Mac.
            PlayerSettings.Android.forceInternetPermission = true;

            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = root;
            Debug.Log("[Scalpal.Hands] Live hands ready. Set HandStreamer.host to the Mac's LAN IP, then on the Mac run: cd services/motion && uv run scalpal-motion live");
        }

        static T GetOrAdd<T>(GameObject target) where T : Component
        {
            var component = target.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(target);
        }

        static void Assign(Object target, params (string field, Object value)[] fields)
        {
            var serialized = new SerializedObject(target);
            foreach (var (field, value) in fields)
            {
                var property = serialized.FindProperty(field);
                if (property == null)
                {
                    Debug.LogWarning($"[Scalpal.Hands] {target.GetType().Name} has no field {field}");
                    continue;
                }
                property.objectReferenceValue = value;
            }
            serialized.ApplyModifiedProperties();
        }

        // Unlit, so joints read clearly against passthrough. URP first, built-in fallback.
        static Material JointMaterial()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null) return material;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            material = new Material(shader);
            if (!AssetDatabase.IsValidFolder(Root + "/Materials")) AssetDatabase.CreateFolder(Root, "Materials");
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }
    }
}
#endif
