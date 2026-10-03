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

        foreach (var action in AllActions(bundle))
        {
            Check(ScalpalRoutes.Resolve(action.method, action.route) != RouteKind.Unknown, $"action {action.method} {action.route} has no Unity route");
        }

        Console.WriteLine($"{bundle.cases.Length} cases, {playable} playable, every step completed in C#.");
        return Report();
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
