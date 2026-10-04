using System;
using System.Linq;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Actual Unity JSON parsing is required: .NET JSON doubles do not reproduce
    // the empty nested object created for an omitted optional body descriptor.
    public static class NativeCaseModelValidation
    {
        public static void Run()
        {
            const string legacyJson = "{\"id\":\"legacy\",\"firstStep\":\"access\",\"steps\":[{\"id\":\"access\",\"check\":{\"type\":\"place_ports\",\"targets\":[\"umbilical\"],\"count\":1}}]}";
            var legacy = JsonUtility.FromJson<Procedure>(legacyJson);
            var runner = new CaseRunner(legacy);
            Require(runner.Body == null, "omitted body descriptor retains legacy engine");
            Require(runner.Handle(CaseEvent.PlacePort("umbilical")).completed, "deserialized legacy port actually advances");
            legacy.openBody = new OpenBodyCase();
            Require(new CaseRunner(legacy).Body == null, "empty optional descriptor cannot change scoring engine");

            var bundle = JsonUtility.FromJson<ScalpalBundle>(Resources.Load<TextAsset>("scalpal_bundle").text);
            var body = bundle.procedures.Single(p => p.id == "open_appendectomy");
            Require(new CaseRunner(body).Body != null, "complete authored predicate case selects body engine");
            RequireRejected(new Procedure { steps = new[] { new ProcedureStep { check = new SuccessCheck { type = "body_predicate" } } }, openBody = new OpenBodyCase() });
            legacy.openBody = body.openBody;
            RequireRejected(legacy);
            var unsupported = JsonUtility.FromJson<Procedure>(JsonUtility.ToJson(body));
            unsupported.openBody.version = 2;
            RequireRejected(unsupported);
            Debug.Log("SCALPAL_NATIVE_CASE_MODEL_VALIDATION_OK checks=7 actualUnityJson=true legacyAndBody=true invalidPlansFailClosed=true");
        }
        static void RequireRejected(Procedure procedure)
        {
            try { new CaseRunner(procedure); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Incomplete or inconsistent body plan was accepted");
        }
        static void Require(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException("Case model regression: " + reason);
        }
    }
}
