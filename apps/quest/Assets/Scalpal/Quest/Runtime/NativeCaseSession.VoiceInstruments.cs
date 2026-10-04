using System;
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
        [Serializable] class InstrumentToolArguments { public string instrument = "", hand = ""; }

        void VoiceTool(QuestJarvisVoice.ToolRequest request)
        {
            InstrumentToolArguments arguments = null;
            try { arguments = JsonUtility.FromJson<InstrumentToolArguments>(string.IsNullOrEmpty(request.ParametersJson) ? "{}" : request.ParametersJson); }
            catch (ArgumentException) { }
            arguments = arguments ?? new InstrumentToolArguments();
            string result; bool ok;
            switch (request.ToolName)
            {
                case "swap_instrument": ok = SwapInstrument(arguments.instrument, arguments.hand, out result); break;
                case "highlight_instrument": ok = HighlightInstrument(arguments.instrument, out result); break;
                default: ok = false; result = "This action is unavailable in the native exercise"; break;
            }
            voice.ResolveClientTool(request, result, !ok);
        }

        // Whatever the chosen hand holds goes back to its rest pose on the stand; the requested tool attaches as a grip pickup
        // would. hand is left, right or either (the hand already holding something, else the right).
        public bool SwapInstrument(string instrument, string hand, out string result)
        {
            string id = InstrumentId(instrument), name = id.Replace('_', ' ');
            if (!Practicing) { result = Phase == "Practicing" ? "failed: practice is paused" : "failed: not practicing yet; finish the briefing and Time-Out first"; return false; }
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
            if (hint) hint.Say(tool.gripAnchor ? tool.gripAnchor : tool.transform, Capitalized(name), useLeft ? XRNode.LeftHand : XRNode.RightHand,
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
