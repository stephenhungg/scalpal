using System;
using System.Linq;
using System.Reflection;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using Scalpal.Realtime;
using UnityEditor;
using UnityEngine;

namespace Scalpal.EncounterOffice.Editor
{
    // Synthetic service fixture: actual createApp/fixtureClient response after allergies, attending,
    // and a wrong kidney-stone/ureteroscopy/elective assessment. No XR, HTTP, voice or scene startup.
    public static class EncounterRouteValidation
    {
        const string Fixture = @"{""state"":{""encounterId"":""enc-routefixture123"",""phase"":""scored"",""version"":2,""patientId"":""patient-demo-multi-source"",""patientName"":""Priya Ramaswamy"",""speakerName"":""Priya Ramaswamy"",""speaker"":""patient"",""elapsedSeconds"":0,""historyAsked"":[{""id"":""allergies"",""label"":""allergies""}],""exams"":[],""tests"":[],""assessment"":{""diagnosis"":""kidney stone"",""differential"":[""appendicitis""],""procedure"":""ureteroscopy"",""urgency"":""elective""}},""scorecard"":{""patientId"":""patient-demo-multi-source"",""patientName"":""Priya Ramaswamy"",""urgency"":""urgent"",""site"":""Abdomen — appendix / right lower quadrant"",""carryoverItems"":[{""flagId"":""flag_latex"",""type"":""latex"",""severity"":""high"",""label"":""Latex allergy"",""detail"":""Latex allergy on file. The operating room, gloves, catheters, and equipment must be latex free before the patient enters."",""status"":""found"",""historyTopics"":[""allergies""],""testIds"":[],""stepIds"":[""access_umbilical""]},{""flagId"":""flag_anemia"",""type"":""anemia"",""severity"":""moderate"",""label"":""Anemia (hemoglobin 11.6)"",""detail"":""Lower tolerance for blood loss. Type and screen, and know the latest hemoglobin before incision."",""status"":""missed"",""historyTopics"":[],""testIds"":[],""stepIds"":[""irrigate""]},{""flagId"":""flag_incomplete_chart"",""type"":""incomplete_chart"",""severity"":""moderate"",""label"":""Incomplete chart (1 gap)"",""detail"":""No usable creatinine or eGFR on file. Kidney function is unknown."",""status"":""missed"",""historyTopics"":[""allergies""],""testIds"":[],""stepIds"":[""access_umbilical""]}],""total"":3,""max"":100,""grade"":""Needs work"",""procedureId"":""lap_appendectomy"",""procedureTitle"":""Laparoscopic appendectomy"",""procedureChosenCorrectly"":false,""sections"":[{""id"":""history"",""label"":""History"",""max"":25,""score"":3,""found"":[""allergies""],""missed"":[""menstrual history and pregnancy"",""last oral intake"",""onset"",""pain migration"",""nausea and vomiting"",""fever"",""bowel habits"",""urinary symptoms"",""past surgical history"",""medications"",""past medical history""]},{""id"":""exam"",""label"":""Examination"",""max"":15,""score"":0,""found"":[],""missed"":[""vital signs"",""abdominal palpation"",""rebound tenderness""]},{""id"":""workup"",""label"":""Workup"",""max"":15,""score"":0,""found"":[],""missed"":[""pregnancy test"",""CBC"",""urinalysis"",""CT abdomen and pelvis""]},{""id"":""diagnosis"",""label"":""Diagnosis"",""max"":20,""score"":0,""found"":[],""missed"":[""Acute appendicitis""]},{""id"":""plan"",""label"":""Plan"",""max"":10,""score"":0,""found"":[],""missed"":[""laparoscopic appendectomy"",""urgent timing""]},{""id"":""differential"",""label"":""Differential"",""max"":15,""score"":0,""found"":[],""missed"":[""Ectopic pregnancy"",""Ovarian torsion"",""Ruptured ovarian cyst""]}],""criticalMissed"":[{""kind"":""history"",""id"":""menstrual_pregnancy"",""label"":""menstrual history and pregnancy"",""why"":""A woman of reproductive age with right lower quadrant pain needs pregnancy excluded: ectopic pregnancy is the can't-miss diagnosis.""},{""kind"":""test"",""id"":""pregnancy_test"",""label"":""pregnancy test"",""why"":""History alone does not exclude pregnancy. Order a beta-hCG before imaging and surgery.""},{""kind"":""history"",""id"":""last_meal"",""label"":""last oral intake"",""why"":""Last oral intake sets anesthesia timing and aspiration risk.""}],""criticalFound"":[{""kind"":""history"",""id"":""allergies"",""label"":""allergies"",""why"":""She has a latex allergy (a contact rash from gloves, low criticality on her chart). The room, gloves, and catheters must still be latex free.""}],""diagnosisGiven"":""kidney stone"",""diagnosisExpected"":""Acute appendicitis"",""diagnosisResult"":""incorrect"",""differentialNamed"":[],""differentialSuggestions"":[""Ectopic pregnancy"",""Ovarian torsion"",""Ruptured ovarian cyst""],""feedback"":[""Must fix: you did not cover menstrual history and pregnancy. A woman of reproductive age with right lower quadrant pain needs pregnancy excluded: ectopic pregnancy is the can't-miss diagnosis."",""Must fix: you did not cover pregnancy test. History alone does not exclude pregnancy. Order a beta-hCG before imaging and surgery."",""Must fix: you did not cover last oral intake. Last oral intake sets anesthesia timing and aspiration risk."",""The diagnosis was acute appendicitis."",""The surgery this patient needs is a laparoscopic appendectomy, and that is what we will do in the operating room."",""Timing: this case is urgent."",""Broaden the differential: consider ectopic pregnancy, ovarian torsion, ruptured ovarian cyst."",""Also missed onset: Timing separates early appendicitis from perforation."",""Also missed pain migration: Pain that starts at the umbilicus and moves to the right lower quadrant is the classic appendicitis pattern."",""Also missed nausea and vomiting: Anorexia and nausea after the pain starts support appendicitis."",""Good: you covered allergies.""],""spoken"":""Needs work: 3 out of 100. You did well to cover allergies. The big miss: menstrual history and pregnancy. A woman of reproductive age with right lower quadrant pain needs pregnancy excluded: ectopic pregnancy is the can't-miss diagnosis.""}}";
        static int checks;
        [Serializable] sealed class Create { public string patientId, encounterId, mode; }

