using System;
using System.Collections.Generic;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Anatomy.Tissue;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Actual native-scene atlas geometry, actual scored adapter, synthetic tracked tool poses.
    // Proves the appendix can be mobilized and delivered, then measured on the moved anatomy.
    // The scene's NativeTissueSimulation also runs, so the local cage grasp and contact solver act on
    // the moved parts exactly as at runtime. Not headset evidence.
    public static class OpenBodyDeliveryValidation
    {
        const float Dt = .02f;
        static int checks;
        static void Require(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Open body delivery validation: " + message);
        }
        public static int Run()
        {
            checks = 0;
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new InvalidOperationException("Open body delivery validation needs to open the native scene; save or discard scene changes first");
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                var scene = new SceneRig(session, adapter, tissue);
                VerifyUnmobilizedControl(scene);
                var vr = VerifyDelivery(scene, "vr_default_fit");
                VerifyRetryRestoresRest(scene);
                VerifySpringBack(scene);
                VerifyFreeze(scene);
                VerifyRoughHandling(scene);
                // AR: the registered torso can sit anywhere; the same wound-relative pull must give the same result.
                var root = session.patientFrame.root;
                root.SetPositionAndRotation(root.position + new Vector3(1.3f, -.2f, .7f), Quaternion.Euler(4, 63, -3) * root.rotation);
                Physics.SyncTransforms();
                var ar = VerifyDelivery(scene, "ar_registered_fit", correctMidPull: true);
                Require(Vector3.Distance(vr, ar) < .005f, $"AR and VR delivery leave the appendix at the same wound-relative pose (vr={vr * 1000:F1} ar={ar * 1000:F1} mm)");
                Debug.Log("SCALPAL_OPEN_DELIVERY_SCENE_OK: " + checks + " actual-scene mobilization/delivery/base checks; vrAppendixWoundMm=" + (vr * 1000).ToString("F1")
                    + " arAppendixWoundMm=" + (ar * 1000).ToString("F1") + "; synthetic tracked poses, no headset test");
                return checks;
            }
            finally
            {
                if (tissue) tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(previous);
            }
        }

        sealed class SceneRig
        {
            public readonly NativeCaseSession session;
            public readonly OpenSurgerySession adapter;
            public readonly OpenBodyInteraction input;
            public readonly AnatomyExerciseBinding exercise;
            public readonly Transform wound;
            public readonly List<BodyRecord> records = new List<BodyRecord>();
            public readonly Transform[] parts;
            public readonly Vector3[] restPositions;
            public readonly Quaternion[] restRotations;
            public readonly AnatomyPart appendix;
            public readonly NativeTissueSimulation tissue;
            public bool gate = true;
            public OrganMobilization Group => input.Mobility.Count > 0 ? input.Mobility[0] : null;
            public SceneRig(NativeCaseSession session, OpenSurgerySession adapter, NativeTissueSimulation tissue)
            {
                this.session = session; this.adapter = adapter; this.tissue = tissue;
                tissue.Initialize(session.anatomy, session.workbench, () => gate);
                Require(tissue.TissueCount == 3, "native cage simulation binds appendix, mesoappendix and artery"); input = adapter.Interaction; exercise = session.exercise; wound = adapter.Wound.transform;
                // Same composition as OpenSurgerySession.ConfigureAttempt, with a validation-owned practice gate.
                input.Initialize(exercise, session.workbench.tools, session.patientFrame, wound, () => gate);
                Require(input.ConfigureMobility(adapter.mobileOrganGroups, out var status) && input.Mobility.Count == 1, "scene mobile organ group binds to atlas parts: " + status);
                Require(OpenSurgeryAnatomy.Bind(session.anatomy, input), "atlas base references bind on the rest pose");
                input.Submitted += (record, _) => records.Add(record);
                Require(session.anatomy.TryGetPart("appendix", out appendix), "atlas appendix exists");
                parts = Group.Parts.ToArray();
                restPositions = parts.Select(p => p.localPosition).ToArray(); restRotations = parts.Select(p => p.localRotation).ToArray();
                Require(parts.Length == 4 && Group.Contains(appendix.transform), "group holds cecum, appendix, mesoappendix and artery");
                foreach (var tool in session.workbench.tools) if (tool) tool.SetHeld(false);
            }
            public InstrumentBehaviour Tool(string id, int index = 0)
            {
                var tool = session.workbench.tools.Where(t => t && t.instrumentId == id).ElementAtOrDefault(index);
                Require(tool && tool.actionPoint, "scene tool exists: " + id + "#" + index);
                return tool;
            }
            public void Hold(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
            public void Place(InstrumentBehaviour tool, Vector3 world)
            {
                tool.transform.rotation = wound.rotation;
                tool.transform.position += world - tool.actionPoint.position;
                Physics.SyncTransforms();
            }
            public void Park(InstrumentBehaviour tool)
            {
                tool.SetHeld(false); Place(tool, wound.TransformPoint(new Vector3(.25f, .25f, -.3f))); Step();
            }
            // Runtime order: the scored adapter in Update, then the local cage/contact step in LateUpdate.
            public void Step(int count = 1) { for (int i = 0; i < count; i++) { input.Simulate(Dt); tissue.Simulate(Dt); Physics.SyncTransforms(); } }
            public void NewAttempt()
            {
                var selected = exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out var reason), "retry selects a fresh attempt: " + reason);
                session.anatomy.SetRegistrationValid(true); gate = true; records.Clear();
                Step(); // The adapter observes the new body and restores the authored anatomy.
                foreach (var step in exercise.SelectedCase.procedure.steps.Take(5)) foreach (var e in CaseRunner.PerfectEvents(step))
                    Require(exercise.Submit(e, out _, out var why), "wall exposure goes through the real score binding: " + why);
                Require(exercise.Body.Get("peritoneum", "opened") == 1 && exercise.Body.Get("appendix", "delivered") == 0, "attempt starts exposed and undelivered");
            }
            public bool AtAuthoredRest()
            {
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i].localPosition != restPositions[i] || parts[i].localRotation != restRotations[i]) return false;
                return true;
            }
            public Vector3[] WorldPositions() => parts.Select(p => p.position).ToArray();
            // Atlas part origins can sit far from their tissue; speed bounds are checked on the tissue.
            public Vector3[] MeshCenters() => parts.Select(p => p.TransformPoint(p.GetComponent<MeshFilter>().sharedMesh.bounds.center)).ToArray();
            // Linear limit plus the bounded tilt rate acting on tissue up to 10 cm from the group pivot.
            public float Bound(float linearMps) => linearMps * Dt + Group.Definition.maxTiltDegreesPerSecond * Dt * Mathf.Deg2Rad * .1f + .00002f;
            public Vector3 AppendixCenterWound()
            {
                var filter = appendix.GetComponent<MeshFilter>();
                return wound.InverseTransformPoint(appendix.transform.TransformPoint(filter.sharedMesh.bounds.center));
            }
            // Grasp site: the upper appendix surface along the wound's inward axis.
            public Vector3 AppendixGraspSite()
            {
                var collider = appendix.GetComponent<MeshCollider>();
                Vector3 center = appendix.transform.TransformPoint(appendix.GetComponent<MeshFilter>().sharedMesh.bounds.center);
                Require(collider.Raycast(new Ray(center - wound.forward * .1f, wound.forward), out var hit, .2f), "actual appendix collider has an upper surface");
                return hit.point - wound.forward * .002f;
            }
            public SurgeryTissueTarget Target(string id) => input.Targets.Single(t => t.tissueId == id);
            public BodyRecord LastGrasp(string id) => records.LastOrDefault(r => r.action.tissueId == id && (r.action.verb == "grasp" || r.action.verb == "retract"));
        }

        // Grasp the appendix and lift along a wound-relative path at a gentle speed.
        static void Pull(SceneRig s, InstrumentBehaviour tool, Vector3 fromWound, Vector3 toWound, float stepMeters = .0015f, Action<int> during = null)
        {
            int steps = Mathf.CeilToInt(Vector3.Distance(fromWound, toWound) / stepMeters);
            for (int i = 1; i <= steps; i++)
            {
                s.Place(tool, s.wound.TransformPoint(Vector3.Lerp(fromWound, toWound, i / (float)steps)));
                s.Step(); during?.Invoke(i);
            }
        }
        static InstrumentBehaviour Grasp(SceneRig s)
        {
            var babcock = s.Tool("babcock"); s.Hold(babcock);
            s.Place(babcock, s.AppendixGraspSite()); s.Step(6);
            Require(s.LastGrasp("appendix") != null && s.LastGrasp("appendix").action.depthMm < 15, "babcock grasps the deep appendix without a delivery measurement");
            return babcock;
        }

        static void VerifyUnmobilizedControl(SceneRig s)
        {
            // The previous behaviour: no mobile group, only the bounded local cage. The tool reaching the
            // delivery height must not earn delivery while the organ stays put.
            Require(s.input.ConfigureMobility(Array.Empty<MobileOrganGroup>(), out _) && s.input.Mobility.Count == 0, "mobility can be disabled for the control run");
            s.NewAttempt();
            Vector3 before = s.AppendixCenterWound();
            Require(before.z > .14f, $"atlas appendix starts deep below the wound plane ({before.z * 1000:F1} mm)");
            var tool = Grasp(s);
            Pull(s, tool, s.wound.InverseTransformPoint(tool.actionPoint.position), new Vector3(0, 0, -.03f));
            Require(s.exercise.Body.Get("appendix", "delivered") == 0 && s.LastGrasp("appendix").action.depthMm < 15,
                "a moving tool with an unmoving organ earns no delivery");
            Require(OpenSurgeryAnatomy.DeliveryReachBound(s.session.anatomy, s.wound, out _, out float cageMm), "cage reach bound measurable");
            float moved = Vector3.Distance(before, s.AppendixCenterWound()) * 1000;
            Require(moved <= cageMm + 1, $"without mobilization only the local cage moves the appendix ({moved:F1} mm, cage bound {cageMm:F1} mm)");
            Debug.Log($"SCALPAL_OPEN_DELIVERY_CONTROL unmobilized appendix centre moved {moved:F1} mm (cage bound {cageMm:F1} mm); delivered=0 lastDepthMm={s.LastGrasp("appendix").action.depthMm:F1}");
            s.Park(tool);
            Require(s.input.ConfigureMobility(s.adapter.mobileOrganGroups, out var status) && s.input.Mobility.Count == 1, "scene group rebinds: " + status);
        }

        static Vector3 VerifyDelivery(SceneRig s, string label, bool correctMidPull = false)
        {
            s.NewAttempt();
            Require(s.AtAuthoredRest(), label + ": attempt begins at authored rest poses");
            var deformables = new[] { "appendix", "mesoappendix", "appendicular_artery" }
                .Select(id => s.session.anatomy.TryGetPart(id, out var part) ? part.GetComponent<DeformableTissue>() : null).ToArray();
            Require(deformables.All(d => d && d.Cage != null), label + ": actual deformable cages exist for the moved parts");
            using (var solver = new TissueContactSolver())
            {
                solver.Initialize(deformables, true);
                // Compare rigid poses only: relax any local cage deformation left by earlier handling.
                foreach (var deformable in deformables) deformable.ResetTissue();
                float restResidual = solver.Measure(true); int restActive = solver.ActiveBodies;
                Require(solver.SupportedBodies == 3 && restActive == 3, label + ": contact solver binds all three rest bodies");
                // Remember a tissue-fixed probe and its rest longitudinal measurement (cages relaxed on both sides).
                var appendixTarget = s.Target("appendix");
                Require(appendixTarget.HasBase, label + ": appendix base reference bound");
                Vector3 probeRest = appendixTarget.basePoint.position + appendixTarget.LongitudinalWorld * .004f;
                Require(appendixTarget.DistanceFromBase(probeRest, out float restMm), label + ": rest base distance measurable");
                Vector3 probeLocal = s.appendix.transform.InverseTransformPoint(probeRest);

                var tool = Grasp(s);
                Vector3 start = s.wound.InverseTransformPoint(tool.actionPoint.position);
                Vector3 end = new Vector3(0, 0, -.03f);
                float maxStep = 0; var last = s.MeshCenters();
                Pull(s, tool, start, end, during: i =>
                {
                    var now = s.MeshCenters();
                    for (int p = 0; p < now.Length; p++) maxStep = Mathf.Max(maxStep, Vector3.Distance(now[p], last[p]));
                    last = now;
                    if (!correctMidPull || i != 40) return;
                    // Registration correction mid-pull: the body (wound + atlas) shifts together.
                    Vector3 appendixWound = s.AppendixCenterWound(), toolWound = s.wound.InverseTransformPoint(tool.actionPoint.position);
                    var root = s.session.patientFrame.root;
                    root.position += root.right * .01f; Physics.SyncTransforms(); s.Place(tool, s.wound.TransformPoint(toolWound));
                    s.Step();
                    Require(Vector3.Distance(appendixWound, s.AppendixCenterWound()) < .00001f, label + ": registration correction moves the group with the body, without a jump");
                    last = s.MeshCenters();
                    // Correction releases the hold; the next stable sample re-acquires the same organ.
                    s.Step(2);
                    Require(s.Group.Held, label + ": grasp re-acquired after registration correction");
                });
                // The group frame is unit scale, so world displacement is metric.
                Require(maxStep <= s.Bound(s.Group.Definition.maxSpeedMps), $"{label}: group motion is speed limited ({maxStep * 1000:F2} mm/frame)");
                var delivery = s.LastGrasp("appendix");
                Vector3 appendixWound = s.AppendixCenterWound();
                Require(s.exercise.Body.Get("appendix", "delivered") == 1 && delivery.action.depthMm >= 15,
                    $"{label}: gentle grasp-and-pull delivers the appendix (measured {delivery.action.depthMm:F1} mm above the wound, centre {-appendixWound.z * 1000:F1} mm)");
                Require(!s.records.Any(r => r.outcomes.Contains("rough_handling")), label + ": gentle delivery is not rough handling");
                Require(s.Group.OffsetMeters.magnitude <= s.Group.Definition.maxTravelMm * .001f + .00001f, label + ": tether bounds travel");
                // depthMm is the grasped tissue point, not the tool tip.
                Vector3 registered = new Vector3(delivery.action.position.x, delivery.action.position.y, delivery.action.position.z);
                Vector3 measured = s.session.patientFrame.TransformPoint(registered);
                Require(Mathf.Abs(-s.wound.InverseTransformPoint(measured).z * 1000 - delivery.action.depthMm) < .01f
                    && s.Target("appendix").TryContact(measured, .003f, out _), label + ": delivery depth is measured on the moved appendix surface");

                tool.SetActivation(0); s.Step(); var held = s.WorldPositions(); s.Step(30);
                Require(!s.Group.Held && s.Group.Delivered(s.wound) && s.WorldPositions().SequenceEqual(held), label + ": released delivered group stays in the wound");
                s.Park(tool);
                foreach (var deformable in deformables) deformable.ResetTissue();

                Require(appendixTarget.DistanceFromBase(s.appendix.transform.TransformPoint(probeLocal), out float movedMm) && Mathf.Abs(movedMm - restMm) < .01f,
                    $"{label}: longitudinal base distance follows the moved appendix ({restMm:F2} vs {movedMm:F2} mm)");
                float movedResidual = solver.Measure(true);
                Require(solver.ActiveBodies == restActive && Mathf.Abs(movedResidual - restResidual) < .0001f,
                    $"{label}: contact solver bodies follow the moved transforms (residual {restResidual * 1000:F3} vs {movedResidual * 1000:F3} mm)");

                VerifyExpectedPathOnMovedAnatomy(s, label);
                Debug.Log($"SCALPAL_OPEN_DELIVERY_DETAIL {label} deliveredDepthMm={delivery.action.depthMm:F1} appendixCentreAboveWoundMm={-appendixWound.z * 1000:F1} "
                    + $"groupTravelMm={s.Group.OffsetMeters.magnitude * 1000:F1} tiltDeg={Quaternion.Angle(s.Group.Tilt, Quaternion.identity):F1} maxTissueStepMm={maxStep * 1000:F2} "
                    + $"stumpMm={s.exercise.Body.Get("appendix", "stumpLengthMm"):F2} milestones=deliver,divide_mesoappendix,ligate_base");
                return appendixWound;
            }
        }

        // Clamp/cut/tie the mesoappendix and crush/tie/cut the base with actual tools on the delivered anatomy.
        static void VerifyExpectedPathOnMovedAnatomy(SceneRig s, string label)
        {
            var milestones = s.exercise.SelectedCase.procedure.openBody.milestones;
            bool Achieved(string id) => milestones.Single(m => m.id == id).predicates.All(s.exercise.Body.Test);
            Require(Achieved("deliver_appendix"), label + ": deliver milestone reached");
            Commit(s, s.Tool("hemostat", 0), "mesoappendix", 5, "clamp");
            Commit(s, s.Tool("hemostat", 1), "mesoappendix", 15, "clamp");
            Cut(s, s.Tool("metzenbaum_scissors"), "mesoappendix", 10);
            Commit(s, s.Tool("suture_tie"), "mesoappendix", 5, "tie");
            Commit(s, s.Tool("suture_tie"), "mesoappendix", 15, "tie");
            Require(Achieved("divide_mesoappendix") && s.exercise.Body.Get("", "activeBleeds") == 0, label + ": mesoappendix divided between measured clamps and ties on the moved anatomy");
            Require(s.input.Choose("appendix", "true_base"), label + ": true base decision accepted");
            Commit(s, s.Tool("right_angle_clamp"), "appendix", 3, "clamp");
            Commit(s, s.Tool("suture_tie"), "appendix", 3, "tie");
            Commit(s, s.Tool("right_angle_clamp"), "appendix", 8, "clamp");
            Cut(s, s.Tool("metzenbaum_scissors"), "appendix", 4);
            double stump = s.exercise.Body.Get("appendix", "stumpLengthMm");
            Require(Achieved("ligate_base") && Math.Abs(stump - 4) < .05, $"{label}: base ligated with a {stump:F2} mm stump measured on the moved anatomy");
            Require(!s.records.Any(r => r.outcomes.Contains("critical_injury") || r.outcomes.Contains("hollow_leak") || r.outcomes.Contains("cut_unsecured")),
                label + ": expected path on moved anatomy creates no injury, leak or unsecured bleed");
        }
        static void Commit(SceneRig s, InstrumentBehaviour tool, string tissueId, float alongMm, string verb)
        {
            Require(FindSite(s, tool, tissueId, alongMm, out var site, out _), $"contact site on moved {tissueId} at {alongMm} mm");
            int count = s.records.Count;
            s.Hold(tool); s.Place(tool, site); s.Step();
            var record = s.records.Skip(count).LastOrDefault(r => r.action.verb == verb);
            Require(record != null && record.action.tissueId == tissueId && Mathf.Abs(record.action.distanceMm - alongMm) < .05f && record.action.choice != "longitudinal_unmeasured",
                $"{tool.instrumentId} {verb} measured on moved {tissueId} at {alongMm} mm (got {record?.action.tissueId} {record?.action.distanceMm:F2})");
            s.Park(tool);
        }
        static void Cut(SceneRig s, InstrumentBehaviour tool, string tissueId, float alongMm)
        {
            Require(FindSite(s, tool, tissueId, alongMm, out var site, out var direction), $"stroke site on moved {tissueId} at {alongMm} mm");
            int count = s.records.Count;
            s.Hold(tool); s.Place(tool, site); s.Step();
            for (int i = 1; i <= 3 && !s.records.Skip(count).Any(r => r.action.verb == "cut"); i++) { s.Place(tool, site + direction * (.0005f * i)); s.Step(); }
            var record = s.records.Skip(count).FirstOrDefault(r => r.action.verb == "cut");
            Require(record != null && record.action.tissueId == tissueId && Mathf.Abs(record.action.distanceMm - alongMm) < .05f && record.action.lengthMm >= 1,
                $"{tool.instrumentId} stroke measured on moved {tissueId} at {alongMm} mm (got {record?.action.tissueId} {record?.action.distanceMm:F2})");
            s.Park(tool);
        }
        // A point on the tissue's longitudinal station whose nearest contact is that tissue and not the wall.
        static bool FindSite(SceneRig s, InstrumentBehaviour tool, string tissueId, float alongMm, out Vector3 site, out Vector3 direction)
        {
            site = direction = default;
            var target = s.Target(tissueId);
            if (!target.HasBase) return false;
            Vector3 axis = target.LongitudinalWorld, origin = target.basePoint.position + axis * (alongMm * .001f);
            Vector3 u = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < .9f ? Vector3.up : Vector3.right).normalized, v = Vector3.Cross(axis, u);
            float radius = Mathf.Min(tool.contactRadius, .006f);
            bool Valid(Vector3 point)
            {
                if (!target.TryContact(point, radius, out var hit)) return false;
                float own = (point - hit).sqrMagnitude;
                foreach (var other in s.input.Targets)
                    if (other != target && other.TryContact(point, radius, out var otherHit) && (point - otherHit).sqrMagnitude <= own) return false;
                Vector3 local = s.wound.InverseTransformPoint(point);
                return !(Mathf.Abs(local.x) <= .065f && Mathf.Abs(local.y) <= .04f && local.z > -.006f);
            }
            for (int ring = 0; ring <= 14; ring++)
                for (int angle = 0; angle < 360; angle += 20)
                {
                    Vector3 radial = (u * Mathf.Cos(angle * Mathf.Deg2Rad) + v * Mathf.Sin(angle * Mathf.Deg2Rad));
                    Vector3 point = origin + radial * (ring * .001f), tangent = Vector3.Cross(axis, radial).normalized;
                    if (!Valid(point)) continue;
                    if (!Enumerable.Range(1, 3).All(i => Valid(point + tangent * (.0005f * i)))) continue;
                    if (!target.DistanceFromBase(point, out float measured) || Mathf.Abs(measured - alongMm) > .01f) continue;
                    site = point; direction = tangent; return true;
                }
            return false;
        }

        static void VerifyRetryRestoresRest(SceneRig s)
        {
            Require(!s.AtAuthoredRest(), "delivered group is displaced before retry");
            s.NewAttempt();
            Require(s.AtAuthoredRest() && s.Group.AtRest && !s.Group.Held, "retry restores exact authored local poses");
        }

        static void VerifySpringBack(SceneRig s)
        {
            s.NewAttempt();
            var tool = Grasp(s);
            Vector3 start = s.wound.InverseTransformPoint(tool.actionPoint.position);
            Pull(s, tool, start, start + new Vector3(0, 0, -.06f));
            Require(s.Group.OffsetMeters.magnitude > .05f && !s.Group.Delivered(s.wound) && s.exercise.Body.Get("appendix", "delivered") == 0,
                "partial pull lifts the group but leaves the appendix below the wound plane");
            tool.SetActivation(0);
            float bound = s.Bound(s.Group.Definition.returnSpeedMps), worst = 0;
            var last = s.MeshCenters(); int frames = 0;
            while (!s.Group.AtRest && frames++ < 200)
            {
                s.Step(); var now = s.MeshCenters();
                for (int p = 0; p < now.Length; p++) worst = Mathf.Max(worst, Vector3.Distance(now[p], last[p]));
                last = now;
            }
            Require(s.Group.AtRest && s.AtAuthoredRest(), $"released undelivered group eases back to exact rest ({frames} frames)");
            Require(worst <= bound && frames > 10, $"spring-back is gradual ({worst * 1000:F2} mm/frame)");
            Debug.Log($"SCALPAL_OPEN_DELIVERY_SPRINGBACK frames={frames} maxTissueStepMm={worst * 1000:F2} exactRest=true");
            s.Park(tool);
        }

        static void VerifyFreeze(SceneRig s)
        {
            s.NewAttempt();
            var tool = Grasp(s);
            Vector3 start = s.wound.InverseTransformPoint(tool.actionPoint.position);
            Pull(s, tool, start, start + new Vector3(0, 0, -.04f));
            var frozen = s.WorldPositions();
            s.gate = false;
            for (int i = 0; i < 10; i++) { s.Place(tool, tool.actionPoint.position - s.wound.forward * .001f); s.Step(); }
            Require(s.WorldPositions().SequenceEqual(frozen), "invalid practice/registration freezes the group in place");
            s.gate = true;
            float bound = s.Bound(s.Group.Definition.returnSpeedMps), follow = s.Bound(s.Group.Definition.maxSpeedMps);
            float Moved(Vector3[] before) => s.MeshCenters().Zip(before, Vector3.Distance).Max();
            var resumed = s.MeshCenters(); s.Step();
            Require(Moved(resumed) <= follow, "resuming after a freeze does not jump the group");
            // Re-grasp, then lose controller tracking and teleport the tool: release without a jump.
            s.Place(tool, s.AppendixGraspSite()); s.Step(2);
            Require(s.Group.Held, "group re-grasped after freeze");
            var beforeLoss = s.MeshCenters();
            tool.SetTrackingValid(false); s.Place(tool, tool.actionPoint.position - s.wound.forward * .08f); s.Step();
            Require(!s.Group.Held && Moved(beforeLoss) <= bound, "tracking loss releases the group without following the lost pose");
            tool.SetTrackingValid(true); tool.SetActivation(1); s.Place(tool, s.AppendixGraspSite()); s.Step(2);
            Require(s.Group.Held, "group re-grasped after tracking returns");
            var beforeJump = s.MeshCenters();
            s.Place(tool, tool.actionPoint.position - s.wound.forward * .08f); s.Step();
            Require(!s.Group.Held && Moved(beforeJump) <= bound, "a tracked pose jump releases instead of yanking the group");
            s.Park(tool);
        }

        static void VerifyRoughHandling(SceneRig s)
        {
            s.NewAttempt();
            var tool = Grasp(s);
            Vector3 start = s.wound.InverseTransformPoint(tool.actionPoint.position);
            float worst = 0; var last = s.MeshCenters();
            Pull(s, tool, start, start + new Vector3(0, 0, -.06f), .006f, _ =>
            {
                var now = s.MeshCenters();
                for (int p = 0; p < now.Length; p++) worst = Mathf.Max(worst, Vector3.Distance(now[p], last[p]));
                last = now;
            });
            Require(s.records.Any(r => r.action.tissueId == "appendix" && r.outcomes.Contains("rough_handling")), "a fast yank on the held appendix is rough handling");
            Require(worst <= s.Bound(s.Group.Definition.maxSpeedMps), $"the yanked group stays speed limited ({worst * 1000:F2} mm/frame)");
            s.Park(tool);
        }
    }
}
