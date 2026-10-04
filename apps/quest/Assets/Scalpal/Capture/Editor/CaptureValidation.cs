using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Scalpal.Handoff;
using Scalpal.Recap;
using UnityEditor;
using UnityEngine;

namespace Scalpal.Capture.Editor
{
    // Editor gate for native capture -> upload -> motion job -> recap. Synthetic
    // frames are labeled "sample" and never claim learner footage; the headset
    // camera path itself needs the device (docs/system-integration.md, native capture).
    public static class CaptureValidation
    {
        public const string MotionFixture = "../../services/motion/tests/fixtures/quest-capture-mjpeg.avi";
        public const string GatewayFixture = "../../services/api/test/unit/fixtures/quest-capture-upload.json";
        static int checks;
        static void Check(bool pass, string message) { checks++; if (!pass) throw new InvalidOperationException("Capture validation: " + message); }

        // ---------- fakes ----------
        sealed class SyntheticSource : ICaptureFrameSource
        {
            public readonly Queue<CaptureFrame> pending = new Queue<CaptureFrame>();
            public bool startOk = true, started, stopped;
            public string startReason = "Capture unavailable: synthetic start refusal.";
            public int width = 64, height = 48;
            public string FrameSource => "synthetic_test_pattern";
            public string Provenance => "sample";
            public string Clock => "synthetic_us";
            public bool Start(out string reason) { reason = startOk ? "" : startReason; started = startOk; return startOk; }
            public void Pump(double now) { }
            public bool TryDequeue(out CaptureFrame frame) { frame = !stopped && pending.Count > 0 ? pending.Dequeue() : null; return frame != null; }
            public void Stop() { stopped = true; }
            public void Dispose() { stopped = true; }
            public CaptureCameraRecord Describe() => new CaptureCameraRecord { api = "synthetic", position = "right", width = width, height = height, sensorWidth = 1280, sensorHeight = 960,
                fx = 434.5f, fy = 434.5f, cx = 320f, cy = 240f, lensOffsetPosition = new[] { 0.032f, -0.011f, 0.068f }, lensOffsetRotation = new[] { 0f, 0f, 0f, 1f } };
            public void Push(int index, long sensorUs, double received)
            {
                var rgba = new byte[width * height * 4];
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                { int i = (y * width + x) * 4; rgba[i] = (byte)(x * 4 + index * 16); rgba[i + 1] = (byte)(y * 5); rgba[i + 2] = (byte)((x ^ y) * 3 + index); rgba[i + 3] = 255; }
                pending.Enqueue(new CaptureFrame { rgba = rgba, width = width, height = height, sensorTimeUs = sensorUs, receivedRealtime = received,
                    cameraPose = new Pose(new Vector3(0.03f, 1.6f, 0.07f + index * 0.001f), Quaternion.Euler(30, 0, 0)), poseValid = true });
            }
        }

