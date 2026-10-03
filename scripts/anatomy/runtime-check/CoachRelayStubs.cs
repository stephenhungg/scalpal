using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Exercises.Coach
{
    // Mirrors only the relay boundary exercised by AnatomyCoachBinding.
    public class CoachCommand
    {
        public string commandId;
        public string action;
        public string targetId;
    }
    public class CoachRelay : MonoBehaviour
    {
        public event Action<CoachCommand> CommandRequested;
        public readonly List<bool> tracking = new List<bool>();
        public readonly List<(string id, bool applied, string reason)> acks = new List<(string, bool, string)>();
        public void Tracking(bool valid) { tracking.Add(valid); }
        public void Ack(string commandId, bool applied, string reason = "") { acks.Add((commandId, applied, reason)); }
        public void Emit(string action, string target)
        {
            CommandRequested?.Invoke(new CoachCommand { commandId = "test-command", action = action, targetId = target });
        }
    }
}
