using System;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Pure synthetic fluid fixtures; no circulation, patient or physical headset evidence.
    public static class NativeBleedingValidation
    {
        static int checks;
        [MenuItem("Scalpal/Quest/Validate Native Bleeding")]
        public static void Run()
        {
            checks = 0;
            FlowAndBalance();
            ControlsAndReset();
            LongPractice();
            InvalidInputs();
            Debug.Log("SCALPAL_NATIVE_BLEEDING_VALIDATION_OK checks=" + checks + " synthetic fluid fixtures; no clinical/headset validation");
        }

        static VesselBleeding Fixture(double pressure = 1000, double density = 1000, double source = 1,
            double duration = VesselBleeding.MaxSimulationSeconds) => new VesselBleeding(
                pressureDifferencePascals: pressure, densityKgPerCubicMeter: density,
                dischargeCoefficient: .5, sourceMilliliters: source, maximumSimulationSeconds: duration);

        static void FlowAndBalance()
        {
            var model = Fixture();
            Assert(model.Step(.1) && model.CumulativeLossMilliliters == 0, "intact vessel emits no fluid");
            Assert(model.OpenInjury(.000001), "valid opening accepted");
            double expectedRate = .5 * .000001 * Math.Sqrt(2) * 1000000;
            Assert(Near(model.FlowMillilitersPerSecond, expectedRate), "Bernoulli orifice rate and m3-to-mL conversion");
            Assert(model.Step(.1) && Near(model.LastStepLossMilliliters, expectedRate * .1), "flow integrates actual time");
            Ledger(model);
            Assert(model.OpenInjury(.000001) && Near(model.FlowMillilitersPerSecond, expectedRate), "duplicate cut does not double discharge");
            Assert(model.OpenInjury(.000002) && Near(model.FlowMillilitersPerSecond, 2 * expectedRate), "larger area doubles discharge");
            Assert(model.OpenInjury(.000001) && Near(model.FlowMillilitersPerSecond, 2 * expectedRate), "smaller contact does not heal injury");

            var highPressure = Fixture(pressure: 4000); highPressure.OpenInjury(.000001);
            Assert(Near(highPressure.FlowMillilitersPerSecond, 2 * expectedRate), "fourfold pressure gives double discharge");
            var dense = new VesselBleeding(1000, 2000, .5, 1); dense.OpenInjury(.000001);
            Assert(Near(dense.FlowMillilitersPerSecond, expectedRate / Math.Sqrt(2)), "density scales inverse square root");
            var zeroPressure = Fixture(pressure: 0); zeroPressure.OpenInjury(.000001); zeroPressure.Step(.1);
            Assert(zeroPressure.CumulativeLossMilliliters == 0, "zero driving pressure emits nothing");
            var zeroCoefficient = new VesselBleeding(1000, 1000, 0, 1); zeroCoefficient.OpenInjury(.000001); zeroCoefficient.Step(.1);
            Assert(zeroCoefficient.CumulativeLossMilliliters == 0, "zero discharge coefficient emits nothing");

            var coarse = Fixture(); var fine = Fixture(); coarse.OpenInjury(.000001); fine.OpenInjury(.000001);
            for (int i = 0; i < 10; i++) Assert(coarse.Step(.1), "coarse time partition accepted");
            for (int i = 0; i < 20; i++) Assert(fine.Step(.05), "fine time partition accepted");
            Assert(Near(coarse.CumulativeLossMilliliters, fine.CumulativeLossMilliliters), "constant discharge independent of step partition");
            Ledger(coarse); Ledger(fine);

            var finiteSource = Fixture(source: .1); finiteSource.OpenInjury(.000001);
            finiteSource.Step(.1); finiteSource.Step(.1);
            Assert(Near(finiteSource.CumulativeLossMilliliters, .1) && finiteSource.RemainingSourceMilliliters == 0, "last discharge capped by finite source");
            Assert(finiteSource.FlowMillilitersPerSecond == 0 && finiteSource.Step(.1) && finiteSource.LastStepLossMilliliters == 0, "empty source stops discharge");
            Ledger(finiteSource);
        }

        static void ControlsAndReset()
        {
            var model = Fixture(); model.OpenInjury(.000001); model.Step(.1);
            double lost = model.CumulativeLossMilliliters;
            model.SetOccluded(true);
            Assert(model.FlowMillilitersPerSecond == 0 && model.Step(.1) && model.CumulativeLossMilliliters == lost, "seal/clamp occlusion stops flow without undoing loss");
            Assert(model.OpenInjury(.000002) && model.IsOccluded && model.FlowMillilitersPerSecond == 0, "injury does not silently undo occlusion");
            model.SetOccluded(false); model.Step(.1);
            Assert(model.CumulativeLossMilliliters > lost, "released clamp resumes discharge");
            lost = model.CumulativeLossMilliliters;
            double removed = model.RemovePool(.02);
            Assert(Near(removed, .02) && Near(model.RemovedMilliliters, .02), "suction reports actual removal");
            Assert(model.CumulativeLossMilliliters == lost && Near(model.PooledMilliliters, lost - .02), "suction lowers pool, not cumulative loss");
            Ledger(model);
            double pool = model.PooledMilliliters;
            Assert(Near(model.RemovePool(1), pool) && model.PooledMilliliters == 0, "over-request drains only available pool");
            Assert(model.RemovePool(1) == 0 && model.RemovePool(0) == 0, "empty pool cannot create suction volume");
            Ledger(model);
            model.Step(.1);
            Assert(model.PooledMilliliters > 0 && model.RemovedMilliliters < model.CumulativeLossMilliliters, "fresh bleeding repopulates drained pool");
            Ledger(model);
            model.SetOccluded(true); model.Reset();
            Assert(model.InjuryAreaSquareMeters == 0 && !model.IsOccluded && !model.IsFaulted && model.ElapsedSeconds == 0, "retry resets injury, clamp, fault and clock");
            Assert(model.CumulativeLossMilliliters == 0 && model.PooledMilliliters == 0 && model.RemovedMilliliters == 0 && model.LastStepLossMilliliters == 0, "retry clears full fluid ledger");
            Assert(model.RemainingSourceMilliliters == model.InitialSourceMilliliters && model.PressureDifferencePascals == 1000, "retry restores source while preserving configuration");
            Assert(model.OpenInjury(.000001) && model.Step(.1), "retry supports a new injury");
            Ledger(model);
        }

        static void LongPractice()
        {
            var model = Fixture();
            bool valid = true;
            for (int i = 0; i < 72001; i++) valid &= model.Step(.05);
            Assert(valid && !model.IsFaulted && model.ElapsedSeconds == 0 && model.CumulativeLossMilliliters == 0,
                "more than one hour of intact practice cannot expire the vessel");
            Assert(model.OpenInjury(.000001) && model.Step(.1) && model.CumulativeLossMilliliters > 0,
                "injury still discharges after long intact practice");
            for (int i = 0; i < 72001; i++) valid &= model.Step(.05);
            Assert(valid && !model.IsFaulted && model.ElapsedSeconds == model.MaximumSimulationSeconds,
                "injured/exhausted model remains usable after diagnostic clock cap");
            Assert(model.RemovePool(1) > 0, "long injury does not disable suction");
            Ledger(model);
        }

        static void InvalidInputs()
        {
            foreach (double area in new[] { double.NaN, double.PositiveInfinity, -.000001, 0, .000011 })
            {
                var model = Fixture(); model.OpenInjury(.000001); model.Step(.1); double lost = model.CumulativeLossMilliliters;
                Assert(!model.OpenInjury(area) && model.IsFaulted, "invalid opening fails closed");
                Assert(model.FlowMillilitersPerSecond == 0 && !model.Step(.1) && model.CumulativeLossMilliliters == lost, "invalid opening cannot emit more fluid");
                Ledger(model); model.Reset(); Assert(!model.IsFaulted && model.OpenInjury(.000001), "reset clears fault latch");
            }
            foreach (double seconds in new[] { double.NaN, double.PositiveInfinity, -.1, .100001 })
            {
                var model = Fixture(); model.OpenInjury(.000001);
                Assert(!model.Step(seconds) && model.IsFaulted && model.CumulativeLossMilliliters == 0 && model.ElapsedSeconds == 0, "nonfinite/negative/stalled time emits no fluid");
                Ledger(model);
            }
            foreach (double removal in new[] { double.NaN, double.PositiveInfinity, -.01, 10001 })
            {
                var model = Fixture(); model.OpenInjury(.000001); model.Step(.1); double pool = model.PooledMilliliters;
                Assert(model.RemovePool(removal) == 0 && model.IsFaulted && model.PooledMilliliters == pool, "invalid suction preserves ledger and fails closed");
                Ledger(model);
            }
            var boundedTime = Fixture(duration: .25); boundedTime.OpenInjury(.000001);
            Assert(boundedTime.Step(.1) && boundedTime.Step(.1), "bounded total time admits valid steps");
            double before = boundedTime.CumulativeLossMilliliters;
            Assert(boundedTime.Step(.1) && !boundedTime.IsFaulted && boundedTime.CumulativeLossMilliliters > before && Near(boundedTime.ElapsedSeconds, .25),
                "diagnostic clock saturates without disabling valid discharge");
            var zeroStep = Fixture(); zeroStep.OpenInjury(.000001);
            Assert(zeroStep.Step(0) && zeroStep.CumulativeLossMilliliters == 0, "zero time is a no-op");
            var empty = Fixture(source: 0); empty.OpenInjury(.000001); empty.Step(.1);
            Assert(empty.CumulativeLossMilliliters == 0 && empty.FlowMillilitersPerSecond == 0, "empty configured source emits nothing");
            foreach (var create in new Func<VesselBleeding>[] {
                () => new VesselBleeding(double.NaN), () => new VesselBleeding(-1),
                () => new VesselBleeding(100001), () => new VesselBleeding(densityKgPerCubicMeter: 0),
                () => new VesselBleeding(densityKgPerCubicMeter: 3001), () => new VesselBleeding(dischargeCoefficient: 1.01),
                () => new VesselBleeding(sourceMilliliters: double.PositiveInfinity), () => new VesselBleeding(sourceMilliliters: -1),
                () => new VesselBleeding(sourceMilliliters: 10001), () => new VesselBleeding(maximumInjuryAreaSquareMeters: .000101),
                () => new VesselBleeding(maximumSimulationSeconds: 0), () => new VesselBleeding(maximumSimulationSeconds: 3601) })
            {
                bool rejected = false; try { create(); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "invalid configuration rejected before simulation");
            }
        }

        static void Ledger(VesselBleeding model)
        {
            Assert(Near(model.InitialSourceMilliliters, model.RemainingSourceMilliliters + model.CumulativeLossMilliliters), "source plus loss conserves initial volume");
            Assert(Near(model.CumulativeLossMilliliters, model.PooledMilliliters + model.RemovedMilliliters), "pool plus suction conserves cumulative loss");
            foreach (double value in new[] { model.RemainingSourceMilliliters, model.CumulativeLossMilliliters, model.PooledMilliliters, model.RemovedMilliliters })
                Assert(!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0, "ledger remains finite and nonnegative");
        }
        static bool Near(double a, double b) => Math.Abs(a - b) <= 1e-9 + 1e-8 * Math.Max(Math.Abs(a), Math.Abs(b));
        static void Assert(bool condition, string message)
        {
            checks++; if (!condition) throw new InvalidOperationException("Native bleeding validation: " + message);
        }
    }
}
