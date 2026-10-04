#!/usr/bin/env python3
"""Pre-render Jarvis's pre-surgery briefing lines into the Quest player (Resources/BriefingVoice).

Same voice, model and request as the coach's reflex clips (services/preop/src/reflex.ts), so the bundled
clip matches what GET /jarvis/reflex/:sid/brief.<stepId> would serve. The briefing runs before the coach
session exists, so the headset plays these bundled clips and needs no network.

Usage: python3 scripts/quest/render_briefing_voice.py [--env services/preop/.env]
Reads ELEVENLABS_API_KEY and JARVIS_VOICE_ID from the env file or the environment; never prints them.
"""
import argparse
import json
import os
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BRIEFING = REPO / "apps/quest/Assets/Scalpal/Briefing/Resources"
MODEL = "eleven_flash_v2_5"  # ReflexAudio default


def read_env(path: Path) -> dict[str, str]:
    values = dict(os.environ)
    if path.is_file():
        for line in path.read_text().splitlines():
            if "=" in line and not line.lstrip().startswith("#"):
                key, value = line.split("=", 1)
                values.setdefault(key.strip(), value.strip().strip('"').strip("'"))
    return values


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--env", type=Path, default=REPO / "services/preop/.env")
    args = parser.parse_args()
    env = read_env(args.env)
    key, voice = env.get("ELEVENLABS_API_KEY", ""), env.get("JARVIS_VOICE_ID", "")
    if not key or not voice:
        raise SystemExit("ELEVENLABS_API_KEY and JARVIS_VOICE_ID are required")
    steps = json.loads((BRIEFING / "briefing_steps.json").read_text())["steps"]
    out = BRIEFING / "BriefingVoice"
    out.mkdir(exist_ok=True)
    clips = []
    for step in steps:
        request = urllib.request.Request(
            f"https://api.elevenlabs.io/v1/text-to-speech/{voice}?output_format=mp3_44100_128",
            data=json.dumps({"text": step["line"], "model_id": MODEL}).encode(),
            headers={"xi-api-key": key, "Content-Type": "application/json", "Accept": "audio/mpeg"},
        )
        with urllib.request.urlopen(request, timeout=60) as response:
            (out / f"{step['id']}.mp3").write_bytes(response.read())
        clips.append({"stepId": step["id"], "text": step["line"], "resourcePath": f"BriefingVoice/{step['id']}"})
        print("rendered", step["id"])
    (out / "manifest.json").write_text(json.dumps({"voiceModel": MODEL, "clips": clips}, indent=2) + "\n")


if __name__ == "__main__":
    main()
