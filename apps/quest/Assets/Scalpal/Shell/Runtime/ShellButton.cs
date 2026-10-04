using System;
using UnityEngine;

namespace Scalpal.Shell
{
    public sealed class ShellButton : MonoBehaviour
    {
        public Action action;
        public bool interactable = true;
        public TextMesh label;
        public bool lift;
        public bool hovered => Time.unscaledTime < until;
        float until;
        Vector3 rest;
        Renderer surface;
        MaterialPropertyBlock block;
        void Awake() { rest = transform.localPosition; surface = GetComponent<Renderer>(); block = new MaterialPropertyBlock(); }
        public void Highlight() { if (interactable) until = Time.unscaledTime + .3f; }
        public void Press() { if (interactable && isActiveAndEnabled) action?.Invoke(); }
        void Update()
        {
            if (!surface) return;
            block.SetColor("_Color", !interactable ? new Color(.065f,.075f,.10f,.96f) : hovered ? new Color(.17f,.14f,.24f,.98f) : new Color(.065f,.082f,.11f,.96f));
            surface.SetPropertyBlock(block);
            if (lift) transform.localPosition = Vector3.Lerp(transform.localPosition, rest + (hovered ? new Vector3(0,.012f,-.012f) : Vector3.zero), 1 - Mathf.Exp(-Time.unscaledDeltaTime * 16));
        }
    }
}
