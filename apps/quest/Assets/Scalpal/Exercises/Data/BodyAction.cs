using System;

namespace Scalpal.Exercises.Data
{
    // Semantic inputs, not claimed outcomes. Torso metres, distances mm, angles degrees,
    // monotonic device milliseconds; the shared body simulator derives consequences.
    [Serializable]
    public class BodyAction
    {
        public string actionId = "", instrumentId = "", instrumentInstanceId = "", secondaryInstanceId = "", verb = "";
        public string tissueId = "", layer = "", coordinateFrame = "registered_torso_m", choice = "";
        public double timeMs;
        public Vec3 position;
        public bool registered;
        public float speedMps, forceProxy, distanceMm, lengthMm, angleDegrees, depthMm, durationMs, separationMm, bloodLostMl, poolMl, flowMlPerSecond;
        public BodyAction Copy() => (BodyAction)MemberwiseClone();
    }
}
