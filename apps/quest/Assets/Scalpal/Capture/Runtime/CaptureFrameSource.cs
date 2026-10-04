using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Meta.XR;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Capture
{
    // One raw camera image read back from the GPU texture, with the metadata of
    // that exact image (sensor timestamp and lens pose), never a rendered frame.
    public sealed class CaptureFrame
    {
        public byte[] rgba;
        public int width, height;
        public long sensorTimeUs;
        public double receivedRealtime;
        public Pose cameraPose;
        public bool poseValid;
        public Action<byte[]> recycle; // thread-safe return of rgba to its source pool
    }

    public interface ICaptureFrameSource : IDisposable
    {
        string FrameSource { get; }   // manifest frameSource
        string Provenance { get; }    // learner for the headset camera; sample for synthetic fixtures
        string Clock { get; }         // name of the sensorTimeUs clock
        bool Start(out string reason);
        void Pump(double now);
        bool TryDequeue(out CaptureFrame frame);
        void Stop();
        CaptureCameraRecord Describe();
    }

    // Meta Passthrough Camera API (MRUK PassthroughCameraAccess), the same path
    // NativeBodyRegistration uses. Registration owns the LEFT camera at 640x480;
    // this source opens the RIGHT camera so neither component toggles the other.
    public sealed class PassthroughCaptureSource : ICaptureFrameSource
    {
        public const string MonotonicClock = "pca_sensor_monotonic_us", UnixClock = "pca_timestamp_unix_us";
        static readonly FieldInfo MonotonicField = typeof(PassthroughCameraAccess).GetField("_timestampNsMonotonic", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly GameObject host;
        readonly double interval;
        readonly Queue<CaptureFrame> ready = new Queue<CaptureFrame>();
        readonly ConcurrentBag<byte[]> pool = new ConcurrentBag<byte[]>();
        PassthroughCameraAccess camera;
        DateTime lastTimestamp;
        double nextAt;
        int pending;
        bool stopped;
        public string FrameSource => "meta_passthrough_camera_access";
        public string Provenance => "learner";
        public string Clock => MonotonicField != null ? MonotonicClock : UnixClock;

        public PassthroughCaptureSource(GameObject host, double targetFps) { this.host = host; interval = 1.0 / Math.Max(1, targetFps); }

        public bool Start(out string reason)
        {
            reason = "";
            if (!SystemInfo.supportsAsyncGPUReadback) { reason = "Capture unavailable: asynchronous camera readback is unsupported on this device."; return false; }
            bool supported;
            try { supported = PassthroughCameraAccess.IsSupported; } catch (Exception e) { supported = false; Debug.LogWarning("SCALPAL_CAPTURE support probe failed: " + e.Message); }
            if (!supported) { reason = "Capture unavailable: the Passthrough Camera API needs a Quest 3/3S on Horizon OS v74+."; return false; }
#if UNITY_ANDROID && !UNITY_EDITOR
            // Permission requests belong to Theatre setup; capture never prompts mid-case.
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission))
            { reason = "Capture unavailable: headset camera permission was not granted in Theatre setup."; return false; }
#endif
            camera = host.AddComponent<PassthroughCameraAccess>();
            camera.enabled = false;
            camera.CameraPosition = PassthroughCameraAccess.CameraPositionType.Right;
            camera.RequestedResolution = new Vector2Int(640, 480);
            camera.MaxFramerate = 30;
            camera.enabled = true;
            return true;
        }

        public void Pump(double now)
        {
            if (stopped || !camera || !camera.enabled || !camera.IsPlaying || pending >= 2 || now < nextAt) return;
            if (camera.Timestamp == default || camera.Timestamp == lastTimestamp) return;
            var texture = camera.GetTexture();
            if (!texture) return;
            lastTimestamp = camera.Timestamp;
            nextAt = now + interval;
            // Capture this image's metadata before the asynchronous readback completes.
            long sensorUs = MonotonicField != null ? (long)MonotonicField.GetValue(camera) / 1000 : (camera.Timestamp - DateTime.UnixEpoch).Ticks / 10;
            Pose pose = camera.GetCameraPose();
            bool poseValid = pose.rotation != default && !(pose.position == Vector3.zero && pose.rotation == Quaternion.identity);
            int width = texture.width, height = texture.height;
            pending++;
            AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, request =>
            {
                pending--;
                if (stopped || request.hasError) return;
                var data = request.GetData<byte>();
                if (!pool.TryTake(out var rgba) || rgba.Length != data.Length) rgba = new byte[data.Length];
                data.CopyTo(rgba);
                ready.Enqueue(new CaptureFrame { rgba = rgba, width = width, height = height, sensorTimeUs = sensorUs,
                    receivedRealtime = Time.realtimeSinceStartupAsDouble, cameraPose = pose, poseValid = poseValid, recycle = Recycle });
            });
        }

        public bool TryDequeue(out CaptureFrame frame)
        {
            frame = ready.Count > 0 ? ready.Dequeue() : null;
            return frame != null;
        }

        void Recycle(byte[] buffer) { if (buffer != null && pool.Count < 4) pool.Add(buffer); }

        public CaptureCameraRecord Describe()
        {
            var record = new CaptureCameraRecord { api = "Meta.XR.PassthroughCameraAccess (MRUK)", position = "right" };
            if (!camera || !camera.IsPlaying) return record;
            var k = camera.Intrinsics;
            record.width = camera.CurrentResolution.x; record.height = camera.CurrentResolution.y;
            record.sensorWidth = k.SensorResolution.x; record.sensorHeight = k.SensorResolution.y;
            record.fx = k.FocalLength.x; record.fy = k.FocalLength.y; record.cx = k.PrincipalPoint.x; record.cy = k.PrincipalPoint.y;
            record.lensOffsetPosition = new[] { k.LensOffset.position.x, k.LensOffset.position.y, k.LensOffset.position.z };
            record.lensOffsetRotation = new[] { k.LensOffset.rotation.x, k.LensOffset.rotation.y, k.LensOffset.rotation.z, k.LensOffset.rotation.w };
            return record;
        }

        public void Stop()
        {
            stopped = true; ready.Clear();
            if (camera) { camera.enabled = false; UnityEngine.Object.Destroy(camera); camera = null; }
        }
        public void Dispose() => Stop();
    }
}
