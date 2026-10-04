using System.Collections.Generic;
using Scalpal.Quest;
using Scalpal.Shell;
using UnityEngine;
using UnityEngine.Rendering;

namespace Scalpal.Briefing
{
    /// <summary>
    /// The briefing's ethereal stage. While the director is active and unfinished nothing of the OR renders: every
    /// renderer under the OR scene's other roots is forced off (Renderer.forceRenderingOff, so colliders, rigidbodies,
    /// active states and the OR's own enabled toggles are untouched and tools never drop or settle), and their lights and
    /// reflection probes are disabled. Kept: the briefing, this stage, the viewer's tracking rig (camera, controller hands),
    /// DialogueBox roots, and everything outside the OR scene (pause menu, handoff card, shell transition).
    /// VR: a gradient void sphere, a few hundred drifting motes, a soft key light and flat ambient over a dark clear colour.
    /// AR: the OR's virtual overlays are hidden the same way, passthrough stays as it is (no void), motes only.
    /// The director going inactive (pause, realign) restores the OR exactly; resuming hides it again. Finish and Skip
    /// restore it and fade the void out over it. Scene unload restores through OnDisable. A separate scene root, not
    /// under the director, so the closing fade outlives it.
    /// </summary>
    [ExecuteAlways]
    public sealed class BriefingStage : MonoBehaviour
    {
        public const float FadeSeconds = .8f, SweepSeconds = .5f, VoidRadius = 15, MoteRadius = 3.5f;
        public const int MaxMotes = 180;
        public static readonly Color VoidTop = new Color(.10f, .12f, .16f), VoidHorizon = new Color(.13f, .14f, .17f), VoidBottom = new Color(.012f, .013f, .018f);
        public static readonly Color Ambient = new Color(.34f, .37f, .43f), KeyColor = new Color(.80f, .84f, .92f);
        public BriefingDirector director;
        public Camera head;
        public bool Hidden { get; private set; }
        public bool Passthrough { get; private set; }
        public bool Fading => fade >= 0;
        public Renderer Void { get; private set; }
        public ParticleSystem Motes { get; private set; }
        public Light Key { get; private set; }
        public int HiddenRenderers => forced.Count;
        public int DisabledLights => disabled.Count;

        readonly HashSet<Renderer> forced = new HashSet<Renderer>();
        readonly HashSet<Behaviour> disabled = new HashSet<Behaviour>();
        readonly List<GameObject> roots = new List<GameObject>();
        readonly List<Renderer> scanRenderers = new List<Renderer>();
        readonly List<Light> scanLights = new List<Light>();
        readonly List<ReflectionProbe> scanProbes = new List<ReflectionProbe>();
        Material voidMaterial, moteMaterial;
        CameraClearFlags clearFlags;
        Color background, ambientSky, ambientEquator, ambientGround;
        AmbientMode ambientMode;
        float sweepClock, fade = -1;
        static readonly int AlphaId = Shader.PropertyToID("_Alpha"), ZTestId = Shader.PropertyToID("_ZTest");

        /// <summary>Builds the backdrop around the viewer. The OR stays visible until the first Sync.</summary>
        public static BriefingStage Create(BriefingDirector director, Transform viewer)
        {
            var stage = new GameObject("BriefingStage").AddComponent<BriefingStage>();
            stage.director = director;
            stage.head = viewer ? viewer.GetComponent<Camera>() : null;
            if (!stage.head) stage.head = Camera.main;
            var presentation = FindFirstObjectByType<NativePresentation>();
            stage.Passthrough = presentation ? presentation.passthrough : stage.head && stage.head.backgroundColor.a < 1;
            stage.transform.position = viewer ? viewer.position : director.transform.position;
            stage.Build(director.transform.forward);
            stage.ShowBackdrop(false);
            return stage;
        }

