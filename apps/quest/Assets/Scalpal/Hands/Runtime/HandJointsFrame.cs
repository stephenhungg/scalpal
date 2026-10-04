// One camera frame of MediaPipe hand joints: the record the headset overlay draws, the live stream sends
// to the Mac, and the recorder writes as training data (one JSON line per frame, JsonUtility-safe).
// Coordinates: pixels are top-left image pixels of the passthrough frame; worldModelMeters is MediaPipe's
// metric hand-centred frame; jointsWorld is Unity world space (meters) after lifting with the camera pose.
using System;

namespace Scalpal.Hands
{
    [Serializable]
    public class HandObservation
    {
        // Physical hand, corrected for MediaPipe assuming a mirrored image ("right" or "left").
        public string hand;
        // Raw landmarker handedness output; above 0.5 is MediaPipe's "Left" label on an unmirrored image.
        public float handednessRaw;
        public float presence;
        // "detector" when this frame ran the palm detector, "tracking" when the crop came from the last frame.
        public string source;
        public float depthMeters;
        public float[] pixels = new float[BlazeHandMath.NumJoints * 2];
        public float[] worldModelMeters = new float[BlazeHandMath.NumJoints * 3];
        public float[] jointsWorld = new float[BlazeHandMath.NumJoints * 3];
    }

    [Serializable]
    public class HandJointsFrame
    {
        public string schema = "scalpal.hand_joints.v1";
        public string sessionId = "";
        public int frameIndex;
        // Passthrough image timestamp in ms when the camera API provides one, else -1.
        public double cameraTimestampMs = -1;
        public double unityTime;
        public int imageWidth;
        public int imageHeight;
        public float[] cameraPosition = new float[3];
        // Quaternion x, y, z, w.
        public float[] cameraRotation = new float[4];
        public float inferenceMs;
        public HandObservation[] hands = new HandObservation[0];
    }
}
