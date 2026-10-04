#!/usr/bin/env python3
"""Launch or observe the Quest app over USB; report app-scoped runtime readiness."""

import argparse
import math
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

PACKAGE = "com.scalpal.nativeworkbench"
ACTIVITY = PACKAGE + "/com.unity3d.player.UnityPlayerGameActivity"
SCENES = ("Launch", "ScalpalShell", "DiagnosisOffice", "NativeSession", "RunEnding", "NativeWorkbench")
FLOW_MARKER = "SCALPAL_FLOW_STATUS "
NATIVE_MARKER = "SCALPAL_NATIVE_STATUS "
FAULT = re.compile(r"\b\w*Exception\b|FATAL EXCEPTION|Fatal signal|SCALPAL_NATIVE_BINDING_ERROR")


def device_time(output):
    try:
        value = float(output.strip())
        return value if math.isfinite(value) else None
    except ValueError:
        return None


def fields(record):
    """Reject malformed/duplicate fields rather than substring-matching True."""
    result = {}
    for token in record.split():
        key, separator, value = token.partition("=")
        if not separator or key in result:
            return {}
        result[key] = value
    return result


def assess_logs(output, now, since, expected_scene=None):
    """Pure assessment of epoch-formatted, PID-scoped logs; never print raw logs."""
    if now is None or since is None or not math.isfinite(now) or not math.isfinite(since) or now < since:
        return 2, ["Device clock is unavailable or changed; current readiness is not confirmed."]
    records = []
    faults = 0
    for line in output.splitlines():
        stamp = device_time(line.split(maxsplit=1)[0]) if line.strip() else None
        # Ignore retained records from earlier observations, and future timestamps.
        if stamp is None or not since <= stamp < now + 1:
            continue
        if FAULT.search(line):
            faults += 1
        for marker in (FLOW_MARKER, NATIVE_MARKER):
            if marker in line:
                records.append((stamp, marker, fields(line.split(marker, 1)[1])))
                break
    if faults:
        return 1, [f"App-scoped faults observed in this observation window: {faults}. Details omitted from smoke output; inspect app logs locally."]

    flow = [entry for entry in records if entry[1] == FLOW_MARKER]
    native = [entry for entry in records if entry[1] == NATIVE_MARKER]
    # Sort by epoch, not assumed logcat ordering. Freshness is assessed at the
    # final sample; a past valid frame cannot mask a current lost-tracking frame.
    latest_flow = max(flow, key=lambda entry: entry[0]) if flow else None
    latest_native = max(native, key=lambda entry: entry[0]) if native else None
    messages = []
    if latest_flow:
        stamp, _, state = latest_flow
        scene = state.get("scene")
        if scene not in SCENES:
            return 2, ["App telemetry reports an unknown scene; supported flow readiness is not confirmed."]
        if expected_scene and scene != expected_scene:
            return 2, [f"Observed scene={scene}; expected scene={expected_scene}."]
        messages.append("SCALPAL_FLOW_STATUS " + " ".join(f"{key}={state.get(key, 'missing')}" for key in ("scene", "xr", "head", "focus") if key == "scene" or state.get(key) in ("True", "False")))
        # `date +%s` is rounded down whereas logcat epoch includes fractions;
        # accept a record within that final second, not a future clock second.
        if not -1 < now - stamp <= 5 or any(state.get(key) != "True" for key in ("xr", "head", "focus")):
            return 2, messages + ["Current scene XR/head/focus readiness is not confirmed."]
        if scene not in ("NativeSession", "NativeWorkbench"):
            return 0, messages + ["Current scene XR/head/focus telemetry is ready. This is not evidence of stage interaction or a complete human playthrough."]
        # Native OR/workbench keeps its stricter alignment/floor readiness gate.
    elif expected_scene:
        return 2, [f"No current flow scene telemetry; expected scene={expected_scene} is not confirmed."]

    if latest_native:
        stamp, _, state = latest_native
        required = ("xr", "head", "floor", "focus", "aligned")
        messages.append("SCALPAL_NATIVE_STATUS " + " ".join(f"{key}={state.get(key)}" for key in required if state.get(key) in ("True", "False")))
        if -1 < now - stamp <= 5 and all(state.get(key) == "True" for key in required):
            return 0, messages + ["Native XR/head/floor/focus/alignment telemetry is ready. Human pickup, stereo, registration and workflow checks are still required."]
    return 2, messages + ["App process exists, but current native XR/head/floor/focus/alignment readiness is not confirmed."]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, help="Install this APK before launch.")
    parser.add_argument("--seconds", type=int, default=15, choices=range(1, 46), metavar="1..45")
    parser.add_argument("--no-launch", action="store_true", help="Observe the current stage without requesting a launch.")
    parser.add_argument("--expect-scene", choices=SCENES, help="Require this actual runtime scene (does not navigate to it).")
    args = parser.parse_args()
    if args.apk and args.no_launch:
        parser.error("--apk cannot be combined with --no-launch")
    adb = shutil.which("adb")
    if not adb:
        print("adb is missing; add Android platform-tools to PATH.", file=sys.stderr)
        return 1

    def run(*command):
        return subprocess.run([adb, "-d", *command], capture_output=True, text=True, timeout=60)

    connected = run("get-state")
    if connected.returncode or connected.stdout.strip() != "device":
        print("No single authorized USB device. Connect Quest and accept USB debugging.", file=sys.stderr)
        return 1
    if args.apk:
        if not args.apk.is_file():
            parser.error("--apk must name an existing APK")
        installed = run("install", "-r", str(args.apk.resolve()))
        if installed.returncode or "Success" not in installed.stdout:
            print("APK install failed; inspect adb locally for details.", file=sys.stderr)
            return 1
        print("APK installed.")
    start_clock = run("shell", "date", "+%s")
    since = device_time(start_clock.stdout) if start_clock.returncode == 0 else None
    if since is None:
        print("Device clock could not be read; fresh smoke evidence cannot be assessed.")
        return 2
    if not args.no_launch:
        launch = run("shell", "am", "start", "-n", ACTIVITY)
        if launch.returncode or "Error:" in launch.stdout + launch.stderr:
            print("App launch failed; inspect adb locally for details.", file=sys.stderr)
            return 1

    # Quest can intercept launch with its controllers-required dialog; am success
    # alone does not prove an app process or XR session exists.
    deadline = time.monotonic() + args.seconds
    pid = ""
    while time.monotonic() < deadline:
        result = run("shell", "pidof", PACKAGE)
        pid = result.stdout.strip() if result.returncode == 0 else ""
        time.sleep(min(1, max(0, deadline - time.monotonic())))
    if not pid or not pid.isdecimal():
        activity = run("shell", "dumpsys", "activity", "activities")
        if "LaunchCheckControllerRequiredDialogActivity" in activity.stdout:
            print("Quest is blocking launch at its controllers-required dialog. Wear Quest, wake both controllers and accept the prompt.")
        else:
            print("App is not running. Wear Quest, wake both controllers, accept any launch prompt, then retry.")
        return 2
    logs = run("logcat", "-d", "-v", "epoch", "--pid=" + pid)
    if logs.returncode:
        print("App log read failed.", file=sys.stderr)
        return 1
    # Never clear global logs, print identifying/config records, or save media.
    now_clock = run("shell", "date", "+%s")
    now = device_time(now_clock.stdout) if now_clock.returncode == 0 else None
    code, messages = assess_logs(logs.stdout, now, since, args.expect_scene)
    for message in messages:
        print(message, file=sys.stderr if code == 1 else sys.stdout)
    return code


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.TimeoutExpired:
        print("USB command timed out; smoke evidence is incomplete.", file=sys.stderr)
        sys.exit(1)
