"""Pull worker for Nathan's gateway (packages/contracts/worker-api.md on nathan/companion-realtime).

Loop: claim -> download clip -> hand inference -> retarget -> render -> upload outputs -> complete.
A background heartbeat renews the lease while processing. A 409 at any point means this run
was superseded, so its result is dropped instead of overwriting a newer run.
"""

from __future__ import annotations

import json
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

from .trajectory import to_robot_trajectory

USER_AGENT = "scalpal-motion-worker/0"


class Stale(Exception):
    """The gateway rejected this run (lease lapsed, cancelled, or already finished)."""


class Gateway:
    def __init__(self, url: str, token: str):
        self.url = url.rstrip("/")
        self.token = token

    def call(self, path: str, body: dict | None = None) -> tuple[int, dict | None]:
        req = urllib.request.Request(
            self.url + path,
            data=json.dumps(body or {}).encode(),
            method="POST",
            headers={"authorization": f"Bearer {self.token}", "content-type": "application/json", "user-agent": USER_AGENT},
        )
        try:
            with urllib.request.urlopen(req, timeout=30) as res:
                raw = res.read()
                return res.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as err:
            raw = err.read()
            if err.code == 409:
                raise Stale(raw.decode(errors="replace")) from err
            try:
                return err.code, (json.loads(raw) if raw else None)
            except json.JSONDecodeError:
                return err.code, {"raw": raw.decode(errors="replace")}

    def resolve(self, url: str) -> str:
        return url if url.startswith(("http://", "https://")) else self.url + url


class Heartbeat(threading.Thread):
    """Renews the lease every lease/3 seconds and remembers the latest progress/stage."""

    def __init__(self, gateway: Gateway, path: str, lease_ms: int):
        super().__init__(daemon=True)
        self.gateway, self.path, self.lease_ms = gateway, path, lease_ms
        self.progress, self.stage = 0.0, "claimed"
        self.stop_event = threading.Event()
        self.stale: str | None = None
        self.lock = threading.Lock()

    def update(self, progress: float, stage: str) -> None:
        with self.lock:
            self.progress, self.stage = progress, stage
        self.beat()
        if self.stale:
            raise Stale(self.stale)

    def beat(self) -> None:
        with self.lock:
            body = {"progress": self.progress, "stage": self.stage, "leaseMs": self.lease_ms}
        try:
            self.gateway.call(self.path, body)
        except Stale as e:
            self.stale = str(e)
        except Exception:  # transient network failure; the next beat retries
            pass

    def run(self) -> None:
        while not self.stop_event.wait(self.lease_ms / 3000.0):
            self.beat()


def _download(url: str, headers: dict, dest: Path) -> None:
    req = urllib.request.Request(url, headers=headers or {})
    with urllib.request.urlopen(req, timeout=120) as res, dest.open("wb") as f:
        while chunk := res.read(1 << 20):
            f.write(chunk)


def _upload(gateway: Gateway, outputs_path: str, kind: str, path: Path, content_type: str) -> str:
    status, out = gateway.call(outputs_path, {"kind": kind, "filename": path.name, "contentType": content_type})
    if status != 200 or not out:
        raise RuntimeError(f"registering {kind} output failed: {status} {out}")
    up = out["upload"]
    req = urllib.request.Request(
        gateway.resolve(up["url"]), data=path.read_bytes(), method=up.get("method", "PUT"), headers=up.get("headers", {})
    )
    with urllib.request.urlopen(req, timeout=300) as res:
        if not 200 <= res.status < 300:
            raise RuntimeError(f"uploading {kind} failed: HTTP {res.status}")
    return out["artifactId"]


