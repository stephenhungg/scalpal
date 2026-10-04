using Scalpal.Instruments;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    // Read-only player telemetry, independent of any stage's business state. Never
    // starts voice/capture, changes tracking origin, or advances the experience.
    public sealed class QuestFlowTelemetry : MonoBehaviour
    {
        float nextStatus;
        bool paused;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var telemetry = new GameObject("ScalpalFlowTelemetry");
            DontDestroyOnLoad(telemetry);
            telemetry.AddComponent<QuestFlowTelemetry>();
#endif
        }

        void Update()
        {
            if (Time.unscaledTime < nextStatus) return;
            nextStatus = Time.unscaledTime + 2;
            // Whitelist scene labels: no patient, session, endpoint or config data
            // can accidentally enter the smoke output.
            string scene = SceneManager.GetActiveScene().name;
            switch (scene)
            {
                case "Launch": case "ScalpalShell": case "DiagnosisOffice":
                case "NativeSession": case "RunEnding": case "NativeWorkbench": break;
                default: scene = "Other"; break;
            }
            bool xr = XRInput.Source.DisplayRunning;
            bool head = XRInput.TryPose(XRNode.Head, out _);
            bool focus = XRInput.Source.HasFocus && !paused;
            Debug.Log($"SCALPAL_FLOW_STATUS scene={scene} xr={xr} head={head} focus={focus}");
        }

        void OnApplicationPause(bool value) { paused = value; nextStatus = 0; }
        void OnApplicationFocus(bool value) { nextStatus = 0; }
    }
}
