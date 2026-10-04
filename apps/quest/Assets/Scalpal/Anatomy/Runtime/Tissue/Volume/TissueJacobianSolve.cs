using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // One synchronous trial's noninversion scratch storage. The caller retains the
    // constitutive solve, history, grip/pin ordering, acceptance and retries.
    // Cells share nodes: preserve the serial Gauss-Seidel ordering, never parallelize them.
    public sealed class TissueJacobianSolve : IDisposable
    {
        public struct CellData
        {
            public int a, b, c, d;
            public TissueTensor restInverse;
            public Vector3 restA,restB,restC,restD;
        }
        readonly Vector3[] managedPositions;
        NativeArray<Vector3> positions;
        NativeArray<CellData> cells;
        NativeArray<float> inverseMass;

        // CellData is numeric/blittable and can be cached by the caller until a topology
        // rebuild. No VolumeMaterial strings/arrays or MaxwellHistory objects enter Burst.
        public TissueJacobianSolve(Vector3[] currentPositions, CellData[] cellData, float[] nodeInverseMass)
        {
            if (currentPositions == null || cellData == null || nodeInverseMass == null ||
                currentPositions.Length == 0 || currentPositions.Length > TissueVolume.MaxNodes ||
                currentPositions.Length != nodeInverseMass.Length || cellData.Length == 0)
                throw new ArgumentException("Noninversion scratch requires matching bounded node arrays and nonempty cells");
            for (int i = 0; i < currentPositions.Length; i++)
                if (!TissueCage.Finite(currentPositions[i]) || !Finite(nodeInverseMass[i]) || nodeInverseMass[i] < 0)
                    throw new ArgumentException("Noninversion scratch requires finite positions and nonnegative inverse masses");
            foreach (var cell in cellData)
                if ((uint)cell.a >= currentPositions.Length || (uint)cell.b >= currentPositions.Length ||
                    (uint)cell.c >= currentPositions.Length || (uint)cell.d >= currentPositions.Length ||
                    !TissueCage.Finite(cell.restInverse.x) || !TissueCage.Finite(cell.restInverse.y) || !TissueCage.Finite(cell.restInverse.z) ||
                    !TissueCage.Finite(cell.restA) || !TissueCage.Finite(cell.restB) || !TissueCage.Finite(cell.restC) || !TissueCage.Finite(cell.restD))
                    throw new ArgumentException("Noninversion scratch requires valid numeric cell coefficients and bindings");
            managedPositions = currentPositions;
            try
            {
                positions = new NativeArray<Vector3>(currentPositions, Allocator.TempJob);
                cells = new NativeArray<CellData>(cellData, Allocator.TempJob);
                inverseMass = new NativeArray<float>(nodeInverseMass, Allocator.TempJob);
            }
            catch { Dispose(); throw; }
        }
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        void RequireCreated()
        {
            if (!positions.IsCreated) throw new ObjectDisposedException(nameof(TissueJacobianSolve));
        }
        public void RunJacobianFloor(int sweeps = 3)
        {
            RequireCreated();
            if (sweeps < 1 || sweeps > 3) throw new ArgumentException("Jacobian floor uses one to three serial sweeps");
            positions.CopyFrom(managedPositions);
            var job=new JacobianJob { positions = positions, cells = cells, inverseMass = inverseMass, sweeps = sweeps };
            job.Run();
            positions.CopyTo(managedPositions);
        }
        public void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (cells.IsCreated) cells.Dispose();
            if (inverseMass.IsCreated) inverseMass.Dispose();
        }
        static TissueTensor Deformation(NativeArray<Vector3> nodes, CellData cell)
        {
            Vector3 a = nodes[cell.a];
            if(a.Equals(cell.restA)&&nodes[cell.b].Equals(cell.restB)&&nodes[cell.c].Equals(cell.restC)&&nodes[cell.d].Equals(cell.restD))return TissueTensor.Identity;
            return new TissueTensor(nodes[cell.b] - a, nodes[cell.c] - a, nodes[cell.d] - a).Multiply(cell.restInverse);
        }
        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
        struct JacobianJob : IJob
        {
            public NativeArray<Vector3> positions;
            [ReadOnly] public NativeArray<CellData> cells;
            [ReadOnly] public NativeArray<float> inverseMass;
            public int sweeps;
            public void Execute()
            {
                for (int sweep = 0; sweep < sweeps; sweep++)
                {
                    bool corrected=false;
                    for (int index = 0; index < cells.Length; index++)
                    {
                        var cell = cells[index]; var f = Deformation(positions, cell); float determinant = f.Determinant;
                        if (float.IsNaN(determinant) || float.IsInfinity(determinant) || determinant >= .2f) continue;
                        var cofactor = new TissueTensor(Vector3.Cross(f.y, f.z), Vector3.Cross(f.z, f.x), Vector3.Cross(f.x, f.y));
                        var gradient = cofactor.Multiply(cell.restInverse.Transpose());
                        Vector3 g1 = gradient.x, g2 = gradient.y, g3 = gradient.z, g0 = -g1 - g2 - g3;
                        float denominator = inverseMass[cell.a] * g0.sqrMagnitude + inverseMass[cell.b] * g1.sqrMagnitude
                            + inverseMass[cell.c] * g2.sqrMagnitude + inverseMass[cell.d] * g3.sqrMagnitude;
                        if (!(denominator > 1e-20f) || float.IsInfinity(denominator)) continue;
                        corrected=true;
                        float change = (.2f - determinant) / denominator;
                        positions[cell.a] += inverseMass[cell.a] * change * g0;
                        positions[cell.b] += inverseMass[cell.b] * change * g1;
                        positions[cell.c] += inverseMass[cell.c] * change * g2;
                        positions[cell.d] += inverseMass[cell.d] * change * g3;
                    }
                    if(!corrected)break;
                }
            }
        }
    }
}
