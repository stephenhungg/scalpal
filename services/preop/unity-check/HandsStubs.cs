// Signature-only stand-ins for the APIs the hand module uses, mirrored from Meta's Passthrough Camera API
// samples (MRUK 85) and Unity's inference-engine BlazeDetectionSample (Inference Engine 2.2/2.4), plus the
// extra UnityEngine/UnityEditor members it touches. They let `npm run test:unity` compile
// Assets/Scalpal/Hands with SCALPAL_HANDS defined. Behavior is not emulated.
using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityEngine
{
    public struct Quaternion
    {
        public float x, y, z, w;
    }

    public struct Pose
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    public struct Ray
    {
        public Vector3 origin, direction;
        public Vector3 GetPoint(float distance) => origin + direction * distance;
    }

    public struct Vector4
    {
        public float x, y, z, w;
        public Vector4(float x, float y) { this.x = x; this.y = y; z = 0; w = 0; }
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Vector4 zero => default;
    }

    public struct Matrix4x4
    {
        public Matrix4x4(Vector4 c0, Vector4 c1, Vector4 c2, Vector4 c3) { }
    }

    public class Texture : Object
    {
        public int width => 0;
        public int height => 0;
    }

    public class ComputeBuffer { }

    public class ComputeShader : Object
    {
        public int FindKernel(string name) => 0;
        public void SetTexture(int kernelIndex, int nameID, Texture texture) { }
        public void SetBuffer(int kernelIndex, int nameID, ComputeBuffer buffer) { }
        public void SetInt(int nameID, int value) { }
        public void SetMatrix(int nameID, Matrix4x4 value) { }
        public void Dispatch(int kernelIndex, int x, int y, int z) { }
    }

    public class LineRenderer : Renderer
    {
        public int positionCount { get; set; }
        public bool useWorldSpace { get; set; }
        public float startWidth { get; set; }
        public float endWidth { get; set; }
        public Color startColor { get; set; }
        public Color endColor { get; set; }
        public void SetPosition(int index, Vector3 position) { }
    }

    public enum PrimitiveType { Sphere, Capsule, Cylinder, Cube, Plane, Quad }

    public enum TextureFormat { RGBA32 }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class RangeAttribute : Attribute
    {
        public RangeAttribute(float min, float max) { }
    }

    public static class Application
    {
        public static string persistentDataPath => "";
        public static string version => "";
    }

    public static class SystemInfo
    {
        public static string deviceModel => "";
    }

    public static class ImageConversion
    {
        public static Unity.Collections.NativeArray<byte> EncodeNativeArrayToJPG<T>(Unity.Collections.NativeArray<T> input, Experimental.Rendering.GraphicsFormat format, uint width, uint height, uint rowBytes = 0, int quality = 75) where T : struct => default;
    }

    [AsyncMethodBuilder(typeof(AwaitableBuilder))]
    public class Awaitable
    {
        public static Awaitable NextFrameAsync() => new Awaitable();
        public Awaiter GetAwaiter() => new Awaiter();

        public struct Awaiter : INotifyCompletion
        {
            public bool IsCompleted => true;
            public void GetResult() { }
            public void OnCompleted(Action continuation) => continuation();
        }
    }

    [AsyncMethodBuilder(typeof(AwaitableBuilder<>))]
    public class Awaitable<T>
    {
        public Awaiter GetAwaiter() => new Awaiter();

        public struct Awaiter : INotifyCompletion
        {
            public bool IsCompleted => true;
            public T GetResult() => default;
            public void OnCompleted(Action continuation) => continuation();
        }
    }
}

namespace UnityEngine
{
    // Unity's Awaitable is task-like; these builders make async methods returning it compile.
    public struct AwaitableBuilder
    {
        public static AwaitableBuilder Create() => default;
        public Awaitable Task => new Awaitable();
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult() { }
        public void SetException(Exception exception) { }
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
    }

