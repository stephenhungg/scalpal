using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    public static class RecapReviewValidation
    {
        static readonly List<string> failures = new List<string>();
        static RunResult Fixture() => RunResultContract.Parse(File.ReadAllText(RecapBuild.Root + "/Fixtures/sample-run-result.json"));
        static void Test(string name, Action action)
        {
            try { action(); Debug.Log("RECAP_REVIEW_PASS " + name); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); Debug.LogError("RECAP_REVIEW_FAIL " + name + ": " + e.Message); }
        }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        public static void All()
        {
            Run(); MediaRed(); RecapVideoReviewValidation.Run(); RecapValidation.Run(); RecapIntegrationValidation.Run();
            Debug.Log("SCALPAL_RECAP_ALL_REVIEW_OK");
        }
        public static void MediaRed()
        {
            failures.Clear();
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene(RecapBuild.ScenePath);
            var c = UnityEngine.Object.FindFirstObjectByType<RecapController>();
            Test("renewed URL cannot replace current media", () =>
            {
                var r = Fixture(); r.isSample = false; r.replay.source = "learner"; r.replay.status = "ready";
                r.replay.jobId = "job-observed"; r.replay.sourceArtifactId = "art-source"; r.replay.replayArtifactId = "art-render";
                r.replay.replayVideoUrl = "http://localhost:1/files/replay.mp4?m=GET&exp=100&sig=first";
                c.replay.Bind(r); string original = c.replay.robotPlayer.url;
                r.replay.replayVideoUrl = "http://localhost:1/files/replay.mp4?m=GET&exp=200&sig=renewed";
                c.replay.Bind(r);
                Require(c.replay.robotPlayer.url == original, "Rotated capability replaced media and reset preparation");
            });
            Test("shipping scene cannot substitute sample scores", () =>
            {
                var context = RecapRunContext.Ensure(); context.result = null;
                typeof(RecapController).GetMethod("Start", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, null);
                Require(c.Result == null, "No result silently became sample scorecards");
            });
            Test("terminal replay stops status polling", () =>
            {
                var r = Fixture(); r.replay.status = "ready"; r.replay.replayVideoUrl = "http://localhost:1/replay.mp4";
                typeof(RecapController).GetProperty("Result").GetSetMethod(true).Invoke(c, new object[] { r });
                var poll = (System.Collections.IEnumerator)typeof(RecapController).GetMethod("Poll", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(c, new object[] { 0 });
                Require(!poll.MoveNext(), "Ready media continued status polling");
                Require(RecapController.PollDelay(0) == 3 && RecapController.PollDelay(10) == 10, "Processing poll backoff missing");
            });
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
            Debug.Log("SCALPAL_RECAP_MEDIA_REGRESSION_OK");
        }
        public static void Run()
        {
            failures.Clear();
            Test("unavailable diagnosis survives Unity export and import", () =>
            {
                var r = Fixture(); r.diagnosis = null; r.diagnosisAvailable = false;
                var go = new GameObject("Missing diagnosis regression");
                try { var context = go.AddComponent<RecapRunContext>(); context.Begin(r); var roundtrip = RunResultContract.Parse(context.ExportResultJson()); Require(RecapPanel.Clinical(roundtrip).Contains("Not available"), "Missing score displayed as a score"); }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            });
            Test("Begin preserves recorded demo flags", () =>
            {
                var go = new GameObject("Demo snapshot regression");
                try { var context = go.AddComponent<RecapRunContext>(); var r = Fixture(); r.demo = DemoFlags.JudgePath(true); context.Begin(r); Require(context.result.demo.skipMarking, "Begin overwrote producer flags"); }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            });
            Test("completed run cannot change demo assistance", () =>
            {
                var go = new GameObject("Demo freeze regression");
                try { var context = go.AddComponent<RecapRunContext>(); context.SetDemoMode(true); context.Begin(Fixture()); context.SetDemoMode(false); Require(context.result.demo.enabled && context.result.demo.skipMarking, "Recap changed recorded run assistance"); }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            });
            Test("Begin rejects reused attempt", () =>
            {
                var go = new GameObject("Retry identity regression");
                try { var context = go.AddComponent<RecapRunContext>(); context.Begin(Fixture()); bool rejected = false; try { context.Begin(Fixture()); } catch (ArgumentException) { rejected = true; } Require(rejected, "Old attempt accepted again"); }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            });
            Test("export strips capability URLs", () =>
            {
                var go = new GameObject("Durable export regression");
                try { var context = go.AddComponent<RecapRunContext>(); var r = Fixture(); r.replay.sourceVideoUrl = "http://localhost:8788/files/clip.mp4?m=GET&exp=1&sig=secret"; context.Begin(r); Require(!context.ExportResultJson().Contains("sig=secret"), "Capability leaked into export"); }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            });
            if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
            Debug.Log("SCALPAL_RECAP_REVIEW_VERIFY_OK");
        }
    }
}
