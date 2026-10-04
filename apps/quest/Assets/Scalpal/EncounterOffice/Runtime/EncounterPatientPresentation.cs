using UnityEngine;

namespace Scalpal.EncounterOffice
{
    // Stylized head/jaw indicators; not phoneme animation or clinical patient motion.
    public sealed class EncounterPatientPresentation : MonoBehaviour
    {
        public GameObject female, male;
        public TextMesh stateLabel;
        public string State { get; private set; } = "waiting";
        Transform head, jaw;
        Quaternion headRest, jawRest;
        public void Select(string patientId)
        {
            if (head) head.localRotation = headRest;
            if (jaw) jaw.localRotation = jawRest;
            if (female) female.SetActive(patientId == EncounterContract.FemalePatientId);
            if (male) male.SetActive(patientId == EncounterContract.MalePatientId);
            var active = patientId == EncounterContract.MalePatientId ? male : female;
            head = jaw = null;
            if (active) foreach (var node in active.GetComponentsInChildren<Transform>(true))
            {
                if (node.name == "HeadPivot") head = node;
                if (node.name == "JawPivot") jaw = node;
            }
            headRest = head ? head.localRotation : Quaternion.identity;
            jawRest = jaw ? jaw.localRotation : Quaternion.identity;
            SetState("waiting");
        }
        public void SetState(string value) { State = value; if (stateLabel) stateLabel.text = "Patient · " + value; }
        void Update()
        {
            float t = Time.unscaledTime;
            if (head) head.localRotation = headRest * Quaternion.Euler(State == "listening" ? Mathf.Sin(t * 1.3f) * 1.5f : Mathf.Sin(t * 0.8f) * 0.6f, 0, 0);
            if (jaw) jaw.localRotation = jawRest * Quaternion.Euler(State == "speaking" ? (Mathf.Sin(t * 14) + 1) * 3 : 0, 0, 0);
        }
        void OnDisable() { if (head) head.localRotation = headRest; if (jaw) jaw.localRotation = jawRest; }
    }
}
