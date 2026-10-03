#!/usr/bin/env python3
"""Compile Android branch against installed Unity assemblies; no Editor/provider/microphone starts."""
import os
from pathlib import Path
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[3]
unity = Path(os.environ.get("SCALPAL_UNITY_CONTENTS", "/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents"))
mono = unity / "MonoBleedingEdge/bin/mono"
csc = unity / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
engine = unity / "Managed/UnityEngine"
references = [engine / ("UnityEngine." + module + ".dll") for module in (
    "CoreModule", "AudioModule", "AndroidJNIModule", "UnityWebRequestModule", "JSONSerializeModule")]
references.append(unity / "MonoBleedingEdge/lib/mono/4.5/Facades/netstandard.dll")
source = repo / "apps/quest/Assets/Scalpal/Voice/Runtime/QuestJarvisVoice.cs"
with tempfile.TemporaryDirectory(prefix="scalpal-voice-check-") as temporary:
    binary = Path(temporary) / "VoiceCheck.exe"
    subprocess.run([str(mono), str(csc), "-nologo", "-define:UNITY_ANDROID", "-warnaserror", "-nowarn:0649",
                    "-out:" + str(binary), *["-r:" + str(path) for path in references], str(source),
                    str(Path(__file__).with_name("Program.cs"))], check=True, cwd=repo)
    environment = os.environ.copy()
    environment["MONO_PATH"] = str(engine)
    subprocess.run([str(mono), str(binary)], check=True, env=environment, cwd=repo)
