using UnityEngine;
using UnityEngine.Video;

namespace Scalpal.Recap
{
    // Streams the robot replay from the coach (HTTP Range) on a loop. A player error or a stalled prepare reports
    // the replay as failed; nothing is substituted.
    public sealed class RecapVideo : MonoBehaviour
    {
        public VideoPlayer robotPlayer;
        public RecapController controller;
        public const float PrepareTimeoutSeconds = 20;
        public string Url { get; private set; } = "";
        public bool Showing => !string.IsNullOrEmpty(Url) && !Failed;
        public bool Failed { get; private set; }
        float prepareStarted;
        bool subscribed;

        void Awake() => Subscribe();
        void Subscribe()
        {
            if (subscribed || !robotPlayer) return;
            robotPlayer.errorReceived += Error; robotPlayer.prepareCompleted += Prepared;
            subscribed = true;
        }
        public void Play(string url)
        {
            Subscribe();
            if (url == Url && !Failed) return;
            robotPlayer.Stop(); Clear();
            Url = url; Failed = false; prepareStarted = Time.realtimeSinceStartup;
            robotPlayer.source = VideoSource.Url; robotPlayer.url = url; robotPlayer.isLooping = true;
            robotPlayer.Prepare();
        }
        public void Stop()
        {
            Url = "";
            if (robotPlayer) robotPlayer.Stop();
        }
        // Black until the first decoded frame, never a stale or uninitialised texture.
        void Clear()
        {
            if (!robotPlayer.targetTexture || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var previous = RenderTexture.active; RenderTexture.active = robotPlayer.targetTexture;
            GL.Clear(true, true, Color.black); RenderTexture.active = previous;
        }
        void Prepared(VideoPlayer player)
        {
            if (player.width > 0 && player.height > 0) player.aspectRatio = VideoAspectRatio.FitInside;
            if (!string.IsNullOrEmpty(Url)) player.Play();
        }
        void Error(VideoPlayer player, string message)
        {
            Debug.LogWarning("[Scalpal.Recap] robot replay failed: " + message);
            Failed = true; player.Stop();
            if (controller) controller.ReplayFailed();
        }
        void Update()
        {
            if (!string.IsNullOrEmpty(Url) && !Failed && !robotPlayer.isPrepared && Time.realtimeSinceStartup - prepareStarted > PrepareTimeoutSeconds)
                Error(robotPlayer, "prepare timeout");
        }
        void OnApplicationPause(bool paused) { if (robotPlayer && !string.IsNullOrEmpty(Url)) { if (paused) robotPlayer.Pause(); else if (robotPlayer.isPrepared) robotPlayer.Play(); } }
        void OnDestroy()
        {
            if (!robotPlayer) return;
            robotPlayer.errorReceived -= Error; robotPlayer.prepareCompleted -= Prepared;
            robotPlayer.Stop(); if (robotPlayer.targetTexture) robotPlayer.targetTexture.Release();
        }
    }
}
