using System;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic editor fixture; this does not claim headset or participant registration validation.
    public static class NativeProcedureInputValidation
    {
        static int checks;
        [MenuItem("Scalpal/Quest/Validate Native Procedure Input")]
        public static void Run()
        {
            checks = 0;
            var root = new GameObject("NativeProcedureInputValidation");
            var nativeRoot = new GameObject("SyntheticInactiveWorkbench");
            nativeRoot.SetActive(false); // Avoid hardware initialization in this fixture.
            try
            {
                var workbench = nativeRoot.AddComponent<NativeWorkbench>();
                var controller = root.AddComponent<AnatomyController>();
                controller.initiallyHiddenSystems = Array.Empty<string>();
                var partObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                partObject.transform.SetParent(root.transform, false);
                partObject.name = "anat_appendix";
                var secondCollider = new GameObject("SecondCollider");
                secondCollider.transform.SetParent(partObject.transform, false);
                var second = secondCollider.AddComponent<BoxCollider>();
                var part = partObject.AddComponent<AnatomyPart>(); part.stableId = "appendix";
                var hit = partObject.GetComponent<Collider>();
                var exercise = root.AddComponent<AnatomyExerciseBinding>(); exercise.anatomy = controller;
                var portObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                portObject.transform.SetParent(root.transform, false);
                portObject.transform.localScale = Vector3.one * 0.15f;
                portObject.GetComponent<Collider>().isTrigger = true;
                var marker = portObject.AddComponent<NativePortMarker>(); marker.portId = "umbilical";
                var trocar = Tool(root, "trocar_12mm");
                var scalpel = Tool(root, "scalpel");
                workbench.tools = new[] { trocar, scalpel };
                var input = root.AddComponent<NativeProcedureInput>(); input.portMarkers = new[] { marker };
                bool practicing = true;
                input.Initialize(exercise, workbench, () => practicing);
                var port = new Port { id = "umbilical", instrumentIds = new[] { "trocar_12mm" } };
                var steps = new[] {
                    new ProcedureStep { id = "ports", instrumentId = "trocar_12mm", targets = Array.Empty<string>(), check = new SuccessCheck { type = "place_ports", targets = new[] { "umbilical" }, count = 1 }, next = "apply" },
                    new ProcedureStep { id = "apply", instrumentId = "scalpel", targets = new[] { "appendix" }, check = new SuccessCheck { type = "apply_count", targets = new[] { "appendix" }, count = 3 }, next = "confirm" },
                    new ProcedureStep { id = "confirm", instrumentId = "scalpel", check = new SuccessCheck { type = "confirm", targets = Array.Empty<string>() } }
                };
                var candidate = new SurgicalCase {
                    caseId = "synthetic-input", patientId = "synthetic", procedureId = "synthetic-procedure", status = "ready",
                    instruments = new[] { new Instrument { id = "trocar_12mm" }, new Instrument { id = "scalpel" } },
                    procedure = new Procedure { id = "synthetic-procedure", firstStep = "ports", structures = new[] { "appendix" }, ports = new[] { port }, steps = steps }
                };
                controller.RebuildIndex();
                Assert(exercise.SelectCase(new ScalpalBundle { cases = new[] { candidate } }, candidate.caseId, false, out var reason), "fixture case: " + reason);
                controller.SetRegistrationValid(true);
                Physics.SyncTransforms();
                int events = 0; exercise.EventHandled += (evt, result) => events++;
                Assert(!input.TryPlacePort("umbilical", trocar, out _), "native tracking gate");
                typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(workbench, new object[] { true });
                practicing = false; Assert(!input.TryPlacePort("umbilical", trocar, out _), "phase gate"); practicing = true;
                controller.SetRegistrationValid(false); Assert(!input.TryPlacePort("umbilical", trocar, out _), "registration gate"); controller.SetRegistrationValid(true);
                trocar.SetHeld(false); Assert(!input.TryPlacePort("umbilical", trocar, out _), "held gate"); trocar.SetHeld(true);
                trocar.SetTrackingValid(false); Assert(!input.TryPlacePort("umbilical", trocar, out _), "tool tracking gate"); trocar.SetTrackingValid(true);
                trocar.SetActivation(0.1f); Assert(!input.TryPlacePort("umbilical", trocar, out _), "trigger gate"); trocar.SetActivation(1);
                Assert(!input.TryPlacePort("umbilical", scalpel, out _), "exact step instrument");
                port.instrumentIds = Array.Empty<string>(); Assert(!input.TryPlacePort("umbilical", trocar, out _), "port allowlist"); port.instrumentIds = new[] { "trocar_12mm" };
                trocar.transform.position = Vector3.right * 3; Physics.SyncTransforms(); Assert(!input.TryPlacePort("umbilical", trocar, out _), "actual tip overlap"); trocar.transform.position = Vector3.zero; Physics.SyncTransforms();
                var contact = trocar.GetComponentInChildren<InstrumentTipContact>();
                Pulse(input, trocar); contact.TryReportContact(hit);
                Assert(events == 0 && exercise.Current.id == "ports", "ordinary touch cannot place a port");
                Assert(input.TryPlacePort("umbilical", trocar, out reason), "real authored port contact: " + reason);
                Assert(events == 1 && exercise.Current.id == "apply", "port event advances once");
                Assert(!input.TryPlacePort("umbilical", trocar, out _), "port input cannot score non-port step");
                Assert(!input.Confirm(out _), "confirm cannot skip authored apply check");
                Assert(!input.IdentifyFocused(out _), "identify cannot replace authored apply check");
                var scalpelContact = scalpel.GetComponentInChildren<InstrumentTipContact>();
                Pulse(input, scalpel); scalpelContact.TryReportContact(hit); scalpelContact.TryReportContact(second);
                Assert(events == 2 && exercise.Current.id == "apply", "multiple colliders count once per activation");
                scalpelContact.ResetContactCycle(); scalpelContact.TryReportContact(hit);
                Assert(events == 2, "adapter rejects duplicate callback in same activation");
                Pulse(input, scalpel); scalpelContact.TryReportContact(hit);
                Assert(events == 3 && exercise.Current.id == "apply", "fresh activation counts one additional action");
                Pulse(input, scalpel); scalpel.transform.position = Vector3.right * 3; Physics.SyncTransforms(); scalpelContact.TryReportContact(hit);
                Assert(events == 3, "reported name without actual tip overlap cannot score");
                scalpel.transform.position = Vector3.zero; Physics.SyncTransforms(); Pulse(input, scalpel); scalpelContact.TryReportContact(hit);
                Assert(events == 4 && exercise.Current.id == "confirm", "three separate valid activations satisfy authored count");
                practicing = false; Assert(!input.Confirm(out _), "pause prevents confirmation"); practicing = true;
                Assert(input.Confirm(out reason) && exercise.Completed && events == 5, "authored confirmation completes");
                Assert(!input.Confirm(out _), "completed attempt cannot score again");
                Debug.Log("SCALPAL_NATIVE_INPUT_VALIDATION_OK checks=" + checks + " synthetic editor fixture; no headset validation");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(nativeRoot); }
        }

        static InstrumentBehaviour Tool(GameObject root, string id)
        {
            var obj = new GameObject(id); obj.transform.SetParent(root.transform, false);
            var tool = obj.AddComponent<InstrumentBehaviour>(); tool.instrumentId = id;
            var tip = new GameObject("Tip"); tip.transform.SetParent(obj.transform, false);
            var sphere = tip.AddComponent<SphereCollider>(); sphere.radius = 0.02f; sphere.isTrigger = true;
            tool.actionPoint = tip.transform; tool.contactRadius = 0.02f;
            tip.AddComponent<InstrumentTipContact>();
            tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1);
            return tool;
        }
        static void Pulse(NativeProcedureInput input, InstrumentBehaviour tool)
        {
            tool.SetActivation(0);
            typeof(NativeProcedureInput).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
            tool.SetActivation(1);
        }
        static void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Native input validation failed: " + message);
            checks++;
        }
    }
}
