using System;
using UnityEngine;

namespace Scalpal.EncounterOffice
{
    // Only an exact returned, authored fact can select its bundled recording.
    // This cache cannot answer a question or change encounter state itself.
    public static class EncounterPatientSpeech
    {
        [Serializable] sealed class Manifest { public Entry[] entries; }
        [Serializable] sealed class Entry
        {
            public string patientId, tool, argument, exactDisplayText, resourcePath;
        }
        static Manifest manifest;

        public static AudioClip Find(string patientId, string tool, string argument, string display)
        {
            if (manifest == null)
            {
                var file = Resources.Load<TextAsset>("EncounterSpeech/manifest");
                if (!file) return null;
                try { manifest = JsonUtility.FromJson<Manifest>(file.text); }
                catch (ArgumentException) { return null; }
            }
            if (manifest?.entries == null || string.IsNullOrEmpty(display)) return null;
            foreach (var entry in manifest.entries)
            {
                if (entry.patientId == patientId && entry.tool == tool && entry.argument == (argument ?? "") &&
                    string.Equals(entry.exactDisplayText, display, StringComparison.Ordinal))
                    return Resources.Load<AudioClip>(entry.resourcePath);
            }
            return null;
        }
    }
}
