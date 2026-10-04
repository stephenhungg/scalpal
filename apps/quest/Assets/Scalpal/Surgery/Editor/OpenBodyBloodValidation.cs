using System;
using System.Linq;
using System.Reflection;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Exercises.Engine;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Actual native scene and OpenSurgerySession composition with synthetic tracked poses: blade strokes on the
    // actual mannequin's skin collider leave incisions fed to Scalpal/PatientSkin, and visible blood follows the
    // body's bleeds, region injuries and pool. Not headset evidence: shading, drop arcs, splat placement from
    // particle collisions (Play Mode only) and Quest frame time are unverified here.
    public static class OpenBodyBloodValidation
    {
        const float Dt = .02f;
        static int checks;
        static void Require(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Open body blood validation: " + message);
        }
        [MenuItem("Scalpal/Surgery/Validate Incisions And Blood")]
        public static void Run()
        {
            checks = 0;
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new InvalidOperationException("Open body blood validation needs to open the native scene; save or discard scene changes first");
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                var rig = new Rig(session, adapter);
                SkinShader(rig);
                var chest = OffFieldChest(rig);
                InFieldIncision(rig);
                var artery = ArterialBleed(rig);
                var regions = RegionBleeds(rig);
                Caps(rig);
                ArDrawsNothing(rig);
                Reset(rig);
                Debug.Log($"SCALPAL_OPEN_BODY_BLOOD_OK: {checks} checks; chestSegments={chest.segments} chestDepthMm={chest.depth * 1000:F1} "
                    + $"arteryMlPerMin={artery.rate:F0} arteryDrops={artery.drops} expected={artery.expected:F0} poolMl={artery.pool:F2}->{artery.suctioned:F2} "
                    + $"neckDrops={regions.neck} chestDrops={regions.chest} armDrops={regions.arm} particles<={SurgeryBlood.MaxParticles} splats<={SurgeryBlood.MaxSplats} "
                    + $"incisions<={PatientIncisions.Capacity}; actual scene, synthetic tracked poses, no headset test");
            }
            finally
            {
                if (tissue) tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(previous);
            }
        }

        sealed class Rig
        {
            public readonly NativeCaseSession session;
            public readonly OpenSurgerySession adapter;
            public readonly AnatomyExerciseBinding exercise;
            public readonly NativeVolumeSimulation volume;
            public readonly PatientIncisions incisions;
            public readonly SurgeryBlood blood;
            public readonly OpenBodyBleeding bleeding;
            public readonly InstrumentBehaviour scalpel, hemostat;
            public readonly Collider skin;
            readonly Transform cutStart, cutEnd;
            public bool gate = true;
            public OpenBodyInteraction Input => adapter.Interaction;
            public Transform Torso => session.patientFrame;
            public Transform Wound => adapter.Wound.transform;
            public Rig(NativeCaseSession session, OpenSurgerySession adapter)
            {
                this.session = session; this.adapter = adapter; exercise = session.exercise;
                volume = session.GetComponent<NativeVolumeSimulation>(); bleeding = session.GetComponent<OpenBodyBleeding>();
                incisions = session.GetComponentInChildren<PatientIncisions>(true); blood = session.GetComponentInChildren<SurgeryBlood>(true);
                Require(incisions && blood && bleeding, "the session composes incisions, blood and the vessel pool for the open case");
                session.presentation.passthrough = false; session.presentation.Apply();
                skin = session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true).SingleOrDefault(c => c.name == "PatientCollision");
                Require(skin && skin.enabled, "VR mannequin has its enabled outward-wound skin collider");
                scalpel = session.workbench.tools.First(t => t && t.instrumentId == "scalpel");
                hemostat = session.workbench.tools.First(t => t && t.instrumentId == "hemostat");
                cutStart = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutStart");
                cutEnd = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutEnd");
                NewAttempt();
            }
            // The runtime retry path (a new body re-composes the attempt), then a validation-owned practice gate.
            public void NewAttempt()
            {
                var selected = exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out var reason), "retry selects a fresh attempt: " + reason);
                session.anatomy.SetRegistrationValid(true); gate = true;
                foreach (var tool in session.workbench.tools) if (tool) { tool.SetHeld(false); var latch = tool.GetComponent<SurgeryInstrumentLatch>(); if (latch) latch.Clear(); }
                typeof(OpenSurgerySession).GetMethod("ConfigureAttempt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, null);
                volume.Initialize(Wound, session.workbench, () => gate, true);
                Input.Initialize(exercise, session.workbench.tools, session.patientFrame, Wound, () => gate);
                Step();
            }
            // Runtime order (OpenSurgerySession.Update, then the wall's LateUpdate).
            public void Step(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    bleeding.Prime(); Input.Simulate(Dt); bleeding.Simulate(Dt); incisions.Simulate(Dt); blood.Simulate(Dt);
                    volume.Simulate(Dt); Physics.SyncTransforms();
                }
            }
            public void Hold(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
            public void Lift(InstrumentBehaviour tool) { tool.SetActivation(0); Step(); tool.SetHeld(false); tool.transform.position += Vector3.up * .4f; Physics.SyncTransforms(); Step(); }
            // Skin under a torso-frame point (+Y anterior), on the actual collider.
            public Vector3 SkinAt(float x, float z)
            {
                var from = Torso.TransformPoint(new Vector3(x, .4f, z));
                Require(skin.Raycast(new Ray(from, -Torso.up), out var hit, 1), $"mannequin skin under torso ({x:F2}, {z:F2})");
                return hit.point;
            }
            // Tool tip `depth` under the skin at (x, z), shaft tilted 30 degrees from vertical toward +X.
            public void Tip(InstrumentBehaviour tool, float x, float z, float depth) => TipAt(tool, SkinAt(x, z), depth);
            public void TipAt(InstrumentBehaviour tool, Vector3 skinPoint, float depth)
            {
                Vector3 inward = (-Torso.up + Torso.right * .577f).normalized;
                Vector3 distal = tool.actionPoint.position - tool.gripAnchor.position;
                tool.transform.rotation = Quaternion.FromToRotation(distal, inward) * tool.transform.rotation;
                tool.transform.position += skinPoint - Torso.up * depth - tool.actionPoint.position;
                Physics.SyncTransforms();
            }
            // The shallowest tip depth (from 3 mm) at which the interaction's coarse body regions place the tip in `region`.
            public float RegionDepth(float x, float z, string region)
            {
                Vector3 skinPoint = SkinAt(x, z);
                for (float depth = .003f; depth <= .06f; depth += .001f)
                    if (OpenBodyInteraction.BodyRegion(Torso.InverseTransformPoint(skinPoint - Torso.up * depth)) == region) return depth;
                Vector3 local = Torso.InverseTransformPoint(skinPoint);
                Require(false, $"torso ({x:F2}, {z:F2}) reaches region {region} within 6 cm of the skin at {local.ToString("F3")} (6 cm: '{OpenBodyInteraction.BodyRegion(local - Vector3.up * .06f)}')"); return 0;
            }
            // One continuous blade stroke along torso +X on the skin, ended by releasing the trigger; every sample
            // is deep enough for the interaction to place the tip in `region`, plus `extra`. Returns the depth used.
            public float Stroke(float fromX, float toX, float z, string region, float extra = 0)
            {
                int steps = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(toX - fromX) / .004f));
                float depth = 0;
                for (int i = 0; i <= steps; i++) depth = Mathf.Max(depth, RegionDepth(Mathf.Lerp(fromX, toX, i / (float)steps), z, region));
                depth += extra;
                Hold(scalpel);
                for (int i = 0; i <= steps; i++) { Tip(scalpel, Mathf.Lerp(fromX, toX, i / (float)steps), z, depth); Step(); }
                Lift(scalpel);
                return depth;
            }
            // Blade edge along the wound's inward axis with its deep end at local (as OpenWallCouplingValidation).
            public void Blade(Vector3 local)
            {
                scalpel.transform.rotation = Wound.rotation;
                scalpel.transform.rotation = Quaternion.FromToRotation(cutEnd.position - cutStart.position, Wound.forward) * scalpel.transform.rotation;
                scalpel.transform.position += Wound.TransformPoint(local) - cutEnd.position;
                Physics.SyncTransforms();
            }
            public double Fact(string tissue, string fact) => exercise.Body.Get(tissue, fact);
            public PatientIncisions.Segment[] Segments() => Enumerable.Range(0, incisions.Count).Select(i => incisions[i]).ToArray();
        }

        static void SkinShader(Rig s)
        {
            var shader = Shader.Find("Scalpal/PatientSkin");
            Require(shader && !ShaderUtil.ShaderHasError(shader), "Scalpal/PatientSkin compiles with the incision ring");
            Require(s.session.presentation.virtualMannequin.sharedMaterials.All(m => m && m.shader == shader), "the VR mannequin is drawn with the skin shader that draws incisions");
        }

        // A blade stroke off the field on the chest records incision segments on the skin, fed to the skin shader.
        static (int segments, float depth) OffFieldChest(Rig s)
        {
            s.NewAttempt();
            Require(s.incisions.Count == 0 && PatientIncisions.ShaderCount == 0, "a fresh attempt has unbroken skin");
            float depth = s.Stroke(.04f, .08f, .3f, "chest");
            var segments = s.Segments();
            Require(s.Input.IsRegionInjured("chest"), "the chest stroke is the interaction's chest injury");
            Require(segments.Length >= 8 && segments.All(x => x.region == "chest"), "the 40 mm chest stroke records its segments on the chest: " + segments.Length);
            Vector3 first = s.Torso.InverseTransformPoint(s.SkinAt(.04f, .3f)), last = s.Torso.InverseTransformPoint(s.SkinAt(.08f, .3f));
            Require(segments.All(x => Mathf.Abs(x.start.z - .3f) < .01f && x.start.x > .03f && x.end.x < .09f) &&
                segments.Min(x => Vector3.Distance(x.start, first)) < .006f && segments.Min(x => Vector3.Distance(x.end, last)) < .006f,
                "segments lie on the skin along the stroke path, end to end");
            Require(segments.All(x => Mathf.Abs(x.depth - depth) < .0015f), $"segment depth is the blade's measured depth under the skin ({depth * 1000:F1} mm)");
            Require(PatientIncisions.ShaderCount == segments.Length, "the skin shader is fed every segment: " + PatientIncisions.ShaderCount);
            var starts = PatientIncisions.ShaderStarts; var ends = PatientIncisions.ShaderEnds;
            Require(starts.Length == PatientIncisions.Capacity && ends.Length == PatientIncisions.Capacity &&
                Enumerable.Range(0, segments.Length).All(i => (Vector3)starts[i] == segments[i].start && (Vector3)ends[i] == segments[i].end && starts[i].w == segments[i].opening && segments[i].opening >= .0003f),
                "shader arrays carry each segment's torso-frame ends and opening");
            // Deeper and longer strokes part wider.
            float shallow = segments.Max(x => x.opening);
            s.Stroke(.04f, .12f, .26f, "chest", .006f);
            var deep = s.Segments().Skip(segments.Length).ToArray();
            Require(deep.Length > segments.Length && deep.Max(x => x.opening) > shallow + .0004f, $"a deeper, longer stroke opens wider ({deep.Max(x => x.opening) * 1000:F2} vs {shallow * 1000:F2} mm)");
            // Ooze: wet while the region bleeds, trickling downhill over time.
            s.Step(100);
            Require(s.Segments().All(x => x.wet && x.trickle > .005f) && PatientIncisions.ShaderEnds.Take(s.incisions.Count).All(v => v.w > .005f),
                "fresh chest cuts ooze and the trickle grows while the chest bleeds");
            return (segments.Length, depth);
        }

        // Inside the surgical field the existing open-wound path keeps the incision: the wall's skin opens and scores,
        // the stroke lies inside the skin shader's cut-away window, and no off-field segment is added.
        static void InFieldIncision(Rig s)
        {
            s.NewAttempt();
            s.Hold(s.scalpel);
            for (int i = 0; i <= 12; i++) { s.Blade(new Vector3(Mathf.Lerp(-.03f, .03f, i / 12f), 0, .0015f)); s.Step(); }
            s.scalpel.SetActivation(0); s.Step(); s.scalpel.SetActivation(1);
            s.Lift(s.scalpel);
            Require(s.Fact("skin", "opened") == 1 && s.volume.Wall.Volume.CutFacesForMaterial("skin") > 0, "an in-field stroke still opens the wall's skin and scores it");
            Require(s.incisions.Count == 0, "an in-field stroke leaves no off-field incision under the wound window: " + s.incisions.Count);
            s.adapter.Wound.SetRegistrationValid(true);
            Require(Shader.GetGlobalFloat("_ScalpalWoundWindow") == 1, "the skin over the live wound is cut away so the wall's incision shows");
        }

        // The appendicular artery cut before clamping: drops at its measured rate, a pool in the cavity, control and suction.
        static (float rate, long drops, float expected, double pool, double suctioned) ArterialBleed(Rig s)
        {
            s.NewAttempt();
            foreach (var step in s.exercise.SelectedCase.procedure.steps.Take(5))
                foreach (var e in CaseRunner.PerfectEvents(step)) Require(s.exercise.Submit(e, out _, out var reason), "exposure goes through the real score binding: " + reason);
            s.Step(5);
            Require(s.blood.Flows.Count == 0 && s.blood.Emitted == 0, "nothing bleeds before the artery is cut");
            var cut = s.Input.CreateMeasurement("scalpel", "cut", "appendicular_artery", s.Wound.TransformPoint(new Vector3(0, 0, .02f)), "validation-blade");
            cut.lengthMm = 4; cut.distanceMm = 10;
            Require(s.Input.SubmitMeasured(cut) && s.Fact("appendicular_artery", "bleeding") == 1, "an unclamped artery cut bleeds in the body");
            s.Step(75); // past the first 1 Hz measured-flow snapshot
            var flow = s.blood.Flows.SingleOrDefault(f => f.id == "appendicular_artery");
            float rate = (float)(s.Fact("appendicular_artery", "measuredFlowMlPerSecond") * 60);
            Require(flow.id != null && Mathf.Abs(flow.rateMlPerMin - rate) < .01f && rate > 0 && flow.arterial,
                $"the artery flows at the body's measured rate ({flow.rateMlPerMin:F1} vs {rate:F1} ml/min), as an arterial pulse");
            long before = s.blood.EmittedFor("appendicular_artery");
            s.Step(250); // 5 s, six beats at the authored 72 bpm
            long drops = s.blood.EmittedFor("appendicular_artery") - before;
            float expected = Mathf.Min(rate / 60 / SurgeryBlood.DropMilliliters, SurgeryBlood.MaxDropsPerSecond) * 5;
            Require(Mathf.Abs(drops - expected) <= expected * .2f, $"drops follow rateMlPerMin: {drops} in 5 s, expected {expected:F0}");
            Require(s.blood.ParticleCount <= SurgeryBlood.MaxParticles, "live drops stay within the particle cap");
            // Pool: the cavity pool follows the reducer's poolMl.
            double pool = s.Fact("", "poolMl");
            Require(pool > 0 && Math.Abs(s.bleeding.ShownPoolMl - pool) <= Math.Max(.05, pool * .25) && s.bleeding.PoolLevelMeters > 0,
                $"the visible pool follows poolMl ({s.bleeding.ShownPoolMl:F2} shown, {pool:F2} ml)");
            var clamp = s.Input.CreateMeasurement("hemostat", "clamp", "appendicular_artery", s.Wound.TransformPoint(new Vector3(0, 0, .02f)), "validation-clamp");
            clamp.distanceMm = 5;
            Require(s.Input.SubmitMeasured(clamp) && s.Fact("appendicular_artery", "bleeding") == 0, "a clamp proximal to the injury controls it in the body");
            s.Step(2);
            long controlled = s.blood.EmittedFor("appendicular_artery");
            s.Step(100);
            Require(s.blood.EmittedFor("appendicular_artery") == controlled && s.blood.Flows.Count == 0, "the controlled artery emits no more drops");
            s.Step(75);
            pool = s.Fact("", "poolMl"); float level = s.bleeding.PoolLevelMeters; double shown = s.bleeding.ShownPoolMl;
            Require(Math.Abs(shown - pool) <= Math.Max(.01, pool * .02), $"once flow stops the pool settles on poolMl ({shown:F2} vs {pool:F2})");
            var suction = s.Input.CreateMeasurement("suction_irrigator", "suction", "appendicular_artery", s.Wound.TransformPoint(new Vector3(0, 0, .025f)), "validation-suction");
            suction.durationMs = Math.Min(1000, pool * 50); suction.choice = "pool_suction";
            Require(s.Input.SubmitMeasured(suction), "pool suction is accepted at the visible pool");
            s.Step(150);
            double suctioned = s.Fact("", "poolMl");
            Require(suctioned < pool - .01 && s.bleeding.ShownPoolMl < shown && s.bleeding.PoolLevelMeters <= level &&
                Math.Abs(s.bleeding.ShownPoolMl - suctioned) <= Math.Max(.01, suctioned * .05),
                $"suction lowers poolMl and the visible pool with it ({pool:F2} -> {suctioned:F2} ml, shown {s.bleeding.ShownPoolMl:F2})");
            return (rate, drops, expected, pool, suctioned);
        }

        // Region injuries outside the field: neck and chest spurt at the coach's rates, an arm wells up; control stops
        // a region's flow and dries its ooze.
        static (long neck, long chest, long arm) RegionBleeds(Rig s)
        {
            s.NewAttempt();
            s.Stroke(-.02f, .02f, .45f, "neck");
            s.Stroke(.04f, .08f, .3f, "chest");
            s.Stroke(-.21f, -.21f, 0, "right_arm");
            Require(s.Input.IsRegionInjured("neck") && s.Input.IsRegionInjured("chest") && s.Input.IsRegionInjured("right_arm"), "three regions are injured");
            Require(s.blood.Flows.Count(f => f.arterial) == 2 && s.blood.Flows.Single(f => f.id == "neck").rateMlPerMin == 300 &&
                s.blood.Flows.Single(f => f.id == "chest").rateMlPerMin == 150 && s.blood.Flows.Single(f => f.id == "right_arm") is var arm && !arm.arterial && arm.rateMlPerMin == 20,
                "neck and chest are arterial at the authored region rates, the arm is a venous well-up");
            long neck = s.blood.EmittedFor("neck"), chest = s.blood.EmittedFor("chest"), armDrops = s.blood.EmittedFor("right_arm");
            s.Step(250);
            neck = s.blood.EmittedFor("neck") - neck; chest = s.blood.EmittedFor("chest") - chest; armDrops = s.blood.EmittedFor("right_arm") - armDrops;
            Require(Mathf.Abs(neck / (float)chest - 2) < .4f && chest > 4 * armDrops && armDrops > 0, $"emission scales with rateMlPerMin: neck {neck}, chest {chest}, arm {armDrops}");
            Require(s.blood.ParticleCount <= SurgeryBlood.MaxParticles, "region drops stay within the particle cap");
            // A hemostat in the neck controls it: its flow stops and its cut dries, while the chest still bleeds.
            s.Hold(s.hemostat); s.Tip(s.hemostat, 0, .45f, s.RegionDepth(0, .45f, "neck")); s.Step(3);
            Require(!s.Input.IsRegionInjured("neck") && s.Input.IsRegionInjured("chest"), "the hemostat controls the neck only");
            s.Lift(s.hemostat);
            long after = s.blood.EmittedFor("neck");
            s.Step(50);
            Require(s.blood.EmittedFor("neck") == after && s.blood.Flows.All(f => f.id != "neck") && s.blood.EmittedFor("chest") > 0, "the controlled neck emits nothing more");
            var segments = s.Segments(); var ends = PatientIncisions.ShaderEnds;
            Require(Enumerable.Range(0, segments.Length).All(i => segments[i].region != "neck" || (!segments[i].wet && ends[i].w < 0)) &&
                Enumerable.Range(0, segments.Length).Any(i => segments[i].region == "chest" && segments[i].wet && ends[i].w > 0),
                "the controlled neck cut dries in the shader while the chest still oozes");
            return (neck, chest, armDrops);
        }

        static void Caps(Rig s)
        {
            var main = s.blood.Drops.main; var collision = s.blood.Drops.collision;
            Require(main.maxParticles == SurgeryBlood.MaxParticles && main.simulationSpace == ParticleSystemSimulationSpace.World && !s.blood.Drops.emission.enabled,
                "one pooled world-space system, emitted only from sim sources, capped at " + SurgeryBlood.MaxParticles);
            Require(collision.enabled && collision.type == ParticleSystemCollisionType.World && collision.bounce.constant <= .1f && collision.dampen.constant >= .8f &&
                collision.lifetimeLoss.constant == 1 && collision.sendCollisionMessages && (collision.collidesWith & (1 << s.skin.gameObject.layer)) != 0,
                "drops collide with the patient, table and floor colliders, barely bounce and die where they land");
            for (int i = 0; i < 2000; i++)
                s.blood.Drops.Emit(new ParticleSystem.EmitParams { position = s.Torso.position, velocity = Vector3.up, startLifetime = 5, startSize = .004f }, 1);
            Require(s.blood.ParticleCount == SurgeryBlood.MaxParticles, "a flood of drops fills the system to its cap and no further: " + s.blood.ParticleCount);
            for (int i = 0; i < 300; i++) s.blood.AddSplat(s.Torso.position + new Vector3(i * .05f, -1, 0), Vector3.up);
            s.Step();
            Require(s.blood.SplatCount == SurgeryBlood.MaxSplats && s.blood.SplatMesh.vertexCount == SurgeryBlood.MaxSplats * 11, "splats recycle a ring of " + SurgeryBlood.MaxSplats);
            int splats = s.blood.SplatCount;
            int vertices = s.blood.SplatMesh.vertexCount;
            Require(s.blood.AddSplat(s.Torso.position + new Vector3(299 * .05f, -1, 0), Vector3.up) && s.blood.SplatCount == splats, "a drop landing on an existing splat grows it instead");
            s.Step();
            Require(s.blood.SplatMesh.vertexCount == vertices, "a grown splat reuses its vertices");
            for (int i = 0; i < 6; i++) s.Stroke(-.12f, -.06f, -.35f - i * .04f, "right_leg");
            Require(s.incisions.Count == PatientIncisions.Capacity && PatientIncisions.ShaderCount == PatientIncisions.Capacity, "incisions recycle a ring of " + PatientIncisions.Capacity);
        }

        // AR: the volunteer is real, so nothing is drawn on the body: no incisions, drops or splats, even while regions bleed.
        static void ArDrawsNothing(Rig s)
        {
            Require(s.incisions.Count > 0 && s.blood.Flows.Count > 0, "VR state before switching shows incisions and active flows");
            // Skin points along a fresh chest line, measured while the VR collider is still enabled.
            var chest = Enumerable.Range(0, 11).Select(i => s.SkinAt(-.08f + i * .004f, .34f)).ToArray();
            float depth = Enumerable.Range(0, 11).Max(i => s.RegionDepth(-.08f + i * .004f, .34f, "chest"));
            var presentation = s.session.presentation;
            presentation.passthrough = true; presentation.Apply();
            try
            {
                s.Step();
                Require(PatientIncisions.ShaderCount == 0, "AR feeds the skin shader no incisions");
                int count = s.incisions.Count; long emitted = s.blood.Emitted;
                Require(s.blood.ParticleCount == 0 && !s.blood.GetComponentInChildren<MeshRenderer>().enabled, "AR shows no drops or splats");
                s.Hold(s.scalpel);
                for (int i = 0; i <= 10; i++) { s.TipAt(s.scalpel, chest[i], depth); s.Step(); }
                s.Lift(s.scalpel); s.Step(50);
                Require(s.incisions.Count == count && s.blood.Emitted == emitted && s.blood.ParticleCount == 0 && PatientIncisions.ShaderCount == 0,
                    "AR blade strokes and region bleeds add no incisions or drops");
            }
            finally { presentation.passthrough = false; presentation.Apply(); }
            s.Step();
            Require(PatientIncisions.ShaderCount == s.incisions.Count, "back in VR the attempt's incisions show again");
        }

        static void Reset(Rig s)
        {
            Require(s.incisions.Count > 0 && s.blood.SplatCount > 0 && s.blood.Emitted > 0, "the attempt has incisions, splats and drops before retry");
            s.NewAttempt();
            Require(s.incisions.Count == 0 && PatientIncisions.ShaderCount == 0 && s.blood.SplatCount == 0 && s.blood.ParticleCount == 0 && s.blood.Emitted == 0
                && s.blood.Flows.Count == 0 && s.bleeding.ShownPoolMl == 0 && s.bleeding.PoolLevelMeters == 0, "retry clears incisions, drops, splats and the pool");
        }
    }
}
