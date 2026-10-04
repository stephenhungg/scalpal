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
        // Doubles, like the JSON numbers services/preop/src/open-body.ts reduces, so both reducers see one value.
        public double speedMps, forceProxy, distanceMm, lengthMm, angleDegrees, depthMm, durationMs, separationMm, bloodLostMl, poolMl, flowMlPerSecond;
        public BodyAction Copy() => (BodyAction)MemberwiseClone();

        // Event-boundary resolution (docs/surgery-state.md): whole mm, ms and degrees, mm/s speed,
        // 0.01 force ratio and ml, 0.001 ml/s flow, 0.1 mm position. The headset applies the same
        // rounded values it sends, so C# and TypeScript reducers cannot disagree at a threshold.
        public void Quantize()
        {
            timeMs = Math.Round(timeMs, MidpointRounding.AwayFromZero);
            distanceMm = Math.Round(distanceMm, MidpointRounding.AwayFromZero); lengthMm = Math.Round(lengthMm, MidpointRounding.AwayFromZero); depthMm = Math.Round(depthMm, MidpointRounding.AwayFromZero);
            separationMm = Math.Round(separationMm, MidpointRounding.AwayFromZero); angleDegrees = Math.Round(angleDegrees, MidpointRounding.AwayFromZero); durationMs = Math.Round(durationMs, MidpointRounding.AwayFromZero);
            speedMps = Math.Round(speedMps * 1000, MidpointRounding.AwayFromZero) / 1000; forceProxy = Math.Round(forceProxy * 100, MidpointRounding.AwayFromZero) / 100;
            bloodLostMl = Math.Round(bloodLostMl * 100, MidpointRounding.AwayFromZero) / 100; poolMl = Math.Round(poolMl * 100, MidpointRounding.AwayFromZero) / 100;
            flowMlPerSecond = Math.Round(flowMlPerSecond * 1000, MidpointRounding.AwayFromZero) / 1000;
            position = new Vec3 { x = (float)(Math.Round(position.x * 10000.0, MidpointRounding.AwayFromZero) / 10000), y = (float)(Math.Round(position.y * 10000.0, MidpointRounding.AwayFromZero) / 10000),
                z = (float)(Math.Round(position.z * 10000.0, MidpointRounding.AwayFromZero) / 10000) };
        }
    }
}
