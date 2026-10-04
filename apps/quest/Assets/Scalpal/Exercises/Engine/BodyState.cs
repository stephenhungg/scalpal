using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Exercises.Data;

namespace Scalpal.Exercises.Engine
{
    public sealed class BodyRecord
    {
        public BodyAction action;
        public string[] outcomes;
    }

    // Case-independent semantic body. Mirrors services/preop/src/open-body.ts.
    // Physical exposure can reject an effect; expected case order never can.
    public sealed class BodyState
    {
        // Clamps/ties control an injury at or proximal to it (smaller distance from the base); a seal
        // controls only its own point. Unmeasured occluders cannot control a measured injury.
        public const double HemostasisToleranceMm = 3;
        // One rough-handling outcome per tool instance and tissue in this window, however often a held tool reports.
        public const double RoughHandlingCooldownMs = 3000;
        struct Injury { public double positionMm; public bool measured; }
        readonly Dictionary<string, double> facts = new Dictionary<string, double>();
        readonly List<BodyRecord> log = new List<BodyRecord>();
        readonly HashSet<string> seen = new HashSet<string>();
        readonly Dictionary<string, Dictionary<string, double>> clamps = new Dictionary<string, Dictionary<string, double>>();
        readonly Dictionary<string, List<double>> ties = new Dictionary<string, List<double>>();
        readonly Dictionary<string, HashSet<string>> looseClamps = new Dictionary<string, HashSet<string>>(); // No longitudinal measurement.
        readonly Dictionary<string, int> looseControls = new Dictionary<string, int>(); // Unmeasured ties and seals.
        readonly Dictionary<string, List<double>> seals = new Dictionary<string, List<double>>();
        readonly Dictionary<string, List<Injury>> injuries = new Dictionary<string, List<Injury>>();
        readonly Dictionary<string, double> roughAt = new Dictionary<string, double>();
        readonly Dictionary<string, string> decisions = new Dictionary<string, string>();
        double clock = -1;
        public TissueDefinition[] Tissues { get; }
        public IReadOnlyList<BodyRecord> Log => log;
        public IReadOnlyDictionary<string, double> Facts => facts;
        public BodyState(TissueDefinition[] tissues)
        {
            Tissues = tissues ?? Array.Empty<TissueDefinition>();
            foreach (var tissue in Tissues) if (tissue.splittable) Set(tissue.id, "bladeUsed", 0);
        }
        public double Get(string tissueId, string fact) => facts.TryGetValue(tissueId + ":" + fact, out var v) ? v : 0;
        public void Set(string tissueId, string fact, double value) => facts[tissueId + ":" + fact] = value;
        public bool Test(BodyPredicate p)
        {
            if (p == null || !facts.TryGetValue(p.tissueId + ":" + p.fact, out var v)) return false;
            return p.op == "gte" ? v >= p.value : p.op == "lte" ? v <= p.value : p.op == "eq" && v == p.value;
        }
        public static readonly IReadOnlyDictionary<string, string[]> ToolVerbs = new Dictionary<string, string[]>
        {
            ["scalpel"] = new[]{"cut"}, ["metzenbaum_scissors"] = new[]{"cut"}, ["skin_marker"] = new[]{"mark"},
            ["toothed_forceps"] = new[]{"grasp","retract"}, ["retractor"] = new[]{"retract"},
            ["babcock"] = new[]{"grasp","retract"}, ["atraumatic_grasper"] = new[]{"grasp","retract"},
            ["hemostat"] = new[]{"clamp","release"}, ["right_angle_clamp"] = new[]{"clamp","release"}, ["suture_tie"] = new[]{"tie","place"},
            ["hook_cautery"] = new[]{"seal"}, ["vessel_sealer"] = new[]{"seal"},
            ["suction_irrigator"] = new[]{"suction","inspect"}, ["decision"] = new[]{"decide"}, ["assistant"] = new[]{"close","tick","fluid"},
        };
        // Assistant clock ticks and measured fluid snapshots keep the body current; they are not learner actions.
        public static bool IsTelemetry(BodyAction e) => e != null && e.instrumentId == "assistant" && (e.verb == "tick" || e.verb == "fluid");
        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        public static bool ValidBodyAction(BodyAction e)
        {
            if (e == null || string.IsNullOrEmpty(e.actionId) || e.instrumentId == null || e.instrumentInstanceId == null || e.secondaryInstanceId == null ||
                e.verb == null || e.tissueId == null || e.layer == null || e.choice == null || e.coordinateFrame != "registered_torso_m") return false;
            if (!Finite(e.position.x) || !Finite(e.position.y) || !Finite(e.position.z)) return false;
            foreach (var v in new double[]{e.timeMs,e.speedMps,e.forceProxy,e.distanceMm,e.lengthMm,e.angleDegrees,e.depthMm,e.durationMs,e.separationMm,e.bloodLostMl,e.poolMl,e.flowMlPerSecond})
                if (!Finite(v) || v < 0) return false;
            return ToolVerbs.TryGetValue(e.instrumentId, out var verbs) && Array.IndexOf(verbs, e.verb) >= 0;
        }
        public BodyRecord Apply(BodyAction e)
        {
            if (!ValidBodyAction(e) || !e.registered || seen.Contains(e.actionId) || e.timeMs < clock) return null;
            var tissue = Array.Find(Tissues, t => t.id == e.tissueId);
            if (tissue == null || tissue.layer != e.layer) return null;
            seen.Add(e.actionId);
            double dt = clock < 0 ? 0 : (e.timeMs - clock) / 1000;
            clock = e.timeMs;
            double lost = 0; int active = 0;
            foreach (var t in Tissues) if (Get(t.id, "bleeding") > 0) { if (Get(t.id,"fluidDriven") == 0) lost += dt * t.flowMlPerSecond; active++; }
            Set("", "bloodLostMl", Get("", "bloodLostMl") + lost);
            Set("", "poolMl", Get("", "poolMl") + lost);
            Set("", "activeBleedSeconds", Get("", "activeBleedSeconds") + (active > 0 ? dt : 0));
            var outcomes = new List<string>();
            void Put(string fact, double value = 1) => Set(tissue.id, fact, value);
            if (!clamps.TryGetValue(tissue.id, out var clamp)) clamp = new Dictionary<string, double>();
            if (!ties.TryGetValue(tissue.id, out var tied)) tied = new List<double>();
            bool blocked = Tissues.Any(t => t.order >= 0 && (tissue.order < 0 || t.order < tissue.order) && Get(t.id, "opened") == 0);
            if (blocked && e.verb != "decide" && e.verb != "tick" && e.verb != "close" && e.verb != "fluid") outcomes.Add("not_exposed");
            else switch (e.verb)
            {
                case "fluid":
                    if (!tissue.perfused || e.bloodLostMl < Get(tissue.id,"measuredLossMl") || e.poolMl > e.bloodLostMl) { outcomes.Add("invalid_fluid"); break; }
                    Set("","bloodLostMl",Get("","bloodLostMl") + e.bloodLostMl - Get(tissue.id,"measuredLossMl"));
                    Set("","poolMl",Math.Max(0,Get("","poolMl") + e.poolMl - Get(tissue.id,"measuredPoolMl")));
                    Put("measuredLossMl",e.bloodLostMl); Put("measuredPoolMl",e.poolMl); Put("measuredFlowMlPerSecond",e.flowMlPerSecond); Put("fluidDriven"); Put("bleeding",e.flowMlPerSecond>0?1:0); break;
                case "mark":
                    Put("marked"); Put("markErrorMm", e.distanceMm); Put("markLengthMm", e.lengthMm); Put("markAngleDegrees", e.angleDegrees); break;
                case "cut":
                    if (!tissue.cuttable) { outcomes.Add("not_cuttable"); break; }
                    if (e.lengthMm < 1) { outcomes.Add("no_cut"); break; }
                    Put("opened"); Put("divided"); Put("cutLengthMm", Math.Max(Get(tissue.id, "cutLengthMm"), e.lengthMm));
                    if (Get(tissue.id, "markLengthMm") > 0) Put("cutCoverage", Math.Min(1, Get(tissue.id, "cutLengthMm") / Get(tissue.id, "markLengthMm")));
                    Put("cutErrorMm", e.distanceMm); Put("cutAngleDegrees", e.angleDegrees); Put("cutDepthMm", e.depthMm);
                    if (tissue.splittable) { Put("bladeUsed"); outcomes.Add("muscle_cut"); }
                    if (!string.IsNullOrEmpty(tissue.fiberAxis) && e.angleDegrees > 25) outcomes.Add("across_fibers");
                    if (tissue.tentable) { Put("tentedBeforeCut", Get(tissue.id, "tented")); if (Get(tissue.id, "tented") == 0) outcomes.Add("untented_cut"); }
                    var positions = clamp.Values.OrderBy(v => v).ToArray();
                    bool measured = e.choice != "longitudinal_unmeasured";
                    bool between = measured && positions.Length >= 2 && e.distanceMm > positions[0] && e.distanceMm < positions[positions.Length - 1];
                    Put("cutBetweenClamps", between ? 1 : 0);
                    Put("cutPositionMm", e.distanceMm);
                    Put("tiedBothSides", tied.Any(p => p < e.distanceMm) && tied.Any(p => p > e.distanceMm) ? 1 : 0);
                    bool proximal = measured && tied.Any(p => p < e.distanceMm);
                    Put("cutBetweenTieAndClamp", proximal && positions.Any(p => p > e.distanceMm) ? 1 : 0);
                    if (tissue.perfused)
                    {
                        var injury = new Injury { positionMm = e.distanceMm, measured = measured };
                        if (!injuries.TryGetValue(tissue.id, out var open)) injuries[tissue.id] = open = new List<Injury>();
                        open.Add(injury); RefreshBleeding(tissue);
                        if (!Controlled(tissue.id, injury)) outcomes.Add("cut_unsecured");
                    }
                    if (tissue.hollow)
                    {
                        Put("removed");
                        if (!proximal) { Put("leaking"); Set("", "contamination", 1); outcomes.Add("hollow_leak"); }
                        else { Put("stumpLengthMm", e.distanceMm); Put("removed"); Put("cutAboveTie"); }
                    }
                    if (tissue.critical) outcomes.Add("critical_injury");
                    break;
                case "clamp":
                {
                    if (string.IsNullOrEmpty(e.instrumentInstanceId)) { outcomes.Add("missing_instance"); break; }
                    if (!looseClamps.TryGetValue(tissue.id, out var loose)) loose = new HashSet<string>();
                    if (e.choice == "longitudinal_unmeasured") { clamp.Remove(e.instrumentInstanceId); loose.Add(e.instrumentInstanceId); looseClamps[tissue.id] = loose; }
                    else
                    {
                        loose.Remove(e.instrumentInstanceId); clamp[e.instrumentInstanceId] = e.distanceMm; clamps[tissue.id] = clamp;
                        if (e.instrumentId == "right_angle_clamp") Put("crushed");
                    }
                    Put("clampCount", clamp.Count); RefreshBleeding(tissue); break;
                }
                case "release":
                {
                    bool removed = clamp.Remove(e.instrumentInstanceId) || (looseClamps.TryGetValue(tissue.id, out var loose) && loose.Remove(e.instrumentInstanceId));
                    if (!removed) { outcomes.Add("not_clamped"); break; }
                    double before = Get(tissue.id, "bleeding"); Put("clampCount", clamp.Count); RefreshBleeding(tissue);
                    if (before == 0 && Get(tissue.id, "bleeding") > 0) outcomes.Add("rebleed");
                    break;
                }
                case "tie":
                    if (e.choice == "longitudinal_unmeasured") { looseControls[tissue.id] = LooseControls(tissue.id) + 1; Put("leaking", 0); RefreshBleeding(tissue); break; }
                    if (!tied.Any(p => Math.Abs(p - e.distanceMm) < 1)) tied.Add(e.distanceMm);
                    ties[tissue.id] = tied; Put("tieCount", tied.Count);
                    if (Get(tissue.id, "divided") > 0)
                    {
                        double cut = Get(tissue.id, "cutPositionMm");
                        Put("tiedBothSides", tied.Any(p => p < cut) && tied.Any(p => p > cut) ? 1 : 0);
                    }
                    Put("tieDistanceMm", tied.Min()); Put("leaking", 0); RefreshBleeding(tissue);
                    break;
                case "seal":
                    Put("sealed");
                    if (e.choice == "longitudinal_unmeasured") looseControls[tissue.id] = LooseControls(tissue.id) + 1;
                    else { if (!seals.TryGetValue(tissue.id, out var points)) seals[tissue.id] = points = new List<double>(); points.Add(e.distanceMm); }
                    RefreshBleeding(tissue); break;
                case "grasp": case "retract":
                    Put("liftMm", e.depthMm); if (tissue.tentable) Put("tented", e.depthMm >= 8 ? 1 : 0);
                    if (e.depthMm >= 15) Put("delivered");
                    if (tissue.splittable && !string.IsNullOrEmpty(e.instrumentInstanceId) && !string.IsNullOrEmpty(e.secondaryInstanceId) && e.instrumentInstanceId != e.secondaryInstanceId && e.separationMm >= 15 && e.angleDegrees <= 25) { Put("opened"); Put("splitWidthMm", e.separationMm); }
                    if (e.speedMps > .1 || e.forceProxy > 1)
                    {
                        string key = e.instrumentInstanceId + "|" + tissue.id;
                        if (!roughAt.TryGetValue(key, out var last) || e.timeMs - last >= RoughHandlingCooldownMs) { roughAt[key] = e.timeMs; outcomes.Add("rough_handling"); }
                    }
                    break;
                case "suction": if (!Tissues.Any(t=>Get(t.id,"fluidDriven")>0)) Set("", "poolMl", Math.Max(0, Get("", "poolMl") - e.durationMs * .005)); break;
                case "inspect": Put("inspectionMs", Math.Max(Get(tissue.id, "inspectionMs"), e.durationMs)); break;
                case "decide":
                    // The latest answer wins; an earlier choice no longer satisfies a decision predicate.
                    if (decisions.TryGetValue(tissue.id, out var previous) && previous != e.choice) Put("decision_" + previous, 0);
                    decisions[tissue.id] = e.choice; Put("decision_" + e.choice); break;
                case "close": Put("closed"); break;
                case "place": Put("placed"); break;
            }
            Set("", "activeBleeds", Tissues.Count(t => Get(t.id, "bleeding") > 0));
            var record = new BodyRecord { action = e.Copy(), outcomes = outcomes.ToArray() };
            log.Add(record); return record;
        }
        int LooseControls(string tissueId) => looseControls.TryGetValue(tissueId, out var count) ? count : 0;
        bool Controlled(string tissueId, Injury injury)
        {
            var clamped = clamps.TryGetValue(tissueId, out var c) ? c.Values.ToList() : new List<double>();
            if (ties.TryGetValue(tissueId, out var t)) clamped.AddRange(t);
            var sealedAt = seals.TryGetValue(tissueId, out var s) ? s : new List<double>();
            // Without injury geometry, any control on the structure is the best available evidence.
            if (!injury.measured)
                return clamped.Count + sealedAt.Count + (looseClamps.TryGetValue(tissueId, out var loose) ? loose.Count : 0) + LooseControls(tissueId) > 0;
            return clamped.Any(p => p <= injury.positionMm + HemostasisToleranceMm) || sealedAt.Any(p => Math.Abs(p - injury.positionMm) <= HemostasisToleranceMm);
        }
        void RefreshBleeding(TissueDefinition tissue)
        {
            if (!tissue.perfused) return;
            int open = injuries.TryGetValue(tissue.id, out var list) ? list.Count(i => !Controlled(tissue.id, i)) : 0;
            Set(tissue.id, "openInjuries", open); Set(tissue.id, "bleeding", open > 0 ? 1 : 0);
        }
    }
}
