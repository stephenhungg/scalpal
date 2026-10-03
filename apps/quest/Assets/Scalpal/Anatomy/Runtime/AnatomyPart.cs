using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Anatomy
{
    // Each source structure has its own identity. Do not infer exercise ids from anatomical labels.
    [DisallowMultipleComponent]
    public sealed class AnatomyPart : MonoBehaviour
    {
        public string stableId = "";
        public string displayName = "";
        public string system = "organs";
        [Tooltip("Optional shared transparent material. Overlapping ghost layers increase Quest overdraw.")]
        public Material ghostMaterial;

        public bool IsGhosted { get; private set; }
        public bool CanGhost
        {
            get { EnsureCached(); return ghostMaterial != null && renderers.Count > 0; }
        }

        public bool IsVisible { get; private set; }
        public bool IsHighlighted { get; private set; }
        public bool HasVisibleGeometry
        {
            get
            {
                EnsureCached();
                foreach (var state in renderers)
                    if (state.renderer != null && state.renderer.enabled && state.renderer.gameObject.activeInHierarchy)
                        return true;
                return false;
            }
        }

        sealed class RendererState
        {
            public Renderer renderer;
            public bool enabled;
            public MaterialPropertyBlock[] originalBlocks;
            public Material[] ghostOriginalMaterials;
        }
        sealed class ColliderState
        {
            public Collider collider;
            public bool enabled;
        }

        readonly List<RendererState> renderers = new List<RendererState>();
        readonly List<ColliderState> colliders = new List<ColliderState>();
        bool cached;
        static readonly int Emission = Shader.PropertyToID("_EmissionColor");
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int LegacyColor = Shader.PropertyToID("_Color");

        void EnsureCached()
        {
            if (cached) return;
            cached = true;
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.GetComponentInParent<AnatomyPart>(true) != this) continue;
                renderers.Add(new RendererState { renderer = renderer, enabled = renderer.enabled });
            }
            foreach (var collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider.GetComponentInParent<AnatomyPart>(true) != this) continue;
                colliders.Add(new ColliderState { collider = collider, enabled = collider.enabled });
            }
        }

        // Renderer/collider switches keep identities discoverable even while hidden.
        public void SetVisible(bool visible)
        {
            EnsureCached();
            IsVisible = visible;
            foreach (var state in renderers)
                if (state.renderer != null) state.renderer.enabled = visible && state.enabled;
            foreach (var state in colliders)
                if (state.collider != null) state.collider.enabled = visible && state.enabled;
            if (!visible) ClearHighlight();
        }

        // Uses one pre-authored shared transparent material for every slot. No material instances.
        // Visibility and registration changes keep this preference for the next visible frame.
        public bool SetGhosted(bool ghosted)
        {
            EnsureCached();
            if (ghosted && !CanGhost) return false;
            if (IsGhosted == ghosted) return true;
            ClearHighlight();
            foreach (var state in renderers)
            {
                if (state.renderer == null) continue;
                if (ghosted)
                {
                    state.ghostOriginalMaterials = state.renderer.sharedMaterials;
                    var ghostSlots = new Material[state.ghostOriginalMaterials.Length];
                    for (var i = 0; i < ghostSlots.Length; i++) ghostSlots[i] = ghostMaterial;
                    state.renderer.sharedMaterials = ghostSlots;
                }
                else if (state.ghostOriginalMaterials != null)
                {
                    state.renderer.sharedMaterials = state.ghostOriginalMaterials;
                    state.ghostOriginalMaterials = null;
                }
            }
            IsGhosted = ghosted;
            return true;
        }

        public bool SetHighlight(Color color)
        {
            EnsureCached();
            ClearHighlight();
            if (!IsVisible || !HasVisibleGeometry) return false;
            var applied = false;
            foreach (var state in renderers)
            {
                var renderer = state.renderer;
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                var materials = renderer.sharedMaterials;
                state.originalBlocks = new MaterialPropertyBlock[materials.Length];
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null) continue;
                    var emission = material.HasProperty(Emission) && material.IsKeywordEnabled("_EMISSION");
                    var colorProperty = material.HasProperty(BaseColor) ? BaseColor : LegacyColor;
                    if (!emission && !material.HasProperty(colorProperty)) continue;
                    var original = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(original, i);
                    state.originalBlocks[i] = original;
                    var block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block, i);
                    // A per-material block overrides the renderer-wide block, so inherit it first.
                    if (block.isEmpty) renderer.GetPropertyBlock(block);
                    block.SetColor(emission ? Emission : colorProperty, color);
                    renderer.SetPropertyBlock(block, i);
                    applied = true;
                }
            }
            IsHighlighted = applied;
            return applied;
        }

        public void ClearHighlight()
        {
            foreach (var state in renderers)
            {
                if (state.renderer != null && state.originalBlocks != null)
                    for (var i = 0; i < state.originalBlocks.Length; i++)
                    {
                        var original = state.originalBlocks[i];
                        if (original != null)
                            state.renderer.SetPropertyBlock(original.isEmpty ? null : original, i);
                    }
                state.originalBlocks = null;
            }
            IsHighlighted = false;
        }

        void OnDisable() { ClearHighlight(); }
    }
}