        [MenuItem("Scalpal/Quest/Validate Encounter Route")]
        public static void Run()
        {
            checks = 0;
            try
            {
                var bridgeObject = new GameObject("EncounterRouteUnavailableBridgeFixture");
                bridgeObject.SetActive(false);
                try
                {
                    var bridge = bridgeObject.AddComponent<QuestSessionBridge>(); bridge.autoConnect = false;
                    Check(!bridge.TryGetEncounterBinding("enc-routefixture123", out var sharedSession, out var sharedAttempt, out var sharedPatient, out var sharedPhase)
                        && sharedSession == "" && sharedAttempt == "" && sharedPatient == "" && sharedPhase == "",
                        "unavailable shared bridge cannot fabricate encounter attempt provenance");
                }
                finally { UnityEngine.Object.DestroyImmediate(bridgeObject); }
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
                Check(asset, "packaged service fixture bundle exists");
                var bundle = JsonUtility.FromJson<ScalpalBundle>(asset.text);
                // A patient can have several packaged cases (open primary first, laparoscopic advanced variant later);
                // the office routes to the case whose procedure the authored scorecard resolves to.
                var kase = bundle.cases.Single(item => item.patientId == EncounterContract.FemalePatientId && item.procedureId == JsonUtility.FromJson<EncounterReply>(Fixture).scorecard.procedureId);
                var fixture = JsonUtility.FromJson<EncounterReply>(Fixture);
                var state = fixture.state; var score = fixture.scorecard;
                Check(state.phase == "scored" && state.assessment.procedure == "ureteroscopy"
                    && score.procedureId == kase.procedureId && !score.procedureChosenCorrectly,
                    "actual wrong learner plan still resolves to authored surgery");
                Check(score.carryoverItems.Any(item => item.type == "latex" && item.status == "found")
                    && score.carryoverItems.Any(item => item.type == "anemia" && item.status == "missed"),
                    "actual service score carries both elicited and missed chart risks");

                EncounterOfficeRoute.SelectPatient("u_test_consented_patient", "http://127.0.0.1:8787");
                Check(EncounterOfficeRoute.TakePatient(out var patient, out var endpoint)
                    && patient == "u_test_consented_patient" && endpoint == "http://127.0.0.1:8787",
                    "explore selection preserves an arbitrary canonical subject and endpoint");
                Check(!EncounterOfficeRoute.TakePatient(out patient, out endpoint) && patient == "" && endpoint == "",
                    "explore selection can be consumed only once");
                foreach (var status in new[] { "blocked", "retry", "connect" })
                    Check(!EncounterOfficeRoute.CanEnter(new PatientListEntry { patientId = "patient-demo-rate-limited", procedureId = "lap_appendectomy", status = status }),
                        "explore blocks unavailable status " + status);
                Check(!EncounterOfficeRoute.CanEnter(new PatientListEntry { patientId = "", procedureId = "lap_appendectomy", status = "ready" }), "connect-only scenario has no selectable subject");
                Check(!EncounterOfficeRoute.CanEnter(new PatientListEntry { patientId = state.patientId, procedureId = "", status = "ready" }), "missing authored procedure blocks selection");
                Check(EncounterOfficeRoute.CanEnter(new PatientListEntry { patientId = state.patientId, procedureId = kase.procedureId, status = "needs_review" }), "reviewable synthetic cases remain selectable");

                var original = Clone(fixture);
                Check(EncounterOfficeRoute.PrepareSurgery(state, score, kase.procedureId, "http://127.0.0.1:8787", out var reason, "session-route-fixture", "attempt-route-fixture"), "production route prepares scored office handoff: " + reason);
                state.assessment.diagnosis = "mutated draft"; state.assessment.differential[0] = "mutated differential";
                score.feedback[0] = "mutated feedback"; score.carryoverItems[0].label = "mutated risk";
                score.carryoverItems[0].historyTopics[0] = "mutated provenance";
                score.sections[0].found[0] = "mutated scored fact";
                Check(EncounterOfficeRoute.TakeSurgery(out var handoff), "production route transfers the prepared handoff");
                Check(handoff.sharedSessionId == "session-route-fixture" && handoff.attemptId == "attempt-route-fixture", "handoff retains the exact captured shared session and attempt IDs");
                Check(JsonUtility.ToJson(handoff.assessment) == JsonUtility.ToJson(original.state.assessment), "handoff deep clones the exact committed assessment and differential");
                Check(JsonUtility.ToJson(handoff.scorecard) == JsonUtility.ToJson(original.scorecard), "handoff deep clones the entire modeled score and risk arrays");
                Check(!EncounterOfficeRoute.TakeSurgery(out _), "surgery handoff can be consumed only once");
                Check(EncounterSurgeryBinding.Validate(handoff, kase, original, Clone(original), out reason), "live service result validates the production handoff: " + reason);
                var create = JsonUtility.FromJson<Create>(EncounterSurgeryBinding.CoachCreateJson(handoff, "virtual"));
                Check(create.patientId == handoff.patientId && create.encounterId == handoff.encounterId && create.mode == "virtual",
                    "coach creation carries the exact encounter, subject and virtual presentation");
                CheckThrows(() => EncounterSurgeryBinding.CoachCreateJson(handoff, "mixed_reality"), "office handoff rejects mixed reality presentation");
                CheckThrows(() => EncounterSurgeryBinding.CoachCreateJson(handoff, "vr"), "invalid coach mode is rejected");
                CheckThrows(() => EncounterSurgeryBinding.CoachCreateJson(null, "virtual"), "missing office handoff cannot create a coach session");
                Reject(handoff, kase, null, original, "missing live encounter");
                var bad = Clone(original); bad.error = new EncounterError { code = "encounter_not_found", message = "Expired encounter" };
                Reject(handoff, kase, bad, original, "live encounter service error");
                Reject(handoff, kase, original, bad, "live score service error");
                bad = Clone(original); bad.state.encounterId = "enc-another123";
                Reject(handoff, kase, bad, original, "different encounter ID");
                bad = Clone(original); bad.state.patientId = EncounterContract.MalePatientId;
                Reject(handoff, kase, bad, original, "different patient");
                bad = Clone(original); bad.state.phase = "attending";
                Reject(handoff, kase, bad, original, "unscored encounter");
                bad = Clone(original); bad.state.version++;
                Reject(handoff, kase, bad, original, "responses from different encounter versions");
                bad = Clone(original); bad.state.assessment.procedure = "laparoscopic appendectomy";
                Reject(handoff, kase, bad, original, "changed committed learner procedure");
                bad = Clone(original); bad.state.assessment.differential = new[] { "different differential" };
                Reject(handoff, kase, original, bad, "changed committed differential in score response");
                bad = Clone(original); bad.scorecard.procedureId = "lap_cholecystectomy";
                Reject(handoff, kase, original, bad, "different authored procedure");
                bad = Clone(original); bad.scorecard.total++;
                Reject(handoff, kase, original, bad, "changed score");
                bad = Clone(original); bad.scorecard.carryoverItems[0].historyTopics = new[] { "medications" };
                Reject(handoff, kase, original, bad, "changed chart-risk provenance");
                bad = Clone(original); bad.scorecard.carryoverItems = new EncounterCarryoverItem[0];
                Reject(handoff, kase, original, bad, "missing chart-risk array");
                bad = Clone(original); bad.scorecard.sections[0].score++;
                Reject(handoff, kase, original, bad, "changed scoring section");
                bad = Clone(original); bad.scorecard.patientId = EncounterContract.MalePatientId;
                Reject(handoff, kase, original, bad, "different scorecard patient");
                var badCase = Clone(kase); badCase.brief.synthetic = false;
                Reject(handoff, badCase, original, original, "non-synthetic live case");
                badCase = Clone(kase); badCase.patientId = EncounterContract.MalePatientId;
                Reject(handoff, badCase, original, original, "live surgical case patient mismatch");
                badCase = Clone(kase); badCase.procedure.id = "lap_cholecystectomy";
                Reject(handoff, badCase, original, original, "live procedure body mismatch");
                badCase = Clone(kase); badCase.status = "retry";
                Reject(handoff, badCase, original, original, "temporarily unavailable live case");

                var arbitrary = Clone(original); arbitrary.state.patientId = arbitrary.scorecard.patientId = "u_test_consented_patient";
                var arbitraryCase = Clone(kase); arbitraryCase.patientId = arbitraryCase.brief.patientId = arbitrary.state.patientId;
                Check(EncounterOfficeRoute.PrepareSurgery(arbitrary.state, arbitrary.scorecard, arbitraryCase.procedureId, "https://preop.example.test", out reason)
                    && EncounterOfficeRoute.TakeSurgery(out var arbitraryHandoff)
                    && EncounterSurgeryBinding.Validate(arbitraryHandoff, arbitraryCase, arbitrary, Clone(arbitrary), out reason),
                    "route and live binding support an arbitrary synthetic subject without a two-patient whitelist");
                Check(EncounterOfficeRoute.PrepareSurgery(arbitrary.state, arbitrary.scorecard, arbitraryCase.procedureId,
                    "https://preop.example.test", out reason, "session-route-fixture", "attempt-route-fixture"), "prepare the actual native surgery consumer fixture");
                var nativeObject = new GameObject("EncounterRouteNativeConsumerFixture"); nativeObject.SetActive(false);
                try
                {
                    var native = nativeObject.AddComponent<NativeCaseSession>();
                    typeof(NativeCaseSession).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(native, null);
                    Check(native.OfficeHandoff != null && native.SelectedPatientId == arbitrary.state.patientId
                        && native.SelectedProcedureId == arbitrary.scorecard.procedureId && native.coachBaseUrl == "https://preop.example.test",
                        "production native Awake adopts arbitrary selected subject, authored procedure and service endpoint");
                    Check(native.OfficeHandoff.sharedSessionId == "session-route-fixture" && native.OfficeHandoff.attemptId == "attempt-route-fixture",
                        "production native consumer preserves captured office shared-attempt provenance");
                    Check(!native.TryChangePresentation(true), "production native office consumer rejects a mixed-reality presentation switch");
                    Check(!EncounterOfficeRoute.TakeSurgery(out _), "native consumer takes the surgery ticket once");
                    typeof(NativeCaseSession).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(native, null);
                    Check(native.SelectedPatientId == arbitrary.state.patientId && native.OfficeHandoff.assessment.procedure == "ureteroscopy",
                        "repeated native Awake cannot replace the committed office proposal with the default case");
                }
                finally { UnityEngine.Object.DestroyImmediate(nativeObject); }
                Check(EncounterOfficeRoute.PrepareSurgery(original.state, original.scorecard, kase.procedureId, "http://127.0.0.1:8787", out reason), "prepare another valid handoff before rejection");
                Check(!EncounterOfficeRoute.PrepareSurgery(original.state, original.scorecard, "lap_cholecystectomy", "http://127.0.0.1:8787", out reason)
                    && !EncounterOfficeRoute.TakeSurgery(out _), "failed preparation clears a previously prepared surgery");
                Check(!EncounterOfficeRoute.PrepareSurgery(original.state, original.scorecard, kase.procedureId, "file:///tmp/service", out reason), "non-HTTP service endpoint is rejected");
                Debug.Log("SCALPAL_ENCOUNTER_ROUTE_OK checks=" + checks);
            }
            finally { EncounterOfficeRoute.TakePatient(out _, out _); EncounterOfficeRoute.ClearSurgery(); }
        }

        static T Clone<T>(T value) => JsonUtility.FromJson<T>(JsonUtility.ToJson(value));
        static void Reject(EncounterSurgeryHandoff handoff, SurgicalCase kase, EncounterReply encounter, EncounterReply score, string label)
        { Check(!EncounterSurgeryBinding.Validate(handoff, kase, encounter, score, out var reason) && !string.IsNullOrEmpty(reason), label + " fails closed with an explanation"); }
        static void CheckThrows(Action action, string label)
        { bool threw = false; try { action(); } catch (ArgumentException) { threw = true; } Check(threw, label); }
        static void Check(bool condition, string label)
        { if (!condition) throw new InvalidOperationException("Encounter route check failed: " + label); checks++; }
    }
}
