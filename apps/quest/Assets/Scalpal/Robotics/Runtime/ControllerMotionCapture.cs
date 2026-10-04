// Controller motion as robot-hand input. Each frame records both Quest controllers' tracked pose, grip,
// trigger, and the instrument each holds, streams it to the Mac over UDP (`scalpal-motion teleop` drives a
// simulated robot hand with it), and, with the operator's consent box ticked, writes JSONL episodes that
// become robot training demonstrations (`scalpal-motion learn sweep --teleop`).
// Poses are relative to Pose Root (the registered PatientRoot) when it is set, so the robot sees motion in
// the patient's frame; else world space when the scene's XRInstrumentInput has a tracking origin; else
// tracking space. The frame's `space` field says which ("patient", "world" or "tracking").
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Scalpal.Instruments;
using UnityEngine;
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
        [Header("Live stream to the Mac")]
        [SerializeField] bool streaming = true;
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
