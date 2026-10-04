using System;
using System.Collections.Generic;
using SpacetimeDB;
using SpacetimeDB.Types;
using UnityEngine;
using UnityEngine.Networking;

namespace Scalpal.Capture
{
    // Opens its own connection with the paired headset's existing identity token
    // (QuestSessionBridge's persisted token, exposed through RecapRunContext). It
    // lives on the persistent run root, so uploads continue after the OR scene and
    // its bridge are destroyed. The token is never logged or serialized.
    public sealed class SpacetimeCaptureTransport : ICaptureTransport
    {
        DbConnection connection;
        SubscriptionHandle subscription;
        bool subscribed;
        string connectionError = "";
        readonly Dictionary<string, string> rejections = new Dictionary<string, string>();
        readonly Dictionary<string, UnityWebRequest> puts = new Dictionary<string, UnityWebRequest>();
        readonly Dictionary<string, string> putResults = new Dictionary<string, string>();

        public bool Ready => connection != null && connection.IsActive && subscribed;
        public string ConnectionError => connectionError;

        public void Connect(string uri, string database, string token)
        {
            try
            {
                connection = DbConnection.Builder().WithUri(uri).WithDatabaseName(database).WithToken(token).WithCompression(Compression.None)
                    .OnConnect((conn, identity, _) =>
                    {
                        if (conn != connection) return;
                        Hook(conn);
                        subscription = conn.SubscriptionBuilder()
                            .OnApplied(_ => { if (conn == connection) subscribed = true; })
                            .OnError((_, error) => { if (conn == connection) { subscribed = false; connectionError = "subscription failed"; } })
                            .Subscribe(new[] { "SELECT * FROM my_transfer_grants", "SELECT * FROM session_artifacts", "SELECT * FROM session_motion_jobs" });
                    })
                    .OnConnectError(error => connectionError = "realtime connection failed")
                    .OnDisconnect((conn, error) => { if (conn == connection) { subscribed = false; connectionError = "realtime connection lost"; } })
                    .Build();
            }
            catch (Exception e) { connectionError = "realtime connection failed"; Debug.LogWarning("SCALPAL_CAPTURE connect: " + e.GetType().Name); }
        }

        bool Mine(ReducerEventContext ctx) => connection?.Identity is Identity id && id.Equals(ctx.Event.CallerIdentity);
        void Record(ReducerEventContext ctx, string key)
        {
            if (!Mine(ctx)) return;
            if (ctx.Event.Status is Status.Failed(var reason)) rejections[key] = string.IsNullOrEmpty(reason) ? "rejected" : reason;
            else if (ctx.Event.Status is Status.OutOfEnergy(var _)) rejections[key] = "out of energy";
        }
        void Hook(DbConnection conn)
        {
            conn.Reducers.OnRequestUpload += (ctx, grantId, artifactId, s, a, k, f, c, b, h) => Record(ctx, grantId);
            conn.Reducers.OnMarkUploaded += (ctx, artifactId) => Record(ctx, artifactId);
            conn.Reducers.OnRequestMotionJob += (ctx, jobId, input, extras, config) => Record(ctx, jobId);
        }

        public void Tick()
        {
            if (connection == null) return;
            try { connection.FrameTick(); }
            catch (Exception e) { subscribed = false; connectionError = "realtime processing failed"; Debug.LogWarning("SCALPAL_CAPTURE tick: " + e.GetType().Name); }
            foreach (var pair in puts)
                if (pair.Value.isDone && !putResults.ContainsKey(pair.Key))
                    putResults[pair.Key] = pair.Value.result == UnityWebRequest.Result.Success ? "" : "HTTP " + pair.Value.responseCode + " " + pair.Value.error;
        }

        public bool RequestUpload(string grantId, string artifactId, string sessionId, string attemptId, string kind, string filename, string contentType, ulong bytes, string sha256, out string error)
        {
            error = "";
            try { connection.Reducers.RequestUpload(grantId, artifactId, sessionId, attemptId, kind, filename, contentType, bytes, sha256); return true; }
            catch (Exception e) { error = "upload request could not be sent (" + e.GetType().Name + ")"; return false; }
        }

        public string GrantStatus(string grantId, out string url, out string method, out string reason)
        {
            var grant = connection?.Db.MyTransferGrants.GrantId.Find(grantId);
            url = grant?.Url ?? ""; method = grant?.Method ?? ""; reason = grant?.Reason ?? "";
            return grant?.Status;
        }

        public void StartPut(string artifactId, string url, string method, string path, string contentType, int timeoutSeconds)
        {
            if (puts.TryGetValue(artifactId, out var old)) { old.Dispose(); puts.Remove(artifactId); }
            putResults.Remove(artifactId);
            // Streams the file from disk; the clip is never loaded into memory.
            var request = new UnityWebRequest(url, method) { uploadHandler = new UploadHandlerFile(path), downloadHandler = new DownloadHandlerBuffer(), timeout = timeoutSeconds };
            request.SetRequestHeader("Content-Type", contentType); // part of the local signed-URL signature and S3 presign
            puts[artifactId] = request;
            request.SendWebRequest();
        }

        public string PutResult(string artifactId) => putResults.TryGetValue(artifactId, out var result) ? result : null;

        public bool MarkUploaded(string artifactId, out string error)
        {
            error = "";
            try { connection.Reducers.MarkUploaded(artifactId); return true; }
            catch (Exception e) { error = "markUploaded could not be sent (" + e.GetType().Name + ")"; return false; }
        }

        public string ArtifactStatus(string artifactId, out string reason)
        {
            var artifact = connection?.Db.SessionArtifacts.ArtifactId.Find(artifactId);
            reason = artifact?.StatusReason ?? "";
            return artifact?.Status;
        }

        public bool RequestMotionJob(string jobId, string inputArtifactId, List<string> extraArtifactIds, string configVersion, out string error)
        {
            error = "";
            try { connection.Reducers.RequestMotionJob(jobId, inputArtifactId, extraArtifactIds, configVersion); return true; }
            catch (Exception e) { error = "motion job request could not be sent (" + e.GetType().Name + ")"; return false; }
        }

        public bool FindJob(string jobId, string inputArtifactId, string configVersion, out string foundJobId, out uint run, out string status)
        {
            foundJobId = status = ""; run = 0;
            if (connection == null) return false;
            foreach (var job in connection.Db.SessionMotionJobs.Iter())
            {
                if (job.JobId != jobId && !(job.InputArtifactId == inputArtifactId && job.ConfigVersion == configVersion)) continue;
                foundJobId = job.JobId; run = job.Run; status = job.Status; return true;
            }
            return false;
        }

        public string Rejection(string key) => rejections.TryGetValue(key, out var reason) ? reason : "";

        public void Dispose()
        {
            foreach (var request in puts.Values) { try { request.Abort(); request.Dispose(); } catch (Exception) { } }
            puts.Clear();
            var old = connection; connection = null; subscribed = false;
            if (old != null)
            {
                try { if (old.IsActive) subscription?.Unsubscribe(); } catch (Exception) { }
                try { old.Disconnect(); } catch (Exception) { }
            }
        }
    }
}
