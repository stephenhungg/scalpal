using UnityEngine;

namespace Scalpal.Surgery
{
    // Excludes render-only replacements from source-anatomy authoring searches.
    // These meshes never supply contact, deformation or longitudinal measurements.
    [DisallowMultipleComponent]
    public sealed class SurgicalVisualGeometry : MonoBehaviour { }
}
