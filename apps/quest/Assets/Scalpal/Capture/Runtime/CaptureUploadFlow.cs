using System;
using System.Collections.Generic;

namespace Scalpal.Capture
{
    // The headset side of the gateway's existing grant contract (services/realtime
    // reducers + services/api reconciler), the same sequence the companion uses:
    // requestUpload -> issued signed PUT -> PUT bytes -> markUploaded -> gateway
    // verifies size/sha256 -> available; then requestMotionJob(motion-v1) naming the
    // capture_manifest (+ timing) as extraArtifactIds. Polling only; never blocks.
    public interface ICaptureTransport : IDisposable
    {
        void Connect(string uri, string database, string token);
        void Tick();
        bool Ready { get; }
        string ConnectionError { get; }
        bool RequestUpload(string grantId, string artifactId, string sessionId, string attemptId, string kind, string filename, string contentType, ulong bytes, string sha256, out string error);
        // status: requested/issued/denied/expired, or null when the row is not visible yet.
        string GrantStatus(string grantId, out string url, out string method, out string reason);
        void StartPut(string artifactId, string url, string method, string path, string contentType, int timeoutSeconds);
        // null while running; "" on 2xx; otherwise the failure.
        string PutResult(string artifactId);
        bool MarkUploaded(string artifactId, out string error);
        string ArtifactStatus(string artifactId, out string reason);
        bool RequestMotionJob(string jobId, string inputArtifactId, List<string> extraArtifactIds, string configVersion, out string error);
        // Finds this job, or the deduplicated job for the same input/config.
        bool FindJob(string jobId, string inputArtifactId, string configVersion, out string foundJobId, out uint run, out string status);
        // Server rejection reported for a reducer call keyed by its id, or "".
        string Rejection(string key);
    }

    public sealed class CaptureUploadFlow
    {
        public sealed class Item
        {
            public string artifactId, grantId, kind, filename, contentType, path, sha256;
            public ulong bytes;
        }
        public enum Outcome { Running, Submitted, Failed }
        public const double ConnectTimeout = 15, GrantTimeout = 20, VerifyTimeout = 60, JobTimeout = 15;
        public const int PutAttempts = 3;
        public Outcome State { get; private set; } = Outcome.Running;
        public string Stage { get; private set; } = "connecting";
        public string Failure { get; private set; } = "";
        public string JobId { get; private set; } = "";
        public uint JobRun { get; private set; }
        readonly ICaptureTransport transport;
        readonly string sessionId, attemptId, jobId;
        readonly List<Item> items;
        readonly string uri, database, token;
        int index, putAttempt;
        double deadline = -1;
        bool started;

        public CaptureUploadFlow(ICaptureTransport transport, string uri, string database, string token,
            string sessionId, string attemptId, string jobId, List<Item> items)
        {
            this.transport = transport; this.uri = uri; this.database = database; this.token = token;
            this.sessionId = sessionId; this.attemptId = attemptId; this.jobId = jobId; this.items = items;
        }

        public Item Input => items[0];
        public List<string> Extras { get { var list = new List<string>(); for (int i = 1; i < items.Count; i++) list.Add(items[i].artifactId); return list; } }

        void Fail(string reason) { State = Outcome.Failed; Failure = Stage + ": " + reason; }
        bool Expired(double now, double seconds) { if (deadline < 0) deadline = now + seconds; return now > deadline; }
        void Next(string stage) { Stage = stage; deadline = -1; }

