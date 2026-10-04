"""Reference client for the Scalpal motion worker API (stdlib only).

Silas's processor can copy this loop: claim a job, download the clip through
the signed URL, run reconstruction/retargeting, upload outputs, complete.
See packages/contracts/worker-api.md for the protocol.

    GATEWAY_URL=http://localhost:8788 WORKER_TOKEN=... python worker_client.py
"""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request

GATEWAY = os.environ.get("GATEWAY_URL", "http://localhost:8788").rstrip("/")
TOKEN = os.environ["WORKER_TOKEN"]


class Stale(Exception):
    """The run was superseded (lease expired, cancelled, or already finished)."""


def call(path: str, body: dict | None = None) -> tuple[int, dict | None]:
    req = urllib.request.Request(
        GATEWAY + path,
        data=json.dumps(body or {}).encode(),
        method="POST",
        headers={"authorization": f"Bearer {TOKEN}", "content-type": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as res:
            raw = res.read()
            return res.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as err:
        raw = err.read()
        if err.code == 409:
            raise Stale(raw.decode()) from err
        return err.code, (json.loads(raw) if raw else None)


def download(url: str, dest: str) -> None:
    with urllib.request.urlopen(url, timeout=120) as res, open(dest, "wb") as f:
        while chunk := res.read(1 << 20):
            f.write(chunk)


def upload(signed: dict, data: bytes) -> None:
    req = urllib.request.Request(signed["url"], data=data, method=signed["method"], headers=signed["headers"])
    with urllib.request.urlopen(req, timeout=120) as res:
        assert 200 <= res.status < 300, res.status


def process(job: dict, inputs: list[dict], endpoints: dict) -> None:
    clip = next(i for i in inputs if i["role"] == "input")
    path = f"/tmp/{job['jobId']}-run{job['run']}-{clip['filename']}"
    call(endpoints["heartbeat"], {"progress": 0.05, "stage": "downloading clip"})
    download(clip["download"]["url"], path)

    # ---- Replace this block with real hand inference + retargeting. --------
    # Call heartbeat at least every leaseMs / 2 so the lease does not expire.
    call(endpoints["heartbeat"], {"progress": 0.5, "stage": "hand inference"})
    trajectory = {
        "schema": "scalpal.robot_trajectory.v1",
        "kind": "kinematic",
        "robot": {"model": "your-hand", "joints": [{"name": "index_mcp", "unit": "rad", "lower": 0, "upper": 1.6}]},
        "timebase": {"unit": "ms", "clock": "clip_pts", "startMs": 0},
        "frames": {"t": [0, 33], "q": [[0.0], [0.1]], "valid": [True, True]},
        "invalidIntervals": [],
        "source": {"inputArtifactId": clip["artifactId"], "handedness": "right"},
        "notes": "example output",
    }
    # -----------------------------------------------------------------------

    status, out = call(endpoints["outputs"], {
        "kind": "robot_trajectory",
        "filename": "robot_trajectory.json",
        "contentType": "application/json",
    })
    assert status == 200, out
    upload(out["upload"], json.dumps(trajectory).encode())

    frames = len(trajectory["frames"]["t"])
    valid = sum(1 for v in trajectory["frames"]["valid"] if v)
    status, res = call(endpoints["complete"], {
        "outputArtifactIds": [out["artifactId"]],
        "quality": {
            "framesTotal": frames,
            "framesValid": valid,
            "invalidIntervals": len(trajectory["invalidIntervals"]),
            "robotModel": trajectory["robot"]["model"],
            "replayKind": trajectory["kind"],
            "notes": trajectory["notes"],
        },
    })
    assert status == 200, res


def main() -> None:
    print(f"polling {GATEWAY}")
    while True:
        status, claim = call("/v1/worker/claim", {"leaseMs": 120_000})
        if status == 204:
            time.sleep(2)
            continue
        if status != 200 or claim is None:
            print("claim failed", status, claim)
            time.sleep(5)
            continue
        job, endpoints = claim["job"], claim["endpoints"]
        print(f"claimed {job['jobId']} run {job['run']}")
        try:
            process(job, claim["inputs"], endpoints)
            print("completed")
        except Stale as err:
            print("run superseded, dropping:", err)
        except Exception as err:  # noqa: BLE001 - report every failure to the gateway
            print("failed:", err)
            try:
                call(endpoints["fail"], {"error": str(err)[:2000], "retryable": True})
            except Stale:
                pass


if __name__ == "__main__":
    main()
