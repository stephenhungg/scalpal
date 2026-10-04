// Pure math for MediaPipe's BlazeHand pipeline (palm detector -> rotated crop -> 21-joint landmarker),
// ported from Unity's official inference-engine BlazeDetectionSample and verified against Google's
// MediaPipe HandLandmarker (services/hands/reference.py: 1.4% of hand size mean error).
// No UnityEngine: image coordinates use Unity texture space (origin bottom-left, pixels).
using System;
using System.Globalization;

namespace Scalpal.Hands
{
    // 2x3 affine transform: (x, y) -> (a*x + b*y + c, d*x + e*y + f).
    public readonly struct Affine2
    {
        public readonly float a, b, c, d, e, f;

        public Affine2(float a, float b, float c, float d, float e, float f)
        {
            this.a = a; this.b = b; this.c = c; this.d = d; this.e = e; this.f = f;
        }

        public static Affine2 Translation(float x, float y) => new Affine2(1, 0, x, 0, 1, y);
        public static Affine2 Scale(float x, float y) => new Affine2(x, 0, 0, 0, y, 0);

        public static Affine2 Rotation(float theta)
        {
            var c = (float)Math.Cos(theta);
            var s = (float)Math.Sin(theta);
            return new Affine2(c, -s, 0, s, c, 0);
        }

        public static Affine2 operator *(Affine2 p, Affine2 q) => new Affine2(
            p.a * q.a + p.b * q.d, p.a * q.b + p.b * q.e, p.a * q.c + p.b * q.f + p.c,
            p.d * q.a + p.e * q.d, p.d * q.b + p.e * q.e, p.d * q.c + p.e * q.f + p.f);

        public (float x, float y) Apply(float x, float y) => (a * x + b * y + c, d * x + e * y + f);
    }

    // A square crop rotated so the hand points up, in top-left pixel space (y down), like the sample's tensor space.
    public struct HandRoi
    {
        public float centerX, centerY, size, rotation;
    }

    public static class BlazeHandMath
    {
        public const int NumAnchors = 2016;
        public const int NumJoints = 21;
        public const int DetectorSize = 192;
        public const int LandmarkerSize = 224;

        public static float Sigmoid(float x) => 1f / (1f + (float)Math.Exp(-Math.Max(-100f, Math.Min(100f, x))));

        public static float[,] LoadAnchors(string csv)
        {
            var anchors = new float[NumAnchors, 4];
            var lines = csv.Split('\n');
            for (var i = 0; i < NumAnchors; i++)
            {
                var values = lines[i].Split(',');
                for (var j = 0; j < 4; j++) anchors[i, j] = float.Parse(values[j], CultureInfo.InvariantCulture);
            }
            return anchors;
        }

        // Detector input: the whole image letterboxed into 192x192. Maps tensor pixels to Unity texture pixels.
        public static Affine2 DetectorToImage(int width, int height)
        {
            var size = Math.Max(width, height);
            var scale = size / (float)DetectorSize;
            return Affine2.Translation(0.5f * (width - size), 0.5f * (height + size)) * Affine2.Scale(scale, -scale);
        }

        // Up to maxHands best detections, skipping any whose centre falls inside an already chosen box.
        // boxes: 2016 x 18 raw regressors, scores: 2016 raw logits. Returns ROIs in detector tensor space.
        public static int SelectDetections(float[] boxes, float[] scores, float[,] anchors, float threshold, int maxHands, HandRoi[] rois, float[] roiScores)
        {
            var count = 0;
            var used = new bool[NumAnchors];
            while (count < maxHands)
            {
                var best = -1;
                var bestScore = threshold;
                for (var i = 0; i < NumAnchors; i++)
                {
                    if (used[i]) continue;
                    var s = Sigmoid(scores[i]);
                    if (s >= bestScore) { bestScore = s; best = i; }
                }
                if (best < 0) break;
                used[best] = true;
                var roi = DetectionToRoi(boxes, best, anchors);
                var overlaps = false;
                for (var k = 0; k < count; k++)
                {
                    var dx = roi.centerX - rois[k].centerX;
                    var dy = roi.centerY - rois[k].centerY;
                    if (Math.Sqrt(dx * dx + dy * dy) < 0.25f * Math.Max(roi.size, rois[k].size)) overlaps = true;
                }
                if (overlaps) continue;
                rois[count] = roi;
                roiScores[count] = bestScore;
                count++;
            }
            return count;
        }

