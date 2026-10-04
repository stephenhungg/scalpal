using System;
using System.Collections.Generic;
using System.Reflection;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Synthetic mechanics and generated geometry; not headset or clinical validation.
    public static class NativeVolumeValidation
    {
        static int checks;
        static void Assert(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException("Tissue volume: " + reason); }
        static bool Near(float a, float b, float relative = .0001f) => Mathf.Abs(a - b) <= Mathf.Max(1e-9f, Mathf.Abs(b) * relative);
        static VolumeMaterial Material => new VolumeMaterial { id = "synthetic_test", youngPascals = 12000, poissonRatio = .3f, densityKgPerCubicMeter = 1000, color = Color.red, measurementSource = "Synthetic fixture, not measured tissue" };
        static float Signed(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => Vector3.Dot(b - a, Vector3.Cross(c - a, d - a)) / 6;
        static Vector3 Center(TissueVolume volume, int cell)
        { var result = Vector3.zero; for (int i = 0; i < 4; i++) result += volume.Original[volume.Cells[cell].Vertex(i)] * .25f; return result; }

        [MenuItem("Scalpal/Quest/Validate Volumetric Tissue")]
        public static void Run()
        {
            checks = 0;
            ValidateUnits();
            var box = TissueVolumeFactory.Box(new Bounds(Vector3.zero, new Vector3(.12f, .12f, .06f)), 4, 4, 2, Material, false);
            ValidateVolume(box); Assert(Near(box.ReferenceVolume, .12f * .12f * .06f), "box partition preserves known metric volume");
            ValidateSurface(box, true);
            int baselineNodes = box.NodeCount;
            Assert(box.CutSweep(Vector3.one, Vector3.one + Vector3.right, Vector3.one + Vector3.up, .0005f) == 0, "remote finite blade cannot cut body");
            Assert(box.CutSweep(Vector3.zero, Vector3.zero, Vector3.up, .0005f) == 0, "degenerate sweep cannot fracture");
            var a = new Vector3(0, -.03f, -.04f); var b = new Vector3(0, .03f, -.04f); var c = new Vector3(0, .03f, .04f);
            float mass = box.TotalMass;
            Assert(box.CutSweep(a, b, c, .0005f) > 0 && box.CutFaceCount > 0, "finite swept knife creates interior fracture faces");
            Assert(Near(box.TotalMass, mass) && box.NodeCount >= baselineNodes, "fracture duplicates connectivity without losing mass");
            foreach (var face in box.Faces)
            {
                if (!face.cut) continue;
                var bounds = new Bounds(box.Original[face.a], Vector3.zero); bounds.Encapsulate(box.Original[face.b]); bounds.Encapsulate(box.Original[face.c]);
                Assert(bounds.min.x <= .0005f && bounds.max.x >= -.0005f && bounds.min.y <= .0305f && bounds.max.y >= -.0305f,
                    "only nearby faces fracture; cutting plane does not extend across remote tissue");
            }
            ValidateSurface(box, false);
            int cutNodes = box.NodeCount, cuts = box.CutFaceCount;
            Assert(box.CutSweep(a, b, c, .0005f) == 0 && box.NodeCount == cutNodes && box.CutFaceCount == cuts, "duplicate blade sweep does not grow topology");
            box.Freeze(); Assert(box.CutFaceCount == cuts && box.NodeCount == cutNodes, "tracking freeze retains incision topology");
            Assert(AllZero(Velocities(box)), "freeze clears momentum");
            box.Reset(); Assert(box.CutFaceCount == 0 && box.NodeCount == baselineNodes && box.Handle == -1 && AllZero(Velocities(box)), "retry restores topology, handle and velocity");
            for (int cell = 0; cell < box.Cells.Length; cell++) for (int corner = 0; corner < 4; corner++)
                Assert(box.Positions[box.NodeFor(cell, corner)] == box.Original[box.Cells[cell].Vertex(corner)], "reset restores cell material coordinates");
            ValidateSurface(box, true);
            ValidateFanSplit(); ValidateBudgets();
            var wall = TissueVolumeFactory.AbdominalWall(); ValidateVolume(wall); ValidateSurface(wall, true);
            Assert(wall.Materials.Length == 3, "generated abdominal wall has three explicit educational layers");
            foreach (var material in wall.Materials)
                Assert(material.HasValidUnits && !string.IsNullOrWhiteSpace(material.id) && !material.id.StartsWith("surface__", StringComparison.Ordinal), "layer has SI parameters and does not impersonate source segmentation");
            var colors = new HashSet<Color>(); foreach (var material in wall.Materials) colors.Add(material.color);
            Assert(colors.Count == 3, "layer cut faces retain distinct appearance");
            var minimumDepth = new[] { float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity };
            var maximumDepth = new[] { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
            var materialCells = new int[3];
            foreach (var cell in wall.Cells)
            {
                materialCells[cell.material]++;
                for (int corner = 0; corner < 4; corner++)
                {
                    float depth = wall.Original[cell.Vertex(corner)].z;
                    minimumDepth[cell.material] = Mathf.Min(minimumDepth[cell.material], depth);
                    maximumDepth[cell.material] = Mathf.Max(maximumDepth[cell.material], depth);
                }
            }
            for (int layer = 0; layer < 3; layer++) Assert(materialCells[layer] > 0 && maximumDepth[layer] > minimumDepth[layer], "each layer owns a positive three-dimensional material region");
            Assert(Mathf.Abs(minimumDepth[0] - BodyRegistrationMath.SourceFront) < 1e-5f && maximumDepth[0] <= minimumDepth[1] + 1e-6f && maximumDepth[1] <= minimumDepth[2] + 1e-6f,
                "skin, fat and peritoneum progress inward along source +Z from registered anterior surface");
            Debug.Log("SCALPAL_NATIVE_VOLUME_VALIDATION_OK checks=" + checks + " synthetic SI mechanics/topology/reset; no headset or clinical evidence");
        }

        static void ValidateUnits()
        {
            var m = Material; Assert(m.HasValidUnits, "finite SI material accepted");
            Assert(Near(m.ShearModulus, 12000f / 2.6f) && Near(m.LameLambda, 12000f * .3f / (1.3f * .4f)), "Lamé constants derive from Young modulus and Poisson ratio");
            Assert(m.Energy(default) == 0, "rest strain has zero elastic energy");
            var strain = new TissueTensor(new Vector3(.01f, 0, 0), Vector3.zero, Vector3.zero);
            Assert(Near(m.Energy(strain), (m.ShearModulus + .5f * m.LameLambda) * .0001f), "uniaxial strain energy agrees with analytical SI density");
            var stress = m.SecondPiola(strain);
            Assert(Near(stress.x.x, (2 * m.ShearModulus + m.LameLambda) * .01f) && Near(stress.y.y, m.LameLambda * .01f) && stress.x.y == 0, "second Piola stress agrees with analytical uniaxial strain");
            var shear = new TissueTensor(new Vector3(0, .01f, 0), new Vector3(.01f, 0, 0), Vector3.zero);
            Assert(Near(m.Energy(shear), m.ShearModulus * .0002f), "symmetric shear strain energy uses both off-diagonal components");
            var rotation = Quaternion.Euler(17, 29, 4);
            var f = new TissueTensor(rotation * Vector3.right, rotation * Vector3.up, rotation * Vector3.forward);
            Assert(m.Energy((f.Transpose().Multiply(f) - TissueTensor.Identity) * .5f) < 1e-7f, "rigid rotation produces no material strain");
            var matrix = new TissueTensor(new Vector3(2, .1f, .2f), new Vector3(.3f, 3, .4f), new Vector3(.5f, .6f, 4));
            Assert((matrix.Multiply(matrix.Inverse()) - TissueTensor.Identity).SquaredNorm < 1e-10f, "column-major rest inverse preserves identity");
            m.youngPascals = float.NaN; Assert(!m.HasValidUnits, "nonfinite modulus rejected"); m = Material; m.poissonRatio = .5f; Assert(!m.HasValidUnits, "singular incompressibility rejected");
        }

        static void ValidateVolume(TissueVolume volume)
        {
            float total = 0, mass = 0;
            var counts = new Dictionary<string, int>();
            for (int index = 0; index < volume.Cells.Length; index++)
            {
                var cell = volume.Cells[index];
                float size = Signed(volume.Original[cell.a], volume.Original[cell.b], volume.Original[cell.c], volume.Original[cell.d]);
                Assert(size > 0 && !float.IsInfinity(size), "positive oriented reference tetrahedron");
                total += size; mass += size * volume.Materials[cell.material].densityKgPerCubicMeter;
                for (int opposite = 0; opposite < 4; opposite++)
                {
                    var ids = new int[3]; int cursor = 0;
                    for (int vertex = 0; vertex < 4; vertex++) if (vertex != opposite) ids[cursor++] = cell.Vertex(vertex);
                    Array.Sort(ids); string key = string.Join(":", ids);
                    counts.TryGetValue(key, out int count); counts[key] = count + 1;
                }
            }
            foreach (var count in counts.Values) Assert(count == 1 || count == 2, "face ownership is manifold");
            Assert(counts.Count == volume.Faces.Length, "deduplicated topology matches independent cell-face reconstruction");
            foreach (var face in volume.Faces)
            {
                var ids = new[] { face.a, face.b, face.c }; Array.Sort(ids);
                Assert(counts[string.Join(":", ids)] == (face.neighbor < 0 ? 1 : 2), "shared face has two matching cell owners");
            }
            Assert(Near(volume.ReferenceVolume, total) && Near(volume.TotalMass, mass), "volume and density-weighted mass agree with independent sum");
        }

        static void ValidateSurface(TissueVolume volume, bool requireClosed)
        {
            var mesh = new Mesh();
            try
            {
                volume.WriteSurface(mesh); var vertices = mesh.vertices; var triangles = mesh.triangles;
                int exterior = 0; foreach (var face in volume.Faces) if (face.neighbor < 0) exterior++;
                Assert(triangles.Length / 3 == exterior + 2 * volume.CutFaceCount, "each incision emits both physical interior faces");
                float signed = 0; var edges = new Dictionary<string, int>();
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var a = vertices[triangles[i]]; var b = vertices[triangles[i + 1]]; var c = vertices[triangles[i + 2]];
                    Assert(TissueCage.Finite(a) && TissueCage.Finite(b) && TissueCage.Finite(c) && Vector3.Cross(b - a, c - a).sqrMagnitude > 1e-18f, "surface has finite nondegenerate triangles");
                    signed += Vector3.Dot(a, Vector3.Cross(b, c)) / 6;
                    AddEdge(edges, a, b); AddEdge(edges, b, c); AddEdge(edges, c, a);
                }
                Assert(Near(signed, volume.ReferenceVolume, .001f), "outward surface winding encloses correct reference volume including opposing cut faces");
                if (requireClosed) foreach (var count in edges.Values) Assert(count == 2, "unfractured boundary is closed across adjacent tetrahedral cells");
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static string PointKey(Vector3 point) => Mathf.RoundToInt(point.x * 1e6f) + ":" + Mathf.RoundToInt(point.y * 1e6f) + ":" + Mathf.RoundToInt(point.z * 1e6f);
        static void AddEdge(Dictionary<string, int> edges, Vector3 a, Vector3 b)
        { string first = PointKey(a), second = PointKey(b); string key = string.CompareOrdinal(first, second) < 0 ? first + "/" + second : second + "/" + first; edges.TryGetValue(key, out int count); edges[key] = count + 1; }
        static Vector3[] Velocities(TissueVolume volume) => (Vector3[])typeof(TissueVolume).GetField("velocities", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(volume);
        static bool AllZero(Vector3[] values) { foreach (var value in values) if (value != Vector3.zero) return false; return true; }

        static void ValidateFanSplit()
        {
            var volume = TissueVolumeFactory.Box(new Bounds(Vector3.zero, Vector3.one * .12f), 2, 2, 2, Material, false);
            var a = new Vector3(0, -.08f, -.08f); var b = new Vector3(0, .08f, -.08f); var c = new Vector3(0, .08f, .08f); var d = new Vector3(0, -.08f, .08f);
            Assert(volume.CutSweep(a, b, c, .0005f) + volume.CutSweep(a, c, d, .0005f) > 0, "full section knife sweep fractures connected fans");
            var left = new HashSet<int>(); var right = new HashSet<int>();
            for (int cell = 0; cell < volume.Cells.Length; cell++) for (int corner = 0; corner < 4; corner++)
                (Center(volume, cell).x < 0 ? left : right).Add(volume.NodeFor(cell, corner));
            Assert(!left.Overlaps(right), "cut sides have independent material nodes rather than shared hidden welds");
            Assert(volume.BeginHandle(new Vector3(-.06f, 0, 0), .002f), "left cut side can acquire handle");
            Assert(left.Contains(volume.Handle), "left handle belongs to left component");
            var restRight = new Dictionary<int, Vector3>(); foreach (int node in right) restRight[node] = volume.Positions[node];
            Vector3 goal = volume.HandlePosition + Vector3.left * .008f;
            for (int step = 0; step < 20; step++) volume.Step(1f / 90, goal, Vector3.zero);
            Assert((volume.HandlePosition - goal).sqrMagnitude < 1e-8f, "held cut side follows bounded handle");
            foreach (int node in right) Assert((volume.Positions[node] - restRight[node]).sqrMagnitude < 1e-10f, "pulling one disconnected cut side does not drag opposite side");
            for (int cell = 0; cell < volume.Cells.Length; cell++)
            {
                float current = Signed(volume.Positions[volume.NodeFor(cell, 0)], volume.Positions[volume.NodeFor(cell, 1)], volume.Positions[volume.NodeFor(cell, 2)], volume.Positions[volume.NodeFor(cell, 3)]);
                Assert(current > 0 && !float.IsNaN(current), "fixed-step deformation remains finite without inverted cells");
            }
            volume.Freeze(); Assert(volume.BeginHandle(new Vector3(.06f, 0, 0), .002f) && right.Contains(volume.Handle), "opposite side independently acquires a handle");
        }

        static void ValidateBudgets()
        {
            var nodes = new List<Vector3>(); var cells = new List<TissueVolume.Cell>();
            // Capacity fixture: independent two-tet pairs, all intersected by one finite blade.
            for (int i = 0; i <= TissueVolume.MaxCutFaces; i++)
            {
                int start = nodes.Count; var offset = new Vector3((i % 23) * .01f, (i / 23) * .01f, 0);
                nodes.Add(offset); nodes.Add(offset + Vector3.right * .003f); nodes.Add(offset + Vector3.up * .003f); nodes.Add(offset + Vector3.forward * .003f); nodes.Add(offset - Vector3.forward * .003f);
                cells.Add(new TissueVolume.Cell { a = start, b = start + 1, c = start + 2, d = start + 3 });
                cells.Add(new TissueVolume.Cell { a = start, b = start + 2, c = start + 1, d = start + 4 });
            }
            var faceBudget = new TissueVolume(nodes.ToArray(), cells.ToArray(), new[] { Material }, new bool[nodes.Count]);
            var largeA = new Vector3(-1, -1, 0); var largeB = new Vector3(2, -1, 0); var largeC = new Vector3(-1, 2, 0);
            int before = faceBudget.NodeCount; float mass = faceBudget.TotalMass;
            Assert(faceBudget.CutSweep(largeA, largeB, largeC, .0005f) == 0 && faceBudget.CutFaceCount == 0 && faceBudget.NodeCount == before && Near(faceBudget.TotalMass, mass), "over-cut-face-budget sweep atomically refuses fracture");
            foreach (var face in faceBudget.Faces) Assert(!face.cut, "face budget rollback retains every intact face");

            nodes.Clear(); cells.Clear();
            for (int i = 0; i < 1022; i++)
            {
                int start = nodes.Count; var offset = new Vector3(2 + (i % 32) * .03f, (i / 32) * .03f, 0);
                nodes.Add(offset); nodes.Add(offset + Vector3.right * .01f); nodes.Add(offset + Vector3.up * .01f); nodes.Add(offset + Vector3.forward * .01f);
                cells.Add(new TissueVolume.Cell { a = start, b = start + 1, c = start + 2, d = start + 3 });
            }
            int root = nodes.Count;
            nodes.Add(Vector3.zero); nodes.Add(Vector3.right * .03f); nodes.Add(Vector3.up * .03f); nodes.Add(Vector3.forward * .03f); nodes.Add(Vector3.back * .03f); nodes.Add(Vector3.down * .03f);
            cells.Add(new TissueVolume.Cell { a = root, b = root + 1, c = root + 2, d = root + 3 });
            cells.Add(new TissueVolume.Cell { a = root, b = root + 2, c = root + 1, d = root + 4 });
            cells.Add(new TissueVolume.Cell { a = root, b = root + 1, c = root + 5, d = root + 4 });
            var nodeBudget = new TissueVolume(nodes.ToArray(), cells.ToArray(), new[] { Material }, new bool[nodes.Count]);
            before = nodeBudget.NodeCount; mass = nodeBudget.TotalMass;
            Assert(before == 4094, "node-capacity fixture begins below actual solver budget");
            Assert(nodeBudget.CutSweep(new Vector3(-.01f, -.01f, 0), new Vector3(.08f, -.01f, 0), new Vector3(-.01f, .08f, 0), .0001f) == 0,
                "fracture requiring excess connected-fan nodes is refused");
            Assert(nodeBudget.NodeCount == before && nodeBudget.CutFaceCount == 0 && Near(nodeBudget.TotalMass, mass), "node budget failure rolls back topology and mass atomically");
            foreach (var face in nodeBudget.Faces) Assert(!face.cut, "node budget rollback retains every intact face");
        }
    }
}
