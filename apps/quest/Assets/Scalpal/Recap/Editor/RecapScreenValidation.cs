using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Recap.Editor
{
    // The learner asked for a recap with nothing extra: two scores, the robot replay with at most one result
    // line (plus the synthetic-provenance line when it applies), and the two ways out. This reads every visible
    // text on the actual RunEnding scene in each robot state, so any added debug line, id or path fails here.
    public static class RecapScreenValidation
    {
        static int checks;
        const string Mistake = "• Off-target contact";
        static void Check(bool pass, string message) { checks++; if (!pass) throw new InvalidOperationException("Recap screen: " + message); }
        const string Ready = "{\"status\":\"ready\",\"stepId\":\"mark_incision\",\"stepTitle\":\"Mark McBurney incision\",\"success\":true,\"pathErrorMm\":6.2,\"policySuccessRate\":0.8,\"demos\":{\"human\":0,\"synthetic\":40},\"synthetic\":true,\"videoUrl\":\"/robot/replays/session-a.mp4\",\"demoId\":\"demo-abcdef12\",\"details\":{\"rollouts\":5}}";
        const string Missed = "{\"status\":\"ready\",\"stepId\":\"mark_incision\",\"stepTitle\":\"Mark McBurney incision\",\"success\":false,\"pathErrorMm\":24.4,\"policySuccessRate\":0.2,\"demos\":{\"human\":1,\"synthetic\":0},\"synthetic\":false,\"videoUrl\":\"/robot/replays/session-b.mp4\"}";
        const string Mixed = "{\"status\":\"ready\",\"stepId\":\"mark_incision\",\"stepTitle\":\"Mark McBurney incision\",\"success\":true,\"pathErrorMm\":null,\"policySuccessRate\":null,\"demos\":{\"human\":1,\"synthetic\":40},\"synthetic\":true,\"videoUrl\":null}";
        const string PendingReply = "{\"status\":\"pending\",\"stepId\":\"mark_incision\",\"stepTitle\":\"Mark McBurney incision\",\"success\":null,\"pathErrorMm\":null,\"demos\":{\"human\":0,\"synthetic\":0},\"synthetic\":true,\"videoUrl\":null,\"pendingDemoId\":\"demo-abcdef12\"}";
        const string UnavailableReply = "{\"status\":\"unavailable\",\"stepId\":\"mark_incision\",\"stepTitle\":\"Mark McBurney incision\",\"success\":null,\"pathErrorMm\":null,\"demos\":{\"human\":0,\"synthetic\":0},\"synthetic\":true,\"videoUrl\":null}";

        public static void Run()
        {
            checks = 0;
            EditorSceneManager.OpenScene(RecapBuild.ScenePath, OpenSceneMode.Single);
            var c = UnityEngine.Object.FindFirstObjectByType<RecapController>();
            Check(c && c.panel && c.replay && c.replay.robotPlayer && c.replay.robotPlayer.targetTexture && c.replay.controller == c, "scene bindings: controller, panel, one robot video");
            Check(UnityEngine.Object.FindObjectsByType<UnityEngine.Video.VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 1, "exactly one video: the robot replay");
            Check(!UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(), "no legacy TextMesh");
            Check(!c.replay.robotPlayer.playOnAwake && c.replay.robotPlayer.isLooping, "replay loops and only plays once the coach has one");
            // Edit mode never runs Awake, so read the exact run context this controller consults.
            c.Load(null);
            var context = (RecapRunContext)typeof(RecapController).GetField("context", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(c);
            string savedSession = context.coachSessionId, savedUrl = context.voiceServiceUrl;
            var savedResult = context.result;
            try
            {
                var r = Fixture(); r.surgery.demoAssisted = false;
                context.coachSessionId = ""; context.voiceServiceUrl = "http://localhost:8787";
                c.Load(null);
                Expect(c, "no result", RecapPanel.Empty, "Choose another patient");

                // Pending: one quiet line, no video, no provenance.
                c.Load(r); c.ApplyRobot(PendingReply);
                Check(c.Robot == RecapController.RobotState.Pending, "pending reply keeps polling state");
                Expect(c, "pending", "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, RecapPanel.Pending, "Choose another patient", "Retry surgery");
                Check(!c.panel.videoSurface.activeInHierarchy, "no video while the robot is learning");
                // Pending that never resolves times out to unavailable, never to a guessed result.
                c.RobotTimedOut();
                Expect(c, "timed out", "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, RecapPanel.Unavailable, "Choose another patient", "Retry surgery");

                // Ready on synthetic demos: the result line, the provenance line and the streamed video from the coach.
                c.Load(r); c.ApplyRobot(Ready);
                Expect(c, "ready synthetic", "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, "Robot · Mark McBurney incision · Success · 6 mm",
                    "Learned from 40 demos (synthetic)", "Choose another patient", "Retry surgery");
                // Android's player refuses cleartext HTTP: the replay downloads to the cache first, then plays the local file.
                Check(c.replay.Url == "http://localhost:8787/robot/replays/session-a.mp4" && c.replay.Downloading && string.IsNullOrEmpty(c.replay.robotPlayer.url) && c.panel.videoSurface.activeInHierarchy,
                    "replay downloads from the coach origin plus the coach-relative videoUrl before playing: " + c.replay.Url);
                string local = RecapVideo.LocalPath(c.replay.Url);
                Check(local.StartsWith(Application.temporaryCachePath, StringComparison.Ordinal) && local.EndsWith("robot-replay-session-a.mp4", StringComparison.Ordinal), "replay cache path: " + local);
                c.replay.PlayLocal(local);
                Check(!c.replay.Downloading && c.replay.robotPlayer.url == "file://" + local && c.replay.Showing, "the downloaded replay plays from the local file: " + c.replay.robotPlayer.url);
                // A player error drops to the unavailable line; the score panels stay.
                typeof(RecapVideo).GetMethod("Error", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(c.replay, new object[] { c.replay.robotPlayer, "validation decode error" });
                Expect(c, "replay error", "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, RecapPanel.Unavailable, "Choose another patient", "Retry surgery");
                Check(!c.panel.videoSurface.activeInHierarchy, "a failed replay hides the video");

                // Ready on headset demos only: no provenance line, missed rounds the error.
                c.Load(r); c.ApplyRobot(Missed);
                Expect(c, "ready missed", "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, "Robot · Mark McBurney incision · Missed · 24 mm", "Choose another patient", "Retry surgery");
                // Mixed demos never read as all-human; an unmeasured error is omitted, not shown as 0 mm.
                c.Load(r); c.ApplyRobot(Mixed);
                Expect(c, "ready mixed", "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, "Robot · Mark McBurney incision · Success",
                    "Learned from 1 headset + 40 synthetic demos", "Choose another patient", "Retry surgery");
                Check(!c.panel.videoSurface.activeInHierarchy && string.IsNullOrEmpty(c.replay.Url), "no videoUrl, no video");

                // Unavailable, malformed and unsafe replies.
                foreach (var (label, json) in new[] { ("unavailable", UnavailableReply), ("malformed", "{\"status\":"), ("unknown status", "{\"status\":\"maybe\"}") })
                {
                    c.Load(r); c.ApplyRobot(json);
                    Check(c.Robot == RecapController.RobotState.Unavailable, label + " reply is unavailable");
                    Expect(c, label, "Diagnosis", "82 / 100", "Surgery", "76 / 100", Mistake, RecapPanel.Unavailable, "Choose another patient", "Retry surgery");
                }
                c.Load(r); c.ApplyRobot(Ready.Replace("/robot/replays/session-a.mp4", "http://elsewhere.invalid/x.mp4"));
                Check(string.IsNullOrEmpty(c.replay.Url) && !c.panel.videoSurface.activeInHierarchy, "an absolute or foreign videoUrl is never streamed");
                Check(!RecapController.VideoPathValid("/robot/replays/../secret.mp4") && RecapController.VideoPathValid("/robot/replays/x.mp4"), "replay path guard");

                // No coach session (captions-only run): unavailable at once, no poll.
                c.Load(r);
                Check(c.Robot == RecapController.RobotState.Unavailable, "no coach session means no robot result");
                // With a coach session the panel starts pending (the poll's first request goes to a closed port here).
                context.coachSessionId = "coach-validation"; context.voiceServiceUrl = "http://127.0.0.1:9";
                c.Load(r);
                Check(c.Robot == RecapController.RobotState.Pending && c.panel.robotLine.text == RecapPanel.Pending, "a coach session starts as Robot learning…");
                context.coachSessionId = ""; c.Load(null);

                // Scores: diagnosis Skipped / Unavailable, surgery Unavailable and assisted.
                var skipped = Fixture(); skipped.diagnosisSkipped = true; skipped.diagnosisAvailable = false; skipped.diagnosis = null;
                skipped.surgery.available = false; skipped.surgery.demoAssisted = true;
                c.Load(skipped); c.ApplyRobot(UnavailableReply);
                Expect(c, "skipped", "Diagnosis", "Skipped", "Surgery · assisted", "Unavailable", RecapPanel.Unavailable, "Choose another patient", "Retry surgery");
                // Mistake lines: from the surgery grade's guardrails, at most four, "No mistakes" for a clean run.
                var clean = Fixture(); clean.surgery.demoAssisted = false; clean.surgery.guardrailViolations = new TimedFact[0];
                c.Load(clean); c.ApplyRobot(UnavailableReply);
                Expect(c, "no mistakes", "Diagnosis", "82 / 100", "Surgery", "76 / 100", RecapPanel.NoMistakes, RecapPanel.Unavailable, "Choose another patient", "Retry surgery");
                var messy = Fixture(); messy.surgery.demoAssisted = false;
                messy.surgery.guardrailViolations = new[] { "Cut before clamping the appendicular artery. Clamp first.", "contact_bowel", "Grasped the ileum", "Cautery on the cecum", "Stapled the ileum" }
                    .Select((label, i) => new TimedFact { id = "g" + i, label = label, atSeconds = i }).ToArray();
                c.Load(messy); c.ApplyRobot(UnavailableReply);
                Expect(c, "many mistakes", "Diagnosis", "82 / 100", "Surgery", "76 / 100", "• Cut before clamping the appendicular artery", "• Contact bowel", "• Grasped the ileum", "+2 more",
                    RecapPanel.Unavailable, "Choose another patient", "Retry surgery");
                var missing = Fixture(); missing.diagnosisAvailable = false; missing.diagnosis = null;
                Check(RecapPanel.Clinical(missing) == "Unavailable", "missing diagnosis is Unavailable, not zero");

                ValidateBrandAndPointer(c);
            }
            finally
            {
                c.Load(null);
                context.coachSessionId = savedSession; context.voiceServiceUrl = savedUrl; context.result = savedResult;
            }
            Debug.Log("SCALPAL_RECAP_SCREEN_OK checks=" + checks + " states=pending,ready,unavailable,timeout,replayError,noResult,mistakes video=download-then-file headset=false decoder=false");
        }
        static RunResult Fixture() => RunResultContract.Parse(File.ReadAllText(RecapBuild.Root + "/Fixtures/sample-run-result.json"));

        // Every visible, non-empty text on the recap, in any order, must be exactly the allowed list.
        static void Expect(RecapController c, string state, params string[] allowed)
        {
            c.panel.Refresh();
            var visible = UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(t => t.enabled && t.gameObject.activeInHierarchy && !string.IsNullOrWhiteSpace(t.text)).Select(t => t.text.Trim()).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var expected = allowed.OrderBy(t => t, StringComparer.Ordinal).ToArray();
            Check(visible.SequenceEqual(expected), state + ": visible texts [" + string.Join(" | ", visible) + "] must be exactly [" + string.Join(" | ", expected) + "]");
            Check(visible.Count(t => t.StartsWith("Robot", StringComparison.Ordinal)) <= 1, state + ": at most one robot line");
            foreach (var text in visible)
                Check(!text.Contains("http") && !text.Contains("/robot") && !text.Contains(".mp4") && !text.Contains("coach-") && !text.Contains("demo-") && !text.Contains("_") && text.Split('\n').Length == 1,
                    state + ": no ids, paths or multi-line debug copy: " + text);
        }

        // Brand SDF type at readable sizes, and the aim-pose ray focusing a recap button with the grey accent.
        static void ValidateBrandAndPointer(RecapController c)
        {
            var brand = Scalpal.Brand.ScalpalBrand.Active;
            c.Load(Fixture()); c.ApplyRobot(Ready);
            Check(c.panel.diagnosisValue.font == brand.display && c.panel.surgeryValue.font == brand.display && c.panel.diagnosisLabel.font == brand.label
                && c.panel.exploreButton.label.font == brand.label && c.panel.robotLine.font == brand.body && c.panel.robotNote.font == brand.body,
                "scores are Instrument Serif; labels and buttons Geist Mono Medium; robot lines Geist Mono");
            var measured = Scalpal.Brand.ScalpalBrandLayout.Measure(c.panel.transform, RecapBuild.Viewer);
            foreach (var item in measured) Check(item.Passes, "recap text meets its floor: " + item.fit.name + " " + item.mmAt1m.ToString("F1"));
            var rig = UnityEngine.Object.FindFirstObjectByType<RecapInput>();
            Check(rig && rig.origin && rig.head && rig.left && rig.right, "headset rig bound");
            var button = c.panel.exploreButton; string action = button.action;
            try
            {
                button.action = "validation-inert";
                for (int hand = 0; hand < 2; hand++)
                {
                    var result = Scalpal.Brand.Editor.ScalpalPointerProbe.Press(hand, rig.origin, button.GetComponent<Collider>(), () => rig.Pointer(hand), rig.StepPointers);
                    Check(result.RayMatchesAim && result.rayVisible && result.hovered && result.accentOnHover, "recap ray from the " + (hand == 0 ? "left" : "right") + " controller focuses a button with the grey accent");
                }
            }
            finally { button.action = action; }
        }
    }
}
