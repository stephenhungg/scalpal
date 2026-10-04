using System;
using UnityEngine;
using UnityEngine.Video;

namespace Scalpal.Recap
{
    public sealed class RecapVideo : MonoBehaviour
    {
        public VideoPlayer sourcePlayer, robotPlayer;
        public VideoClip sampleClip;
        public RecapPanel panel;
        public bool Fallback { get; private set; }
        public bool Playing { get; private set; }
        public string PlaybackError { get; private set; } = "";
        public double Duration { get; private set; }
        public double Position => robotPlayer && robotPlayer.isPrepared ? Math.Max(0, robotPlayer.time) : 0;
        RunResult result;
        string boundUrl;
        bool prepared;
        float prepareStarted;
        void Awake()
        {
            robotPlayer.errorReceived += Error; sourcePlayer.errorReceived += Error;
            robotPlayer.prepareCompleted += Prepared; sourcePlayer.prepareCompleted += Prepared;
        }
        public void Bind(RunResult value)
        {
            result = value;
            var url = value.replay.status == "ready" ? value.replay.replayVideoUrl : "";
            var key = value.runId + "|" + value.attemptId + "|" + url + "|" + (string.IsNullOrEmpty(url) ? "" : value.replay.sourceVideoUrl) + "|" + value.replay.source;
            if (boundUrl == key && (prepared || robotPlayer.isPrepared)) { ApplyWindow(); return; }
            boundUrl = key; PlaybackError = ""; Pause(); prepared = false; prepareStarted = Time.realtimeSinceStartup;
            robotPlayer.Stop(); sourcePlayer.Stop(); ClearSource();
            Fallback = string.IsNullOrWhiteSpace(url);
            if (Fallback) LoadSample();
            else
            {
                robotPlayer.source = VideoSource.Url; robotPlayer.url = url; robotPlayer.Prepare();
                if (!string.IsNullOrWhiteSpace(value.replay.sourceVideoUrl)) { sourcePlayer.source = VideoSource.Url; sourcePlayer.url = value.replay.sourceVideoUrl; sourcePlayer.Prepare(); }
            }
            ApplyWindow();
        }
        void LoadSample()
        {
            Fallback = true; sourcePlayer.Stop(); ClearSource(); robotPlayer.Stop();
            if (sampleClip) { robotPlayer.source = VideoSource.VideoClip; robotPlayer.clip = sampleClip; robotPlayer.Prepare(); }
            else PlaybackError = "Sample asset unavailable. Learning scores remain available.";
        }
        void Prepared(VideoPlayer player)
        {
            FitVideo(player);
            if (player == sourcePlayer) { if (sourcePlayer.canSetTime) sourcePlayer.time = Position; if (Playing) sourcePlayer.Play(); }
            if (player == robotPlayer) { prepared = true; if (!Fallback && result != null) result.replay.durationSeconds = robotPlayer.length; ApplyWindow(); }
            panel.Refresh();
        }
        void ClearSource()
        {
            if (!sourcePlayer.targetTexture || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            var previous = RenderTexture.active; RenderTexture.active = sourcePlayer.targetTexture; GL.Clear(true, true, Color.black); RenderTexture.active = previous;
        }
        static void FitVideo(VideoPlayer player)
        {
            if (player.width == 0 || player.height == 0) return;
            // The target texture already uses VideoPlayer's FitInside letterboxing; retain the 4:3 surface.
            player.aspectRatio = VideoAspectRatio.FitInside;
        }
        public void ApplyWindow()
        {
            double length = robotPlayer.isPrepared ? robotPlayer.length : Fallback ? (sampleClip ? sampleClip.length : 20) : result?.replay.durationSeconds ?? 0;
            Duration = PlaybackWindow(length, result?.demo);
            if (Position > Duration) Seek(0);
        }
        public static double PlaybackWindow(double length, DemoFlags demo) => demo?.enabled == true ? Math.Min(length, Math.Max(1, Math.Min(20, demo.replayHighlightSeconds))) : length;
        public void TogglePlay()
        {
            if (Playing) { Pause(); return; }
            if (!robotPlayer.isPrepared) return;
            if (Position >= Duration - .05) Seek(0);
            robotPlayer.Play();
            if (!Fallback && sourcePlayer.isPrepared) { sourcePlayer.time = Position; sourcePlayer.Play(); }
            Playing = true;
        }
        public void Pause() { Playing = false; if (robotPlayer) robotPlayer.Pause(); if (sourcePlayer) sourcePlayer.Pause(); }
        public void Seek(double time)
        {
            Pause(); time = Math.Max(0, Math.Min(Duration, time));
            if (robotPlayer.isPrepared && robotPlayer.canSetTime) robotPlayer.time = time;
            if (!Fallback && sourcePlayer.isPrepared && sourcePlayer.canSetTime) sourcePlayer.time = time;
        }
        public void SeekError(int index)
        {
            if (Fallback || result?.surgery?.available != true || index < 0 || index >= result.surgery.guardrailViolations.Length) return;
            if (RunResultContract.TryClipTime(result, result.surgery.guardrailViolations[index], out var seconds) && seconds <= Duration) Seek(seconds);
        }
        void Error(VideoPlayer player, string message)
        {
            PlaybackError = player == sourcePlayer ? "Source recording could not play; robot replay remains separate." : "Learner replay could not play; showing the synthetic sample.";
            if (player == robotPlayer && !Fallback) { Pause(); prepared = false; prepareStarted = Time.realtimeSinceStartup; LoadSample(); }
            else if (player == robotPlayer) PlaybackError = "Sample playback unavailable. Continue to your learning scores.";
            panel.Refresh();
        }
        void Update()
        {
            if (!prepared && Time.realtimeSinceStartup - prepareStarted > 20 && string.IsNullOrEmpty(PlaybackError)) Error(robotPlayer, "Prepare timeout");
            if (Playing && Position >= Duration - .04) Pause();
            if (Playing && !Fallback && sourcePlayer.isPrepared && Math.Abs(sourcePlayer.time - Position) > .25 && sourcePlayer.canSetTime) sourcePlayer.time = Position;
        }
        void OnApplicationPause(bool paused) { if (paused) Pause(); }
        void OnApplicationFocus(bool focused) { if (!focused) Pause(); }
        void OnDestroy()
        {
            foreach (var player in new[] { sourcePlayer, robotPlayer }) if (player) { player.Stop(); if (player.targetTexture) player.targetTexture.Release(); }
        }
    }
}
