using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Preop;

namespace Scalpal.Anatomy.EditorTools
{
    /// <summary>Optional desktop inspection scene. Contains no XR rig or participant registration.</summary>
    public static class AnatomyPreviewSceneBuilder
    {
        public const string ScenePath = "Assets/Scalpal/Anatomy/Scenes/AnatomyPreview.unity";
        public const string DemoScenePath = "Assets/Scalpal/Anatomy/Scenes/AppendectomyDemo.unity";

        [MenuItem("Scalpal/Anatomy/Build Appendectomy Demo Scene")]
        public static void BuildDemoFromMenu()
        {
            Build(true);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScenePath);
        }

        [MenuItem("Scalpal/Anatomy/Build Atlas Preview Scene")]
        public static void BuildFromMenu()
        {
            try
            {
                Build();
                var sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
                Selection.activeObject = sceneAsset;
                EditorGUIUtility.PingObject(sceneAsset);
            }
            catch (Exception error)
            {
                Debug.LogError("[Scalpal anatomy] Desktop preview scene build failed: " + error.Message);
            }
        }

        public static void Build() { Build(false); }

        public static void Build(bool exerciseDemo)
        {
            var prefab = AnatomyAtlasBuilder.Build();
            const string folder = "Assets/Scalpal/Anatomy/Scenes";
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Scalpal/Anatomy", "Scenes");
            var previous = SceneManager.GetActiveScene();
            // Additive construction preserves the user's open scene and unsaved edits.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var body = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                if (body == null) throw new InvalidOperationException("Could not instantiate the body preview prefab.");
                if (exerciseDemo)
                {
                    var binding = body.AddComponent<AnatomyExerciseBinding>();
                    binding.anatomy = body.GetComponent<AnatomyController>();
                    binding.requireCoachSynchronization = false;
                    var relay = body.AddComponent<CoachRelay>();
                    binding.coach = relay;
                    var coachBinding = body.AddComponent<AnatomyCoachBinding>();
                    coachBinding.anatomy = binding.anatomy;
                    coachBinding.relay = relay;
                    var service = body.AddComponent<ScalpalPreopService>();
                    var source = body.AddComponent<AnatomyCaseSource>();
                    source.service = service;
                    source.exercise = binding;
                    var controls = body.AddComponent<AnatomyDemoPanel>();
                    controls.exercise = binding;
                    controls.caseSource = source;
                    controls.caseBundle = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
                    if (controls.caseBundle == null) throw new InvalidOperationException("Missing packaged exercise bundle.");
                }
                else
                {
                    var controls = body.AddComponent<AnatomyPreviewPanel>();
                    controls.anatomy = body.GetComponent<AnatomyController>();
                }
                var renderers = body.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) throw new InvalidOperationException("The body preview has no renderers to frame.");
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);

                var cameraObject = new GameObject("Desktop anatomy camera (not XR)");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(0.1f, Mathf.Max(bounds.extents.y, bounds.extents.x) * 1.25f);
                float distance = Mathf.Max(1f, bounds.size.magnitude * 1.5f);
                camera.transform.position = bounds.center + new Vector3(0f, 0f, -distance);
                camera.transform.LookAt(bounds.center);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = distance * 3f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.045f, 0.055f, 0.07f);

                var lightObject = new GameObject("Desktop anatomy key light");
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                light.transform.rotation = Quaternion.Euler(25f, -25f, 0f);

                var targetPath = exerciseDemo ? DemoScenePath : ScenePath;
                if (!EditorSceneManager.SaveScene(scene, targetPath))
                    throw new InvalidOperationException("Unity could not save " + targetPath);
                Debug.Log("[Scalpal anatomy] Saved desktop inspection scene at " + targetPath
                    + ". Open it and press Play to inspect the rotating atlas. No XR rig, participant registration, or build settings were added.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
