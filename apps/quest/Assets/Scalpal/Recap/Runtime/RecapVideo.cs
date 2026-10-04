using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace Scalpal.Recap
{
    public sealed class RecapVideo : MonoBehaviour
    {
        public VideoPlayer sourcePlayer, robotPlayer;
        public VideoClip sampleClip;
        public RecapPanel panel;
        public event Action AccessRefreshRequested;
        public bool Fallback { get; private set; }
        public bool Playing { get; private set; }
        public bool SourceUnavailable { get; private set; } = true;
        public string PlaybackError { get; private set; } = "";
        public double Duration { get; private set; }
        public double WindowStart { get; private set; }
        public double WindowEnd { get; private set; }
        public bool HighlightEnabled => highlight;
        public double Position => robotPlayer && robotPlayer.isPrepared ? Math.Max(0, robotPlayer.time) : pendingPosition;
        RunResult result;
        string boundIdentity;
        bool prepared, subscribed, refreshPending, resumeAfterPrepare, highlight;
        double pendingPosition;
        long activeExpiresAt;
        int automaticRefreshes;
        float prepareStarted;

        void Awake() { Subscribe(); }
        void Subscribe()
        {
            if (subscribed || !robotPlayer || !sourcePlayer) return;
            robotPlayer.errorReceived += Error; sourcePlayer.errorReceived += Error;
            robotPlayer.prepareCompleted += Prepared; sourcePlayer.prepareCompleted += Prepared;
            subscribed = true;
        }
        static bool HasAccess(RunResult value) => value?.replay?.status == "ready" && value.replay.source != "unknown" && !string.IsNullOrWhiteSpace(value.replay.replayVideoUrl);
        static string Identity(RunResult value)
        {
            if (value?.replay == null) return "";
            // Length-prefix fields so delimiter characters cannot alias distinct identities.
            return string.Concat(new[] { value.runId, value.sessionId, value.attemptId, value.replay.jobId,
                value.replay.jobRun.ToString(), value.replay.sourceArtifactId, value.replay.replayArtifactId,
                value.replay.source, HasAccess(value) ? "resolved" : "fallback" }.Select(s => (s ?? "").Length + ":" + s));
        }
        public void Bind(RunResult value)
        {
            Subscribe();
            if (value == null) return;
            string identity = Identity(value);
            result = value;
            if (boundIdentity == identity) { ApplyWindow(); return; }
            boundIdentity = identity; automaticRefreshes = 0;
            highlight = value.demo?.enabled == true;
            pendingPosition = 0; resumeAfterPrepare = false;
            PrepareMedia(value);
        }
        // Only this explicit path renews an existing capability. Ordinary status polls never restart media.
        public void RefreshAccess(RunResult value)
        {
            if (value == null || !HasAccess(value))
            {
                refreshPending = false; resumeAfterPrepare = false; pendingPosition = 0;
                PlaybackError = "Replay access could not be refreshed; showing the labeled sample.";
                LoadSample(); ApplyWindow(); return;
            }
            if (Identity(value) != boundIdentity) { Bind(value); return; }
            if (!refreshPending) { pendingPosition = Position; resumeAfterPrepare = Playing; }
            result = value;
            PrepareMedia(value);
        }
        void PrepareMedia(RunResult value)
        {
            Pause(); prepared = false; refreshPending = false; PlaybackError = "";
            prepareStarted = Time.realtimeSinceStartup;
            robotPlayer.Stop(); sourcePlayer.Stop(); ClearSource(); SourceUnavailable = true;
            Fallback = !HasAccess(value);
            activeExpiresAt = Fallback ? 0 : value.replay.expiresAtUnixMs;
            if (Fallback) LoadSample();
            else
            {
                robotPlayer.source = VideoSource.Url; robotPlayer.url = value.replay.replayVideoUrl; robotPlayer.Prepare();
                if (!string.IsNullOrWhiteSpace(value.replay.sourceVideoUrl))
                {
                    sourcePlayer.source = VideoSource.Url; sourcePlayer.url = value.replay.sourceVideoUrl; sourcePlayer.Prepare();
                }
            }
            ApplyWindow();
        }
        void LoadSample()
        {
            Fallback = true; activeExpiresAt = 0; prepared = false; prepareStarted = Time.realtimeSinceStartup;
            sourcePlayer.Stop(); ClearSource(); SourceUnavailable = true; robotPlayer.Stop();
            pendingPosition = 0;
            if (sampleClip) { robotPlayer.source = VideoSource.VideoClip; robotPlayer.clip = sampleClip; robotPlayer.Prepare(); }
            else PlaybackError = "Sample asset unavailable. Learning scores remain available.";
        }
        void Prepared(VideoPlayer player)
        {
            FitVideo(player);
            if (player == sourcePlayer && !Fallback)
            {
                SourceUnavailable = false;
                if (sourcePlayer.canSetTime) sourcePlayer.time = Position;
                if (Playing) sourcePlayer.Play();
            }
            if (player == robotPlayer)
            {
                prepared = true;
                if (!Fallback && result != null) result.replay.durationSeconds = robotPlayer.length;
                double resumeAt = pendingPosition;
                ApplyWindow();
                SetTime(Math.Max(WindowStart, Math.Min(WindowEnd, resumeAt)));
                if (resumeAfterPrepare) { resumeAfterPrepare = false; StartPlaying(); }
            }
            if (panel) panel.Refresh();
        }
        void ClearSource()
        {
            if (!sourcePlayer || !sourcePlayer.targetTexture || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var previous = RenderTexture.active; RenderTexture.active = sourcePlayer.targetTexture;
            GL.Clear(true, true, Color.black); RenderTexture.active = previous;
        }
        static void FitVideo(VideoPlayer player)
        {
            if (player.width > 0 && player.height > 0) player.aspectRatio = VideoAspectRatio.FitInside;
        }
        public void SetHighlight(bool enabled) { highlight = enabled; ApplyWindow(); }
        public void ApplyWindow()
        {
            Duration = robotPlayer && robotPlayer.isPrepared ? robotPlayer.length : Fallback ? (sampleClip ? sampleClip.length : 20) : result?.replay.durationSeconds ?? 0;
            double length = highlight ? Math.Min(Duration, Math.Max(1, Math.Min(20, result?.demo?.replayHighlightSeconds ?? 20))) : Duration;
            WindowStart = highlight && !Fallback && result != null ? RunResultContract.HighlightStart(result, Duration) : 0;
            WindowStart = Math.Max(0, Math.Min(WindowStart, Duration - length));
            WindowEnd = WindowStart + length;
            if (Position < WindowStart || Position > WindowEnd) SetTime(WindowStart);
        }
        public static double PlaybackWindow(double length, DemoFlags demo) => demo?.enabled == true ? Math.Min(length, Math.Max(1, Math.Min(20, demo.replayHighlightSeconds))) : length;
        public void TogglePlay()
        {
            if (Playing) { Pause(); return; }
            if (!robotPlayer.isPrepared) return;
            if (!Fallback && activeExpiresAt > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= activeExpiresAt) { RequestRefresh(true); return; }
            if (Position >= WindowEnd - .05 || Position < WindowStart) SetTime(WindowStart);
            StartPlaying();
        }
        void StartPlaying()
        {
            robotPlayer.Play();
            if (!Fallback && !SourceUnavailable && sourcePlayer.isPrepared) { sourcePlayer.time = Position; sourcePlayer.Play(); }
            Playing = true;
        }
        public void Pause() { Playing = false; if (robotPlayer) robotPlayer.Pause(); if (sourcePlayer) sourcePlayer.Pause(); }
        void SetTime(double time)
        {
            pendingPosition = Math.Max(WindowStart, Math.Min(WindowEnd, time));
            if (robotPlayer && robotPlayer.isPrepared && robotPlayer.canSetTime) robotPlayer.time = pendingPosition;
            if (!Fallback && !SourceUnavailable && sourcePlayer && sourcePlayer.isPrepared && sourcePlayer.canSetTime) sourcePlayer.time = pendingPosition;
        }
        public void Seek(double time) { Pause(); resumeAfterPrepare = false; SetTime(time); }
        public void SeekError(int index)
        {
            if (Fallback || result?.surgery?.available != true) return;
            var errors = RunResultContract.Errors(result);
            if (index >= 0 && index < errors.Length && RunResultContract.TryClipTime(result, errors[index], out var seconds))
            {
                // A selected logged event must seek to its real time, never a nearby window edge.
                if (seconds < WindowStart || seconds > WindowEnd) SetHighlight(false);
                Seek(seconds);
            }
        }
        void RequestRefresh(bool playAfter, bool automatic = false)
        {
            if (refreshPending || Fallback) return;
            if (automatic && automaticRefreshes++ >= 1)
            {
                RefreshAccess(null);
                return;
            }
            pendingPosition = Position; resumeAfterPrepare = playAfter; refreshPending = true;
            Pause();
            if (AccessRefreshRequested != null) AccessRefreshRequested.Invoke();
            else RefreshAccess(null);
        }
        void Error(VideoPlayer player, string message)
        {
            if (player == sourcePlayer)
            {
                sourcePlayer.Stop(); ClearSource(); SourceUnavailable = true;
                PlaybackError = "Source recording could not play; its image is hidden while access refreshes.";
            }
            else PlaybackError = Fallback ? "Sample playback unavailable. Continue to your learning scores." : "Replay access failed; refreshing without changing the recorded result.";
            if (!Fallback) RequestRefresh(Playing, true);
            else Pause();
            if (panel) panel.Refresh();
        }
        void Update()
        {
            if (boundIdentity == null) return;
            if (robotPlayer.isPrepared) pendingPosition = Math.Max(0, robotPlayer.time);
            if (!prepared && !refreshPending && Time.realtimeSinceStartup - prepareStarted > 20 && string.IsNullOrEmpty(PlaybackError)) Error(robotPlayer, "Prepare timeout");
            if (Playing && Position >= WindowEnd - .04) Pause();
            if (Playing && !Fallback && !SourceUnavailable && sourcePlayer.isPrepared && Math.Abs(sourcePlayer.time - Position) > .25 && sourcePlayer.canSetTime) sourcePlayer.time = Position;
        }
        void OnApplicationPause(bool paused) { if (paused) Pause(); }
        void OnApplicationFocus(bool focused) { if (!focused) Pause(); }
        void OnDestroy()
        {
            foreach (var player in new[] { sourcePlayer, robotPlayer }) if (player)
            {
                player.errorReceived -= Error; player.prepareCompleted -= Prepared;
                player.Stop(); if (player.targetTexture) player.targetTexture.Release();
            }
        }
    }
}