        void Build(Vector3 forward)
        {
            var source = Resources.Load<BriefingAtlasSource>(BriefingAtlas.SourceResource);
            if (!Passthrough && source && source.voidMaterial)
            {
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Discard(sphere.GetComponent<Collider>());
                sphere.name = "BriefingVoid"; sphere.transform.SetParent(transform, false); sphere.transform.localScale = Vector3.one * VoidRadius * 2;
                voidMaterial = new Material(source.voidMaterial) { name = "BriefingVoid (stage)" };
                voidMaterial.SetColor("_Top", VoidTop); voidMaterial.SetColor("_Horizon", VoidHorizon); voidMaterial.SetColor("_Bottom", VoidBottom);
                Void = sphere.GetComponent<Renderer>(); Void.sharedMaterial = voidMaterial;
                Void.shadowCastingMode = ShadowCastingMode.Off; Void.receiveShadows = false;
                Void.lightProbeUsage = LightProbeUsage.Off; Void.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            if (source && source.moteMaterial) BuildMotes(source.moteMaterial);
            // Soft key from above and behind the learner: hands and buttons read without the OR lights.
            Key = new GameObject("BriefingKeyLight").AddComponent<Light>();
            Key.transform.SetParent(transform, false);
            Key.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, Vector3.up).normalized * .7f + Vector3.down * .7f);
            Key.type = LightType.Directional; Key.color = KeyColor; Key.intensity = .55f; Key.shadows = LightShadows.None;
        }

        void BuildMotes(Material template)
        {
            var go = new GameObject("BriefingMotes");
            go.transform.SetParent(transform, false);
            Motes = go.AddComponent<ParticleSystem>();
            Motes.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = Motes.main;
            main.loop = true; main.prewarm = true; main.playOnAwake = false; main.maxParticles = MaxMotes;
            main.startLifetime = new ParticleSystem.MinMaxCurve(10, 16);
            main.startSpeed = new ParticleSystem.MinMaxCurve(.004f, .025f);
            main.startSize = new ParticleSystem.MinMaxCurve(.012f, .04f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(.78f, .82f, .90f, .32f), new Color(.92f, .92f, .96f, .18f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = Motes.emission; emission.rateOverTime = MaxMotes / 14f;
            // Spawn in a shell 1.9 to 3.5 m out, so no mote starts at the eyes or inside the anatomy.
            var shape = Motes.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = MoteRadius; shape.radiusThickness = .45f;
            var velocity = Motes.velocityOverLifetime; velocity.enabled = true; velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(0); velocity.y = new ParticleSystem.MinMaxCurve(.012f); velocity.z = new ParticleSystem.MinMaxCurve(0);
            var color = Motes.colorOverLifetime; color.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, .25f), new GradientAlphaKey(1, .7f), new GradientAlphaKey(0, 1) });
            color.color = gradient;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            moteMaterial = new Material(template) { name = "BriefingMote (stage)" };
            renderer.sharedMaterial = moteMaterial; renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>Follows the director: hides the OR while it is active and unfinished, restores it otherwise, and runs the
        /// closing fade. Update drives it with unscaled time; the director calls it on enable/disable/finish; validation directly.</summary>
        public void Sync(float dt)
        {
            bool wanted = director && !director.Finished && director.gameObject.activeInHierarchy;
            if (wanted && !Hidden) Hide();
            else if (!wanted && Hidden) Reveal(director && director.Finished);
            if (Hidden && (sweepClock += Mathf.Max(0, dt)) >= SweepSeconds) { sweepClock = 0; Sweep(); }
            if (Fading)
            {
                fade += Mathf.Max(0, dt);
                SetAlpha(1 - fade / FadeSeconds);
                if (fade >= FadeSeconds) Discard(gameObject);
            }
            else if (!Hidden && (!director || director.Finished)) Discard(gameObject);
        }

        void Update() { if (Application.isPlaying) Sync(Time.unscaledDeltaTime); }