        // Mirrors the reducer + reconciler checks a real gateway applies.
        sealed class FakeTransport : ICaptureTransport
        {
            static readonly Regex Id = new Regex("^[A-Za-z0-9_-]{6,64}$");
            static readonly string[] Kinds = { "raw_clip", "capture_manifest", "scene_timeline", "hand_estimates", "robot_trajectory", "replay_video", "quality_report", "other" };
            public sealed class Art { public string id, session, attempt, kind, filename, contentType, sha256, status = "pending_upload", reason = "", stored; public ulong bytes; }
            public readonly List<string> log = new List<string>();
            public readonly List<string> uploads = new List<string>();
            public readonly Dictionary<string, Art> arts = new Dictionary<string, Art>();
            readonly Dictionary<string, string> grants = new Dictionary<string, string>(), puts = new Dictionary<string, string>(), rejected = new Dictionary<string, string>();
            public readonly List<string[]> jobs = new List<string[]>(); // jobId, input, config, extrasJoined
            public string connectError = "", failPutKind = "", store;
            public int putCalls;
            public bool ready, disposed;
            public string jobCall = "";
            public FakeTransport(string store) { this.store = store; Directory.CreateDirectory(store); }
            public void Connect(string uri, string database, string token) { log.Add("connect " + uri + " " + database + " token=" + (token.Length > 0)); ready = connectError.Length == 0; }
            public void Tick() { }
            public bool Ready => ready;
            public string ConnectionError => connectError;
            public bool RequestUpload(string grantId, string artifactId, string sessionId, string attemptId, string kind, string filename, string contentType, ulong bytes, string sha256, out string error)
            {
                error = "";
                log.Add("requestUpload " + kind);
                uploads.Add("{\"grantId\":\"" + grantId + "\",\"artifactId\":\"" + artifactId + "\",\"sessionId\":\"" + sessionId + "\",\"attemptId\":\"" + attemptId + "\",\"kind\":\"" + kind + "\",\"filename\":\"" + filename + "\",\"contentType\":\"" + contentType + "\",\"declaredBytes\":" + bytes + ",\"sha256\":\"" + sha256 + "\"}");
                if (!Id.IsMatch(grantId) || !Id.IsMatch(artifactId) || !Kinds.Contains(kind) || filename.Length > 200 || contentType.Length > 120
                    || (sha256 != null && !Regex.IsMatch(sha256, "^[a-f0-9]{64}$")) || grants.ContainsKey(grantId)) { rejected[grantId] = "invalid upload request"; return true; }
                arts[artifactId] = new Art { id = artifactId, session = sessionId, attempt = attemptId, kind = kind, filename = filename, contentType = contentType, bytes = bytes, sha256 = sha256 };
                grants[grantId] = artifactId;
                return true;
            }
            public string GrantStatus(string grantId, out string url, out string method, out string reason)
            { reason = ""; method = "PUT"; url = grants.TryGetValue(grantId, out var art) ? "http://127.0.0.1:8788/files/" + art + "?m=PUT" : ""; return grants.ContainsKey(grantId) ? "issued" : null; }
            public void StartPut(string artifactId, string url, string method, string path, string contentType, int timeoutSeconds)
            {
                putCalls++;
                var art = arts[artifactId];
                if (art.kind == failPutKind) { puts[artifactId] = "HTTP 0 network unreachable"; return; }
                if (contentType != art.contentType || method != "PUT") { puts[artifactId] = "HTTP 403 invalid or expired signature"; return; }
                art.stored = Path.Combine(store, artifactId); File.Copy(path, art.stored, true); puts[artifactId] = "";
            }
            public string PutResult(string artifactId) => puts.TryGetValue(artifactId, out var r) ? r : null;
            public bool MarkUploaded(string artifactId, out string error)
            {
                error = ""; log.Add("markUploaded " + arts[artifactId].kind);
                var art = arts[artifactId];
                if (art.stored == null) { art.status = "failed"; art.reason = "object not found in storage"; return true; }
                long size = new FileInfo(art.stored).Length;
                if ((ulong)size != art.bytes) { art.status = "failed"; art.reason = "size mismatch"; return true; }
                if (art.sha256 != null && CaptureSegment.Sha256(art.stored) != art.sha256) { art.status = "failed"; art.reason = "sha256 mismatch"; return true; }
                art.status = "available"; return true;
            }
            public string ArtifactStatus(string artifactId, out string reason) { reason = arts.TryGetValue(artifactId, out var a) ? a.reason : ""; return arts.TryGetValue(artifactId, out var b) ? b.status : null; }
            public bool RequestMotionJob(string jobId, string inputArtifactId, List<string> extras, string configVersion, out string error)
            {
                error = ""; log.Add("requestMotionJob " + configVersion);
                jobCall = "{\"jobId\":\"" + jobId + "\",\"inputArtifactId\":\"" + inputArtifactId + "\",\"extraArtifactIds\":[" + string.Join(",", extras.Select(e => "\"" + e + "\"")) + "],\"configVersion\":\"" + configVersion + "\"}";
                if (!Id.IsMatch(jobId) || !arts.TryGetValue(inputArtifactId, out var input) || input.status != "available" || input.kind != "raw_clip" || extras.Count > 8
                    || extras.Any(e => !arts.TryGetValue(e, out var x) || x.status != "available" || x.session != input.session)) { rejected[jobId] = "invalid motion job"; return true; }
                if (!jobs.Any(j => j[1] == inputArtifactId && j[2] == configVersion)) jobs.Add(new[] { jobId, inputArtifactId, configVersion, string.Join(",", extras) });
                return true;
            }
            public bool FindJob(string jobId, string inputArtifactId, string configVersion, out string foundJobId, out uint run, out string status)
            {
                var job = jobs.FirstOrDefault(j => j[0] == jobId || j[1] == inputArtifactId && j[2] == configVersion);
                foundJobId = job?[0] ?? ""; run = 0; status = job == null ? "" : "queued"; return job != null;
            }
            public string Rejection(string key) => rejected.TryGetValue(key, out var r) ? r : "";
            public void Dispose() { disposed = true; }
        }

        // ---------- harness ----------
        static double now;
        static int ids;
        static SyntheticSource lastSource;
        static FakeTransport lastTransport;
        static int sourceCount, transportCount;
        static string scratch;
        static HandoffTicket ticket;
        static TheatrePreflight preflight;

