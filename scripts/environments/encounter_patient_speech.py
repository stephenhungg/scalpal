"""Build bounded offline speech from the two current authored demo encounters.

Requires installed macOS say voices Samantha/Daniel, afconvert, and Bun.
Never plays speakers, reads charts, generates findings, or calls a provider.
"""
import argparse
import array
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import tempfile
import wave

PATIENTS = {
    "patient-demo-multi-source": {"folder": "Priya", "voice": "Samantha", "locale": "en_US", "rate": 170},
    "patient-demo-sparse": {"folder": "Jonah", "voice": "Daniel", "locale": "en_GB", "rate": 160},
}
UNKNOWN = "I don't know or don't remember."


def checked(command):
    return subprocess.run(command, check=True, text=True, capture_output=True).stdout


def inspect_wave(path):
    with wave.open(str(path), "rb") as audio:
        channels, width, rate, frames = audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getnframes()
        samples = array.array("h", audio.readframes(frames))
    assert (channels, width, rate) == (1, 2, 24000), (path, channels, width, rate)
    assert 0.1 < frames / rate < 90, (path, frames)
    peak = max(abs(v) for v in samples)
    rms = math.sqrt(sum(v * v for v in samples) / len(samples))
    assert peak > 100 and rms > 20, (path, peak, rms)
    return {"durationSeconds": round(frames / rate, 4), "sampleRateHz": rate, "channels": channels,
        "bitsPerSample": width * 8, "peakPcm": peak, "rmsPcm": round(rms, 2),
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def catalog_data(root, scratch, bun):
    catalog = root / "services/preop/src/catalog/encounters.ts"
    extractor = scratch / "extract.mjs"
    extractor.write_text("""const { ENCOUNTERS, HISTORY_TOPICS } = await import(process.argv[2]);
const selected = new Set(['patient-demo-multi-source', 'patient-demo-sparse']);
console.log(JSON.stringify({historyTopics:HISTORY_TOPICS, encounters:ENCOUNTERS
  .filter(e => selected.has(e.planSubject)).map(e => ({patientId:e.planSubject,
  speaker:e.persona.speaker, voiceKey:e.persona.voiceKey, greeting:e.persona.opener, history:e.history}))}));
""")
    data = json.loads(checked([bun, "run", str(extractor), str(catalog)]))
    assert {e["patientId"] for e in data["encounters"]} == set(PATIENTS)
    assert all(e["speaker"] == "patient" for e in data["encounters"])
    return catalog, data


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    root = args.root.resolve()
    resources = root / "apps/quest/Assets/Scalpal/EncounterOffice/Resources/EncounterSpeech"
    manifest_path = resources / "manifest.json"
    bun = shutil.which("bun")
    if not bun:
        raise SystemExit("Bun is required to read the actual TypeScript catalog.")
    scratch_base = root.parent / "encounter-speech-work"
    scratch_base.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="generate-", dir=scratch_base) as temporary:
        scratch = Path(temporary)
        catalog, data = catalog_data(root, scratch, bun)
        expected = []
        for encounter in data["encounters"]:
            patient = encounter["patientId"]
            settings = PATIENTS[patient]
            folder = settings["folder"]
            expected.append({"patientId": patient, "tool": "greeting", "argument": "",
                "exactDisplayText": encounter["greeting"], "spokenText": encounter["greeting"],
                "resourcePath": f"EncounterSpeech/{folder}/greeting"})
            for topic in data["historyTopics"]:
                # Missing topics only map to the route's exact unknown response.
                # Any chart-derived text or computed not-applicable response fails
                # exact lookup and therefore receives no clip from this corpus.
                text = encounter["history"].get(topic, UNKNOWN)
                suffix = "answer_" + topic if topic in encounter["history"] else "unknown"
                expected.append({"patientId": patient, "tool": "answer", "argument": topic,
                    "exactDisplayText": text, "spokenText": text,
                    "resourcePath": f"EncounterSpeech/{folder}/{suffix}"})
        if args.verify_only:
            manifest = json.loads(manifest_path.read_text())
            assert manifest["sourceCatalogSha256"] == hashlib.sha256(catalog.read_bytes()).hexdigest(), "Authored catalog changed; rebuild speech."
            assert len(manifest["entries"]) == len(expected)
            verified = {}
            for actual, identity in zip(manifest["entries"], expected):
                assert all(actual[key] == value for key, value in identity.items()), (actual, identity)
                clip = root / "apps/quest/Assets/Scalpal/EncounterOffice/Resources" / (actual["resourcePath"] + ".wav")
                stats = inspect_wave(clip)
                assert actual["sha256"] == stats["sha256"], clip
                verified[actual["resourcePath"]] = stats
            print("SCALPAL_PATIENT_SPEECH_VERIFIED", json.dumps({"entries": len(expected), "clips": len(verified),
                "durationSeconds": round(sum(v["durationSeconds"] for v in verified.values()), 2),
                "format": "PCM16 mono 24000Hz", "source": "actual authored greetings/history only", "audioPlayback": "not run"}))
            return
        say, convert = shutil.which("say"), shutil.which("afconvert")
        if not say or not convert:
            raise SystemExit("macOS say and afconvert are required; no substitute engine is installed automatically.")
        voices = checked([say, "-v", "?"])
        for settings in PATIENTS.values():
            assert any(line.split()[0] == settings["voice"] for line in voices.splitlines()), settings["voice"]
        resources.mkdir(parents=True, exist_ok=True)
        generated = {}
        entries = []
        for identity in expected:
            resource_path = identity["resourcePath"]
            if resource_path not in generated:
                settings = PATIENTS[identity["patientId"]]
                text_file, aiff = scratch / "text.txt", scratch / "speech.aiff"
                text_file.write_text(identity["spokenText"], encoding="utf-8")
                destination = root / "apps/quest/Assets/Scalpal/EncounterOffice/Resources" / (resource_path + ".wav")
                destination.parent.mkdir(parents=True, exist_ok=True)
                checked([say, "-v", settings["voice"], "-r", str(settings["rate"]), "-f", str(text_file), "-o", str(aiff)])
                checked([convert, "-f", "WAVE", "-d", "LEI16@24000", "-c", "1", str(aiff), str(destination)])
                generated[resource_path] = inspect_wave(destination)
            entries.append({**identity, **generated[resource_path]})
        manifest = {"schemaVersion": 1, "speakerRole": "patient", "sourceCatalog": str(catalog.relative_to(root)),
            "sourceCatalogSha256": hashlib.sha256(catalog.read_bytes()).hexdigest(),
            "sourceGitCommit": checked(["git", "-C", str(root), "rev-parse", "HEAD"]).strip(),
            "synthesizer": "macOS /usr/bin/say with installed system voices; /usr/bin/afconvert PCM conversion",
            "voices": [{"patientId": patient, "voiceIdentifier": value["voice"], "locale": value["locale"],
                "wordsPerMinute": value["rate"]} for patient, value in PATIENTS.items()],
            "entries": entries,
            "limitations": ["Locally synthesized speech; no live provider call, voice clone, or redistributed voice model.",
                "Exact-match authored greeting/history only; no clinical exam/test results, assessment keys or chart-derived facts.",
                "A manifest mismatch or missing clip must leave the visual answer available and must not choose a nearby fact.",
                "Speech is not claimed to have a CC0 license; system voice terms remain applicable.",
                "Waveformat/non-silent PCM checked; speaker playback and headset audibility not exercised by generation."]}
        manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
        print("SCALPAL_PATIENT_SPEECH_GENERATED", json.dumps({"entries": len(entries), "clips": len(generated),
            "durationSeconds": round(sum(v["durationSeconds"] for v in generated.values()), 2),
            "bytes": sum((root / "apps/quest/Assets/Scalpal/EncounterOffice/Resources" / (key + ".wav")).stat().st_size for key in generated),
            "format": "PCM16 mono 24000Hz", "audioPlayback": "not run"}))


if __name__ == "__main__":
    main()
