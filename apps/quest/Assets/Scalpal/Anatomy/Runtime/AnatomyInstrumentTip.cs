using UnityEngine;
using Scalpal.Exercises.Engine;

namespace Scalpal.Anatomy
{
    // Put this on an instrument tip with a trigger collider and a kinematic Rigidbody.
    // Select the instrument through AnatomyExerciseBinding before using it.
    [DisallowMultipleComponent]
    public sealed class AnatomyInstrumentTip : MonoBehaviour
    {
        public AnatomyExerciseBinding exercise;
        public string instrumentId = "";
        [Tooltip("Disable when a controller button explicitly invokes ActivateContact instead.")]
        public bool activateOnEntry = true;
        public string LastRejection { get; private set; } = "";
        void OnTriggerEnter(Collider other)
        {
            if (activateOnEntry) ActivateContact(other, out _);
        }
        public bool ActivateContact(Collider other, out CaseResult result)
        {
            result = default;
            if (!isActiveAndEnabled || exercise == null || exercise.SelectedInstrumentId != instrumentId || string.IsNullOrEmpty(instrumentId))
            {
                LastRejection = "tip is inactive or not the selected instrument";
                return false;
            }
            var accepted = exercise.TouchCollider(other, out result, out var reason);
            LastRejection = reason;
            return accepted;
        }
    }
}
