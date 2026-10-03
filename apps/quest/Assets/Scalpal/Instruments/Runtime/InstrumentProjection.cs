using UnityEngine;

namespace Scalpal.Instruments
{
    // Known virtual-object projection, not image recognition or passthrough-camera calibration.
    public sealed class InstrumentProjection : MonoBehaviour
    {
        public bool TryGetPixelBounds(Camera camera, out Rect bounds, Camera.MonoOrStereoscopicEye eye = Camera.MonoOrStereoscopicEye.Mono)
        {
            bounds = default;
            if (camera == null) return false;
            Vector2 minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            bool found = false;
            foreach (var renderer in GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                Bounds world = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = world.center + Vector3.Scale(world.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 pixel = camera.WorldToScreenPoint(corner, eye);
                    // Hide a box crossing the near plane rather than presenting a misleading enormous rectangle.
                    if (pixel.z <= camera.nearClipPlane) return false;
                    minimum = Vector2.Min(minimum, new Vector2(pixel.x, pixel.y));
                    maximum = Vector2.Max(maximum, new Vector2(pixel.x, pixel.y));
                    found = true;
                }
            }
            Rect viewport = camera.pixelRect;
            minimum = Vector2.Max(minimum, viewport.min);
            maximum = Vector2.Min(maximum, viewport.max);
            if (!found || maximum.x <= minimum.x || maximum.y <= minimum.y) return false;
            bounds = Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
            return true;
        }

        public bool TryGetNormalizedTopLeftBounds(Camera camera, out Rect bounds, Camera.MonoOrStereoscopicEye eye = Camera.MonoOrStereoscopicEye.Mono)
        {
            bounds = default;
            if (!TryGetPixelBounds(camera, out Rect pixels, eye)) return false;
            Rect viewport = camera.pixelRect;
            if (viewport.width <= 0 || viewport.height <= 0) return false;
            bounds = new Rect((pixels.xMin - viewport.xMin) / viewport.width, 1 - (pixels.yMax - viewport.yMin) / viewport.height, pixels.width / viewport.width, pixels.height / viewport.height);
            return true;
        }
    }
}
