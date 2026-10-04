using System.Collections.Generic;
using Scalpal.Voice;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    // Audio amplitude drives the real donor jaw bone; subtle body motion is authored presentation, not phoneme animation.
    // Every playable encounter seats someone: the patient in the patient chair (scaled for age), and for a
    // parent speaker the parent in the companion chair beside the child. The speaker's mouth carries the voice.
    public sealed class EncounterPatientPresentation : MonoBehaviour
    {
        public GameObject female, male;
        public TMPro.TextMeshPro stateLabel;
        public QuestJarvisVoice voice;
        // Companion chair seat in the office art (measured from the DoctorOffice upholstery mesh), and the seated
        // template's yaw. The chair itself faces about 8 degrees away from the patient; turning the parent -20 degrees
        // inside it put the left hand into the armrest, so the parent sits square to the chair.
        public Vector3 companionSeat = new Vector3(1.15f, 0, -.30f);
        public float companionYaw = 0;
        public const float SeatHeight = .52f; // inventory.json patientSeatHeightMetres
        public string State { get; private set; } = "waiting";
        public GameObject Patient { get; private set; }
        public GameObject Companion { get; private set; }
        public GameObject Speaker { get; private set; }
        public AudioSource MouthSource { get; private set; }
        Transform head, jaw, chest;
        Quaternion headRest, jawRest, chestRest;
        float mouth;
        bool focused = true, paused, restCaptured;
        Vector3 templatePosition; Quaternion templateRotation; Vector3 templateScale = Vector3.one;
        GameObject femaleCompanion, maleCompanion;
        // Arms of every seated person (patient and parent), posed on the donor rig's real upper-arm, forearm and hand bones.
        public static readonly string[] ArmBones = { "upperarm01.R", "lowerarm01.R", "wrist.R", "upperarm01.L", "lowerarm01.L", "wrist.L" };
        // Largest offset from the imported seated pose per bone (upper arm, forearm, hand), in degrees.
        public static readonly float[] ArmLimit = { 14, 30, 30 };
        sealed class Figure
        {
            public readonly Transform[] bones = new Transform[6];
            public readonly Quaternion[] rest = new Quaternion[6], toBody = new Quaternion[6];
            public bool guards, speaks;
            public float seed, gesture, wince;
        }
        readonly List<Figure> figures = new List<Figure>();
        AudioSource attendingSource;
        string speakerLabel = "No encounter selected", avatarLabel = "Waiting for a patient";

        public void Select(EncounterState state)
        {
            RestorePose();
            CaptureTemplatePose();
            foreach (var model in new[] { female, male, femaleCompanion, maleCompanion }) if (model) model.SetActive(false);
            Patient = Companion = Speaker = null;
            string role = Normalize(state?.speaker);
            bool isParent = role == "parent";
            if (state == null) { speakerLabel = "No encounter selected"; avatarLabel = "Waiting for a patient"; }
            else
            {
                speakerLabel = isParent
                    ? Name(state.speakerName, "Parent") + " · parent speaking for " + Name(state.patientName, "the patient")
                    : Name(state.patientName, "Patient") + " · patient";
                // Never leave the chair empty for a playable encounter: unknown sex or age falls back to a generic adult.
                string patientSex = Sex(state.patientSex, isParent ? null : state.speakerSex);
                int patientAge = state.patientAge > 0 ? state.patientAge : isParent ? 0 : state.speakerAge;
                Patient = patientSex == "female" ? female : male;
                if (Patient) { Seat(Patient, Vector3.zero, 0, AgeScale(patientAge)); Patient.SetActive(true); }
                avatarLabel = Describe(patientSex, patientAge);
                if (isParent)
                {
                    string parentSex = Sex(state.speakerSex, null);
                    Companion = CompanionFor(parentSex);
                    if (Companion) { Seat(Companion, companionSeat, companionYaw, AgeScale(Mathf.Max(18, state.speakerAge))); Companion.SetActive(true); }
                    avatarLabel += " · parent beside";
                }
                Speaker = isParent && Companion ? Companion : Patient;
            }
            figures.Clear();
            AddFigure(Patient, true, 0);
            AddFigure(Companion, false, 7.3f);
            head = jaw = chest = null;
            if (Speaker) foreach (var node in Speaker.GetComponentsInChildren<Transform>(true))
            {
                if (node.name == "HeadPivot") head = node;
                if (node.name == "JawPivot") jaw = node;
                if (node.name == "spine02") chest = node;
            }
            headRest = head ? head.localRotation : Quaternion.identity;
            jawRest = jaw ? jaw.localRotation : Quaternion.identity;
            chestRest = chest ? chest.localRotation : Quaternion.identity;
            SetState("waiting");
        }

        // Moves the given transport's output to the speaking avatar's mouth (patient role) as a 3D source,
        // or back to the original non-spatial source for Jarvis, who is not a person in the room.
        public AudioSource BindVoice(QuestJarvisVoice target, bool patientSpeaking)
        {
            if (!target) return null;
            if (!attendingSource && target.Speaker && target.Speaker != MouthSource) attendingSource = target.Speaker;
            if (patientSpeaking && jaw)
            {
                if (!MouthSource)
                {
                    var go = new GameObject("PatientMouthVoice");
                    MouthSource = go.AddComponent<AudioSource>();
                }
                MouthSource.transform.SetParent(jaw, false);
                MouthSource.transform.localPosition = Vector3.zero;
                MouthSource.playOnAwake = false; MouthSource.mute = false; MouthSource.volume = 1;
                MouthSource.spatialBlend = 1; MouthSource.dopplerLevel = 0; MouthSource.spread = 0; MouthSource.priority = 0;
                MouthSource.rolloffMode = AudioRolloffMode.Logarithmic; MouthSource.minDistance = 1.5f; MouthSource.maxDistance = 12;
                target.Speaker = MouthSource;
                return MouthSource;
            }
            if (attendingSource) { attendingSource.spatialBlend = 0; attendingSource.mute = false; attendingSource.volume = 1; }
            target.Speaker = attendingSource;
            return attendingSource;
        }

        // Arm offsets are authored in the seated body's frame (x = the person's right, y = up, z = forward) for the right
        // side and mirrored for the left, so one table fits both donor rigs at any seated scale.
        void AddFigure(GameObject model, bool patient, float seed)
        {
            if (!model) return;
            var figure = new Figure { guards = patient, speaks = model == Speaker, seed = seed };
            Transform nose = null, headPivot = null;
            foreach (var node in model.GetComponentsInChildren<Transform>(true))
            {
                int index = System.Array.IndexOf(ArmBones, node.name);
                if (index >= 0) figure.bones[index] = node;
                if (node.name == "NoseTip") nose = node;
                if (node.name == "HeadPivot") headPivot = node;
            }
            if (System.Array.IndexOf(figure.bones, null) >= 0) return;
            var up = model.transform.up;
            var forward = nose && headPivot ? Vector3.ProjectOnPlane(nose.position - headPivot.position, up) : model.transform.forward;
            var body = Quaternion.LookRotation(forward.sqrMagnitude > 1e-6f ? forward : model.transform.forward, up);
            for (int i = 0; i < 6; i++)
            {
                figure.rest[i] = figure.bones[i].localRotation;
                figure.toBody[i] = Quaternion.Inverse(figure.bones[i].rotation) * body;
            }
            figures.Add(figure);
            PoseArms(figure, 0, 0, 0);
        }

        // Right-arm poses per bone (upper arm, forearm, hand); +x pitches a bone's tip down/back, +y swings it to the
        // person's right, +z rolls it outward. A seated adult rests the hand on the thigh; the appendicitis patient
        // guards the right lower abdomen and presses it harder when a wince comes.
        static readonly Vector3[] Lap = { new Vector3(4, 0, 3), new Vector3(14, 5, 0), new Vector3(14, 0, 0) };
        static readonly Vector3[] Guard = { new Vector3(12, 0, 2), new Vector3(10, -10, 0), new Vector3(8, -10, 0) };
        static readonly Vector3[] Press = { new Vector3(3, 0, 0), new Vector3(4, -5, 0), new Vector3(6, -4, 0) };
        static readonly Vector3[] LapWince = { new Vector3(-2, 0, 0), new Vector3(-6, -14, 0), new Vector3(0, -10, 0) };

        // An occasional wince while the patient talks: slow noise crossing a high threshold, smoothly in and out.
        public static float Wince(float time) => Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.6f, .72f, Mathf.PerlinNoise(time * .16f, 4.7f)));

        static float Wave(float time, float seed) => .55f * Mathf.Sin(time * 1.7f + seed) + .3f * Mathf.Sin(time * 2.9f + seed * 1.3f)
            + .9f * (Mathf.PerlinNoise(time * .8f, seed) - .5f);

        void PoseArms(Figure figure, float time, float breath, float emphasis)
        {
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0, guarding = figure.guards && right;
                float seed = figure.seed + side * 3.1f, t = time + seed;
                // The guarding hand stays mostly on the belly; the free hand carries the conversational gestures.
                float gesture = figure.gesture * (guarding ? .25f : figure.guards ? 1 : right ? .8f : .6f);
                for (int bone = 0; bone < 3; bone++)
                {
                    var offset = (guarding ? Guard : Lap)[bone];
                    if (figure.guards) offset += figure.wince * (guarding ? Press : LapWince)[bone];
                    if (bone == 0) offset += gesture * new Vector3(-4 - 3 * Wave(t * .7f, seed), 0, 3 * Wave(t * .5f, seed + 2));
                    if (bone == 1) offset += gesture * new Vector3(-16 - 9 * Wave(t, seed) - 6 * emphasis, 9 * Wave(t * .8f, seed + 5), 0);
                    if (bone == 2) offset += gesture * new Vector3(-6 * Wave(t * 1.2f, seed + 1), 6 * Wave(t * .9f, seed + 3), 14 * Wave(t * .6f, seed + 4) - 6);
                    offset.x += breath * (bone == 1 ? .8f : .4f);
                    float limit = ArmLimit[bone] - 1;
                    offset = new Vector3(Mathf.Clamp(offset.x, -limit, limit), Mathf.Clamp(offset.y, -limit, limit), Mathf.Clamp(offset.z, -limit, limit));
                    offset = Vector3.ClampMagnitude(offset, limit);
                    if (!right) offset = new Vector3(offset.x, -offset.y, -offset.z);
                    int index = side * 3 + bone;
                    figure.bones[index].localRotation = figure.rest[index] * figure.toBody[index] * Quaternion.Euler(offset) * Quaternion.Inverse(figure.toBody[index]);
                }
            }
        }

        void CaptureTemplatePose()
        {
            if (restCaptured || !female) return;
            restCaptured = true;
            templatePosition = female.transform.localPosition; templateRotation = female.transform.localRotation; templateScale = female.transform.localScale;
        }

        // Scale about the floor origin, then lift so a smaller seated body's hips still meet the seat.
        void Seat(GameObject model, Vector3 offset, float yaw, float scale)
        {
            var t = model.transform;
            t.localScale = templateScale * scale;
            t.localRotation = Quaternion.Euler(0, yaw, 0) * templateRotation;
            t.localPosition = templatePosition + offset + Vector3.up * SeatHeight * (1 - scale);
        }

        GameObject CompanionFor(string sex)
        {
            var source = sex == "female" ? female : male;
            if (!source) return null;
            if (source != Patient) return source;
            ref GameObject clone = ref (sex == "female" ? ref femaleCompanion : ref maleCompanion);
            if (!clone)
            {
                clone = Instantiate(source, source.transform.parent);
                clone.name = source.name + "Companion";
            }
            return clone;
        }

        // Age-appropriate seated size: a child by growth, older adults slightly shorter. Unknown age stays adult.
        public static float AgeScale(int age)
        {
            if (age <= 0 || age >= 18 && age < 65) return 1;
            if (age >= 65) return age >= 80 ? .95f : .97f;
            return Mathf.Clamp(.45f + age * .035f, .5f, 1); // 9 years -> .77 (about 1.33 m standing)
        }

        static string Describe(string sex, int age)
        {
            if (age > 0 && age < 18) return "Scaled child " + sex + " avatar";
            if (age >= 65) return "Generic older adult " + sex + " avatar";
            return "Generic adult " + sex + " avatar";
        }

        static string Sex(string value, string fallback)
        {
            string sex = Normalize(value);
            if (sex == "female" || sex == "male") return sex;
            sex = Normalize(fallback);
            return sex == "female" ? "female" : "male";
        }

        static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        static string Name(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        public void SetState(string value)
        {
            State = value;
            if (stateLabel) stateLabel.text = speakerLabel + "\n" + avatarLabel + " · " + value;
            if (value != "speaking") { mouth = 0; if (jaw) jaw.localRotation = jawRest; }
        }

        void LateUpdate()
        {
            ApplyAnimation(Time.unscaledDeltaTime, Time.unscaledTime, voice ? voice.PlaybackLevel : 0, voice && voice.PlaybackActive);
        }

        // Deterministic pose step also used by Editor tests with a controlled envelope, without a microphone/provider.
        void ApplyAnimation(float deltaTime, float time, float playbackLevel, bool playbackActive)
        {
            if (!focused || paused) { RestorePose(); return; }
            // Provider mode can change before its last queued syllable finishes playing.
            bool talking = (State == "speaking" || State == "listening") && playbackActive;
            float level = float.IsNaN(playbackLevel) || float.IsInfinity(playbackLevel) ? 0 : Mathf.Clamp01(playbackLevel);
            float target = talking && level > .015f ? Mathf.Pow(level, .65f) : 0;
            if (!talking || target == 0) mouth = 0; // Silent gaps and disconnects must not leave a talking patient.
            else mouth = Mathf.Lerp(mouth, target, 1 - Mathf.Exp(-22 * Mathf.Clamp(deltaTime, 0, .2f)));
            if (jaw) jaw.localRotation = jawRest * Quaternion.Euler(mouth * 9, 0, 0);

            float listening = State == "listening" ? 1 : 0;
            if (head) head.localRotation = headRest * Quaternion.Euler(
                Mathf.Sin(time * .83f) * .35f + listening * Mathf.Sin(time * 1.3f) * .65f + mouth * .35f,
                Mathf.Sin(time * .47f) * .38f, Mathf.Sin(time * .61f) * .22f);
            // MakeHuman's spine02 is a weighted upper-torso bone; a small rotation gently moves chest/clothes with breath.
            if (chest) chest.localRotation = chestRest * Quaternion.Euler(Mathf.Sin(time * 1.45f) * .32f, 0, 0);

            // Arms: everyone breathes; whoever is voicing gestures; the patient guards the sore belly and sometimes winces.
            float dt = Mathf.Clamp(deltaTime, 0, .2f), breath = Mathf.Sin(time * 1.45f);
            foreach (var figure in figures)
            {
                bool voiced = figure.speaks && talking;
                figure.gesture = Mathf.Lerp(figure.gesture, voiced ? 1 : 0, 1 - Mathf.Exp(-2.5f * dt));
                figure.wince = Mathf.Lerp(figure.wince, voiced && figure.guards ? Wince(time) : 0, 1 - Mathf.Exp(-6 * dt));
                PoseArms(figure, time, breath, mouth);
                if (figure.speaks && figure.wince > .01f)
                {
                    if (head) head.localRotation *= Quaternion.Euler(figure.wince * 4, 0, 0);
                    if (chest) chest.localRotation *= Quaternion.Euler(figure.wince * 1.5f, 0, 0);
                }
            }
        }

        void RestorePose()
        {
            mouth = 0;
            if (head) head.localRotation = headRest;
            if (jaw) jaw.localRotation = jawRest;
            if (chest) chest.localRotation = chestRest;
            foreach (var figure in figures)
            {
                figure.gesture = figure.wince = 0;
                for (int i = 0; i < 6; i++) if (figure.bones[i]) figure.bones[i].localRotation = figure.rest[i];
            }
        }
        void OnApplicationFocus(bool focus) { focused = focus; if (!focus) RestorePose(); }
        void OnApplicationPause(bool pause) { paused = pause; if (pause) RestorePose(); }
        void OnDisable() => RestorePose();
    }
}