        // Same construction as the official sample: centre shifted toward the fingers, box enlarged 2.6x,
        // rotation from palm keypoint 0 to keypoint 2.
        public static HandRoi DetectionToRoi(float[] boxes, int index, float[,] anchors)
        {
            var o = index * 18;
            var ax = DetectorSize * anchors[index, 0];
            var ay = DetectorSize * anchors[index, 1];
            var cx = ax + boxes[o + 0];
            var cy = ay + boxes[o + 1];
            var size = Math.Max(boxes[o + 2], boxes[o + 3]);
            var dx = boxes[o + 8] - boxes[o + 4];
            var dy = boxes[o + 9] - boxes[o + 5];
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f) len = 1e-6f;
            cx += 0.5f * size * dx / len;
            cy += 0.5f * size * dy / len;
            return new HandRoi { centerX = cx, centerY = cy, size = size * 2.6f, rotation = 0.5f * (float)Math.PI - (float)Math.Atan2(dy, dx) };
        }

        // Landmarker crop: 224x224 tensor pixels -> Unity texture pixels, given the frame space the ROI lives in.
        public static Affine2 LandmarkerToImage(Affine2 roiSpaceToImage, HandRoi roi)
        {
            var s = roi.size / LandmarkerSize;
            var half = 0.5f * LandmarkerSize;
            return roiSpaceToImage * Affine2.Translation(roi.centerX, roi.centerY) * Affine2.Scale(s, -s) * Affine2.Rotation(roi.rotation) * Affine2.Translation(-half, -half);
        }

        // Top-left pixel space (y down) to Unity texture space (y up).
        public static Affine2 TopLeftToImage(int height) => Affine2.Translation(0, height) * Affine2.Scale(1, -1);

        // MediaPipe-style tracking: the next frame's ROI from this frame's landmarks (top-left pixel space),
        // rotated so wrist (0) to middle knuckle (9) points up, square, a multiple of the landmark extent.
        // Defaults (1.8x, no shift) measured best against MediaPipe in services/hands/reference.py; a larger
        // scale keeps fast-moving hands inside the crop at a small accuracy cost.
        public static HandRoi RoiFromLandmarks(float[] xTopLeft, float[] yTopLeft, float scale = 1.8f, float shiftTowardFingers = 0f)
        {
            var dx = xTopLeft[9] - xTopLeft[0];
            var dy = yTopLeft[9] - yTopLeft[0];
            var rotation = 0.5f * (float)Math.PI - (float)Math.Atan2(dy, dx);
            var cos = (float)Math.Cos(-rotation);
            var sin = (float)Math.Sin(-rotation);
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            for (var i = 0; i < NumJoints; i++)
            {
                var u = cos * xTopLeft[i] - sin * yTopLeft[i];
                var v = sin * xTopLeft[i] + cos * yTopLeft[i];
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            var cu = 0.5f * (minU + maxU);
            var cv = 0.5f * (minV + maxV);
            var size = scale * Math.Max(maxU - minU, maxV - minV);
            var cx = cos * cu + sin * cv;
            var cy = -sin * cu + cos * cv;
            var len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-6f)
            {
                cx += shiftTowardFingers * size * dx / len;
                cy += shiftTowardFingers * size * dy / len;
            }
            return new HandRoi { centerX = cx, centerY = cy, size = size, rotation = rotation };
        }
    }

    // One Euro filter for display smoothing only. Recorded and streamed values stay raw.
    public sealed class OneEuroFilter
    {
        readonly float minCutoff, beta, dCutoff;
        float prev, prevDerivative;
        double prevTime = double.NaN;

        public OneEuroFilter(float minCutoff = 1.5f, float beta = 0.02f, float dCutoff = 1f)
        {
            this.minCutoff = minCutoff; this.beta = beta; this.dCutoff = dCutoff;
        }

        static float Alpha(float cutoff, float dt) => 1f / (1f + 1f / (2f * (float)Math.PI * cutoff * dt));

        public float Filter(float value, double time)
        {
            if (double.IsNaN(prevTime)) { prevTime = time; prev = value; return value; }
            var dt = (float)Math.Max(1e-4, time - prevTime);
            prevTime = time;
            var derivative = (value - prev) / dt;
            prevDerivative += Alpha(dCutoff, dt) * (derivative - prevDerivative);
            var cutoff = minCutoff + beta * Math.Abs(prevDerivative);
            prev += Alpha(cutoff, dt) * (value - prev);
            return prev;
        }

        public void Reset() { prevTime = double.NaN; prevDerivative = 0; }
    }
}
