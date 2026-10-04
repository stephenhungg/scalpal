using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Scalpal.Anatomy;
using Scalpal.Brand;
using Scalpal.Exercises.Data;
using Scalpal.Quest;
using Scalpal.Instruments;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scalpal.Surgery.Editor
{
    // The open case's first step as the learner meets it in VR: actual native scene, actual OpenSurgerySession
    // composition, the actual mannequin skin and the actual OpenBodyInteraction marker path, driven by synthetic
    // tracked marker poses. Why it matters: a learner who cannot see where to mark, or whose ink never appears,
    // cannot start the operation at all (the reported "box on the hip, and the marker doesn't do anything").
    // With a graphics device it also renders the learner's view and counts the violet ink actually on screen.
    // Not headset evidence.
    public static class MarkingGuideValidation
    {
        const float Dt = .02f;
        static int checks;
        static string renders = "";
        static void Require(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Marking guide validation: " + message);
        }
        public static void Run()
        {
            checks = 0; renders = "";
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                // The learner's virtual theatre: the visible mannequin, its physics skin and the virtual room.
                session.presentation.passthrough = false; session.presentation.Apply();
                var s = new Rig(session, adapter);
                s.NewAttempt();
                StepZero(s);
                int ink = MarkAlongGuide(s);
                AcceptedFades(s);
                Reveal(s);
                Closure(s);
                OffTarget(s);
                Debug.Log($"SCALPAL_MARKING_GUIDE_OK: {checks} checks; inkPixels={ink} renders={(renders == "" ? "skipped (no graphics device)" : renders)}; "
                    + "actual scene, synthetic tracked marker, no headset test");
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
            public readonly OpenBodyInteraction input;
            public readonly AnatomyExerciseBinding exercise;
            public readonly Transform wound, frame;
            public readonly NativeVolumeSimulation volume;
            public readonly MarkingGuide guide;
            public readonly SurgeryTriggerHint hint;
            public readonly InstrumentBehaviour marker, scalpel;
            public readonly MeshCollider skin;
            public readonly List<string> mistakes = new List<string>();
            public bool gate = true;
            public Rig(NativeCaseSession session, OpenSurgerySession adapter)
            {
                this.session = session; this.adapter = adapter; input = adapter.Interaction; exercise = session.exercise;
                wound = adapter.Wound.transform; frame = session.patientFrame; volume = session.GetComponent<NativeVolumeSimulation>();
                guide = adapter.Guide; hint = session.GetComponent<SurgeryTriggerHint>();
                Require(guide && hint, "the open session composes a marking guide and the hint line");
                marker = session.workbench.tools.Single(t => t && t.instrumentId == "skin_marker");
                scalpel = session.workbench.tools.First(t => t && t.instrumentId == "scalpel");
                skin = session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true).Single();
                exercise.MistakeMade += (_, mistake) => mistakes.Add(mistake.id);
                // The learner stands at the patient's right, above McBurney's point; labels face this head.
                session.workbench.headCamera.transform.position = wound.position + Vector3.up * .5f - frame.right * .4f - frame.forward * .1f;
                session.workbench.headCamera.transform.LookAt(wound.position);
            }
            public void NewAttempt()
            {
                var selected = exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out var reason), "fresh attempt: " + reason);
                session.anatomy.SetRegistrationValid(true); gate = true;
                foreach (var tool in session.workbench.tools) if (tool) tool.SetHeld(false);
                // Same per-attempt composition as OpenSurgerySession.ConfigureAttempt, with a validation-owned practice gate.
                volume.Initialize(wound, session.workbench, () => gate, true);
                input.Initialize(exercise, session.workbench.tools, frame, wound, () => gate);
                guide.Initialize(session, input, adapter.Wound, hint, null, null, () => gate);
                mistakes.Clear(); hint.Hide(); Park(marker); Park(scalpel);
                Step();
            }
            // Runtime order: session Update (wound registration, interaction, wound view), then LateUpdate (wall, guide).
            public void Step(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    adapter.Wound.SetRegistrationValid(gate); input.Simulate(Dt); adapter.Wound.Apply(exercise.Body);
                    volume.Simulate(Dt); guide.Refresh(Dt); Physics.SyncTransforms();
                }
            }
            public void Park(InstrumentBehaviour tool)
            {
                tool.SetHeld(false); tool.SetActivation(0);
                tool.transform.position += wound.TransformPoint(new Vector3(.25f, .25f, -.3f)) - tool.actionPoint.position; Physics.SyncTransforms();
            }
            // Wound-local (x, y) to the visible patient's skin, in world space.
            public bool SkinAt(Vector3 local, out Vector3 world)
            {
                local.z = 0; Vector3 at = wound.TransformPoint(local);
                bool backfaces = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;
                try { bool hit = skin.Raycast(new Ray(at - wound.forward * .25f, wound.forward), out var surface, .5f); world = surface.point; return hit; }
                finally { Physics.queriesHitBackfaces = backfaces; }
            }
            // Signed millimetres outside the visible skin along its outward normal (+ = above the skin).
            public float AboveSkinMm(Vector3 world)
            {
                Require(SkinAt(wound.InverseTransformPoint(world), out var surface), "skin exists under " + world);
                return Vector3.Dot(world - surface, -wound.forward) * 1000;
            }
            // A held marker whose tip rests 1 mm into the skin the learner sees, trigger held, from a to b.
            public void Stroke(Vector3 a, Vector3 b, Action<int> during = null)
            {
                marker.SetHeld(true); marker.SetTrackingValid(true); marker.SetActivation(1);
                int steps = Mathf.Max(2, Mathf.RoundToInt(Vector3.Distance(a, b) / .002f));
                for (int i = 0; i <= steps; i++)
                {
                    Require(SkinAt(Vector3.Lerp(a, b, i / (float)steps), out var surface), "stroke stays over the patient");
                    marker.transform.rotation = wound.rotation;
                    marker.transform.position += surface + wound.forward * .001f - marker.actionPoint.position; Physics.SyncTransforms();
                    Step(); during?.Invoke(i * 100 / steps);
                }
                marker.SetActivation(0); Step(); // lifting the trigger ends the stroke
            }
            public double Fact(string fact) => exercise.Body.Get("skin", fact);
            public bool Achieved(string id) => exercise.CompletedMilestones.Contains(id);
        }

        static string MilestoneId(Rig s) => s.exercise.SelectedCase.procedure.openBody.milestones.First(m => m.predicates.Any(p => p.tissueId == "skin" && p.fact == "marked")).id;

        // Step 0: what is on the patient before anything is drawn.
        static void StepZero(Rig s)
        {
            Require(s.exercise.Current?.id == MilestoneId(s) && MilestoneId(s) == "mark_incision", "the attempt opens on the marking step");
            // The "box on the hip" was the closed teaching wall seen through a 160 x 100 mm window cut in the
            // patient's skin: the layered wound coupon plus the flat wall slab, beside three laparoscopic port spheres.
            Require(Shader.GetGlobalFloat("_ScalpalWoundWindow") == 0, "intact skin: the patient skin has no window cut over the wound");
            Require(!s.adapter.Wound.transform.Find("MeasuredWoundSurfaces").gameObject.activeInHierarchy, "the closed layered wound coupon is not drawn on intact skin");
            Require(!s.volume.Wall.GetComponent<MeshRenderer>().enabled, "the flat teaching wall slab is not drawn on intact skin");
            Require(s.frame.GetComponentsInChildren<NativePortMarker>(false).Length == 0, "laparoscopic port spheres are hidden in the open case");
            Require(s.guide.Showing, "the marking guide shows in step 0");
            // Landmarks: on the visible skin at the authored right ASIS proxy and the umbilicus (torso origin).
            Vector3 asis = s.frame.TransformPoint(OpenSurgerySession.AuthoredRightAsis), navel = s.frame.position;
            foreach (var (dot, expected, label) in new[] { (s.guide.HipDot, asis, MarkingGuide.HipLabel), (s.guide.NavelDot, navel, MarkingGuide.NavelLabel) })
            {
                Require(dot && dot.gameObject.activeInHierarchy, label + " dot exists");
                Vector3 offset = Vector3.ProjectOnPlane(dot.position - expected, s.wound.forward);
                float above = s.AboveSkinMm(dot.position);
                Require(offset.magnitude < .001f && above > .2f && above < 2, $"{label} dot lies on the skin over its landmark (offset {offset.magnitude * 1000:F2} mm, {above:F2} mm above skin)");
                var text = dot.GetComponentInChildren<TMPro.TextMeshPro>(true);
                Require(text && text.text == label && text.font == ScalpalBrand.Active.Font(ScalpalTextRole.Label) && text.color.a > .5f && text.color.r > .8f && Mathf.Abs(text.color.r - text.color.b) < .05f,
                    label + " is labelled in the brand label font, subtle grey-white");
            }
            // Dashed guide: McBurney's point (a third from the right ASIS to the umbilicus), about 6 cm, along the scored line.
            var mesh = s.guide.GuideLine.GetComponent<MeshFilter>().sharedMesh;
            Vector3 mcBurney = s.wound.InverseTransformPoint(s.frame.TransformPoint(OpenSurgeryStroke.McBurney(OpenSurgerySession.AuthoredRightAsis, Vector3.zero)));
            Vector3 axis = (s.input.ReferenceEnd - s.input.ReferenceStart).normalized;
            var along = mesh.vertices.Select(v => Vector3.Dot(v - mcBurney, axis)).ToArray();
            var across = mesh.vertices.Select(v => Mathf.Abs(Vector3.Dot(v - mcBurney, Vector3.Cross(Vector3.forward, axis)))).ToArray();
            float length = (along.Max() - along.Min()) * 1000, centre = (along.Max() + along.Min()) * 500;
            Require(mesh.vertexCount >= 32 && length > 57 && length < 63 && Mathf.Abs(centre) < 1 && across.Max() < .001f,
                $"dashed guide centred on McBurney's point ({centre:F2} mm), {length:F1} mm long, on the scored line (max off-line {across.Max() * 1000:F2} mm)");
            float oblique = Vector3.Angle(s.wound.TransformDirection(axis), s.frame.right);
            Require(oblique > 20 && oblique < 60, $"the guide is oblique to the body axes ({oblique:F0} deg from transverse)");
            Require(mesh.vertices.All(v => { float above = s.AboveSkinMm(s.wound.TransformPoint(v)); return above > .2f && above < 2; }), "every dash lies just above the visible skin");
            Require(s.hint.Text == MarkingGuide.Instruction, "the hint line says plainly what to do: " + s.hint.Text);
            Render(s, "step0", s.session.workbench.headCamera.transform.position, 60);
        }

        static int MarkAlongGuide(Rig s)
        {
            int liveChecked = 0, pixels = 0;
            s.Stroke(s.input.ReferenceStart, s.input.ReferenceEnd, percent =>
            {
                if (percent < 50 || liveChecked > 0) return;
                var live = s.guide.LiveInk; var points = new Vector3[live.positionCount]; live.GetPositions(points);
                Require(live.enabled && live.gameObject.activeInHierarchy && points.Length >= 10, "ink appears on the skin while the marker draws (" + points.Length + " points)");
                Require(points.All(p => { float above = s.AboveSkinMm(s.wound.TransformPoint(p)); return above > .2f && above < 2; }), "live ink lies on top of the visible skin, not under it");
                Require(s.hint.Text != MarkingGuide.Instruction, "the instruction steps aside while drawing");
                pixels = Render(s, "drawing", s.wound.position - s.wound.forward * .25f, 40);
                liveChecked++;
            });
            Require(liveChecked == 1, "mid-stroke ink was inspected");
            Require(s.Fact("marked") == 1 && s.Achieved("mark_incision") && s.exercise.Current?.id != "mark_incision",
                $"a stroke along the guide completes mark_incision (error {s.Fact("markErrorMm")} mm, length {s.Fact("markLengthMm")} mm, angle {s.Fact("markAngleDegrees")} deg)");
            var ink = s.guide.Ink; var committed = new Vector3[ink.positionCount]; ink.GetPositions(committed);
            Require(ink.enabled && ink.gameObject.activeInHierarchy && committed.Length >= 20 && !s.guide.LiveInk.enabled, "the recorded stroke stays as ink");
            Require(committed.All(p => { float above = s.AboveSkinMm(s.wound.TransformPoint(p)); return above > .2f && above < 2; }), "recorded ink lies on top of the visible skin");
            Require(ink.sharedMaterial.color == MarkingGuide.InkColor && ink.sharedMaterial.color.b > ink.sharedMaterial.color.r && ink.startWidth >= .002f, "dark violet surgical-marker ink, wide enough to read");
            Require(ink.sharedMaterial.renderQueue > 2000, "ink draws after the opaque skin it lies on");
            int after = Render(s, "marked", s.wound.position - s.wound.forward * .25f, 40);
            if (renders != "") Require(pixels > 150 && after > 300, $"violet ink is actually on screen (drawing {pixels} px, marked {after} px)");
            return after;
        }

        static void AcceptedFades(Rig s)
        {
            Require(s.guide.Showing, "the guide is still there the moment the mark is accepted");
            s.Step(60); // 1.2 s
            Require(!s.guide.Showing && !s.guide.Root.activeInHierarchy && s.guide.Labels.All(l => !l.gameObject.activeInHierarchy),
                "after acceptance the guide, dots and labels fade away");
            Require(s.guide.Ink.enabled && s.hint.Text != MarkingGuide.Instruction, "the ink stays and the instruction is gone");
            Require(Shader.GetGlobalFloat("_ScalpalWoundWindow") == 0, "the skin stays whole until it is cut");
            Render(s, "accepted", s.session.workbench.headCamera.transform.position, 60);
        }

        // The wall returns as soon as the skin is actually opened, so the incision step is unchanged.
        static void Reveal(Rig s)
        {
            var start = s.scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutStart");
            var end = s.scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutEnd");
            s.scalpel.SetHeld(true); s.scalpel.SetTrackingValid(true); s.scalpel.SetActivation(1);
            for (int i = 0; i <= 12; i++)
            {
                s.scalpel.transform.rotation = s.wound.rotation;
                s.scalpel.transform.rotation = Quaternion.FromToRotation(end.position - start.position, s.wound.forward) * s.scalpel.transform.rotation;
                s.scalpel.transform.position += s.wound.TransformPoint(new Vector3(Mathf.Lerp(-.03f, .03f, i / 12f), 0, .0015f)) - end.position;
                Physics.SyncTransforms(); s.Step();
            }
            s.scalpel.SetActivation(0); s.Step(); s.Park(s.scalpel); s.Step();
            Require(s.Fact("opened") == 1, "the skin incision opens the skin");
            Require(!s.adapter.Wound.Concealed && Shader.GetGlobalFloat("_ScalpalWoundWindow") == 1 && s.volume.Wall.GetComponent<MeshRenderer>().enabled
                && s.adapter.Wound.transform.Find("MeasuredWoundSurfaces").gameObject.activeInHierarchy, "an opened skin reveals the wound and wall again");
            var ink = s.guide.Ink; var points = new Vector3[ink.positionCount]; ink.GetPositions(points);
            Require(points.All(p => p.z < 0 && p.z > -.002f), "revealed, the ink lies on the wall's skin plane");
            Render(s, "opened", s.wound.position - s.wound.forward * .25f, 40);
        }

        // Actual close event changes presentation, not physical topology. Runtime ordering must
        // keep the solver's subsequent visibility refresh from exposing the fractured slab again.
        static void Closure(Rig s)
        {
            Require(s.Fact("opened") == 1 && !s.adapter.Wound.Concealed && s.volume.Wall.GetComponent<MeshRenderer>().enabled,
                "closure starts with opened skin and a visible teaching wall");
            int cuts = s.volume.Wall.Volume.CutFaceCount, topology = s.volume.Wall.Volume.TopologyRevision;
            Require(cuts > 0, "closure fixture contains actual fractured wall faces before hiding them");
            var close = s.input.CreateMeasurement("assistant", "close", "skin", s.wound.position, "closure-visual-validation");
            Require(close != null && s.input.SubmitMeasured(close), "actual assistant close event accepted through the body action route");
            Require(s.Fact("closed") == 1 && s.Fact("opened") == 1, "closed skin retains its prior opening history");
            s.Step();
            Require(Shader.GetGlobalFloat("_ScalpalWoundWindow") == 0, "closure restores opaque patient skin by disabling the window");
            Require(s.adapter.Wound.Concealed && !s.adapter.Wound.transform.Find("MeasuredWoundSurfaces").gameObject.activeInHierarchy,
                "closed skin conceals the authored wound surfaces");
            Require(!s.volume.Wall.GetComponent<MeshRenderer>().enabled, "closed skin hides the fractured teaching wall");
            Require(s.volume.Wall.Volume.CutFaceCount == cuts && s.volume.Wall.Volume.TopologyRevision == topology,
                "visual closure does not reset or claim to heal the physical wall topology");
            s.Step(3);
            Require(!s.guide.Showing && !s.guide.Root.activeInHierarchy && !s.guide.Ink.gameObject.activeInHierarchy
                && !s.guide.LiveInk.gameObject.activeInHierarchy && s.hint.Text != MarkingGuide.Instruction,
                "closed skin shows neither marking guidance nor ink on subsequent runtime refreshes");
            Require(Shader.GetGlobalFloat("_ScalpalWoundWindow") == 0 && !s.volume.Wall.GetComponent<MeshRenderer>().enabled,
                "the solver visibility refresh cannot reveal the closed wall again");
            Render(s, "closed", s.wound.position - s.wound.forward * .25f, 40);
            s.NewAttempt();
            Require(s.Fact("closed") == 0 && s.Fact("opened") == 0 && s.guide.Showing
                && Shader.GetGlobalFloat("_ScalpalWoundWindow") == 0 && !s.volume.Wall.GetComponent<MeshRenderer>().enabled,
                "retry restores the intact skin and the initial marking guide");
        }

        static void OffTarget(Rig s)
        {
            var plan = s.exercise.SelectedCase.procedure.openBody;
            string far = plan.guardrails.Single(g => g.tissueId == "skin" && g.verb == "mark").feedback;
            Vector3 a = s.input.ReferenceStart, b = s.input.ReferenceEnd, side = Vector3.Cross(Vector3.forward, (b - a).normalized);
            Vector3 centre = (a + b) * .5f;
            void Expect(string label, Vector3 from, Vector3 to, string reason, string guardrail)
            {
                s.NewAttempt();
                s.Stroke(from, to);
                Require(s.Fact("marked") == 1 && !s.Achieved("mark_incision"), label + ": the mark is recorded but not accepted");
                Require(s.guide.LastReason == reason && s.hint.Text == reason, $"{label}: one short reason on the hint line ('{s.hint.Text}'; error {s.Fact("markErrorMm")} mm, length {s.Fact("markLengthMm")} mm, angle {s.Fact("markAngleDegrees")} deg)");
                Require(guardrail == null ? !s.mistakes.Contains("mark_far") : s.mistakes.Contains(guardrail), label + ": the case guardrail record matches");
                Require(s.guide.Showing && s.guide.Ink.enabled, label + ": the guide stays and the attempt's ink is visible to correct against");
                s.Park(s.marker); s.Step();
            }
            Expect("far", a + side * .03f, b + side * .03f, far, "mark_far");
            Render(s, "far", s.session.workbench.headCamera.transform.position, 60);
            Expect("short", centre - (b - a) * .25f, centre + (b - a) * .25f, "Too short. Draw the whole dotted line.", null);
            Vector3 tilted = Quaternion.AngleAxis(-32, Vector3.forward) * (b - a) * .6f;
            Expect("angle", centre - tilted, centre + tilted, "Wrong angle. Follow the dotted line.", null);
        }

        // Learner-eye render; returns the number of dark-violet ink pixels. Needs a graphics device (no -nographics).
        static int Render(Rig s, string name, Vector3 eye, float fov)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return 0;
            string folder = Environment.GetEnvironmentVariable("SCALPAL_MARKING_RENDERS");
            var go = new GameObject("MarkingGuideValidationCamera"); var camera = go.AddComponent<Camera>();
            camera.transform.position = eye; camera.transform.LookAt(s.wound.position, Vector3.up);
            camera.fieldOfView = fov; camera.nearClipPlane = .01f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
            var target = new RenderTexture(1280, 960, 24) { antiAliasing = 4 }; camera.targetTexture = target;
            var previous = RenderTexture.active; Texture2D image = null;
            try
            {
                camera.Render(); RenderTexture.active = target;
                image = new Texture2D(1280, 960, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 1280, 960), 0, 0); image.Apply();
                if (!string.IsNullOrEmpty(folder)) { File.WriteAllBytes(Path.Combine(folder, "marking_" + name + ".png"), image.EncodeToPNG()); renders += (renders == "" ? "" : ",") + name; }
                else if (renders == "") renders = "inspected";
                return image.GetPixels32().Count(p => p.b > p.r + 12 && p.r > p.g + 12 && p.b > 50 && p.b < 150);
            }
            finally
            {
                RenderTexture.active = previous; camera.targetTexture = null;
                if (image) UnityEngine.Object.DestroyImmediate(image);
                target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
