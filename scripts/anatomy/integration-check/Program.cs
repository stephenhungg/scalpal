using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Exercises.Coach;
using Scalpal.Exercises.Preop;
using UnityEngine;

static class Program
{
    static int checks;
    static void Check(bool condition, string detail) { checks++; if (!condition) throw new Exception(detail); }
    static void Main(string[] args)
    {
        var bundle = JsonSerializer.Deserialize<ScalpalBundle>(File.ReadAllText(args.Length > 0 ? args[0] :
            "apps/quest/Assets/Scalpal/Exercises/Resources/scalpal_bundle.json"), new JsonSerializerOptions { IncludeFields = true });
        var selected = bundle.cases.Single(c => c.patientId == "patient-demo-multi-source");
        var root = new GameObject("anatomy");
        var anatomy = root.AddComponent<AnatomyController>();
        foreach (var id in selected.procedure.structures)
        {
            if (id == "cecum" || id == "terminal_ileum" || id == "abdominal_wall" || id == "umbilicus") continue;
            Add(root, id);
        }
        var exercise = root.AddComponent<AnatomyExerciseBinding>(); exercise.anatomy = anatomy;
        Check(!exercise.SelectCase(bundle, selected.caseId, false, out var reason), "needs_review requires explicit acknowledgement");
        Check(!exercise.SelectCase(bundle, selected.caseId, true, out reason) && reason.Contains("cecum") && reason.Contains("terminal_ileum"), "missing demo blockers reported");
        Add(root, "cecum"); Add(root, "terminal_ileum");
        Check(exercise.SelectCase(bundle, selected.caseId, true, out reason), "valid appendectomy selectable: " + reason);
        int handled = 0, started = 0; bool complete = false;
        exercise.EventHandled += (input, result) => handled++;
        exercise.StepStarted += step => started++;
        exercise.CaseCompleted += () => complete = true;
        Check(!exercise.Submit(CaseEvent.Confirm(), out _, out reason) && handled == 0, "invalid registration prevents UI scoring");
        anatomy.SetPreviewMode(true);
        anatomy.SetRegistrationValid(true);
        Check(!exercise.CanScore, "preview mode never scores even if registration valid");
        anatomy.SetPreviewMode(false);
        Check(exercise.CanScore, "valid practice can score");
        Check(!exercise.SelectInstrument("made_up"), "unknown tool rejected");
        Check(!exercise.Submit(CaseEvent.PlacePort("made_up"), out _, out reason), "unknown port rejected");
        Check(!exercise.Submit(CaseEvent.Identify("made_up"), out _, out reason), "unknown structure rejected");
        var foreign = new GameObject("foreign"); Add(foreign, "cecum");
        Check(!exercise.TouchCollider(foreign.GetComponentsInChildren<Collider>(true)[0], out _, out reason), "other anatomy rig collider rejected");
        var guard = 0;
        while (!exercise.Completed && guard++ < 30)
        {
            var step = exercise.Current;
            foreach (var input in CaseRunner.PerfectEvents(step))
            {
                anatomy.SetRegistrationValid(false);
                Check(!exercise.Submit(input, out _, out reason), "registration loss blocks " + step.id);
                anatomy.SetRegistrationValid(true);
                bool accepted; CaseResult result;
                if (input.type == CaseEventType.Touch)
                {
                    Check(exercise.SelectInstrument(input.instrumentId), "select authored instrument");
                    Check(anatomy.TryGetPart(input.id, out var part), "touch target exists");
                    accepted = exercise.TouchCollider(part.GetComponentsInChildren<Collider>(true)[0], out result, out reason);
                }
                else accepted = exercise.Submit(input, out result, out reason);
                Check(accepted && result.mistake == null, "valid authored input accepted: " + reason);
            }
        }
        Check(complete && exercise.Completed && started == selected.procedure.steps.Length - 1, "all appendectomy steps completed");
        Check(!exercise.Submit(CaseEvent.Confirm(), out _, out reason), "completed attempt rejects more scoring");
        Check(!exercise.SelectCase(bundle, "made_up", true, out reason) && exercise.Completed, "failed selection preserves prior attempt");
        var relay = root.AddComponent<CoachRelay>();
        exercise.coach = relay; exercise.requireCoachSynchronization = true;
        Check(exercise.SelectCase(bundle, selected.caseId, true, out reason), "live case selects while pending sync");
        Check(!exercise.CanScore, "disconnected live case pauses");
        relay.Connected = true; relay.IsSynchronized = true;
        relay.SessionPatientId = selected.patientId; relay.SessionProcedureId = "wrong"; relay.SessionInitialStepId = selected.procedure.firstStep;
        Check(!exercise.CanScore, "wrong coach procedure pauses");
        relay.SessionProcedureId = selected.procedureId;
        Check(exercise.CanScore, "matching fresh coach enables");
        Check(exercise.Submit(CaseEvent.Confirm(), out _, out reason) && relay.forwarded == 1, "live handled event forwarded exactly once");
        relay.SessionId = "other-session";
        Check(!exercise.Submit(CaseEvent.Confirm(), out _, out reason) && relay.forwarded == 1, "session replacement pauses active attempt");
        exercise.requireCoachSynchronization = false;
        Check(!exercise.CanScore, "cannot silently switch active live attempt to local");
        exercise.SelectCase(bundle, selected.caseId, true, out reason);
        var tip = root.Child().AddComponent<AnatomyInstrumentTip>(); tip.exercise = exercise; tip.instrumentId = selected.instruments[0].id;
        Check(anatomy.TryGetPart("cecum", out var cecum), "cecum resolves");
        var collider = cecum.GetComponentsInChildren<Collider>(true)[0];
        Check(!tip.ActivateContact(collider, out _), "unselected instrument tip rejects");
        exercise.SelectInstrument(tip.instrumentId);
        Check(tip.ActivateContact(collider, out _), "selected tool tip routes contact to exercise");
        anatomy.SetSystemVisible(cecum.system, false);
        Check(!tip.ActivateContact(collider, out _), "hidden anatomy collider rejects tool contact");
        var atlas = JsonDocument.Parse(File.ReadAllText("apps/quest/Assets/Scalpal/Anatomy/Resources/anatomy-atlas.json"));
        var actualIds = atlas.RootElement.GetProperty("parts").EnumerateArray()
            .Select(p => p.GetProperty("stableId").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var procedure in bundle.procedures)
        {
            var actualRoot = new GameObject("actual catalog " + procedure.id);
            var actualAnatomy = actualRoot.AddComponent<AnatomyController>();
            foreach (var id in actualIds) Add(actualRoot, id);
            var binding = actualRoot.AddComponent<AnatomyExerciseBinding>(); binding.anatomy = actualAnatomy;
            var actualCase = bundle.cases.First(c => c.procedureId == procedure.id && c.status != "blocked");
            Check(binding.SelectCase(bundle, actualCase.caseId, true, out reason), "actual atlas supports " + procedure.id + ": " + reason);
            actualAnatomy.SetRegistrationValid(true);
            var turns = 0;
            while (!binding.Completed && turns++ < 30)
            {
                foreach (var input in CaseRunner.PerfectEvents(binding.Current))
                    Check(binding.Submit(input, out var outcome, out reason) && outcome.mistake == null,
                        "actual atlas playthrough " + procedure.id + ": " + reason);
            }
            Check(binding.Completed, "actual atlas completed " + procedure.id);
        }
        var sourceRoot = new GameObject("case source test");
        var sourceAnatomy = sourceRoot.AddComponent<AnatomyController>();
        foreach (var id in actualIds) Add(sourceRoot, id);
        var sourceExercise = sourceRoot.AddComponent<AnatomyExerciseBinding>(); sourceExercise.anatomy = sourceAnatomy;
        var service = sourceRoot.AddComponent<ScalpalPreopService>();
        var source = sourceRoot.AddComponent<AnatomyCaseSource>(); source.service = service; source.exercise = sourceExercise;
        sourceExercise.coach = sourceRoot.AddComponent<CoachRelay>();
        service.BaseUrl = "http://192.168.1.20:8787";
        source.Rebind();
        var readyCase = bundle.cases.First(c => c.status == "ready");
        Check(source.LoadPatient(readyCase.patientId) && source.IsLoading && service.RequestedPatient == readyCase.patientId, "service patient load routed");
        Check(sourceExercise.coach.Endpoint == service.BaseUrl, "case client and coach share configured LAN endpoint");
        Check(!source.LoadPatient(selected.patientId), "overlapping service load rejected");
        service.Deliver(readyCase);
        Check(sourceExercise.SelectedCase == readyCase && !source.IsLoading && !sourceExercise.CanScore, "ready service case starts unregistered");
        sourceAnatomy.SetRegistrationValid(true);
        Check(sourceExercise.CanScore, "ready service case allows registered scoring");
        source.LoadPatient(selected.patientId);
        sourceAnatomy.SetRegistrationValid(true);
        Check(sourceExercise.SelectedCase == null && !sourceExercise.CanScore, "new request discards stale runner despite reacquisition");
        service.Deliver(selected);
        Check(source.PendingCase == selected && sourceExercise.SelectedCase == null, "review case waits for acknowledgement");
        Check(source.AcknowledgeReviewAndStart() && sourceExercise.SelectedCase == selected, "acknowledged service case starts");
        source.LoadPatient(readyCase.patientId);
        service.Fail("network unavailable");
        sourceAnatomy.SetRegistrationValid(true);
        Check(source.Status == "network unavailable" && !sourceExercise.CanScore && source.PendingCase == null, "service failure leaves stale attempt stopped");
        var blockedCase = bundle.cases.First(c => c.status == "blocked");
        source.LoadPatient(blockedCase.patientId); service.Deliver(blockedCase);
        Check(sourceExercise.SelectedCase == null && !source.IsLoading && !source.AcknowledgeReviewAndStart(), "blocked case cannot start");
        source.LoadPatient(readyCase.patientId); service.Deliver(selected);
        Check(sourceExercise.SelectedCase == null && source.Status.Contains("did not match"), "wrong patient response rejected");
        Console.WriteLine($"integration-check: {checks} checks passed; real CaseRunner completed all three procedures using actual atlas IDs through AnatomyExerciseBinding.");
    }
    static void Add(GameObject root, string id)
    {
        var obj = root.Child(); obj.name = "anat_" + id;
        var part = obj.AddComponent<AnatomyPart>(); part.stableId = id;
        obj.AddComponent<Renderer>(); obj.AddComponent<MeshCollider>();
    }
}
