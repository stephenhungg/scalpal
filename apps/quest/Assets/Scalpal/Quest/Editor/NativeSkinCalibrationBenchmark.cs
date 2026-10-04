using System;
using System.IO;
using Scalpal.Anatomy.Tissue;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Quest.Editor
{
    // Source-summary comparison through the actual volume/history/force path.
    // An affine material coupon is NOT a reproduction of the source specimen/grips.
    public static class NativeSkinCalibrationBenchmark
    {
        [Serializable] sealed class Source
        {
            public string doi;
            public float holdEngineeringStrain, holdDurationSecondsApproximate;
            public float initialRelaxationModulusPascals, thirtySecondRelaxationModulusPascals;
            public float totalRelaxationFraction, totalRelaxationUncertainty;
        }
        [Serializable] sealed class Candidate
        {
            public float branchFraction, tauSeconds, youngPascals;
            public double equivalentUniaxialBranchViscosityPascalSeconds;
            public float initialApparentModulusPascals, thirtySecondApparentModulusPascals, endApparentModulusPascals;
            public float initialRelativeError, thirtySecondRelativeError, endRelaxationFraction;
            public bool endSummaryIntervalSatisfied;
        }
        [Serializable] sealed class Report
        {
            public string sourceDoi;
            public string evidenceKind = "Source summary comparison through synthetic affine volume coupons; not measured specimen reproduction";
            public bool runtimeProfileCalibrated = false;
            public float assumedRampSeconds, assumedPoissonRatio = .3f;
            public string geometry = "Authored 20x10x2mm rectangular coupon; all nodes prescribed with zero transverse second-Piola stress";
            public string stressConvention = "Nominal axial reaction/original area, divided by engineering strain; source convention/ramp must be confirmed";
            public string identifiability = "Two fit points plus an approximate end interval admit distinct passive spectra; equivalent branch viscosities are estimates, not measured tissue viscosities";
            public Candidate[] candidates;
            public int candidatesSatisfyingEndInterval;
            public float refinedMeshAndTimestepRelativeDifference;
        }
        const float Length = .02f, Area = .01f * .002f;

        [MenuItem("Scalpal/Quest/Benchmark Published Skin Summaries")]
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
            var source = JsonUtility.FromJson<Source>(File.ReadAllText(Path.Combine(root, "assets/anatomy/material-data/blanchard2026/summary.json")));
            Require(source != null && source.doi == "10.1038/s41598-026-42371-9" && source.holdEngineeringStrain > 0 && source.holdEngineeringStrain < .25f,
                "Expected traceable skin-summary source");
            const float dt = 1f / 60f;
            var report = new Report { sourceDoi = source.doi, assumedRampSeconds = dt };
            var fractions = new[] { .33f, .37f, .39f, .41f, .50f, .70f };
            report.candidates = new Candidate[fractions.Length];
            for (int i = 0; i < fractions.Length; i++)
            {
                float fraction = fractions[i];
                float tau = FitTime(fraction, dt, source.thirtySecondRelaxationModulusPascals / source.initialRelaxationModulusPascals);
                float stretch = 1 + source.holdEngineeringStrain;
                float secantFactor = stretch * (stretch * stretch - 1) / (2 * source.holdEngineeringStrain);
                float peakFraction = 1 - fraction + fraction * Ramp(dt / tau);
                float young = source.initialRelaxationModulusPascals / (secantFactor * peakFraction);
                var candidate = Replay(source, fraction, tau, young, dt, 1);
                Require(candidate.initialRelativeError < .001f && candidate.thirtySecondRelativeError < .001f,
                    "Actual solver reactions must agree with both fitted summary points");
                report.candidates[i] = candidate;
                if (candidate.endSummaryIntervalSatisfied) report.candidatesSatisfyingEndInterval++;
            }
            Require(report.candidatesSatisfyingEndInterval > 1 && report.candidatesSatisfyingEndInterval < fractions.Length,
                "End summary must reject some fits while demonstrating nonunique surviving spectra");
            var selected = report.candidates[2];
            // Keep the same assumed ramp duration while subdividing it; no refit on the held-out discretization.
            var refined = Replay(source, selected.branchFraction, selected.tauSeconds, selected.youngPascals, dt / 2, 2, 2);
            report.refinedMeshAndTimestepRelativeDifference = Mathf.Abs(refined.thirtySecondApparentModulusPascals - selected.thirtySecondApparentModulusPascals) /
                selected.thirtySecondApparentModulusPascals;
            Require(report.refinedMeshAndTimestepRelativeDifference < .001f, "Affine reaction is stable under mesh/timestep refinement");
            string output = Environment.GetEnvironmentVariable("SCALPAL_CALIBRATION_REPORT");
            if (string.IsNullOrEmpty(output)) output = Path.Combine(root, "assets/anatomy/material-data/blanchard2026/solver-summary-comparison.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, JsonUtility.ToJson(report, true) + "\n");
            Debug.Log($"SCALPAL_NATIVE_SKIN_CALIBRATION_BENCHMARK_OK candidates={fractions.Length} nonuniqueEndCompatible={report.candidatesSatisfyingEndInterval} refinementRelativeDifference={report.refinedMeshAndTimestepRelativeDifference:G6} calibrated=false sourceSummaryOnly=true");
        }

        static Candidate Replay(Source source, float fraction, float tau, float young, float dt, int resolution, int rampSteps = 1)
        {
            var material = new VolumeMaterial { id = "blanchard2026_summary_candidate_NOT_RUNTIME", youngPascals = young, poissonRatio = .3f,
                densityKgPerCubicMeter = 1000, relaxationFractions = new[] { fraction }, relaxationSeconds = new[] { tau },
                measurementSource = "Fit to two published summary points under assumed ramp/nominal stress; not validated specimen material" };
            var grid = TissueVolumeFactory.Box(new Bounds(new Vector3(Length / 2, 0, 0), new Vector3(Length, .01f, .002f)),
                resolution, resolution, resolution, material, false);
            var pins = new bool[grid.Original.Length]; for (int i = 0; i < pins.Length; i++) pins[i] = true;
            var coupon = new TissueVolume(grid.Original, grid.Cells, new[] { material }, pins);
            var forces = new Vector3[coupon.NodeCount];
            float finalStretch = 1 + source.holdEngineeringStrain;
            // Linear Green-strain ramp matches the exact history update; source ramp remains unknown.
            float green = (finalStretch * finalStretch - 1) / 2;
            for (int step = 1; step <= rampSteps; step++)
            {
                float e = green * step / rampSteps;
                var f = new TissueTensor(Vector3.right * Mathf.Sqrt(1 + 2 * e), Vector3.up * Mathf.Sqrt(1 - .6f * e), Vector3.forward * Mathf.Sqrt(1 - .6f * e));
                for (int i = 0; i < coupon.Original.Length; i++) Require(coupon.SetBoundaryTarget(i, f.Multiply(coupon.Original[i])), "Prescribed coupon boundary accepted");
                coupon.Step(dt, Vector3.zero, Vector3.zero);
            }
            Require(coupon.HasAcceptedStep, "Coupon ramp committed");
            var result = new Candidate { branchFraction = fraction, tauSeconds = tau, youngPascals = young,
                equivalentUniaxialBranchViscosityPascalSeconds = (double)fraction * young * tau };
            result.initialApparentModulusPascals = ApparentModulus(coupon, forces, source.holdEngineeringStrain);
            int thirtySteps = Mathf.RoundToInt(30 / dt), endSteps = Mathf.RoundToInt(source.holdDurationSecondsApproximate / dt);
            for (int step = 1; step <= endSteps; step++)
            {
                coupon.Step(dt, Vector3.zero, Vector3.zero);
                if (step == thirtySteps) result.thirtySecondApparentModulusPascals = ApparentModulus(coupon, forces, source.holdEngineeringStrain);
            }
            result.endApparentModulusPascals = ApparentModulus(coupon, forces, source.holdEngineeringStrain);
            result.initialRelativeError = Mathf.Abs(result.initialApparentModulusPascals - source.initialRelaxationModulusPascals) / source.initialRelaxationModulusPascals;
            result.thirtySecondRelativeError = Mathf.Abs(result.thirtySecondApparentModulusPascals - source.thirtySecondRelaxationModulusPascals) / source.thirtySecondRelaxationModulusPascals;
            result.endRelaxationFraction = 1 - result.endApparentModulusPascals / result.initialApparentModulusPascals;
            result.endSummaryIntervalSatisfied = Mathf.Abs(result.endRelaxationFraction - source.totalRelaxationFraction) <= source.totalRelaxationUncertainty;
            return result;
        }
        static float ApparentModulus(TissueVolume coupon, Vector3[] forces, float strain)
        {
            coupon.MeasureNodalForces(forces); float reaction = 0;
            for (int cell = 0; cell < coupon.Cells.Length; cell++) for (int corner = 0; corner < 4; corner++)
            {
                int node = coupon.NodeFor(cell, corner), original = coupon.Cells[cell].Vertex(corner);
                if (Mathf.Abs(coupon.Original[original].x - Length) < 1e-7f) { reaction -= forces[node].x; forces[node] = Vector3.zero; }
            }
            return reaction / (Area * strain);
        }
        static float Ramp(double ratio) => (float)((1 - Math.Exp(-ratio)) / ratio);
        static float FitTime(float fraction, float rampSeconds, float ratio)
        {
            double lo = .1, hi = 1000;
            for (int i = 0; i < 60; i++)
            {
                double tau = (lo + hi) / 2, peak = fraction * Ramp(rampSeconds / tau);
                double model = (1 - fraction + peak * Math.Exp(-30 / tau)) / (1 - fraction + peak);
                if (model < ratio) lo = tau; else hi = tau;
            }
            return (float)((lo + hi) / 2);
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Skin summary benchmark: " + message); }
    }
}
