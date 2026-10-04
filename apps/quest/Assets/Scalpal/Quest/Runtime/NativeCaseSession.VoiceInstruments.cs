using System;
using System.Collections.Generic;
using SpacetimeDB.Types;
using Scalpal.Instruments;
using Scalpal.Surgery;
using Scalpal.Voice;
using UnityEngine;
using UnityEngine.XR;

namespace Scalpal.Quest
{
    public sealed partial class NativeCaseSession
    {
        // Coach client tools that act on the instruments: "Scalpal, give me the hemostat" (swap_instrument) and the coach
        // pointing out the tool the learner needs on the stand (highlight_instrument). Each call resolves exactly once.
        // With a bound shared session Jarvis acts like every other actor: the call becomes a handInstrument or
        // highlightInstrument row in the SpacetimeDB command table and is applied when that row comes back through the
        // subscription. Without one (or if the module refuses the request) it applies locally, as the fallback build does.
        [Serializable] class InstrumentToolArguments { public string instrument = "", hand = ""; }
        public const float SharedReducerSeconds = 4, SharedToolSeconds = 8; // under the voice's 10 s tool timeout
        sealed class SharedTool { public QuestJarvisVoice.ToolRequest request; public string action, instrument, hand; public float since; }
        readonly Dictionary<string, SharedTool> sharedTools = new Dictionary<string, SharedTool>();
        readonly HashSet<string> abandonedShared = new HashSet<string>();

