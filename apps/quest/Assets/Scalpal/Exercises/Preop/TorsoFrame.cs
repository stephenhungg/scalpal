// Converts service coordinates into Unity space. The torso root (owned by registration) must have
// local +Z toward the participant's head and local +Y out of the abdomen; local +X is then the
// participant's left, matching the service's torso frame without mirroring.
using Scalpal.Exercises.Data;
using UnityEngine;

namespace Scalpal.Exercises.Preop
{
    public static class TorsoFrame
    {
        public static Vector3 ToVector3(this Vec3 v) => new Vector3(v.x, v.y, v.z);

        // Local position for a port under the torso root, scaled for the patient's body size.
        public static Vector3 PortLocalPosition(Port port, float bodyScale) => port.position.ToVector3() * Mathf.Max(0.1f, bodyScale);
    }
}
