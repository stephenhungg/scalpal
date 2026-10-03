using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using PassthroughCameraSamples.MultiObjectDetection;

namespace QuestDiagnostics
{
    // Diagnostic observer only: does not create, enable or take ownership of a camera.
    [DefaultExecutionOrder(10000)]
    public sealed class QuestCameraDiagnostics : MonoBehaviour
    {
        private const string Endpoint = "http://localhost:8098";
        private static readonly HashSet<string> AllowedScenes = new()
        {
            "StartScene", "CameraViewer", "CameraToWorld", "BrightnessEstimation",
            "MultiObjectDetection", "ShaderSample"
        };
        private readonly Dictionary<int, CameraState> _states = new();
        private PassthroughCameraAccess[] _cameras = Array.Empty<PassthroughCameraAccess>();
        private double _lastReportTime;
        private double _nextPreviewTime;
        private bool _paused;
        private bool _eventBusy;
        private bool _previewBusy;
        private bool _detectionEventBusy;
        private string _lastDetectionJson;
        private int _lastCommandId;
        private double _lastNetworkWarningTime = -100;
        private string _scene;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (!Debug.isDebugBuild) return;
            var owner = new GameObject(nameof(QuestCameraDiagnostics));
            DontDestroyOnLoad(owner);
            owner.AddComponent<QuestCameraDiagnostics>();
        }

        private void Start()
        {
            _lastReportTime = Time.realtimeSinceStartupAsDouble;
            _nextPreviewTime = _lastReportTime + 5;
            RefreshCameras();
            StartCoroutine(PollCommands());
        }

        private void OnApplicationPause(bool paused) => _paused = paused;

        private void RefreshCameras()
        {
            _scene = SceneManager.GetActiveScene().name;
            _cameras = FindObjectsByType<PassthroughCameraAccess>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var present = new HashSet<int>();
            foreach (var camera in _cameras)
            {
                int id = camera.GetInstanceID();
                present.Add(id);
                if (!_states.ContainsKey(id)) _states[id] = new CameraState();
            }
            var removed = new List<int>();
            foreach (var id in _states.Keys) if (!present.Contains(id)) removed.Add(id);
            foreach (var id in removed) _states.Remove(id);
        }

        private void LateUpdate()
        {
            double now = Time.realtimeSinceStartupAsDouble;
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            // Android's Unity log messages truncate long JSON. Forward the complete
            // once-per-second record over the existing local USB diagnostic tunnel.
            var detectionJson = QuestDetectionTelemetry.LastReportJson;
            if (!_detectionEventBusy && !string.IsNullOrEmpty(detectionJson)
                && detectionJson != _lastDetectionJson)
            {
                _lastDetectionJson = detectionJson;
                StartCoroutine(SendDetectionEvent(detectionJson));
            }
#endif
            if (_scene != SceneManager.GetActiveScene().name) RefreshCameras();
            foreach (var camera in _cameras)
            {
                if (!camera || !camera.isActiveAndEnabled || !camera.IsPlaying) continue;
                var state = _states[camera.GetInstanceID()];
                long ticks = camera.Timestamp.Ticks;
                if (ticks > 0 && ticks != state.lastTicks)
                {
                    // Changes observed by LateUpdate, not an independent hardware capture counter.
                    if (state.lastTicks > 0 && ticks > state.lastTicks)
                        state.lastDeltaMs = (ticks - state.lastTicks) / (double)TimeSpan.TicksPerMillisecond;
                    else state.lastDeltaMs = 0;
                    state.lastTicks = ticks;
                    state.lastChangeTime = now;
                    state.changes++;
                }
            }

            if (now - _lastReportTime >= 1)
            {
                RefreshCameras();
                var records = new List<CameraRecord>();
                double interval = now - _lastReportTime;
                foreach (var camera in _cameras)
                {
                    if (!camera) continue;
                    var state = _states[camera.GetInstanceID()];
                    records.Add(Describe(camera, state, now, interval));
                    state.changes = 0;
                }
                var report = new Report
                {
                    kind = "unity-camera-diagnostics", scene = _scene,
                    focused = Application.isFocused, paused = _paused,
                    realtimeSeconds = now, intervalSeconds = interval,
                    cameras = records.ToArray()
                };
                string json = JsonUtility.ToJson(report);
                Debug.Log("QUEST_DIAGNOSTICS " + json);
                if (!_eventBusy) StartCoroutine(SendEvent(json));
                _lastReportTime = now;
            }

            if (now >= _nextPreviewTime && !_previewBusy && !_paused && Application.isFocused)
            {
                _nextPreviewTime = now + 5;
                foreach (var camera in _cameras)
                    if (camera && camera.isActiveAndEnabled && camera.IsPlaying
                        && _states[camera.GetInstanceID()].lastChangeTime >= 0
                        && now - _states[camera.GetInstanceID()].lastChangeTime <= 0.25)
                    {
                        StartCoroutine(SendPreview(camera));
                        break;
                    }
            }
        }