        void VoiceTool(QuestJarvisVoice.ToolRequest request)
        {
            InstrumentToolArguments arguments = null;
            try { arguments = JsonUtility.FromJson<InstrumentToolArguments>(string.IsNullOrEmpty(request.ParametersJson) ? "{}" : request.ParametersJson); }
            catch (ArgumentException) { }
            arguments = arguments ?? new InstrumentToolArguments();
            string action = request.ToolName == "swap_instrument" ? "handInstrument" : request.ToolName == "highlight_instrument" ? "highlightInstrument" : null;
            if (action == null) { voice.ResolveClientTool(request, "This action is unavailable in the native exercise", true); return; }
            string hand = (arguments.hand ?? "").Trim().ToLowerInvariant();
            string commandId = "jarvis-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            if (realtime && SharedMatches && realtime.RequestCommand(commandId, action, InstrumentId(arguments.instrument),
                    action == "handInstrument" && hand == "left" ? 0 : action == "handInstrument" && hand == "right" ? 1 : (double?)null))
            {
                sharedTools[commandId] = new SharedTool { request = request, action = action, instrument = arguments.instrument, hand = hand, since = Time.realtimeSinceStartup };
                return;
            }
            ApplyInstrumentLocally(request, action, arguments.instrument, hand);
        }

        void ApplyInstrumentLocally(QuestJarvisVoice.ToolRequest request, string action, string instrument, string hand)
        {
            string result;
            bool ok = action == "handInstrument" ? SwapInstrument(instrument, hand, out result, "Scalpal") : HighlightInstrument(instrument, out result);
            voice.ResolveClientTool(request, result, !ok);
        }

        // Every actor's instrument command (browser scrub nurse, coach, Jarvis via VoiceTool) lands here once.
        void SharedInstrumentCommand(Command command)
        {
            string result; bool ok;
            if (abandonedShared.Remove(command.CommandId)) { ok = false; result = "failed: superseded by the headset's local fallback"; }
            else if (command.Action == "handInstrument")
            {
                string hand = command.ArgNumber == 0 ? "left" : command.ArgNumber == 1 ? "right" : "either";
                ok = SwapInstrument(command.TargetId, hand, out result, CommandSource(command));
            }
            else ok = HighlightInstrument(command.TargetId, out result);
            Publish();
            realtime.ResolveCommand(command, ok ? "applied" : "rejected", ok ? null : result.StartsWith("failed: ", StringComparison.Ordinal) ? result.Substring(8) : result);
            FinishSharedTool(command.CommandId, result, !ok);
        }

        void FinishSharedTool(string commandId, string result, bool isError)
        {
            if (!sharedTools.TryGetValue(commandId, out var tool)) return;
            sharedTools.Remove(commandId);
            voice.ResolveClientTool(tool.request, result, isError);
        }

        // Never leaves a Jarvis tool call hanging: a refused request falls back to the local hand-over, a request with no
        // reducer outcome in 4 s is abandoned (its row, if it ever arrives, is rejected) and applied locally, and a
        // committed command the headset has not applied in 8 s answers the call with an error.
        void PumpSharedTools(float now)
        {
            if (sharedTools.Count == 0) return;
            foreach (var id in new List<string>(sharedTools.Keys))
            {
                var tool = sharedTools[id];
                string reason = null, status = realtime ? realtime.OwnCommandStatus(id, out reason) : "unknown";
                if (status == "refused" || status == "unknown" || status == "sent" && now - tool.since >= SharedReducerSeconds)
                {
                    sharedTools.Remove(id);
                    if (status == "sent") abandonedShared.Add(id);
                    ApplyInstrumentLocally(tool.request, tool.action, tool.instrument, tool.hand);
                }
                else if (status != "sent" && status != "pending")
                    FinishSharedTool(id, status == "applied" ? "ok: " + status : "failed: " + (string.IsNullOrEmpty(reason) ? "shared command " + status : reason), status != "applied");
                else if (now - tool.since >= SharedToolSeconds)
                { abandonedShared.Add(id); FinishSharedTool(id, "failed: the shared session did not deliver the command in time", true); }
            }
        }

        // Who handed the instrument over, for the cue at the hand; the surgeon's own actions get no attribution.
        static string CommandSource(Command command)
        {
            if (command.CommandId.StartsWith("jarvis-", StringComparison.Ordinal)) return "Scalpal";
            switch (command.RequestedRole)
            {
                case "coach": return "coach";
                case "operator": case "viewer": return "nurse";
                default: return null;
            }
        }

        // Whatever the chosen hand holds goes back to its rest pose on the stand; the requested tool attaches as a grip pickup
        // would. hand is left, right or either (the hand already holding something, else the right).
        public bool SwapInstrument(string instrument, string hand, out string result, string from = null)
        {
            string id = InstrumentId(instrument), name = id.Replace('_', ' ');
            if (!Practicing) { result = Phase == "Practicing" ? "failed: practice is paused" : "failed: not practicing yet; finish the briefing first"; return false; }
            if (!Hands(out var left, out var right)) { result = "failed: hands not tracked"; return false; }
            hand = (hand ?? "").Trim().ToLowerInvariant();
            bool either = hand != "left" && hand != "right";
            bool useLeft = hand == "left" || (either && (left.HeldInstrument && !right.HeldInstrument || !right.TrackingValid));
            var grip = useLeft ? left : right; string side = useLeft ? "left" : "right";
            if (!grip.TrackingValid) { result = "failed: " + side + " hand not tracked"; return false; }
            if (grip.HeldInstrument && grip.HeldInstrument.instrumentId == id) { result = "failed: " + name + " already in your " + side + " hand"; return false; }
            InstrumentBehaviour tool = null; bool inCase = false;
            foreach (var candidate in workbench.tools ?? Array.Empty<InstrumentBehaviour>())
            {
                if (!candidate || !candidate.gameObject.activeInHierarchy || candidate.instrumentId != id) continue;
                inCase = true;
                if (!candidate.Held) { tool = candidate; break; }
            }
            if (!inCase) { result = "failed: " + (id == "" ? "no instrument named" : name + " is not in this case"); return false; }
            if (!tool) { result = "failed: the " + name + " is already in your " + (useLeft ? "right" : "left") + " hand"; return false; }
            if (!grip.TryHandOver(tool)) { result = "failed: " + side + " hand not tracked"; return false; }
            // A soft cue at the hand: the tool's name and a short tap on that controller.
            var hint = GetComponent<SurgeryTriggerHint>();
            if (hint) hint.Say(tool.gripAnchor ? tool.gripAnchor : tool.transform, Capitalized(name) + (string.IsNullOrEmpty(from) ? "" : " · from " + from), useLeft ? XRNode.LeftHand : XRNode.RightHand,
                workbench.headCamera ? workbench.headCamera.transform : null, 1.2f, .7f);
            result = "ok: " + name + " in " + side + " hand";
            return true;
        }

        public NativeInstrumentCallout InstrumentCallout => GetComponent<NativeInstrumentCallout>();

        public bool HighlightInstrument(string instrument, out string result)
        {
            string id = InstrumentId(instrument), name = id.Replace('_', ' ');
            InstrumentBehaviour tool = null, held = null;
            foreach (var candidate in workbench ? workbench.tools ?? Array.Empty<InstrumentBehaviour>() : Array.Empty<InstrumentBehaviour>())
            {
                if (!candidate || !candidate.gameObject.activeInHierarchy || candidate.instrumentId != id) continue;
                if (!candidate.Held) { tool = candidate; break; }
                held = candidate;
            }
            if (!tool) { result = "failed: " + (held ? "the " + name + " is already in the learner's hand" : id == "" ? "no instrument named" : name + " is not in this case"); return false; }
            var callout = InstrumentCallout ? InstrumentCallout : gameObject.AddComponent<NativeInstrumentCallout>();
            callout.Show(tool);
            result = "ok: " + name + " highlighted";
            return true;
        }

        bool Hands(out InstrumentInteractor left, out InstrumentInteractor right)
        {
            left = right = null;
            foreach (var input in workbench ? workbench.inputs ?? Array.Empty<XRInstrumentInput>() : Array.Empty<XRInstrumentInput>())
            {
                var grip = input ? input.GetComponent<InstrumentInteractor>() : null;
                if (!grip) continue;
                if (input.controller == XRNode.LeftHand) left = grip; else if (input.controller == XRNode.RightHand) right = grip;
            }
            return left && right && (left.TrackingValid || right.TrackingValid);
        }

        static string InstrumentId(string spoken) => (spoken ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);
    }
}
