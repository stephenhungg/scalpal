using System;
using System.Collections.Generic;
using System.IO;
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
    // The whole open expected path as the learner sees it in VR: actual native scene, actual OpenSurgerySession
    // composition, the actual wall volume, atlas anatomy and tools, driven by synthetic tracked poses through the
    // real interaction path (marker, blade, retractors, forceps, babcock, clamps, scissors, tie, suction, assistant
    // close). Why it matters: a learner who completes a step and sees no change on the body cannot tell that the
    // step worked or what the body now looks like. Not headset evidence.
    public static class OpenStepVisualsValidation
    {
        const float Dt = .02f;
        static int checks;
        static string demoFolder;
        static string renders = "", pixels = "";
        static readonly MethodInfo AutoClose = typeof(OpenSurgerySession).GetMethod("AutoClose", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo HintLateUpdate = typeof(SurgeryTriggerHint).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo LatchLateUpdate = typeof(SurgeryInstrumentLatch).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        static void Require(bool passed, string message)
        {
            checks++;
            if (!passed) throw new InvalidOperationException("Open step visuals validation: " + message);
        }
        [MenuItem("Scalpal/Surgery/Validate Step Visuals")]
        public static void Run()
        {
            checks = 0; renders = ""; pixels = "";
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new InvalidOperationException("Open step visuals validation needs to open the native scene; save or discard scene changes first");
            var previous = EditorSceneManager.GetSceneManagerSetup();
            NativeTissueSimulation tissue = null;
            try
            {
                var adapter = OpenSurgeryBuild.ConfigureSceneAttempt(out var session, out tissue);
                session.presentation.passthrough = false; session.presentation.Apply();
                var s = new Rig(session, adapter, tissue);
                s.NewAttempt();
                Walk(s);
                Debug.Log($"SCALPAL_OPEN_STEP_VISUALS_OK: {checks} checks; changedPixels={(pixels == "" ? "skipped (no graphics device)" : pixels)}; "
                    + "actual scene, synthetic tracked poses through the real interaction path, no headset test");
            }
            finally
            {
                if (tissue) tissue.Dispose();
                OpenSurgeryBuild.RestoreScenes(previous);
            }
        }

        // Optional reel export: the real synthetic step fixture, with matching incision/closure
        // cameras and wider organ stages. Does not modify validation views or pixel thresholds.
        [MenuItem("Scalpal/Surgery/Export Demo Step Renders")]
        public static void ExportDemo()
        {
            demoFolder = Environment.GetEnvironmentVariable("SCALPAL_DEMO_RENDERS");
            if (string.IsNullOrEmpty(demoFolder))
                throw new InvalidOperationException("Set SCALPAL_DEMO_RENDERS to the demo output directory");
            Directory.CreateDirectory(demoFolder);
            try { Run(); Debug.Log("SCALPAL_DEMO_STEP_RENDERS_OK: 1920x1080 synthetic Editor views; " + demoFolder); }
            finally { demoFolder = null; }
        }

        sealed class Rig
        {
            public readonly NativeCaseSession session;
            public readonly OpenSurgerySession adapter;
            public readonly OpenBodyInteraction input;
            public readonly AnatomyExerciseBinding exercise;
            public readonly Transform wound, frame;
            public readonly NativeVolumeSimulation volume;
            public readonly NativeTissueSimulation tissue;
            public readonly OpenBodyBleeding bleeding;
            public readonly PatientIncisions incisions;
            public readonly SurgeryBlood blood;
            public readonly MeshCollider skin;
            public readonly List<BodyRecord> records = new List<BodyRecord>();
            public bool gate = true;
            public Rig(NativeCaseSession session, OpenSurgerySession adapter, NativeTissueSimulation tissue)
            {
                this.session = session; this.adapter = adapter; this.tissue = tissue;
                input = adapter.Interaction; exercise = session.exercise; wound = adapter.Wound.transform; frame = session.patientFrame;
                volume = session.GetComponent<NativeVolumeSimulation>(); bleeding = session.GetComponent<OpenBodyBleeding>();
                incisions = session.GetComponentInChildren<PatientIncisions>(true); blood = session.GetComponentInChildren<SurgeryBlood>(true);
                skin = session.presentation.virtualMannequin.GetComponentsInChildren<MeshCollider>(true).Single();
                Require(volume && bleeding && incisions && blood && adapter.Guide, "the open session composes wall, pool, incisions, blood and marking guide");
                input.Submitted += (record, _) => records.Add(record);
                // The learner stands at the patient's right, above McBurney's point.
                session.workbench.headCamera.transform.position = Eye;
                session.workbench.headCamera.transform.LookAt(wound.position);
            }
            public Vector3 Eye => wound.position + Vector3.up * .5f - frame.right * .4f - frame.forward * .1f;
            // The runtime retry path (a new body re-composes the attempt), with a validation-owned practice gate.
            public void NewAttempt()
            {
                var selected = exercise.SelectedCase;
                Require(exercise.SelectCase(new ScalpalBundle { cases = new[] { selected } }, selected.caseId, true, out var reason), "fresh attempt: " + reason);
                session.anatomy.SetRegistrationValid(true); gate = true;
                foreach (var tool in session.workbench.tools) if (tool) { tool.SetHeld(false); var latch = tool.GetComponent<SurgeryInstrumentLatch>(); if (latch) latch.Clear(); }
                typeof(OpenSurgerySession).GetMethod("ConfigureAttempt", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, null);
                volume.Initialize(wound, session.workbench, () => gate, true);
                input.Initialize(exercise, session.workbench.tools, frame, wound, () => gate);
                Require(input.ConfigureMobility(adapter.mobileOrganGroups, out var status), "mobile organ group binds: " + status);
                Require(OpenSurgeryAnatomy.Bind(session.anatomy, input), "atlas base references bind");
                tissue.Initialize(session.anatomy, session.workbench, () => gate);
                adapter.Guide.Initialize(session, input, adapter.Wound, session.GetComponent<SurgeryTriggerHint>(), null, null, () => gate);
                records.Clear();
                foreach (var tool in session.workbench.tools) if (tool && tool.actionPoint) Park(tool);
                Step();
            }
            // Runtime order: OpenSurgerySession.Update, then LateUpdate (wall solver, organ cages, latches, guide).
            public void Step(int count = 1)
            {
                for (int i = 0; i < count; i++)
                {
                    adapter.Wound.SetRegistrationValid(gate);
                    bleeding.Prime(); input.Simulate(Dt); bleeding.Simulate(Dt); incisions.Simulate(Dt); blood.Simulate(Dt);
                    AutoClose.Invoke(adapter, new object[] { Dt });
                    adapter.Wound.Apply(exercise.Body);
                    volume.Simulate(Dt); tissue.Simulate(Dt);
                    foreach (var latch in session.workbench.tools.Where(t => t).Select(t => t.GetComponent<SurgeryInstrumentLatch>()).Where(l => l)) LatchLateUpdate.Invoke(latch, null);
                    adapter.Guide.Refresh(Dt);
                    session.GetComponent<SurgicalOrganAppearance>()?.Refresh();
                    session.GetComponent<SurgicalActionAppearance>()?.Refresh();
                    session.GetComponent<SurgicalClosureAppearance>()?.Refresh();
                    var hint=session.GetComponent<SurgeryTriggerHint>();if(hint)HintLateUpdate.Invoke(hint,null);
                    Physics.SyncTransforms();
                }
            }
            public InstrumentBehaviour Tool(string id, int index = 0)
            {
                var tool = session.workbench.tools.Where(t => t && t.instrumentId == id).ElementAtOrDefault(index);
                Require(tool && tool.actionPoint, "scene tool exists: " + id + "#" + index);
                return tool;
            }
            public void Hold(InstrumentBehaviour tool) { tool.SetHeld(true); tool.SetTrackingValid(true); tool.SetActivation(1); }
            // Put down far from the patient (a latched tool returns to its tissue on the next LateUpdate).
            public void Park(InstrumentBehaviour tool)
            {
                tool.SetHeld(false); tool.SetActivation(0);
                tool.transform.position += wound.TransformPoint(new Vector3(.25f, .25f, -.3f)) - tool.actionPoint.position + Vector3.up * 3;
                Physics.SyncTransforms();
            }
            public void PlaceLocal(InstrumentBehaviour tool, Vector3 local) => PlaceWorld(tool, wound.TransformPoint(local));
            public void PlaceWorld(InstrumentBehaviour tool, Vector3 world)
            {
                tool.transform.rotation = wound.rotation;
                tool.transform.position += world - tool.actionPoint.position;
                Physics.SyncTransforms();
            }
            public bool SkinAt(Vector3 local, out Vector3 world)
            {
                local.z = 0; Vector3 at = wound.TransformPoint(local);
                bool backfaces = Physics.queriesHitBackfaces; Physics.queriesHitBackfaces = true;
                try { bool hit = skin.Raycast(new Ray(at - wound.forward * .25f, wound.forward), out var surface, .5f); world = surface.point; return hit; }
                finally { Physics.queriesHitBackfaces = backfaces; }
            }
            public double Fact(string tissueId, string fact) => exercise.Body.Get(tissueId, fact);
            public bool Achieved(string id) => exercise.CompletedMilestones != null && exercise.CompletedMilestones.Contains(id);
            public SurgeryTissueTarget Target(string id) => input.Targets.Single(t => t.tissueId == id);
        }

        static OpenWoundView Wound(Rig s) => s.adapter.Wound;
        static SurgicalActionAppearance Actions(Rig s) => s.session.GetComponent<SurgicalActionAppearance>();
        static SurgicalClosureAppearance Closure(Rig s) => s.session.GetComponent<SurgicalClosureAppearance>();
        static float Width(Rig s, int layer) => Wound(s).LayerWidth(layer) * 1000;
        static Renderer Organ(Rig s, string id) { s.session.anatomy.TryGetPart(id, out var part); return part ? part.GetComponent<Renderer>() : null; }
        static float OrganDepthMm(Rig s, string id) => s.wound.InverseTransformPoint(Organ(s, id).bounds.center).z * 1000;
        static bool WindowOpen() => Shader.GetGlobalFloat("_ScalpalWoundWindow") == 1 && Shader.GetGlobalVector("_ScalpalWoundOpening").w > 0;
        static bool SlabDrawn(Rig s) { var r = s.volume.Wall.GetComponent<MeshRenderer>(); return r.enabled && !r.forceRenderingOff; }

        // One expected-path step: the visual is absent before, the milestone completes through the real interaction
        // path, the visual is present after, and (with a graphics device) the learner's view of the body changes.
        static void Step(Rig s, string label, string milestone, Func<bool> visual, string what, Action act)
        {
            Require(!s.Achieved(milestone) && !visual(), $"{label}: before the step, no {what}");
            var views = Views(s); var before = Render(s, views, null);
            act();
            Require(s.Achieved(milestone), $"{label}: {milestone} completes through the real interaction path");
            Require(visual(), $"{label}: after the step, {what}");
            var after = Render(s, views, label);
            if (before != null)
            {
                int changed = Enumerable.Range(0, views.Length).Select(i => Changed(before[i], after[i])).Max();
                pixels += (pixels == "" ? "" : " ") + label + "=" + changed;
                Require(changed >= 400, $"{label}: the learner's view of the body changes ({changed} px)");
            }
        }

        static void Walk(Rig s)
        {
            var closed = Render(s, Views(s), "closed");
            Require(!WindowOpen() && !Wound(s).RenderingWound && !SlabDrawn(s), "closed patient: intact skin, no wound, no wall slab");

            Step(s, "0_mark", "mark_incision", () => s.adapter.Guide.Ink.enabled && s.adapter.Guide.Ink.positionCount >= 20, "violet ink line on the skin", () => { Mark(s); s.Step(60); });

            Step(s, "1_skin", "incise_skin", () => WindowOpen() && Wound(s).IsLayerVisible(0) && Wound(s).IsLayerVisible(1) && Width(s, 0) > 0 && Width(s, 1) > 0 && Wound(s).IsLayerVisible(2) && Width(s, 2) == 0,
                "a skin opening along the line with fat walls down to the closed aponeurosis", () => { Blade(s, .0015f); Blade(s, .010f); });
            Require(!SlabDrawn(s) && s.volume.Wall.GetComponent<MeshCollider>().enabled, "1_skin: the flat physics slab is not drawn (its collider stays live)");
            Require(!s.adapter.Guide.Ink.gameObject.activeInHierarchy, "1_skin: the ink has become the incision");

            Step(s, "2_fascia", "open_fascia", () => Width(s, 2) > 0 && Wound(s).IsLayerVisible(3) && Width(s, 3) == 0,
                "the white aponeurosis split along its fibers over red muscle", () => Blade(s, .0195f));

            float skinBefore = Width(s, 0);
            Step(s, "3_muscle", "split_muscle", () => Width(s, 3) > 0 && Wound(s).IsLayerVisible(4) && Width(s, 4) == 0,
                "the muscle parted by the retractors over the closed peritoneum", () => Split(s));
            Require(Width(s, 0) > skinBefore, $"3_muscle: the retracted wound widens ({skinBefore:F1} -> {Width(s, 0):F1} mm)");
            s.Step(30);
            Require(Width(s, 3) > 0, "3_muscle: the gap stays open while the latched retractors hold it");

            Step(s, "4_peritoneum", "open_peritoneum", () => Width(s, 4) > 0 && Wound(s).RenderingCavity && Wound(s).BowelVisible, "the peritoneum opened over glistening bowel", () => Peritoneum(s));

            var appendix = Organ(s, "appendix");
            var block = new MaterialPropertyBlock(); appendix.GetPropertyBlock(block);
            Require(block.GetColor("_BaseColor") == SurgicalOrganAppearance.Inflamed, "the appendix is drawn inflamed (swollen-red)");
            Step(s, "5_delivered", "deliver_appendix", () => OrganDepthMm(s, "appendix") < 0 && OrganDepthMm(s, "cecum") < 0,
                "the inflamed appendix and the caecum lifted above the wound", () => Deliver(s));

            Step(s, "6_mesoappendix", "divide_mesoappendix", () => Actions(s).VisibleTieCount >= 2 && Actions(s).DivisionVisible("mesoappendix") && !Actions(s).OozeVisible,
                "two ties on the mesoappendix vessels and the cut between them, ooze stopped", () => Mesoappendix(s));

            Step(s, "7_base", "ligate_base", () => Actions(s).VisibleTieCount >= 3 && Actions(s).DivisionVisible("appendix"),
                "a tie at the base, the stump, and the appendix detached and set aside", () => Base(s));

            float wet = Wound(s).FieldWetness;
            Require(wet > .3f && Wound(s).FieldBloodVisible, $"before cleaning there is blood in the field (wetness {wet:F2})");
            Step(s, "8_clean", "inspect_clean", () => Wound(s).FieldWetness < .05f && !Wound(s).FieldBloodVisible && Actions(s).HighlightVisible("appendix") && Actions(s).HighlightVisible("mesoappendix"),
                "a dry field with the stump and vessels highlighted", () => Clean(s));

            Step(s, "9_closed", "close", () => !WindowOpen() && Closure(s).ClosureVisible && !Wound(s).RenderingWound && Organ(s, "cecum").forceRenderingOff && !Actions(s).HighlightVisible("appendix"),
                "layers closed and the skin sutured over the returned bowel", () => CloseInLayers(s));

            s.NewAttempt();
            Require(!WindowOpen() && !Wound(s).RenderingWound && !SlabDrawn(s) && Enumerable.Range(0, 5).All(i => Width(s, i) == 0) && Wound(s).FieldWetness == 0
                && !Closure(s).ClosureVisible && Actions(s).TieCount == 0 && Actions(s).DivisionCount == 0, "retry: intact skin, no wound, no slab, no ties, divisions or sutures");
            Require(!Organ(s, "appendix").forceRenderingOff && OrganDepthMm(s, "appendix") > 100, "retry: the organs are back at rest, deep in the abdomen");
            var retry = Render(s, Views(s), "retry");
            if (closed != null)
            {
                int changed = Enumerable.Range(0, closed.Length).Select(i => Changed(closed[i], retry[i])).Max();
                pixels += " retry=" + changed;
                Require(changed < 400, $"retry: the learner sees the closed patient again ({changed} px differ from the first view)");
            }
        }
        // Auto-close, one frame at a time: each layer closes in order and is drawn closed as it does.
        static void CloseInLayers(Rig s)
        {
            string[] order = { "peritoneum", "muscle", "fascia", "fat", "skin" };
            int next = 0;
            for (int frame = 0; frame < 200 && next < order.Length; frame++)
            {
                s.Step();
                while (next < order.Length && s.Fact(order[next], "closed") == 1)
                {
                    string layer = order[next];
                    Require(order.Skip(next + 1).All(l => s.Fact(l, "closed") == 0), $"9_closed: {layer} closes before the layers above it");
                    int index = Array.IndexOf(Wound(s).layerIds, layer);
                    Require(Width(s, index) == 0, $"9_closed: the closed {layer} is drawn closed");
                    if (layer == "peritoneum") Require(!Wound(s).RenderingCavity, "9_closed: the closed peritoneum hides the cavity and bowel");
                    if (layer != "skin" && layer != "peritoneum") Require(Wound(s).IsLayerVisible(index), $"9_closed: the closed {layer} is the wound's floor");
                    next++;
                }
            }
            Require(next == order.Length, "9_closed: every layer closed");
        }

        // Learner views from the patient's right: standing (wide), close over the wound, leaning over it, and at the
        // delivered organs. Camera poses are fixed before a step so before/after compare the same view.
        static (string name, Vector3 eye, Vector3 target, float fov)[] Views(Rig s)
        {
            Vector3 organs = s.wound.position - s.wound.forward * .02f;
            var a = s.input.Targets.FirstOrDefault(t => t.tissueId == "appendix" && t.HasBase); var m = s.input.Targets.FirstOrDefault(t => t.tissueId == "mesoappendix" && t.HasBase);
            if (a && m && s.wound.InverseTransformPoint(a.basePoint.position).z < 0) organs = (a.basePoint.position + m.basePoint.position) * .5f;
            return new[] {
                ("learner", s.Eye, s.wound.position, 50f),
                ("close", s.wound.position + Vector3.up * .22f - s.frame.right * .14f - s.frame.forward * .05f, s.wound.position, 40f),
                ("over", s.wound.position + Vector3.up * .32f - s.frame.right * .06f, s.wound.position, 34f),
                ("organs", organs + Vector3.up * .16f - s.frame.right * .09f + s.frame.forward * .03f, organs, 34f) };
        }
        // Renders the body as the learner sees it, without the tools (latched or parked) and the tool-tip hint, so a
        // difference is the body's. Null without a graphics device (-nographics): pixel checks are then skipped.
        static Color32[][] Render(Rig s, (string name, Vector3 eye, Vector3 target, float fov)[] views, string label)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            string folder = Environment.GetEnvironmentVariable("SCALPAL_STEP_RENDERS");
            var hint = s.session.GetComponent<SurgeryTriggerHint>(); if (hint) hint.Hide();
            var tools = s.session.workbench.tools.Where(t => t).SelectMany(t => t.GetComponentsInChildren<Renderer>(true)).Where(r => !r.forceRenderingOff).ToArray();
            foreach (var r in tools) r.forceRenderingOff = true;
            var result = new Color32[views.Length][];
            try
            {
                for (int i = 0; i < views.Length; i++)
                {
                    var go = new GameObject("OpenStepVisualsCamera"); var camera = go.AddComponent<Camera>();
                    camera.transform.position = views[i].eye; camera.transform.LookAt(views[i].target, Vector3.up);
                    camera.fieldOfView = views[i].fov; camera.nearClipPlane = .01f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                    var target = new RenderTexture(960, 720, 24) { antiAliasing = 4 }; camera.targetTexture = target;
                    var previous = RenderTexture.active; Texture2D image = null;
                    try
                    {
                        camera.Render(); RenderTexture.active = target;
                        image = new Texture2D(960, 720, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, 960, 720), 0, 0); image.Apply();
                        if (label != null && !string.IsNullOrEmpty(folder)) File.WriteAllBytes(Path.Combine(folder, "step_" + label + "_" + views[i].name + ".png"), image.EncodeToPNG());
                        result[i] = image.GetPixels32();
                    }
                    finally
                    {
                        RenderTexture.active = previous; camera.targetTexture = null;
                        if (image) UnityEngine.Object.DestroyImmediate(image);
                        target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(go);
                    }
                }
            }
            finally { foreach (var r in tools) if (r) r.forceRenderingOff = false; }
            if (label != null && !string.IsNullOrEmpty(demoFolder)) RenderDemo(s, label);
            if (label != null) renders += (renders == "" ? "" : ",") + label;
            return result;
        }
        static void RenderDemo(Rig s, string label)
        {
            var go = new GameObject("SyntheticDemoCamera");
            var camera = go.AddComponent<Camera>();
            // 60 mm incision spans roughly 40% of the 150 mm horizontal field.
            // Camera only: no wound, body, organ, tool or scoring frame is moved.
            camera.transform.position = s.wound.position + Vector3.up * .225f - s.frame.right * .035f;
            camera.transform.LookAt(s.wound.position, s.frame.forward);
            bool organs = label == "5_delivered" || label == "6_mesoappendix" || label == "7_base" || label == "8_clean";
            camera.orthographic = true; camera.orthographicSize = organs ? .073f : .043f;
            camera.nearClipPlane = .005f; camera.farClipPlane = 4f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.035f, .055f, .065f);
            var target = new RenderTexture(1920, 1080, 24) { antiAliasing = 8 };
            var previous = RenderTexture.active; Texture2D image = null;
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            var intensities = lights.Select(light => light.intensity).ToArray();
            var ambientMode = RenderSettings.ambientMode; var ambientLight = RenderSettings.ambientLight;
            foreach (var light in lights) light.intensity *= .45f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.62f, .62f, .62f);
            Material seamOverlay = null; UnityEngine.Rendering.CommandBuffer closureDraw = null;
            // The collision hull used by the existing seam does not exactly match the coarse
            // visible mannequin. In this synthetic export only, draw those original seam
            // vertices above the skin depth. No line is enlarged, relocated or newly scored.
            var seam = s.wound.Find("AssistedClosure_SeamAndInterruptedStitches");
            if (seam && seam.gameObject.activeInHierarchy)
            {
                seamOverlay = new Material(Shader.Find("Hidden/Internal-Colored"));
                seamOverlay.SetColor("_Color", new Color(.21f, .10f, .10f));
                seamOverlay.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                seamOverlay.SetInt("_ZWrite", 0);
                seamOverlay.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                closureDraw = new UnityEngine.Rendering.CommandBuffer { name = "Synthetic closure seam visibility" };
                foreach (var line in seam.GetComponentsInChildren<LineRenderer>()) closureDraw.DrawRenderer(line, seamOverlay);
                camera.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterEverything, closureDraw);
            }
            var hint = s.session.GetComponent<SurgeryTriggerHint>(); if (hint) hint.Hide();
            // Scene labels can obscure the specimen in this tight export. The final screen
            // caption explicitly retains assisted-display provenance instead.
            var sceneText = UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsSortMode.None)
                .SelectMany(text => text.GetComponentsInChildren<Renderer>(true))
                .Concat(UnityEngine.Object.FindObjectsByType<TMPro.TMP_Text>(FindObjectsSortMode.None)
                    .SelectMany(text => text.GetComponentsInChildren<Renderer>(true)));
            var tools = s.session.workbench.tools.Where(t => t).SelectMany(t => t.GetComponentsInChildren<Renderer>(true))
                .Concat(sceneText).Where(r => r && !r.forceRenderingOff).Distinct().ToArray();
            foreach (var r in tools) r.forceRenderingOff = true;
            try
            {
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                image = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(demoFolder, "demo-step-" + label.Replace('_', '-') + ".png"), image.EncodeToPNG());
            }
            finally
            {
                foreach (var r in tools) if (r) r.forceRenderingOff = false;
                for (int i = 0; i < lights.Length; i++) if (lights[i]) lights[i].intensity = intensities[i];
                RenderSettings.ambientMode = ambientMode; RenderSettings.ambientLight = ambientLight;
                if (closureDraw != null) { camera.RemoveAllCommandBuffers(); closureDraw.Release(); }
                if (seamOverlay) UnityEngine.Object.DestroyImmediate(seamOverlay);
                RenderTexture.active = previous; camera.targetTexture = null;
                if (image) UnityEngine.Object.DestroyImmediate(image);
                target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(go);
            }
        }

        static int Changed(Color32[] a, Color32[] b)
        {
            int count = 0;
            for (int i = 0; i < a.Length; i++)
                if (Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)) > 24) count++;
            return count;
        }

        // Step 0: the marker along the dotted guide on the visible skin.
        static void Mark(Rig s)
        {
            var marker = s.Tool("skin_marker"); s.Hold(marker);
            Vector3 a = s.input.ReferenceStart, b = s.input.ReferenceEnd;
            int steps = Mathf.Max(2, Mathf.RoundToInt(Vector3.Distance(a, b) / .002f));
            for (int i = 0; i <= steps; i++)
            {
                Require(s.SkinAt(Vector3.Lerp(a, b, i / (float)steps), out var surface), "marker stroke stays over the patient");
                marker.transform.rotation = s.wound.rotation;
                marker.transform.position += surface + s.wound.forward * .001f - marker.actionPoint.position; Physics.SyncTransforms();
                s.Step();
            }
            marker.SetActivation(0); s.Step(); s.Park(marker); s.Step();
        }
        // One continuous blade stroke along the marked line (wound +X), tip at the given wall depth.
        static void Blade(Rig s, float tipDepth)
        {
            var scalpel = s.Tool("scalpel");
            var start = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutStart");
            var end = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutEnd");
            s.Hold(scalpel);
            for (int i = 0; i <= 12; i++)
            {
                scalpel.transform.rotation = s.wound.rotation;
                scalpel.transform.rotation = Quaternion.FromToRotation(end.position - start.position, s.wound.forward) * scalpel.transform.rotation;
                scalpel.transform.position += s.wound.TransformPoint(new Vector3(Mathf.Lerp(-.03f, .03f, i / 12f), 0, tipDepth)) - end.position;
                Physics.SyncTransforms(); s.Step();
            }
            scalpel.SetActivation(0); s.Step(); s.Park(scalpel); s.Step();
        }
        // Two retractors on the lips of the fascial opening, pulled apart across the fibers, then let go (latched).
        static void Split(Rig s)
        {
            InstrumentBehaviour first = s.Tool("retractor", 0), second = s.Tool("retractor", 1);
            Vector3 a = new Vector3(-.01f, -.008f, .019f), b = new Vector3(-.01f, .008f, .019f);
            s.Hold(first); s.Hold(second); s.PlaceLocal(first, a); s.PlaceLocal(second, b); s.Step(6);
            for (int i = 1; i <= 20; i++) { s.PlaceLocal(first, a + Vector3.down * (i * .001f)); s.PlaceLocal(second, b + Vector3.up * (i * .001f)); s.Step(); }
            s.Step(15);
            first.SetHeld(false); second.SetHeld(false); s.Step(30);
        }
        // Forceps tent the membrane in the split, the blade nicks beside them.
        static void Peritoneum(Rig s)
        {
            var forceps = s.Tool("toothed_forceps"); s.Hold(forceps);
            Vector3 grip = new Vector3(.01f, 0, .027f); s.PlaceLocal(forceps, grip); s.Step(6);
            for (int i = 1; i <= 15; i++) { s.PlaceLocal(forceps, grip - Vector3.forward * (i * .001f)); s.Step(); }
            s.Step(10); Render(s, Views(s), "4_tent_before_nick");
            var held = s.records.Last(r => r.action.verb == "grasp" && r.action.tissueId == "peritoneum");
            Vector3 membrane = s.wound.InverseTransformPoint(s.frame.TransformPoint(new Vector3(held.action.position.x, held.action.position.y, held.action.position.z)));
            Require(s.volume.TryContactLayer("peritoneum", s.wound.TransformPoint(new Vector3(membrane.x - .008f, 0, membrane.z)), .008f, out var surface), "lifted membrane reachable");
            float depth = s.wound.InverseTransformPoint(surface).z + .001f;
            var scalpel = s.Tool("scalpel");
            var start = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutStart");
            var end = scalpel.GetComponentsInChildren<Transform>(true).Single(t => t.name == "CutEnd");
            s.Hold(scalpel);
            int steps = Mathf.RoundToInt(.008f / .005f);
            for (int i = 0; i <= steps; i++)
            {
                scalpel.transform.rotation = s.wound.rotation;
                scalpel.transform.rotation = Quaternion.FromToRotation(end.position - start.position, s.wound.forward) * scalpel.transform.rotation;
                scalpel.transform.position += s.wound.TransformPoint(new Vector3(Mathf.Lerp(membrane.x - .012f, membrane.x - .004f, i / (float)steps), 0, depth)) - end.position;
                Physics.SyncTransforms(); s.Step();
            }
            scalpel.SetActivation(0); s.Step(); s.Park(scalpel); s.Park(forceps); s.Step(10);
        }
        // Babcock on the deep appendix, lifted gently out above the wound plane, then let go.
        static void Deliver(Rig s)
        {
            s.session.anatomy.TryGetPart("appendix", out var appendix);
            var collider = appendix.GetComponent<MeshCollider>();
            Vector3 center = appendix.transform.TransformPoint(appendix.GetComponent<MeshFilter>().sharedMesh.bounds.center);
            Require(collider.Raycast(new Ray(center - s.wound.forward * .1f, s.wound.forward), out var hit, .2f), "appendix upper surface");
            var babcock = s.Tool("babcock"); s.Hold(babcock);
            s.PlaceWorld(babcock, hit.point - s.wound.forward * .002f); s.Step(6);
            Vector3 from = s.wound.InverseTransformPoint(babcock.actionPoint.position), to = new Vector3(0, 0, -.03f);
            int steps = Mathf.CeilToInt(Vector3.Distance(from, to) / .0015f);
            for (int i = 1; i <= steps; i++) { s.PlaceLocal(babcock, Vector3.Lerp(from, to, i / (float)steps)); s.Step(); }
            babcock.SetActivation(0); s.Step(); s.Park(babcock); s.Step(10);
        }
        static void Mesoappendix(Rig s)
        {
            Commit(s, s.Tool("hemostat", 0), "mesoappendix", 5, "clamp");
            Commit(s, s.Tool("hemostat", 1), "mesoappendix", 15, "clamp"); Render(s, Views(s), "6_clamped_before_cut");
            Require(!Actions(s).DivisionVisible("mesoappendix") && Actions(s).VisibleTieCount == 0 && !Actions(s).OozeVisible, "6_mesoappendix: clamped, nothing divided or tied yet");
            Cut(s, s.Tool("metzenbaum_scissors"), "mesoappendix", 10); Render(s, Views(s), "6_divided_before_ties");
            Require(Actions(s).DivisionVisible("mesoappendix") && Actions(s).OozeVisible, "6_mesoappendix: the cut between the clamps oozes until it is tied");
            Commit(s, s.Tool("suture_tie"), "mesoappendix", 5, "tie");
            Commit(s, s.Tool("suture_tie"), "mesoappendix", 15, "tie");
        }
        static void Base(Rig s)
        {
            Require(s.input.Choose("appendix", "true_base"), "true base decision accepted");
            Commit(s, s.Tool("right_angle_clamp"), "appendix", 3, "clamp");
            Commit(s, s.Tool("suture_tie"), "appendix", 3, "tie"); Render(s, Views(s), "7_ligated_before_cut");
            Commit(s, s.Tool("right_angle_clamp"), "appendix", 8, "clamp");
            Cut(s, s.Tool("metzenbaum_scissors"), "appendix", 4);
        }
        // Suction dwells on the stump and on the divided mesoappendix.
        static void Clean(Rig s)
        {
            var suction = s.Tool("suction_irrigator");
            foreach (var (id, along) in new[] { ("appendix", 2f), ("mesoappendix", 10f) })
            {
                Require(FindSite(s, suction, id, along, out var site, out _), "suction site on " + id);
                s.Hold(suction); s.PlaceWorld(suction, site); s.Step(70);
                s.Park(suction); s.Step();
            }
        }
        static void Commit(Rig s, InstrumentBehaviour tool, string tissueId, float alongMm, string verb)
        {
            Require(FindSite(s, tool, tissueId, alongMm, out var site, out _), $"contact site on {tissueId} at {alongMm} mm");
            int count = s.records.Count;
            s.Hold(tool); s.PlaceWorld(tool, site); s.Step();
            Require(s.records.Skip(count).Any(r => r.action.verb == verb && r.action.tissueId == tissueId), $"{tool.instrumentId} {verb} on {tissueId} at {alongMm} mm");
            s.Park(tool); s.Step();
        }
        static void Cut(Rig s, InstrumentBehaviour tool, string tissueId, float alongMm)
        {
            Require(FindSite(s, tool, tissueId, alongMm, out var site, out var direction), $"stroke site on {tissueId} at {alongMm} mm");
            int count = s.records.Count;
            s.Hold(tool); s.PlaceWorld(tool, site); s.Step();
            for (int i = 1; i <= 3; i++) { s.PlaceWorld(tool, site + direction * (.0005f * i)); s.Step(); }
            tool.SetActivation(0); s.Step();
            Require(s.records.Skip(count).Count(r => r.action.verb == "cut" && r.action.tissueId == tissueId) == 1, $"one {tissueId} stroke is one cut");
            s.Park(tool); s.Step();
        }
        // A point on the tissue's longitudinal station whose nearest contact is that tissue (as OpenBodyDeliveryValidation).
        static bool FindSite(Rig s, InstrumentBehaviour tool, string tissueId, float alongMm, out Vector3 site, out Vector3 direction)
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
                    Vector3 radial = u * Mathf.Cos(angle * Mathf.Deg2Rad) + v * Mathf.Sin(angle * Mathf.Deg2Rad);
                    Vector3 point = origin + radial * (ring * .001f), tangent = Vector3.Cross(axis, radial).normalized;
                    if (!Valid(point)) continue;
                    if (!Enumerable.Range(1, 3).All(i => Valid(point + tangent * (.0005f * i)))) continue;
                    if (!target.DistanceFromBase(point, out float measured) || Mathf.Abs(measured - alongMm) > .01f) continue;
                    site = point; direction = tangent; return true;
                }
            return false;
        }

    }
}
