#!/usr/bin/env python3
"""Run actual component/session gates; optional USB evidence never substitutes for a human playthrough."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import uuid

REPO = Path(__file__).resolve().parents[2]
UNITY_DEFAULT = "/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents/MacOS/Unity"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--suite", choices=("all", "services", "unity", "voice", "encounter", "motion", "registration", "playmode"), default="all")
    parser.add_argument("--headset", action="store_true", help="Also check the installed player over USB.")
    parser.add_argument("--config", type=Path, help="Private development pairing JSON for --headset.")
    args = parser.parse_args()
    environment = os.environ.copy()
    failures = []

    def check(name, command, cwd=REPO, timeout=180):
        print(f"\nChecking: {name}", flush=True)
        try:
            result = subprocess.run(command, cwd=cwd, env=environment, timeout=timeout)
        except (subprocess.TimeoutExpired, OSError) as error:
            print("Check could not finish:", type(error).__name__)
            failures.append(name)
            return False
        if result.returncode:
            failures.append(name)
        return result.returncode == 0

    if args.suite in {"all", "services"}:
        if not shutil.which("npm") or not shutil.which("spacetime"):
            parser.error("services require Node/npm and SpacetimeDB 2.10.2 on PATH")
        try:
            with urllib.request.urlopen("http://127.0.0.1:3000/v1/ping", timeout=3) as reply:
                if reply.status != 200:
                    raise ValueError("not ready")
        except Exception:
            parser.error("start a local SpacetimeDB on port 3000 before running the service gate")
        # The upstream harness deletes its configured test database. Force a fresh local fixture,
        # independent of inherited developer settings or any participant/demo database.
        environment["TEST_SPACETIME_SERVER"] = "http://127.0.0.1:3000"
        environment["TEST_SPACETIMEDB_URI"] = "ws://127.0.0.1:3000"
        environment["TEST_SPACETIMEDB_DB"] = "scalpal-test-verify-" + uuid.uuid4().hex
        environment["SCALPAL_CHECK_DATABASE"] = environment["TEST_SPACETIMEDB_DB"]
        environment["TEST_GATEWAY_PORT"] = "8799"
        api_ready = False
        for relative, scripts in (
            ("services/preop", ("typecheck", "test", "validate")),
            ("services/api", ("typecheck", "build", "test")),
            ("apps/companion", ("typecheck", "build")),
        ):
            folder = REPO / relative
            if not (folder / "node_modules").is_dir():
                if not check(relative + " dependencies", ["npm", "ci"], folder):
                    continue
            for script in scripts:
                passed = check(relative + ": " + script, ["npm", "run", script], folder)
                if relative == "services/api" and script == "test":
                    api_ready = passed
        node = shutil.which("node")
        tsx = REPO / "services/preop/node_modules/tsx/dist/cli.mjs"
        check("isolated coach HTTP rejection/retry regressions",
              [node, str(tsx), str(REPO / "scripts/quest/session-check/coach-http.ts")])
        check("production native coach + isolated HTTP fault exchanges",
              [sys.executable, str(REPO / "scripts/quest/native-coach-check/run.py")])
        if api_ready:
            check("real subscription/reconnect/attempt boundaries on throwaway database",
                  [node, str(tsx), str(REPO / "scripts/quest/session-check/live-realtime.ts")])
            check("authoritative encounters + actual coach bridge on throwaway database",
                  [node, str(tsx), str(REPO / "scripts/quest/encounter-check/live-exchange.ts")])

    if args.suite in {"all", "encounter"}:
        service = REPO / "services/preop"
        if not (service / "node_modules").is_dir():
            check("encounter fixture dependencies", ["npm", "ci"], service)
        unity = environment.get("SCALPAL_UNITY", UNITY_DEFAULT)
        encounter_log = Path(tempfile.gettempdir()) / ("scalpal-encounter-" + uuid.uuid4().hex + ".log")
        passed = check("floral office scene + native encounter coroutines + isolated HTTP", [unity,
                       "-batchmode", "-nographics", "-projectPath", str(REPO / "apps/quest"),
                       "-buildTarget", "Android", "-executeMethod",
                       "Scalpal.EncounterOffice.Editor.EncounterOfficeBuild.Verify", "-quit", "-logFile", str(encounter_log)], timeout=300)
        output = encounter_log.read_text(errors="replace") if encounter_log.exists() else ""
        if passed and "SCALPAL_ENCOUNTER_OFFICE_VERIFY_OK" not in output:
            failures.append("encounter office verification marker absent")
        for line in output.splitlines():
            if line.startswith("SCALPAL_") or "error CS" in line:
                print(line)
        print("Encounter diagnostic log:", encounter_log)

    if args.suite in {"all", "voice"}:
        check("native voice PCM/protocol", [sys.executable, str(REPO / "scripts/quest/voice-check/run.py")])
        service = REPO / "services/preop"
        if not (service / "node_modules").is_dir():
            check("voice tool fixture dependencies", ["npm", "ci"], service)
        voice_log = Path(tempfile.gettempdir()) / ("scalpal-voice-tools-" + uuid.uuid4().hex + ".log")
        unity = environment.get("SCALPAL_UNITY", UNITY_DEFAULT)
        passed = check("actual native voice tool coroutines + isolated coach HTTP", [unity,
                       "-batchmode", "-nographics", "-projectPath", str(REPO / "apps/quest"),
                       "-buildTarget", "Android", "-executeMethod",
                       "Scalpal.Quest.Editor.NativeVoiceToolsValidation.Run", "-quit", "-logFile", str(voice_log)], timeout=180)
        output = voice_log.read_text(errors="replace") if voice_log.exists() else ""
        if passed and "SCALPAL_NATIVE_VOICE_TOOLS_VALIDATION_OK" not in output:
            failures.append("native voice tool verification marker absent")
        for line in output.splitlines():
            if line.startswith("SCALPAL_") or "error CS" in line:
                print(line)
        print("Native voice tool diagnostic log:", voice_log)

    if args.suite in {"all", "unity"}:
        check("production native bridge failure boundaries",
              [sys.executable, str(REPO / "scripts/quest/session-check/run-bridge.py")])
        unity = environment.get("SCALPAL_UNITY", UNITY_DEFAULT)
        if not Path(unity).is_file():
            parser.error("set SCALPAL_UNITY to the Unity 6000.0.66f2 executable")
        log = Path(tempfile.gettempdir()) / ("scalpal-verify-" + uuid.uuid4().hex + ".log")
        command = [unity, "-batchmode", "-nographics", "-projectPath", str(REPO / "apps/quest"),
                   "-buildTarget", "Android", "-executeMethod", "Scalpal.Quest.Editor.NativeSessionBuild.Verify",
                   "-quit", "-logFile", str(log)]
        passed = check("Editor scene/components + synthetic scoring/attempt fixtures", command, timeout=240)
        output = log.read_text(errors="replace") if log.exists() else ""
        # Require the terminal marker: an editor exit alone is not proof the method ran.
        if passed and "SCALPAL_NATIVE_SESSION_VERIFY_OK" not in output:
            failures.append("Unity verification marker absent")
        if "Leak Detected : Persistent allocates" in output:
            failures.append("Unity persistent native allocation leak")
        for line in output.splitlines():
            if line.startswith("SCALPAL_") or "error CS" in line:
                print(line)
        print("Unity diagnostic log:", log)

    if args.suite in {"all", "playmode"}:
        check("real NativeSession Play Mode lifecycle/buttons/physics + isolated backend",
              [sys.executable, str(REPO / "scripts/quest/play-mode-check/run.py")], timeout=300)

    if args.suite in {"all", "registration"}:
        if not shutil.which("uv"):
            parser.error("registration requires uv with Python 3.11 support")
        folder = REPO / "services/registration"
        if check("registration frozen dependencies", ["uv", "sync", "--frozen"], folder, timeout=180):
            check("local body-pose HTTP/inference failure boundaries", ["uv", "run", "--frozen", "pytest", "-q"], folder)

    if args.suite in {"all", "motion"}:
        if not shutil.which("uv"):
            parser.error("motion requires uv with Python 3.11 support")
        folder = REPO / "services/motion"
        if check("motion frozen dependencies", ["uv", "sync", "--frozen"], folder, timeout=600):
            check("motion reconstruction/retargeting/job/read-path regressions",
                  ["uv", "run", "--frozen", "pytest", "-q"], folder, timeout=180)

    if args.headset:
        if args.config:
            check("private USB pairing", [sys.executable, str(REPO / "scripts/quest/configure_session.py"),
                                          "--config", str(args.config.resolve())])
        check("installed headset readiness", [sys.executable, str(REPO / "scripts/quest/native_smoke.py"),
                                             "--seconds", "10"], timeout=45)

    print("\n" + json.dumps({"suite": args.suite, "passed": not failures, "failed": failures,
                              "physical_playthrough_verified": False}))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
