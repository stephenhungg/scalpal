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
        // Companion chair seat in the office art (measured from the DoctorOffice upholstery mesh), and the yaw that
        // turns a seated template toward the patient chair.
        public Vector3 companionSeat = new Vector3(1.15f, 0, -.30f);
        public float companionYaw = -20;
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
        }

        void RestorePose()
        {
            mouth = 0;
            if (head) head.localRotation = headRest;
            if (jaw) jaw.localRotation = jawRest;
            if (chest) chest.localRotation = chestRest;
        }
        void OnApplicationFocus(bool focus) { focused = focus; if (!focus) RestorePose(); }
        void OnApplicationPause(bool pause) { paused = pause; if (pause) RestorePose(); }
        void OnDisable() => RestorePose();
    }
}
