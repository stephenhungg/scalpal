// Controller motion as robot input. Each frame records both Quest controllers' tracked pose, grip, trigger,
// the instrument each holds, the head pose and the coach step (stepId). Frames of the demo step (mark_incision)
// are buffered in memory and, once the step completes, posted to the coach (POST /coach/sessions/:id/robot-demo)
// as a robot demonstration; the Mac's simulated arm + hand learns from them. Optional: a UDP stream to a laptop
// on the LAN (off by default; the headset normally reaches the Mac only over adb-reversed TCP), and, with the
// operator's consent box ticked, on-device JSONL episodes.
// Poses are relative to Pose Root (the registered PatientRoot) when it is set, so the robot sees motion in
// the patient's frame; else world space when the scene's XRInstrumentInput has a tracking origin; else
// tracking space. The frame's `space` field says which ("patient", "world" or "tracking").
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Scalpal.Instruments;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR;

namespace Scalpal.Robotics
{
    [Serializable]
    public class ControllerSample
    {
        public string hand;
        public bool tracked;
        public float[] position = new float[3];
        // Quaternion x, y, z, w (Unity: left-handed, y up).
        public float[] rotation = new float[4];
        public float grip;
        public float trigger;
        public string heldInstrument = "";
    }

    [Serializable]
    public class ControllerMotionFrame
    {
        public string schema = "scalpal.controller_motion.v1";
        public string sessionId = "";
        // The coach step the learner was on when this frame was captured ("" outside practice).
        public string stepId = "";
        public int frameIndex;
        public double unityTime;
        // "patient" when Pose Root is set, "world" when only a tracking origin converted the poses, else "tracking".
        public string space = "tracking";
        public bool headTracked;
        public float[] headPosition = new float[3];
        public float[] headRotation = new float[4];
        public ControllerSample[] controllers = new ControllerSample[0];
    }

    [Serializable]
    public class ControllerEpisodeInfo
    {
        public string schema = "scalpal.controller_episode.v1";
        public string sessionId;
        public string episodeId;
        public string label;
        public string startedAtUtc;
        public bool participantConsented;
        public string device;
        public string appVersion;
    }

    [DisallowMultipleComponent]
    public sealed class ControllerMotionCapture : MonoBehaviour
    {
        [Header("Optional live stream to a laptop on the LAN")]
        [SerializeField] bool streaming;
        [Tooltip("The Mac's LAN IP (ipconfig getifaddr en0). Same network as the headset.")]
        [SerializeField] string host = "192.168.1.20";
        [SerializeField] int port = 9124;
        [SerializeField] float maxStreamRate = 60f;

        [Header("Pose frame")]
        [Tooltip("Registered patient/torso root (the scene's PatientRoot). Poses are expressed relative to it.")]
        [SerializeField] Transform poseRoot;

        [Header("Recording training episodes")]
        [Tooltip("Tick only after the participant agreed to recording their controller motion this session.")]
        [SerializeField] bool operatorConfirmedConsent;
        [SerializeField] bool recordOnStart;
        [SerializeField] string startLabel = "instrument_transfer";

        [Header("Robot demo for the coach")]
        [Tooltip("Frames stamped with this step are buffered and posted when it completes.")]
        [SerializeField] string demoStep = DemoStep;
        [SerializeField] float demoRate = 30f;

        public const string DemoStep = "mark_incision";
        // The coach accepts at most 3 MB per demo; the buffer halves its rate to stay under this budget.
        public const int MaxDemoBytes = 3 * 1024 * 1024, DemoBudgetBytes = 2_800_000;
        // Test seam: receives (url, json) instead of an HTTP request.
        public static Action<string, string> RequestSink;
        readonly List<string> demoFrames = new List<string>();
        int demoBytes;
        double demoInterval, lastBuffered = double.NegativeInfinity;

        readonly XRInstrumentInput[] inputs = new XRInstrumentInput[2];
        string sessionId;
        int frameIndex;
        double lastSent;
        UdpClient client;
        StreamWriter writer;

