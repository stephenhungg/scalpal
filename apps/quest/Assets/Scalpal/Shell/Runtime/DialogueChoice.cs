using Scalpal.Brand;
using UnityEngine;

namespace Scalpal.Shell
{
    // One selectable interview choice row in the dialogue box. The office's brand pointer (controller aim ray +
    // trigger, or hand pinch) hovers and presses it; the box reports the key through ChoicePicked.
    public sealed class DialogueChoice : MonoBehaviour, IScalpalPressable
    {
        public DialogueBox box;
        public string key = "";
        float hoveredUntil;
        public bool Hovered => Time.unscaledTime < hoveredUntil;
        public bool Pressable => box && box.ChoicesVisible && gameObject.activeInHierarchy;
        public void Hover(Vector3 point) => Highlight();
        public void Highlight() { hoveredUntil = Time.unscaledTime + .08f; }
        public void Press() { if (box && gameObject.activeInHierarchy) box.Pick(key); }
    }
}
