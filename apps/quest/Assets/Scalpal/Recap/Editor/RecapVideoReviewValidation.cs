using System;
using System.IO;
using System.Reflection;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    public static class RecapVideoReviewValidation
    {
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        public static void Run()
        {
            EditorSceneManager.OpenScene(RecapBuild.ScenePath);
            var video = UnityEngine.Object.FindFirstObjectByType<RecapVideo>();
            var r = RunResultContract.Parse(File.ReadAllText(RecapBuild.Root + "/Fixtures/sample-run-result.json"));
            r.isSample = false; r.replay.status = "ready"; r.replay.source = "learner";
            r.replay.jobId = "video-review-job"; r.replay.jobRun = 1;
            r.replay.sourceArtifactId = "source-review-artifact"; r.replay.replayArtifactId = "robot-review-artifact";
            r.replay.durationSeconds = 180; r.replay.captureStartRunSeconds = 10; r.replay.clockAligned = true;
            r.replay.replayVideoUrl = "http://localhost:1/replay.mp4?signature=first";
            r.replay.sourceVideoUrl = "http://localhost:1/source.mp4?signature=first";
            r.demo = DemoFlags.JudgePath(true);
            r.surgery.guardrailViolations = new[] { new TimedFact { id = "guardrail", label = "contact", atSeconds = 67 } };
            video.Bind(r);
            Require(video.Duration == 180 && video.WindowStart == 54 && video.WindowEnd == 74, "Highlight must use full duration and logged error offset");
            video.Seek(62);
            r.replay.replayVideoUrl = "http://localhost:1/replay.mp4?signature=renewed";
            video.Bind(r);
            Require(video.robotPlayer.url.EndsWith("signature=first") && video.Position == 62, "Poll replaced capability or position");
            typeof(RecapVideo).GetProperty("Playing").GetSetMethod(true).Invoke(video, new object[] { true });
            video.RefreshAccess(r);
            Require(video.robotPlayer.url.EndsWith("signature=renewed") && video.Position == 62, "Explicit refresh did not retain position");
            Require((bool)typeof(RecapVideo).GetField("resumeAfterPrepare", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(video), "Refresh lost play intent");
            video.SetHighlight(false);
            Require(r.demo.enabled && video.Duration == 180 && video.WindowStart == 0 && video.WindowEnd == 180, "Local highlight changed recorded flags or full duration");
            int requests = 0;
            video.AccessRefreshRequested += () => requests++;
            typeof(RecapVideo).GetMethod("Error", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(video, new object[] { video.sourcePlayer, "Expired capability" });
            Require(video.SourceUnavailable && requests == 1, "Source error failed to hide image and request renewal");
            video.RefreshAccess(r);
            typeof(RecapVideo).GetMethod("Error", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(video, new object[] { video.sourcePlayer, "Still unavailable" });
            Require(requests == 1 && video.Fallback, "Persistent source failure caused an unbounded access refresh loop");
            r.attemptId = "new-attempt"; r.replay.replayVideoUrl = "http://localhost:1/new-attempt.mp4";
            video.Bind(r);
            Require(video.robotPlayer.url.EndsWith("new-attempt.mp4") && video.Position == 54, "New attempt did not reset to its event highlight");
            r.surgery.orderDeviations = new[] { new TimedFact { id = "late-order", label = "late event", atSeconds = 140 } };
            video.SeekError(1);
            Require(video.Position == 130 && !video.HighlightEnabled && r.demo.enabled, "Marker outside highlight did not seek its actual timestamp");
            Debug.Log("SCALPAL_RECAP_VIDEO_REVIEW_OK identity=true rotation=true refreshPosition=true playIntent=true sourceError=true eventWindow=true attemptReset=true boundedRefresh=true markerSeek=true headset=false decoder=false");
        }
    }
}
