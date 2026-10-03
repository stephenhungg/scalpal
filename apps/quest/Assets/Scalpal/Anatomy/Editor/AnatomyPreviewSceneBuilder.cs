using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scalpal.Anatomy.EditorTools
{
    /// <summary>Optional desktop inspection scene. Contains no XR rig or participant registration.</summary>
    public static class AnatomyPreviewSceneBuilder
    {
        public const string ScenePath = "Assets/Scalpal/Anatomy/Scenes/AnatomyPreview.unity";

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

        public static void Build()
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

                if (!EditorSceneManager.SaveScene(scene, ScenePath))
                    throw new InvalidOperationException("Unity could not save " + ScenePath);
                Debug.Log("[Scalpal anatomy] Saved desktop inspection scene at " + ScenePath
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
