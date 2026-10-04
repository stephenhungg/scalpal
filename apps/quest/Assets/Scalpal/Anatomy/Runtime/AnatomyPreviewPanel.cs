using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Anatomy
{
    // Optional mouse/keyboard inspector for the generated desktop preview scene only.
    // Android/iOS players contain no IMGUI panel; the Editor keeps it for any build target.
    // This is not an XR interaction surface.
    [DisallowMultipleComponent]
    public sealed class AnatomyPreviewPanel : MonoBehaviour
    {
        public AnatomyController anatomy;

#if UNITY_EDITOR || (!UNITY_ANDROID && !UNITY_IOS)
        const int MaximumMatches = 20;
        readonly List<string> systems = new List<string>();
        AnatomyController indexedAnatomy;
        int indexedCount = -1;
        string query = "";
        string feedback = "Select a system or search for an anatomical structure.";
        bool rotating = true;
        Vector2 scroll;

        void OnGUI()
        {
            var width = Math.Min(360f, Math.Max(180f, Screen.width - 24f));
            GUILayout.BeginArea(new Rect(12, 12, width, Math.Max(100f, Screen.height - 24f)), GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label("Scalpal anatomy | desktop preview");
            GUILayout.Label("Mouse controls only. Not XR UI or participant registration.");
            if (anatomy == null)
            {
                GUILayout.Label("No anatomy controller assigned to this preview panel.");
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }
            RefreshSystems();
            if (!anatomy.PreviewMode)
                GUILayout.Label("Preview mode is off. Practice visibility requires valid registration.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Restore view"))
            {
                anatomy.RestoreVisibility();
                anatomy.ClearHighlight();
                feedback = "Isolation and highlight cleared; your system filters are preserved.";
            }
            if (GUILayout.Button(rotating ? "Pause rotation" : "Rotate"))
            {
                rotating = !rotating;
                anatomy.SetPreviewRotation(rotating);
                feedback = rotating ? "Preview rotation enabled." : "Preview rotation paused.";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Systems (ghost layers cost additional rendering work)");
            foreach (var system in systems)
            {
                var visible = false;
                var ghosted = false;
                foreach (var part in anatomy.Parts)
                {
                    if (part == null || !string.Equals(part.system, system, StringComparison.OrdinalIgnoreCase)) continue;
                    visible |= part.IsVisible;
                    ghosted |= part.IsGhosted;
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(system, GUILayout.Width(135));
                if (GUILayout.Button(visible ? "Hide" : "Show", GUILayout.Width(70)))
                {
                    // System changes exit isolation so the requested layer action is observable.
                    anatomy.RestoreVisibility();
                    var applied = anatomy.SetSystemVisible(system, !visible);
                    feedback = applied ? system + (visible ? " hidden." : " enabled (registration gate still applies).")
                        : "Layer change rejected: system is unavailable.";
                }
                if (GUILayout.Button(ghosted ? "Solid" : "Ghost", GUILayout.Width(70)))
                {
                    var applied = anatomy.SetSystemGhosted(system, !ghosted);
                    feedback = applied ? system + (ghosted ? " uses solid materials." : " uses see-through materials.")
                        : "Ghost change rejected: this system has missing geometry or ghost materials.";
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(8);
            GUILayout.Label("Find a source label or stable id");
            query = GUILayout.TextField(query);
            var matches = FindMatches(query);
            GUILayout.Label("Showing up to 20 matches; narrow the search for more.");
            foreach (var part in matches)
            {
                GUILayout.Label(string.IsNullOrEmpty(part.displayName) ? part.stableId : part.displayName);
                GUILayout.Label(part.stableId);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Isolate"))
                    feedback = anatomy.Isolate(part.stableId) ? "Isolated " + part.displayName + "."
                        : "Isolation rejected: anatomy is hidden, registration is invalid, or the id is ambiguous.";
                if (GUILayout.Button("Highlight"))
                    feedback = anatomy.Highlight(part.stableId) ? "Highlighted " + part.displayName + "."
                        : "Highlight rejected: show or isolate this structure first; its id and material must be supported.";
                GUILayout.EndHorizontal();
            }
            if (matches.Count == 0) GUILayout.Label("No matching structures in this atlas.");
            GUILayout.Space(8);
            GUILayout.Label(feedback);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        internal List<AnatomyPart> FindMatches(string search)
        {
            var matches = new List<AnatomyPart>();
            if (anatomy == null) return matches;
            search = (search ?? "").Trim();
            foreach (var part in anatomy.Parts)
            {
                if (part == null) continue;
                if (search.Length != 0 && (part.displayName ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                    && (part.stableId ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matches.Add(part);
                if (matches.Count == MaximumMatches) break;
            }
            return matches;
        }

        void RefreshSystems()
        {
            if (indexedAnatomy == anatomy && indexedCount == anatomy.Parts.Count) return;
            indexedAnatomy = anatomy;
            indexedCount = anatomy.Parts.Count;
            systems.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in anatomy.Parts)
                if (part != null && !string.IsNullOrWhiteSpace(part.system) && seen.Add(part.system)) systems.Add(part.system);
            systems.Sort(StringComparer.OrdinalIgnoreCase);
        }
#endif
    }
}
