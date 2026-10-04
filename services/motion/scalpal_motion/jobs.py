"""Worker boundary for Nathan's gateway: one job in, one result manifest out.

A job names its input clip (local path or signed http(s) URL) and its job/run identity.
Outputs go to <output_dir>/<run_id>/ so a retried or superseded run never overwrites
another run's files. The result reports processing status and motion quality only; it
does not decide learning completion or contribution acceptance.
"""

from __future__ import annotations

import hashlib
import json
import re
import tempfile
import time
import urllib.request
from datetime import datetime, timezone
from importlib.metadata import version
from pathlib import Path

from . import ROBOT_MOTION_SCHEMA

JOB_SCHEMA = "scalpal.motion_job/0"
RESULT_SCHEMA = "scalpal.motion_result/0"
DEFAULT_CONFIG = {"hand": "Right", "mirrored": False, "smooth": None}
RUN_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}")


def valid_run_id(value: object) -> bool:
    """Run IDs are literal directory names; reject rather than sanitize aliases."""
    return isinstance(value, str) and RUN_ID.fullmatch(value) is not None


class JobError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _artifact(kind: str, path: Path, content_type: str) -> dict:
    return {
        "kind": kind,
        "file": path.name,
        "content_type": content_type,
        "bytes": path.stat().st_size,
        "sha256": _sha256(path),
    }


def _fetch_input(source: str, workdir: Path) -> Path:
    if source.startswith(("http://", "https://")):
        dest = workdir / "input_clip"
        try:
            urllib.request.urlretrieve(source, dest)
        except Exception as e:  # network/storage failures are reported, not raised
            raise JobError("input_unavailable", f"could not download input: {e}") from e
        return dest
    path = Path(source).expanduser()
    if not path.is_file():
        raise JobError("input_unavailable", f"input file not found: {path.name}")
    return path


def _quality(motion: dict) -> dict:
    s = motion["summary"]
    valid = [seg for seg in s["segments"] if seg["status"] == "valid"]
    longest = max(((seg["end_ms"] - seg["start_ms"]) / 1000.0 for seg in valid), default=0.0)
    return {
        "frames": s["frames"],
        "valid_frames": s["valid_frames"],
        "valid_fraction": s["valid_fraction"],
        "longest_valid_segment_s": longest,
        "vector_error_m_median": s["vector_error_m_median"],
        "vector_error_m_p95": s["vector_error_m_p95"],
        "finger_bend_correlation": s.get("finger_bend_correlation"),
        "segments": s["segments"],
        "note": "vector error is fit to the estimated human hand, not reconstruction accuracy",
    }


def validate_job(job: dict) -> dict:
    if not isinstance(job, dict):
        raise JobError("bad_job", "job must be an object")
    if job.get("schema") != JOB_SCHEMA:
        raise JobError("bad_job", f"expected schema {JOB_SCHEMA}")
    for key in ("job_id", "run_id", "input"):
        if not job.get(key):
            raise JobError("bad_job", f"missing {key}")
    if not isinstance(job["job_id"], str):
        raise JobError("bad_job", "job_id must be a string")
    if not valid_run_id(job["run_id"]):
        raise JobError("bad_job", "run_id must start with an ASCII letter or digit and contain only letters, digits, -, _, or . (maximum 128 characters)")
    if not isinstance(job["input"], dict):
        raise JobError("bad_job", "input must be an object")
    if not isinstance(job["input"].get("source"), str) or not job["input"]["source"]:
        raise JobError("bad_job", "missing input.source")
    if not isinstance(job.get("config", {}), dict):
        raise JobError("bad_job", "config must be an object")
    config = {**DEFAULT_CONFIG, **job.get("config", {})}
    if config["hand"] not in ("Right", "Left"):
        raise JobError("unsupported_config", "hand must be Right or Left")
    return config


def run_job(job: dict, output_root: str | Path) -> dict:
    """Process one job. Always returns a result dict; never raises for job-level failures."""
    started = time.perf_counter()
    fields = job if isinstance(job, dict) else {}
    input_fields = fields.get("input") if isinstance(fields.get("input"), dict) else {}
    result = {
        "schema": RESULT_SCHEMA,
        "job_id": fields.get("job_id"),
        "run_id": fields.get("run_id"),
        "attempt_id": fields.get("attempt_id"),
        "input_artifact_id": input_fields.get("artifact_id"),
        "status": "failed",
        "processor": {
            "name": "scalpal-motion",
            "version": version("scalpal-motion"),
            "output_schema": ROBOT_MOTION_SCHEMA,
        },
    }
    out_dir = None
    try:
        config = validate_job(job)
        result["config"] = config
        run_dir = Path(output_root) / job["run_id"]
        try:
            run_dir.mkdir(parents=True)
        except FileExistsError:
            # Never touch an existing run's files, including its result.json.
            raise JobError("run_exists", "outputs for this run_id already exist; use a new run_id to retry")
        out_dir = run_dir

        from .perception import track_hand
        from .replay import render_replay
        from .retarget import RETARGET_CONFIG_VERSION, retarget_frames

        with tempfile.TemporaryDirectory() as tmp:
            clip = _fetch_input(job["input"]["source"], Path(tmp))
            try:
                track = track_hand(clip, hand=config["hand"], mirrored=config["mirrored"])
            except FileNotFoundError as e:
                raise JobError("decode_failed", str(e)) from e
            if track["summary"]["frames"] == 0:
                raise JobError("decode_failed", "no frames could be decoded")
            motion = retarget_frames(track["frames"], hand=config["hand"], low_pass_alpha=config["smooth"])
            motion["input"] = {"artifact_id": result["input_artifact_id"], "model": track["model"]}

            (out_dir / "hand_track.json").write_text(json.dumps(track))
            (out_dir / "motion.json").write_text(json.dumps(motion))
            result["processor"].update(retarget_config=RETARGET_CONFIG_VERSION, robot=motion["robot"]["name"])
            result["quality"] = _quality(motion)
            artifacts = [
                _artifact("hand_track", out_dir / "hand_track.json", "application/json"),
                _artifact("robot_motion", out_dir / "motion.json", "application/json"),
            ]
            result["artifacts"] = artifacts
            if track["summary"]["valid_frames"] == 0:
                raise JobError("no_hand_detected", f"no {config['hand'].lower()} hand found in any frame")
            render_replay(motion, out_dir / "replay.mp4", video_path=clip, track=track)
            artifacts.append(_artifact("replay_video", out_dir / "replay.mp4", "video/mp4"))
        result["status"] = "ready"
    except JobError as e:
        result["error"] = {"code": e.code, "message": str(e)}
    except Exception as e:  # unexpected processor failure: report it rather than crash the worker
        result["error"] = {"code": "processor_error", "message": f"{type(e).__name__}: {e}"}
    result["processing_s"] = round(time.perf_counter() - started, 2)
    result["finished_at"] = datetime.now(timezone.utc).isoformat()
    if out_dir is not None and out_dir.exists():
        result["output_dir"] = str(out_dir)
        (out_dir / "result.json").write_text(json.dumps(result, indent=1))
    return result
