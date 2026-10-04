using System;
using UnityEngine;

namespace Scalpal.Briefing
{
    /// <summary>
    /// One briefing step (Resources/briefing_steps.json), keyed like briefing_parts.json beats and the coach's
    /// brief.&lt;stepId&gt; narration. Peeled = every part with layer &lt;= peelThroughLayer plus the peel ids (cumulative);
    /// focus ids are lifted, scaled, spun and glow. line is Jarvis's narration (services/preop/src/briefing.ts).
    /// </summary>
    [Serializable]
    public sealed class BriefingStep
    {
        public string id = "", title = "", line = "";
        public int peelThroughLayer = -1;
        public string[] peel = Array.Empty<string>(), focus = Array.Empty<string>();
    }

    [Serializable]
    public sealed class BriefingStepList
    {
        public int version;
        public BriefingStep[] steps;
    }

    public static class BriefingSteps
    {
        public const string Resource = "briefing_steps";
        public static BriefingStep[] Load()
        {
            var file = Resources.Load<TextAsset>(Resource);
            if (!file) { Debug.LogWarning("SCALPAL_BRIEFING_STEPS_MISSING Resources/" + Resource); return Array.Empty<BriefingStep>(); }
            try { return JsonUtility.FromJson<BriefingStepList>(file.text)?.steps ?? Array.Empty<BriefingStep>(); }
            catch (ArgumentException exception) { Debug.LogWarning("SCALPAL_BRIEFING_STEPS_INVALID " + exception.Message); return Array.Empty<BriefingStep>(); }
        }
    }

    /// <summary>Bundled Jarvis lines (Resources/BriefingVoice). A clip plays only if it was rendered from the exact current line.</summary>
    public static class BriefingVoice
    {
        [Serializable] sealed class Manifest { public Entry[] clips; }
        [Serializable] sealed class Entry { public string stepId, text, resourcePath; }

        public static AudioClip Find(BriefingStep step)
        {
            if (step == null) return null;
            var file = Resources.Load<TextAsset>("BriefingVoice/manifest");
            if (!file) return null;
            Manifest manifest;
            try { manifest = JsonUtility.FromJson<Manifest>(file.text); } catch (ArgumentException) { return null; }
            if (manifest?.clips == null) return null;
            foreach (var entry in manifest.clips)
                if (entry.stepId == step.id && string.Equals(entry.text, step.line, StringComparison.Ordinal))
                    return Resources.Load<AudioClip>(entry.resourcePath);
            return null;
        }
    }
}
