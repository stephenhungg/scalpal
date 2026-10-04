// Minimal signatures of the UnityEngine APIs the exercise scripts use, matching Unity 6's public API,
// so the scripts compile under plain .NET. Behavior is not emulated; Program.cs only exercises pure C#.
using System;
using System.Collections;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; }
        public static T Instantiate<T>(T original) where T : Object => original;
        public static void Destroy(Object obj) { }
    }

    public class Component : Object
    {
        public Transform transform => null;
        public GameObject gameObject => null;
        public T GetComponent<T>() => default;
        public T GetComponentInParent<T>() => default;
        public T GetComponentInParent<T>(bool includeInactive) => default;
        public T[] GetComponentsInChildren<T>(bool includeInactive) => new T[0];
    }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { }
        public Transform transform => null;
        public bool activeInHierarchy => true;
        public void SetActive(bool value) { }
        public bool activeSelf => true;
        public T GetComponent<T>() => default;
        public static GameObject CreatePrimitive(PrimitiveType type) => new GameObject();
        public T AddComponent<T>() where T : Component => default;
    }

    public enum Space { World, Self }

    public class Transform : Component
    {
        public Vector3 position { get; set; }
        public Vector3 localScale { get; set; }
        public Vector3 InverseTransformPoint(Vector3 position) => position;
        public void Rotate(Vector3 axis, float angle, Space relativeTo) { }
        public Vector3 localPosition { get; set; }
        public Transform parent => null;
        public void SetParent(Transform parent, bool worldPositionStays) { }
    }

    public class Collider : Component
    {
        public bool enabled { get; set; }
    }

    public class SphereCollider : Collider
    {
        public bool isTrigger { get; set; }
        public float radius { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class DisallowMultipleComponent : Attribute { }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color Lerp(Color a, Color b, float t) => new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
    }

    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public class Shader : Object
    {
        public static int PropertyToID(string name) => name.GetHashCode();
    }

    public class Material : Object
    {
        public bool HasProperty(int nameID) => false;
        public bool IsKeywordEnabled(string keyword) => false;
    }

    public sealed class MaterialPropertyBlock
    {
        public bool isEmpty => true;
        public void SetColor(int nameID, Color value) { }
    }

    public static class Time
    {
        public static float deltaTime => 0f;
        public static double realtimeSinceStartupAsDouble => 0.0;
    }

    public struct Bounds
    {
        public Vector3 center => default;
    }

    public class Renderer : Component
    {
        public Bounds bounds => default;
        public bool enabled { get; set; }
        public Material[] sharedMaterials { get; set; }
        public void GetPropertyBlock(MaterialPropertyBlock properties) { }
        public void GetPropertyBlock(MaterialPropertyBlock properties, int materialIndex) { }
        public void SetPropertyBlock(MaterialPropertyBlock properties, int materialIndex) { }
        public void SetPropertyBlock(MaterialPropertyBlock properties) { }
        public Material sharedMaterial { get; set; }
    }

    public class Mesh : Object
    {
        public int[] triangles => new int[0];
    }

    public class MeshFilter : Component
    {
        public Mesh sharedMesh => null;
    }


    public class Behaviour : Component
    {
        public bool enabled { get; set; }
        public bool isActiveAndEnabled => true;
    }

    public class Coroutine { }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => new Coroutine();
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SerializeField : Attribute { }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class TooltipAttribute : Attribute
    {
        public TooltipAttribute(string tooltip) { }
    }

    public class TextAsset : Object
    {
        public string text => "";
    }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
    }

    public static class JsonUtility
    {
        public static T FromJson<T>(string json) => default;
        public static string ToJson(object obj) => "";
        public static string ToJson(object obj, bool prettyPrint) => "";
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogWarning(object message, Object context) { }
        public static void LogError(object message) { }
        public static void LogException(Exception exception, Object context) { }
    }

    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Sign(float f) => f >= 0f ? 1f : -1f;
        public static float Min(float a, float b) => Math.Min(a, b);
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public static float Clamp(float v, float min, float max) => Math.Max(min, Math.Min(max, v));
        public static float Tan(float f) => (float)Math.Tan(f);
        public static float Sqrt(float f) => (float)Math.Sqrt(f);
        public static float Abs(float f) => Math.Abs(f);
        public static float InverseLerp(float a, float b, float v) => a == b ? 0f : Clamp((v - a) / (b - a), 0f, 1f);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(float d, Vector3 a) => a * d;
        public static float Angle(Vector3 from, Vector3 to) => 0f;
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
    }

    public sealed class WaitForSeconds : YieldInstruction
    {
        public WaitForSeconds(float seconds) { }
    }

    public class AsyncOperation : YieldInstruction { }

    public class YieldInstruction { }
}

namespace UnityEngine.Networking
{
    public class UnityWebRequestAsyncOperation : AsyncOperation { }

    public class DownloadHandler : IDisposable
    {
        public string text => "";
        public void Dispose() { }
    }

    public sealed class DownloadHandlerBuffer : DownloadHandler { }

    public class UploadHandler : IDisposable
    {
        public void Dispose() { }
    }

    public sealed class UploadHandlerRaw : UploadHandler
    {
        public UploadHandlerRaw(byte[] data) { }
    }

    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }

        public UnityWebRequest(string url, string method) { }
        public DownloadHandler downloadHandler { get; set; }
        public UploadHandler uploadHandler { get; set; }
        public int timeout { get; set; }
        public Result result => Result.Success;
        public long responseCode => 200;
        public void SetRequestHeader(string name, string value) { }
        public UnityWebRequestAsyncOperation SendWebRequest() => new UnityWebRequestAsyncOperation();
        public void Dispose() { }
    }
}

namespace UnityEditor
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class MenuItem : System.Attribute
    {
        public MenuItem(string itemName) { }
    }

    public static class Selection
    {
        public static UnityEngine.GameObject activeGameObject => null;
    }
}

// Stephen's instrument runtime (Assets/Scalpal/Instruments, its own asmdef) needs XR input and physics
// stubs to compile here, so only the members the Experience layer reads are mirrored. Keep in sync with
// Instruments/Runtime/InstrumentBehaviour.cs.
namespace Scalpal.Instruments
{
    public sealed class InstrumentBehaviour : UnityEngine.MonoBehaviour
    {
        public string instrumentId;
        public bool Held { get; private set; }
        public bool TrackingValid { get; private set; }
        public float Activation { get; private set; }
    }
}
