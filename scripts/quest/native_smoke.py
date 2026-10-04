#!/usr/bin/env python3
"""Launch the native workbench over USB and print only app-scoped evidence."""

import argparse
import shutil
import subprocess
import sys
import time
from pathlib import Path

PACKAGE = "com.scalpal.nativeworkbench"
ACTIVITY = PACKAGE + "/com.unity3d.player.UnityPlayerGameActivity"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, help="Install this APK before launch.")
    parser.add_argument("--seconds", type=int, default=15, choices=range(1, 46), metavar="1..45")
    args = parser.parse_args()
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
            print("APK install failed:\n" + installed.stdout + installed.stderr, file=sys.stderr)
            return 1
        print("APK installed.")
    launch = run("shell", "am", "start", "-n", ACTIVITY)
    if launch.returncode or "Error:" in launch.stdout + launch.stderr:
        print("Workbench launch failed:\n" + launch.stdout + launch.stderr, file=sys.stderr)
        return 1

    # Quest can intercept launch with its controllers-required dialog; am success
    # alone does not prove an app process or XR session exists.
    deadline = time.monotonic() + args.seconds
    pid = ""
    while time.monotonic() < deadline:
        result = run("shell", "pidof", PACKAGE)
        pid = result.stdout.strip() if result.returncode == 0 else ""
        time.sleep(1)
    if not pid or not pid.isdecimal():
        activity = run("shell", "dumpsys", "activity", "activities")
        if "LaunchCheckControllerRequiredDialogActivity" in activity.stdout:
            print("Quest is blocking launch at its controllers-required dialog. Wear Quest, wake both controllers and accept the prompt.")
        else:
            print("Workbench is not running. Wear Quest, wake both controllers, accept any launch prompt, then retry.")
        return 2
    logs = run("logcat", "-d", "-v", "epoch", "--pid=" + pid)
    if logs.returncode:
        print("App log read failed.", file=sys.stderr)
        return 1
    # Never clear global device logs or save camera frames / identifying system logs.
    app_lines = [line for line in logs.stdout.splitlines() if "SCALPAL_NATIVE_" in line]
    records = [line[line.index("SCALPAL_NATIVE_"):] for line in app_lines]
    for record in records[-20:]:
        print(record)
    faults = [line for line in logs.stdout.splitlines() if "Exception:" in line or "FATAL EXCEPTION" in line or "SCALPAL_NATIVE_BINDING_ERROR" in line]
    if faults:
        print("\n".join(faults[-10:]), file=sys.stderr)
        return 1
    statuses = [record for record in records if record.startswith("SCALPAL_NATIVE_STATUS ")]
    status_lines = [line for line in app_lines if "SCALPAL_NATIVE_STATUS " in line]
    now = run("shell", "date", "+%s")
    try:
        fresh = bool(status_lines) and 0 <= float(now.stdout.strip()) - float(status_lines[-1].split()[0]) <= 5
    except ValueError:
        fresh = False
    if not fresh or not statuses or not all(token in statuses[-1] for token in ("xr=True", "head=True", "floor=True", "focus=True", "aligned=True")):
        print("App process exists, but current XR/head/floor/focus readiness is not confirmed.")
        return 2
    print("App telemetry reports valid XR/head/floor/focus. Human pickup, stereo and reset checks are still required.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
