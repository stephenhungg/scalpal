using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Instruments
{
    // A virtual scene camera only. It does not expose a physical endoscope or raw passthrough stream.
    [RequireComponent(typeof(InstrumentBehaviour))]
    public sealed class LaparoscopeView : MonoBehaviour
    {
        public Transform opticDirection;
        public Renderer monitor;
        public LayerMask visibleLayers = ~0;
        [Range(128, 1024)] public int resolution = 512;
        [Range(1, 30)] public float framesPerSecond = 15;
        public RenderTexture ViewTexture { get; private set; }
        Camera viewCamera;
        InstrumentBehaviour instrument;
        Material monitorMaterial, originalMonitorMaterial;
        double nextFrame;

        void LateUpdate()
        {
            if (viewCamera != null) viewCamera.enabled = false;
            if (instrument == null) instrument = GetComponent<InstrumentBehaviour>();
            if (!instrument.Held || !instrument.TrackingValid || instrument.Activation < 0.7f) return;
            if (Time.realtimeSinceStartupAsDouble < nextFrame) return;
            if (!InitializeView()) return;
            nextFrame = Time.realtimeSinceStartupAsDouble + 1 / Mathf.Max(1, framesPerSecond);
            // SRP cameras render through the configured pipeline rather than Camera.Render's built-in path.
            if (GraphicsSettings.currentRenderPipeline != null) viewCamera.enabled = true;
            else viewCamera.Render();
        }

        bool InitializeView()
        {
            if (viewCamera != null) return true;
            if (opticDirection == null) return false;
            ViewTexture = new RenderTexture(resolution, resolution, 16) { name = "ScalpalVirtualLaparoscopeView" };
            ViewTexture.Create();
            var cameraObject = new GameObject("VirtualLaparoscopeCamera");
            cameraObject.transform.SetParent(opticDirection, false);
            viewCamera = cameraObject.AddComponent<Camera>();
            viewCamera.enabled = false;
            viewCamera.stereoTargetEye = StereoTargetEyeMask.None;
            viewCamera.fieldOfView = 65;
            viewCamera.nearClipPlane = 0.004f;
            viewCamera.farClipPlane = 5;
            viewCamera.cullingMask = visibleLayers;
            viewCamera.targetTexture = ViewTexture;
            if (monitor != null)
            {
                var shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (shader != null)
                {
                    originalMonitorMaterial = monitor.sharedMaterial;
                    monitorMaterial = new Material(shader);
                    monitorMaterial.mainTexture = ViewTexture;
                    if (monitorMaterial.HasProperty("_BaseMap")) monitorMaterial.SetTexture("_BaseMap", ViewTexture);
                    monitor.sharedMaterial = monitorMaterial;
                }
            }
            return true;
        }

        void OnDisable()
        {
            if (monitor != null && monitorMaterial != null) monitor.sharedMaterial = originalMonitorMaterial;
            if (viewCamera != null) Destroy(viewCamera.gameObject);
            if (monitorMaterial != null) Destroy(monitorMaterial);
            if (ViewTexture != null) { ViewTexture.Release(); Destroy(ViewTexture); }
            viewCamera = null;
            ViewTexture = null;
            monitorMaterial = null;
        }
    }
}
