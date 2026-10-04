// Loads the offline bundle exactly as Unity would see it and proves, in C#, that every case is
// completable, every action route resolves, and every referenced mesh/prefab is in the case payload.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Exercises.Generated;
using Scalpal.Hands;

static class Program
{
    static readonly List<string> Failures = new List<string>();

    static void Check(bool ok, string message)
    {
        if (!ok) Failures.Add(message);
    }

    static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : "../../apps/quest/Assets/Scalpal/Exercises/Resources/scalpal_bundle.json";
        // Case-sensitive public fields, like JsonUtility.
        var options = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = false };
        var bundle = JsonSerializer.Deserialize<ScalpalBundle>(File.ReadAllText(path), options);
        Check(bundle?.cases != null && bundle.cases.Length > 0, "bundle has no cases");
        if (bundle?.cases == null) return Report();

        var playable = 0;
        foreach (var kase in bundle.cases)
        {
            var who = $"{kase.patientId} ({kase.status})";
            Check(kase.actions != null && kase.actions.Length > 0, $"{who}: case has no actions");

            if (string.IsNullOrEmpty(kase.procedureId))
            {
                Check(kase.status == "blocked" || kase.status == "retry", $"{who}: no procedure but status is {kase.status}");
                continue;
            }
            playable++;

            var anatomy = new HashSet<string>(kase.anatomy.Select(a => a.id));
            foreach (var s in kase.procedure.structures) Check(anatomy.Contains(s), $"{who}: structure {s} missing from case anatomy");
            var instruments = new HashSet<string>(kase.instruments.Select(i => i.id));
            foreach (var step in kase.procedure.steps) Check(instruments.Contains(step.instrumentId), $"{who}: instrument {step.instrumentId} missing from case");

            var runner = new CaseRunner(kase.procedure);
            var started = new List<string>();
            var completed = false;
            runner.StepStarted += s => started.Add(s.id);
            runner.CaseCompleted += () => completed = true;
            runner.Begin();
            var guard = 0;
            while (!runner.Completed && guard++ < 100)
            {
                var step = runner.Current;
                var advanced = false;
                foreach (var e in CaseRunner.PerfectEvents(step)) advanced |= runner.Handle(e).advanced;
                Check(advanced, $"{who}: step {step.id} did not advance on its perfect events");
                if (!advanced) break;
            }
            Check(completed, $"{who}: case never completed");
            Check(started.SequenceEqual(kase.procedure.steps.Select(s => s.id)), $"{who}: steps ran out of order");
            Check(runner.Mistakes.Count == 0, $"{who}: perfect play recorded mistakes");

            var perfect = PreopScorer.Score(kase, kase.brief.flags.Select(f => f.type).ToArray());
            Check(perfect.passed && perfect.score == perfect.total, $"{who}: perfect pre-op check did not pass");
            var none = PreopScorer.Score(kase, new string[0]);
            Check(none.passed == (kase.brief.flags.Length == 0), $"{who}: empty pre-op check scored wrong");
        }

        VerifyOpenBody(bundle);

        foreach (var action in AllActions(bundle))
        {
            Check(ScalpalRoutes.Resolve(action.method, action.route) != RouteKind.Unknown, $"action {action.method} {action.route} has no Unity route");
        }

        Console.WriteLine($"{bundle.cases.Length} cases, {playable} playable, every step completed in C#.");
        CheckHandMath(args.Length > 1 ? args[1] : "../hands/tests/golden.json");
        return Report();
    }

    // BlazeHandMath must reproduce services/hands/reference.py, which is checked against Google's MediaPipe.
    static void CheckHandMath(string goldenPath)
    {
        if (!File.Exists(goldenPath))
        {
            Failures.Add($"hand golden file missing: {goldenPath}");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(goldenPath));
        var g = doc.RootElement;
        float F(JsonElement e) => (float)e.GetDouble();
        float[] Arr(JsonElement e) => e.EnumerateArray().Select(F).ToArray();
        bool Near(float a, float b) => Math.Abs(a - b) <= 1e-3f * Math.Max(1f, Math.Abs(b));
        void Affine(string what, Affine2 m, float[] expected)
        {
            var got = new[] { m.a, m.b, m.c, m.d, m.e, m.f };
            for (var i = 0; i < 6; i++) Check(Near(got[i], expected[i]), $"hand math {what}[{i}]: {got[i]} vs {expected[i]}");
        }
        void Roi(string what, HandRoi r, JsonElement e)
        {
            Check(Near(r.centerX, F(e.GetProperty("centerX"))) && Near(r.centerY, F(e.GetProperty("centerY"))), $"hand math {what} centre");
            Check(Near(r.size, F(e.GetProperty("size"))), $"hand math {what} size: {r.size} vs {F(e.GetProperty("size"))}");
            Check(Near(r.rotation, F(e.GetProperty("rotation"))), $"hand math {what} rotation: {r.rotation} vs {F(e.GetProperty("rotation"))}");
        }

        var width = g.GetProperty("image").GetProperty("width").GetInt32();
        var height = g.GetProperty("image").GetProperty("height").GetInt32();
        var toImage = BlazeHandMath.DetectorToImage(width, height);
        Affine("DetectorToImage", toImage, Arr(g.GetProperty("detectorToImage")));

        var det = g.GetProperty("detection");
        var index = det.GetProperty("index").GetInt32();
        var boxes = new float[BlazeHandMath.NumAnchors * 18];
        Array.Copy(Arr(det.GetProperty("box")), 0, boxes, index * 18, 18);
        var anchors = new float[BlazeHandMath.NumAnchors, 4];
        var anchor = Arr(det.GetProperty("anchor"));
        for (var k = 0; k < 4; k++) anchors[index, k] = anchor[k];
        var roi = BlazeHandMath.DetectionToRoi(boxes, index, anchors);
        Roi("DetectionToRoi", roi, det.GetProperty("roi"));
        Affine("LandmarkerToImage(detection)", BlazeHandMath.LandmarkerToImage(toImage, roi), Arr(g.GetProperty("landmarkerToImage")));

        var tracking = g.GetProperty("tracking");
        var tracked = BlazeHandMath.RoiFromLandmarks(Arr(tracking.GetProperty("xTopLeft")), Arr(tracking.GetProperty("yTopLeft")));
        Roi("RoiFromLandmarks", tracked, tracking.GetProperty("roi"));
        Affine("LandmarkerToImage(tracking)", BlazeHandMath.LandmarkerToImage(BlazeHandMath.TopLeftToImage(height), tracked), Arr(tracking.GetProperty("landmarkerToImage")));

        var scores = new float[BlazeHandMath.NumAnchors];
        for (var i = 0; i < scores.Length; i++) scores[i] = -10f;
        scores[index] = 5f;
        var rois = new HandRoi[2];
        var roiScores = new float[2];
        var count = BlazeHandMath.SelectDetections(boxes, scores, anchors, 0.5f, 2, rois, roiScores);
        Check(count == 1 && Near(rois[0].centerX, roi.centerX), $"hand math SelectDetections found {count} hands");
        Console.WriteLine("hand math matches the MediaPipe-checked Python reference.");
    }

    static void VerifyOpenBody(ScalpalBundle bundle)
    {
        var procedure = bundle.procedures.FirstOrDefault(p => p.openBody != null);
        Check(procedure != null, "open body procedure is present in offline bundle");
        if (procedure == null) return;
        BodyAction Action(string id, string verb, string tissue, string instrument, double time = 0, float distance = 0)
            => new BodyAction { actionId = id, verb = verb, tissueId = tissue, layer = tissue,
                instrumentId = instrument, instrumentInstanceId = id + "-tool", registered = true,
                timeMs = time, distanceMm = distance, lengthMm = verb == "cut" ? 4 : 0 };
        void Expose(CaseRunner runner)
        {
            foreach (var step in procedure.steps.Take(5))
                foreach (var e in CaseRunner.PerfectEvents(step)) runner.Handle(e);
        }
        var body = new CaseRunner(procedure);
        int closeCallbacks = 0;
        body.StepCompleted += step => { if (step.id == "close") closeCallbacks++; };
        body.Handle(CaseEvent.Surgery(Action("close-early", "close", "skin", "assistant")));
        Check(body.Achieved.Contains("close") && body.Current.id == "mark_incision" && closeCallbacks == 1,
            "out-of-order closure milestone is recognized without gating earlier guidance");
        Check(body.OrderDeviations.Contains("close"), "out-of-order milestone recorded");
        body.Handle(CaseEvent.Surgery(Action("close-again", "close", "skin", "assistant")));
        Check(closeCallbacks == 1, "milestone callback emits only once");
        Check(!body.Body.Test(new BodyPredicate { tissueId = "appendix", fact = "stumpLengthMm", op = "lte", value = 5 }),
            "missing stump measurement never passes a threshold");
        var hidden = body.Handle(CaseEvent.Surgery(Action("hidden", "cut", "terminal_ileum", "scalpel")));
        Check(body.Body.Get("", "contamination") == 0 && body.Body.Log.Last().outcomes.Contains("not_exposed"),
            "unexposed organ is physically blocked without invented injury");
        Expose(body);
        var injury = body.Handle(CaseEvent.Surgery(Action("bowel", "cut", "terminal_ileum", "scalpel")));
        Check(body.Body.Get("", "contamination") == 1 && injury.mistake != null,
            "off-path bowel cut contaminates and raises a guardrail");
        body.Handle(CaseEvent.Surgery(Action("bleed", "cut", "mesoappendix", "scalpel", 1000)));
        Check(body.Body.Get("", "activeBleeds") == 1 && body.Mistakes.Any(m => m.id == "cut_before_control"),
            "cut before clamp produces bleeding and a guardrail");
        body.Handle(CaseEvent.Surgery(Action("clamp", "clamp", "mesoappendix", "hemostat", 2000)));
        Check(body.Body.Get("", "activeBleeds") == 0 && body.Body.Get("", "bloodLostMl") == 2,
            "clamp stops bleeding after time-integrated blood loss");
        body.Handle(CaseEvent.Surgery(Action("bleed-again", "cut", "appendicular_artery", "scalpel", 3000)));
        var tied = Action("tie", "tie", "appendicular_artery", "suture_tie", 4000);
        body.Handle(CaseEvent.Surgery(tied));
        Check(body.Body.Get("", "activeBleeds") == 0, "tie stops a separate bleeding vessel");
        int count = body.Body.Log.Count;
        body.Handle(CaseEvent.Surgery(tied));
        Check(body.Body.Log.Count == count, "duplicate actions cannot apply twice");
        tied.position.x = 900;
        Check(body.Body.Log.Last().action.position.x == 0, "action history snapshots submitted measurements");
        var dummy = new Procedure { id = "dummy", steps = procedure.steps, firstStep = procedure.firstStep,
            openBody = new OpenBodyCase { version = 1, tissues = procedure.openBody.tissues, milestones = procedure.openBody.milestones,
                guardrails = Array.Empty<BodyGuardrail>(), decisions = Array.Empty<BodyDecision>() } };
        var second = new CaseRunner(dummy);
        Expose(second);
        second.Handle(CaseEvent.Surgery(Action("same-bowel", "cut", "terminal_ileum", "scalpel")));
        Check(second.Body.Get("", "contamination") == 1 && second.Mistakes.Count == 0,
            "same verb and tissue consequences hold under a second case with different guardrails");
        var baseRunner = new CaseRunner(procedure);
        Expose(baseRunner);
        foreach (var e in CaseRunner.PerfectEvents(procedure.steps.First(s => s.id == "ligate_base"))) baseRunner.Handle(e);
        Check(baseRunner.Achieved.Contains("ligate_base") && baseRunner.Body.Get("appendix", "stumpLengthMm") == 4,
            "base decision and short tied stump milestone can complete before delivery guidance");
        Check(baseRunner.Body.Get("appendix", "decision_true_base") == 1, "true-base decision persists in body facts");
    }

    static IEnumerable<ScalpalAction> AllActions(ScalpalBundle b) =>
        (b.actions ?? new ScalpalAction[0])
            .Concat(b.patients.SelectMany(p => p.actions))
            .Concat(b.cases.SelectMany(c => c.actions))
            .Concat(b.cases.Select(c => c.brief).Where(br => br.actions != null).SelectMany(br => br.actions));

    static int Report()
    {
        if (Failures.Count == 0)
        {
            Console.WriteLine("unity-check ok");
            return 0;
        }
        foreach (var f in Failures) Console.Error.WriteLine($"FAIL {f}");
        return 1;
    }
}
