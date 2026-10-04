using System;
using Scalpal.Brand;
using TMPro;
using UnityEngine;

namespace Scalpal.Shell
{
    public sealed class ShellButton : MonoBehaviour, IScalpalPressable
    {
        public Action action;
        public bool interactable = true;
        // Primary actions (Start, Begin, Resume) carry the spark accent at rest, not only on focus.
        public bool primary;
        // Secondary actions (for example "Skip to surgery"): ghost outline, accent only on focus.
        public bool ghost;
        public TextMeshPro label;
        public bool lift;
        public bool hovered => Time.unscaledTime < until;
        float until;
        int lastAppearance=-1;
        Vector3 rest;
        Renderer surface;
        MaterialPropertyBlock block;
        public bool Pressable => interactable && isActiveAndEnabled;
        void Awake() { rest = transform.localPosition; Bind(); }
        void Bind() { if (!surface) surface = GetComponent<Renderer>(); block ??= new MaterialPropertyBlock(); }
        public void Highlight() { if (interactable) until = Time.unscaledTime + .3f; }
        public void Hover(Vector3 point) => Highlight();
        public void Press() { if (interactable && isActiveAndEnabled) action?.Invoke(); }
        /// <summary>Applies the resting appearance immediately (also used before the first frame and in previews).</summary>
        public void Refresh() { lastAppearance = -1; Apply(); }
        void Update()
        {
            Apply();
            if (lift) transform.localPosition = Vector3.Lerp(transform.localPosition, rest + (hovered ? new Vector3(0,.012f,-.012f) : Vector3.zero), 1 - Mathf.Exp(-Time.unscaledDeltaTime * 16));
        }
        void Apply()
        {
            Bind();
            if (!surface) return;
            int appearance=!interactable?0:hovered?2:1;
            if(appearance==lastAppearance)return;
            lastAppearance=appearance;
            var fill=!interactable?ScalpalBrand.ButtonDisabledTint:hovered?ScalpalBrand.ButtonHoverTint:ghost?ScalpalBrand.GhostTint:ScalpalBrand.ButtonTint;
            ScalpalBrand.Tint(surface,block,fill,interactable&&(hovered||primary&&!ghost),!interactable?.5f:1);
            if(ghost&&interactable&&!hovered){block.SetColor("_RimColor",ScalpalBrand.GhostRim);block.SetColor("_RimColorB",ScalpalBrand.GhostRim);surface.SetPropertyBlock(block);}
            if(label)label.color=interactable?ScalpalBrand.Ink:ScalpalBrand.InkDisabled;
        }
    }
}
