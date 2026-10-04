using Scalpal.Brand;
using UnityEngine;

namespace Scalpal.Handoff
{
    /// <summary>One Theatre/Time-Out action surface: the pointer ray's collider target.</summary>
    public sealed class HandoffCardTarget : MonoBehaviour, IScalpalPressable
    {
        public HandoffCard card;
        public int index;
        public bool Pressable => card && card.CanPress(index);
        public void Hover(Vector3 point) { }
        public void Press() { if (card) card.PressTarget(index); }
    }
}
