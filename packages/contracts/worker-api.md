# Motion Worker API v1 (proposed)

Status: **proposed by Nathan, implemented in `services/api/src/http.ts`; needs Silas's agreement.** Reference clients: `services/api/examples/worker_client.py` (stdlib Python) and `services/api/scripts/synthetic-worker.ts`.

Workers are pull-based: they poll the gateway over HTTPS, so they can run on a laptop behind NAT. Every request carries `Authorization: Bearer <worker token>`. The gateway maps tokens to a `workerId` through `WORKER_TOKENS`.

## 1. Claim

`POST /v1/worker/claim` with body `{ "leaseMs": 120000 }` (optional; clamped to [min, 600000])

- `204`: nothing queued.
- `200`:

```json
{
  "job": { "jobId": "job_…", "run": 1, "sessionId": "ses_…", "attemptId": "ses_…-a1", "configVersion": "motion-v1", "leaseMs": 120000 },
  "inputs": [
    { "role": "input", "artifactId": "art_…", "kind": "raw_clip", "filename": "clip.mp4", "contentType": "video/mp4", "bytes": 1234, "sha256": "…",
      "download": { "url": "https://…", "method": "GET", "headers": {} } },
    { "role": "extra", "kind": "capture_manifest", "…": "…" }
  ],
  "endpoints": {
    "heartbeat": "/v1/worker/jobs/<job>/runs/<run>/heartbeat",
    "outputs":   "/v1/worker/jobs/<job>/runs/<run>/outputs",
    "complete":  "/v1/worker/jobs/<job>/runs/<run>/complete",
    "fail":      "/v1/worker/jobs/<job>/runs/<run>/fail"
  }
}
```

Download URLs are valid for one hour by default.

## 2. Heartbeat (renews the lease)

`POST {heartbeat}` with body `{ "progress": 0.4, "stage": "hand inference", "leaseMs": 120000 }`

Send at least every `leaseMs / 2`. If the lease lapses, the job is requeued and another worker may claim run `n+1`; your run then gets `409`.

## 3. Register and upload outputs

`POST {outputs}` with body `{ "kind": "robot_trajectory", "filename": "robot_trajectory.json", "contentType": "application/json" }`

Response: `{ "artifactId": "art_…", "upload": { "url", "method": "PUT", "headers": { "content-type": "…" } } }`. PUT the bytes with exactly those headers.

Output kinds: `robot_trajectory` (required for the companion's replay view), `replay_video` (optional rendered simulation), `hand_estimates`, `quality_report`.

## 4. Complete

`POST {complete}` with body:

```json
{
  "outputArtifactIds": ["art_…"],
  "quality": {
    "framesTotal": 150, "framesValid": 138, "invalidIntervals": 1,
    "robotModel": "shadow-hand", "replayKind": "kinematic",
    "notes": "finger flexion only; wrist translation not reconstructed"
  }
}
```

The gateway checks that every output exists in storage before accepting. Responses:

| Code | Meaning |
| --- | --- |
| `200` | Accepted; the job is `ready` and viewers see it immediately |
| `409` | Stale run (superseded, cancelled, or already finished). Drop the result |
| `422` | An output was registered but never uploaded |
| `400` | Validation error |

## 5. Fail

`POST {fail}` with body `{ "error": "no hand detected in 92% of frames", "retryable": false }`

`retryable: true` requeues the job if runs remain; otherwise the job fails and the operator can retry.

## Expectations

- Report missing segments honestly: mark frames invalid in the trajectory rather than interpolating long gaps.
- Distinguish reconstructed finger motion from metric wrist/arm motion in `notes`.
- Do not substitute canned animation for a failed reconstruction. Fail instead.
- Keep clips and derived data out of Git; download from the signed URL and delete local copies per the agreed retention.
