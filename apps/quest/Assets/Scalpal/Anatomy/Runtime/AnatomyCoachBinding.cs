using Scalpal.Exercises.Coach;
using UnityEngine;

namespace Scalpal.Anatomy
{
    // Assign the existing relay and the currently active anatomy root in the scene.
    // This binding handles rendering only. The scene must also stop authored exercise events
    // on registration loss; hiding colliders does not block UI-driven scoring events.
    [DisallowMultipleComponent]
    public sealed class AnatomyCoachBinding : MonoBehaviour
    {
        public AnatomyController anatomy;
        public CoachRelay relay;
        AnatomyController subscribedAnatomy;
        CoachRelay subscribedRelay;

        void OnEnable() { Rebind(); }
        void OnDisable() { Unbind(); }

        // Call after replacing the active anatomy root or relay while this component is enabled.
        public void Rebind()
        {
            Unbind();
            if (!isActiveAndEnabled || anatomy == null || relay == null) return;
            subscribedAnatomy = anatomy;
            subscribedRelay = relay;
            subscribedRelay.CommandRequested += ApplyCommand;
            subscribedAnatomy.RegistrationChanged += ForwardRegistration;
            subscribedRelay.Tracking(subscribedAnatomy.RegistrationValid);
        }

        void Unbind()
        {
            if (subscribedRelay != null) subscribedRelay.CommandRequested -= ApplyCommand;
            if (subscribedAnatomy != null) subscribedAnatomy.RegistrationChanged -= ForwardRegistration;
            subscribedRelay = null;
            subscribedAnatomy = null;
        }

        void ForwardRegistration(bool valid)
        {
            if (subscribedRelay != null) subscribedRelay.Tracking(valid);
        }

        void ApplyCommand(CoachCommand command)
        {
            if (command == null || subscribedRelay == null) return;
            if (subscribedAnatomy == null)
            {
                subscribedRelay.Ack(command.commandId, false, "anatomy unavailable");
                return;
            }
            if (command.action == "clear_highlight")
            {
                subscribedAnatomy.ClearHighlight();
                subscribedRelay.Ack(command.commandId, true);
                return;
            }
            if (command.action != "highlight")
            {
                subscribedRelay.Ack(command.commandId, false, "unsupported anatomy command");
                return;
            }
            if (!subscribedAnatomy.CanDisplay)
            {
                subscribedRelay.Ack(command.commandId, false, "anatomy hidden or registration invalid");
                return;
            }
            var applied = subscribedAnatomy.Highlight(command.targetId);
            subscribedRelay.Ack(command.commandId, applied,
                applied ? "" : "structure missing, hidden, ambiguous, or material unsupported");
        }
    }
}
