using UnityEngine;

namespace Scalpal.Anatomy.Tissue
{
    // Preview/reference presentation only; never adds targets to the authored case.
    public sealed class AnatomyLayerView : MonoBehaviour
    {
        public enum Layer { Organs, Surface, AbdominalWall, Skeleton, Vessels }
        public Layer Current { get; private set; }
        public void Cycle() => Show((Layer)(((int)Current + 1) % 5));
        public void Show(Layer layer)
        {
            var controller = GetComponent<AnatomyController>();
            if (!controller) return;
            Current = layer;
            foreach (var system in new[] { "surface", "muscular", "skeletal", "cardiovascular", "visceral", "exercise-targets", "lymphatic" })
            {
                bool visible = layer == Layer.Organs ? system != "surface" && system != "muscular" && system != "skeletal" :
                    layer == Layer.Surface ? system == "surface" : layer == Layer.AbdominalWall ? system == "muscular" :
                    layer == Layer.Skeleton ? system == "skeletal" : system == "cardiovascular";
                controller.SetSystemVisible(system, visible);
            }
            controller.RestoreVisibility();
        }
    }
}