        void Hide()
        {
            Hidden = true; sweepClock = 0; fade = -1;
            if (head)
            {
                clearFlags = head.clearFlags; background = head.backgroundColor;
                if (!Passthrough) { head.clearFlags = CameraClearFlags.SolidColor; head.backgroundColor = VoidBottom; }
            }
            ambientMode = RenderSettings.ambientMode; ambientSky = RenderSettings.ambientSkyColor;
            ambientEquator = RenderSettings.ambientEquatorColor; ambientGround = RenderSettings.ambientGroundColor;
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Ambient;
            if (voidMaterial) { voidMaterial.renderQueue = (int)RenderQueue.Background; voidMaterial.SetFloat(ZTestId, (float)CompareFunction.LessEqual); }
            SetAlpha(1);
            ShowBackdrop(true);
            Sweep();
        }

        /// <summary>Forces off every OR renderer and disables OR lights/probes not already off; repeats while hidden so
        /// OR views created mid-briefing (monitor, checklist, pointer boxes) stay hidden too.</summary>
        public void Sweep()
        {
            if (!Hidden) return;
            gameObject.scene.GetRootGameObjects(roots);
            foreach (var root in roots)
            {
                if (Kept(root.transform)) continue;
                root.GetComponentsInChildren(true, scanRenderers);
                foreach (var renderer in scanRenderers) if (!renderer.forceRenderingOff) { renderer.forceRenderingOff = true; forced.Add(renderer); }
                root.GetComponentsInChildren(true, scanLights);
                foreach (var light in scanLights) if (light.enabled) { light.enabled = false; disabled.Add(light); }
                root.GetComponentsInChildren(true, scanProbes);
                foreach (var probe in scanProbes) if (probe.enabled) { probe.enabled = false; disabled.Add(probe); }
            }
            roots.Clear(); scanRenderers.Clear(); scanLights.Clear(); scanProbes.Clear();
        }

        bool Kept(Transform root) => root == transform || (director && root == director.transform.root) || (head && root == head.transform.root)
            || root.GetComponentInChildren<DialogueBox>(true) != null;

        void Reveal(bool fadeOut)
        {
            Hidden = false;
            foreach (var renderer in forced) if (renderer) renderer.forceRenderingOff = false;
            foreach (var behaviour in disabled) if (behaviour) behaviour.enabled = true;
            forced.Clear(); disabled.Clear();
            if (head) { head.clearFlags = clearFlags; head.backgroundColor = background; }
            RenderSettings.ambientMode = ambientMode; RenderSettings.ambientSkyColor = ambientSky;
            RenderSettings.ambientEquatorColor = ambientEquator; RenderSettings.ambientGroundColor = ambientGround;
            if (Key) Key.enabled = false;
            if (!fadeOut) { ShowBackdrop(false); return; }
            // The OR is back underneath; the void now draws over everything and fades away.
            fade = 0;
            if (voidMaterial) { voidMaterial.renderQueue = (int)RenderQueue.Overlay; voidMaterial.SetFloat(ZTestId, (float)CompareFunction.Always); }
            if (Motes) Motes.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        void ShowBackdrop(bool visible)
        {
            if (Void) Void.gameObject.SetActive(visible);
            if (Key) { Key.gameObject.SetActive(visible); Key.enabled = visible; }
            if (!Motes) return;
            Motes.gameObject.SetActive(visible);
            if (visible && Application.isPlaying && !Motes.isPlaying) Motes.Play();
        }

        void SetAlpha(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            if (voidMaterial) voidMaterial.SetFloat(AlphaId, alpha);
            if (moteMaterial) moteMaterial.SetFloat(AlphaId, alpha);
        }

        // Scene unload or teardown mid-briefing: the OR, camera and ambient are restored exactly.
        void OnDisable() { if (Hidden) Reveal(false); }

        void OnDestroy()
        {
            if (voidMaterial) Discard(voidMaterial);
            if (moteMaterial) Discard(moteMaterial);
        }

        static void Discard(Object target)
        {
            if (!target) return;
            if (Application.isPlaying) Destroy(target); else DestroyImmediate(target);
        }
    }
}
