using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Environments.Editor
{
    // Static art preview only. This does not initialize XR, registration or exercise state.
    public static class EnvironmentPreviewBuilder
    {
        const string Root = "Assets/Scalpal/Environment/";
        const string ScenePath = Root + "Samples/OperatingRoomPreview.unity";

        [MenuItem("Scalpal/Environment/Build Static Operating Room Preview")]
        public static void Build()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (string directory in new[] { "Materials", "Prefabs", "Samples" }) Directory.CreateDirectory(Root + directory);
                AssetDatabase.Refresh();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var room = CreateModel("OperatingTheatre");
                room.name = "VirtualOperatingRoom";
                PrefabUtility.SaveAsPrefabAsset(room, Root + "Prefabs/OperatingTheatre.prefab");
                UnityEngine.Object.DestroyImmediate(room);
                room = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/OperatingTheatre.prefab"));
                var patient = CreateModel("SupinePatient");
                patient.name = "VirtualPatient";
                PrefabUtility.SaveAsPrefabAsset(patient, Root + "Prefabs/SupinePatient.prefab");
                UnityEngine.Object.DestroyImmediate(patient);
                patient = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/SupinePatient.prefab"));
                var patientRoot = new GameObject("PatientRoot");
                patient.transform.SetParent(patientRoot.transform, true);
                var bounds = GetBounds(patient);
                patient.transform.position += new Vector3(0, 0.98f - bounds.min.y, 0);
                new GameObject("AnatomyRoot_Unbound").transform.SetParent(patientRoot.transform);
                var camera = new GameObject("PreviewCamera").AddComponent<Camera>();
                camera.transform.position = new Vector3(-2.6f, 2.4f, -3.0f);
                camera.transform.LookAt(new Vector3(0, 1.1f, 0));
                camera.fieldOfView = 60; camera.nearClipPlane = 0.05f; camera.farClipPlane = 30;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.12f, 0.15f, 0.19f);
                var light = new GameObject("PreviewLight").AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.2f;
                light.transform.rotation = Quaternion.Euler(45, -25, 0);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.6f, 0.65f, 0.7f);
                EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), ScenePath);
                AssetDatabase.SaveAssets();
                Debug.Log("SCALPAL_ENVIRONMENT_PREVIEW_BUILT: static room and reclining patient; no XR or surgery behavior.");
                Validate();
            }
            finally { if (previous.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previous); }
        }

        static GameObject CreateModel(string name)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Models/" + name + ".fbx");
            if (!asset) throw new InvalidOperationException("Missing model: " + name);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(asset);
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                renderer.sharedMaterials = renderer.sharedMaterials.Select(GetMaterial).ToArray();
            return instance;
        }

        static Material GetMaterial(Material source)
        {
            if (!source) throw new InvalidOperationException("Imported material missing.");
            string name = source.name;
            string path = Root + "Materials/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (!shader) throw new InvalidOperationException("No compatible preview shader found.");
            if (!material) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            material.shader = shader; material.color = source.color;
            float metallic = name == "steel" ? 0.85f : name == "glass" ? 0.4f : 0;
            float smoothness = name == "steel" ? 0.66f : name == "glass" ? 0.93f : 0.25f;
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        static Bounds GetBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException("No model geometry.");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        static int Triangles(GameObject root) => root.GetComponentsInChildren<MeshFilter>(true)
            .Sum(filter => filter.sharedMesh ? filter.sharedMesh.triangles.Length / 3 : 0);

        public static void Validate()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                var room = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/OperatingTheatre.prefab");
                var patient = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Prefabs/SupinePatient.prefab");
                if (!room || !patient) throw new InvalidOperationException("Preview prefabs missing.");
                int roomTriangles = Triangles(room), patientTriangles = Triangles(patient);
                int roomRenderers = room.GetComponentsInChildren<Renderer>(true).Length;
                if (roomTriangles != 12168 || roomRenderers != 8 || patientTriangles != 1578)
                    throw new InvalidOperationException("Prepared source geometry changed; inspect the imports.");
                var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                var body = roots.Single(o => o.name == "PatientRoot").GetComponentsInChildren<Renderer>(true).Single();
                if (Mathf.Abs(body.bounds.size.z - 1.75f) > 0.02f || body.bounds.size.y > 0.6f || Mathf.Abs(body.bounds.min.y - 0.98f) > 0.005f)
                    throw new InvalidOperationException("Patient must be metric, reclining and on the authored table height.");
                foreach (var root in roots)
                {
                    if (root.GetComponentsInChildren<Component>(true).Any(component => component == null))
                        throw new InvalidOperationException("Preview has a missing script.");
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                        if (renderer.sharedMaterials.Any(material => !material || !material.shader))
                            throw new InvalidOperationException("Preview has a missing material or shader.");
                    foreach (var mesh in root.GetComponentsInChildren<MeshFilter>(true))
                        if (!mesh.sharedMesh) throw new InvalidOperationException("Preview has a missing mesh.");
                }
                Debug.Log($"SCALPAL_ENVIRONMENT_VALIDATION_OK: room {roomTriangles} triangles / {roomRenderers} renderers, patient {patientTriangles} triangles, 1.75m reclining fit. Editor only; no headset validation.");
            }
            finally { if (previous.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previous); }
        }
    }
}
