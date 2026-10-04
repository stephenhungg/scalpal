using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Scalpal.Handoff;
using Scalpal.Quest;
using Scalpal.Shell;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Scalpal.Briefing.Editor
{
    /// <summary>
    /// Editor gates for the pre-surgery briefing. Throws so -executeMethod fails. Runs the actual director, atlas,
    /// data texture, picker and HandoffFlow phase on the synthetic atlas and, when present, the real one.
    /// No scene, XR input, network, provider or headset.
    /// </summary>
    public static class BriefingValidation
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static int checks;
        static readonly List<string> failures = new List<string>();

        [Serializable] sealed class ManifestSteps { public ManifestStep[] steps; }
        [Serializable] sealed class ManifestStep { public string id; public int peelThroughLayer; public string[] focus; }

        [MenuItem("Scalpal/Briefing/Validate")]
        public static void Run()
        {
            checks = 0; failures.Clear();
            var source = BriefingBuild.Prepare();
            Assets(source);
            var steps = BriefingSteps.Load();
            Steps(steps);
            Voice(steps);
            Atlas(steps, true);
            if (source.mesh) { RealImport(source.mesh); Atlas(steps, false); }
            else Debug.LogWarning("SCALPAL_BRIEFING_REAL_ATLAS_ABSENT " + BriefingBuild.ModelPath);
            Stage();
            Flow();
            if (failures.Count > 0) throw new InvalidOperationException("Briefing validation failed (" + failures.Count + "):\n" + string.Join("\n", failures));
            Debug.Log("SCALPAL_BRIEFING_VERIFY_OK checks=" + checks + " real=" + (source.mesh != null));
        }

        static void Check(bool condition, string message)
        {
            checks++;
            if (!condition) failures.Add(message);
        }

        static void Assets(BriefingAtlasSource source)
        {
            Check(source && source.material && source.material.shader.name == "Scalpal/BriefingAtlas", "Resources handle carries the BriefingAtlas material");
            Check(source && source.material && !ShaderUtil.ShaderHasError(source.material.shader), "BriefingAtlas shader compiles");
            Check(source && source.voidMaterial && source.voidMaterial.shader.name == "Scalpal/BriefingVoid" && !ShaderUtil.ShaderHasError(source.voidMaterial.shader)
                && source.moteMaterial && source.moteMaterial.shader.name == "Scalpal/BriefingMote" && !ShaderUtil.ShaderHasError(source.moteMaterial.shader),
                "Resources handle carries the compiled void and mote backdrop materials (they ship without Shader.Find)");
            if (File.Exists(BriefingBuild.ModelPath))
            {
                var importer = (ModelImporter)AssetImporter.GetAtPath(BriefingBuild.ModelPath);
                Check(source.mesh && source.mesh.isReadable, "real atlas mesh is wired and readable (the picker reads it on device)");
                Check(importer && importer.meshOptimizationFlags == 0 && !importer.generateSecondaryUV && importer.indexFormat == ModelImporterIndexFormat.UInt32,
                    "atlas import keeps triangle order, uv2 part indices and 32-bit indices");
            }
            // The briefing must never wait on the network: no request code in its runtime.
            foreach (var file in Directory.GetFiles(BriefingBuild.Root + "/Runtime", "*.cs"))
                Check(!File.ReadAllText(file).Contains("UnityWebRequest"), "briefing runtime has no network dependency: " + Path.GetFileName(file));
        }

        static void Steps(BriefingStep[] steps)
        {
            var expected = new[] { "mark_incision", "incise_skin", "open_fascia", "split_muscle", "open_peritoneum", "deliver_appendix", "divide_mesoappendix", "ligate_base", "inspect_clean", "close" };
            Check(steps.Select(s => s.id).SequenceEqual(expected), "steps follow the open appendectomy expected path in surgical order: " + string.Join(",", steps.Select(s => s.id)));
            var manifest = Resources.Load<TextAsset>(BriefingAtlas.ManifestResource);
            var beats = manifest ? JsonUtility.FromJson<ManifestSteps>(manifest.text)?.steps : null;
            if (beats != null && beats.Length > 0)
                Check(beats.Select(b => b.id).SequenceEqual(steps.Select(s => s.id)), "step ids match the atlas beats in briefing_parts.json");
            // Jarvis's lines are the coach's brief.<stepId> narration, verbatim (one source of text for both).
            var coach = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../services/preop/src/briefing.ts"));
            if (File.Exists(coach))
            {
                var authored = Regex.Matches(File.ReadAllText(coach), "^\\s+(\\w+): \"(.*)\",$", RegexOptions.Multiline).Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
                foreach (var step in steps) Check(authored.TryGetValue(step.id, out var line) && line == step.line, "line for " + step.id + " matches services/preop/src/briefing.ts");
            }
            foreach (var step in steps)
            {
                Check(!string.IsNullOrWhiteSpace(step.title) && !string.IsNullOrWhiteSpace(step.line) && step.line.Split(' ').Length <= 40, "step " + step.id + " has a title and a short line");
                Check(BriefingDirector.ReadingSeconds(step.line) >= 4 && BriefingDirector.ReadingSeconds(step.line) <= 12, "caption-only pacing is bounded for " + step.id);
            }
        }

        static void Voice(BriefingStep[] steps)
        {
            float total = 0;
            foreach (var step in steps)
            {
                var clip = BriefingVoice.Find(step);
                Check(clip && clip.length > .5f, "bundled Jarvis clip rendered from the exact line: " + step.id);
                if (!clip) continue;
                total += Mathf.Max(BriefingDirector.MinimumStepSeconds, clip.length + BriefingDirector.ClipTailSeconds);
                var importer = (AudioImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip));
                Check(importer.defaultSampleSettings.loadType == AudioClipLoadType.DecompressOnLoad, "clip samples are readable by PlayLocalSpeech: " + step.id);
            }
            Check(total >= 45 && total <= 100, "voiced briefing lasts about a minute and a half (" + total.ToString("0.0") + " s)");
            Debug.Log("SCALPAL_BRIEFING_VOICED_SECONDS " + total.ToString("0.0"));
        }

        static void RealImport(Mesh mesh)
        {
            var file = Resources.Load<TextAsset>(BriefingAtlas.ManifestResource);
            var manifest = JsonUtility.FromJson<BriefingPartManifest>(file.text);
            var vertices = mesh.vertices; var uv2 = new List<Vector2>(); mesh.GetUVs(1, uv2); var triangles = mesh.triangles;
            Check(mesh.subMeshCount == 1 && uv2.Count == vertices.Length, "real atlas is one submesh with a part index per vertex");
            float worst = 0; int ordered = 0;
            foreach (var part in manifest.parts)
            {
                Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue; bool same = true;
                for (int t = part.triStart; t < part.triStart + part.triCount && t * 3 + 2 < triangles.Length; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        int v = triangles[t * 3 + k];
                        lo = Vector3.Min(lo, vertices[v]); hi = Vector3.Max(hi, vertices[v]);
                        if (Mathf.RoundToInt(uv2[v].x) != part.index) same = false;
                    }
                if (same) ordered++;
                worst = Mathf.Max(worst, (lo - new Vector3(part.min[0], part.min[1], part.min[2])).magnitude, (hi - new Vector3(part.max[0], part.max[1], part.max[2])).magnitude);
            }
            Check(ordered == manifest.parts.Length, "imported triangle ranges keep their part (triStart/triCount): " + ordered + "/" + manifest.parts.Length);
            Check(worst < .002f, "imported part bounds match briefing_parts.json within 2 mm (worst " + (worst * 1000).ToString("0.00") + " mm)");
            Debug.Log("SCALPAL_BRIEFING_REAL_ATLAS parts=" + manifest.parts.Length + " tris=" + triangles.Length / 3 + " boundsErrorMm=" + (worst * 1000).ToString("0.00"));
        }

        static void Atlas(BriefingStep[] steps, bool synthetic)
        {
            string label = synthetic ? "synthetic" : "real";
            var viewer = new GameObject("BriefingValidationViewer").transform;
            var space = new GameObject("BriefingValidationSpace").transform; viewer.SetParent(space, false);
            viewer.position = new Vector3(0, 1.6f, 0);
            BriefingDirector director = null;
            try
            {
                director = BriefingDirector.Begin(viewer, synthetic);
                var atlas = director.Atlas;
                Check(atlas.Synthetic == synthetic, label + ": loaded the requested atlas");
                var renderers = atlas.GetComponentsInChildren<Renderer>(true);
                Check(renderers.Length == 1 && renderers[0].sharedMaterials.Length == 1 && atlas.Mesh.subMeshCount == 1,
                    label + ": one renderer, one material, one submesh for all " + atlas.Parts.Length + " parts");
                Check(atlas.Data.width == atlas.Parts.Length && atlas.Data.height == BriefingAtlas.Rows && atlas.Data.filterMode == FilterMode.Point
                    && atlas.Material.GetTexture("_PartTex") == atlas.Data, label + ": data texture is one point-filtered column per part, bound to the material");
                foreach (var step in steps)
                    foreach (var id in step.focus.Concat(step.peel))
                        Check(atlas.TryFind(id, out _), label + ": step " + step.id + " names a known part " + id);
                Check(Vector3.Dot(viewer.position - atlas.transform.position, atlas.transform.TransformDirection(atlas.Anterior)) > 0, label + ": the abdomen's front faces the learner");
                HashSet<int> previous = null;
                for (int s = 0; s < steps.Length; s++)
                {
                    director.Enter(s); atlas.Snap();
                    var step = steps[s];
                    var peeled = new HashSet<int>(); atlas.PeelThrough(step.peelThroughLayer, peeled); atlas.Resolve(step.peel, peeled);
                    var focus = new HashSet<int>(); atlas.Resolve(step.focus, focus); peeled.ExceptWith(focus);
                    if (previous != null && s < steps.Length - 1) Check(previous.IsSubsetOf(peeled), label + ": peel is cumulative at " + step.id);
                    previous = peeled;
                    bool visibleSet = true, focusSet = true;
                    // Focus scale: 2.5x, limited so the lifted group stays within FocusMaximumSize (never shrunk).
                    Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
                    foreach (int i in focus) { lo = Vector3.Min(lo, atlas.Parts[i].min); hi = Vector3.Max(hi, atlas.Parts[i].max); }
                    var size = hi - lo; float extent = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                    float scale = focus.Count == 0 ? 1 : Mathf.Max(1, Mathf.Min(BriefingDirector.FocusScale, BriefingDirector.FocusMaximumSize / extent));
                    for (int i = 0; i < atlas.Parts.Length; i++)
                    {
                        if (atlas.Parts[i].triangles.Length == 0) continue;
                        var state = atlas.Current[i];
                        if (peeled.Contains(i)) visibleSet &= !atlas.IsVisible(i) && state.alpha == 0;
                        else visibleSet &= atlas.IsVisible(i) && state.alpha == 1;
                        if (focus.Contains(i)) focusSet &= Mathf.Abs(state.scale - scale) < 1e-4f && state.glow == 1 && Vector3.Dot(state.lift, atlas.Anterior) >= BriefingDirector.MinimumLift - 1e-4f;
                        else focusSet &= state.scale == 1 && state.glow == 0 && state.lift == Vector3.zero;
                    }
                    Check(visibleSet, label + ": " + step.id + " shows exactly the unpeeled parts (" + peeled.Count + " peeled)");
                    Check(focusSet && director.Step.id == step.id, label + ": " + step.id + " lifts, scales (" + scale.ToString("0.00") + "x) and lights only its " + focus.Count + " focus parts");
                    if (focus.Count > 0)
                    {
                        // A spinning focus must never swing back into the torso: at every spin angle its scaled bounds
                        // stay in front of everything left in place.
                        var pivot = (lo + hi) * .5f; float reach = 0, front = float.MinValue;
                        foreach (int i in focus) for (int c = 0; c < 8; c++)
                        {
                            var corner = new Vector3((c & 1) == 0 ? atlas.Parts[i].min.x : atlas.Parts[i].max.x, (c & 2) == 0 ? atlas.Parts[i].min.y : atlas.Parts[i].max.y, (c & 4) == 0 ? atlas.Parts[i].min.z : atlas.Parts[i].max.z);
                            reach = Mathf.Max(reach, (corner - pivot).magnitude * scale);
                        }
                        for (int i = 0; i < atlas.Parts.Length; i++)
                            if (!peeled.Contains(i) && !focus.Contains(i) && atlas.Parts[i].triangles.Length > 0) front = Mathf.Max(front, Vector3.Dot(atlas.Parts[i].max, atlas.Anterior));
                        float lifted = Vector3.Dot(pivot, atlas.Anterior) + Vector3.Dot(atlas.Current[focus.First()].lift, atlas.Anterior);
                        bool spins = atlas.Target[focus.First()].spin;
                        Check(!spins || front == float.MinValue || lifted - reach >= front - 1e-4f, label + ": " + step.id + " spins clear of the torso (gap " + ((lifted - reach - front) * 1000).ToString("0") + " mm)");
                        if (step.id == "deliver_appendix" || step.id == "divide_mesoappendix") Check(spins, label + ": the organ focus spins at " + step.id);
                    }
                    if (step.id == "deliver_appendix" || step.id == "divide_mesoappendix") Check(scale >= 1.5f, label + ": small focus structures are enlarged at " + step.id + " (" + scale.ToString("0.00") + "x)");
                    Rows(atlas, label + " " + step.id);
                    Picking(director, focus, peeled, viewer, label + " " + step.id);
                }
                // Spinning focus: the box and the pick follow the animation.
                director.Enter(Array.FindIndex(steps, x => x.id == "ligate_base")); atlas.Snap();
                for (int i = 0; i < 30; i++) atlas.Step(1f / 15);
                var spinFocus = new HashSet<int>(); atlas.Resolve(director.Step.focus, spinFocus);
                Check(spinFocus.All(i => atlas.Current[i].angle > .5f), label + ": focus spins about its centre");
                Rows(atlas, label + " spinning");
                Picking(director, spinFocus, new HashSet<int>(), viewer, label + " spinning");
                int target = spinFocus.First();
                director.Picker.Show(target);
                var corners = new Vector3[8]; atlas.Corners(target, corners);
                var path = new Vector3[16]; director.Picker.Box.GetPositions(path);
                Check(director.Picker.Box.enabled && corners.All(c => path.Any(p => (p - c).sqrMagnitude < 1e-8f)) && director.Picker.Label.text == atlas.Parts[target].name,
                    label + ": green box spans the animated part and the label names it");
                Check(director.Picker.Box.startColor == BriefingPicker.BoxGreen && atlas.Target[target].highlight == 1, label + ": box is green and the part is highlighted");
                director.Picker.Show(-1);
                Check(!director.Picker.Box.enabled && atlas.Target[target].highlight == 0, label + ": pointing away clears box, label and highlight");
            }
            finally
            {
                if (director && director.Stage) UnityEngine.Object.DestroyImmediate(director.Stage.gameObject);
                if (director) UnityEngine.Object.DestroyImmediate(director.gameObject);
                UnityEngine.Object.DestroyImmediate(space.gameObject);
            }
        }

        // The texture rows the shader reads equal the CPU state (half precision).
        static void Rows(BriefingAtlas atlas, string label)
        {
            bool match = true;
            for (int i = 0; i < atlas.Parts.Length; i++)
            {
                var s = atlas.Current[i]; var offset = atlas.Offset(i); var center = atlas.Parts[i].center;
                match &= Near(atlas.Data.GetPixel(i, 0), new Color(offset.x, offset.y, offset.z, atlas.Parts[i].triangles.Length > 0 ? s.alpha : 0));
                match &= Near(atlas.Data.GetPixel(i, 1), new Color(s.scale, s.glow, s.angle, s.highlight));
                match &= Near(atlas.Data.GetPixel(i, 2), new Color(center.x, center.y, center.z, 1));
                // The shader transform equals the picker's: Ry(angle)*((p-c)*scale)+c+offset.
                var probe = atlas.Parts[i].max;
                var shader = BriefingAtlas.Spin(s.angle) * ((probe - center) * s.scale) + center + offset;
                match &= (shader - atlas.Apply(i, probe)).sqrMagnitude < 1e-10f && (atlas.Unapply(i, atlas.Apply(i, probe)) - probe).sqrMagnitude < 1e-8f;
            }
            Check(match, label + ": data texture rows match the part state");
        }

        static bool Near(Color a, Color b)
        {
            for (int k = 0; k < 4; k++) if (Mathf.Abs(a[k] - b[k]) > 1.5e-3f + Mathf.Abs(b[k]) * 2e-3f) return false;
            return true;
        }

        static void Picking(BriefingDirector director, HashSet<int> focus, HashSet<int> peeled, Transform viewer, string label)
        {
            var atlas = director.Atlas;
            var front = atlas.transform.TransformDirection(atlas.Anterior).normalized;
            foreach (int part in focus)
            {
                var aim = atlas.transform.TransformPoint(atlas.Apply(part, FrontPoint(atlas, part)));
                int hit = director.Picker.Pick(new Ray(aim + front * .8f, -front), out _);
                Check(focus.Count == 1 ? hit == part : focus.Contains(hit), label + ": laser at the moved, scaled focus " + atlas.Parts[part].id + " picks it (got " + (hit >= 0 ? atlas.Parts[hit].id : "none") + ")");
                // Where the part rested before the lift, the laser no longer finds it from the front.
                var rest = atlas.transform.TransformPoint(FrontPoint(atlas, part));
                var restHit = director.Picker.Pick(new Ray(rest + atlas.transform.right * .8f, -atlas.transform.right), out var restPoint);
                Check(restHit != part || (atlas.transform.InverseTransformPoint(restPoint) - FrontPoint(atlas, part)).magnitude > .005f,
                    label + ": the picker uses the animated transform, not the rest pose, for " + atlas.Parts[part].id);
            }
            foreach (int part in peeled.Take(3))
            {
                var rest = atlas.transform.TransformPoint(atlas.Apply(part, FrontPoint(atlas, part)));
                Check(director.Picker.Pick(new Ray(rest + front * .8f, -front), out _) != part, label + ": a peeled part cannot be picked: " + atlas.Parts[part].id);
            }
            if (focus.Count == 0)
            {
                // No focus: the frontmost unpeeled part straight ahead is identified.
                int hit = director.Picker.Pick(new Ray(viewer.position, (atlas.transform.position - viewer.position).normalized), out _);
                Check(hit >= 0 && !peeled.Contains(hit), label + ": the laser identifies a visible part (" + (hit >= 0 ? atlas.Parts[hit].id : "none") + ")");
            }
        }

        // A point on the part's surface near its centre that faces the learner (frontmost triangle centroid near the centre axis).
        static Vector3 FrontPoint(BriefingAtlas atlas, int part)
        {
            var p = atlas.Parts[part]; var vertices = atlas.Mesh.vertices;
            float radius = Mathf.Max(.002f, .35f * Mathf.Min(p.Size.x, p.Size.y));
            Vector3 best = p.center; float bestScore = float.MinValue; float nearest = float.MaxValue; Vector3 fallback = p.center;
            for (int t = 0; t < p.triangles.Length; t += 3)
            {
                var c = (vertices[p.triangles[t]] + vertices[p.triangles[t + 1]] + vertices[p.triangles[t + 2]]) / 3;
                float axial = new Vector2(c.x - p.center.x, c.y - p.center.y).magnitude;
                if (axial < nearest) { nearest = axial; fallback = c; }
                if (axial <= radius && c.z > bestScore) { bestScore = c.z; best = c; }
            }
            return bestScore > float.MinValue ? best : fallback;
        }

        static void Flow()
        {
            Check(HandoffFlow.AfterFit(false, false) == "briefing" && HandoffFlow.AfterFit(false, true) == "timeout" && HandoffFlow.AfterFit(true, false) == "paused",
                "a confirmed fit opens the briefing once, then the Time-Out; a started attempt resumes paused");
            var oldTicket = HandoffRun.Current;
            var current = typeof(HandoffRun).GetProperty(nameof(HandoffRun.Current));
            var host = new GameObject("BriefingFlowFixture"); host.SetActive(false);
            try
            {
                current.SetValue(null, new HandoffTicket { presentationMode = "virtual" });
                var flow = host.AddComponent<HandoffFlow>();
                var card = UnityEngine.Object.Instantiate(Resources.Load<HandoffCard>("HandoffCard"), host.transform);
                Set(flow, "card", card);
                foreach (bool skip in new[] { true, false })
                {
                    string mode = skip ? "skip" : "finish";
                    Call(flow, "ResetRunState"); Call(flow, "SetPhase", "briefing"); Call(flow, "Briefing");
                    var director = Get<BriefingDirector>(flow, "briefing");
                    Check(director && Get<string>(flow, "phase") == "briefing" && !card.Visible && director.StepIndex == 0, mode + ": the briefing spawns in place of the Time-Out card");
                    if (skip) director.Skip();
                    else
                    {
                        // Caption timing alone (no clip, no network) carries the briefing to its end.
                        for (int i = 0; i < 400 && !director.Finished; i++) director.Tick(.5f);
                        Check(director.Finished && !director.Skipped && director.StepIndex == director.Steps.Length - 1, mode + ": every step plays and the briefing ends on its own");
                    }
                    Call(flow, "Briefing");
                    Check(Get<string>(flow, "phase") == "timeout" && Get<bool>(flow, "briefingDone") && !Get<BriefingDirector>(flow, "briefing"), mode + ": the Time-Out follows the briefing");
                    Call(flow, "SetPhase", "briefing"); Call(flow, "Briefing");
                    Check(Get<string>(flow, "phase") == "timeout" && !Get<BriefingDirector>(flow, "briefing"), mode + ": the briefing plays once per OR entry");
                }
                // Next on the last step goes to the Time-Out as well.
                Call(flow, "ResetRunState"); Call(flow, "SetPhase", "briefing"); Call(flow, "Briefing");
                var last = Get<BriefingDirector>(flow, "briefing");
                for (int i = 0; i < last.Steps.Length; i++) last.Next();
                Call(flow, "Briefing");
                Check(last.Finished && !last.Skipped && Get<string>(flow, "phase") == "timeout", "Next through every step reaches the Time-Out");
            }
            finally
            {
                ClearBriefings();
                UnityEngine.Object.DestroyImmediate(host);
                current.SetValue(null, oldTicket);
            }
        }

        static void ClearBriefings()
        {
            foreach (var stage in UnityEngine.Object.FindObjectsByType<BriefingStage>(FindObjectsInactive.Include, FindObjectsSortMode.None)) UnityEngine.Object.DestroyImmediate(stage.gameObject);
            foreach (var director in UnityEngine.Object.FindObjectsByType<BriefingDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None)) UnityEngine.Object.DestroyImmediate(director.gameObject);
        }

        /// <summary>Everything that decides what the head camera draws: per renderer forceRenderingOff, enabled and active;
        /// per light/probe enabled; per collider/rigidbody/GameObject physics state; camera clear; ambient.</summary>
        sealed class RenderState
        {
            public readonly Dictionary<Renderer, (bool forced, bool enabled, bool active)> renderers = new Dictionary<Renderer, (bool, bool, bool)>();
            public readonly Dictionary<Behaviour, bool> behaviours = new Dictionary<Behaviour, bool>();
            public readonly Dictionary<Component, string> physics = new Dictionary<Component, string>();
            public CameraClearFlags flags; public Color background, sky, equator, ground; public AmbientMode mode;

            public static RenderState Capture(Scene scene, Camera head)
            {
                var state = new RenderState { flags = head.clearFlags, background = head.backgroundColor, mode = RenderSettings.ambientMode,
                    sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor };
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true)) state.renderers[r] = (r.forceRenderingOff, r.enabled, r.gameObject.activeSelf);
                    foreach (var l in root.GetComponentsInChildren<Light>(true)) state.behaviours[l] = l.enabled;
                    foreach (var p in root.GetComponentsInChildren<ReflectionProbe>(true)) state.behaviours[p] = p.enabled;
                    foreach (var c in root.GetComponentsInChildren<Collider>(true)) state.physics[c] = c.enabled + "/" + c.gameObject.activeInHierarchy;
                    foreach (var b in root.GetComponentsInChildren<Rigidbody>(true)) state.physics[b] = b.isKinematic + "/" + b.useGravity + "/" + b.position + "/" + b.gameObject.activeInHierarchy;
                }
                return state;
            }

            public bool PhysicsSame(RenderState now) => physics.All(p => p.Key && now.physics.TryGetValue(p.Key, out var v) && v == p.Value);

            public bool Same(Scene scene, Camera head, out string difference)
            {
                var now = Capture(scene, head);
                difference = renderers.Where(r => r.Key && (!now.renderers.TryGetValue(r.Key, out var v) || v != r.Value)).Select(r => r.Key.name).FirstOrDefault()
                    ?? behaviours.Where(b => b.Key && (!now.behaviours.TryGetValue(b.Key, out var v) || v != b.Value)).Select(b => b.Key.name).FirstOrDefault()
                    ?? (!PhysicsSame(now) ? "physics" : null)
                    ?? (now.flags != flags || now.background != background ? "camera clear" : null)
                    ?? (now.mode != mode || now.sky != sky || now.equator != equator || now.ground != ground ? "ambient" : null);
                return difference == null;
            }
        }

        static bool Rendered(Renderer r) => r && r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy;

        /// <summary>
        /// The real NativeSession scene: during the briefing nothing of the OR renders (theatre, patient, tools, workbench,
        /// monitors, lights, probes, and OR views created mid-briefing), the ethereal void and motes are present, hands
        /// and Jarvis's dialogue box stay visible and nothing is deactivated or physically changed. Pause restores the OR
        /// exactly and resume hides it again; finish, skip and teardown restore the exact prior render state and the
        /// backdrop goes away after its fade. AR keeps passthrough (no void, clear camera) and hides only the overlays.
        /// </summary>
        static void Stage()
        {
            var previous = EditorSceneManager.GetSceneManagerSetup();
            var oldTicket = HandoffRun.Current;
            var current = typeof(HandoffRun).GetProperty(nameof(HandoffRun.Current));
            try
            {
                current.SetValue(null, null);
                var scene = EditorSceneManager.OpenScene(Scalpal.Quest.Editor.NativeSessionBuild.ScenePath, OpenSceneMode.Single);
                var presentation = scene.GetRootGameObjects().Select(root => root.GetComponentInChildren<NativePresentation>(true)).FirstOrDefault(found => found);
                Check(presentation && presentation.headCamera, "stage: the OR scene has its presentation and head camera");
                if (!presentation || !presentation.headCamera) return;
                var head = presentation.headCamera; var rig = head.transform.root;
                // ControllerHandPose enables the hand skins while the controllers track; simulate live controllers.
                var hands = rig.GetComponentsInChildren<Renderer>(true).Where(r => r.gameObject.activeInHierarchy).ToArray();
                foreach (var hand in hands) hand.enabled = true;
                Check(hands.Length > 0, "stage: the tracking rig has visible controller hands to keep");
                // Jarvis's dialogue box lives in the OR scene as its own root; it must stay visible.
                var dialogue = new GameObject("BriefingStageDialogueFixture"); dialogue.AddComponent<DialogueBox>();
                var dialogueCard = GameObject.CreatePrimitive(PrimitiveType.Quad); dialogueCard.transform.SetParent(dialogue.transform, false);
                var session = scene.GetRootGameObjects().Select(root => root.GetComponentInChildren<NativeCaseSession>(true)).FirstOrDefault(found => found);
                foreach (bool passthrough in new[] { false, true })
                {
                    string mode = passthrough ? "AR" : "VR";
                    presentation.passthrough = passthrough; presentation.Apply();
                    foreach (string ending in passthrough ? new[] { "skip" } : new[] { "finish", "skip", "teardown" })
                    {
                        string label = "stage " + mode + " " + ending;
                        var baseline = RenderState.Capture(scene, head);
                        var director = BriefingDirector.Begin(head.transform);
                        var stage = director.Stage;
                        Check(stage && stage.Hidden && stage.Passthrough == passthrough, label + ": the briefing hides the OR from its first frame");
                        Hidden(scene, rig, dialogue, director, stage, label);
                        Check(hands.All(Rendered) && Rendered(dialogueCard.GetComponent<Renderer>()) && Rendered(director.Atlas.Renderer)
                            && director.GetComponentsInChildren<Renderer>(true).All(r => !r.forceRenderingOff),
                            label + ": controller hands, the dialogue box and the briefing (atlas, buttons, green box, rays) stay visible");
                        Check(baseline.PhysicsSame(RenderState.Capture(scene, head)), label + ": no OR object is deactivated and no collider or rigidbody changes (tools cannot drop)");
                        if (passthrough)
                            Check(!stage.Void && head.backgroundColor == baseline.background && head.clearFlags == baseline.flags && stage.Motes,
                                label + ": passthrough stays as it is (no opaque void, clear camera); only faint motes join the briefing");
                        else
                        {
                            Check(stage.Void && Rendered(stage.Void) && stage.Void.sharedMaterial.shader.name == "Scalpal/BriefingVoid" && !stage.Void.GetComponent<Collider>()
                                && BriefingStage.VoidRadius < head.farClipPlane && head.clearFlags == CameraClearFlags.SolidColor && head.backgroundColor == BriefingStage.VoidBottom,
                                label + ": an ethereal gradient void (no collider, inside the far plane) surrounds the briefing over a dark clear colour");
                            Check(stage.Motes && stage.Motes.main.maxParticles <= 300 && stage.Motes.GetComponent<ParticleSystemRenderer>().sharedMaterial.shader.name == "Scalpal/BriefingMote"
                                && stage.Key && stage.Key.enabled && stage.Key.shadows == LightShadows.None && RenderSettings.ambientMode == AmbientMode.Flat,
                                label + ": one cheap mote system, a soft shadowless key light and flat ambient light the anatomy");
                        }
                        // An OR view created mid-briefing (vitals monitor, checklist, pointer box) is hidden by the next sweep.
                        var late = GameObject.CreatePrimitive(PrimitiveType.Cube); late.name = "LateOperatingRoomView";
                        UnityEngine.Object.DestroyImmediate(late.GetComponent<Collider>());
                        if (session) late.transform.SetParent(session.transform, false);
                        stage.Sync(BriefingStage.SweepSeconds);
                        Check(!Rendered(late.GetComponent<Renderer>()), label + ": an OR view spawned during the briefing is hidden too");
                        if (!passthrough && ending == "finish")
                        {
                            // Pause/realign deactivates the briefing: the OR returns exactly; resuming hides it again.
                            director.gameObject.SetActive(false); stage.Sync(0);
                            bool restored = baseline.Same(scene, head, out var paused);
                            Check(!stage.Hidden && restored && Rendered(late.GetComponent<Renderer>()) && !stage.Void.gameObject.activeInHierarchy,
                                label + ": pausing restores the OR exactly and hides the void (" + (paused ?? "same") + ")");
                            director.gameObject.SetActive(true); stage.Sync(0);
                            Check(stage.Hidden && !Rendered(late.GetComponent<Renderer>()) && Rendered(stage.Void), label + ": resuming the briefing hides the OR again");
                            Hidden(scene, rig, dialogue, director, stage, label + " resumed");
                        }
                        UnityEngine.Object.DestroyImmediate(late);
                        if (ending == "teardown")
                        {
                            // Scene unload mid-briefing tears the stage down: OnDisable restores everything.
                            UnityEngine.Object.DestroyImmediate(stage.gameObject);
                            Check(baseline.Same(scene, head, out var torn), label + ": tearing the briefing down mid-step restores the OR exactly (" + (torn ?? "same") + ")");
                            UnityEngine.Object.DestroyImmediate(director.gameObject);
                            continue;
                        }
                        if (ending == "skip") director.Skip(); else for (int i = 0; i < director.Steps.Length && !director.Finished; i++) director.Next();
                        bool same = baseline.Same(scene, head, out var difference);
                        Check(director.Finished && same, label + ": the OR is restored to its exact prior render state at once (" + (difference ?? "same") + ")");
                        Check(!passthrough ? stage.Fading && stage.Void.sharedMaterial.renderQueue == (int)RenderQueue.Overlay : stage.Fading, label + ": the void fades out over the restored OR");
                        stage.Sync(BriefingStage.FadeSeconds * .5f);
                        Check(stage && stage.Fading, label + ": the fade takes a moment");
                        stage.Sync(BriefingStage.FadeSeconds);
                        Check(!stage && UnityEngine.Object.FindObjectsByType<BriefingStage>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0
                            && baseline.Same(scene, head, out difference), label + ": after the fade the backdrop is gone and the OR renders as before (" + (difference ?? "same") + ")");
                        UnityEngine.Object.DestroyImmediate(director.gameObject);
                    }
                }
            }
            finally
            {
                ClearBriefings();
                current.SetValue(null, oldTicket);
                // Drop the fixture-modified scene without saving, then return to whatever was open before.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (previous.Any(item => item.isLoaded && item.isActive)) EditorSceneManager.RestoreSceneManagerSetup(previous);
            }
        }

        // Every OR root renderer, light and probe is off; the named theatre, patient, tool and monitor roots have geometry.
        static void Hidden(Scene scene, Transform rig, GameObject dialogue, BriefingDirector director, BriefingStage stage, string label)
        {
            var or = scene.GetRootGameObjects().Where(root => root.transform != rig && root != dialogue && root != director.gameObject && root != stage.gameObject).ToArray();
            var shown = or.SelectMany(root => root.GetComponentsInChildren<Renderer>(true)).Where(Rendered).Select(r => r.name).ToArray();
            Check(shown.Length == 0, label + ": no OR renderer draws during the briefing (" + string.Join(",", shown.Take(5)) + ")");
            Check(or.SelectMany(root => root.GetComponentsInChildren<Light>(true)).All(l => !l.enabled || !l.gameObject.activeInHierarchy)
                && or.SelectMany(root => root.GetComponentsInChildren<ReflectionProbe>(true)).All(p => !p.enabled || !p.gameObject.activeInHierarchy),
                label + ": OR lights and reflection probes are off");
            foreach (var name in new[] { "VirtualOperatingRoom", "PatientRoot", "Workbench", "VirtualLaparoscopeMonitor", "inst_scalpel", "inst_atraumatic_grasper" })
            {
                var root = or.FirstOrDefault(found => found.name == name);
                var all = root ? root.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
                Check(all.Length > 0 && all.All(r => r.forceRenderingOff || !r.enabled || !r.gameObject.activeInHierarchy), label + ": " + name + " is not rendered (" + all.Length + " renderers)");
            }
        }

        static void Set(object target, string field, object value) => target.GetType().GetField(field, Private).SetValue(target, value);
        static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private).GetValue(target);
        static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Private).Invoke(target, args);
    }
}
