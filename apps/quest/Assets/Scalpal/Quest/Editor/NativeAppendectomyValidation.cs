using System;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Actual packaged scene geometry and authored case, with synthetic tracked poses in the editor.
    // Run with -batchmode -executeMethod; this does not simulate a human learner or validate a headset.
    public static class NativeAppendectomyValidation
    {
        const string CaseId = "case_patient-demo-multi-source_lap_appendectomy";
        static int checks;

        public static void Run()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Run this scene fixture in batch mode so it cannot discard unsaved editor work.");
            checks = 0;
            var previous = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                var scene = EditorSceneManager.OpenScene(NativeSessionBuild.ScenePath, OpenSceneMode.Single);
                var session = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<NativeCaseSession>(true)).Single();
                session.enabled = false;
                session.realtime.autoConnect = false; session.realtime.enabled = false;
                session.voice.enabled = false; session.coach.enabled = false;
                var workbench = session.workbench; workbench.enabled = false;
                foreach (var hand in workbench.inputs) hand.enabled = false;
                var exercise = session.exercise;
                exercise.requireCoachSynchronization = false; exercise.explicitCoachSessionId = "";
                session.preview.gameObject.SetActive(false);
                var anatomy = session.anatomy;
                anatomy.RebuildIndex();
                Assert(workbench.tools.Length == 15 && workbench.tools.All(tool => tool && tool.actionPoint), "15 actual tools with authored tips");
                Assert(anatomy.Parts.Count == 9, "nine actual practice anatomy parts");
                var input = session.GetComponent<NativeProcedureInput>();
                if (!input) input = session.gameObject.AddComponent<NativeProcedureInput>();
                input.portMarkers = session.patientFrame.GetComponentsInChildren<NativePortMarker>(true);
                bool practicing = true;
                input.Initialize(exercise, workbench, () => practicing);
                ReleaseAll(workbench, input);
                Assert(!input.Confirm(out _), "input is gated before a case exists");
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Scalpal/Exercises/Resources/scalpal_bundle.json");
                Assert(asset, "packaged case bundle exists");
                var bundle = JsonUtility.FromJson<ScalpalBundle>(asset.text);
                var candidate = bundle.cases.Single(item => item.caseId == CaseId);
                Assert(candidate.brief != null && candidate.brief.synthetic, "explicit synthetic brief acknowledgement");
                Assert(candidate.procedure.steps.Length == 10, "ten authored appendectomy steps");
                Assert(exercise.SelectCase(bundle, CaseId, true, out var reason), "case binds actual anatomy: " + reason);
                Assert(exercise.Body == null, "deserialized legacy case retains port/touch progression rather than empty body model");
                anatomy.SetRegistrationValid(true);
                SetReady(workbench, true);
                Physics.SyncTransforms();
                int actions = 0, completedSteps = 0, warnings = 0;
                exercise.EventHandled += (evt, result) => { actions++; if (result.advanced) completedSteps++; if (result.mistake != null) warnings++; };

                // Gates are checked using a valid actual tool tip overlapping the first actual marker.
                var firstStep = exercise.Current;
                var firstTool = Tool(workbench, firstStep.instrumentId);
                var firstMarker = input.portMarkers.Single(marker => marker.portId == firstStep.check.targets[0]);
                PoseTip(firstTool, firstMarker.transform.position);
                Activate(input, firstTool);
                practicing = false; Assert(!input.TryPlacePort(firstMarker.portId, firstTool, out _), "selection/paused phase cannot score"); practicing = true;
                SetReady(workbench, false); Assert(!input.TryPlacePort(firstMarker.portId, firstTool, out _), "native tracking loss cannot score"); SetReady(workbench, true);
                anatomy.SetPreviewMode(true); Assert(!input.TryPlacePort(firstMarker.portId, firstTool, out _), "preview cannot score"); anatomy.SetPreviewMode(false);
                anatomy.SetRegistrationValid(false); Assert(!input.TryPlacePort(firstMarker.portId, firstTool, out _), "invalid fit cannot score"); anatomy.SetRegistrationValid(true);
                firstTool.SetTrackingValid(false); Assert(!input.TryPlacePort(firstMarker.portId, firstTool, out _), "untracked held tool cannot score");
                Assert(actions == 0 && exercise.Current == firstStep, "gate probes did not advance the authored case");

                int expectedActions = 0;
                foreach (var authored in candidate.procedure.steps)
                {
                    Assert(exercise.Current != null && exercise.Current.id == authored.id, "expected current step " + authored.id);
                    ReleaseAll(workbench, input);
                    var tool = Tool(workbench, authored.instrumentId);
                    switch (authored.check.type)
                    {
                        case "place_ports":
                            foreach (var id in authored.check.targets)
                            {
                                var marker = input.portMarkers.Single(item => item.portId == id);
                                PoseTip(tool, marker.transform.position); Activate(input, tool);
                                Assert(input.TryPlacePort(id, tool, out reason), "actual port contact " + id + ": " + reason);
                                expectedActions++;
                            }
                            break;
                        case "identify_targets":
                            foreach (var id in authored.check.targets)
                            {
                                PositionOnSurface(anatomy, input, tool, id, true);
                                Assert(input.FocusedPartId == id, "actual collider focus for " + id);
                                Assert(input.IdentifyFocused(out reason), "actual focused identification " + id + ": " + reason);
                                expectedActions++;
                            }
                            break;
                        case "touch_target":
                            foreach (var id in authored.check.targets)
                            {
                                var collider = PositionOnSurface(anatomy, input, tool, id, false);
                                Activate(input, tool);
                                int before = actions;
                                var tip = tool.GetComponentsInChildren<InstrumentTipContact>(true).First(contact => contact.GetComponent<Collider>());
                                Assert(tip.TryReportContact(collider), "actual tip callback for " + id);
                                Assert(actions == before + 1, "one scoring event for intentional contact " + id);
                                Assert(!tip.TryReportContact(collider) && actions == before + 1, "same activation cannot score twice " + id);
                                expectedActions++;
                            }
                            break;
                        case "confirm":
                            Assert(input.Confirm(out reason), "authored final confirmation: " + reason);
                            expectedActions++;
                            break;
                        default: throw new InvalidOperationException("Actual appendectomy fixture does not support authored check " + authored.check.type);
                    }
                    Assert(exercise.Completed || exercise.Current.id == authored.next, "step advanced only after its authored actions " + authored.id);
                }
                Assert(exercise.Completed && completedSteps == 10, "all ten actual-scene steps completed");
                Assert(warnings == 0, "actual target/tool actions produced no authored warnings");
                Assert(actions == expectedActions && actions == 13, "thirteen actual intentional adapter actions");
                Assert(!input.Confirm(out _), "completed case refuses extra scoring");
                Debug.Log("SCALPAL_NATIVE_APPENDECTOMY_VALIDATION_OK checks=" + checks + " steps=" + completedSteps + " actions=" + actions +
                    " actualSceneGeometry=true syntheticEditorPoses=true coachSync=false headsetValidated=false");
            }
            finally
            {
                if (previous.Any(item => item.isLoaded) && previous.Count(item => item.isActive) == 1)
                    EditorSceneManager.RestoreSceneManagerSetup(previous);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // Use an actual transformed MeshCollider surface vertex; never substitute a bounds center.
        static Collider PositionOnSurface(AnatomyController anatomy, NativeProcedureInput input, InstrumentBehaviour tool, string id, bool requireFocus)
        {
            Assert(anatomy.TryGetPart(id, out var part) && part.IsVisible && part.HasVisibleGeometry, "visible actual anatomy " + id);
            tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(0);
            TickInput(input);
            var tip = tool.GetComponentsInChildren<InstrumentTipContact>(true).Select(contact => contact.GetComponent<Collider>())
                .FirstOrDefault(candidate => candidate && candidate.enabled && candidate.isTrigger);
            if (!tip) throw new InvalidOperationException("Actual tool has no active tip trigger: " + tool.instrumentId);
            foreach (var collider in part.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!collider.enabled || !collider.gameObject.activeInHierarchy || collider.GetComponentInParent<AnatomyPart>(true) != part || !collider.sharedMesh) continue;
                var vertices = collider.sharedMesh.vertices;
                foreach (var vertex in vertices)
                {
                    // ActionPoint and the actual authored Tip center differ by about 5 mm.
                    // Align the physical trigger center, rather than assuming those anchors coincide.
                    var point = collider.transform.TransformPoint(vertex);
                    var center = tip is SphereCollider sphere ? sphere.transform.TransformPoint(sphere.center) : tip.bounds.center;
                    tool.transform.position += point - center;
                    Physics.SyncTransforms();
                    if (!Physics.ComputePenetration(tip, tip.transform.position, tip.transform.rotation,
                        collider, collider.transform.position, collider.transform.rotation, out _, out _)) continue;
                    if (requireFocus) { input.RefreshFocus(); if (input.FocusedPartId != id) continue; }
                    return collider;
                }
            }
            throw new InvalidOperationException("No actual mesh surface vertex produced a valid tool-tip " + (requireFocus ? "focus" : "overlap") + " for " + id);
        }

        static void PoseTip(InstrumentBehaviour tool, Vector3 point)
        {
            tool.transform.position += point - tool.actionPoint.position;
            Physics.SyncTransforms();
        }
        static void Activate(NativeProcedureInput input, InstrumentBehaviour tool)
        {
            tool.SetActivation(0); TickInput(input);
            tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); Physics.SyncTransforms();
        }
        static void ReleaseAll(NativeWorkbench workbench, NativeProcedureInput input)
        {
            foreach (var tool in workbench.tools) { tool.SetActivation(0); tool.SetHeld(false); tool.SetTrackingValid(false); }
            TickInput(input);
        }
        static void TickInput(NativeProcedureInput input) => typeof(NativeProcedureInput)
            .GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, null);
        static InstrumentBehaviour Tool(NativeWorkbench workbench, string id) => workbench.tools.Single(tool => tool.instrumentId == id);
        static void SetReady(NativeWorkbench workbench, bool ready) => typeof(NativeWorkbench).GetProperty("IsReady").GetSetMethod(true).Invoke(workbench, new object[] { ready });
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Actual-scene appendectomy validation failed: " + message);
            checks++;
        }
    }
}
