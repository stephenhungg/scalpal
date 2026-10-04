using System;
using System.Reflection;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Analytical material points isolate constitutive response from node velocity damping.
    // Additional synthetic volumes test actual solver history lifecycle, not measured tissue.
    public static class NativeViscoelasticValidation
    {
        static int checks;
        static readonly float[] Fractions = { .35f, .25f };
        static readonly float[] Times = { .1f, .7f };
        static VolumeMaterial Material => new VolumeMaterial { id = "synthetic_maxwell_fixture", youngPascals = 12000, poissonRatio = 0, densityKgPerCubicMeter = 1000, measurementSource = "Synthetic analytical fixture; no tissue calibration" };
        static TissueTensor Axial(float value) => new TissueTensor(new Vector3(value, 0, 0), Vector3.zero, Vector3.zero);
        static MaxwellHistory History() => new MaxwellHistory(Fractions, Times);

        [MenuItem("Scalpal/Quest/Validate Viscoelastic Law")]
        public static void Run()
        {
            checks = 0;
            ParametersAndUnits(); RampAndFixedStrainHold(); EnergyGradient(); Subdivision(); TransactionAndReset(); PassiveBounds();
            ActualVolumeHistoryLifecycle(); RejectedVolumeTrial();
            Debug.Log("SCALPAL_NATIVE_VISCOELASTIC_VALIDATION_OK checks=" + checks + " analytical material points and synthetic volume history lifecycle; no measured tissue viscosity or headset evidence");
        }

        static void ParametersAndUnits()
        {
            foreach (float bad in new[] { -1f, float.NaN, float.PositiveInfinity })
                Throws<ArgumentException>(() => new MaxwellHistory(new[] { bad }, new[] { 1f }), "invalid passive fraction");
            foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                Throws<ArgumentException>(() => new MaxwellHistory(new[] { .2f }, new[] { bad }), "invalid seconds");
            Throws<ArgumentException>(() => new MaxwellHistory(new[] { .5f, .5f }, new[] { 1f, 2f }), "zero equilibrium rejected");
            Throws<ArgumentException>(() => new MaxwellHistory(new[] { 1.1f }, new[] { 1f }), "negative equilibrium rejected");
            Throws<ArgumentException>(() => new MaxwellHistory(null, Array.Empty<float>()), "null arrays rejected");
            Throws<ArgumentException>(() => new MaxwellHistory(new[] { .2f }, Array.Empty<float>()), "mismatched arrays rejected");
            var g = new[] { .2f }; var tau = new[] { .5f }; var law = new MaxwellHistory(g, tau);
            g[0] = 1; tau[0] = 0;
            Assert(Near(law.EquilibriumFraction, .8) && law.BranchFraction(0) == .2f && law.RelaxationSeconds(0) == .5f, "constructor owns a stable passive spectrum");
            Assert(Near(law.BranchViscosityPascalSeconds(0, Material), 1200), "uniaxial branch viscosity has Pa*s scaling");
            Assert(!law.HasHistory, "new material point has no committed history");
            Throws<InvalidOperationException>(() => law.Energy(Axial(.01f), Material), "unprepared potential rejected");
            Throws<InvalidOperationException>(() => law.Commit(Axial(.01f)), "unprepared commit rejected");
            var elastic = new MaxwellHistory(Array.Empty<float>(), Array.Empty<float>()); elastic.Prepare(.1f);
            var e = Axial(.02f);
            Assert(elastic.BranchCount == 0 && elastic.EquilibriumFraction == 1, "empty spectrum is pure elasticity");
            Assert(Near(elastic.Energy(e, Material), 2.4) && Near(elastic.Stress(e, Material).x.x, 240), "elastic energy J/m^3 and stress Pa agree with known SI values");
            elastic.Commit(e); Assert(elastic.HasHistory, "commit records previous strain even for elastic limit");
        }

        static void RampAndFixedStrainHold()
        {
            var law = History(); const float rampDuration = .4f, rate = .1f, dt = .02f;
            for (int i = 1; i <= 20; i++)
            {
                float t = i * dt; var e = Axial(rate * t);
                law.Prepare(dt);
                double expected = .4 * rate * t;
                for (int b = 0; b < Fractions.Length; b++) expected += Fractions[b] * Times[b] * rate * (1 - Math.Exp(-t / Times[b]));
                Assert(Near(law.Stress(e, Material).x.x, Material.youngPascals * expected), "exact constant Green-strain-rate ramp stress at sample " + i);
                law.Commit(e);
            }
            var fixedStrain = Axial(rate * rampDuration);
            float last = float.PositiveInfinity;
            for (int i = 1; i <= 200; i++)
            {
                float t = i * dt; law.Prepare(dt);
                double expected = .4 * rate * rampDuration;
                for (int b = 0; b < Fractions.Length; b++)
                    expected += Fractions[b] * Times[b] * rate * (1 - Math.Exp(-rampDuration / Times[b])) * Math.Exp(-t / Times[b]);
                float reaction = law.Stress(fixedStrain, Material).x.x;
                Assert(Near(reaction, Material.youngPascals * expected), "fixed-strain hold has exponential reaction decay at sample " + i);
                Assert(reaction <= last + .0001f && reaction >= .4f * Material.youngPascals * fixedStrain.x.x - .001f, "hold reaction decreases toward positive equilibrium without node motion");
                Assert(law.Energy(fixedStrain, Material) >= 0, "hold trial potential remains nonnegative");
                last = reaction; law.Commit(fixedStrain);
            }
            Assert(last < .4f * Material.youngPascals * fixedStrain.x.x * 1.003f, "long fixed-strain hold approaches equilibrium modulus");
        }

        static void EnergyGradient()
        {
            var law = History(); var material = Material; material.poissonRatio = .3f;
            var prior = new TissueTensor(new Vector3(.02f, .003f, -.001f), new Vector3(.003f, -.004f, .002f), new Vector3(-.001f, .002f, .008f));
            law.Prepare(.03f); law.Commit(prior); law.Prepare(.017f);
            var e = prior + new TissueTensor(new Vector3(.007f, -.001f, .002f), new Vector3(-.001f, .002f, .001f), new Vector3(.002f, .001f, -.003f));
            var basis = new[] {
                Axial(1), new TissueTensor(Vector3.zero, Vector3.up, Vector3.zero), new TissueTensor(Vector3.zero, Vector3.zero, Vector3.forward),
                new TissueTensor(Vector3.up, Vector3.right, Vector3.zero), new TissueTensor(Vector3.forward, Vector3.zero, Vector3.right),
                new TissueTensor(Vector3.zero, Vector3.forward, Vector3.up)
            };
            var stress = law.Stress(e, material); const float h = .00002f;
            foreach (var direction in basis)
            {
                float finiteDifference = (law.Energy(e + direction * h, material) - law.Energy(e - direction * h, material)) / (2 * h);
                float analytical = Vector3.Dot(stress.x, direction.x) + Vector3.Dot(stress.y, direction.y) + Vector3.Dot(stress.z, direction.z);
                Assert(Near(finiteDifference, analytical, .002, .003), "SI trial energy gradient equals full second Piola reaction, including symmetric shear");
            }
            Assert(TissueCage.Finite(stress.x) && TissueCage.Finite(stress.y) && TissueCage.Finite(stress.z) && Finite(law.Energy(e, material)), "stress and trial energy remain finite");
        }

        static void Subdivision()
        {
            var coarse = History(); var fine = History(); const float duration = .6f;
            var final = new TissueTensor(new Vector3(.04f, .006f, .002f), new Vector3(.006f, -.012f, .003f), new Vector3(.002f, .003f, .007f));
            coarse.Prepare(duration); var coarseStress = coarse.Stress(final, Material); coarse.Commit(final);
            TissueTensor fineStress = default;
            for (int i = 1; i <= 120; i++) { var e = final * (i / 120f); fine.Prepare(duration / 120); fineStress = fine.Stress(e, Material); fine.Commit(e); }
            AssertTensor(coarseStress, fineStress, "constant strain-rate ramp is invariant to timestep subdivision");
            coarse.Prepare(.4f); coarseStress = coarse.Stress(final, Material); coarse.Commit(final);
            for (int i = 0; i < 80; i++) { fine.Prepare(.005f); fineStress = fine.Stress(final, Material); fine.Commit(final); }
            AssertTensor(coarseStress, fineStress, "fixed-strain hold is invariant to timestep subdivision");
        }

        static void TransactionAndReset()
        {
            var control = History(); var trial = History(); var prior = Axial(.03f);
            foreach (var law in new[] { control, trial }) { law.Prepare(.12f); law.Commit(prior); }
            trial.Prepare(.6f); trial.Stress(Axial(-.3f), Material); trial.Energy(Axial(.4f), Material);
            Assert(trial.HasHistory, "aborted trial retains previously committed history");
            foreach (float invalid in new[] { 0f, -.1f, float.NaN, float.PositiveInfinity })
                Throws<ArgumentException>(() => trial.Prepare(invalid), "invalid trial duration rejected without a commit");
            Throws<ArgumentException>(() => trial.Commit(Axial(float.NaN)), "invalid committed strain rejected atomically");
            control.Prepare(.01f); trial.Prepare(.01f);
            AssertTensor(control.Stress(prior, Material), trial.Stress(prior, Material), "rejected trials and repeated Prepare never advance committed memory");
            float pausedReaction = trial.Stress(prior, Material).x.x;
            for (int i = 0; i < 100; i++) Assert(trial.Stress(prior, Material).x.x == pausedReaction, "paused evaluation does not decay history with elapsed wall time");
            control.Commit(prior); trial.Commit(prior);
            Throws<InvalidOperationException>(() => trial.Commit(prior), "one Prepare cannot commit twice");
            control.Prepare(.025f); trial.Prepare(.025f);
            AssertTensor(control.Stress(prior, Material), trial.Stress(prior, Material), "accepted successor trial agrees with untouched control");
            trial.Reset(); Assert(!trial.HasHistory, "retry clears committed material history");
            Throws<InvalidOperationException>(() => trial.Stress(prior, Material), "retry invalidates prepared trial");
            var fresh = History(); fresh.Prepare(.04f); trial.Prepare(.04f);
            AssertTensor(fresh.Stress(prior, Material), trial.Stress(prior, Material), "retry exactly restores virgin ramp response");
            Assert(Near(fresh.Energy(prior, Material), trial.Energy(prior, Material)), "retry restores virgin trial potential");
        }

        static void PassiveBounds()
        {
            foreach (float dt in new[] { 1e-12f, 1e-6f, .01f, 10f, 1e8f })
            {
                var law = History(); law.Prepare(dt); var strain = Axial(.05f);
                float reaction = law.Stress(strain, Material).x.x;
                Assert(Finite(reaction) && reaction >= .4f * 600 - .001f && reaction <= 600 + .001f, "positive branch spectrum keeps ramp stiffness between equilibrium and instantaneous limits");
                Assert(Finite(law.Energy(strain, Material)) && law.Energy(strain, Material) >= 0, "passive ramp potential is finite nonnegative across timestep scales");
            }
            var zero = new MaxwellHistory(new[] { 0f }, new[] { .2f }); zero.Prepare(.2f);
            Assert(Near(zero.Stress(Axial(.05f), Material).x.x, 600), "zero-weight Maxwell branch adds no reaction");
            var largeRatio = new MaxwellHistory(new[] { .5f }, new[] { float.Epsilon }); largeRatio.Prepare(float.MaxValue);
            Assert(Near(largeRatio.Stress(Axial(.05f), Material).x.x, 300), "extreme positive dt/tau avoids underflow division and reaches equilibrium");
            var smallRatio = new MaxwellHistory(new[] { .5f }, new[] { float.MaxValue }); smallRatio.Prepare(float.Epsilon);
            Assert(Near(smallRatio.Stress(Axial(.05f), Material).x.x, 600), "extreme small dt/tau avoids cancellation and retains instantaneous stiffness");
        }

        static VolumeMaterial CoupledMaterial()
        {
            var material = Material;
            material.relaxationFractions = (float[])Fractions.Clone(); material.relaxationSeconds = (float[])Times.Clone();
            return material;
        }

        sealed class CommittedState
        {
            readonly MaxwellHistory[] identities;
            readonly TissueTensor[] strain;
            readonly TissueTensor[][] viscous;
            readonly bool[] present;
            public CommittedState(TissueVolume volume)
            {
                identities = (MaxwellHistory[])Histories(volume).Clone();
                strain = new TissueTensor[identities.Length]; viscous = new TissueTensor[identities.Length][]; present = new bool[identities.Length];
                for (int i = 0; i < identities.Length; i++)
                {
                    strain[i] = Field<TissueTensor>(identities[i], "previous");
                    viscous[i] = (TissueTensor[])Field<TissueTensor[]>(identities[i], "viscous").Clone();
                    present[i] = identities[i].HasHistory;
                }
            }
            public void AssertRetained(TissueVolume volume, string reason)
            {
                var histories = Histories(volume); Assert(histories.Length == identities.Length, reason + ": material cell count");
                for (int i = 0; i < histories.Length; i++)
                {
                    Assert(ReferenceEquals(histories[i], identities[i]) && histories[i].HasHistory == present[i], reason + ": per-cell material identity/history flag");
                    Assert((Field<TissueTensor>(histories[i], "previous") - strain[i]).SquaredNorm == 0, reason + ": committed Green strain");
                    var branch = Field<TissueTensor[]>(histories[i], "viscous");
                    Assert(branch.Length == viscous[i].Length, reason + ": branch count");
                    for (int b = 0; b < branch.Length; b++) Assert((branch[b] - viscous[i][b]).SquaredNorm == 0, reason + ": committed viscous strain");
                }
            }
        }

        static void ActualVolumeHistoryLifecycle()
        {
            var nodes = new[] { Vector3.zero, Vector3.right * .03f, Vector3.up * .03f, Vector3.forward * .02f, Vector3.back * .02f };
            var cells = new[] { new TissueVolume.Cell { a = 0, b = 1, c = 2, d = 3 }, new TissueVolume.Cell { a = 0, b = 2, c = 1, d = 4 } };
            var volume = new TissueVolume(nodes, cells, new[] { CoupledMaterial() }, new[] { true, true, true, false, false });
            var histories = Histories(volume);
            Assert(histories.Length == cells.Length && !histories[0].HasHistory && !histories[1].HasHistory, "actual solver begins with separate virgin per-cell material history");
            Assert(volume.BeginHandle(nodes[3], .001f), "actual volume acquires movable apex for strain loading");
            var target = nodes[3] + Vector3.right * .001f;
            for (int i = 0; i < 20; i++) volume.Step(1f / 90, target, Vector3.zero);
            Assert(volume.Handle >= 0 && (volume.HandlePosition - target).sqrMagnitude < 1e-12f, "actual solver accepts bounded handle displacement");
            bool nonzeroStrain = false, nonzeroViscous = false;
            for (int cell = 0; cell < cells.Length; cell++)
            {
                Assert(histories[cell].HasHistory && histories[cell].BranchCount == Fractions.Length, "accepted timestep commits each cell's explicit passive spectrum");
                var committed = Field<TissueTensor>(histories[cell], "previous");
                Assert((committed - GreenStrain(volume, cell)).SquaredNorm < 1e-12f, "committed strain agrees with independently reconstructed accepted cell geometry");
                nonzeroStrain |= committed.SquaredNorm > 1e-8f;
                foreach (var branch in Field<TissueTensor[]>(histories[cell], "viscous")) nonzeroViscous |= branch.SquaredNorm > 1e-10f;
            }
            Assert(nonzeroStrain && nonzeroViscous, "held geometry develops actual strain and time-dependent viscous memory");
            var loaded = new CommittedState(volume); var positions = (Vector3[])volume.Positions.Clone();
            volume.Freeze(); loaded.AssertRetained(volume, "tracking freeze retains material memory");
            Assert(volume.Handle < 0, "tracking freeze releases loading handle");
            for (int i = 0; i < positions.Length; i++) Assert((volume.Positions[i] - positions[i]).sqrMagnitude == 0, "tracking freeze preserves loaded geometry");
            int intactNodes = volume.NodeCount; float mass = volume.TotalMass;
            Assert(volume.CutSweep(new Vector3(-.005f, -.005f, 0), new Vector3(.06f, -.005f, 0), new Vector3(-.005f, .06f, 0), .0001f) == 1,
                "actual finite cut fractures the shared material face");
            Assert(volume.NodeCount > intactNodes && volume.CutFaceCount == 1 && Near(volume.TotalMass, mass), "fracture changes node fans while conserving material mass");
            loaded.AssertRetained(volume, "fracture preserves cell material history across new node bindings");
            volume.Reset();
            Assert(volume.CutFaceCount == 0 && volume.NodeCount == intactNodes && Near(volume.TotalMass, mass), "retry restores intact coupled mechanics");
            foreach (var history in Histories(volume))
            {
                Assert(!history.HasHistory && Field<TissueTensor>(history, "previous").SquaredNorm == 0, "retry clears cell committed strain");
                foreach (var branch in Field<TissueTensor[]>(history, "viscous")) Assert(branch.SquaredNorm == 0, "retry clears every viscous strain branch");
            }
        }

        static void RejectedVolumeTrial()
        {
            // A thin tetrahedron with pinned base and movable apex: the final handle
            // projection forces inversion independently of constitutive solver strength.
            var nodes = new[] { Vector3.zero, Vector3.right * .03f, Vector3.up * .03f, Vector3.forward * .0002f };
            var volume = new TissueVolume(nodes, new[] { new TissueVolume.Cell { a = 0, b = 1, c = 2, d = 3 } }, new[] { CoupledMaterial() }, new[] { true, true, true, false });
            Assert(volume.BeginHandle(nodes[3], .00005f), "thin cell acquires apex loading handle");
            var target = nodes[3] + Vector3.forward * .00004f;
            for (int i = 0; i < 8; i++) volume.Step(1f / 90, target, Vector3.zero);
            var history = Histories(volume)[0];
            Assert(history.HasHistory && Field<TissueTensor>(history, "previous").SquaredNorm > 1e-5f, "thin cell first accepts loading with nonzero committed strain");
            Assert(Field<TissueTensor[]>(history, "viscous")[0].SquaredNorm > 1e-8f, "thin cell has nonzero viscous memory before rejection");
            var accepted = new CommittedState(volume);
            foreach (float invalid in new[] { 0f, -.1f, float.NaN, float.PositiveInfinity, .1f })
            {
                volume.Step(invalid, target, Vector3.zero); accepted.AssertRetained(volume, "invalid solver timestep retains committed history");
            }
            Assert(volume.BeginHandle(target, .00005f), "thin cell reacquires its accepted apex");
            var positions = (Vector3[])volume.Positions.Clone(); float mass = volume.TotalMass;
            volume.Step(1f / 90, Vector3.back * .0002f, Vector3.zero);
            Assert(volume.Handle < 0, "inverted thin-cell trial is rejected and releases handle");
            for (int i = 0; i < positions.Length; i++) Assert((volume.Positions[i] - positions[i]).sqrMagnitude == 0, "rejected thin-cell trial rolls geometry back to accepted state");
            Assert(Near(volume.TotalMass, mass) && volume.CutFaceCount == 0, "rejected trial preserves reference mass and topology");
            accepted.AssertRetained(volume, "inverted solver trial cannot advance material history");
        }

        static TissueTensor GreenStrain(TissueVolume volume, int cell)
        {
            var source = volume.Cells[cell];
            var rest = new TissueTensor(volume.Original[source.b] - volume.Original[source.a], volume.Original[source.c] - volume.Original[source.a], volume.Original[source.d] - volume.Original[source.a]);
            var origin = volume.Positions[volume.NodeFor(cell, 0)];
            var deformed = new TissueTensor(volume.Positions[volume.NodeFor(cell, 1)] - origin, volume.Positions[volume.NodeFor(cell, 2)] - origin, volume.Positions[volume.NodeFor(cell, 3)] - origin);
            var f = deformed.Multiply(rest.Inverse()); return (f.Transpose().Multiply(f) - TissueTensor.Identity) * .5f;
        }
        static MaxwellHistory[] Histories(TissueVolume volume) => Field<MaxwellHistory[]>(volume, "histories");
        static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);

        static void AssertTensor(TissueTensor expected, TissueTensor actual, string reason)
        {
            Assert(Math.Sqrt((expected - actual).SquaredNorm) <= Math.Max(.001, Math.Sqrt(expected.SquaredNorm) * .0003), reason);
        }
        static bool Near(double actual, double expected, double relative = .00015, double absolute = .0001) => Math.Abs(actual - expected) <= Math.Max(absolute, Math.Abs(expected) * relative);
        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static void Throws<T>(Action action, string reason) where T : Exception
        {
            bool rejected = false; try { action(); } catch (T) { rejected = true; }
            Assert(rejected, reason);
        }
        static void Assert(bool value, string reason)
        {
            checks++; if (!value) throw new InvalidOperationException("Viscoelastic law validation failed: " + reason);
        }
    }
}
