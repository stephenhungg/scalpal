using System;
using System.Collections.Generic;
using System.Reflection;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Independent synthetic SI coupons exercise actual prescribed volume boundaries
    // and committed material forces. They do not validate measured tissue parameters.
    public static class NativeCouponValidation
    {
        const float Length = .02f, Stretch = 1.08f;
        static int checks;
        static VolumeMaterial Elastic(float density = 1000) => new VolumeMaterial {
            id = "synthetic_coupon", youngPascals = 12000, poissonRatio = 0,
            densityKgPerCubicMeter = density, color = Color.red,
            measurementSource = "Synthetic analytical coupon; not measured tissue"
        };
        static VolumeMaterial Relaxing()
        {
            var material = Elastic(); material.relaxationFractions = new[] { .25f, .35f };
            material.relaxationSeconds = new[] { .08f, .3f }; return material;
        }

        [MenuItem("Scalpal/Quest/Validate Material Force Coupons")]
        public static void Run()
        {
            checks = 0;
            ElasticTractionAndCovariance(); Scaling(); HeldRelaxation(); Subdivision();
            InvalidAndRejectedTrials(); CutAndReset();
            Debug.Log("SCALPAL_NATIVE_COUPON_VALIDATION_OK checks=" + checks + " synthetic prescribed-boundary SI force/relaxation checks; no measured tissue fit or headset evidence");
        }

        // A six-tetrahedron Kuhn partition per voxel, constructed independently of
        // TissueVolumeFactory. Each voxel shares the same positive body diagonal.
        static TissueVolume Cube(int subdivisions, float length, VolumeMaterial material)
        {
            int width = subdivisions + 1;
            var nodes = new Vector3[width * width * width]; var pins = new bool[nodes.Length];
            for (int z = 0; z < width; z++) for (int y = 0; y < width; y++) for (int x = 0; x < width; x++)
            {
                int index = Index(x, y, z, width); nodes[index] = new Vector3(x, y, z) * (length / subdivisions); pins[index] = true;
            }
            var cells = new List<TissueVolume.Cell>();
            int[,] paths = { { 0, 1, 2 }, { 0, 2, 1 }, { 1, 0, 2 }, { 1, 2, 0 }, { 2, 0, 1 }, { 2, 1, 0 } };
            for (int z = 0; z < subdivisions; z++) for (int y = 0; y < subdivisions; y++) for (int x = 0; x < subdivisions; x++)
                for (int permutation = 0; permutation < 6; permutation++)
                {
                    int px = x, py = y, pz = z; var ids = new int[4]; ids[0] = Index(px, py, pz, width);
                    for (int corner = 1; corner < 4; corner++)
                    {
                        int axis = paths[permutation, corner - 1];
                        if (axis == 0) px++; else if (axis == 1) py++; else pz++;
                        ids[corner] = Index(px, py, pz, width);
                    }
                    cells.Add(new TissueVolume.Cell { a = ids[0], b = ids[1], c = ids[2], d = ids[3] });
                }
            return new TissueVolume(nodes, cells.ToArray(), new[] { material }, pins);
        }
        static int Index(int x, int y, int z, int width) => x + width * (y + width * z);
        static Vector3 Affine(Vector3 point, float stretch, Quaternion rotation, Vector3 translation)
            => rotation * new Vector3(point.x * stretch, point.y, point.z) + translation;
        static void Prescribe(TissueVolume volume, float stretch, Quaternion rotation, Vector3 translation)
        {
            for (int root = 0; root < volume.Original.Length; root++)
                Assert(volume.SetBoundaryTarget(root, Affine(volume.Original[root], stretch, rotation, translation)), "constructor-pinned root accepts finite metric target");
        }
        static void Step(TissueVolume volume, float dt = .02f) => volume.Step(dt, Vector3.zero, Vector3.zero);
        static Vector3[] Forces(TissueVolume volume)
        {
            var result = new Vector3[volume.NodeCount]; volume.MeasureNodalForces(result); return result;
        }
        static Vector3 GripReaction(TissueVolume volume, Vector3[] force, float length)
        {
            var roots = new int[volume.NodeCount];
            for (int cell = 0; cell < volume.Cells.Length; cell++) for (int corner = 0; corner < 4; corner++)
                roots[volume.NodeFor(cell, corner)] = volume.Cells[cell].Vertex(corner);
            var result = Vector3.zero;
            for (int node = 0; node < force.Length; node++)
                if (Mathf.Abs(volume.Original[roots[node]].x - length) < length * .0001f) result -= force[node];
            return result; // Applied reaction balances the internal material force.
        }
        static double ElasticReaction(float stretch, float length)
            => stretch * 12000 * ((double)stretch * stretch - 1) * .5 * length * length;
        static void AssertGeometry(TissueVolume volume, float stretch, Quaternion rotation, Vector3 translation)
        {
            for (int cell = 0; cell < volume.Cells.Length; cell++) for (int corner = 0; corner < 4; corner++)
                Assert((volume.Positions[volume.NodeFor(cell, corner)] - Affine(volume.Original[volume.Cells[cell].Vertex(corner)], stretch, rotation, translation)).sqrMagnitude < 1e-16f,
                    "actual active cell corner follows its original prescribed boundary");
        }

        static void ElasticTractionAndCovariance()
        {
            var coupon = Cube(2, Length, Elastic());
            Assert(!coupon.HasAcceptedStep && Zero(Forces(coupon)), "virgin coupon has no accepted loading and zero material forces");
            Prescribe(coupon, Stretch, Quaternion.identity, Vector3.zero);
            Assert(Zero(Forces(coupon)), "pending loading leaves committed forces unchanged");
            AssertGeometry(coupon, 1, Quaternion.identity, Vector3.zero);
            Step(coupon); Assert(coupon.HasAcceptedStep, "actual volume accepts prescribed affine loading");
            AssertGeometry(coupon, Stretch, Quaternion.identity, Vector3.zero);
            var force = Forces(coupon); var reaction = GripReaction(coupon, force, Length);
            NearVector(reaction, Vector3.right * (float)ElasticReaction(Stretch, Length), "grip force N equals independent first Piola traction times reference area m^2");
            var total = Vector3.zero; var moment = Vector3.zero;
            for (int node = 0; node < force.Length; node++) { total += force[node]; moment += Vector3.Cross(coupon.Positions[node], force[node]); }
            NearVector(total, Vector3.zero, "internal forces sum to zero", 2e-6f);
            NearVector(moment, Vector3.zero, "internal forces have zero net moment", 1e-7f);
            var accepted = new HistoryState(coupon); var positions = (Vector3[])coupon.Positions.Clone();
            for (int i = 0; i < 16; i++) Exact(force, Forces(coupon), "repeated measurement preserves accepted forces without advancing time");
            Exact(positions, coupon.Positions, "force measurement preserves geometry"); accepted.AssertRetained(coupon, "force measurement retains committed history");
            var rotation = Quaternion.Euler(13, 37, -19); var translation = new Vector3(.014f, -.012f, .018f);
            var rotated = Cube(2, Length, Elastic()); Prescribe(rotated, Stretch, rotation, translation); Step(rotated);
            var rotatedForce = Forces(rotated);
            for (int node = 0; node < force.Length; node++) NearVector(rotatedForce[node], rotation * force[node], "superposed rigid motion rotates material force");
            NearVector(GripReaction(rotated, rotatedForce, Length), rotation * reaction, "grip reaction is covariant under rigid motion");
            var rigid = Cube(1, Length, Elastic()); Prescribe(rigid, 1, rotation, translation); Step(rigid);
            foreach (var f in Forces(rigid)) NearVector(f, Vector3.zero, "rigid transform produces no appreciable material force", 3e-6f);
        }

        static void Scaling()
        {
            foreach (int subdivisions in new[] { 1, 2, 3 }) foreach (float density in new[] { 500f, 2000f }) foreach (float dt in new[] { .005f, .025f })
            {
                var coupon = Cube(subdivisions, Length, Elastic(density)); Prescribe(coupon, Stretch, Quaternion.identity, Vector3.zero); Step(coupon, dt);
                Near(coupon.ReferenceVolume, (double)Length * Length * Length, "independent subdivision preserves reference volume", 1e-10);
                Near(coupon.TotalMass, density * (double)Length * Length * Length, "material mass has kg scaling", 1e-8);
                NearVector(GripReaction(coupon, Forces(coupon), Length), Vector3.right * (float)ElasticReaction(Stretch, Length), "static reaction is independent of mass, resolution and timestep");
            }
            var scaled = Cube(2, Length * 2, Elastic()); Prescribe(scaled, Stretch, Quaternion.identity, Vector3.zero); Step(scaled);
            Near(GripReaction(scaled, Forces(scaled), Length * 2).x, 4 * ElasticReaction(Stretch, Length), "doubling metric dimensions multiplies force by area factor four");
        }

        static double RampFactor(double dt)
            => .4 + .25 * .08 / dt * (1 - Math.Exp(-dt / .08)) + .35 * .3 / dt * (1 - Math.Exp(-dt / .3));
        static double HoldFactor(double rampDuration, double held)
            => .4 + .25 * .08 / rampDuration * (1 - Math.Exp(-rampDuration / .08)) * Math.Exp(-held / .08)
                + .35 * .3 / rampDuration * (1 - Math.Exp(-rampDuration / .3)) * Math.Exp(-held / .3);
        static void HeldRelaxation()
        {
            var coupon = Cube(2, Length, Relaxing()); Prescribe(coupon, Stretch, Quaternion.identity, Vector3.zero); Step(coupon);
            var positions = (Vector3[])coupon.Positions.Clone(); double last = GripReaction(coupon, Forces(coupon), Length).x;
            Near(last, ElasticReaction(Stretch, Length) * RampFactor(.02), "committed ramp reaction agrees with independent Maxwell solution");
            for (int i = 1; i <= 100; i++)
            {
                Step(coupon); double reaction = GripReaction(coupon, Forces(coupon), Length).x;
                Near(reaction, ElasticReaction(Stretch, Length) * HoldFactor(.02, i * .02), "fixed affine coupon reaction follows exponential relaxation");
                Assert(reaction <= last + 1e-6 && reaction > ElasticReaction(Stretch, Length) * .399, "held reaction decays toward positive equilibrium");
                Exact(positions, coupon.Positions, "relaxation happens at fixed prescribed geometry"); last = reaction;
            }
            Near(last, .4 * ElasticReaction(Stretch, Length), "long fixed strain approaches equilibrium force", 1e-5, .0015);
        }
        static void Subdivision()
        {
            const float duration = .1f; double finalStrain = ((double)Stretch * Stretch - 1) / 2;
            var coarse = Cube(1, Length, Relaxing()); var fine = Cube(2, Length, Relaxing());
            foreach (var coupon in new[] { coarse, fine })
            {
                int steps = ReferenceEquals(coupon, coarse) ? 5 : 25;
                for (int i = 1; i <= steps; i++)
                {
                    float stretch = (float)Math.Sqrt(1 + 2 * finalStrain * i / steps);
                    Prescribe(coupon, stretch, Quaternion.identity, Vector3.zero); Step(coupon, duration / steps);
                }
                Near(GripReaction(coupon, Forces(coupon), Length).x, ElasticReaction(Stretch, Length) * RampFactor(duration), "constant Green strain rate ramp agrees across timestep subdivisions");
                for (int i = 0; i < steps * 4; i++) Step(coupon, duration / steps);
                Near(GripReaction(coupon, Forces(coupon), Length).x, ElasticReaction(Stretch, Length) * HoldFactor(duration, duration * 4), "fixed strain hold agrees across timestep subdivisions");
            }
            NearVector(GripReaction(coarse, Forces(coarse), Length), GripReaction(fine, Forces(fine), Length), "coarse and fine ramp/hold end with same force");
        }

        static void InvalidAndRejectedTrials()
        {
            var nodes = new[] { Vector3.zero, Vector3.right * .02f, Vector3.up * .02f, Vector3.forward * .02f };
            var partial = new TissueVolume(nodes, new[] { new TissueVolume.Cell { a = 0, b = 1, c = 2, d = 3 } }, new[] { Elastic() }, new[] { true, true, true, false });
            Assert(!partial.SetBoundaryTarget(3, nodes[3]) && !partial.SetBoundaryTarget(-1, Vector3.zero) && !partial.SetBoundaryTarget(4, Vector3.zero), "free nodes and out-of-range original roots reject boundary targets");
            Assert(!partial.SetBoundaryTarget(0, new Vector3(float.NaN, 0, 0)) && !partial.SetBoundaryTarget(0, new Vector3(0, float.PositiveInfinity, 0)), "nonfinite boundary targets rejected");
            var trial = Cube(1, Length, Relaxing()); var control = Cube(1, Length, Relaxing());
            foreach (var coupon in new[] { trial, control }) { Prescribe(coupon, Stretch, Quaternion.identity, Vector3.zero); for (int i = 0; i < 8; i++) Step(coupon); }
            var history = new HistoryState(trial); var positions = (Vector3[])trial.Positions.Clone(); var force = Forces(trial);
            Throws<ArgumentException>(() => trial.MeasureNodalForces(null), "null force output rejected");
            Throws<ArgumentException>(() => trial.MeasureNodalForces(new Vector3[trial.NodeCount + 1]), "wrong force buffer size rejected");
            Prescribe(trial, -.5f, Quaternion.identity, Vector3.zero); Exact(force, Forces(trial), "finite pending inversion leaves accepted force untouched"); Step(trial);
            Exact(positions, trial.Positions, "prescribed inversion rolls back accepted geometry"); Exact(force, Forces(trial), "prescribed inversion retains accepted force cache");
            history.AssertRetained(trial, "prescribed inversion retains committed material memory"); Assert(trial.HasAcceptedStep, "rejected successor retains existing accepted state");
            Step(trial); Exact(positions, trial.Positions, "rejected prescribed target remains pending until replaced"); history.AssertRetained(trial, "repeated rejected loading cannot advance time");
            foreach (float dt in new[] { 0f, -.01f, float.NaN, float.PositiveInfinity, .1f }) { Step(trial, dt); Exact(force, Forces(trial), "invalid duration retains accepted force"); }
            Prescribe(trial, Stretch, Quaternion.identity, Vector3.zero); Step(trial); Step(control);
            Exact(positions, trial.Positions, "replacing rejected target resumes from accepted geometry");
            Exact(Forces(control), Forces(trial), "accepted successor agrees with untouched history control");
        }

        static void CutAndReset()
        {
            var nodes = new[] { Vector3.zero, Vector3.right * .03f, Vector3.up * .03f, Vector3.forward * .02f, Vector3.back * .02f };
            var cells = new[] { new TissueVolume.Cell { a = 0, b = 1, c = 2, d = 3 }, new TissueVolume.Cell { a = 0, b = 2, c = 1, d = 4 } };
            var coupon = new TissueVolume(nodes, cells, new[] { Relaxing() }, new[] { true, true, true, true, true });
            Prescribe(coupon, Stretch, Quaternion.identity, Vector3.zero); for (int i = 0; i < 5; i++) Step(coupon);
            var history = new HistoryState(coupon); float mass = coupon.TotalMass; int intact = coupon.NodeCount;
            var reaction = GripReaction(coupon, Forces(coupon), .03f);
            Assert(coupon.CutSweep(new Vector3(-.005f, -.005f, 0), new Vector3(.06f, -.005f, 0), new Vector3(-.005f, .06f, 0), .0001f) == 1, "finite blade breaks independently constructed shared face");
            Assert(coupon.NodeCount > intact && coupon.CutFaceCount == 1, "cut duplicates boundary material fans");
            Near(coupon.TotalMass, mass, "cut preserves reference mass"); history.AssertRetained(coupon, "cut retains committed cell histories");
            NearVector(GripReaction(coupon, Forces(coupon), .03f), reaction, "cut redistributes internal forces without changing aggregate grip traction");
            AssertGeometry(coupon, Stretch, Quaternion.identity, Vector3.zero); Step(coupon); AssertGeometry(coupon, Stretch, Quaternion.identity, Vector3.zero);
            var translation = new Vector3(.002f, .003f, -.001f); Prescribe(coupon, Stretch, Quaternion.identity, translation); Step(coupon);
            AssertGeometry(coupon, Stretch, Quaternion.identity, translation);
            coupon.Freeze(); var frozen = Forces(coupon); Exact(frozen, Forces(coupon), "tracking freeze does not relax force through wall time");
            coupon.Reset(); Assert(!coupon.HasAcceptedStep && coupon.NodeCount == intact && coupon.CutFaceCount == 0 && Zero(Forces(coupon)), "retry restores topology and clears accepted force/history");
            AssertGeometry(coupon, 1, Quaternion.identity, Vector3.zero); Step(coupon); AssertGeometry(coupon, 1, Quaternion.identity, Vector3.zero);
            foreach (var force in Forces(coupon)) NearVector(force, Vector3.zero, "retry resets original-root grip targets to rest without appreciable force");
        }

        sealed class HistoryState
        {
            readonly MaxwellHistory[] identities; readonly TissueTensor[] previous; readonly TissueTensor[][] viscous;
            public HistoryState(TissueVolume volume)
            {
                identities = (MaxwellHistory[])Field<MaxwellHistory[]>(volume, "histories").Clone();
                previous = new TissueTensor[identities.Length]; viscous = new TissueTensor[identities.Length][];
                for (int i = 0; i < identities.Length; i++) { previous[i] = Field<TissueTensor>(identities[i], "previous"); viscous[i] = (TissueTensor[])Field<TissueTensor[]>(identities[i], "viscous").Clone(); }
            }
            public void AssertRetained(TissueVolume volume, string reason)
            {
                var actual = Field<MaxwellHistory[]>(volume, "histories");
                for (int i = 0; i < actual.Length; i++)
                {
                    Assert(ReferenceEquals(actual[i], identities[i]) && (Field<TissueTensor>(actual[i], "previous") - previous[i]).SquaredNorm == 0, reason + ": material identity and strain");
                    var branches = Field<TissueTensor[]>(actual[i], "viscous");
                    for (int b = 0; b < branches.Length; b++) Assert((branches[b] - viscous[i][b]).SquaredNorm == 0, reason + ": viscous branch");
                }
            }
        }
        static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        static bool Zero(Vector3[] values) { foreach (var value in values) if (value.sqrMagnitude != 0) return false; return true; }
        static void Exact(Vector3[] expected, Vector3[] actual, string reason)
        {
            Assert(expected.Length == actual.Length, reason + ": node count");
            for (int i = 0; i < expected.Length; i++) Assert((expected[i] - actual[i]).sqrMagnitude == 0, reason);
        }
        static void NearVector(Vector3 actual, Vector3 expected, string reason, float absolute = 2e-6f)
            => Assert((actual - expected).magnitude <= Mathf.Max(absolute, expected.magnitude * .0005f), reason);
        static void Near(double actual, double expected, string reason, double absolute = 2e-6, double relative = .0005)
            => Assert(Math.Abs(actual - expected) <= Math.Max(absolute, Math.Abs(expected) * relative), reason);
        static void Throws<T>(Action action, string reason) where T : Exception
        { bool rejected = false; try { action(); } catch (T) { rejected = true; } Assert(rejected, reason); }
        static void Assert(bool value, string reason)
        { checks++; if (!value) throw new InvalidOperationException("Material force coupon: " + reason); }
    }
}
