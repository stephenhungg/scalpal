using System.Collections.Generic;
using UnityEngine;

namespace Scalpal.Surgery
{
    // Geometry uses metres in a single registered frame. The caller must transform world/controller
    // points into that frame; none of these measurements infer depth from camera pixels.
    public sealed class OpenSurgeryStroke
    {
        readonly List<Vector2> projectedSegments = new List<Vector2>();
        Vector3 previous, start, origin, axis = Vector3.right, normal = Vector3.forward;
        bool sampling;
        float minimum, maximum, maximumError, maximumDepth, elapsed, pathLength, referenceLength = .06f;
        public float LengthMm => sampling ? (maximum - minimum) * 1000 : 0;
        public float PathLengthMm => pathLength * 1000;
        public float ErrorMm => maximumError * 1000;
        public float DepthMm => maximumDepth * 1000;
        public float DurationMs => elapsed * 1000;
        public float AngleDegrees { get; private set; }
        public Vector3 Start => start;
        public Vector3 End => previous;
        public void Reset()
        {
            sampling = false; minimum = maximum = maximumError = maximumDepth = elapsed = pathLength = 0;
            AngleDegrees = 0; projectedSegments.Clear();
        }
        public bool ConfigureLine(Vector3 lineStart, Vector3 lineEnd, Vector3 inwardNormal)
        {
            if (!Finite(lineStart) || !Finite(lineEnd) || !Finite(inwardNormal) || inwardNormal.sqrMagnitude < .000001f) return false;
            var direction = Vector3.ProjectOnPlane(lineEnd - lineStart, inwardNormal);
            if (direction.sqrMagnitude < .000001f) return false;
            Reset(); origin = lineStart; normal = inwardNormal.normalized;
            referenceLength = direction.magnitude; axis = direction.normalized; return true;
        }
        // Off-line strokes are valid measurements, not rejected actions. Only invalid time/pose or
        // a tracking discontinuity reset the sample. planeDepth is measured along inwardNormal.
        public bool Sample(Vector3 point, float seconds, float planeDepth)
        {
            if (!Finite(point) || !float.IsFinite(seconds) || !float.IsFinite(planeDepth) || seconds <= 0 || seconds > .1f)
            { Reset(); return false; }
            if (sampling && Vector3.Distance(previous, point) > .05f) { Reset(); return false; }
            float along = Vector3.Dot(point - origin, axis);
            Vector3 lateral = Vector3.ProjectOnPlane(point - origin, normal) - axis * along;
            if (!sampling) { sampling = true; start = point; minimum = maximum = along; }
            else
            {
                pathLength += Vector3.Distance(previous, point);
                float prior = Vector3.Dot(previous - origin, axis);
                projectedSegments.Add(new Vector2(Mathf.Min(prior, along), Mathf.Max(prior, along)));
            }
            minimum = Mathf.Min(minimum, along); maximum = Mathf.Max(maximum, along);
            maximumError = Mathf.Max(maximumError, lateral.magnitude);
            maximumDepth = Mathf.Max(maximumDepth, Mathf.Max(0, Vector3.Dot(point - origin, normal) - planeDepth));
            elapsed += seconds; previous = point;
            Vector3 travel = Vector3.ProjectOnPlane(point - start, normal);
            if (travel.sqrMagnitude > .000001f) AngleDegrees = Mathf.Min(Vector3.Angle(travel, axis), Vector3.Angle(travel, -axis));
            return true;
        }
        // Unique length covered along the authored line, so back-and-forth strokes cannot inflate coverage.
        // An off-line stroke deliberately has zero coverage but still reports its error/depth to the body.
        public float Coverage(float toleranceMm = 5)
        {
            if (!sampling || ErrorMm > toleranceMm || referenceLength <= 0 || projectedSegments.Count == 0) return 0;
            var intervals = new List<Vector2>(projectedSegments); intervals.Sort((a, b) => a.x.CompareTo(b.x));
            float covered = 0, end = 0;
            foreach (var segment in intervals)
            {
                float first = Mathf.Clamp(segment.x, 0, referenceLength), last = Mathf.Clamp(segment.y, 0, referenceLength);
                covered += Mathf.Max(0, last - Mathf.Max(first, end)); end = Mathf.Max(end, last);
            }
            return Mathf.Clamp01(covered / referenceLength);
        }
        public static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
        public static Vector3 McBurney(Vector3 rightAsis, Vector3 umbilicus) => Vector3.Lerp(rightAsis, umbilicus, 1f / 3f);
        public static float DistanceToSegmentMm(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 line = b - a;
            float t = line.sqrMagnitude > .00000001f ? Mathf.Clamp01(Vector3.Dot(point - a, line) / line.sqrMagnitude) : 0;
            return Vector3.Distance(point, a + line * t) * 1000;
        }
        public static bool CutBetween(float cut, float first, float second) => float.IsFinite(cut) && float.IsFinite(first) && float.IsFinite(second) && Mathf.Abs(first-second)>=.004f && cut>Mathf.Min(first,second) && cut<Mathf.Max(first,second);
    }
}
