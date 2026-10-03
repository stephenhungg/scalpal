// Minimal signatures of the UnityEngine APIs the exercise scripts use, matching Unity 6's public API,
// so the scripts compile under plain .NET. Behavior is not emulated; Program.cs only exercises pure C#.
using System;
using System.Collections;

namespace UnityEngine
{
    public class Object { }

    public class Component : Object { }

    public class Behaviour : Component { }

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
    }

    public static class Debug
    {
        public static void LogWarning(object message) { }
    }

    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 operator *(Vector3 a, float d) => new Vector3(a.x * d, a.y * d, a.z * d);
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
