// Signature-only editor doubles. The harness does not import FBX or save Unity prefabs.
using System;
using UnityEngine;

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute { public MenuItem(string path) { } }
    public class SceneAsset : UnityEngine.Object { }
    public static class Selection { public static UnityEngine.Object activeObject; }
    public static class EditorGUIUtility { public static void PingObject(UnityEngine.Object value) { } }
    public static class EditorUtility { public static void SetDirty(UnityEngine.Object value) { } }
    public enum PrefabUnpackMode { Completely }
    public enum InteractionMode { AutomatedAction }
    public enum ModelImporterAnimationType { None }
    public enum ModelImporterMaterialImportMode { None }
    public class AssetImporter : UnityEngine.Object
    {
        public static AssetImporter GetAtPath(string path) => null;
    }
    public class ModelImporter : AssetImporter
    {
        public bool bakeAxisConversion, useFileScale, importAnimation;
        public float globalScale;
        public ModelImporterAnimationType animationType;
        public ModelImporterMaterialImportMode materialImportMode;
        public void SaveAndReimport() { }
    }
    public static class AssetDatabase
    {
        public static T LoadAssetAtPath<T>(string path) where T : UnityEngine.Object => null;
        public static bool IsValidFolder(string path) => true;
        public static string CreateFolder(string parent, string name) => "";
        public static void CreateAsset(UnityEngine.Object value, string path) { }
        public static void SaveAssets() { }
    }
    public static class PrefabUtility
    {
        public static UnityEngine.Object InstantiatePrefab(UnityEngine.Object value, Transform parent) => null;
        public static UnityEngine.Object InstantiatePrefab(UnityEngine.Object value, UnityEngine.SceneManagement.Scene scene) => null;
        public static void UnpackPrefabInstance(GameObject value, PrefabUnpackMode mode, InteractionMode action) { }
        public static GameObject SaveAsPrefabAsset(GameObject value, string path, out bool success) { success = false; return null; }
    }
}

namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup { EmptyScene }
    public enum NewSceneMode { Additive }
    public static class EditorSceneManager {
        public static UnityEngine.SceneManagement.Scene NewScene(NewSceneSetup setup, NewSceneMode mode) => default;
        public static bool SaveScene(UnityEngine.SceneManagement.Scene scene, string path) => false;
        public static bool CloseScene(UnityEngine.SceneManagement.Scene scene, bool removeScene) => false;
    }
}
