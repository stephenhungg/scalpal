using System;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Exercises.Data;
using Scalpal.Instruments;
using Scalpal.Quest;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // Real NativeSession skin collider and accepted tracked-pose injuries/control, synthetic display gates
    // and collision landing points. No physical headset, clinical healing or fluid validation claim.
    public static class SurgicalWoundPersistenceValidation
    {
        static int checks;
        static void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Surgical wound persistence: " + message);
        }
        [MenuItem("Scalpal/Surgery/Validate Persistent Wounds And Stains")]
        public static void Run()
        {
            checks = 0;
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new InvalidOperationException("Save or discard the edited scene before wound persistence validation.");
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                var r = new Rig(session, adapter);
                r.Stroke(.04f, .08f, .3f, "chest");
                Require(r.input.IsRegionInjured("chest") && r.cuts.Count >= 8, "real chest stroke creates an accepted injury and visible incision");
                r.Stroke(.08f, .1f, 0, "");
                Require(r.Segments().Any(s => s.region == ""), "real abdominal stroke includes an unclassified cosmetic cut");
                // An intact skin point inside the old 160 x100 mm exclusion rectangle but beyond
                // the actual 120 mm wall is an off-field puncture, not an invisible wound.
                Vector3 near = r.torso.InverseTransformPoint(r.wound.TransformPoint(new Vector3(.07f,0,0)));
                Vector3 onSkin = r.wound.InverseTransformPoint(r.SkinAt(near.x,near.z));
                Require(Mathf.Abs(onSkin.x)<.08f&&Mathf.Abs(onSkin.y)<.05f&&onSkin.x>.06f,
                    "actual adjacent skin is in the former excluded rectangle and outside wall contact");
                int beforeAdjacent=r.cuts.Count;
                r.Hold(r.scalpel);r.Tip(r.scalpel,near.x,near.z,.003f);r.Step(2);r.Lift(r.scalpel);
                Require(r.cuts.Count>beforeAdjacent,"a real adjacent puncture records a persistent visible wound instead of vanishing inside the old rectangle");
                var original = r.Segments();
                Require(r.blood.AddSplat(r.SkinAt(.04f, .3f), r.torso.up), "synthetic drop landing creates a stored skin stain");
                r.blood.Simulate(.02f);
                var stain = r.blood.SplatMesh.vertices;
                for (int i = 0; i < 600; i++) { r.cuts.Simulate(.1f); r.blood.Simulate(.1f); }
                Require(SameCuts(original, r.Segments()), "after 60 seconds cuts retain location, depth and opening (past former 45-second timeout)");
                Require(r.Segments().Where(s => s.region == "").All(s => s.wet && s.trickle > 0), "elapsed time cannot invent control of an unclassified cut");
                Require(r.blood.SplatCount == 1 && stain.SequenceEqual(r.blood.SplatMesh.vertices), "stain has no expiry or autonomous mesh shrink");

                r.Hold(r.hemostat);
                r.Tip(r.hemostat, .06f, .3f, r.Depth(.06f, .3f, "chest"));
                r.Step(3);
                r.Lift(r.hemostat);
                Require(!r.input.IsRegionInjured("chest"), "actual hemostat contact accepts regional control");
                long controlled = r.blood.EmittedFor("chest");
                for (int i = 0; i < 50; i++) { r.cuts.Simulate(.02f); r.blood.Simulate(.02f); }
                Require(r.blood.EmittedFor("chest") == controlled && r.blood.Flows.All(f => f.id != "chest"), "accepted control stops new chest flow");
                Require(SameCuts(original, r.Segments()) && r.Segments().Where(s => s.region == "chest").All(s => !s.wet), "control dries rather than heals the stored chest wound");
                Require(PatientIncisions.ShaderEnds.Take(r.cuts.Count).Where((v, i) => r.cuts[i].region == "chest").All(v => v.w < 0), "controlled wounds still feed dry stain lengths to the skin shader");

                var attemptBeforeReset=r.exercise.Body;
                session.workbench.ResetTools();r.cuts.Simulate(0);r.blood.Simulate(0);
                Require(ReferenceEquals(attemptBeforeReset,r.exercise.Body)&&SameCuts(original,r.Segments())&&r.blood.SplatCount==1
                    &&stain.SequenceEqual(r.blood.SplatMesh.vertices),"actual A/equipment-reset route preserves the attempt, wounds and blood stain");
                int cuts = r.cuts.Count;
                long emitted = r.blood.Emitted;
                r.BindPresentation();
                Require(r.cuts.Count == cuts && r.blood.SplatCount == 1 && r.blood.Emitted == emitted, "same Body reinitialization retains wound, stain and emission history");

                r.blood.Drops.Emit(new ParticleSystem.EmitParams { position = r.torso.position, startLifetime = 5, startSize = .004f }, 1);
                int particles = r.blood.ParticleCount;
                var starts = PatientIncisions.ShaderStarts.Take(cuts).ToArray();
                var ends = PatientIncisions.ShaderEnds.Take(cuts).ToArray();
                r.display = false; r.cuts.Simulate(0); r.blood.Simulate(0);
                Require(PatientIncisions.ShaderCount == 0 && r.blood.VisibleParticleCount == 0 && !r.StainRenderer.enabled,
                    "registration/privacy display loss hides cuts, drops and stains");
                Require(r.cuts.Count == cuts && r.blood.SplatCount == 1 && r.blood.ParticleCount == particles && r.blood.Drops.isPaused,
                    "display loss pauses rather than clears stored wounds, stains and live drops");
                r.display = true; r.cuts.Simulate(0); r.blood.Simulate(0);
                Require(PatientIncisions.ShaderCount == cuts && r.StainRenderer.enabled && r.blood.ParticleCount == particles,
                    "reacquisition restores retained presentation");
                Require(starts.SequenceEqual(PatientIncisions.ShaderStarts.Take(cuts)) && ends.SequenceEqual(PatientIncisions.ShaderEnds.Take(cuts))
                    && stain.SequenceEqual(r.blood.SplatMesh.vertices), "restored wound/stain geometry is byte-equivalent to pre-loss geometry");

                r.gate = false; r.input.Simulate(0); r.cuts.Simulate(.02f); r.blood.Simulate(.02f);
                Require(!r.input.Ready && PatientIncisions.ShaderCount == cuts && r.StainRenderer.enabled && r.blood.Emitted == emitted,
                    "a paused inspection/input gate retains registered wounds and stains but emits no new flow");
                r.gate = true;
                var retained = r.cuts[0];
                for (int i = 0; i < 12; i++) r.Stroke(-.08f, -.02f, .25f + i * .005f, "chest");
                Require(r.cuts.Count == PatientIncisions.Capacity && SameCut(retained, r.cuts[0]), "incision budget saturation cannot overwrite the first wound with fresh skin");
                for (int i = 0; i < SurgeryBlood.MaxSplats + 20; i++) r.blood.AddSplat(r.torso.position + new Vector3(i * .05f, -1, 0), Vector3.up);
                r.blood.Simulate(0);
                Require(r.blood.SplatCount == SurgeryBlood.MaxSplats && stain.Take(11).SequenceEqual(r.blood.SplatMesh.vertices.Take(11)),
                    "stain budget saturation cannot overwrite the first stain");
                Require(r.blood.AddSplat(r.SkinAt(.04f, .3f), r.torso.up), "a retained stain can still grow at a saturated budget");

                var selected = r.exercise.SelectedCase;
                Require(r.exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out var reason), "retry selects a new Body: " + reason);
                session.anatomy.SetRegistrationValid(true);
                r.input.Initialize(r.exercise, session.workbench.tools, r.torso, r.wound, () => r.gate);
                r.BindPresentation();
                Require(r.cuts.Count == 0 && PatientIncisions.ShaderCount == 0 && r.blood.SplatCount == 0 && r.blood.ParticleCount == 0 && r.blood.Emitted == 0,
                    "a genuinely new attempt clears cuts, stains, drops and emission counters");
                Debug.Log($"SCALPAL_SURGICAL_WOUND_PERSISTENCE_OK: {checks} checks; real scene and skin strokes/control, synthetic visibility gates/drop landings; no headset test");
            }
            finally { if (tissue) tissue.Dispose(); OpenSurgeryBuild.RestoreScenes(previous); }
        }
        static bool SameCut(PatientIncisions.Segment a, PatientIncisions.Segment b) => a.start == b.start && a.end == b.end && a.normal == b.normal && a.depth == b.depth && a.opening == b.opening && a.stroke == b.stroke && a.region == b.region;
        static bool SameCuts(PatientIncisions.Segment[] a, PatientIncisions.Segment[] b) => a.Length == b.Length && a.Where((s, i) => !SameCut(s, b[i])).Count() == 0;

        sealed class Rig
        {
            public readonly NativeCaseSession session;
            public readonly AnatomyExerciseBinding exercise;
            public readonly OpenBodyInteraction input;
            public readonly PatientIncisions cuts;
            public readonly SurgeryBlood blood;
            public readonly Transform torso, wound;
            public readonly Collider skin;
            public readonly InstrumentBehaviour scalpel, hemostat;
            public bool gate = true, display = true;
            public MeshRenderer StainRenderer => blood.transform.Find("BloodSplats").GetComponent<MeshRenderer>();
            public Rig(NativeCaseSession session, OpenSurgerySession adapter)
            {
                this.session = session; exercise = session.exercise; input = adapter.Interaction;
                torso = session.patientFrame; wound = adapter.Wound.transform;
                session.presentation.passthrough = false; session.presentation.Apply();
                skin = session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true).Single(c => c.name == "PatientCollision");
                cuts = session.GetComponentInChildren<PatientIncisions>(true); blood = session.GetComponentInChildren<SurgeryBlood>(true);
                scalpel = session.workbench.tools.First(t => t && t.instrumentId == "scalpel");
                hemostat = session.workbench.tools.First(t => t && t.instrumentId == "hemostat");
                foreach (var tool in session.workbench.tools) if (tool) tool.SetHeld(false);
                session.anatomy.SetRegistrationValid(true);
                input.Initialize(exercise, session.workbench.tools, torso, wound, () => gate);
                BindPresentation(); cuts.Clear(); blood.Clear();
            }
            public void BindPresentation()
            {
                cuts.Initialize(input, torso, wound, skin, () => !session.presentation.passthrough);
                blood.Initialize(input, cuts, exercise, torso, wound, () => !session.presentation.passthrough, null);
                cuts.PresentationVisible = () => display; blood.PresentationVisible = () => display;
            }
            public PatientIncisions.Segment[] Segments() => Enumerable.Range(0, cuts.Count).Select(i => cuts[i]).ToArray();
            public void Step(int n = 1) { for (int i = 0; i < n; i++) { input.Simulate(.02f); cuts.Simulate(.02f); blood.Simulate(.02f); Physics.SyncTransforms(); } }
            public void Hold(InstrumentBehaviour t) { t.SetHeld(true); t.SetTrackingValid(true); t.SetActivation(1); }
            public void Lift(InstrumentBehaviour t) { t.SetActivation(0); Step(); t.SetHeld(false); t.transform.position += Vector3.up * .4f; Physics.SyncTransforms(); Step(); }
            public Vector3 SkinAt(float x, float z)
            {
                Require(skin.Raycast(new Ray(torso.TransformPoint(new Vector3(x, .4f, z)), -torso.up), out var hit, 1), "actual mannequin skin at selected point");
                return hit.point;
            }
            public float Depth(float x, float z, string region)
            {
                var p = SkinAt(x, z);
                for (float d = .003f; d <= .06f; d += .001f) if (OpenBodyInteraction.BodyRegion(torso.InverseTransformPoint(p - torso.up * d)) == region) return d;
                throw new InvalidOperationException("Skin point does not reach selected region: " + region);
            }
            public void Tip(InstrumentBehaviour tool, float x, float z, float depth)
            {
                Vector3 direction = (-torso.up + torso.right * .577f).normalized;
                tool.transform.rotation = Quaternion.FromToRotation(tool.actionPoint.position - tool.gripAnchor.position, direction) * tool.transform.rotation;
                tool.transform.position += SkinAt(x, z) - torso.up * depth - tool.actionPoint.position;
                Physics.SyncTransforms();
            }
            public void Stroke(float fromX, float toX, float z, string region)
            {
                int n = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(toX - fromX) / .004f));
                float depth = Enumerable.Range(0, n + 1).Max(i => Depth(Mathf.Lerp(fromX, toX, i / (float)n), z, region));
                Hold(scalpel);
                for (int i = 0; i <= n; i++) { Tip(scalpel, Mathf.Lerp(fromX, toX, i / (float)n), z, depth); Step(); }
                Lift(scalpel);
            }
        }
    }
}
