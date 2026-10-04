using Scalpal.Voice;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    // Audio amplitude drives the real donor jaw bone; subtle body motion is authored presentation, not phoneme animation.
    public sealed class EncounterPatientPresentation : MonoBehaviour
    {
        public GameObject female, male;
        public TextMesh stateLabel;
        public QuestJarvisVoice voice;
        public string State { get; private set; } = "waiting";
        Transform head, jaw, chest;
        Quaternion headRest, jawRest, chestRest;
        float mouth;
        bool focused = true, paused;

        public void Select(string patientId)
        {
            RestorePose();
            if (female) female.SetActive(patientId == EncounterContract.FemalePatientId);
            if (male) male.SetActive(patientId == EncounterContract.MalePatientId);
            var active = patientId == EncounterContract.MalePatientId ? male : female;
            head = jaw = chest = null;
            if (active) foreach (var node in active.GetComponentsInChildren<Transform>(true))
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

        public void SetState(string value)
        {
            State = value;
            if (stateLabel) stateLabel.text = "Patient · " + value;
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
