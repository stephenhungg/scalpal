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
        // Open surgery: show the view on a panel that follows the learner's gaze, only while the trigger is held.
        public bool followView;
        public float panelDistance = .55f, panelSize = .26f;
        public RenderTexture ViewTexture { get; private set; }
        public GameObject ViewPanel { get; private set; }
        public bool PanelShown => ViewPanel && ViewPanel.activeSelf;
        Camera viewCamera;
        InstrumentBehaviour instrument;
        Material monitorMaterial, originalMonitorMaterial;
        double nextFrame;

        void LateUpdate()
        {
            if (viewCamera != null) viewCamera.enabled = false;
            if (instrument == null) instrument = GetComponent<InstrumentBehaviour>();
            bool viewing = instrument.Held && instrument.TrackingValid && instrument.Activation >= 0.7f;
            if (followView) FollowGaze(viewing);
            if (!viewing) return;
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

        void FollowGaze(bool viewing)
        {
            var head = Camera.main;
            if (!viewing || !head || !InitializeView()) { if (ViewPanel) ViewPanel.SetActive(false); return; }
            bool appearing = !ViewPanel || !ViewPanel.activeSelf;
            if (!ViewPanel)
            {
                ViewPanel = new GameObject("LaparoscopeViewPanel");
                var frame = GameObject.CreatePrimitive(PrimitiveType.Quad); frame.name = "Frame";
                frame.transform.SetParent(ViewPanel.transform, false); frame.transform.localPosition = new Vector3(0, 0, .002f);
                frame.transform.localScale = Vector3.one * (panelSize + .016f);
                var screen = GameObject.CreatePrimitive(PrimitiveType.Quad); screen.name = "Screen";
                screen.transform.SetParent(ViewPanel.transform, false); screen.transform.localScale = Vector3.one * panelSize;
                foreach (var quad in new[] { frame, screen }) Release(quad.GetComponent<Collider>()); // never a pointer or tool target
                var unlit = Shader.Find("Unlit/Texture"); // shipped: the recap video materials use it
                frame.GetComponent<Renderer>().sharedMaterial = new Material(unlit) { mainTexture = Texture2D.blackTexture };
                screen.GetComponent<Renderer>().sharedMaterial = new Material(unlit) { mainTexture = ViewTexture };
            }
            ViewPanel.SetActive(true);
            // Lower right of the gaze, easing after the head so it reads like a scope monitor that keeps up.
            var view = head.transform;
            Vector3 target = view.position + view.forward * panelDistance + view.right * .14f - view.up * .1f;
            Quaternion facing = Quaternion.LookRotation(target - view.position, Vector3.up);
            float k = appearing ? 1 : 1 - Mathf.Exp(-8 * Time.unscaledDeltaTime);
            ViewPanel.transform.SetPositionAndRotation(Vector3.Lerp(ViewPanel.transform.position, target, k), Quaternion.Slerp(ViewPanel.transform.rotation, facing, k));
        }

        static void Release(Object item) { if (!item) return; if (Application.isPlaying) Destroy(item); else DestroyImmediate(item); }

        void OnDisable()
        {
            if (ViewPanel != null) { Release(ViewPanel); ViewPanel = null; }
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
