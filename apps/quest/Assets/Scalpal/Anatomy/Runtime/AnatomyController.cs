using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Anatomy
{
    [DisallowMultipleComponent]
    public sealed class AnatomyController : MonoBehaviour
    {
        [Tooltip("Only the selection pedestal may show anatomy before participant registration.")]
        [SerializeField] bool previewMode;
        [SerializeField] bool rotatePreview = true;
        [SerializeField] float previewDegreesPerSecond = 12f;
        [SerializeField] Transform previewRotationRoot;
        public string[] initiallyHiddenSystems = new[] { "surface" };
        [SerializeField] Color highlightColor = new Color(1f, 0.65f, 0.1f, 1f);

        readonly Dictionary<string, AnatomyPart> index = new Dictionary<string, AnatomyPart>(StringComparer.Ordinal);
        readonly HashSet<string> ambiguousIds = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> hiddenSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AnatomyPart[] parts = Array.Empty<AnatomyPart>();
        string isolatedId, desiredHighlight;
        HashSet<string> exerciseParts;
        bool registrationValid;
        bool initialFiltersApplied;

        public bool RegistrationValid => registrationValid;
        public bool PreviewMode => previewMode;
        public bool CanDisplay => isActiveAndEnabled && (previewMode || registrationValid);
        public IReadOnlyList<AnatomyPart> Parts => parts;
        public string HighlightedPartId
        {
            get
            {
                if (!CanDisplay) return "";
                foreach (var part in parts) if (part && part.IsVisible && part.IsHighlighted) return part.stableId;
                return "";
            }
        }
        public event Action<bool> RegistrationChanged;

        void Awake() { RebuildIndex(); }
        void OnEnable() { RefreshVisibility(); }
        void OnDisable()
        {
            foreach (var part in parts) if (part != null) part.SetVisible(false);
        }
        void Update()
        {
            if (previewMode && rotatePreview && CanDisplay)
                (previewRotationRoot != null ? previewRotationRoot : transform)
                    .Rotate(Vector3.up, previewDegreesPerSecond * Time.deltaTime, Space.Self);
        }

        public void RebuildIndex()
        {
            if (!initialFiltersApplied)
            {
                initialFiltersApplied = true;
                if (initiallyHiddenSystems != null)
                    foreach (var system in initiallyHiddenSystems)
                        if (!string.IsNullOrWhiteSpace(system)) hiddenSystems.Add(system);
            }
            ClearHighlight();
            index.Clear();
            ambiguousIds.Clear();
            var owned = new List<AnatomyPart>();
            foreach (var part in GetComponentsInChildren<AnatomyPart>(true))
            {
                if (part.GetComponentInParent<AnatomyController>(true) != this) continue;
                owned.Add(part);
                var id = part.stableId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (index.ContainsKey(id) || ambiguousIds.Contains(id))
                {
                    index.Remove(id);
                    ambiguousIds.Add(id);
                    Debug.LogWarning("AnatomyController: duplicate structure id is not addressable: " + id, this);
                }
                else index.Add(id, part);
            }
            parts = owned.ToArray();
            RefreshVisibility();
        }

        public bool TryGetPart(string id, out AnatomyPart part)
        {
            part = null;
            return id != null && index.TryGetValue(id, out part) && part != null;
        }

        public bool SetSystemVisible(string system, bool visible)
        {
            if (string.IsNullOrWhiteSpace(system)) return false;
            var found = false;
            foreach (var part in parts)
                if (part != null && string.Equals(part.system, system, StringComparison.OrdinalIgnoreCase)) found = true;
            if (!found) return false;
            if (visible) hiddenSystems.Remove(system); else hiddenSystems.Add(system);
            RefreshVisibility();
            return true;
        }

        // All members must have a pre-authored ghost material before enabling the system.
        // Reject unsupported requests without partially changing the system.
        public bool SetSystemGhosted(string system, bool ghosted)
        {
            if (string.IsNullOrWhiteSpace(system)) return false;
            var members = new List<AnatomyPart>();
            foreach (var part in parts)
            {
                if (part == null || !string.Equals(part.system, system, StringComparison.OrdinalIgnoreCase)) continue;
                if (ghosted && !part.CanGhost) return false;
                members.Add(part);
            }
            if (members.Count == 0) return false;
            foreach (var part in members) part.SetGhosted(ghosted);
            return true;
        }

        // Isolation temporarily overrides system filters. RestoreVisibility brings those filters back.
        public bool Isolate(string id)
        {
            if (!CanDisplay || !TryGetPart(id, out _) || (exerciseParts != null && !exerciseParts.Contains(id))) return false;
            isolatedId = id;
            RefreshVisibility();
            return true;
        }

        public void RestoreVisibility()
        {
            isolatedId = null;
            RefreshVisibility();
        }

        public void ShowAllSystems()
        {
            hiddenSystems.Clear();
            RestoreVisibility();
        }

        // Case context is independent of temporary isolation and layer visibility.
        // Unknown/ambiguous IDs reject the entire update instead of hiding required targets.
        public bool SetExerciseParts(IEnumerable<string> ids)
        {
            HashSet<string> next = null;
            if (ids != null)
            {
                next = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    if (!TryGetPart(id, out _)) return false;
                    next.Add(id);
                }
            }
            exerciseParts = next;
            isolatedId = null;
            RefreshVisibility();
            return true;
        }

        public bool Highlight(string id)
        {
            if (!CanDisplay || !TryGetPart(id, out var part) || !part.IsVisible) return false;
            ClearHighlight();
            bool applied = part.SetHighlight(highlightColor);
            if (applied) desiredHighlight = id;
            return applied;
        }

        public void ClearHighlight()
        {
            desiredHighlight = null;
            foreach (var part in parts) if (part != null) part.ClearHighlight();
        }

        public void SetRegistrationValid(bool valid)
        {
            var changed = registrationValid != valid;
            registrationValid = valid;
            RefreshVisibility();
            if (changed) RegistrationChanged?.Invoke(valid);
        }

        public void SetPreviewMode(bool enabled)
        {
            previewMode = enabled;
            RefreshVisibility();
        }

        public void SetPreviewRotation(bool enabled) { rotatePreview = enabled; }

        void RefreshVisibility()
        {
            foreach (var part in parts)
            {
                if (part == null) continue;
                var inCase = exerciseParts == null || exerciseParts.Contains(part.stableId);
                var selected = inCase && (isolatedId != null ? part.stableId == isolatedId : !hiddenSystems.Contains(part.system ?? ""));
                part.SetVisible(CanDisplay && selected);
                if (part.IsVisible && part.stableId == desiredHighlight && !part.IsHighlighted)
                    part.SetHighlight(highlightColor);
            }
        }
    }
}
