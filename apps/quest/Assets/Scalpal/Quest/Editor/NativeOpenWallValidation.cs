using System;
using System.Reflection;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Actual public volume mechanics with authored synthetic teaching geometry.
    // This is not measured tissue, clinical validation, or physical Quest evidence.
    public static class NativeOpenWallValidation
    {
        static int checks;
        static void Assert(bool valid, string message)
        { checks++; if (!valid) throw new InvalidOperationException("Open wall: " + message); }
        static bool Near(float a, float b, float tolerance = 1e-6f) => Mathf.Abs(a - b) <= tolerance;
        static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-6f) => (a - b).sqrMagnitude <= tolerance * tolerance;
        static readonly string[] Ids = { "skin", "fat", "fascia", "muscle", "peritoneum" };
        static readonly float[] Depths = { 0, .002f, .014f, .019f, .027f, .028f };

        [MenuItem("Scalpal/Quest/Validate Open Wall Tissue APIs")]
        public static void Run()
        {
            checks = 0;
            ValidateDescriptors();
            ValidateLayerGeometry();
            ValidateContactsAndFracture();
            ValidateActualMaterialHandles();
            ValidatePeritoneumNick();
            ValidateWorldFacade(.8f);
            ValidateWorldFacade(1.2f);
            ValidateScaledBladeTolerance(.8f);
            ValidateScaledBladeTolerance(1.2f);
            ValidateInvalidWorldFrames();
            ValidateBleedingSnapshots();
            Debug.Log("SCALPAL_NATIVE_OPEN_WALL_VALIDATION_OK checks=" + checks +
                " actual public volume layers/contact/fracture/two-grip mechanics; authored geometry; headset=false clinical=false");
        }

        static void ValidateDescriptors()
        {
            Assert(OpenWallLayers.Count == Ids.Length, "exactly five stable layer descriptors");
            for (int i = 0; i < Ids.Length; i++)
            {
                var layer = OpenWallLayers.Get(i);
                Assert(layer.index == i && layer.id == Ids[i], "stable ordered ID " + Ids[i]);
                Assert(Near(layer.startDepthMeters, Depths[i]) && Near(layer.endDepthMeters, Depths[i + 1]), "SI depth " + Ids[i]);
                Assert(layer.ThicknessMeters > 0 && !string.IsNullOrWhiteSpace(layer.provenance), "positive authored thickness with provenance");
                Assert(OpenWallLayers.TryGet(Ids[i], out var found) && found.index == i, "exact ID lookup");
                Assert(OpenWallLayers.TryAtDepth((Depths[i] + Depths[i + 1]) * .5f, out found) && found.index == i, "interior depth lookup");
                Assert(OpenWallLayers.TryAtDepth(Depths[i], out found) && found.index == i, "interface belongs to deeper layer");
                Assert(layer.splittable == (Ids[i] == "muscle") && layer.tentable == (Ids[i] == "peritoneum"), "split/tent semantics");
                Assert(layer.cuttable, "wrong-layer cutting remains physically possible rather than case-gated");
            }
            Assert(OpenWallLayers.TryAtDepth(.028f, out var deepest) && deepest.id == "peritoneum", "final inward boundary belongs to membrane");
            foreach (var invalid in new[] { -.00001f, .0281f, float.NaN, float.PositiveInfinity })
                Assert(!OpenWallLayers.TryAtDepth(invalid, out _), "invalid depth cannot fabricate a layer");
            Assert(!OpenWallLayers.TryGet("Skin", out _) && !OpenWallLayers.TryGet("bowel", out _) && !OpenWallLayers.TryGet(null, out _), "unknown/case-mismatched IDs have no fallback");
            Assert(OpenWallLayers.TryFiberAngle("fascia", Vector3.right, out float parallel) && Near(parallel, 0), "parallel fascia incision angle is zero");
            Assert(OpenWallLayers.TryFiberAngle("fascia", Vector3.left, out parallel) && Near(parallel, 0), "reverse fiber direction is equivalent");
            Assert(OpenWallLayers.TryFiberAngle("fascia", Vector3.up, out float cross) && Near(cross, 90, .001f), "perpendicular fascia angle is ninety");
            Assert(OpenWallLayers.TryFiberAngle("fascia", new Vector3(1, 1, 4), out float diagonal) && Near(diagonal, 45, .001f), "fiber angle ignores normal component");
            Assert(OpenWallLayers.TryFiberAngle("muscle", Vector3.right, out parallel) && Near(parallel, 0), "muscle authored fiber axis exposed");
            Assert(!OpenWallLayers.TryFiberAngle("skin", Vector3.right, out _) && !OpenWallLayers.TryFiberAngle("fascia", Vector3.forward, out _) &&
                !OpenWallLayers.TryFiberAngle("fascia", Vector3.zero, out _) && !OpenWallLayers.TryFiberAngle("fascia", new Vector3(float.NaN, 0, 0), out _), "absent/nonfinite fibers cannot become alignment evidence");
            var asis = new Vector3(-.12f, .9f, -.1f); var navel = new Vector3(0, 1.05f, -.1f);
            Assert(OpenWallLayers.TryMcBurney(asis, navel, out var point) && Near(point, asis + (navel - asis) / 3), "McBurney interpolation follows explicit same-frame landmarks");
            Assert(!Near(point, navel), "wound centre is not silently the umbilicus");
            Assert(!OpenWallLayers.TryMcBurney(asis, asis, out _) && !OpenWallLayers.TryMcBurney(new Vector3(float.NaN, 0, 0), navel, out _), "degenerate/absent landmarks rejected");
        }

        static void ValidateLayerGeometry()
        {
            var wall = TissueVolumeFactory.OpenAbdominalWall();
            Assert(wall.Materials.Length == Ids.Length, "five actual volume materials");
            var counts = new int[Ids.Length];
            var minimum = new float[Ids.Length]; var maximum = new float[Ids.Length];
            for (int i = 0; i < Ids.Length; i++)
            {
                minimum[i] = float.PositiveInfinity; maximum[i] = float.NegativeInfinity;
                Assert(wall.Materials[i].id == Ids[i] && wall.Materials[i].HasValidUnits, "descriptor ID agrees with actual SI material");
            }
            foreach (var cell in wall.Cells)
            {
                Assert(cell.material >= 0 && cell.material < Ids.Length, "every cell has an explicit material identity");
                counts[cell.material]++;
                for (int corner = 0; corner < 4; corner++)
                {
                    float depth = wall.Original[cell.Vertex(corner)].z;
                    minimum[cell.material] = Mathf.Min(minimum[cell.material], depth);
                    maximum[cell.material] = Mathf.Max(maximum[cell.material], depth);
                }
            }
            for (int i = 0; i < Ids.Length; i++)
                Assert(counts[i] > 0 && Near(minimum[i], Depths[i]) && Near(maximum[i], Depths[i + 1]), "actual cell slab matches " + Ids[i] + " descriptor");
            PositiveJacobians(wall);
        }

        static void ValidateContactsAndFracture()
        {
            var wall = TissueVolumeFactory.OpenAbdominalWall();
            Assert(wall.TryMaterialContact("skin", Vector3.zero, .001f, out var skin) && Near(skin.z, 0), "actual exposed anterior skin contact");
            Assert(wall.TryMaterialContact("peritoneum", new Vector3(0, 0, .028f), .001f, out var membrane) && Near(membrane.z, .028f), "actual exposed posterior membrane contact");
            Assert(!wall.TryMaterialContact("muscle", new Vector3(0, 0, .023f), .001f, out _), "intact inner muscle interface is not an exposed surface");
            Assert(!wall.TryMaterialContact("fascia", new Vector3(0, 0, .017f), .001f, out _), "intact fascia cannot be contacted through skin");
            Assert(!wall.TryMaterialContact("unknown", Vector3.zero, .01f, out _) && !wall.TryMaterialContact("Skin", Vector3.zero, .01f, out _), "unknown contact material rejected");
            foreach (float radius in new[] { 0f, -.01f, float.NaN, float.PositiveInfinity, .051f })
                Assert(!wall.TryMaterialContact("skin", Vector3.zero, radius, out _), "invalid/unbounded contact radius rejected");
            Assert(!wall.TryMaterialContact("skin", new Vector3(float.PositiveInfinity, 0, 0), .01f, out _), "nonfinite contact rejected");
            var a = new Vector3(0, -.025f, -.001f); var b = new Vector3(0, .025f, -.001f); var c = new Vector3(0, .025f, .003f);
            int baseline = wall.CutFaceCount;
            Assert(wall.FractureMaterialSweep("unknown", a, b, c, .0002f) == 0 && wall.CutFaceCount == baseline, "unknown fracture cannot mutate topology");
            Assert(wall.FractureMaterialSweep("skin", a, a, c, .0002f) == 0, "degenerate material sweep rejected");
            Assert(wall.FractureMaterialSweep("skin", a, b, c, float.NaN) == 0, "nonfinite fracture tolerance rejected");
            int created = wall.FractureMaterialSweep("skin", a, b, c, .0002f);
            Assert(created > 0 && wall.CutFacesForMaterial("skin") == created, "finite skin blade fractures actual shared skin faces");
            for (int i = 1; i < Ids.Length; i++) Assert(wall.CutFacesForMaterial(Ids[i]) == 0, "skin blade does not fracture other layers/interfaces");
            Assert(wall.CutFacesForMaterial("unknown") == 0 && wall.FractureMaterialSweep("skin", a, b, c, .0002f) == 0, "unknown cut count and duplicate blade remain bounded");
            // Cutting through the slab exposes real interior faces; do not synthesize a contact at an intact interface.
            var deepA = new Vector3(0, -.025f, .018f); var deepB = new Vector3(0, .025f, .018f); var deepC = new Vector3(0, .025f, .028f);
            Assert(wall.FractureMaterialSweep("muscle", deepA, deepB, deepC, .0002f) > 0, "finite wrong-layer blade can expose actual muscle faces");
            Assert(wall.TryMaterialContact("muscle", new Vector3(0, .015f, .024f), .004f, out var muscle) && TissueCage.Finite(muscle), "cut muscle contact comes from actual exposed geometry");
            PositiveJacobians(wall);
        }

        static TissueVolume MuscleFixture()
        {
            var material = new VolumeMaterial { id = "muscle", youngPascals = 60000, poissonRatio = .3f,
                densityKgPerCubicMeter = 1050, color = Color.red, measurementSource = "synthetic API fixture, not calibrated tissue" };
            return TissueVolumeFactory.Box(new Bounds(Vector3.zero, new Vector3(.08f, .08f, .008f)), 4, 4, 1, material, true);
        }

        static void ValidateActualMaterialHandles()
        {
            var capped = MuscleFixture();
            Assert(TissueVolume.MaximumMaterialHandles == 4, "finite material handle budget is explicit");
            for (int i = 0; i < TissueVolume.MaximumMaterialHandles; i++)
                Assert(capped.BeginMaterialHandle(100 + i, "muscle", new Vector3((i % 2 == 0 ? -1 : 1) * .01f, (i < 2 ? -1 : 1) * .01f, -.004f), .002f), "each budgeted actual handle attaches");
            Assert(!capped.BeginMaterialHandle(999, "muscle", new Vector3(0, 0, -.004f), .002f) && capped.MaterialHandleCount == 4,
                "fifth handle refuses without replacing an existing tool");
            capped.Freeze(); Assert(capped.MaterialHandleCount == 0, "freeze releases full handle budget");
            var volume = MuscleFixture();
            var leftPoint = new Vector3(0, -.025f, -.004f); var rightPoint = new Vector3(0, .025f, -.004f);
            Assert(!volume.BeginMaterialHandle(1, "unknown", leftPoint, .002f) && volume.MaterialHandleCount == 0, "unknown material cannot attach");
            Assert(!volume.BeginMaterialHandle(1, "muscle", Vector3.one, .002f), "remote handle cannot attach");
            Assert(!volume.BeginMaterialHandle(1, "muscle", leftPoint, float.NaN), "invalid handle radius cannot attach");
            Assert(volume.BeginMaterialHandle(11, "muscle", leftPoint, .002f) && volume.BeginMaterialHandle(22, "muscle", rightPoint, .002f), "two distinct actual material grips attach simultaneously");
            Assert(volume.MaterialHandleCount == 2 && !volume.BeginMaterialHandle(11, "muscle", rightPoint, .002f), "duplicate tool ID rejected without losing either grip");
            Assert(volume.TryMaterialHandlePosition(11, out var leftStart), "first actual anchor queryable");
            Assert(volume.TryMaterialHandlePosition(22, out var rightStart), "second actual anchor queryable");
            Assert(!volume.SetMaterialHandleTarget(11, new Vector3(float.NaN, 0, 0)) && !volume.SetMaterialHandleTarget(99, Vector3.zero), "invalid or missing handle target rejected");
            var before = (Vector3[])volume.Positions.Clone();
            Assert(volume.SetMaterialHandleTarget(11, leftStart + Vector3.down * .005f) && volume.SetMaterialHandleTarget(22, rightStart + Vector3.up * .005f), "independent opposing targets accepted");
            Assert(volume.TryMaterialHandlePosition(11, out var leftBefore) && Near(leftBefore, leftStart) &&
                volume.TryMaterialHandlePosition(22, out var rightBefore) && Near(rightBefore, rightStart), "requested targets do not fabricate material motion before solver step");
            EqualPositions(volume, before, "target updates do not mutate geometry");
            volume.Step(0, Vector3.zero, Vector3.zero); EqualPositions(volume, before, "invalid dt cannot fabricate motion");
            int accepted = 0;
            for (int step = 0; step < 90; step++)
            {
                volume.Step(1f / 90, Vector3.zero, Vector3.zero);
                if (volume.LastStepAccepted) accepted++;
                Assert(volume.MaterialHandleCount == 2, "numerical retry retains both grips");
            }
            Assert(accepted > 0, "actual held geometry advances accepted solver steps");
            Assert(volume.TryMaterialHandlePosition(11, out var leftMoved), "first held anchor survives solver motion");
            Assert(volume.TryMaterialHandlePosition(22, out var rightMoved), "second held anchor survives solver motion");
            Assert(leftStart.y - leftMoved.y > .0001f && rightMoved.y - rightStart.y > .0001f, "both grips produce nonzero opposite physical displacement");
            Assert(rightMoved.y - leftMoved.y > rightStart.y - leftStart.y + .0002f, "measured separation increases rather than reporting controller target separation");
            Assert((leftMoved - leftStart).magnitude < .02f && (rightMoved - rightStart).magnitude < .02f, "actual material motion remains within interactive bounded travel");
            Assert(volume.TryMaterialContact("muscle", leftMoved, .0005f, out var leftContact) && Near(leftContact, leftMoved, .0005f) &&
                volume.TryMaterialContact("muscle", rightMoved, .0005f, out var rightContact) && Near(rightContact, rightMoved, .0005f), "queried handle anchors lie on deformed material surface");
            PositiveJacobians(volume);
            // A nick must remap the material grip to its own cell/face fan, not drop it or move its anchor.
            var a = new Vector3(0, -.03f, -.01f); var b = new Vector3(0, .03f, -.01f); var c = new Vector3(0, .03f, .01f);
            Assert(volume.FractureMaterialSweep("muscle", a, b, c, .0002f) > 0, "finite cut actually changes held tissue topology");
            Assert(volume.MaterialHandleCount == 2 && volume.TryMaterialHandlePosition(11, out var leftCut) && Near(leftCut, leftMoved) &&
                volume.TryMaterialHandlePosition(22, out var rightCut) && Near(rightCut, rightMoved), "material-face anchors remain geometrically stable across cut fan duplication");
            int cuts = volume.CutFaceCount;
            volume.Step(1f / 90, Vector3.zero, Vector3.zero);
            Assert(volume.MaterialHandleCount == 2 && volume.LastStepAccepted, "remapped grips continue actual solver motion after nick");
            PositiveJacobians(volume);
            volume.ReleaseMaterialHandle(11);
            Assert(volume.MaterialHandleCount == 1 && !volume.TryMaterialHandlePosition(11, out _) && volume.TryMaterialHandlePosition(22, out _), "release one grip does not release the other");
            volume.ReleaseMaterialHandle(99); Assert(volume.MaterialHandleCount == 1, "unknown release leaves live handle intact");
            volume.Freeze();
            Assert(volume.MaterialHandleCount == 0 && !volume.TryMaterialHandlePosition(22, out _) && volume.CutFaceCount == cuts, "freeze releases grips and retains incision topology");
            volume.Reset();
            Assert(volume.MaterialHandleCount == 0 && volume.CutFaceCount == 0 && !volume.SetMaterialHandleTarget(22, Vector3.zero), "reset clears grips, topology and stale tool target");
            for (int cell = 0; cell < volume.Cells.Length; cell++) for (int corner = 0; corner < 4; corner++)
                Assert(Near(volume.Positions[volume.NodeFor(cell, corner)], volume.Original[volume.Cells[cell].Vertex(corner)]), "reset restores every bound material corner");
            Assert(volume.BeginMaterialHandle(33, "muscle", leftPoint, .002f), "reset volume can attach a new tool");
            volume.Reset(); Assert(volume.MaterialHandleCount == 0 && !volume.TryMaterialHandlePosition(33, out _), "reset also releases an actively held grip");
        }

        static void ValidatePeritoneumNick()
        {
            // Actual five-layer membrane held at a barycentric exposed surface point.
            // Skin fracture is unrelated; the later membrane nick remaps the held fan.
            var wall = TissueVolumeFactory.OpenAbdominalWall();
            var point = new Vector3(0, .01f, .028f);
            const int forceps = 901;
            Assert(wall.BeginMaterialHandle(forceps, "peritoneum", point, .001f), "forceps acquire actual five-layer peritoneum");
            Assert(wall.TryMaterialHandlePosition(forceps, out var baseline), "membrane forceps actual anchor queryable");
            Assert(wall.SetMaterialHandleTarget(forceps, baseline - Vector3.forward * .001f), "peritoneum tent target accepted");
            for (int step = 0; step < 20; step++) wall.Step(1f / 90, Vector3.zero, Vector3.zero);
            Assert(wall.TryMaterialHandlePosition(forceps, out var tented) && (tented - baseline).sqrMagnitude > 1e-16f,
                "five-layer membrane produces actual nonzero accepted tent deformation");
            Assert(wall.FractureMaterialSweep("skin", new Vector3(0, -.025f, -.001f), new Vector3(0, .025f, -.001f), new Vector3(0, .025f, .003f), .0002f) > 0,
                "unrelated finite skin blade changes real topology while membrane held");
            Assert(wall.MaterialHandleCount == 1 && wall.TryMaterialHandlePosition(forceps, out var afterSkin) && Near(afterSkin, tented),
                "peritoneum grip remains at actual identical face point after unrelated skin cut");
            // Nick the actual deformed membrane near its held point, with finite blade travel.
            var a = new Vector3(tented.x, tented.y - .015f, tented.z - .002f);
            var b = new Vector3(tented.x, tented.y + .015f, tented.z - .002f);
            var c = new Vector3(tented.x, tented.y + .015f, tented.z + .002f);
            Assert(wall.FractureMaterialSweep("peritoneum", a, b, c, .0002f) > 0 && wall.CutFacesForMaterial("peritoneum") > 0,
                "finite blade nicks real deformed held membrane");
            Assert(wall.MaterialHandleCount == 1 && wall.TryMaterialHandlePosition(forceps, out var nicked) && Near(nicked, tented),
                "peritoneum forceps token and same barycentric geometry survive its own nick");
            Assert(wall.TryMaterialContact("peritoneum", tented, .0005f, out var surface) && Near(surface, tented, .0005f),
                "held post-nick membrane remains an actual material surface contact");
            long beforeStep = wall.AcceptedStepSequence;
            Assert(wall.SetMaterialHandleTarget(forceps, baseline - Vector3.forward * .002f), "held nicked membrane takes next tent target");
            for (int step = 0; step < 20; step++) wall.Step(1f / 90, Vector3.zero, Vector3.zero);
            Assert(wall.AcceptedStepSequence > beforeStep && wall.MaterialHandleCount == 1 &&
                wall.TryMaterialHandlePosition(forceps, out var afterStep) && (afterStep - tented).sqrMagnitude > 1e-16f,
                "nick retains forceps and advances actual accepted membrane motion");
            PositiveJacobians(wall);
            wall.Freeze(); Assert(wall.MaterialHandleCount == 0, "tracking freeze releases actual nicked membrane forceps");
        }

        sealed class WorldFixture : IDisposable
        {
            public readonly GameObject root, rigObject;
            public readonly Transform wound;
            public readonly NativeVolumeSimulation simulation;
            public bool ready = true;
            public TissueVolume Volume => simulation.Wall.Volume;
            public WorldFixture(float scale)
            {
                root = new GameObject("OpenWallWorldApiFixture");
                root.transform.SetPositionAndRotation(new Vector3(.4f, 1.1f, -.3f), Quaternion.Euler(17, 38, -9));
                root.transform.localScale = Vector3.one * scale;
                wound = new GameObject("ExplicitMcBurneyWoundFrame").transform;
                wound.SetParent(root.transform, false);
                wound.localPosition = new Vector3(-.07f, -.04f, -.01f);
                wound.localRotation = Quaternion.Euler(7, -11, 4);
                rigObject = new GameObject("InactiveOpenWallApiRig"); rigObject.SetActive(false);
                var rig = rigObject.AddComponent<NativeWorkbench>();
                simulation = root.AddComponent<NativeVolumeSimulation>();
                simulation.Initialize(wound, rig, () => ready, true);
            }
            public Vector3 World(Vector3 local) => simulation.Wall.transform.TransformPoint(local);
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(rigObject);
            }
        }

        static void ValidateWorldFacade(float scale)
        {
            using (var f = new WorldFixture(scale))
            {
                var sim = f.simulation; var wall = sim.Wall.transform;
                Assert(wall.parent == f.wound && wall.localPosition == Vector3.zero && wall.localRotation == Quaternion.identity && wall.localScale == Vector3.one,
                    "open teaching volume inherits explicit McBurney origin without atlas projection");
                Assert(Near(wall.TransformPoint(Vector3.zero), f.wound.position), "anterior open-wall centre is the wound-frame origin");
                Assert(sim.TryContactLayer("skin", f.World(Vector3.zero), .001f * scale, out var contact) && Near(contact, f.wound.position), "rotated uniformly scaled world contact stays at actual material surface");
                Assert(!sim.TryContactLayer("muscle", f.World(new Vector3(0, 0, .023f)), .001f * scale, out _), "world facade cannot contact unopened inner tissue");
                Assert(!sim.TryContactLayer("skin", f.World(Vector3.zero), .011f, out _) &&
                    !sim.TryBeginLayerHandle("unknown", f.World(Vector3.zero), .001f, out _), "world radius/material negatives refuse");
                var localMembrane = new Vector3(0, .01f, .028f);
                sim.Simulate(1f / 90); // An older accepted step is not evidence for a newly acquired grip.
                Assert(sim.TryBeginLayerHandle("peritoneum", f.World(localMembrane), .001f * scale, out int token), "actual posterior membrane can be tented through facade");
                Assert(sim.TryMeasureLayerHandle(token, out var baseline) && baseline.layerId == "peritoneum" && !baseline.hasAcceptedStep && Near(baseline.worldDisplacement, Vector3.zero), "new grip has no fabricated historical displacement");
                Assert(sim.TrySetLayerHandleTarget(token, baseline.worldPosition - wall.forward * .005f * scale), "world tent target accepted");
                Assert(!sim.TrySetLayerHandleTarget(token, new Vector3(float.NaN, 0, 0)) && !sim.TrySetLayerHandleTarget(int.MaxValue, Vector3.zero), "world target nonfinite/stale tokens rejected");
                Assert(sim.TryMeasureLayerHandle(token, out var unstepped) && !unstepped.hasAcceptedStep && Near(unstepped.worldPosition, baseline.worldPosition) && unstepped.outwardLiftMillimeters == 0,
                    "controller travel without physical step is not a lift");
                for (int step = 0; step < 45; step++) sim.Simulate(1f / 90);
                Assert(sim.TryMeasureLayerHandle(token, out var lifted) && lifted.hasAcceptedStep, "actual accepted step establishes motion evidence");
                Assert(f.Volume.TryMaterialHandlePosition(token, out var localPosition) && Near(lifted.worldPosition, wall.TransformPoint(localPosition)), "world measurement uses actual material anchor");
                Vector3 expectedDisplacement = wall.TransformVector(localPosition - wall.InverseTransformPoint(baseline.worldPosition));
                Assert(Near(lifted.worldDisplacement, expectedDisplacement), "world displacement includes uniform scale and rotation");
                float expectedLift = Vector3.Dot(expectedDisplacement, -wall.forward) * 1000;
                Assert(Near(lifted.outwardLiftMillimeters, expectedLift, .0001f) && expectedLift > .001f,
                    "outward lift is nonzero accepted geometry in world millimetres at scale " + scale);
                Assert((lifted.worldPosition - baseline.worldPosition).magnitude < .015f && lifted.topologyRevision == f.Volume.TopologyRevision, "bounded actual movement and actual topology revision");
                PositiveJacobians(f.Volume);
                sim.ReleaseLayerHandle(token);
                Assert(!sim.TryMeasureLayerHandle(token, out _) && !sim.TrySetLayerHandleTarget(token, baseline.worldPosition), "released facade token cannot act or produce evidence");

                // A mechanically split surface follows +X fibers and +Z wall depth.
                int splitEvents = 0;
                sim.LayerFractured += fracture => { if (fracture.verb == "split") splitEvents++; };
                var splitA = new Vector3(-.04f, 0, .018f); var splitB = new Vector3(.04f, 0, .018f); var splitC = new Vector3(.04f, 0, .028f);
                Assert(!sim.TrySplitMuscle(f.World(new Vector3(0, -.04f, .018f)), f.World(new Vector3(0, .04f, .018f)), f.World(new Vector3(0, .04f, .028f)), out _), "across-fiber split plane refused");
                Assert(!sim.TrySplitMuscle(f.World(splitA), f.World(splitA), f.World(splitC), out _), "degenerate split refused");
                Assert(sim.TrySplitMuscle(f.World(splitA), f.World(splitB), f.World(splitC), out var split) && split.newlyBrokenFaces > 0 && split.layerId == "muscle" && split.verb == "split" && split.fiberAngleDegrees == 0,
                    "fiber-parallel mechanical split exposes actual faces and emits its distinct verb");
                Assert(splitEvents == 1 && split.topologyRevision == f.Volume.TopologyRevision, "only accepted fracture emits current topology evidence");
                Assert(!sim.TrySplitMuscle(f.World(splitA), f.World(splitB), f.World(splitC), out _) && splitEvents == 1, "duplicate split does not fabricate another event");
                Assert(sim.TryBeginLayerHandle("muscle", f.World(new Vector3(-.03f, 0, .023f)), .006f * scale, out int first), "first exposed muscle surface acquires facade handle");
                Assert(sim.TryBeginLayerHandle("muscle", f.World(new Vector3(.03f, 0, .023f)), .006f * scale, out int second), "second exposed muscle surface acquires distinct facade handle");
                Assert(!sim.TryMeasureMuscleSplit(first, first, out _) && !sim.TryMeasureMuscleSplit(first, second, out _), "same token and unstepped targets cannot satisfy retraction evidence");
                Assert(sim.TryMeasureLayerHandle(first, out var firstBaseline), "first world muscle anchor queryable");
                Assert(sim.TryMeasureLayerHandle(second, out var secondBaseline), "second world muscle anchor queryable");
                Assert(sim.TrySetLayerHandleTarget(first, firstBaseline.worldPosition - wall.up * .004f * scale) &&
                    sim.TrySetLayerHandleTarget(second, secondBaseline.worldPosition + wall.up * .004f * scale), "opposing world muscle targets accepted");
                for (int step = 0; step < 45; step++) sim.Simulate(1f / 90);
                Assert(sim.TryMeasureMuscleSplit(first, second, out float opening) && opening > .001f, "muscle separation requires real accepted geometry");
                Assert(sim.TryMeasureLayerHandle(first, out var firstActual), "first retracted anchor remains material measurement");
                Assert(sim.TryMeasureLayerHandle(second, out var secondActual), "second retracted anchor remains material measurement");
                float independentOpening = (Mathf.Abs(Vector3.Dot(firstActual.worldPosition - secondActual.worldPosition, wall.up)) -
                    Mathf.Abs(Vector3.Dot(firstBaseline.worldPosition - secondBaseline.worldPosition, wall.up))) * 1000;
                Assert(Near(opening, independentOpening, .0001f), "muscle opening is actual signed world separation rather than raw target distance");
                Debug.Log($"SCALPAL_OPEN_WALL_MECHANICS_RANGE scale={scale:F1} requestedPeritoneumLiftMm={.005f * scale * 1000:F6} acceptedPeritoneumLiftMm={lifted.outwardLiftMillimeters:F6} requestedMuscleOpeningMm={.008f * scale * 1000:F6} acceptedMuscleOpeningMm={opening:F6} synthetic=true surgicalThresholdEstablished=false");
                PositiveJacobians(f.Volume);
                f.ready = false;
                Assert(!sim.TryMeasureLayerHandle(first, out _) && f.Volume.MaterialHandleCount == 0 && !sim.TryMeasureMuscleSplit(first, second, out _), "invalid readiness releases all tokens/evidence");
                f.ready = true;
                Assert(!sim.TrySetLayerHandleTarget(first, firstBaseline.worldPosition), "restored gate does not resurrect old token");
                Assert(f.Volume.TryMaterialContact("skin", Vector3.zero, .05f, out var currentSkin), "deformed retained skin has an actual exposed contact after gate loss");
                Assert(sim.TryBeginLayerHandle("skin", f.World(currentSkin), .001f * scale, out int frameToken), "fresh grip on actual retained skin after invalid gate");
                f.root.transform.position += new Vector3(.01f, .02f, -.03f);
                Assert(!sim.TryMeasureLayerHandle(frameToken, out _) && !sim.TrySetLayerHandleTarget(frameToken, Vector3.zero), "registration/frame motion clears stale token instead of being tissue motion");
                Assert(sim.TryBeginLayerHandle("skin", f.World(currentSkin), .001f * scale, out int resetToken), "fresh grip on actual retained skin after frame change");
                sim.ResetTissues();
                Assert(!sim.TryMeasureLayerHandle(resetToken, out _) && f.Volume.MaterialHandleCount == 0 && f.Volume.CutFaceCount == 0, "retry resets physical tissue and refuses pre-reset tokens");
            }
        }

        // Internal geometry fixture: invokes the production blade boundary directly.
        // This verifies physical tolerance units, not controller buttons or a complete tool stroke.
        static void ValidateScaledBladeTolerance(float scale)
        {
            using (var f = new WorldFixture(scale))
            {
                var applyBlade = typeof(NativeVolumeSimulation).GetMethod("ApplyBlade", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(applyBlade != null, "actual production blade boundary available for internal geometry fixture");
                foreach (float worldGap in new[] { .00045f, .00055f })
                {
                    f.simulation.ResetTissues();
                    var volume = f.Volume;
                    int selected = -1;
                    for (int index = 0; index < volume.Faces.Length; index++)
                    {
                        var candidate = volume.Faces[index];
                        if (candidate.neighbor < 0 || candidate.cut || volume.Materials[volume.Cells[candidate.cell].material].id != "fascia" ||
                            volume.Materials[volume.Cells[candidate.neighbor].material].id != "fascia") continue;
                        Vector3 centre = (CurrentFaceVertex(volume, candidate.cell, candidate.a) + CurrentFaceVertex(volume, candidate.cell, candidate.b) +
                            CurrentFaceVertex(volume, candidate.cell, candidate.c)) / 3;
                        if (Mathf.Abs(centre.x) < .04f && Mathf.Abs(centre.y) < .02f) { selected = index; break; }
                    }
                    Assert(selected >= 0, "intact internal fascia face selected from actual volume topology");
                    var face = volume.Faces[selected];
                    Vector3 p = CurrentFaceVertex(volume, face.cell, face.a), q = CurrentFaceVertex(volume, face.cell, face.b), r = CurrentFaceVertex(volume, face.cell, face.c);
                    Vector3 normal = Vector3.Cross(q - p, r - p).normalized;
                    Assert(normal.sqrMagnitude > .99f && !face.cut, "selected fascia face has a finite actual surface normal and intact reset state");
                    Vector3 offset = normal * (worldGap / scale);
                    Assert(Near(f.simulation.Wall.transform.TransformVector(offset).magnitude, worldGap, 1e-7f), "fixture offset is physical world gap independent of scale");
                    // Other faces may intersect this triangle. Only this exact parallel-offset face is the near-miss oracle.
                    applyBlade.Invoke(f.simulation, new object[] { p + offset, q + offset, r + offset, Vector3.right * .003f });
                    Assert(volume.Faces[selected].cut == (worldGap < .0005f),
                        "production finite blade selected-face world tolerance: scale=" + scale + " physicalGapMm=" + worldGap * 1000);
                }
            }
        }
        static Vector3 CurrentFaceVertex(TissueVolume volume, int cell, int root)
        {
            for (int corner = 0; corner < 4; corner++)
                if (volume.Cells[cell].Vertex(corner) == root) return volume.Positions[volume.NodeFor(cell, corner)];
            throw new InvalidOperationException("Open wall fixture: face vertex missing from owner cell");
        }

        static void ValidateInvalidWorldFrames()
        {
            using (var f = new WorldFixture(1))
            {
                f.root.transform.localScale = new Vector3(1, 1.2f, 1);
                Assert(!f.simulation.TryContactLayer("skin", f.World(Vector3.zero), .001f, out _) &&
                    !f.simulation.TryBeginLayerHandle("skin", f.World(Vector3.zero), .001f, out _), "nonuniform world scale refuses SI measurement");
                f.root.transform.localScale = new Vector3(2, 1, 1);
                f.wound.localRotation = Quaternion.Euler(0, 0, 45);
                Assert(!f.simulation.TryContactLayer("skin", f.World(Vector3.zero), .001f, out _), "sheared registered wound frame refuses instead of guessing a scalar");
                f.root.transform.localScale = new Vector3(-1, 1, 1);
                f.wound.localRotation = Quaternion.identity;
                Assert(!f.simulation.TryContactLayer("skin", f.World(Vector3.zero), .001f, out _), "reflected frame rejects handedness mismatch");
            }
        }

        static bool NearDouble(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-10, Math.Abs(b) * 1e-9);
        static void SnapshotConservation(VesselBleeding.Snapshot snapshot)
        {
            Assert(NearDouble(snapshot.RemainingSourceMilliliters + snapshot.CumulativeLossMilliliters, snapshot.InitialSourceMilliliters), "snapshot independently conserves source + cumulative loss");
            Assert(NearDouble(snapshot.PooledMilliliters + snapshot.RemovedMilliliters, snapshot.CumulativeLossMilliliters), "snapshot independently conserves pool + suction ledger");
            Assert(snapshot.RemainingSourceMilliliters >= 0 && snapshot.PooledMilliliters >= 0 && snapshot.RemovedMilliliters >= 0 &&
                snapshot.CumulativeLossMilliliters <= snapshot.InitialSourceMilliliters, "snapshot fluid ledger is physically bounded");
        }
        static void ValidateBleedingSnapshots()
        {
            var model = new VesselBleeding(1000, 1000, .5, 1);
            var intact = model.CaptureSnapshot();
            Assert(intact.IsValid && !intact.IsFaulted && !intact.IsInjured && !intact.IsOccluded && !intact.HasActiveFlow && intact.FlowMillilitersPerSecond == 0, "intact model snapshot has no fabricated injury/flow");
            Assert(model.OpenInjury(.000001) && model.Step(.1), "actual injury and fluid step accepted");
            var injured = model.CaptureSnapshot();
            double expectedRate = .5 * .000001 * Math.Sqrt(2 * 1000.0 / 1000) * 1000000;
            Assert(injured.IsValid && injured.IsInjured && !injured.IsOccluded && injured.HasActiveFlow && NearDouble(injured.InjuryAreaSquareMeters, .000001), "injury snapshot exposes actual injury identity/state");
            Assert(NearDouble(injured.FlowMillilitersPerSecond, expectedRate) && NearDouble(injured.CumulativeLossMilliliters, expectedRate * .1), "snapshot captures actual SI rate and integrated fluid loss");
            Assert(NearDouble(injured.ElapsedSeconds, .1), "snapshot clock is actual model elapsed injury time");
            SnapshotConservation(injured);
            Assert(NearDouble(model.RemovePool(injured.PooledMilliliters * .5), injured.PooledMilliliters * .5), "actual suction removes available pool");
            model.SetOccluded(true);
            Assert(model.Step(.1), "occluded model step accepted");
            var occluded = model.CaptureSnapshot();
            Assert(occluded.IsValid && occluded.IsInjured && occluded.IsOccluded && !occluded.HasActiveFlow && occluded.FlowMillilitersPerSecond == 0, "occlusion snapshot stops current flow while retaining injury");
            Assert(NearDouble(occluded.CumulativeLossMilliliters, injured.CumulativeLossMilliliters) && occluded.RemovedMilliliters > 0 &&
                NearDouble(occluded.PooledMilliliters, injured.PooledMilliliters * .5), "occlusion/suction do not erase cumulative blood loss");
            SnapshotConservation(occluded);
            Assert(!intact.IsInjured && intact.CumulativeLossMilliliters == 0 && intact.RemovedMilliliters == 0 &&
                injured.HasActiveFlow && !injured.IsOccluded && injured.RemovedMilliliters == 0 && NearDouble(injured.ElapsedSeconds, .1), "prior value snapshots remain immutable after injury/suction/clamp");
            model.SetOccluded(false);
            Assert(model.Step(.1), "unclamping actually resumes fluid integration");
            var resumed = model.CaptureSnapshot();
            Assert(resumed.HasActiveFlow && resumed.CumulativeLossMilliliters > occluded.CumulativeLossMilliliters, "new snapshot sees resumed rate and increased loss");
            Assert(!model.Step(double.NaN), "invalid actual step faults model");
            var faulted = model.CaptureSnapshot();
            Assert(faulted.IsFaulted && !faulted.IsValid && !faulted.HasActiveFlow && faulted.FlowMillilitersPerSecond == 0 &&
                NearDouble(faulted.CumulativeLossMilliliters, resumed.CumulativeLossMilliliters), "fault snapshot preserves ledger and refuses active evidence");
            SnapshotConservation(faulted);
            model.Reset(); var reset = model.CaptureSnapshot();
            Assert(reset.IsValid && !reset.IsInjured && !reset.IsOccluded && !reset.IsFaulted && !reset.HasActiveFlow &&
                reset.ElapsedSeconds == 0 && reset.CumulativeLossMilliliters == 0 && reset.RemovedMilliliters == 0 && reset.PooledMilliliters == 0 &&
                NearDouble(reset.RemainingSourceMilliliters, 1), "retry restores actual fluid model snapshot");
            Assert(faulted.IsFaulted && faulted.IsInjured && faulted.CumulativeLossMilliliters > 0 && occluded.IsOccluded && occluded.RemovedMilliliters > 0,
                "reset cannot retroactively mutate saved fault/clamp snapshots");
            SnapshotConservation(reset);
        }

        static void EqualPositions(TissueVolume volume, Vector3[] expected, string reason)
        {
            Assert(volume.Positions.Length == expected.Length, reason + " node count");
            for (int node = 0; node < expected.Length; node++) Assert(volume.Positions[node] == expected[node], reason);
        }
        static void PositiveJacobians(TissueVolume volume)
        {
            // Independently derive F from current bound corners and original cell geometry.
            for (int cell = 0; cell < volume.Cells.Length; cell++)
            {
                var c = volume.Cells[cell]; var a = volume.Positions[volume.NodeFor(cell, 0)];
                var current = new TissueTensor(volume.Positions[volume.NodeFor(cell, 1)] - a,
                    volume.Positions[volume.NodeFor(cell, 2)] - a, volume.Positions[volume.NodeFor(cell, 3)] - a);
                var reference = new TissueTensor(volume.Original[c.b] - volume.Original[c.a],
                    volume.Original[c.c] - volume.Original[c.a], volume.Original[c.d] - volume.Original[c.a]);
                float j = current.Multiply(reference.Inverse()).Determinant;
                Assert(!float.IsNaN(j) && !float.IsInfinity(j) && j > .02f, "actual cell Jacobian remains positive above rejection floor");
            }
        }
    }
}