    public struct AwaitableBuilder<T>
    {
        public static AwaitableBuilder<T> Create() => default;
        public Awaitable<T> Task => new Awaitable<T>();
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine => stateMachine.MoveNext();
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult(T result) { }
        public void SetException(Exception exception) { }
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine) where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
    }
}

namespace UnityEngine.Experimental.Rendering
{
    public enum GraphicsFormat { R8G8B8A8_SRGB, R8G8B8A8_UNorm }
}

namespace UnityEngine.Rendering
{
    public struct AsyncGPUReadbackRequest
    {
        public bool hasError => false;
        public Unity.Collections.NativeArray<T> GetData<T>() where T : struct => default;
    }

    public static class AsyncGPUReadback
    {
        public static AsyncGPUReadbackRequest Request(Texture src, int mipIndex, TextureFormat dstFormat, Action<AsyncGPUReadbackRequest> callback) => default;
    }
}

namespace Unity.Collections
{
    public enum Allocator { Temp, TempJob, Persistent }

    public struct NativeArray<T> : IDisposable where T : struct
    {
        public NativeArray(NativeArray<T> array, Allocator allocator) { }
        public T[] ToArray() => new T[0];
        public void Dispose() { }
    }
}

namespace Meta.XR
{
    public class PassthroughCameraAccess : MonoBehaviour
    {
        public bool IsPlaying => false;
        public Pose GetCameraPose() => default;
        public Texture GetTexture() => null;
        public Ray ViewportPointToRay(Vector2 viewportPoint, Pose cameraPose) => default;
    }
}

namespace Unity.InferenceEngine
{
    public class ModelAsset : UnityEngine.Object { }

    public class Model { }

    public static class ModelLoader
    {
        public static Model Load(ModelAsset asset) => new Model();
    }

    public enum BackendType { GPUCompute, CPU, GPUPixel }

    public class TensorShape
    {
        public TensorShape(params int[] dims) { }
        public int this[int axis] => 0;
    }

    public abstract class Tensor : IDisposable
    {
        public TensorShape shape => new TensorShape();
        public void Dispose() { }
    }

    public class Tensor<T> : Tensor where T : unmanaged
    {
        public Tensor(TensorShape shape) { }
        public T this[int index] => default;
        public Awaitable<Tensor<T>> ReadbackAndCloneAsync() => new Awaitable<Tensor<T>>();
    }

    public class Worker : IDisposable
    {
        public Worker(Model model, BackendType backendType) { }
        public void Schedule(params Tensor[] inputs) { }
        public Tensor PeekOutput(string name) => null;
        public Tensor PeekOutput(int index) => null;
        public void Dispose() { }
    }

    public class ComputeTensorData
    {
        public ComputeBuffer buffer => null;
        public static ComputeTensorData Pin(Tensor tensor, bool clearOnInit = true) => new ComputeTensorData();
    }
}

namespace UnityEditor
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class InitializeOnLoadAttribute : Attribute { }

    public static class EditorApplication
    {
        public delegate void CallbackFunction();
        public static CallbackFunction delayCall;
    }

    public static class PlayerSettings
    {
        public static string GetScriptingDefineSymbols(Build.NamedBuildTarget target) => "";
        public static void SetScriptingDefineSymbols(Build.NamedBuildTarget target, string defines) { }
    }
}

namespace UnityEditor.Build
{
    public struct NamedBuildTarget
    {
        public static NamedBuildTarget Android => default;
        public static NamedBuildTarget Standalone => default;
    }
}

namespace UnityEditor.PackageManager
{
    public class PackageInfo
    {
        public string name => "";
        public static PackageInfo[] GetAllRegisteredPackages() => new PackageInfo[0];
    }

    public class PackageRegistrationEventArgs { }

    public static class Events
    {
        public static event Action<PackageRegistrationEventArgs> registeredPackages { add { } remove { } }
    }
}
