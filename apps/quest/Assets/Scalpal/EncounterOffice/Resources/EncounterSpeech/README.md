# Offline Authored Patient Speech

This corpus supplies audible patient responses when live provider voice is unavailable. It contains locally synthesized speech for the two current authored demo patients, not provider recordings, voice clones or redistributed speech models.

The exact source is `services/preop/src/catalog/encounters.ts` at repository snapshot `814e551`. The manifest records the actual catalog SHA-256 so authoring checks detect later changes. Only each patient's authored greeting and explicit first-person history facts are synthesized. No chart fallback, examination finding, test result, diagnosis, differential answer key or attending assessment is voiced by this corpus.

## Identity and Runtime Lookup

Load `Resources.Load<TextAsset>("EncounterSpeech/manifest")`. Its `entries` array provides:

| Field | Meaning |
| --- | --- |
| `patientId` | Exact authored demo patient ID |
| `tool` | `greeting` or `answer` |
| `argument` | Empty for greeting; exact history topic for answer |
| `exactDisplayText` | Exact service display/greeting string required for playback |
| `spokenText` | Text actually supplied to the local synthesizer; identical to display text |
| `resourcePath` | Extension-free path for `Resources.Load<AudioClip>` |
| `sha256` | Actual WAV file hash |
| `durationSeconds`, `sampleRateHz`, `channels`, `bitsPerSample` | Measured PCM properties |

A clip is eligible only when patient ID, tool, argument and returned display text all match exactly. Match the service's actual `display` or `patientFirstMessage`, not a role-prefixed UI transcript. An absent/mismatched entry leaves the visual answer available; never choose the nearest question, previous patient's response or a paraphrase.

For missing authored history topics, entries contain only the route's exact unknown display: `I don't know or don't remember.` The same unknown clip is reused for that patient's gaps. If the service instead returns a chart-derived fact or computed not-applicable response, exact matching fails and that text remains visual-only. This deliberately prevents prerecorded patient audio from asserting unrecorded chart facts.

## Generation

Installed macOS system voice `Samantha` (`en_US`, 170 words/minute) supplies Priya; `Daniel` (`en_GB`, 160 words/minute) supplies Jonah. `/usr/bin/say` writes AIFF files; `/usr/bin/afconvert` converts them into 24,000Hz, mono, signed 16-bit little-endian PCM WAV. Temporary text/AIFF files stay in the task work directory and are removed afterward. The script never plays speakers, contacts a provider or reads private clinical records.

From the repository root, with Bun and those voices already installed:

```sh
python3 scripts/environments/encounter_patient_speech.py --root .
python3 scripts/environments/encounter_patient_speech.py --root . --verify-only
```

The TypeScript catalog is imported directly through Bun. No duplicated hand-maintained fact list is introduced. Generation fails if required local tools/voices are missing; it does not install another engine automatically.

## Actual Evidence and Limits

Generation and independent verification passed for 44 exact-match entries pointing to 42 unique clips, totaling 157.67 seconds and 7,739,992 bytes. Checks cover current catalog identity/text parity, hashes, format, duration and non-silent PCM. Patient speech is intended to drive the native audio/RMS animation path; this asset generation does not verify that runtime, headset audibility, microphone input or live provider interaction.

No speaker playback was run during generation or verification. Stock macOS synthesized speech is not claimed to be CC0; applicable system voice terms remain separate from the MakeHuman art licenses. No installed voice model or synthesizer binary is copied into this repository.
