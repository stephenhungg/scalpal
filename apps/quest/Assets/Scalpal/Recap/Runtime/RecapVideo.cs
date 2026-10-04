using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace Scalpal.Recap
{
    // Downloads the robot replay from the coach to the cache, then loops it from the local file: Android's media
    // player refuses cleartext HTTP streams, UnityWebRequest does not. A failed download, a player error or a
    // stalled prepare reports the replay as failed; nothing is substituted.
    public sealed class RecapVideo : MonoBehaviour
    {
        public VideoPlayer robotPlayer;
        public RecapController controller;
        public const float PrepareTimeoutSeconds = 20;
        public const int DownloadTimeoutSeconds = 30;
        public string Url { get; private set; } = "";
        public bool Downloading { get; private set; }
        public bool Showing => !string.IsNullOrEmpty(Url) && !Failed;
        public bool Failed { get; private set; }
        float prepareStarted;
        bool subscribed;
        UnityWebRequest download;

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
            CancelDownload(); robotPlayer.Stop(); Clear();
            Url = url; Failed = false; robotPlayer.source = VideoSource.Url; robotPlayer.url = ""; robotPlayer.isLooping = true;
            Downloading = true; StartCoroutine(Download(url));
        }
        // Cache file for a coach-relative replay URL; the controller only passes safe /robot/replays/<file>.mp4 paths.
        public static string LocalPath(string url) => Path.Combine(Application.temporaryCachePath, "robot-replay-" + Path.GetFileName(new System.Uri(url).AbsolutePath));
        IEnumerator Download(string url)
        {
            string path = LocalPath(url);
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(path) { removeFileOnAbort = true }, null);
            request.timeout = DownloadTimeoutSeconds; download = request;
            yield return request.SendWebRequest();
            if (download != request) yield break;
            download = null; Downloading = false;
            bool ok = request.result == UnityWebRequest.Result.Success;
            string error = ok ? "" : "download failed (" + request.responseCode + "): " + request.error;
            request.Dispose();
            if (url != Url) yield break;
            if (ok) PlayLocal(path); else Error(robotPlayer, error);
        }
        public void PlayLocal(string path)
        {
            Downloading = false; prepareStarted = Time.realtimeSinceStartup;
            robotPlayer.url = "file://" + path;
            robotPlayer.Prepare();
        }
        void CancelDownload()
        {
            StopAllCoroutines(); Downloading = false;
            if (download != null) { try { download.Abort(); download.Dispose(); } catch (System.ObjectDisposedException) { } download = null; }
        }
        public void Stop()
        {
            Url = ""; CancelDownload();
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
            if (!string.IsNullOrEmpty(Url) && !Failed && !Downloading && !robotPlayer.isPrepared && Time.realtimeSinceStartup - prepareStarted > PrepareTimeoutSeconds)
                Error(robotPlayer, "prepare timeout");
        }
        void OnApplicationPause(bool paused) { if (robotPlayer && !string.IsNullOrEmpty(Url)) { if (paused) robotPlayer.Pause(); else if (robotPlayer.isPrepared) robotPlayer.Play(); } }
        void OnDestroy()
        {
            CancelDownload();
            if (!robotPlayer) return;
            robotPlayer.errorReceived -= Error; robotPlayer.prepareCompleted -= Prepared;
            robotPlayer.Stop(); if (robotPlayer.targetTexture) robotPlayer.targetTexture.Release();
        }
    }
}
