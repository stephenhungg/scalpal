using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Surgery;
using Scalpal.Surgery.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // The actual open-wall consumer, not controller-distance assertions. Finite placement samples
    // exercise the wound centre, old 20 mm boundaries and minimum 8 mm opposing tip spacing.
    // Wide end-of-incision fixtures are positive only where both real contacts exist.
    // These are authored synthetic inputs; Editor timings are not Quest performance evidence.
    public static class NativeWoundResolutionValidation
    {
        const float Dt = .02f, RequiredOpeningMm = 8;
        static int checks, positivePassed, negativePassed;
        static void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Wound resolution: " + message);
        }
        struct Placement
        {
            public float x, y, spacing;
            public bool expectedOutsideSurface;
            public string Label => $"x={x * 1000:F1} y={y * 1000:F1} spacing={spacing * 1000:F1} mm";
        }
        static IEnumerable<Placement> Placements()
        {
            foreach (float x in new[] { -.03f, -.02f, -.01f, 0, .01f, .02f, .03f })
                foreach (float spacing in new[] { .008f, .01f, .016f })
                    yield return new Placement { x = x, spacing = spacing, expectedOutsideSurface = Mathf.Abs(x) == .03f && spacing == .016f };
            foreach (float x in new[] { -.01f, 0, .01f })
                foreach (float y in new[] { -.004f, .004f })
                    yield return new Placement { x = x, y = y, spacing = .008f };
            yield return new Placement { x = .12f, spacing = .008f, expectedOutsideSurface = true };
        }

        [MenuItem("Scalpal/Quest/Validate Wound Grip Resolution")]
        public static void Run()
        {
            checks = positivePassed = negativePassed = 0;
            ValidateLocalResolution();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new InvalidOperationException("Save or discard modified scenes before the wound sweep");
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            var failures = new List<string>();
            int placements = 0;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                var rig = new Rig(session, adapter);
                foreach (var placement in Placements()) Check(rig, placement, "default", failures, ref placements);
                // Same wound-local input in a displaced/rotated registration frame. No physical AR fit is claimed.
                var root = session.patientFrame.root;
                root.SetPositionAndRotation(root.position + new Vector3(1.3f, -.2f, .7f), Quaternion.Euler(4, 63, -3) * root.rotation);
                Physics.SyncTransforms();
                foreach (float x in new[] { -.02f, -.01f, 0, .01f, .02f })
                    Check(rig, new Placement { x = x, spacing = .008f }, "rotated", failures, ref placements);
                var cost = rig.volumeMilliseconds.OrderBy(v => v).ToArray();
                double p95 = cost.Length == 0 ? 0 : cost[(int)Math.Floor((cost.Length - 1) * .95)];
                Debug.Log($"SCALPAL_WOUND_RESOLUTION_MEASUREMENTS placements={placements} positivePassed={positivePassed} negativePassed={negativePassed} failures={failures.Count} originalNodes={rig.originalNodes} "
                    + $"cells={rig.cells} maximumActiveNodes={rig.maximumNodes} maximumCutFaces={rig.maximumCutFaces} sampledMaximumStepRetries={rig.maximumRetries} sampledMaximumStepBacktracks={rig.maximumBacktracks} "
                    + $"volumeSamples={cost.Length} editorVolumeP95Ms={p95:F3} editorVolumeMaxMs={(cost.Length == 0 ? 0 : cost[cost.Length - 1]):F3} "
                    + "includesSolverAndSurface=true headset=false synthetic=true");
                var solver=rig.solverMilliseconds.OrderBy(v=>v).ToArray();var surface=rig.surfaceMilliseconds.OrderBy(v=>v).ToArray();
                Debug.Log($"SCALPAL_WOUND_PHASE_TIMINGS solverP95Ms={solver[(int)((solver.Length-1)*.95)]:F3} surfaceP95Ms={surface[(int)((surface.Length-1)*.95)]:F3} "
                    + $"inputP95Ms={P95(rig.inputMilliseconds):F3} inputPlusVolumeP95Ms={P95(rig.totalMilliseconds):F3} excludesPhysicsSync=true editor=true headset=false");
                Require(failures.Count == 0, "all valid-contact samples must open and unavailable boundary/outside contacts must refuse splitting: " + string.Join("; ", failures));
                Debug.Log($"SCALPAL_WOUND_RESOLUTION_VALIDATION_OK checks={checks} placements={placements} positivePassed={positivePassed} negativePassed={negativePassed} "
                    + "actualScene=true actualConsumer=true minimumGripSpacingMm=8 requiredAcceptedOpeningMm=8 exhaustive=false headset=false");
            }
            finally
            {
                if (tissue) tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(previous);
            }
        }
        static double P95(List<double> values)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            return sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)];
        }
        static void ValidateLocalResolution()
        {
            var wall=TissueVolumeFactory.OpenAbdominalWall();
            Require(wall.Original.Length<=600&&wall.Cells.Length<=2400,"local refinement has bounded initial solver size");
            var inspected=new int[OpenWallLayers.Count];
            foreach(var cell in wall.Cells)
            {
                Vector3 min=Vector3.one*float.PositiveInfinity,max=Vector3.one*float.NegativeInfinity;
                for(int c=0;c<4;c++){var p=wall.Original[cell.Vertex(c)];min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
                Vector3 center=(min+max)*.5f;
                if(Mathf.Abs(center.x)>.03f||Mathf.Abs(center.y)>.012f)continue;
                Require(max.x-min.x<=.007501f&&max.y-min.y<=.006001f,
                    "every incision-region cell is <=7.5 mm along / <=6 mm across; no cell can contain two transverse grips 8 mm apart");
                inspected[cell.material]++;
            }
            foreach(int count in inspected)Require(count>0,"all five materials inherit conforming wound refinement");
        }
        static void Check(Rig rig, Placement placement, string frame, List<string> failures, ref int placements)
        {
            placements++;
            Debug.Log("SCALPAL_WOUND_PLACEMENT_BEGIN frame="+frame+" "+placement.Label);
            try { if (VerifyPlacement(rig, placement)) negativePassed++; else positivePassed++; }
            catch (InvalidOperationException error) { failures.Add(frame + " " + placement.Label + ": " + error.Message); }
        }

        sealed class Rig
        {
            public readonly NativeCaseSession session;
            public readonly OpenBodyInteraction input;
            public readonly AnatomyExerciseBinding exercise;
            public readonly Transform wound;
            public readonly NativeVolumeSimulation volume;
            public readonly List<BodyRecord> records = new List<BodyRecord>();
            public readonly List<double> solverMilliseconds=new List<double>(),surfaceMilliseconds=new List<double>();
            public readonly List<double> volumeMilliseconds = new List<double>();
            public readonly List<double> inputMilliseconds = new List<double>(), totalMilliseconds = new List<double>();
            public readonly InstrumentBehaviour scalpel, first, second;
            readonly Transform cutStart, cutEnd;
            public int splits, originalNodes, cells, maximumNodes, maximumCutFaces, maximumRetries, maximumBacktracks;
            public Rig(NativeCaseSession session, OpenSurgerySession adapter)
            {
                this.session = session; input = adapter.Interaction; exercise = session.exercise; wound = adapter.Wound.transform;
                volume = session.GetComponent<NativeVolumeSimulation>();
                Require(volume && volume.Wall && volume.Wall.transform.parent == wound, "actual scene wall uses the wound frame");
                input.Submitted += (record, _) => records.Add(record);
                volume.LayerFractured += fracture => { if (fracture.layerId == "muscle" && fracture.verb == "split") splits += fracture.newlyBrokenFaces; };
                scalpel = Tool("scalpel"); first = Tool("retractor", 0); second = Tool("retractor", 1);
                cutStart = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutStart");
                cutEnd = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutEnd");
            }
            InstrumentBehaviour Tool(string id, int index = 0)
            {
                var tool = session.workbench.tools.Where(t => t && t.instrumentId == id).ElementAtOrDefault(index);
                Require(tool && tool.actionPoint, "scene instrument exists: " + id + "#" + index); return tool;
            }
            public void Reset()
            {
                var selected = exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out string reason), "fresh body attempt: " + reason);
                session.anatomy.SetRegistrationValid(true);
                foreach (var tool in session.workbench.tools)
                {
                    if (!tool) continue;
                    tool.SetHeld(false); var latch = tool.GetComponent<SurgeryInstrumentLatch>(); if (latch) latch.Clear();
                }
                volume.Initialize(wound, session.workbench, () => true, true);
                input.Initialize(exercise, session.workbench.tools, session.patientFrame, wound, () => true);
                records.Clear(); splits = 0;
                originalNodes = volume.Wall.Volume.Original.Length; cells = volume.Wall.Volume.Cells.Length;
                Step();
                Require(exercise.Body.Log.Count == 0 && volume.Wall.Volume.CutFaceCount == 0, "retry removes old body and volume state");
            }
            public void Step(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    long inputBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                    input.Simulate(Dt);
                    double inputMs = (System.Diagnostics.Stopwatch.GetTimestamp() - inputBegin) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                    long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                    volume.Simulate(Dt);
                    double volumeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                    volumeMilliseconds.Add(volumeMs); inputMilliseconds.Add(inputMs); totalMilliseconds.Add(inputMs + volumeMs);
                    solverMilliseconds.Add(volume.LastSolverMilliseconds);surfaceMilliseconds.Add(volume.LastSurfaceMilliseconds);
                    maximumRetries = Math.Max(maximumRetries, volume.Wall.Volume.LastStepRetries);
                    maximumBacktracks = Math.Max(maximumBacktracks, volume.Wall.Volume.LastStepBacktracks);
                    maximumNodes = Math.Max(maximumNodes, volume.Wall.Volume.NodeCount);
                    maximumCutFaces = Math.Max(maximumCutFaces, volume.Wall.Volume.CutFaceCount);
                    Physics.SyncTransforms();
                }
            }
            public void Hold(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
            public void Place(InstrumentBehaviour tool, Vector3 local)
            {
                tool.transform.rotation = wound.rotation;
                tool.transform.position += wound.TransformPoint(local) - tool.actionPoint.position;
                Physics.SyncTransforms();
            }
            public void Stroke(float depth)
            {
                Hold(scalpel);
                for (int i = 0; i <= 12; i++)
                {
                    scalpel.transform.rotation = wound.rotation;
                    scalpel.transform.rotation = Quaternion.FromToRotation(cutEnd.position - cutStart.position, wound.forward) * scalpel.transform.rotation;
                    scalpel.transform.position += wound.TransformPoint(new Vector3(-.03f + .005f * i, 0, depth)) - cutEnd.position;
                    Physics.SyncTransforms(); Step();
                }
                scalpel.SetActivation(0); Step(); scalpel.SetHeld(false); Place(scalpel, new Vector3(.25f, .25f, -.3f)); Step();
            }
            public string Instance(InstrumentBehaviour tool) => "tool-" + tool.GetInstanceID().ToString().Replace("-", "n");
            public BodyRecord Record(InstrumentBehaviour tool, bool last)
            {
                var matches = records.Where(r => r.action.verb == "retract" && r.action.tissueId == "muscle" && r.action.instrumentInstanceId == Instance(tool));
                return last ? matches.LastOrDefault() : matches.FirstOrDefault();
            }
            public Vector3 World(BodyRecord record) => session.patientFrame.TransformPoint(new Vector3(record.action.position.x, record.action.position.y, record.action.position.z));
        }

        static bool VerifyPlacement(Rig s, Placement placement)
        {
            s.Reset(); s.Stroke(.0015f); s.Stroke(.01f); s.Stroke(.0195f);
            Require(new[] { "skin", "fat", "fascia" }.All(id => s.exercise.Body.Get(id, "opened") == 1), "actual blade opens the three preceding layers");
            Vector3 a = new Vector3(placement.x, placement.y - placement.spacing / 2, .019f);
            Vector3 b = new Vector3(placement.x, placement.y + placement.spacing / 2, .019f);
            bool contactA=s.volume.TryContactLayer("muscle",s.wound.TransformPoint(a),.006f,out var hitA);
            bool contactB=s.volume.TryContactLayer("muscle",s.wound.TransformPoint(b),.006f,out var hitB);
            Debug.Log($"SCALPAL_WOUND_CONTACT {placement.Label} first={contactA} second={contactB} firstDistanceMm={(contactA?Vector3.Distance(s.wound.TransformPoint(a),hitA)*1000:-1):F3} secondDistanceMm={(contactB?Vector3.Distance(s.wound.TransformPoint(b),hitB)*1000:-1):F3}");
            s.Hold(s.first); s.Hold(s.second); s.Place(s.first, a); s.Place(s.second, b);
            if (placement.x == .12f)Require(!contactA&&!contactB,"far-wall fixture has no material contact");
            if (placement.expectedOutsideSurface && !(contactA && contactB))
            {
                // At the wide incision endpoints a lip is outside the exposed muscle;
                // the +120 mm fixture is beyond the complete 160 mm wall width.
                // Do not turn a nonexistent wall contact into successful muscle splitting.
                Require(!contactA || !contactB, "explicit far-wall negative has at least one unavailable muscle contact");
                s.Step(6);
                RequireNoSplit(s, "stationary outside-surface grips");
                for (int i = 1; i <= 20; i++)
                {
                    s.Place(s.first, a - Vector3.up * (i * .001f)); s.Place(s.second, b + Vector3.up * (i * .001f)); s.Step();
                    RequireNoSplit(s, "outside-surface opposing pull " + i);
                }
                s.Step(15); RequireNoSplit(s, "settled outside-surface pull");
                return true;
            }
            Require(contactA && contactB, "positive placement has two physically available muscle contacts");
            s.Step(6);
            var startA = s.Record(s.first, false); var startB = s.Record(s.second, false);
            Require(startA != null && startB != null && s.volume.Wall.Volume.MaterialHandleCount == 2, "two distinct physical muscle grips acquire exposed material");
            Require(s.splits == 0 && s.exercise.Body.Get("muscle", "opened") == 0, "stationary opposing grips do not award splitting");
            float initialGap = Mathf.Abs(Vector3.Dot(s.World(startB) - s.World(startA), s.wound.up)) * 1000;
            int facesBefore = s.volume.Wall.Volume.CutFaceCount;
            for (int i = 1; i <= 20; i++)
            {
                s.Place(s.first, a - Vector3.up * (i * .001f)); s.Place(s.second, b + Vector3.up * (i * .001f)); s.Step();
            }
            s.Step(15);
            var endA = s.Record(s.first, true); var endB = s.Record(s.second, true);
            Require(endA != null && endB != null && endA != startA && endB != startB, "both grips report accepted post-pull geometry");
            Require(s.splits > 0 && s.volume.Wall.Volume.CutFaceCount > facesBefore, "paired movement produces new muscle split topology");
            float finalGap = Mathf.Abs(Vector3.Dot(s.World(endB) - s.World(endA), s.wound.up)) * 1000;
            float acceptedIncrease = finalGap - initialGap;
            Require(acceptedIncrease >= RequiredOpeningMm, $"actual material lip gap increases >=8 mm (accepted={acceptedIncrease:F2} mm)");
            Require(endA.action.separationMm >= RequiredOpeningMm && endB.action.separationMm >= RequiredOpeningMm,
                "consumer reports >=8 mm from material motion, not requested controller spread");
            Require(s.exercise.Body.Get("muscle", "opened") == 1 && s.exercise.Body.Get("muscle", "splitWidthMm") >= 15,
                "20 mm opposing pulls also satisfy the unchanged production 15 mm split milestone");
            Require(Math.Abs(endA.action.separationMm - acceptedIncrease) <= 1.5 && Math.Abs(endB.action.separationMm - acceptedIncrease) <= 1.5,
                "quantized body split measurement agrees with the independently recomputed material-point separation");
            Require(s.volume.Wall.Visible && SurfaceDistance(s, s.World(endA)) < .0015f && SurfaceDistance(s, s.World(endB)) < .0015f,
                "both measured lips remain on the visible accepted wall surface");
            Require(s.volume.Wall.Volume.NodeCount <= TissueVolume.MaxNodes && s.volume.Wall.Volume.CutFaceCount + s.volume.Wall.Volume.SeparatedFaceCount <= s.volume.Wall.Volume.CutFaceBudget,
                "refined wall bounds total cut and separated faces inside the runtime topology budgets");
            VerifyIntactAnteriorMembrane(s, placement);
            return false;
        }
        static void VerifyIntactAnteriorMembrane(Rig s, Placement placement)
        {
            var volume = s.volume.Wall.Volume;
            Require(volume.CutFacesForMaterial("peritoneum") == 0 && s.exercise.Body.Get("peritoneum", "opened") == 0,
                "actual muscle splitting exposes the membrane without incising it or awarding its open milestone");
            Require(volume.SeparatedFacesForMaterials("muscle", "peritoneum") > 0,
                "real paired muscle split releases a finite underlying interface");
            var membrane = OpenWallLayers.Get(OpenWallLayers.Count - 1);
            int material = Array.FindIndex(volume.Materials, value => value.id == membrane.id);
            Vector3 wanted = new Vector3(placement.x, placement.y, membrane.startDepthMeters);
            Vector3 anterior = default;
            float closest = float.PositiveInfinity;
            foreach (var face in volume.Faces)
            {
                if (!face.separated || face.neighbor < 0 ||
                    Mathf.Abs(volume.Original[face.a].z - membrane.startDepthMeters) > 1e-6f ||
                    Mathf.Abs(volume.Original[face.b].z - membrane.startDepthMeters) > 1e-6f ||
                    Mathf.Abs(volume.Original[face.c].z - membrane.startDepthMeters) > 1e-6f) continue;
                int owner = volume.Cells[face.cell].material == material ? face.cell : face.neighbor;
                if (volume.Cells[owner].material != material) continue;
                Vector3 centre = (BoundPosition(volume, owner, face.a) + BoundPosition(volume, owner, face.b) + BoundPosition(volume, owner, face.c)) / 3;
                float distance = (centre - wanted).sqrMagnitude;
                if (distance >= closest) continue;
                closest = distance; anterior = centre;
            }
            Require(closest < .02f * .02f, "an actual separated anterior membrane triangle is present near this muscle opening");
            Vector3 world = s.volume.Wall.transform.TransformPoint(anterior);
            Require(s.volume.TryContactLayer("peritoneum", world, .00015f, out Vector3 contact) && Vector3.Distance(world, contact) < .00001f,
                "public material contact reaches the current anterior membrane face, rather than its posterior exterior");
        }
        static Vector3 BoundPosition(TissueVolume volume, int owner, int root)
        {
            for (int corner = 0; corner < 4; corner++)
                if (volume.Cells[owner].Vertex(corner) == root) return volume.Positions[volume.NodeFor(owner, corner)];
            throw new InvalidOperationException("Membrane contact face is not owned by its declared cell");
        }
        static void RequireNoSplit(Rig s, string label)
        {
            Require(s.splits == 0 && s.exercise.Body.Get("muscle", "opened") == 0 && s.exercise.Body.Get("muscle", "splitWidthMm") == 0,
                label + ": unavailable second material contact cannot create muscle topology or a scored split milestone");
        }
        static float SurfaceDistance(Rig s, Vector3 world)
        {
            var mesh = s.volume.Wall.Surface; var vertices = mesh.vertices; var triangles = mesh.triangles;
            Vector3 local = s.volume.Wall.transform.InverseTransformPoint(world);
            float best = float.PositiveInfinity;
            for (int i = 0; i < triangles.Length; i += 3)
                best = Mathf.Min(best, (TissueVolume.ClosestTriangle(local, vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]) - local).magnitude);
            return s.volume.Wall.transform.TransformVector(Vector3.right).magnitude * best;
        }
    }
}
