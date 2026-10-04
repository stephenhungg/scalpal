// Records training episodes: session.json plus one HandJointsFrame JSON line per camera frame, and optionally
// JPEG frames for offline re-processing. Writes under persistentDataPath/scalpal-hands/<session>/<episode>/;
// pull with `adb pull /sdcard/Android/data/<package>/files/scalpal-hands`.
// Hand joints computed from camera images are Meta Device User Data: nothing is written without the
// participant's consent for this session, and raw footage stays private.
#if SCALPAL_HANDS
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Scalpal.Hands
{
    [Serializable]
    public class HandEpisodeInfo
    {
        public string schema = "scalpal.hand_episode.v1";
        public string sessionId;
        public string episodeId;
        public string label;
        public string exerciseId;
        public string patientId;
        public string startedAtUtc;
        public bool participantConsented;
        public string consentNote;
        public string model = "mediapipe-blazehand (unity/inference-engine-blaze-hand)";
        public int jpegEveryFrames;
        public string device;
        public string appVersion;
    }

    [DisallowMultipleComponent]
    public sealed class HandEpisodeRecorder : MonoBehaviour
    {
        [SerializeField] BlazeHandTracker tracker;
        [Tooltip("Optional: lets the recorder save raw camera frames.")]
        [SerializeField] PassthroughCameraAccess cameraAccess;
        [Tooltip("0 records joints only. 2 saves every second frame as JPEG.")]
        [SerializeField] int jpegEveryFrames = 0;
        [Range(50, 95)] [SerializeField] int jpegQuality = 85;
        [Tooltip("Tick only after the participant agreed out loud to recording their hand joints this session.")]
        [SerializeField] bool operatorConfirmedConsent;
        [Tooltip("Start an episode when the app starts (needs the consent box above). For recording demo clips.")]
        [SerializeField] bool recordOnStart;
        [SerializeField] string startLabel = "instrument_transfer";

        StreamWriter writer;
        string episodeDir;
        int pendingJpegs;
        bool consented;

        public bool Recording => writer != null;
        public int FramesWritten { get; private set; }
        public string EpisodeDirectory => episodeDir;

        // Call from the consent UI. Applies to this app session only.
        public void SetParticipantConsent(bool consent) => consented = consent;

        public bool StartEpisode(string label, string exerciseId = "", string patientId = "")
        {
            if (Recording) StopEpisode();
            if (!consented || tracker == null)
            {
                Debug.LogWarning("[Scalpal.Hands] Recording refused: participant consent not recorded for this session.", this);
                return false;
            }
            var info = new HandEpisodeInfo
            {
                sessionId = tracker.SessionId,
                episodeId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                label = label ?? "",
                exerciseId = exerciseId ?? "",
                patientId = patientId ?? "",
                startedAtUtc = DateTime.UtcNow.ToString("o"),
                participantConsented = true,
                consentNote = "Participant agreed to recording hand joints for Scalpal training data in this session.",
                jpegEveryFrames = jpegEveryFrames,
                device = SystemInfo.deviceModel,
                appVersion = Application.version,
            };
            episodeDir = Path.Combine(Application.persistentDataPath, "scalpal-hands", info.sessionId, info.episodeId);
            Directory.CreateDirectory(episodeDir);
            if (jpegEveryFrames > 0) Directory.CreateDirectory(Path.Combine(episodeDir, "frames"));
            File.WriteAllText(Path.Combine(episodeDir, "session.json"), JsonUtility.ToJson(info, true));
            writer = new StreamWriter(Path.Combine(episodeDir, "frames.jsonl"), false, new UTF8Encoding(false));
            FramesWritten = 0;
            tracker.FrameReady += OnFrame;
            return true;
        }

        public void StopEpisode()
        {
            if (tracker != null) tracker.FrameReady -= OnFrame;
            writer?.Flush();
            writer?.Dispose();
            writer = null;
        }

        void Start()
        {
            if (operatorConfirmedConsent) SetParticipantConsent(true);
            if (recordOnStart) StartEpisode(startLabel);
        }

        void OnDisable() => StopEpisode();

        void OnFrame(HandJointsFrame frame)
        {
            if (writer == null) return;
            writer.WriteLine(JsonUtility.ToJson(frame));
            FramesWritten++;
            if (jpegEveryFrames > 0 && cameraAccess != null && frame.frameIndex % jpegEveryFrames == 0 && Volatile.Read(ref pendingJpegs) < 3) SaveJpeg(frame.frameIndex);
        }

        // GPU readback, then encode and write off the main thread. Drops frames rather than stalling tracking.
        void SaveJpeg(int frameIndex)
        {
            var texture = cameraAccess.GetTexture();
            if (texture == null) return;
            var width = texture.width;
            var height = texture.height;
            var path = Path.Combine(episodeDir, "frames", frameIndex.ToString("D6") + ".jpg");
            var quality = jpegQuality;
            Interlocked.Increment(ref pendingJpegs);
            AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32, request =>
            {
                if (request.hasError)
                {
                    Interlocked.Decrement(ref pendingJpegs);
                    return;
                }
                var pixels = new NativeArray<byte>(request.GetData<byte>(), Allocator.Persistent);
                Task.Run(() =>
                {
                    try
                    {
                        var jpg = ImageConversion.EncodeNativeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_SRGB, (uint)width, (uint)height, 0, quality);
                        File.WriteAllBytes(path, jpg.ToArray());
                        jpg.Dispose();
                    }
                    finally
                    {
                        pixels.Dispose();
                        Interlocked.Decrement(ref pendingJpegs);
                    }
                });
            });
        }
    }
}
#endif
