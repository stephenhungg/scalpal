#!/usr/bin/env python3
"""Run actual CoachRelay + CaseRunner against controlled transport and isolated synthetic Hono.

Uses the installed Unity Mono compiler; never starts Unity, XR or a voice provider.
"""
import os
from pathlib import Path
import subprocess
import selectors
import shutil
import tempfile

repo = Path(__file__).resolve().parents[3]
unity = Path(os.environ.get("SCALPAL_UNITY_CONTENTS", "/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents"))
mono = unity / "MonoBleedingEdge/bin/mono"
framework = unity / "MonoBleedingEdge/lib/mono/4.5"
sources = [repo / "apps/quest/Assets/Scalpal/Exercises" / item for item in (
    "Coach/CoachRelay.cs", "Engine/CaseRunner.cs", "Data/ScalpalCaseData.cs")]
helper = Path(__file__).parent
with tempfile.TemporaryDirectory(prefix="scalpal-native-coach-check-") as temporary:
    binary = Path(temporary) / "NativeCoachCheck.exe"
    subprocess.run([str(mono), str(framework / "csc.exe"), "-nologo", "-warnaserror", "-nowarn:0649",
                    "-out:" + str(binary), "-r:" + str(framework / "System.Web.Extensions.dll"),
                    *map(str, sources), str(helper / "Stubs.cs"), str(helper / "Program.cs")], check=True, cwd=repo)
    service = repo / "services/preop"
    node = shutil.which("node")
    if not node:
        raise RuntimeError("Node is required for the isolated actual coach route fixture")
    server = subprocess.Popen([node, "--import", str(service / "node_modules/tsx/dist/loader.mjs"),
                               str(helper / "server.ts")], cwd=service, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        with selectors.DefaultSelector() as readiness:
            readiness.register(server.stdout, selectors.EVENT_READ)
            if not readiness.select(timeout=15):
                raise RuntimeError("Isolated coach fixture did not become ready")
            line = server.stdout.readline().strip()
        prefix = "SCALPAL_COACH_TEST_ENDPOINT="
        if not line.startswith(prefix):
            raise RuntimeError("Isolated coach fixture failed before readiness: " + server.stderr.read())
        environment = os.environ.copy()
        environment["SCALPAL_COACH_TEST_ENDPOINT"] = line[len(prefix):]
        subprocess.run([str(mono), str(binary)], check=True, cwd=repo, env=environment, timeout=75)
    finally:
        server.terminate()
        try:
            server.wait(timeout=5)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=5)