        public ControllerMotionFrame LastFrame { get; private set; }
        public bool Recording => writer != null;
        public Transform PoseRoot { get => poseRoot; set => poseRoot = value; }
        public int FramesWritten { get; private set; }
        public string EpisodeDirectory { get; private set; } = "";
        public bool OperatorConfirmedConsent => operatorConfirmedConsent;
        // Stamped on every frame. The OR session sets it to the coach's current step.
        public string StepId { get; set; } = "";
        public int DemoFrameCount => demoFrames.Count;
        public int DemoBytes => demoBytes;
        public bool DemoPosted { get; private set; }

        void Awake() => sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);

        void OnEnable()
        {
            FindInputs();
            try
            {
                client = new UdpClient();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Scalpal.Robotics] UDP unavailable: " + e.Message, this);
            }
        }

        void Start()
        {
            if (recordOnStart) StartEpisode(startLabel);
        }

        void OnDisable()
        {
            StopEpisode();
            client?.Close();
            client = null;
        }

        // The scene's controller inputs carry the tracking origin and the instrument interactor for each hand.
        void FindInputs()
        {
            inputs[0] = inputs[1] = null;
            foreach (var input in FindObjectsByType<XRInstrumentInput>(FindObjectsSortMode.None))
            {
                if (input.controller == XRNode.LeftHand) inputs[0] = input;
                else if (input.controller == XRNode.RightHand) inputs[1] = input;
            }
        }

        void Update()
        {
            var frame = new ControllerMotionFrame
            {
                sessionId = sessionId,
                stepId = StepId ?? "",
                frameIndex = frameIndex++,
                unityTime = Time.realtimeSinceStartupAsDouble,
                controllers = new[] { Sample(XRNode.LeftHand, inputs[0], "left"), Sample(XRNode.RightHand, inputs[1], "right") },
            };
            var origin = inputs[1] != null && inputs[1].trackingOrigin != null ? inputs[1].trackingOrigin
                : inputs[0] != null ? inputs[0].trackingOrigin : null;
            frame.space = poseRoot != null ? "patient" : origin != null ? "world" : "tracking";
            if (XRInput.TryPose(XRNode.Head, out var head))
            {
                frame.headTracked = true;
                Write(frame.headPosition, frame.headRotation, origin, poseRoot, head);
            }
            LastFrame = frame;
            if (writer != null)
            {
                writer.WriteLine(JsonUtility.ToJson(frame));
                FramesWritten++;
            }
            Send(frame);
            Buffer(frame);
        }

        // Keeps the demo step's frames (patient space only) at demoRate, halving the rate to fit the budget.
        public void Buffer(ControllerMotionFrame frame)
        {
            if (DemoPosted || frame.stepId != demoStep || frame.space != "patient") return;
            if (demoInterval <= 0) demoInterval = 1.0 / Mathf.Max(1f, demoRate);
            if (frame.unityTime - lastBuffered < demoInterval) return;
            lastBuffered = frame.unityTime;
            var json = JsonUtility.ToJson(frame);
            demoFrames.Add(json); demoBytes += json.Length + 1;
            while (demoBytes > DemoBudgetBytes && demoFrames.Count > 2)
            {
                demoBytes = 0;
                for (int i = demoFrames.Count - 1; i >= 0; i--)
                    if (i % 2 == 1) demoFrames.RemoveAt(i); else demoBytes += demoFrames[i].Length + 1;
                demoInterval *= 2;
            }
        }

        public static string DemoUrl(string coachBaseUrl, string coachSessionId) =>
            coachBaseUrl.TrimEnd('/') + "/coach/sessions/" + Uri.EscapeDataString(coachSessionId) + "/robot-demo";

        public string DemoBody() => "{\"stepId\":\"" + demoStep + "\",\"frames\":[" + string.Join(",", demoFrames) + "]}";

        // Posts the buffered demo once. Without consent, a coach session or two frames it posts nothing.
        // A failed post only logs: it never interrupts the surgery.
        public bool SubmitDemo(string coachBaseUrl, string coachSessionId, bool consented)
        {
            if (DemoPosted || !consented || demoFrames.Count < 2 || string.IsNullOrEmpty(coachBaseUrl) || string.IsNullOrEmpty(coachSessionId)) return false;
            string url = DemoUrl(coachBaseUrl, coachSessionId), body = DemoBody();
            DemoPosted = true; demoFrames.Clear(); demoBytes = 0;
            if (RequestSink != null) { RequestSink(url, body); return true; }
            var request = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(), timeout = 20,
            };
            request.SetRequestHeader("Content-Type", "application/json");
            // Not a coroutine: the request outlives this scene when the case ends right after the step.
            request.SendWebRequest().completed += _ =>
            {
                if (request.result != UnityWebRequest.Result.Success)
                    Debug.LogWarning("[Scalpal.Robotics] robot demo post failed (" + request.responseCode + "): " + request.error);
                else Debug.Log("[Scalpal.Robotics] robot demo accepted: " + request.downloadHandler.text);
                request.Dispose();
            };
            return true;
        }

        // A new attempt starts a new demo.
        public void ResetDemo()
        {
            demoFrames.Clear(); demoBytes = 0; DemoPosted = false;
            demoInterval = 0; lastBuffered = double.NegativeInfinity;
        }

        ControllerSample Sample(XRNode node, XRInstrumentInput input, string hand)
        {
            var sample = new ControllerSample { hand = hand };
            if (XRInput.TryPose(node, out var pose))
            {
                sample.tracked = true;
                Write(sample.position, sample.rotation, input != null ? input.trackingOrigin : null, poseRoot, pose);
                sample.grip = XRInput.Grip(node);
                sample.trigger = XRInput.Trigger(node);
            }
            var interactor = input != null ? input.GetComponent<InstrumentInteractor>() : null;
            if (interactor != null && interactor.HeldInstrument != null) sample.heldInstrument = interactor.HeldInstrument.instrumentId ?? "";
            return sample;
        }

        static void Write(float[] position, float[] rotation, Transform origin, Transform root, Pose pose)
        {
            var p = origin != null ? origin.TransformPoint(pose.position) : pose.position;
            var r = origin != null ? origin.rotation * pose.rotation : pose.rotation;
            if (root != null)
            {
                p = root.InverseTransformPoint(p);
                r = Quaternion.Inverse(root.rotation) * r;
            }
            position[0] = p.x; position[1] = p.y; position[2] = p.z;
            rotation[0] = r.x; rotation[1] = r.y; rotation[2] = r.z; rotation[3] = r.w;
        }

        void Send(ControllerMotionFrame frame)
        {
            if (!streaming || client == null || frame.unityTime - lastSent < 1.0 / maxStreamRate) return;
            lastSent = frame.unityTime;
            var bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(frame));
            try
            {
                client.Send(bytes, bytes.Length, host, port);
            }
            catch (Exception e)
            {
                // A missing Mac never interrupts the surgery; recording continues.
                Debug.LogWarning("[Scalpal.Robotics] stream send failed: " + e.Message, this);
                streaming = false;
            }
        }

        public bool StartEpisode(string label)
        {
            StopEpisode();
            if (!operatorConfirmedConsent)
            {
                Debug.LogWarning("[Scalpal.Robotics] Recording refused: tick Operator Confirmed Consent after the participant agrees.", this);
                return false;
            }
            var info = new ControllerEpisodeInfo
            {
                sessionId = sessionId,
                episodeId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                label = label ?? "",
                startedAtUtc = DateTime.UtcNow.ToString("o"),
                participantConsented = true,
                device = SystemInfo.deviceModel,
                appVersion = Application.version,
            };
            EpisodeDirectory = Path.Combine(Application.persistentDataPath, "scalpal-controller", info.sessionId, info.episodeId);
            Directory.CreateDirectory(EpisodeDirectory);
            File.WriteAllText(Path.Combine(EpisodeDirectory, "session.json"), JsonUtility.ToJson(info, true));
            writer = new StreamWriter(Path.Combine(EpisodeDirectory, "frames.jsonl"), false, new UTF8Encoding(false));
            FramesWritten = 0;
            return true;
        }

        public void StopEpisode()
        {
            writer?.Flush();
            writer?.Dispose();
            writer = null;
        }
    }
}
