// Offline pre-op safety check scoring. Mirrors scorePreopCheck in services/preop/src/case-builder.ts
// so the headset can score the checklist without the service.
using System.Collections.Generic;
using System.Linq;
using Scalpal.Exercises.Data;

namespace Scalpal.Exercises.Engine
{
    public static class PreopScorer
    {
        public static PreopCheckResult Score(SurgicalCase kase, string[] selected)
        {
            var present = new HashSet<string>(kase.brief.flags.Select(f => f.type));
            var offered = kase.checklistOptions.ToDictionary(o => o.type);
            var picked = new HashSet<string>((selected ?? new string[0]).Where(offered.ContainsKey));
            ChecklistOption Option(string t) => offered.TryGetValue(t, out var o) ? o : new ChecklistOption { type = t, label = t };

            var caught = present.Where(picked.Contains).Select(Option).ToArray();
            var missed = present.Where(t => !picked.Contains(t)).Select(Option).ToArray();
            var falseAlarms = picked.Where(t => !present.Contains(t)).Select(Option).ToArray();
            var feedback = missed
                .Select(m => $"Missed: {m.label}. {kase.brief.flags.FirstOrDefault(f => f.type == m.type)?.detail ?? ""}".Trim())
                .Concat(falseAlarms.Select(f => $"Not in this chart: {f.label}."))
                .ToArray();
            var total = present.Count;
            var passed = missed.Length == 0 && falseAlarms.Length == 0;

            string say;
            if (total == 0) say = "This chart has no flagged risks. Good to proceed.";
            else if (passed) say = $"You caught all {total} risks. Let's scrub in.";
            else
            {
                say = $"You caught {caught.Length} of {total}.";
                if (missed.Length > 0) say += $" You missed {string.Join(", ", missed.Select(m => m.label.ToLowerInvariant()))}.";
                if (falseAlarms.Length > 0) say += falseAlarms.Length == 1 ? " One pick isn't in this chart." : " Some picks aren't in this chart.";
            }

            return new PreopCheckResult
            {
                patientId = kase.patientId,
                caseId = kase.caseId,
                caught = caught,
                missed = missed,
                falseAlarms = falseAlarms,
                score = caught.Length,
                total = total,
                passed = passed,
                feedback = feedback,
                say = say,
                actions = new[]
                {
                    new ScalpalAction { id = "view_procedure", label = "View procedure steps", method = "GET", route = $"/procedures/{kase.procedureId}" },
                    new ScalpalAction { id = "open_case", label = "Back to case", method = "GET", route = $"/patients/{kase.patientId}/case" },
                    new ScalpalAction { id = "choose_patient", label = "Choose another patient", method = "GET", route = "/patients" },
                },
            };
        }
    }
}
