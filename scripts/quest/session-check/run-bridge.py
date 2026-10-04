#!/usr/bin/env python3
"""Compile the unchanged QuestSessionBridge and run deterministic failure-boundary checks.

The Unity Mono compiler is used, but no Unity process, SDK socket or provider is opened.
Connection lifecycle checks use a disposable synthetic token directory, never the app's token.
"""
import os
from pathlib import Path
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[3]
unity = Path(os.environ.get("SCALPAL_UNITY_CONTENTS", "/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents"))
mono = unity / "MonoBleedingEdge/bin/mono"
csc = unity / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
source = repo / "apps/quest/Assets/Scalpal/Realtime/Runtime/QuestSessionBridge.cs"
helper = Path(__file__).parent
with tempfile.TemporaryDirectory(prefix="scalpal-session-check-") as directory:
    binary = Path(directory) / "BridgeCheck.exe"
    # Unity Editor assigns the private requestSender seam through reflection. The standalone
    # compiler cannot see that assignment; retain all other warnings as errors.
    subprocess.run([str(mono), str(csc), "-nologo", "-warnaserror", "-nowarn:0649", "-out:" + str(binary),
                    str(source), str(helper / "BridgeStubs.cs"), str(helper / "BridgeCheck.cs")], check=True, cwd=repo)
    subprocess.run([str(mono), str(binary)], check=True, cwd=repo, timeout=30)
