using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace UnityEngine
{
    public class Coroutine { }
    public class MonoBehaviour
    {
        public bool isActiveAndEnabled = true;
        public Coroutine StartCoroutine(IEnumerator routine) { Scheduler.Add(routine); return new Coroutine(); }
    }
    public class SerializeField : Attribute { }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string _) { } }
    public class WaitForSeconds { public WaitForSeconds(float _) { } }
    public static class Debug { public static void LogWarning(object _) { } }
    public static class JsonUtility
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 10000000 };
        public static string ToJson(object value) { return Serializer.Serialize(value); }
        public static T FromJson<T>(string value)
        {
            try { return Serializer.Deserialize<T>(value); }
            catch (InvalidOperationException error) { throw new ArgumentException("Invalid JSON", error); }
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
// Scheduling/response timing are doubles; relay decisions and local case engine are production source.
public static class Scheduler
{
    sealed class Work
    {
        readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
        object wait;
        public Work(IEnumerator routine) { stack.Push(routine); }
        public bool Step(bool clocks)
        {
            var request = wait as UnityEngine.Networking.UnityWebRequest;
            if (request != null && request.result == UnityEngine.Networking.UnityWebRequest.Result.InProgress) return true;
            if (wait is UnityEngine.WaitForSeconds && !clocks) return true;
            wait = null;
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext()) { stack.Pop(); continue; }
                var inner = top.Current as IEnumerator;
                if (inner != null) { stack.Push(inner); continue; }
                wait = top.Current;
                return true;
            }
            return false;
        }
    }
    static readonly List<Work> work = new List<Work>();
    public static readonly List<UnityEngine.Networking.UnityWebRequest> Requests = new List<UnityEngine.Networking.UnityWebRequest>();
    public static void Add(IEnumerator routine) { work.Add(new Work(routine)); }
    public static void Pump(bool clocks = false)
    {
        foreach (var item in work.ToArray()) if (!item.Step(clocks)) work.Remove(item);
    }
    public static void Reset() { work.Clear(); Requests.Clear(); }
}
