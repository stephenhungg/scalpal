using System;

namespace Scalpal.Anatomy.Tissue
{
    // Fixed-pressure, finite-source incompressible orifice model; parameters are
    // teaching assumptions, not measured appendicular-artery physiology.
    // All stored volumes are m^3. Only explicitly named public mL values convert units.
    public sealed class VesselBleeding
    {
        public const double MaxStepSeconds = .1;
        public const double MaxPressurePascals = 100000;
        public const double MaxSourceMilliliters = 10000;
        public const double MaxInjuryAreaSquareMeters = .0001;
        public const double MaxSimulationSeconds = 3600;
        const double MillilitersPerCubicMeter = 1000000;

        readonly double initialSourceCubicMeters;
        double cumulativeLossCubicMeters, removedCubicMeters;

        public double PressureDifferencePascals { get; }
        public double DensityKgPerCubicMeter { get; }
        public double DischargeCoefficient { get; }
        public double MaximumInjuryAreaSquareMeters { get; }
        public double MaximumSimulationSeconds { get; }
        public double InjuryAreaSquareMeters { get; private set; }
        public double ElapsedSeconds { get; private set; }
        public bool IsOccluded { get; private set; }
        public bool IsFaulted { get; private set; }
        public double LastStepLossMilliliters { get; private set; }

        public double InitialSourceMilliliters => initialSourceCubicMeters * MillilitersPerCubicMeter;
        public double RemainingSourceMilliliters => Math.Max(0, initialSourceCubicMeters - cumulativeLossCubicMeters) * MillilitersPerCubicMeter;
        public double CumulativeLossMilliliters => cumulativeLossCubicMeters * MillilitersPerCubicMeter;
        public double RemovedMilliliters => removedCubicMeters * MillilitersPerCubicMeter;
        public double PooledMilliliters => Math.Max(0, cumulativeLossCubicMeters - removedCubicMeters) * MillilitersPerCubicMeter;

        // Q = Cd A sqrt(2 deltaP / rho). This is the available discharge rate,
        // not the last step's actual rate when the finite source is exhausted.
        public double FlowCubicMetersPerSecond => IsFaulted || IsOccluded || RemainingSourceMilliliters <= 0 ? 0 :
            DischargeCoefficient * InjuryAreaSquareMeters * Math.Sqrt(2 * PressureDifferencePascals / DensityKgPerCubicMeter);
        public double FlowMillilitersPerSecond => FlowCubicMetersPerSecond * MillilitersPerCubicMeter;

        public VesselBleeding(double pressureDifferencePascals = 12000, double densityKgPerCubicMeter = 1060,
            double dischargeCoefficient = .6, double sourceMilliliters = 500,
            double maximumInjuryAreaSquareMeters = .00001, double maximumSimulationSeconds = 3600)
        {
            if (!InRange(pressureDifferencePascals, 0, MaxPressurePascals) ||
                !InRange(densityKgPerCubicMeter, 1, 3000) || !InRange(dischargeCoefficient, 0, 1) ||
                !InRange(sourceMilliliters, 0, MaxSourceMilliliters) ||
                !InRange(maximumInjuryAreaSquareMeters, double.Epsilon, MaxInjuryAreaSquareMeters) ||
                !InRange(maximumSimulationSeconds, double.Epsilon, MaxSimulationSeconds))
                throw new ArgumentException("Invalid bleeding configuration; use bounded SI parameters");
            PressureDifferencePascals = pressureDifferencePascals;
            DensityKgPerCubicMeter = densityKgPerCubicMeter;
            DischargeCoefficient = dischargeCoefficient;
            MaximumInjuryAreaSquareMeters = maximumInjuryAreaSquareMeters;
            MaximumSimulationSeconds = maximumSimulationSeconds;
            initialSourceCubicMeters = sourceMilliliters / MillilitersPerCubicMeter;
        }

        // One effective vessel injury; repeated contact must not sum the same cut.
        // Further contact can enlarge the opening. Opening does not remove a clamp.
        public bool OpenInjury(double areaSquareMeters)
        {
            if (IsFaulted) return false;
            if (!InRange(areaSquareMeters, double.Epsilon, MaximumInjuryAreaSquareMeters)) return Fault();
            InjuryAreaSquareMeters = Math.Max(InjuryAreaSquareMeters, areaSquareMeters);
            return true;
        }

        // Invalid time emits no fluid and latches a fault until retry/reset.
        // No catch-up integration of a stalled frame or hidden-time interpolation.
        public bool Step(double seconds)
        {
            LastStepLossMilliliters = 0;
            if (IsFaulted) return false;
            if (!InRange(seconds, 0, MaxStepSeconds) || ElapsedSeconds + seconds > MaximumSimulationSeconds)
                return Fault();
            double emitted = Math.Min(FlowCubicMetersPerSecond * seconds,
                Math.Max(0, initialSourceCubicMeters - cumulativeLossCubicMeters));
            double nextLoss = Math.Min(initialSourceCubicMeters, cumulativeLossCubicMeters + emitted);
            if (!Finite(emitted) || !Finite(nextLoss) || emitted < 0 || nextLoss < cumulativeLossCubicMeters)
                return Fault();
            emitted = nextLoss - cumulativeLossCubicMeters;
            cumulativeLossCubicMeters = nextLoss;
            ElapsedSeconds += seconds;
            LastStepLossMilliliters = emitted * MillilitersPerCubicMeter;
            return true;
        }

        // Root's gated Seal/Clip interaction owns when this changes. No scored event here.
        public void SetOccluded(bool occluded) => IsOccluded = occluded;

        // Returns actual volume removed. Suction cannot undo blood already lost.
        public double RemovePool(double requestedMilliliters)
        {
            if (IsFaulted) return 0;
            if (!InRange(requestedMilliliters, 0, MaxSourceMilliliters)) { Fault(); return 0; }
            double available = Math.Max(0, cumulativeLossCubicMeters - removedCubicMeters);
            double removed = Math.Min(available, requestedMilliliters / MillilitersPerCubicMeter);
            // Preserve the loss = pool + removed ledger even on a complete drain.
            removedCubicMeters = removed >= available ? cumulativeLossCubicMeters : removedCubicMeters + removed;
            return removed * MillilitersPerCubicMeter;
        }

        public void Reset()
        {
            InjuryAreaSquareMeters = 0; ElapsedSeconds = 0;
            cumulativeLossCubicMeters = 0; removedCubicMeters = 0;
            LastStepLossMilliliters = 0; IsOccluded = false; IsFaulted = false;
        }

        bool Fault() { IsFaulted = true; LastStepLossMilliliters = 0; return false; }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool InRange(double value, double minimum, double maximum) => Finite(value) && value >= minimum && value <= maximum;
    }
}
