// Live MediaPipe hand joints on the Quest: runs the palm detector and the 21-joint landmarker (MediaPipe's
// models, converted by Unity) on each passthrough frame, tracks hands between frames like MediaPipe, and
// lifts the joints into world space with the camera pose captured for that frame.
// Compiles only when the Inference Engine and MRUK packages are present (SCALPAL_HANDS, set by
// Editor/ScalpalHandsDefine.cs), so projects without them are unaffected.
#if SCALPAL_HANDS
using System;
using System.Collections.Generic;
using Meta.XR;
using Unity.InferenceEngine;
using UnityEngine;

namespace Scalpal.Hands
{
    [DisallowMultipleComponent]
    public sealed class BlazeHandTracker : MonoBehaviour
    {
        [SerializeField] PassthroughCameraAccess cameraAccess;
        [SerializeField] ModelAsset handDetector;
        [SerializeField] ModelAsset handLandmarker;
        [SerializeField] TextAsset anchorsCsv;
        [Tooltip("Resources path of HandImageTransform.compute.")]
        [SerializeField] string imageTransformShader = "ComputeShaders/HandImageTransform";
        [Tooltip("The camera baseline measured CPU faster than GPUCompute for YOLO; benchmark both for these models.")]
        [SerializeField] BackendType backend = BackendType.GPUCompute;
        [Range(1, 2)] [SerializeField] int maxHands = 2;
        [SerializeField] float detectionThreshold = 0.5f;
        [SerializeField] float presenceThreshold = 0.5f;
        [Tooltip("Run the palm detector at least this often even while tracking, to pick up a second hand.")]
        [SerializeField] int redetectEveryFrames = 15;
        [Tooltip("Convert linear texture samples to sRGB, as the official sample does. Turn off if joints look poor on raw camera textures.")]
        [SerializeField] bool convertToSrgb = true;
        [SerializeField] string sessionId = "";

        public event Action<HandJointsFrame> FrameReady;
        public HandJointsFrame LastFrame { get; private set; }
        public string SessionId => sessionId;

        float[,] anchors;
        Worker detectorWorker;
        Worker landmarkerWorker;
        Tensor<float> detectorInput;
        Tensor<float> landmarkerInput;
        ComputeShader shader;
        int kernel;
        readonly List<HandRoi> tracked = new List<HandRoi>();
        int frameIndex;
        bool running;
        System.Reflection.PropertyInfo timestampProperty;

        static readonly int Optr = Shader.PropertyToID("Optr");
        static readonly int XTex = Shader.PropertyToID("X_tex2D");
        static readonly int OHeight = Shader.PropertyToID("O_height");
        static readonly int OWidth = Shader.PropertyToID("O_width");
        static readonly int OChannels = Shader.PropertyToID("O_channels");
        static readonly int XHeight = Shader.PropertyToID("X_height");
        static readonly int XWidth = Shader.PropertyToID("X_width");
        static readonly int AffineMatrix = Shader.PropertyToID("affineMatrix");
        static readonly int ApplySrgb = Shader.PropertyToID("ApplySrgb");

