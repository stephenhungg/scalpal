using System.Collections.Generic;
using Scalpal.Brand;
using Scalpal.Shell;
using Scalpal.Voice;
using TMPro;
using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>
    /// Pre-surgery briefing between the theatre card and the Time-Out: Jarvis walks the open appendectomy one step at a
    /// time over a floating abdomen. Each step peels the covering layers, lifts/scales/spins/glows the focus structure
    /// and plays one bundled line. Trigger or Next advances, Skip ends; steps also advance on their own (clip length or a
    /// reading timer). No network: lines are bundled clips, and missing clips fall back to captions on a timer.
    /// While it runs the OR is hidden behind an ethereal void (BriefingStage). HandoffFlow owns the lifecycle and reads Finished.
    /// </summary>
    public sealed class BriefingDirector : MonoBehaviour
    {
        public const float Distance = .8f, Drop = .18f, ModelScale = .75f, MinimumLift = .06f, MaximumLift = .5f, LiftClearance = .02f, FocusScale = 2.5f, FocusMaximumSize = .32f, PeelDistance = .12f;
        public const float MinimumStepSeconds = 3.5f, ClipTailSeconds = 1.1f;
        public BriefingAtlas Atlas { get; private set; }
        public BriefingPicker Picker { get; private set; }
        public BriefingStage Stage { get; private set; }
        public BriefingStep[] Steps { get; private set; } = System.Array.Empty<BriefingStep>();
        public int StepIndex { get; private set; } = -1;
        public BriefingStep Step => StepIndex >= 0 && StepIndex < Steps.Length ? Steps[StepIndex] : null;
        public bool Finished { get; private set; }
        public bool Skipped { get; private set; }
        public float StepElapsed { get; private set; }
        public float StepDuration { get; private set; }
        public bool SpokeClip { get; private set; }
        public Transform viewer;
        public QuestJarvisVoice voice;
        readonly HashSet<int> peeled = new HashSet<int>(), focused = new HashSet<int>();
        readonly ScalpalPointerHand[] pointers = { new ScalpalPointerHand(0, "BriefingRayLeft"), new ScalpalPointerHand(1, "BriefingRayRight") };
        readonly ScalpalPressGate[] advance = { new ScalpalPressGate(), new ScalpalPressGate() };
        Transform controls;
        TextMeshPro caption;
        ShellButton next;
        AudioSource fallbackSpeaker;

        /// <summary>Spawns the briefing in front of the viewer (scene object: it ends with the OR scene).</summary>
        public static BriefingDirector Begin(Transform viewer, bool forceSynthetic = false)
        {
            var root = new GameObject("PreSurgeryBriefing");
            var director = root.AddComponent<BriefingDirector>();
            director.viewer = viewer;
            director.voice = FindFirstObjectByType<QuestJarvisVoice>();
            director.Steps = BriefingSteps.Load();
            director.Atlas = BriefingAtlas.Create(root.transform, forceSynthetic);
            director.Place();
            director.Picker = BriefingPicker.Create(director.Atlas, root.transform, viewer);
            director.BuildControls();
            director.Stage = BriefingStage.Create(director, viewer);
            if (director.Steps.Length == 0) { director.Finish(false); return director; }
            director.Enter(0);
            director.Stage.Sync(0);
            return director;
        }

        void Place()
        {
            var forward = Vector3.forward; var origin = Vector3.zero;
            if (viewer && ScalpalPlacement.TryFlatForward(viewer, out forward)) origin = viewer.position + forward * Distance + Vector3.down * Drop;
            transform.SetPositionAndRotation(origin, ScalpalPlacement.Level(forward));
            // Yaw the mesh so its anterior (wall in front of organs) faces the learner, centred on the root.
            var yaw = Quaternion.LookRotation(Vector3.back) * Quaternion.Inverse(Quaternion.LookRotation(Atlas.Anterior));
            Atlas.transform.localRotation = yaw; Atlas.transform.localScale = Vector3.one * ModelScale;
            Atlas.transform.localPosition = -(yaw * Atlas.RestBounds.center) * ModelScale;
        }

        void BuildControls()
        {
            var brand = ScalpalBrand.Active;
            if (!brand) return;
            if (!ShellView.Configured) ShellView.Configure(brand);
            controls = new GameObject("BriefingControls").transform;
            controls.SetParent(transform, false);
            controls.localPosition = new Vector3(0, -.3f, -.12f);
            ShellView.Panel(controls, "BriefingGlass", new Vector3(0, 0, .004f), new Vector2(.46f, .15f));
            caption = ShellView.FixedText(controls, "", new Vector3(-.21f, .062f, -.004f), .022f, .42f, ScalpalTextRole.Label);
            next = ShellView.Button(controls, "Next", new Vector3(-.105f, -.03f, -.004f), new Vector2(.19f, .058f), Next, true, true);
            ShellView.Button(controls, "Skip briefing", new Vector3(.105f, -.03f, -.004f), new Vector2(.19f, .058f), Skip, true, false, true);
        }

        /// <summary>Applies step <paramref name="index"/>: cumulative peel, focus lift/scale/spin/glow, Jarvis line.</summary>
        public void Enter(int index)
        {
            if (Finished || index < 0 || index >= Steps.Length) return;
            StepIndex = index; StepElapsed = 0;
            var step = Steps[index];
            peeled.Clear(); focused.Clear();
            Atlas.PeelThrough(step.peelThroughLayer, peeled); Atlas.Resolve(step.peel, peeled); Atlas.Resolve(step.focus, focused);
            foreach (int part in focused) peeled.Remove(part);
            ApplyTargets();
            Speak(step);
            if (caption) caption.text = (index + 1) + " / " + Steps.Length + " · " + step.title;
            if (next && next.label) ShellView.SetText(next.label, index + 1 < Steps.Length ? "Next" : "To Time-Out");
        }

        public bool IsPeeled(int part) => peeled.Contains(part);
        public bool IsFocused(int part) => focused.Contains(part);

        void ApplyTargets()
        {
            var bounds = Atlas.RestBounds; var anterior = Atlas.Anterior;
            // The focus group moves as one about its own centre, so adjacent structures stay together.
            Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
            foreach (int part in focused) { lo = Vector3.Min(lo, Atlas.Parts[part].min); hi = Vector3.Max(hi, Atlas.Parts[part].max); }
            var pivot = focused.Count > 0 ? (lo + hi) * .5f : bounds.center;
            float extent = focused.Count > 0 ? Mathf.Max(1e-3f, Mathf.Max(hi.x - lo.x, Mathf.Max(hi.y - lo.y, hi.z - lo.z))) : 1;
            float scale = Mathf.Max(1, Mathf.Min(FocusScale, FocusMaximumSize / extent));
            // Lift toward the learner until every focus part's centre clears the front of everything left in place.
            float front = float.MinValue;
            for (int i = 0; i < Atlas.Parts.Length; i++)
                if (!peeled.Contains(i) && !focused.Contains(i) && Atlas.Parts[i].triangles.Length > 0) front = Mathf.Max(front, Vector3.Dot(Atlas.Parts[i].max, anterior));
            // The group spins about its pivot, so clear the whole scaled sweep (a sphere around the pivot), not just the
            // part centres: otherwise the far side of a spinning organ swings back through the torso.
            float sweep = 0;
            foreach (int part in focused)
                foreach (var corner in Corners(Atlas.Parts[part].min, Atlas.Parts[part].max)) sweep = Mathf.Max(sweep, (corner - pivot).magnitude * scale);
            float lift = MinimumLift;
            if (focused.Count > 0 && front > float.MinValue) lift = Mathf.Max(lift, front + LiftClearance + sweep - Vector3.Dot(pivot, anterior));
            // A wall layer as big as the torso (skin, aponeurosis) cannot spin clear at a comfortable distance: it lifts
            // forward and glows without turning.
            bool spin = lift <= MaximumLift;
            lift = Mathf.Min(lift, MaximumLift);
            for (int i = 0; i < Atlas.Parts.Length; i++)
            {
                var part = Atlas.Parts[i];
                var state = new BriefingAtlas.PartState { alpha = part.triangles.Length > 0 ? 1 : 0, scale = 1, pivot = part.center, highlight = Atlas.Target[i].highlight };
                if (peeled.Contains(i))
                {
                    // Slide outward (through the front and away from the middle) while fading.
                    var outward = Vector3.ProjectOnPlane(part.center - bounds.center, anterior);
                    state.alpha = 0;
                    state.peel = (anterior + (outward.sqrMagnitude > 1e-8f ? outward.normalized * .6f : Vector3.zero)).normalized * PeelDistance;
                }
                else if (focused.Contains(i)) { state.scale = scale; state.glow = 1; state.lift = anterior * lift; state.pivot = pivot; state.spin = spin; }
                Atlas.SetTarget(i, state);
            }
        }

        static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
        {
            for (int i = 0; i < 8; i++) yield return new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
        }

        void Speak(BriefingStep step)
        {
            SpokeClip = false;
            StopSpeech();
            var clip = BriefingVoice.Find(step);
            if (clip && Application.isPlaying)
            {
                if (voice) SpokeClip = voice.PlayLocalSpeech(clip);
                if (!SpokeClip)
                {
                    if (!fallbackSpeaker) { fallbackSpeaker = gameObject.AddComponent<AudioSource>(); fallbackSpeaker.playOnAwake = false; fallbackSpeaker.spatialBlend = 0; }
                    fallbackSpeaker.clip = clip; fallbackSpeaker.Play(); SpokeClip = true;
                }
            }
            if (DialogueBox.Active) DialogueBox.Active.Say(DialogueSpeaker.Coach, "", step.line);
            StepDuration = Mathf.Max(MinimumStepSeconds, SpokeClip ? clip.length + ClipTailSeconds : ReadingSeconds(step.line));
        }

        /// <summary>Caption-only pacing: about 2.6 words per second plus a beat, 4 to 12 s.</summary>
        public static float ReadingSeconds(string line)
        {
            int words = string.IsNullOrWhiteSpace(line) ? 0 : line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries).Length;
            return Mathf.Clamp(words / 2.6f + 1.5f, 4, 12);
        }

        void StopSpeech()
        {
            if (voice && voice.PlaybackActive && !voice.Connected) voice.InterruptPlayback();
            if (fallbackSpeaker) fallbackSpeaker.Stop();
        }

        public void Next()
        {
            if (Finished) return;
            if (StepIndex + 1 < Steps.Length) Enter(StepIndex + 1); else Finish(false);
        }

        public void Skip() => Finish(true);

        void Finish(bool skipped)
        {
            if (Finished) return;
            Finished = true; Skipped = skipped;
            StopSpeech();
            foreach (var pointer in pointers) pointer.Clear();
            gameObject.SetActive(false);
            if (Stage) Stage.Sync(0); // Restores the OR and starts the void's fade-out.
        }

        /// <summary>Advances the step clock; Update drives it with unscaled time, validation drives it directly.</summary>
        public void Tick(float dt)
        {
            if (Finished || Step == null) return;
            StepElapsed += Mathf.Max(0, dt);
            if (StepElapsed >= StepDuration) Next();
        }

        void Update()
        {
            if (Finished) return;
            if (ShellPause.Instance && ShellPause.Instance.IsPaused) { foreach (var pointer in pointers) pointer.Clear(); return; }
            StepPointers();
            Tick(Time.unscaledDeltaTime);
        }

        void StepPointers()
        {
            var space = viewer ? viewer.parent : null;
            Ray? first = null, second = null;
            for (int hand = 1; hand >= 0; hand--)
            {
                var pointer = pointers[hand];
                var pressed = pointer.Step(space, space != null, transform);
                if (Finished) return;
                var sample = pointer.LastSample;
                bool free = advance[hand].Sample(sample.select, sample.Valid && space != null, ScalpalAim.Now);
                if (!sample.Valid || space == null || pointer.Idle || pointer.Hovered != null || pressed != null) continue;
                if (first == null) first = pointer.LastRay; else second = pointer.LastRay;
                // Trigger anywhere off the buttons advances.
                if (free) { Next(); return; }
            }
            int part = Picker.Track(first, second, out _);
            if (part < 0) return;
            // End the hovering hand's ray on the part it identifies.
            foreach (var pointer in pointers)
                if (pointer.Visual != null && pointer.Visual.Visible && pointer.Hovered == null && Picker.Pick(pointer.LastRay, out var hit) == part)
                { pointer.Visual.Show(pointer.LastRay.origin, hit, true, Vector3.zero, false); break; }
        }

        void OnEnable()
        {
            // Returning from a pause or registration interruption replays the current step's line.
            if (Atlas && Step != null && !Finished) Speak(Step);
            if (Stage) Stage.Sync(0);
        }

        void OnDisable()
        {
            if (Stage) Stage.Sync(0); // Pause/realign: the OR shows again until the briefing resumes.
            StopSpeech();
            foreach (var pointer in pointers) pointer.Clear();
        }

        void OnDestroy() { foreach (var pointer in pointers) pointer.Destroy(); }
    }
}