        static RunResult MakeRun(string attempt) => new RunResult
        {
            runId = "run_capture_validation", sessionId = "ses_capture", attemptId = attempt, encounterId = "enc_capture", patientId = "patient-demo-001", procedureId = "open_appendectomy",
            diagnosisAvailable = true, diagnosis = new DiagnosisScorecard { total = 82, max = 100, grade = "B", patientId = "patient-demo-001" },
            surgery = new SurgeryGrade { available = false },
            replay = new ReplayResult { source = "unknown", status = "failed", failureReason = "No capture adapter has supplied a recording for this attempt." }
        };
        static SurgeryGrade Grade() => new SurgeryGrade { available = true, total = 56, max = 80, grade = "C", rubric = "illustrative_v1_uncalibrated", complete = true };

        static (RecapRunContext, HandCaptureRecorder) Setup(string mode, bool learner, bool volunteer, Action<SyntheticSource> configure = null, Action<FakeTransport> transport = null)
        {
            var go = new GameObject("Capture validation run");
            var context = go.AddComponent<RecapRunContext>();
            context.clientToken = "validation-client-token";
            var recorder = go.AddComponent<HandCaptureRecorder>();
            recorder.Bind(context);
            recorder.realtimeUri = "ws://127.0.0.1:3000"; recorder.realtimeDatabase = "scalpal";
            recorder.storageRoot = Path.Combine(scratch, "segments");
            recorder.queueLimit = 64; // deterministic frame counts; the bound itself is checked in MemoryBound()
            ticket = new HandoffTicket { runId = "run_capture_validation", presentationMode = mode };
            preflight = new TheatrePreflight { learnerCaptureConsented = learner, volunteerConsented = volunteer };
            HandCaptureRecorder.CurrentTicket = () => ticket;
            HandCaptureRecorder.CurrentPreflight = () => preflight;
            HandCaptureRecorder.SourceFactory = (r, m) => { sourceCount++; lastSource = new SyntheticSource(); configure?.Invoke(lastSource); return lastSource; };
            HandCaptureRecorder.TransportFactory = () => { transportCount++; lastTransport = new FakeTransport(Path.Combine(scratch, "store-" + transportCount)); transport?.Invoke(lastTransport); return lastTransport; };
            sourceCount = transportCount = 0; lastSource = null; lastTransport = null;
            return (context, recorder);
        }
        static void Teardown(RecapRunContext context) { UnityEngine.Object.DestroyImmediate(context.gameObject); }

        static void Drive(HandCaptureRecorder recorder, int steps = 400)
        {
            if (recorder.State == HandCaptureRecorder.RecorderState.Finalizing) Check(recorder.WaitForFinalize(20000), "finalization completes");
            for (int i = 0; i < steps && recorder.State == HandCaptureRecorder.RecorderState.Uploading; i++) { now += 0.05; recorder.Pump(now); }
        }