def process_claim(gateway: Gateway, claim: dict, mirrored: bool = False, log=print) -> str:
    """Handle one claimed job. Returns "completed", "failed", or "stale"."""
    from .perception import track_hand
    from .replay import render_replay
    from .retarget import retarget_frames

    job, endpoints = claim["job"], claim["endpoints"]
    lease_ms = int(job.get("leaseMs", 120_000))
    hb = Heartbeat(gateway, endpoints["heartbeat"], lease_ms)
    hb.start()

    def fail(error: str, retryable: bool) -> str:
        try:
            gateway.call(endpoints["fail"], {"error": error[:2000], "retryable": retryable})
        except Stale:
            return "stale"
        log(f"failed ({'retryable' if retryable else 'final'}): {error}")
        return "failed"

    try:
        clip_in = next((i for i in claim.get("inputs", []) if i.get("role") == "input"), None)
        if clip_in is None:
            return fail("job has no input clip", retryable=False)
        with tempfile.TemporaryDirectory(prefix="scalpal-motion-") as tmp:
            tmp = Path(tmp)
            clip = tmp / ("clip" + Path(clip_in.get("filename", "clip.mp4")).suffix)
            hb.update(0.05, "downloading clip")
            try:
                dl = clip_in["download"]
                _download(gateway.resolve(dl["url"]), dl.get("headers", {}), clip)
            except Exception as e:
                return fail(f"could not download input clip: {e}", retryable=True)

            hb.update(0.15, "hand inference")
            try:
                track = track_hand(clip, hand="Right", mirrored=mirrored)
            except FileNotFoundError as e:
                return fail(f"could not decode input clip: {e}", retryable=False)
            if track["summary"]["frames"] == 0:
                return fail("no frames could be decoded from the clip", retryable=False)
            if track["summary"]["valid_frames"] == 0:
                return fail(f"no right hand detected in any of {track['summary']['frames']} frames", retryable=False)

            hb.update(0.6, "retargeting")
            motion = retarget_frames(track["frames"], hand="Right")
            motion["input"] = {"artifact_id": clip_in.get("artifactId"), "model": track["model"]}
            trajectory = to_robot_trajectory(motion, clip_in.get("artifactId"))

            hb.update(0.75, "rendering replay")
            files = {
                "robot_trajectory": (tmp / "robot_trajectory.json", "application/json"),
                "hand_estimates": (tmp / "hand_track.json", "application/json"),
                "quality_report": (tmp / "quality_report.json", "application/json"),
                "replay_video": (tmp / "replay.mp4", "video/mp4"),
            }
            files["robot_trajectory"][0].write_text(json.dumps(trajectory))
            files["hand_estimates"][0].write_text(json.dumps(track))
            s = motion["summary"]
            files["quality_report"][0].write_text(json.dumps({
                "job": job, "summary": s, "retargeting": motion["retargeting"], "robot": motion["robot"],
                "perception": track["model"], "mirrored_input": mirrored,
            }, indent=1))
            render_replay(motion, files["replay_video"][0], video_path=clip, track=track)

            hb.update(0.9, "uploading outputs")
            artifact_ids = [_upload(gateway, endpoints["outputs"], kind, p, ct) for kind, (p, ct) in files.items()]

            corr = s.get("finger_bend_correlation") or {}
            corr_txt = ", ".join(f"{k} {v:.2f}" for k, v in corr.items() if v is not None)
            notes = (
                f"{100 * s['valid_fraction']:.0f}% of frames tracked; finger motion only, wrist not reconstructed"
                + (f"; robot-vs-estimate finger bend r: {corr_txt}" if corr_txt else "")
            )
            hb.update(0.97, "completing")
            status, res = gateway.call(endpoints["complete"], {
                "outputArtifactIds": artifact_ids,
                "quality": {
                    "framesTotal": s["frames"],
                    "framesValid": s["valid_frames"],
                    "invalidIntervals": len(trajectory["invalidIntervals"]),
                    "robotModel": trajectory["robot"]["model"],
                    "replayKind": trajectory["kind"],
                    "notes": notes,
                },
            })
            if status != 200:
                return fail(f"gateway rejected completion: {status} {res}", retryable=True)
            log(f"completed {job['jobId']} run {job['run']}: {notes}")
            return "completed"
    except Stale as e:
        log(f"run {job.get('jobId')}#{job.get('run')} superseded, dropping result: {e}")
        return "stale"
    except Exception as e:  # report every processor failure to the gateway
        return fail(f"processor error: {type(e).__name__}: {e}", retryable=True)
    finally:
        hb.stop_event.set()


def run_worker(url: str, token: str, lease_ms: int = 120_000, mirrored: bool = False, once: bool = False,
               poll_s: float = 2.0, log=print) -> None:
    gateway = Gateway(url, token)
    log(f"polling {gateway.url} for motion jobs")
    while True:
        try:
            status, claim = gateway.call("/v1/worker/claim", {"leaseMs": lease_ms})
        except Exception as e:
            log(f"claim request failed: {e}")
            status, claim = 0, None
        if status == 200 and claim:
            log(f"claimed {claim['job']['jobId']} run {claim['job']['run']}")
            process_claim(gateway, claim, mirrored=mirrored, log=log)
            if once:
                return
            continue
        if status not in (0, 204):
            log(f"claim failed: {status} {claim}")
        if once:
            return
        time.sleep(poll_s if status == 204 else 5.0)
