using System;

namespace Scalpal.Exercises.Coach
{
    // DTOs mirror services/preop/src/patient-condition.ts. They contain server facts,
    // never a second physiology/death model or a request to acquire a real baseline.
    [Serializable] public sealed class CoachPatientCondition
    {
        public CoachMonitorVitals vitals;
        public string baselineSource;
        public float weightKg, rawBloodLossMl;
        public CoachConditionRegion[] regions;
        public CoachConditionOutcome outcome;
    }
    [Serializable] public sealed class CoachMonitorVitals
    {
        public float hr, rr, sys, dia, spo2, bloodLossPct, scale;
        public int hemorrhageClass;
        public bool simulated;
        public string label;
        public CoachMonitorBaseline baseline;
    }
    [Serializable] public sealed class CoachMonitorBaseline { public float hr, rr; public string source; }
    [Serializable] public sealed class CoachConditionRegion
    {public string region, label;public bool bleeding;public float rawBleedMlPerMin;public long at;}
    [Serializable] public sealed class CoachConditionOutcome {public string result, cause, at;}
}