        [MenuItem("Scalpal/Capture/Verify")]
        public static void Run()
        {
            checks = 0; ids = 0; now = 1000;
            scratch = Path.Combine(Path.GetTempPath(), "scalpal-capture-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            HandCaptureRecorder.NewId = prefix => prefix + "_" + (++ids).ToString("D24");
            HandCaptureRecorder.Clock = () => now;
            try
            {
                Container(); MemoryBound(); Lifecycle(); Consent(); VirtualUnavailable(); HappyPathAndShapes(); Abandon(); Cap(); NetworkFailure(); ContractRejections(); Wiring();
            }
            finally
            {
                HandCaptureRecorder.ResetSeams();
                try { Directory.Delete(scratch, true); } catch (Exception e) { Debug.LogWarning("capture validation scratch cleanup: " + e.Message); }
            }
            Debug.Log("SCALPAL_CAPTURE_VERIFY_OK " + checks + " assertions");
        }

        static void Container()
        {
            var source = new SyntheticSource();
            string path = Path.Combine(scratch, "fixture.avi");
            using (var writer = new MjpegAviWriter(path, source.width, source.height))
            {
                for (int i = 0; i < 4; i++)
                {
                    source.Push(i, i * 66_667, i);
                    var frame = source.pending.Dequeue();
                    var jpeg = ImageConversion.EncodeArrayToJPG(frame.rgba, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, (uint)frame.width, (uint)frame.height, 0, CaptureSegment.JpegQuality);
                    writer.AppendFrame(jpeg, jpeg.Length);
                }
                bool rejected = false; try { writer.AppendFrame(new byte[] { 1, 2, 3, 4 }, 4); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "non-JPEG frames are rejected");
                writer.Finish(15);
            }
            Check(MjpegAviWriter.TryReadIndex(path, out int w, out int h, out uint frames, out uint rate, out uint scale, out var sizes, out var error), "written AVI parses: " + error);
            Check(w == 64 && h == 48 && frames == 4 && sizes.Count == 4 && rate == 15000 && scale == 1000, "AVI header carries size, count and measured rate");
            SyncFixture(path, MotionFixture, "services/motion decode fixture");
        }

        // The committed fixture must stay byte-identical to what this writer produces,
        // so the Python decode test exercises the exact headset container.
        static void SyncFixture(string produced, string fixture, string label)
        {
            var bytes = File.ReadAllBytes(produced);
            if (Environment.GetEnvironmentVariable("SCALPAL_UPDATE_CAPTURE_FIXTURES") == "1")
            { Directory.CreateDirectory(Path.GetDirectoryName(fixture)); File.WriteAllBytes(fixture, bytes); Debug.Log("SCALPAL_CAPTURE fixture updated " + fixture); }
            Check(File.Exists(fixture) && File.ReadAllBytes(fixture).SequenceEqual(bytes), label + " is current (rerun with SCALPAL_UPDATE_CAPTURE_FIXTURES=1 after an intended format change)");
        }

        static void MemoryBound()
        {
            var segment = new CaptureSegment(Path.Combine(scratch, "bound"), "ses", "att_bound", "run", "virtual", 0, 300, 1L << 30);
            segment.HoldEncoder = true;
            var source = new SyntheticSource();
            int accepted = 0;
            for (int i = 0; i < 6; i++) { source.Push(i, 1000 + i, 1); if (segment.Submit(source.pending.Dequeue())) accepted++; }
            Check(accepted == CaptureSegment.QueueLimit && segment.DroppedQueueFull == 6 - CaptureSegment.QueueLimit, "a stalled encoder holds at most the queue limit of raw frames; overflow is dropped and counted");
            segment.HoldEncoder = false;
            var manifest = new CaptureProvenanceManifest { source = "sample" };
            manifest.capture.consent.learnerCaptureConsented = true; manifest.capture.clock.frameTimestamps = "synthetic_us";
            manifest.capture.camera = source.Describe();
            segment.clipArtifactId = "art_bound_clip"; segment.timingArtifactId = "art_bound_timing";
            Check(segment.Finish(manifest) == "" && manifest.capture.video.frameCount == CaptureSegment.QueueLimit && manifest.capture.frames.droppedQueueFull == 3, "dropped frames are absent from the clip and timing, and declared in the manifest");
            Check(!segment.Submit(new CaptureFrame { rgba = new byte[64 * 48 * 4], width = 64, height = 48, sensorTimeUs = 99999, receivedRealtime = 2 }), "a finished segment accepts nothing");
            segment.Delete();
            Check(!Directory.Exists(segment.directory), "segment deletion removes the local files");
        }

        static void Lifecycle()
        {
            var (context, recorder) = Setup("virtual", true, false);
            Check(recorder.State == HandCaptureRecorder.RecorderState.Idle && sourceCount == 0, "nothing records before practice starts");
            context.Begin(MakeRun("att_lifecycle1"));
            Check(recorder.State == HandCaptureRecorder.RecorderState.WaitingForFrames && sourceCount == 1 && lastSource.started, "practice start opens exactly one camera segment");
            var source = lastSource; double start = now;
            source.Push(0, 5_000_000, start - 0.5); // queued before the segment opened
            now += 0.1; recorder.Pump(now);
            Check(recorder.FramesSeen == 0 && recorder.Segment.DroppedInvalid == 1, "a frame received before practice start is rejected");
            source.Push(1, 5_066_667, now); source.Push(2, 5_066_667, now); now += 0.1; recorder.Pump(now);
            Check(recorder.FramesSeen == 1 && recorder.Segment.DroppedNonMonotonic == 1 && recorder.State == HandCaptureRecorder.RecorderState.Recording, "duplicate sensor timestamps are dropped, not recorded twice");
            source.Push(3, 5_000_001, now); now += 0.1; recorder.Pump(now);
            Check(recorder.Segment.DroppedNonMonotonic == 2, "a backwards sensor timestamp is dropped");
            source.Push(4, 5_133_334, now); now += 0.1; recorder.Pump(now);
            Check(context.EndSurgery("att_lifecycle1", Grade()), "case end closes the segment");
            Check(source.stopped && recorder.State == HandCaptureRecorder.RecorderState.Finalizing, "case end stops the camera and finalizes");
            Check(context.result.replay.status == "queued" && string.IsNullOrEmpty(context.result.replay.jobId) && context.captureNotice.Contains("Uploading"), "recap shows an honest upload-in-progress state, not a job");
            source.stopped = false; source.Push(5, 5_200_000, now); now += 0.1; recorder.Pump(now);
            Check(recorder.Segment == null || recorder.Segment.FrameCount == 2, "frames after case end never enter the clip");
            Drive(recorder);
            Check(recorder.State == HandCaptureRecorder.RecorderState.Submitted && context.result.replay.jobId.StartsWith("job_"), "job id reaches RunResult");
            Check(recorder.LastManifest.capture.video.frameCount == 2 && recorder.LastManifest.capture.frames.droppedNonMonotonic == 2 && recorder.LastManifest.capture.frames.droppedInvalid == 1, "manifest records exactly the accepted frames and the drops");
            Teardown(context);
        }

        static void Consent()
        {
            foreach (var (mode, learner, volunteer, expect) in new[] { ("virtual", false, false, "hand-recording consent"), ("mixed_reality", true, false, "volunteer"), ("mixed_reality", false, true, "hand-recording consent") })
            {
                var (context, recorder) = Setup(mode, learner, volunteer);
                context.Begin(MakeRun("att_consent_" + mode + learner + volunteer));
                Check(sourceCount == 0 && recorder.State == HandCaptureRecorder.RecorderState.Unavailable && recorder.Reason.Contains(expect), "no camera opens without consent: " + mode + " " + expect);
                context.EndSurgery(context.result.attemptId, Grade());
                Check(context.result.replay.status == "failed" && context.result.replay.failureReason.Contains(expect) && context.result.surgery.available && context.result.diagnosisAvailable, "missing consent fails only the replay, with its reason");
                Check(transportCount == 0, "nothing is uploaded without consent");
                Teardown(context);
            }
            {
                var (context, recorder) = Setup("virtual", true, false);
                HandCaptureRecorder.CurrentTicket = () => null;
                context.Begin(MakeRun("att_consent_legacy"));
                Check(sourceCount == 0 && recorder.Reason.Contains("no theatre handoff"), "legacy runs without a handoff consent record never capture");
                Teardown(context);
            }
            {
                var (context, recorder) = Setup("mixed_reality", true, true);
                context.Begin(MakeRun("att_consent_withdraw"));
                lastSource.Push(0, 1_000_000, now); now += 0.1; recorder.Pump(now);
                string dir = recorder.Segment.directory;
                preflight.volunteerConsented = false; // End AR / withdraw volunteer
                now += 0.1; recorder.Pump(now);
                Check(recorder.State == HandCaptureRecorder.RecorderState.Discarded && lastSource.stopped && !Directory.Exists(dir), "consent withdrawal stops the camera and deletes the local clip");
                context.EndSurgery("att_consent_withdraw", Grade());
                Check(context.result.replay.status == "failed" && context.result.replay.failureReason.Contains("withdrawn") && transportCount == 0, "withdrawn capture is reported, never uploaded");
                Teardown(context);
            }
            Check(HandCaptureRecorder.PlannedNotice(new HandoffTicket { presentationMode = "virtual" }, new TheatrePreflight { learnerCaptureConsented = true }).StartsWith("Recording hands begins"), "Time-Out states that recording begins with practice");
            Check(HandCaptureRecorder.PlannedNotice(new HandoffTicket { presentationMode = "virtual" }, new TheatrePreflight()).StartsWith("Recording unavailable"), "Time-Out states when recording is unavailable");
        }

        static void VirtualUnavailable()
        {
            {
                var (context, recorder) = Setup("virtual", true, false);
                context.Begin(MakeRun("att_vr_noframes"));
                now += HandCaptureRecorder.FirstFrameTimeout - 0.5; recorder.Pump(now);
                Check(recorder.State == HandCaptureRecorder.RecorderState.WaitingForFrames, "VR capture waits for the first frame");
                now += 1; recorder.Pump(now);
                Check(recorder.State == HandCaptureRecorder.RecorderState.Unavailable && recorder.Reason == HandCaptureRecorder.VrUnavailable && lastSource.stopped, "VR without frames is labeled capture unavailable in VR");
                context.EndSurgery("att_vr_noframes", Grade());
                Check(context.result.replay.status == "failed" && context.result.replay.failureReason.StartsWith("Capture unavailable in VR") && transportCount == 0, "VR-unavailable reaches the recap and nothing is faked or uploaded");
                Teardown(context);
            }
            {
                var (context, recorder) = Setup("virtual", true, false, s => s.startOk = false);
                context.Begin(MakeRun("att_vr_refused"));
                Check(recorder.Reason.StartsWith("Capture unavailable in VR:"), "a refused camera start in VR is labeled as VR-unavailable");
                Teardown(context);
            }
            {
                var (context, recorder) = Setup("mixed_reality", true, true);
                context.Begin(MakeRun("att_ar_noframes"));
                now += HandCaptureRecorder.FirstFrameTimeout + 0.5; recorder.Pump(now);
                Check(recorder.Reason.StartsWith("Capture unavailable: no passthrough camera frames"), "AR without frames gets its own reason");
                Teardown(context);
            }
        }

        static void HappyPathAndShapes()
        {
            ids = 0; now = 2000;
            var (context, recorder) = Setup("virtual", true, false);
            context.Begin(MakeRun("att_shapes_0001"));
            double start = now;
            for (int i = 0; i < 6; i++) { now += 1.0 / 15; lastSource.Push(i, 9_000_000 + i * 66_667L, now); recorder.Pump(now); }
            context.EndSurgery("att_shapes_0001", Grade());
            Check(recorder.WaitForFinalize(20000), "finalize completes");
            var t = lastTransport;
            Drive(recorder);
            var r = context.result.replay;
            Check(recorder.State == HandCaptureRecorder.RecorderState.Submitted, "upload submitted: " + recorder.Reason);
            Check(string.Join("|", t.log) == "connect ws://127.0.0.1:3000 scalpal token=True|requestUpload raw_clip|markUploaded raw_clip|requestUpload other|markUploaded other|requestUpload capture_manifest|markUploaded capture_manifest|requestMotionJob motion-v1", "gateway sequence: " + string.Join("|", t.log));
            var clip = t.arts.Values.Single(a => a.kind == "raw_clip");
            var manifestArt = t.arts.Values.Single(a => a.kind == "capture_manifest");
            var timingArt = t.arts.Values.Single(a => a.kind == "other");
            Check(clip.contentType == "video/x-msvideo" && clip.session == "ses_capture" && clip.attempt == "att_shapes_0001" && clip.status == "available", "raw clip is a private video artifact of this attempt");
            Check(t.jobs.Count == 1 && t.jobs[0][1] == clip.id && t.jobs[0][2] == "motion-v1" && t.jobs[0][3] == timingArt.id + "," + manifestArt.id, "motion-v1 job names the manifest and timing as extras");
            Check(r.jobId == t.jobs[0][0] && r.status == "queued" && r.source == "sample" && r.sourceArtifactId == clip.id && !r.clockAligned && r.eventClock == "run", "RunResult carries the queued job, clip and honest provenance");
            Check(Math.Abs(r.captureStartRunSeconds - (start + 1.0 / 15 - start)) < 1e-6 && string.IsNullOrEmpty(context.captureNotice), "capture start is measured from practice start");
            Check(!Directory.Exists(Path.Combine(scratch, "segments")) || Directory.GetDirectories(Path.Combine(scratch, "segments")).Length == 0, "local recording is deleted after upload");
            var manifest = JsonUtility.FromJson<CaptureProvenanceManifest>(File.ReadAllText(manifestArt.stored));
            var timing = JsonUtility.FromJson<CaptureTiming>(File.ReadAllText(timingArt.stored));
            Check(CaptureContract.Validate(manifest, timing, out var why), "uploaded manifest validates: " + why);
            Check(manifest.schemaVersion == "scalpal.capture-provenance.v1" && manifest.sessionId == "ses_capture" && manifest.attemptId == "att_shapes_0001" && manifest.inputArtifactId == clip.id && manifest.source == "sample", "gateway provenance fields bind clip to attempt");
            Check(manifest.capture.frames.timingArtifactId == timingArt.id && manifest.capture.video.sha256 == clip.sha256 && manifest.capture.presentationMode == "virtual" && !manifest.capture.subjectInFrame && manifest.capture.rawCameraFrames && !manifest.capture.compositedFrames, "manifest describes raw camera frames of this clip");
            Check(manifest.capture.camera.fx > 0 && manifest.capture.camera.lensOffsetPosition.Length == 3 && timing.cameraPose.Length == 6 * 7 && timing.sensorTimeUs.Length == 6, "intrinsics, extrinsics and per-frame poses are recorded");
            Check(Encoding.UTF8.GetByteCount(File.ReadAllText(manifestArt.stored)) <= CaptureContract.MaxManifestBytes, "manifest fits the gateway bound");
            Check(MjpegAviWriter.TryReadIndex(clip.stored, out _, out _, out uint frames, out uint rate, out _, out _, out var err) && frames == 6 && Math.Abs((long)rate - 15000) <= 1, "uploaded clip parses with the measured rate: " + err);
            WriteGatewayFixture(t, File.ReadAllText(manifestArt.stored));
            Teardown(context);
        }

        // Shapes the services/api unit test replays through the real gateway routes.
        static void WriteGatewayFixture(FakeTransport t, string manifest)
        {
            string json = "{\n  \"note\": \"Generated by Scalpal.Capture.Editor.CaptureValidation from the headset upload flow; regenerate with SCALPAL_UPDATE_CAPTURE_FIXTURES=1.\",\n  \"requestUpload\": [\n    "
                + string.Join(",\n    ", t.uploads) + "\n  ],\n  \"requestMotionJob\": " + t.jobCall + ",\n  \"manifest\": " + manifest + "\n}\n";
            string produced = Path.Combine(scratch, "quest-capture-upload.json");
            File.WriteAllText(produced, json, new UTF8Encoding(false));
            SyncFixture(produced, GatewayFixture, "services/api upload-shape fixture");
        }

        static void Abandon()
        {
            var (context, recorder) = Setup("virtual", true, false);
            context.Begin(MakeRun("att_abandon1"));
            var first = lastSource;
            first.Push(0, 1_000, now); now += 0.1; recorder.Pump(now);
            string dir = recorder.Segment.directory;
            Check(context.AbandonSegment("att_abandon1", "Practice restarted before the case ended; recording discarded."), "abandon is accepted for the open attempt");
            Check(recorder.State == HandCaptureRecorder.RecorderState.Discarded && first.stopped && !Directory.Exists(dir) && transportCount == 0, "abandon stops the camera, deletes the clip and uploads nothing");
            context.Begin(MakeRun("att_abandon2"));
            Check(sourceCount == 2 && recorder.State == HandCaptureRecorder.RecorderState.WaitingForFrames, "the retry opens a fresh segment");
            lastSource.Push(0, 2_000, now); now += 0.1; recorder.Pump(now);
            string dir2 = recorder.Segment.directory;
            context.Begin(MakeRun("att_abandon3"));
            Check(!Directory.Exists(dir2) && sourceCount == 3, "a new attempt discards an unfinished previous segment");
            context.EndSurgery("att_abandon3", Grade());
            Check(!context.AbandonSegment("att_abandon3", "late"), "abandon after case end is a no-op");
            Teardown(context);
        }

        static void Cap()
        {
            var (context, recorder) = Setup("mixed_reality", true, true);
            recorder.maxSeconds = 1;
            context.Begin(MakeRun("att_cap"));
            var source = lastSource;
            for (int i = 0; i < 20; i++) { now += 0.1; source.Push(i, 100_000_000 + i * 100_000L, now); recorder.Pump(now); }
            Check(recorder.State == HandCaptureRecorder.RecorderState.Capped && source.stopped && recorder.Segment.FrameCount == 11, "duration cap stops the camera at the cap");
            context.EndSurgery("att_cap", Grade());
            Drive(recorder);
            var m = recorder.LastManifest;
            Check(m != null && m.capture.segment.truncated && m.capture.segment.stoppedBy == "duration_cap" && m.capture.segment.durationSeconds <= 1.0001 && m.capture.subjectInFrame && m.capture.consent.volunteerConsented, "capped AR clip is uploaded, labeled truncated with the volunteer in frame");
            Check(context.result.replay.status == "queued" && !string.IsNullOrEmpty(context.result.replay.jobId), "capped clip still produces a job");
            Teardown(context);
        }

        static void NetworkFailure()
        {
            foreach (var (label, configure) in new (string, Action<FakeTransport>)[] { ("connect", f => f.connectError = "realtime connection failed"), ("put", f => f.failPutKind = "raw_clip") })
            {
                var (context, recorder) = Setup("virtual", true, false, null, configure);
                context.Begin(MakeRun("att_network_" + label));
                lastSource.Push(0, 1_000_000, now); now += 0.1; lastSource.Push(1, 1_066_667, now); recorder.Pump(now);
                context.EndSurgery("att_network_" + label, Grade());
                Drive(recorder);
                var r = context.result;
                Check(recorder.State == HandCaptureRecorder.RecorderState.Failed && r.replay.status == "failed" && r.replay.failureReason.Contains("upload failed") && r.replay.failureReason.Contains("results are kept"), label + " failure is shown with a reason");
                Check(r.surgery.available && r.surgery.total == 56 && r.diagnosisAvailable && r.diagnosis.total == 82, label + " failure keeps the learning result");
                Check(label != "put" || lastTransport.putCalls == CaptureUploadFlow.PutAttempts, "a failed PUT is retried a bounded number of times");
                RunResultContract.Validate(r);
                Teardown(context);
            }
            {
                var (context, recorder) = Setup("virtual", true, false);
                context.clientToken = "";
                context.Begin(MakeRun("att_no_token"));
                lastSource.Push(0, 1_000_000, now); now += 0.1; recorder.Pump(now);
                context.EndSurgery("att_no_token", Grade()); Drive(recorder);
                Check(context.result.replay.failureReason.Contains("no paired session credential"), "missing pairing credential is explained");
                Teardown(context);
            }
        }

        static void ContractRejections()
        {
            var timing = new CaptureTiming { sessionId = "s", attemptId = "a", inputArtifactId = "clip", clock = "c", sensorTimeUs = new long[] { 10, 20 }, receivedRealtimeSeconds = new double[] { 1, 2 }, cameraPose = new float[14], poseValid = new[] { true, false } };
            CaptureProvenanceManifest Valid()
            {
                var m = new CaptureProvenanceManifest { sessionId = "s", attemptId = "a", inputArtifactId = "clip", source = "learner" };
                var c = m.capture; c.runId = "r"; c.presentationMode = "virtual"; c.consent.learnerCaptureConsented = true;
                c.camera.width = c.video.width = 64; c.camera.height = c.video.height = 48; c.video.frameCount = 2; c.video.bytes = 100; c.video.nominalFps = 15;
                c.video.sha256 = c.frames.timingSha256 = new string('a', 64); c.frames.timingArtifactId = "timing"; c.frames.monotonic = true; c.frames.poseValidFrames = 1;
                c.clock.frameTimestamps = "c"; c.clock.firstFrameSensorUs = 10; c.clock.lastFrameSensorUs = 20; c.clock.segmentStartRealtimeSeconds = 0.5; c.clock.firstFrameRealtimeSeconds = 1; c.clock.captureStartRunSeconds = 0.5;
                c.segment.stoppedBy = "case_end"; c.segment.durationSeconds = 0.00001; c.segment.maxDurationSeconds = 300;
                return m;
            }
            Check(CaptureContract.Validate(Valid(), timing, out var reason), "baseline contract fixture is valid: " + reason);
            var cases = new (string, Action<CaptureProvenanceManifest, CaptureTiming>)[]
            {
                ("composited frames", (m, t) => m.capture.compositedFrames = true),
                ("no learner consent", (m, t) => m.capture.consent.learnerCaptureConsented = false),
                ("volunteer in frame without consent", (m, t) => m.capture.subjectInFrame = true),
                ("non-monotonic timestamps", (m, t) => t.sensorTimeUs = new long[] { 20, 20 }),
                ("receipt before segment", (m, t) => t.receivedRealtimeSeconds = new double[] { 0.1, 2 }),
                ("claimed clock alignment", (m, t) => m.capture.clock.clockAligned = true),
                ("mismatched attempt", (m, t) => t.attemptId = "other"),
                ("frame count mismatch", (m, t) => m.capture.video.frameCount = 3),
                ("oversized manifest", (m, t) => m.capture.runId = new string('r', CaptureContract.MaxManifestBytes)),
                ("unknown source", (m, t) => m.source = "unknown"),
            };
            foreach (var (name, mutate) in cases)
            {
                var m = Valid(); var t = JsonUtility.FromJson<CaptureTiming>(JsonUtility.ToJson(timing)); mutate(m, t);
                Check(!CaptureContract.Validate(m, t, out _), "contract rejects " + name);
            }
            var go = new GameObject("context contract"); var context = go.AddComponent<RecapRunContext>();
            context.Begin(MakeRun("att_ctx"));
            Check(!context.MarkCaptureUploading("att_ctx", "x") && !context.ReportCaptureFailure("att_ctx", "x"), "capture state cannot change before case end");
            context.EndSurgery("att_ctx", Grade());
            Check(!context.ReportCaptureFailure("other", "x") && !context.ReportCaptureFailure("att_ctx", " "), "stale or blank capture failures are rejected");
            Check(context.AttachMotionJob("att_ctx", "job_ctx", 1, false, "learner", "art"), "job attaches after case end");
            Check(!context.ReportCaptureFailure("att_ctx", "late") && context.result.replay.status == "queued", "a late capture failure cannot overwrite an attached job; the gateway owns its state");
            UnityEngine.Object.DestroyImmediate(go);
        }

        static void Wiring()
        {
            string session = File.ReadAllText("Assets/Scalpal/Quest/Runtime/NativeCaseSession.cs");
            int ensure = session.IndexOf("HandCaptureRecorder.Ensure()", StringComparison.Ordinal), begin = session.IndexOf("RecapSessionIntegration.Ensure().Begin(", StringComparison.Ordinal);
            Check(ensure > 0 && begin > ensure, "the OR subscribes capture before Begin opens the segment");
            Check(Regex.Matches(session, "AbandonCapture\\(\"").Count == 3, "retry, attempt change and leaving the OR abandon the segment");
            string handoff = File.ReadAllText("Assets/Scalpal/Handoff/Runtime/HandoffFlow.cs");
            Check(handoff.Contains("HandCaptureRecorder.PlannedNotice(") && handoff.Contains("learnerCaptureConsented = !p.learnerCaptureConsented") && !handoff.Contains("no hand clip is being saved"), "Time-Out and operator setup reflect real capture consent/state");
        }
    }
}