        public void Step(double now)
        {
            if (State != Outcome.Running) return;
            if (!started)
            {
                started = true;
                if (string.IsNullOrWhiteSpace(token)) { Stage = "credential"; Fail("no paired session credential on this headset"); return; }
                if (string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(database)) { Stage = "configuration"; Fail("realtime URI/database not configured"); return; }
                transport.Connect(uri, database, token);
            }
            transport.Tick();
            if (Stage == "connecting")
            {
                if (transport.ConnectionError.Length > 0) { Fail(transport.ConnectionError); return; }
                if (transport.Ready) Next("request_upload");
                else if (Expired(now, ConnectTimeout)) Fail("realtime database unreachable");
                return;
            }
            if (!transport.Ready) { Fail(transport.ConnectionError.Length > 0 ? transport.ConnectionError : "realtime connection lost"); return; }
            if (index < items.Count) { StepItem(now, items[index]); return; }
            if (Stage == "request_job")
            {
                if (!transport.RequestMotionJob(jobId, Input.artifactId, Extras, CaptureContract.ConfigVersion, out var error)) { Fail(error); return; }
                Next("await_job");
                return;
            }
            if (Stage == "await_job")
            {
                string rejected = transport.Rejection(jobId);
                if (rejected.Length > 0) { Fail("gateway rejected the motion job: " + rejected); return; }
                if (transport.FindJob(jobId, Input.artifactId, CaptureContract.ConfigVersion, out var found, out var run, out _))
                { JobId = found; JobRun = run; State = Outcome.Submitted; Stage = "submitted"; return; }
                if (Expired(now, JobTimeout)) Fail("motion job was not confirmed");
            }
        }

        void StepItem(double now, Item item)
        {
            switch (Stage)
            {
                case "request_upload":
                    if (!transport.RequestUpload(item.grantId, item.artifactId, sessionId, attemptId, item.kind, item.filename, item.contentType, item.bytes, item.sha256, out var error))
                    { Fail(error); return; }
                    Next("await_grant"); return;
                case "await_grant":
                {
                    string rejected = transport.Rejection(item.grantId);
                    if (rejected.Length > 0) { Fail("upload request rejected for " + item.kind + ": " + rejected); return; }
                    string status = transport.GrantStatus(item.grantId, out var url, out var method, out var reason);
                    if (status == "issued")
                    {
                        if (string.IsNullOrEmpty(url)) { Fail("gateway issued an empty upload URL"); return; }
                        putAttempt = 0; Next("put"); transport.StartPut(item.artifactId, url, string.IsNullOrEmpty(method) ? "PUT" : method, item.path, item.contentType, PutTimeout(item.bytes));
                        return;
                    }
                    if (status != null && status != "requested") { Fail("upload grant " + status + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)); return; }
                    if (Expired(now, GrantTimeout)) Fail("gateway did not issue an upload URL (is services/api running?)");
                    return;
                }
                case "put":
                {
                    string result = transport.PutResult(item.artifactId);
                    if (result == null) return;
                    if (result.Length > 0)
                    {
                        // A signed URL remains valid within its TTL; retry the same grant.
                        if (++putAttempt < PutAttempts) { transport.GrantStatus(item.grantId, out var url, out var method, out _); transport.StartPut(item.artifactId, url, string.IsNullOrEmpty(method) ? "PUT" : method, item.path, item.contentType, PutTimeout(item.bytes)); return; }
                        Fail("uploading " + item.kind + " failed: " + result); return;
                    }
                    if (!transport.MarkUploaded(item.artifactId, out var error2)) { Fail(error2); return; }
                    Next("await_verified"); return;
                }
                case "await_verified":
                {
                    string rejected = transport.Rejection(item.artifactId);
                    if (rejected.Length > 0) { Fail("markUploaded rejected: " + rejected); return; }
                    string status = transport.ArtifactStatus(item.artifactId, out var reason);
                    if (status == "available") { index++; Next(index < items.Count ? "request_upload" : "request_job"); return; }
                    if (status == "failed" || status == "deleted") { Fail(item.kind + " verification " + status + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)); return; }
                    if (Expired(now, VerifyTimeout)) Fail("gateway did not verify " + item.kind);
                    return;
                }
            }
        }
        // Allows ~256 KiB/s worst case, minimum one minute.
        public static int PutTimeout(ulong bytes) => (int)Math.Min(3600, Math.Max(60, bytes / 262144));
    }
}