        void Awake()
        {
            if (string.IsNullOrEmpty(sessionId)) sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        void OnEnable()
        {
            if (cameraAccess == null || handDetector == null || handLandmarker == null || anchorsCsv == null)
            {
                Debug.LogWarning("[Scalpal.Hands] Assign the camera access, both models, and anchors.", this);
                enabled = false;
                return;
            }
            anchors = BlazeHandMath.LoadAnchors(anchorsCsv.text);
            detectorWorker = new Worker(ModelLoader.Load(handDetector), backend);
            landmarkerWorker = new Worker(ModelLoader.Load(handLandmarker), backend);
            detectorInput = new Tensor<float>(new TensorShape(1, BlazeHandMath.DetectorSize, BlazeHandMath.DetectorSize, 3));
            landmarkerInput = new Tensor<float>(new TensorShape(1, BlazeHandMath.LandmarkerSize, BlazeHandMath.LandmarkerSize, 3));
            shader = Resources.Load<ComputeShader>(imageTransformShader);
            kernel = shader.FindKernel("ImageSample");
            // Read the per-image timestamp when this MRUK version exposes one, without a hard compile dependency.
            timestampProperty = cameraAccess.GetType().GetProperty("Timestamp");
            running = true;
            Loop();
        }

        void OnDisable()
        {
            running = false;
            tracked.Clear();
            detectorWorker?.Dispose();
            landmarkerWorker?.Dispose();
            detectorInput?.Dispose();
            landmarkerInput?.Dispose();
            detectorWorker = null;
            landmarkerWorker = null;
            detectorInput = null;
            landmarkerInput = null;
        }

        async void Loop()
        {
            while (running)
            {
                try
                {
                    if (!cameraAccess.IsPlaying) await Awaitable.NextFrameAsync();
                    else await ProcessFrame();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                    await Awaitable.NextFrameAsync();
                }
            }
        }

        async Awaitable ProcessFrame()
        {
            var started = Time.realtimeSinceStartupAsDouble;
            // Capture the pose with the image, before inference, so a moving head doesn't smear the joints.
            var pose = cameraAccess.GetCameraPose();
            var texture = cameraAccess.GetTexture();
            if (texture == null)
            {
                await Awaitable.NextFrameAsync();
                return;
            }
            var width = texture.width;
            var height = texture.height;
            var frame = new HandJointsFrame
            {
                sessionId = sessionId,
                frameIndex = frameIndex++,
                cameraTimestampMs = CameraTimestampMs(),
                unityTime = started,
                imageWidth = width,
                imageHeight = height,
                cameraPosition = new[] { pose.position.x, pose.position.y, pose.position.z },
                cameraRotation = new[] { pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w },
            };

            var rois = new List<(HandRoi roi, Affine2 space, string source)>();
            var topLeft = BlazeHandMath.TopLeftToImage(height);
            foreach (var roi in tracked) rois.Add((roi, topLeft, "tracking"));
            if (tracked.Count < maxHands && (tracked.Count == 0 || frame.frameIndex % redetectEveryFrames == 0))
            {
                foreach (var detected in await Detect(texture, width, height))
                {
                    if (rois.Count >= maxHands) break;
                    var (x, y) = detected.space.Apply(detected.roi.centerX, detected.roi.centerY);
                    if (OverlapsAny(rois, x, y, height)) continue;
                    rois.Add((detected.roi, detected.space, "detector"));
                }
            }

            var observations = new List<HandObservation>();
            tracked.Clear();
            foreach (var (roi, space, source) in rois)
            {
                var observation = await Landmarks(texture, width, height, roi, space, pose);
                if (observation == null) continue;
                observation.source = source;
                observations.Add(observation);
                var xs = new float[BlazeHandMath.NumJoints];
                var ys = new float[BlazeHandMath.NumJoints];
                for (var i = 0; i < BlazeHandMath.NumJoints; i++)
                {
                    xs[i] = observation.pixels[2 * i];
                    ys[i] = observation.pixels[2 * i + 1];
                }
                tracked.Add(BlazeHandMath.RoiFromLandmarks(xs, ys));
            }
            frame.hands = observations.ToArray();
            frame.inferenceMs = (float)((Time.realtimeSinceStartupAsDouble - started) * 1000.0);
            LastFrame = frame;
            FrameReady?.Invoke(frame);
        }

        async Awaitable<List<(HandRoi roi, Affine2 space)>> Detect(Texture texture, int width, int height)
        {
            var toImage = BlazeHandMath.DetectorToImage(width, height);
            Sample(texture, detectorInput, toImage);
            detectorWorker.Schedule(detectorInput);
            using var boxes = await (detectorWorker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();
            using var scores = await (detectorWorker.PeekOutput("Identity_1") as Tensor<float>).ReadbackAndCloneAsync();
            var boxValues = new float[BlazeHandMath.NumAnchors * 18];
            var scoreValues = new float[BlazeHandMath.NumAnchors];
            for (var i = 0; i < boxValues.Length; i++) boxValues[i] = boxes[i];
            for (var i = 0; i < scoreValues.Length; i++) scoreValues[i] = scores[i];
            var rois = new HandRoi[maxHands];
            var roiScores = new float[maxHands];
            var count = BlazeHandMath.SelectDetections(boxValues, scoreValues, anchors, detectionThreshold, maxHands, rois, roiScores);
            var result = new List<(HandRoi, Affine2)>();
            for (var i = 0; i < count; i++) result.Add((rois[i], toImage));
            return result;
        }

        async Awaitable<HandObservation> Landmarks(Texture texture, int width, int height, HandRoi roi, Affine2 space, Pose pose)
        {
            var toImage = BlazeHandMath.LandmarkerToImage(space, roi);
            Sample(texture, landmarkerInput, toImage);
            landmarkerWorker.Schedule(landmarkerInput);
            using var screen = await (landmarkerWorker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();
            using var presence = await (landmarkerWorker.PeekOutput("Identity_1") as Tensor<float>).ReadbackAndCloneAsync();
            using var handedness = await (landmarkerWorker.PeekOutput("Identity_2") as Tensor<float>).ReadbackAndCloneAsync();
            using var world = await (landmarkerWorker.PeekOutput("Identity_3") as Tensor<float>).ReadbackAndCloneAsync();
            if (presence[0] < presenceThreshold) return null;

            var observation = new HandObservation
            {
                presence = presence[0],
                handednessRaw = handedness[0],
                // MediaPipe labels assume a mirrored (selfie) image; passthrough is unmirrored, so swap.
                hand = handedness[0] > 0.5f ? "right" : "left",
            };
            var rays = new Ray[BlazeHandMath.NumJoints];
            for (var i = 0; i < BlazeHandMath.NumJoints; i++)
            {
                var (x, y) = toImage.Apply(screen[3 * i], screen[3 * i + 1]);
                observation.pixels[2 * i] = x;
                observation.pixels[2 * i + 1] = height - y;
                rays[i] = cameraAccess.ViewportPointToRay(new Vector2(x / width, y / height), pose);
                for (var k = 0; k < 3; k++) observation.worldModelMeters[3 * i + k] = world[3 * i + k];
            }
            Lift(observation, rays);
            return observation;
        }

        // Depth from apparent size: the metric wrist-to-middle-knuckle length from MediaPipe's world joints over
        // the angle it spans in the image. Each joint then sits along its own camera ray, offset by its depth
        // relative to the wrist. Monocular: expect a scale bias that a one-time calibration can remove.
        static void Lift(HandObservation observation, Ray[] rays)
        {
            var w = observation.worldModelMeters;
            float Dist(int a, int b)
            {
                var dx = w[3 * a] - w[3 * b];
                var dy = w[3 * a + 1] - w[3 * b + 1];
                var dz = w[3 * a + 2] - w[3 * b + 2];
                return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            var length = Dist(9, 0);
            var angle = Vector3.Angle(rays[0].direction, rays[9].direction) * Mathf.Deg2Rad;
            var depth = Mathf.Clamp(length / Mathf.Max(2f * Mathf.Tan(0.5f * angle), 1e-4f), 0.1f, 2.0f);
            observation.depthMeters = depth;
            for (var i = 0; i < BlazeHandMath.NumJoints; i++)
            {
                var point = rays[i].GetPoint(depth + (w[3 * i + 2] - w[2]));
                observation.jointsWorld[3 * i] = point.x;
                observation.jointsWorld[3 * i + 1] = point.y;
                observation.jointsWorld[3 * i + 2] = point.z;
            }
        }

        static bool OverlapsAny(List<(HandRoi roi, Affine2 space, string source)> rois, float x, float y, int height)
        {
            foreach (var (roi, space, _) in rois)
            {
                var (ox, oy) = space.Apply(roi.centerX, roi.centerY);
                var reach = 0.35f * roi.size * (space.a != 0 ? Mathf.Abs(space.a) : 1f);
                if (Mathf.Abs(ox - x) < reach && Mathf.Abs(oy - y) < reach) return true;
            }
            return false;
        }

        // Affine-sampled crop of the camera texture into the model's NHWC input (ImageTransform.compute).
        void Sample(Texture texture, Tensor<float> destination, Affine2 m)
        {
            var data = ComputeTensorData.Pin(destination, false);
            shader.SetTexture(kernel, XTex, texture);
            shader.SetBuffer(kernel, Optr, data.buffer);
            shader.SetInt(OHeight, destination.shape[1]);
            shader.SetInt(OWidth, destination.shape[2]);
            shader.SetInt(OChannels, destination.shape[3]);
            shader.SetInt(XHeight, texture.height);
            shader.SetInt(XWidth, texture.width);
            shader.SetInt(ApplySrgb, convertToSrgb ? 1 : 0);
            shader.SetMatrix(AffineMatrix, new Matrix4x4(new Vector4(m.a, m.d), new Vector4(m.b, m.e), new Vector4(m.c, m.f), Vector4.zero));
            var groups = (destination.shape[1] + 7) / 8;
            shader.Dispatch(kernel, groups, groups, 1);
        }

        double CameraTimestampMs()
        {
            if (timestampProperty == null) return -1;
            var value = timestampProperty.GetValue(cameraAccess);
            if (value is DateTime time) return (time.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
            if (value is long ticks) return ticks / 1e6;
            if (value is double ms) return ms;
            return -1;
        }
    }
}
#endif