        private CameraRecord Describe(PassthroughCameraAccess camera, CameraState state, double now, double interval)
        {
            double age = state.lastChangeTime < 0 ? -1 : now - state.lastChangeTime;
            var record = new CameraRecord
            {
                instanceId = camera.GetInstanceID(), eye = camera.CameraPosition.ToString(),
                enabled = camera.isActiveAndEnabled, isPlaying = camera.IsPlaying,
                timestampTicks = camera.Timestamp.Ticks,
                observedDistinctTimestamps = state.changes,
                observedTimestampChangesPerSecond = state.changes / interval,
                millisecondsSinceLastTimestampChange = age < 0 ? -1 : age * 1000,
                lastTimestampDeltaMs = state.lastDeltaMs,
                fresh = camera.isActiveAndEnabled && camera.IsPlaying && age >= 0 && age <= 0.25 && !_paused,
                resolution = camera.CurrentResolution
            };
            if (camera.isActiveAndEnabled && camera.IsPlaying)
            {
                try
                {
                    var intrinsics = camera.Intrinsics;
                    record.focalLength = intrinsics.FocalLength;
                    record.principalPoint = intrinsics.PrincipalPoint;
                    record.sensorResolution = intrinsics.SensorResolution;
                    record.lensOffset = intrinsics.LensOffset;
                    record.pose = camera.GetCameraPose();
                    record.metadataAvailable = true;
                }
                catch (Exception error) { record.metadataError = error.GetType().Name; }
            }
            return record;
        }

