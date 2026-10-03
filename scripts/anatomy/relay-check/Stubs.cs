using System.Collections;
using System.Text.Json;

namespace UnityEngine
{
    public class Coroutine { }
    public class MonoBehaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) { Scheduler.Add(routine); return new Coroutine(); }
    }
    public class SerializeField : Attribute { }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string _) { } }
    public class WaitForSeconds { public WaitForSeconds(float _) { } }
    public static class Debug { public static void LogWarning(object _) { } }
    public static class JsonUtility
    {
        static readonly JsonSerializerOptions Options = new() { IncludeFields = true };
        public static string ToJson(object value) => JsonSerializer.Serialize(value, Options);
        public static T FromJson<T>(string value)
        {
            try { return JsonSerializer.Deserialize<T>(value, Options); }
            catch (JsonException e) { throw new ArgumentException("Invalid JSON", e); }
        }
    }
}
namespace UnityEngine.Networking
{
    public class DownloadHandlerBuffer { public string text = ""; }
    public class UploadHandlerRaw { public byte[] data; public UploadHandlerRaw(byte[] bytes) { data = bytes; } }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError }
        public readonly string url, method;
        public DownloadHandlerBuffer downloadHandler;
        public UploadHandlerRaw uploadHandler;
        public int timeout;
        public long responseCode;
        public Result result = Result.InProgress;
        public UnityWebRequest(string url, string method) { this.url = url; this.method = method; }
        public void SetRequestHeader(string _, string __) { }
        public object SendWebRequest() { Scheduler.Requests.Add(this); return this; }
        public void Complete(string json, bool success = true)
        {
            downloadHandler.text = json;
            result = success ? Result.Success : Result.ConnectionError;
            responseCode = success ? 200 : 0;
        }
        public void Dispose() { }
    }
}
// Controlled coroutine/network doubles, no sockets or Unity runtime.
public static class Scheduler
{
    sealed class Task
    {
        public Stack<IEnumerator> stack = new();
        public object wait;
        public Task(IEnumerator routine) { stack.Push(routine); }
        public bool Step(bool clocks)
        {
            if (wait is UnityEngine.Networking.UnityWebRequest r && r.result == UnityEngine.Networking.UnityWebRequest.Result.InProgress) return true;
            if (wait is UnityEngine.WaitForSeconds && !clocks) return true;
            wait = null;
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                if (top.Current is IEnumerator inner) { stack.Push(inner); continue; }
                wait = top.Current;
                return true;
            }
            return false;
        }
    }
    static readonly List<Task> tasks = new();
    public static readonly List<UnityEngine.Networking.UnityWebRequest> Requests = new();
    public static void Add(IEnumerator routine) { tasks.Add(new Task(routine)); }
    public static void Pump(bool clocks = false)
    {
        foreach (var task in tasks.ToArray()) if (!task.Step(clocks)) tasks.Remove(task);
    }
    public static void Reset() { tasks.Clear(); Requests.Clear(); }
}
