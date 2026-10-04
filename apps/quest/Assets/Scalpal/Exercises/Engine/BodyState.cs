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
        readonly Dictionary<string, double> facts = new Dictionary<string, double>();
        readonly List<BodyRecord> log = new List<BodyRecord>();
        readonly HashSet<string> seen = new HashSet<string>();
        readonly Dictionary<string, Dictionary<string, float>> clamps = new Dictionary<string, Dictionary<string, float>>();
        readonly Dictionary<string, List<float>> ties = new Dictionary<string, List<float>>();
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
            ["hemostat"] = new[]{"clamp"}, ["right_angle_clamp"] = new[]{"clamp"}, ["suture_tie"] = new[]{"tie","place"},
            ["hook_cautery"] = new[]{"seal"}, ["vessel_sealer"] = new[]{"seal"},
            ["suction_irrigator"] = new[]{"suction","inspect"}, ["decision"] = new[]{"decide"}, ["assistant"] = new[]{"close","tick"},
        };
        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        public static bool ValidBodyAction(BodyAction e)
        {
            if (e == null || string.IsNullOrEmpty(e.actionId) || e.instrumentId == null || e.instrumentInstanceId == null || e.secondaryInstanceId == null ||
                e.verb == null || e.tissueId == null || e.layer == null || e.choice == null || e.coordinateFrame != "registered_torso_m") return false;
            if (!Finite(e.position.x) || !Finite(e.position.y) || !Finite(e.position.z)) return false;
            foreach (var v in new double[]{e.timeMs,e.speedMps,e.forceProxy,e.distanceMm,e.lengthMm,e.angleDegrees,e.depthMm,e.durationMs,e.separationMm})
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
            foreach (var t in Tissues) if (Get(t.id, "bleeding") > 0) { lost += dt * t.flowMlPerSecond; active++; }
            Set("", "bloodLostMl", Get("", "bloodLostMl") + lost);
            Set("", "poolMl", Get("", "poolMl") + lost);
            Set("", "activeBleedSeconds", Get("", "activeBleedSeconds") + (active > 0 ? dt : 0));
            var outcomes = new List<string>();
            void Put(string fact, double value = 1) => Set(tissue.id, fact, value);
            if (!clamps.TryGetValue(tissue.id, out var clamp)) clamp = new Dictionary<string, float>();
            if (!ties.TryGetValue(tissue.id, out var tied)) tied = new List<float>();
            bool blocked = Tissues.Any(t => t.order >= 0 && (tissue.order < 0 || t.order < tissue.order) && Get(t.id, "opened") == 0);
            if (blocked && e.verb != "decide" && e.verb != "tick" && e.verb != "close") outcomes.Add("not_exposed");
            else switch (e.verb)
            {
                case "mark":
                    Put("marked"); Put("markErrorMm", e.distanceMm); Put("markLengthMm", e.lengthMm); Put("markAngleDegrees", e.angleDegrees); break;
                case "cut":
                    if (!tissue.cuttable) { outcomes.Add("not_cuttable"); break; }
                    if (e.lengthMm < 1) { outcomes.Add("no_cut"); break; }
                    Put("opened"); Put("divided"); Put("cutLengthMm", Math.Max(Get(tissue.id, "cutLengthMm"), e.lengthMm));
                    if (Get(tissue.id, "markLengthMm") > 0) Put("cutCoverage", Math.Min(1, Get(tissue.id, "cutLengthMm") / Get(tissue.id, "markLengthMm")));
                    Put("cutErrorMm", e.distanceMm); Put("cutAngleDegrees", e.angleDegrees); Put("cutDepthMm", e.depthMm);
                    if (tissue.splittable) { Put("bladeUsed"); outcomes.Add("muscle_cut"); }
                    if (e.angleDegrees > 25) outcomes.Add("across_fibers");
                    if (tissue.tentable) { Put("tentedBeforeCut", Get(tissue.id, "tented")); if (Get(tissue.id, "tented") == 0) outcomes.Add("untented_cut"); }
                    var positions = clamp.Values.OrderBy(v => v).ToArray();
                    bool between = positions.Length >= 2 && e.distanceMm > positions[0] && e.distanceMm < positions[positions.Length - 1];
                    Put("cutBetweenClamps", between ? 1 : 0);
                    Put("cutPositionMm", e.distanceMm);
                    Put("tiedBothSides", tied.Any(p => p < e.distanceMm) && tied.Any(p => p > e.distanceMm) ? 1 : 0);
                    bool proximal = tied.Any(p => p < e.distanceMm);
                    Put("cutBetweenTieAndClamp", proximal && positions.Any(p => p > e.distanceMm) ? 1 : 0);
                    if (tissue.perfused && !between && !proximal && Get(tissue.id, "sealed") == 0) { Put("bleeding"); outcomes.Add("cut_unsecured"); }
                    if (tissue.hollow)
                    {
                        Put("removed");
                        if (!proximal) { Put("leaking"); Set("", "contamination", 1); outcomes.Add("hollow_leak"); }
                        else { Put("stumpLengthMm", e.distanceMm); Put("removed"); Put("cutAboveTie"); }
                    }
                    if (tissue.critical) outcomes.Add("critical_injury");
                    break;
                case "clamp":
                    if (string.IsNullOrEmpty(e.instrumentInstanceId)) { outcomes.Add("missing_instance"); break; }
                    clamp[e.instrumentInstanceId] = e.distanceMm; clamps[tissue.id] = clamp;
                    Put("clampCount", clamp.Count); Put("bleeding", 0);
                    if (e.instrumentId == "right_angle_clamp") Put("crushed"); break;
                case "tie":
                    if (!tied.Any(p => Math.Abs(p - e.distanceMm) < 1)) tied.Add(e.distanceMm);
                    ties[tissue.id] = tied; Put("tieCount", tied.Count); Put("tieDistanceMm", tied.Min()); Put("bleeding", 0); Put("leaking", 0);
                    if (Get(tissue.id, "divided") > 0)
                    {
                        double cut = Get(tissue.id, "cutPositionMm");
                        Put("tiedBothSides", tied.Any(p => p < cut) && tied.Any(p => p > cut) ? 1 : 0);
                    }
                    break;
                case "seal": Put("sealed"); Put("bleeding", 0); break;
                case "grasp": case "retract":
                    Put("liftMm", e.depthMm); if (tissue.tentable) Put("tented", e.depthMm >= 8 ? 1 : 0);
                    if (e.depthMm >= 15) Put("delivered");
                    if (tissue.splittable && !string.IsNullOrEmpty(e.instrumentInstanceId) && !string.IsNullOrEmpty(e.secondaryInstanceId) && e.instrumentInstanceId != e.secondaryInstanceId && e.separationMm >= 15 && e.angleDegrees <= 25) { Put("opened"); Put("splitWidthMm", e.separationMm); }
                    if (e.speedMps > .1f || e.forceProxy > 1) outcomes.Add("rough_handling"); break;
                case "suction": Set("", "poolMl", Math.Max(0, Get("", "poolMl") - e.durationMs * .005)); break;
                case "inspect": Put("inspectionMs", Math.Max(Get(tissue.id, "inspectionMs"), e.durationMs)); break;
                case "decide": Put("decision_" + e.choice); break;
                case "close": Put("closed"); break;
                case "place": Put("placed"); break;
            }
            Set("", "activeBleeds", Tissues.Count(t => Get(t.id, "bleeding") > 0));
            var record = new BodyRecord { action = e.Copy(), outcomes = outcomes.ToArray() };
            log.Add(record); return record;
        }
    }
}
