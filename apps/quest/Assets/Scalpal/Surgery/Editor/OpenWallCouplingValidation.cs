using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Actual native scene, actual OpenSurgerySession composition and the actual five-layer wall volume,
    // driven by synthetic tracked poses. The body facts must come from what the volume did: a blade
    // stroke scores a layer only where it opened that layer, a muscle split only when two retractors
    // pulled the gripped muscle apart along its fibers, and a tent only when the gripped membrane rose.
    // Not headset evidence; thresholds remain authored teaching values.
    public static class OpenWallCouplingValidation
    {
        const float Dt = .02f;
        static int checks;
        static void Require(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Open wall coupling validation: " + message);
        }
        public static int Run()
        {
            checks = 0;
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new InvalidOperationException("Open wall coupling validation needs to open the native scene; save or discard scene changes first");
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                var rig = new Rig(session, adapter);
                var vr = VerifyOpenWall(rig, "vr_default_fit");
                VerifyNegatives(rig, "vr_default_fit");
                // AR: the registered torso can sit anywhere; the same wound-relative hands must give the same tissue.
                var root = session.patientFrame.root;
                root.SetPositionAndRotation(root.position + new Vector3(1.3f, -.2f, .7f), Quaternion.Euler(4, 63, -3) * root.rotation);
                Physics.SyncTransforms();
                var ar = VerifyOpenWall(rig, "ar_registered_fit");
                VerifyNegatives(rig, "ar_registered_fit");
                Require(Mathf.Abs(vr.split - ar.split) < .5f && Mathf.Abs(vr.lift - ar.lift) < .5f,
                    $"AR and VR give the same measured split ({vr.split:F2}/{ar.split:F2} mm) and tent lift ({vr.lift:F2}/{ar.lift:F2} mm)");
                Debug.Log($"SCALPAL_OPEN_WALL_COUPLING_OK: {checks} checks; vrSplitMm={vr.split:F2} arSplitMm={ar.split:F2} vrLiftMm={vr.lift:F2} arLiftMm={ar.lift:F2} "
                    + $"splitAngleDeg={vr.angle:F1}; actual scene volume, synthetic tracked poses, no headset test");
                return checks;
            }
            finally
            {
                if (tissue) tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(previous);
            }
        }

        sealed class Rig
        {
            public readonly NativeCaseSession session;
            public readonly OpenBodyInteraction input;
            public readonly AnatomyExerciseBinding exercise;
            public readonly Transform wound;
            public readonly NativeVolumeSimulation volume;
            public readonly List<BodyRecord> records = new List<BodyRecord>();
            public readonly List<string> mistakes = new List<string>();
            public readonly InstrumentBehaviour scalpel, first, second, forceps;
            readonly Transform cutStart, cutEnd;
            public int splits;
            public bool gate = true;
            public Rig(NativeCaseSession session, OpenSurgerySession adapter)
            {
                this.session = session; input = adapter.Interaction; exercise = session.exercise; wound = adapter.Wound.transform;
                volume = session.GetComponent<NativeVolumeSimulation>();
                Require(volume && volume.Wall && volume.Wall.transform.parent == wound, "the session's open wall lives in the registered McBurney wound frame");
                input.Submitted += (record, _) => records.Add(record);
                exercise.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                volume.LayerFractured += fracture => { if (fracture.verb == "split") splits++; };
                scalpel = Tool("scalpel"); first = Tool("retractor", 0); second = Tool("retractor", 1); forceps = Tool("toothed_forceps");
                cutStart = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutStart");
                cutEnd = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutEnd");
            }
            InstrumentBehaviour Tool(string id, int index = 0)
            {
                var tool = session.workbench.tools.Where(t => t && t.instrumentId == id).ElementAtOrDefault(index);
                Require(tool && tool.actionPoint, "scene tool exists: " + id + "#" + index);
                return tool;
            }
            public void NewAttempt()
            {
                var selected = exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out var reason), "retry selects a fresh attempt: " + reason);
                session.anatomy.SetRegistrationValid(true); gate = true;
                foreach (var tool in session.workbench.tools) if (tool) { tool.SetHeld(false); var latch = tool.GetComponent<SurgeryInstrumentLatch>(); if (latch) latch.Clear(); }
                // Same per-attempt composition as OpenSurgerySession.ConfigureAttempt, with a validation-owned practice gate.
                volume.Initialize(wound, session.workbench, () => gate, true);
                input.Initialize(exercise, session.workbench.tools, session.patientFrame, wound, () => gate);
                records.Clear(); mistakes.Clear(); splits = 0;
                Step();
                Require(exercise.Body.Log.Count == 0 && volume.Wall.Volume.CutFaceCount == 0, "attempt starts with an intact wall and an empty body log");
            }
            // Runtime order: the scored adapter in Update, then the wall solver and blade sweep in LateUpdate.
            public void Step(int count = 1) { for (int i = 0; i < count; i++) { input.Simulate(Dt); volume.Simulate(Dt); Physics.SyncTransforms(); } }
            public void Hold(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
            public void Place(InstrumentBehaviour tool, Vector3 local)
            {
                tool.transform.rotation = wound.rotation;
                tool.transform.position += wound.TransformPoint(local) - tool.actionPoint.position;
                Physics.SyncTransforms();
            }
            // Blade edge along the wound's inward axis with its deep end at local.
            public void Blade(Vector3 local)
            {
                scalpel.transform.rotation = wound.rotation;
                scalpel.transform.rotation = Quaternion.FromToRotation(cutEnd.position - cutStart.position, wound.forward) * scalpel.transform.rotation;
                scalpel.transform.position += wound.TransformPoint(local) - cutEnd.position;
                Physics.SyncTransforms();
            }
            public void Park(InstrumentBehaviour tool) { tool.SetHeld(false); Place(tool, new Vector3(.25f, .25f, -.3f)); Step(); }
            // One continuous blade stroke along the wound's +X line, ended by releasing the trigger.
            public void Stroke(float fromX, float toX, float y, float tipDepth)
            {
                Hold(scalpel);
                int steps = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(toX - fromX) / .005f));
                for (int i = 0; i <= steps; i++) { Blade(new Vector3(Mathf.Lerp(fromX, toX, i / (float)steps), y, tipDepth)); Step(); }
                scalpel.SetActivation(0); Step(); scalpel.SetActivation(1);
                Park(scalpel);
            }
            public Vector3 World(BodyRecord record) => session.patientFrame.TransformPoint(new Vector3(record.action.position.x, record.action.position.y, record.action.position.z));
            public string Instance(InstrumentBehaviour tool) => "tool-" + tool.GetInstanceID().ToString().Replace("-", "n");
            public double Fact(string tissue, string fact) => exercise.Body.Get(tissue, fact);
        }

        // Cut faces of one material near the stroke line (x in [-30, 30] mm, y = 0) versus elsewhere, in rest wall coordinates.
        static void CutFacesAround(Rig s, string material, out int near, out int far)
        {
            var volume = s.volume.Wall.Volume; near = far = 0;
            int index = Array.FindIndex(volume.Materials, m => m.id == material);
            foreach (var face in volume.Faces)
            {
                if (!face.cut || (volume.Cells[face.cell].material != index && (face.neighbor < 0 || volume.Cells[face.neighbor].material != index))) continue;
                Vector3 centroid = (volume.Original[face.a] + volume.Original[face.b] + volume.Original[face.c]) / 3;
                float distance = new Vector2(Mathf.Max(0, Mathf.Abs(centroid.x) - .03f), centroid.y).magnitude;
                if (distance <= .015f) near++; else far++;
            }
        }
        static float SurfaceDistance(Rig s, Vector3 world)
        {
            var mesh = s.volume.Wall.Surface; var vertices = mesh.vertices; var triangles = mesh.triangles;
            Vector3 local = s.volume.Wall.transform.InverseTransformPoint(world);
            float best = float.PositiveInfinity;
            for (int i = 0; i < triangles.Length; i += 3)
                best = Mathf.Min(best, (Scalpal.Anatomy.Tissue.TissueVolume.ClosestTriangle(local, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]) - local).magnitude);
            return s.volume.Wall.transform.TransformVector(Vector3.right).magnitude * best;
        }
        static BodyRecord LastCut(Rig s, string tissue) => s.records.LastOrDefault(r => r.action.verb == "cut" && r.action.tissueId == tissue);

        // Skin, fat and fascia strokes; each must score its own layer exactly where the volume opened it.
        static void OpenToMuscle(Rig s, string label)
        {
            s.Stroke(-.03f, .03f, 0, .0015f);
            CutFacesAround(s, "skin", out int skinNear, out int skinFar);
            Require(LastCut(s, "skin") != null && s.Fact("skin", "opened") == 1 && skinNear > 0 && skinFar == 0,
                label + ": skin stroke scores skin and opens the actual skin volume at the stroke");
            Require(new[] { "fat", "fascia", "muscle", "peritoneum" }.All(id => s.volume.Wall.Volume.CutFacesForMaterial(id) == 0) && s.Fact("fat", "opened") == 0,
                label + ": a skin-depth stroke opens no deeper layer");
            s.Stroke(-.03f, .03f, 0, .010f);
            CutFacesAround(s, "fat", out int fatNear, out int fatFar);
            Require(LastCut(s, "fat") != null && s.Fact("fat", "opened") == 1 && fatNear > 0 && fatFar == 0 && s.volume.Wall.Volume.CutFacesForMaterial("fascia") == 0,
                label + ": fat stroke scores fat and opens the actual fat at the stroke");
            s.Stroke(-.03f, .03f, 0, .0195f);
            CutFacesAround(s, "fascia", out int fasciaNear, out int fasciaFar);
            Require(LastCut(s, "fascia") != null && s.Fact("fascia", "opened") == 1 && fasciaNear > 0 && fasciaFar == 0 && LastCut(s, "fascia").action.angleDegrees <= 1,
                label + ": fascia stroke along the fibers scores fascia with its fiber angle and opens the actual fascia");
            Require(s.volume.Wall.Volume.CutFacesForMaterial("muscle") > 0 && s.Fact("muscle", "bladeUsed") == 0 && !s.records.Any(r => r.outcomes.Contains("muscle_cut")),
                label + ": opening the fascia to the muscle surface exposes muscle without scoring a muscle cut");
            Require(s.records.Count(r => r.action.verb == "cut") == 3, label + ": one scored cut per stroke and layer: " + string.Join(",", s.records.Where(r => r.action.verb == "cut").Select(r => r.action.tissueId + ":" + r.action.lengthMm + ":" + r.action.depthMm)));
        }
        static void Pull(Rig s, InstrumentBehaviour a, Vector3 fromA, Vector3 directionA, InstrumentBehaviour b, Vector3 fromB, Vector3 directionB, float meters)
        {
            int steps = Mathf.RoundToInt(meters / .001f);
            for (int i = 1; i <= steps; i++)
            {
                if (a) s.Place(a, fromA + directionA * (i * .001f));
                if (b) s.Place(b, fromB + directionB * (i * .001f));
                s.Step();
            }
            s.Step(15);
        }
        static BodyRecord Split(Rig s, string label)
        {
            // One retractor on each lip of the fascial opening, 16 mm apart. Fracture follows the wall's 20 mm
            // tetrahedral cells: two grips on one tetrahedron (e.g. both on the x = 0 cell boundary) share
            // material and cannot be parted, so no split is awarded there. This placement is off that boundary.
            Vector3 a = new Vector3(-.01f, -.008f, .019f), b = new Vector3(-.01f, .008f, .019f);
            s.Hold(s.first); s.Hold(s.second); s.Place(s.first, a); s.Place(s.second, b); s.Step(6);
            Require(s.records.Any(r => r.action.verb == "retract" && r.action.tissueId == "muscle" && r.action.instrumentInstanceId == s.Instance(s.first)) &&
                s.records.Any(r => r.action.verb == "retract" && r.action.tissueId == "muscle" && r.action.instrumentInstanceId == s.Instance(s.second)),
                label + ": both retractors engage the physically exposed muscle");
            Require(s.Fact("muscle", "opened") == 0 && s.splits == 0, label + ": two stationary retractors do not split the muscle");
            Pull(s, s.first, a, Vector3.down, s.second, b, Vector3.up, .02f);
            var split = s.records.LastOrDefault(r => r.action.tissueId == "muscle" && r.action.verb == "retract" && r.action.separationMm >= 15);
            Require(s.splits >= 1 && s.volume.Wall.Volume.CutFaceCount > s.volume.Wall.Volume.CutFacesForMaterial("skin"),
                label + ": the paired pull splits the actual muscle volume");
            Require(split != null && s.Fact("muscle", "opened") == 1 && s.Fact("muscle", "splitWidthMm") >= 15 && split.action.angleDegrees <= 25 &&
                split.action.secondaryInstanceId != "" && split.action.secondaryInstanceId != split.action.instrumentInstanceId,
                label + $": measured split width {s.Fact("muscle", "splitWidthMm"):F1} mm along the fibers from two distinct gripped retractors: "
                + string.Join(",", s.records.Where(r => r.action.tissueId == "muscle").Select(r => r.action.verb + ":" + r.action.instrumentInstanceId.Substring(Math.Max(0, r.action.instrumentInstanceId.Length - 3)) + ":" + r.action.secondaryInstanceId.Length + ":" + r.action.separationMm + ":" + r.action.angleDegrees + ":" + string.Join("|", r.outcomes))));
            return split;
        }

        static (float split, float lift, float angle) VerifyOpenWall(Rig s, string label)
        {
            s.NewAttempt();
            OpenToMuscle(s, label);
            var split = Split(s, label);
            // The rendered wall itself parts: each retractor's measured material point lies on the committed
            // surface, and the two lie at least the split width further apart across the fibers.
            var lips = new[] { s.first, s.second }.Select(t => s.records.Last(r => r.action.verb == "retract" && r.action.instrumentInstanceId == s.Instance(t))).ToArray();
            Vector3 lipA = s.World(lips[0]), lipB = s.World(lips[1]);
            float across = Mathf.Abs(Vector3.Dot(lipB - lipA, s.wound.up)) * 1000;
            Require(s.volume.Wall.Visible && SurfaceDistance(s, lipA) < 1.5f * .001f && SurfaceDistance(s, lipB) < 1.5f * .001f && across >= 16 + 15,
                label + $": the visible wall surface opens with the retractors ({across:F1} mm apart across the fibers)");
            // Steady held grips: the adapter allocates nothing per frame between measurement reports.
            long allocated = -1;
            for (int batch = 0; batch < 5 && allocated != 0; batch++)
            {
                int before = s.records.Count; long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10; i++) s.input.Simulate(.001f);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
                if (s.records.Count == before) allocated = bytes;
            }
            Require(allocated == 0, label + ": held wall grips and layer contact allocate no managed memory per adapter frame (" + allocated + " bytes)");
            // Let go: the latched retractors keep holding the actual muscle open.
            s.first.SetHeld(false); s.second.SetHeld(false); s.Step(30);
            Require(s.first.GetComponent<SurgeryInstrumentLatch>().Attached && s.second.GetComponent<SurgeryInstrumentLatch>().Attached &&
                SurfaceDistance(s, lipA) < 2f * .001f && SurfaceDistance(s, lipB) < 2f * .001f,
                label + ": released retractors stay placed and the muscle stays open");
            // Tent: forceps grip the membrane in the split and lift it; the accepted lift is the measurement.
            s.Hold(s.forceps); Vector3 grip = new Vector3(.01f, 0, .027f); s.Place(s.forceps, grip); s.Step(6);
            Require(s.Fact("peritoneum", "tented") == 0 && s.records.Any(r => r.action.verb == "grasp" && r.action.tissueId == "peritoneum"),
                label + ": forceps grip the exposed peritoneum without tenting it");
            for (int i = 1; i <= 15; i++) { s.Place(s.forceps, grip - Vector3.forward * (i * .001f)); s.Step(); }
            s.Step(10);
            float lift = (float)s.Fact("peritoneum", "liftMm");
            Require(s.Fact("peritoneum", "tented") == 1 && lift >= 8 && lift <= 16, label + $": accepted membrane lift {lift:F1} mm tents the peritoneum");
            // Nick beside the forceps, at the lifted membrane surface the learner can see.
            Vector3 membrane = s.wound.InverseTransformPoint(s.World(s.records.Last(r => r.action.verb == "grasp" && r.action.tissueId == "peritoneum")));
            Require(s.volume.TryContactLayer("peritoneum", s.wound.TransformPoint(new Vector3(membrane.x - .008f, 0, membrane.z)), .008f, out var surface),
                label + ": the lifted membrane surface is reachable beside the forceps");
            float depth = s.wound.InverseTransformPoint(surface).z + .001f;
            s.Stroke(membrane.x - .012f, membrane.x - .004f, 0, depth);
            Require(LastCut(s, "peritoneum") != null && s.Fact("peritoneum", "opened") == 1 && s.Fact("peritoneum", "tentedBeforeCut") == 1 &&
                !s.mistakes.Contains("lift_first") && s.volume.Wall.Volume.CutFacesForMaterial("peritoneum") > 0,
                label + ": a nick through the actually lifted membrane is a tented opening");
            s.Park(s.forceps);
            int persistentCuts=s.volume.Wall.Volume.CutFaceCount;
            int persistentTopology=s.volume.Wall.Volume.TopologyRevision;
            s.gate=false;s.Step(3);
            Require(s.volume.Wall.Volume.CutFaceCount==persistentCuts&&s.volume.Wall.Volume.TopologyRevision==persistentTopology,
                label+": temporary interaction/registration loss never heals fractured wall topology");
            s.gate=true;s.Step(3);
            Require(s.volume.Wall.Volume.CutFaceCount==persistentCuts&&s.Fact("skin","opened")==1&&s.Fact("peritoneum","opened")==1,
                label+": returning to practice restores the same cuts and body opening history");
            return ((float)s.Fact("muscle", "splitWidthMm"), lift, (float)split.action.angleDegrees);
        }

        static void VerifyNegatives(Rig s, string label)
        {
            s.NewAttempt();
            OpenToMuscle(s, label);
            // One retractor pulling alone stretches the muscle but cannot split it.
            Vector3 a = new Vector3(-.01f, -.008f, .019f);
            s.Hold(s.first); s.Place(s.first, a); s.Step(6);
            Pull(s, s.first, a, Vector3.down, null, default, default, .02f);
            Require(s.records.Any(r => r.action.verb == "retract" && r.action.tissueId == "muscle") && s.Fact("muscle", "opened") == 0 &&
                s.splits == 0 && s.records.All(r => r.action.separationMm == 0 && r.action.secondaryInstanceId == ""),
                label + ": a single retractor does not split the muscle");
            s.Park(s.first); s.Step(60);
            // Isolate this negative from the prior off-centre single-grip strain/material history.
            // A fresh opened wall must still refuse an along-fiber split in either presentation frame.
            s.NewAttempt();
            OpenToMuscle(s, label);
            // Two retractors pulling apart along the fibers (across the would-be split) do not split it.
            Vector3 left = new Vector3(-.015f, 0, .019f), right = new Vector3(-.005f, 0, .019f);
            s.Hold(s.first); s.Hold(s.second); s.Place(s.first, left); s.Place(s.second, right); s.Step(6);
            Pull(s, s.first, left, Vector3.left, s.second, right, Vector3.right, .02f);
            Require(s.records.Count(r => r.action.verb == "retract" && r.action.tissueId == "muscle" && r.action.secondaryInstanceId != "") > 0 &&
                s.Fact("muscle", "opened") == 0 && s.splits == 0 && !s.records.Any(r => r.action.separationMm >= 15),
                label + $": pulling apart along the fibers does not split the muscle (pairedRecords={s.records.Count(r => r.action.verb == "retract" && r.action.tissueId == "muscle" && r.action.secondaryInstanceId != "")}, opened={s.Fact("muscle", "opened")}, fractures={s.splits}, maxSeparationMm={s.records.Select(r => r.action.separationMm).DefaultIfEmpty(0).Max():F3})");
            s.Park(s.first); s.Park(s.second); s.Step(60);
            // Fresh wall: split properly, then nick the membrane the forceps hold without lifting it.
            s.NewAttempt();
            OpenToMuscle(s, label);
            Split(s, label);
            s.Hold(s.forceps); s.Place(s.forceps, new Vector3(.01f, 0, .027f)); s.Step(12);
            Require(s.records.Any(r => r.action.verb == "grasp" && r.action.tissueId == "peritoneum") && s.Fact("peritoneum", "tented") == 0 && s.Fact("peritoneum", "liftMm") < 8,
                label + ": a held but unlifted membrane is not tented");
            s.Stroke(-.012f, -.004f, 0, .0295f);
            var nick = LastCut(s, "peritoneum");
            Require(nick != null && nick.outcomes.Contains("untented_cut") && s.Fact("peritoneum", "tentedBeforeCut") == 0 && s.mistakes.Contains("lift_first"),
                label + ": nicking the membrane without a real lift is an untented cut");
            s.Park(s.forceps);
        }
    }
}
