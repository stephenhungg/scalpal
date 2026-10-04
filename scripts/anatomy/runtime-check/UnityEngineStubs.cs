// Small in-memory test doubles, not evidence of Unity shader output or Quest performance.
using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class DisallowMultipleComponent : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class SerializeField : Attribute { }
    [AttributeUsage(AttributeTargets.Field)] public sealed class TooltipAttribute : Attribute { public TooltipAttribute(string _) { } }
    public class Object { public string name; public static void DestroyImmediate(Object value) { } public static implicit operator bool(Object value) => value != null; }
    public class GameObject : Object
    {
        public int layer;
        public bool activeSelf = true;
        public bool activeInHierarchy => activeSelf && (parent == null || parent.activeInHierarchy);
        public GameObject parent;
        public readonly List<GameObject> children = new List<GameObject>();
        public readonly List<Component> components = new List<Component>();
        public readonly Transform transform;
        public GameObject() { transform = AddComponent<Transform>(); }
        public GameObject(string name) : this() { this.name = name; }
        public T GetComponent<T>() where T : Component => components.OfType<T>().FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component => transform.GetComponentsInChildren<T>(includeInactive);
        public T AddComponent<T>() where T : Component, new()
        {
            var component = new T { gameObject = this };
            components.Add(component);
            return component;
        }
        public GameObject Child()
        {
            var child = new GameObject { parent = this };
            children.Add(child);
            return child;
        }
        public IEnumerable<GameObject> Descendants()
        {
            yield return this;
            foreach (var child in children)
                foreach (var descendant in child.Descendants()) yield return descendant;
        }
    }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform => gameObject.transform;
        public T GetComponent<T>() where T : Component => gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T : Component => gameObject.Descendants()
            .Where(g => includeInactive || g.activeInHierarchy).SelectMany(g => g.components.OfType<T>()).ToArray();
        public T GetComponentInParent<T>(bool includeInactive) where T : Component
        {
            for (var current = gameObject; current != null; current = current.parent)
                if (includeInactive || current.activeInHierarchy)
                {
                    var match = current.components.OfType<T>().FirstOrDefault();
                    if (match != null) return match;
                }
            return null;
        }
    }
    public class MonoBehaviour : Component
    {
        public bool enabled = true;
        public bool isActiveAndEnabled => enabled && gameObject.activeInHierarchy;
    }
    public class Transform : Component
    {
        public Vector3 position;
        public Quaternion rotation;
        public void LookAt(Vector3 target) { }
        public float rotationDegrees;
        public void Rotate(Vector3 axis, float degrees, Space space) { rotationDegrees += degrees; }
    }
    public enum Space { Self }
    public struct Vector3 {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float magnitude => (float)Math.Sqrt(x*x+y*y+z*z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 up => new Vector3();
    }
    public struct Quaternion { public static Quaternion Euler(float x, float y, float z) => default; }
    public static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
    public struct Bounds { public Vector3 center, extents, size; public void Encapsulate(Bounds other) { } }
    public class Camera : Component { public bool orthographic; public float orthographicSize, nearClipPlane, farClipPlane; public CameraClearFlags clearFlags; public Color backgroundColor; }
    public enum CameraClearFlags { SolidColor }
    public class Light : Component { public LightType type; public float intensity; }
    public enum LightType { Directional }
    public struct Color : IEquatable<Color>
    {
        public float r, g, b, a;
        public static Color black => new Color(0, 0, 0, 1);
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public bool Equals(Color other) => r == other.r && g == other.g && b == other.b && a == other.a;
    }
    public static class Time { public static float deltaTime = 0.5f; }
    public static class Debug { public static void LogWarning(string message, Object context = null) { Console.WriteLine(message); } public static void Log(string message) { Console.WriteLine(message); } public static void LogError(string message) { Console.WriteLine(message); } }
    public class Shader : Object
    {
        public static Shader Find(string name) => new Shader();
        static readonly Dictionary<string, int> ids = new Dictionary<string, int>();
        public static int PropertyToID(string name)
        {
            if (!ids.ContainsKey(name)) ids[name] = ids.Count + 1;
            return ids[name];
        }
    }
    public class Material : Object
    {
        public Material() { }
        public Material(Shader shader) { this.shader = shader; }
        public Shader shader;
        public int renderQueue;
        public readonly Dictionary<string, Color> colors = new Dictionary<string, Color>();
        public readonly Dictionary<string, float> floats = new Dictionary<string, float>();
        public readonly Dictionary<string, string> tags = new Dictionary<string, string>();
        public readonly Dictionary<string, bool> passes = new Dictionary<string, bool>();
        public readonly HashSet<string> keywords = new HashSet<string>();
        public void SetColor(string key, Color value) { properties.Add(Shader.PropertyToID(key)); colors[key] = value; }
        public void SetFloat(string key, float value) { floats[key] = value; }
        public void SetOverrideTag(string tag, string value) { tags[tag] = value; }
        public void SetShaderPassEnabled(string pass, bool enabled) { passes[pass] = enabled; }
        public void EnableKeyword(string key) { keywords.Add(key); if (key == "_EMISSION") emission = true; }
        public void DisableKeyword(string key) { keywords.Remove(key); if (key == "_EMISSION") emission = false; }
        public readonly HashSet<int> properties = new HashSet<int>();
        public bool emission;
        public bool HasProperty(int property) => properties.Contains(property);
        public bool IsKeywordEnabled(string keyword) => keyword == "_EMISSION" ? emission : keywords.Contains(keyword);
    }
    public class MaterialPropertyBlock
    {
        readonly Dictionary<int, Color> colors = new Dictionary<int, Color>();
        public bool isEmpty => colors.Count == 0;
        public void SetColor(int property, Color color) { colors[property] = color; }
        public Color GetColor(int property) => colors.TryGetValue(property, out var color) ? color : default;
        public void CopyFrom(MaterialPropertyBlock source)
        {
            colors.Clear();
            if (source != null) foreach (var entry in source.colors) colors.Add(entry.Key, entry.Value);
        }
    }
    public class Renderer : Component
    {
        public Bounds bounds;
        public bool enabled = true;
        public Material[] sharedMaterials = Array.Empty<Material>();
        readonly Dictionary<int, MaterialPropertyBlock> blocks = new Dictionary<int, MaterialPropertyBlock>();
        public void GetPropertyBlock(MaterialPropertyBlock block, int index = -1)
        {
            blocks.TryGetValue(index, out var stored);
            block.CopyFrom(stored);
        }
        public void SetPropertyBlock(MaterialPropertyBlock block, int index = -1)
        {
            if (block == null) { blocks.Remove(index); return; }
            var copy = new MaterialPropertyBlock();
            copy.CopyFrom(block);
            blocks[index] = copy;
        }
    }
    public class Collider : Component { public bool enabled = true; }
    public class Mesh : Object { }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class MeshCollider : Collider { public Mesh sharedMesh; public bool convex; public bool isTrigger; }
    public class TextAsset : Object { public string text; }
    public static class LayerMask { public static int NameToLayer(string name) => -1; }
    public static class JsonUtility { public static T FromJson<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }); }
}

namespace UnityEngine.Rendering { public enum BlendMode { Zero = 0, One = 1, SrcAlpha = 5, OneMinusSrcAlpha = 10 } }
namespace UnityEngine.SceneManagement
{
    public struct Scene { public bool isLoaded => false; public bool IsValid() => false; }
    public static class SceneManager {
        public static Scene GetActiveScene() => default;
        public static bool SetActiveScene(Scene scene) => false;
        public static void MoveGameObjectToScene(UnityEngine.GameObject obj, Scene scene) { }
    }
}