        private IEnumerator PollCommands()
        {
            var delay = new WaitForSecondsRealtime(1);
            while (true)
            {
                using (var request = UnityWebRequest.Get(Endpoint + "/unity-command"))
                {
                    request.timeout = 2;
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        Command command = null;
                        try { command = JsonUtility.FromJson<Command>(request.downloadHandler.text); }
                        catch (Exception) { NetworkWarning("invalid command JSON"); }
                        if (command != null && command.id != _lastCommandId)
                        {
                            _lastCommandId = command.id;
                            if (AllowedScenes.Contains(command.scene ?? "") && IsBuiltScene(command.scene))
                            {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
                                if (!string.IsNullOrEmpty(command.backend))
                                {
                                    if (command.backend == "CPU")
                                        QuestDetectionTelemetry.RequestedBackend = Unity.InferenceEngine.BackendType.CPU;
                                    else if (command.backend == "GPUCompute")
                                        QuestDetectionTelemetry.RequestedBackend = Unity.InferenceEngine.BackendType.GPUCompute;
                                    else
                                    {
                                        NetworkWarning("command rejected: unsupported diagnostic inference backend");
                                        continue;
                                    }
                                }
#endif
                                Debug.Log("QUEST_DIAGNOSTICS command " + JsonUtility.ToJson(command));
                                SceneManager.LoadScene(command.scene);
                                if (command.scene == "MultiObjectDetection" && command.startDetection)
                                    StartCoroutine(StartDiagnosticDetection());
                            }
                            else NetworkWarning("command rejected: scene is not in the named build whitelist");
                        }
                    }
                    else NetworkWarning("command endpoint unavailable: " + request.result);
                }
                yield return delay;
            }
        }

        private static bool IsBuiltScene(string scene)
        {
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var path = SceneUtility.GetScenePathByBuildIndex(i);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == scene) return true;
            }
            return false;
        }

        private IEnumerator StartDiagnosticDetection()
        {
            // Explicit USB test command enters the same pause handler as the sample's A/pinch action.
            // Keep the permission and camera readiness gates; this component exists only in development builds.
            double deadline = Time.realtimeSinceStartupAsDouble + 15;
            // LoadScene takes effect on the next frame; do not reject while the
            // previous active scene is still visible in the command coroutine.
            yield return null;
            while (SceneManager.GetActiveScene().name == "MultiObjectDetection"
                   && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var menu = FindFirstObjectByType<DetectionUiMenuManager>();
                var camera = FindFirstObjectByType<PassthroughCameraAccess>();
                if (menu && camera && camera.IsPlaying
                    && OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)
                    && OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
                {
                    menu.SendMessage("OnPauseMenu", false, SendMessageOptions.RequireReceiver);
                    Debug.Log("QUEST_DIAGNOSTICS detection started by explicit USB test command");
                    yield break;
                }
                yield return null;
            }
            NetworkWarning("detection command could not pass readiness gates");
        }

        private IEnumerator SendEvent(string json)
        {
            _eventBusy = true;
            using (var request = Post("/event", Encoding.UTF8.GetBytes(json), "application/json"))
                yield return request.SendWebRequest();
            _eventBusy = false;
        }

        private IEnumerator SendDetectionEvent(string json)
        {
            _detectionEventBusy = true;
            using (var request = Post("/event", Encoding.UTF8.GetBytes(json), "application/json"))
                yield return request.SendWebRequest();
            _detectionEventBusy = false;
        }

        private IEnumerator SendPreview(PassthroughCameraAccess camera)
        {
            _previewBusy = true;
            byte[] jpeg = null;
            Texture2D copy = null;
            RenderTexture temporary = null;
            RenderTexture previous = RenderTexture.active;
            string snapshotId = Guid.NewGuid().ToString("N");
            PreviewRecord record = null;
            try
            {
                var source = camera.GetTexture();
                int width = 640;
                int height = Math.Max(1, Mathf.RoundToInt(width * source.height / (float)source.width));
                record = new PreviewRecord
                {
                    kind = "unity-camera-preview", snapshotId = snapshotId,
                    scene = SceneManager.GetActiveScene().name, eye = camera.CameraPosition.ToString(),
                    cameraTimestampTicksBeforeReadback = camera.Timestamp.Ticks,
                    cameraPoseBeforeReadback = camera.GetCameraPose(),
                    width = width, height = height, realtimeSeconds = Time.realtimeSinceStartupAsDouble,
                    unsynchronizedPreview = true,
                    synchronizationCaveat = "GPU readback JPEG is a visual preview; camera timestamp and pose are nearby samples, not a proven synchronized capture."
                };
                temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                copy = new Texture2D(width, height, TextureFormat.RGB24, false);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                jpeg = copy.EncodeToJPG(70);
            }
            catch (Exception error) { NetworkWarning("preview unavailable: " + error.GetType().Name); }
            finally
            {
                RenderTexture.active = previous;
                if (temporary) RenderTexture.ReleaseTemporary(temporary);
                if (copy) Destroy(copy);
            }
            if (jpeg != null)
            {
                string json = JsonUtility.ToJson(record);
                Debug.Log("QUEST_DIAGNOSTICS " + json);
                using (var request = Post("/event", Encoding.UTF8.GetBytes(json), "application/json"))
                    yield return request.SendWebRequest();
                using (var request = Post("/frame", jpeg, "image/jpeg"))
                {
                    request.SetRequestHeader("x-questprobe-snapshot-id", snapshotId);
                    request.SetRequestHeader("x-questprobe-source", "unity-unsynchronized-preview");
                    // DateTime ticks are deliberately not sent in the raw-image nanosecond header.
                    yield return request.SendWebRequest();
                }
            }
            _previewBusy = false;
        }

        private static UnityWebRequest Post(string path, byte[] body, string contentType)
        {
            var request = new UnityWebRequest(Endpoint + path, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body), downloadHandler = new DownloadHandlerBuffer(), timeout = 2
            };
            request.SetRequestHeader("Content-Type", contentType);
            return request;
        }

        private void NetworkWarning(string message)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastNetworkWarningTime < 10) return;
            _lastNetworkWarningTime = now;
            Debug.LogWarning("QUEST_DIAGNOSTICS " + message);
        }

        private sealed class CameraState
        {
            public long lastTicks;
            public double lastChangeTime = -1;
            public double lastDeltaMs;
            public int changes;
        }

        [Serializable] private sealed class Command { public int id; public string scene; public bool startDetection; public string backend; }
        [Serializable] private sealed class Report
        {
            public string kind, scene;
            public bool focused, paused;
            public double realtimeSeconds, intervalSeconds;
            public CameraRecord[] cameras;
            public string freshnessDefinition = "Timestamp changed within 250 ms of realtime observation. IsPlaying only means a camera image was received at least once. Counts are observed in LateUpdate, not hardware frame counts.";
        }
        [Serializable] private sealed class CameraRecord
        {
            public int instanceId, observedDistinctTimestamps;
            public string eye, metadataError;
            public bool enabled, isPlaying, fresh, metadataAvailable;
            public long timestampTicks;
            public double observedTimestampChangesPerSecond, millisecondsSinceLastTimestampChange, lastTimestampDeltaMs;
            public Vector2Int resolution, sensorResolution;
            public Vector2 focalLength, principalPoint;
            public Pose pose, lensOffset;
        }
        [Serializable] private sealed class PreviewRecord
        {
            public string kind, snapshotId, scene, eye, synchronizationCaveat;
            public long cameraTimestampTicksBeforeReadback;
            public Pose cameraPoseBeforeReadback;
            public int width, height;
            public double realtimeSeconds;
            public bool unsynchronizedPreview;
        }
    }
}
