# Native Diagnosis Office

The full-VR botanical clinic is the diagnosis stage of the main experience flow, using Matthew’s patient-interview and attending assessment engine. Explore selection and the same-attempt OR handoff are implemented and have a local Unity Play Mode integration gate. It preserves the surgery scene, tissue mechanics, registration and scored instrument path. Its dedicated development Android package can coexist with the surgery player.

## Source and Ownership

Office runtime, presentation and tests live under `apps/quest/Assets/Scalpal/EncounterOffice/`. This lane also owns `QuestJarvisVoice` and the small Unity handoff integration; Claude Code owns the encounter/coach service, and the main Codex lane owns native tissue and session lifecycle. Work goes directly to main in small commits: fetch and merge/rebase `origin/main` before each chunk and push; never force-push main. Former draft PRs are historical.

## Main-Flow Integration

Explore supplies the canonical FinchNode subject ID and service endpoint. The office validates the matching ready/review synthetic case, confirms a new shared attempt, then creates one authoritative encounter. Patient selection, assessment, score and surgery use that same patient/encounter/attempt. The theatre flow binds a deep snapshot of the office result and retains the original assessment, complete scorecard, structured carryover, authored procedure and service endpoint. The OR rechecks the live case, encounter, score and exact shared encounter row before coach creation. It sends `patientId` and `encounterId` to `/coach/sessions` and does not start another attempt.

Stephen's confirmed routing policy is **the authored procedure after Jarvis feedback**. A wrong learner proposal remains in the immutable assessment and scorecard. The theatre flow displays the service's score feedback; a wrong proposal goes through the Jarvis challenge and consequence screen before virtual OR entry. Its Time-Out requires patient, procedure, site, risks, antibiotic and imaging acknowledgements before practice. The office and its OR handoff are full VR; the later docs' AR/VR-choice recommendation conflicts with the explicit full-VR-only instruction and does not override this route.

The fixture has 12 scenarios and 10 patient subjects. Eight subjects have available authored encounters. The rate-limited and consent-revoked subjects stay unavailable; `connect-cancelled` and `connect-failed` have no subject. The grid preserves all 12 scenarios without fabricating encounters. The newly merged theatre currently permits only `lap_appendectomy`; other authored procedures show Surgery coming soon rather than launch unsupported content. Live explore eligibility also requires the service's `encounterAvailable` flag; cached charts alone cannot authorize a live interview.

The two imported adult humans are generic presentation templates. Selection uses explicit encounter patient age/sex, or explicit parent speaker age/sex for a parent encounter; it never derives appearance from a voice key. Unknown demographics or a child speaker hide the adult templates with an availability label. The service still needs to expose `patientAge`, `patientSex`, `speakerAge` and `speakerSex` in encounter state; these fields are absent in the inspected producer. No child model is bundled.

### UX Alignment

Follow [experience UX](experience-ux.md) for future office UI. The current component is a checkpoint, not compliance with the complete UX target. Source review identifies these gaps:

| UX requirement | Current office | Next work |
| --- | --- | --- |
| Hold-to-talk by default | Grip holds unmute speech; release sends silent PCM, with first-use controller hint, supported-device haptic requests and optional open mic. Voice/Stop controls remain | Measure provider latency/interruption and physical controller behavior; local playback interruption does not guarantee server generation cancellation |
| Patient knows only patient facts | Patient prompt excludes the answer key; history tools supply facts, exams supply reactions and orders supply no results | Diagnosis/synonym output filtering and regeneration are absent; do not claim prompt instructions guarantee spoken leak prevention |
| Presentation and dialogue | Patient disconnects before attending connects; last response appears in a paged panel | Distinct Jarvis avatar/position, speaker-colored two-line captions and full clipboard transcript with learner partials |
| Wrong-diagnosis challenge and revision | `record_assessment` immediately scores and freezes the encounter | A logged challenge/revision stage requires an explicit service transition; preserve initial and revised assessment rather than overwrite scored evidence. Stephen confirmed authored surgery after feedback; initial proposal and score are preserved |
| Pause, recenter and comfort | Voice and pending connection attempts stop on suspension; explicit user input resumes voice; rig aligns once | Shared shell/recenter and suspension state are implemented; physical comfort remains unverified. Inter/glass and mesh-bound fitting exist, but minimum angular text size, contrast and physical placement have not been verified; fitting can shrink text |
| Short exam/order lists and teaching feedback | Four choices per page; orders return immediately; score uses critical/expected items | Case-specific short lists, authored order tiers/delays, soft clock, logged hint tiers and office contribution to the separate reasoning recap |

Explore-selected subjects, shell fades and same-attempt OR routing are integrated. The phase stepper, judge fast path and complete OR → replay → recap route remain separate flow work. Existing deterministic findings/scoring, single active voice role and gathered-fact attending summary are compatible with the spec. No new provider or physical usability evidence was produced by this source review.

Office art lives in `assets/environments/doctor-office/`, with native exports under `apps/quest/Assets/Scalpal/EncounterOffice/Art/`. Runtime, editor tools, materials, fonts and the dedicated scene live under `EncounterOffice/`. The built-in renderer and existing Android OpenXR configuration are reused. The botanical direction follows the requested flowery MHacks theme; [MHacks 2026 describes its theme as Digital Garden](https://www.mhacks.org/). Original flowers, pastel colors and decorative arrangements interpret that direction without claiming official branding.

## Art and Licensing

The room, flowers and furnishings are original Blender geometry. The two patient models replace the initial primitive characters with MakeHuman core CC0 data, authored using MPFB2 2.0.17 at `afb9f530a7c2741dedb8df0ebae2e0b183caec21`. The addon code is GPLv3; the bundled/exported core character data is CC0. The selected official [MakeHuman system asset pack](https://static.makehumancommunity.org/assets/assetpacks/makehuman_system_assets.html) supplies skin, hair, clothes, shoes, eyes and brows. Asset-specific source, legal text and hashes live beside the editable source under `assets/environments/doctor-office/`; the addon itself is not bundled into the player.

`doctor-office.blend` contains editable room and seated characters with packed images. The room contains 42,600 triangles in 17 render meshes; Priya 27,180 and Jonah 36,906 triangles, each in six skinned meshes. Runtime diffuse maps are capped at 1,024 pixels. Only one patient renders at a time. These are measured asset budgets rather than headset frame-time measurements.

Independent FBX re-import checks passed mesh counts, material assignments, node identities and actual weighted head/jaw deformations. Five-degree head rotations moved sampled skin by 12.5/13.6 mm, and jaw rotations by 7.7/9.1 mm for Priya/Jonah. The patient now breathes through the weighted upper-torso bone, makes subtle head/listening motions and opens the jaw from actual consumed speech PCM. Silence, disconnected playback, attending speech, suspension and case changes close the mouth and restore the relevant pose. This is amplitude-driven speech animation, without phoneme or clinically meaningful expression claims. Their appearances are artistic choices for fictional cases, not likenesses of real patients. See the [art README](../assets/environments/doctor-office/README.md) for editable sources and rebuild commands.

## Authoritative Encounter Route

`POST /encounters` creates a fresh authored encounter for the selected available synthetic demo subject. The native office supports all eight available service cases, including parent-speaker encounters. Source and exact patient-identity guards reject real records and mismatched responses.

Patient questions, selected examination maneuvers and orders call `/encounters/:id/tools/answer`, `examine` and `order_test`. Findings and results render from returned server state. Selecting an examination is an authored simulated action; it does not measure a learner’s physical examination technique. Unknown maneuvers remain unavailable instead of claiming a normal finding.

`POST /encounters/:id/attending` changes the role to Jarvis. `get_encounter_summary` exposes facts the learner gathered. `record_assessment` accepts the learner’s diagnosis, differential, procedure and urgency; `/encounters/:id/score` exposes the deterministic attending scorecard after assessment. Interview tools cannot mutate an attending/scored encounter, and scored evidence remains fixed. The attending prompt excludes uncollected answer-key facts. Clinical content is an authored teaching exercise, not a validated medical assessment.

There is one authoritative server encounter engine. Unity presents the encounter and forwards actions. The existing `RealtimeBridge` mirrors actions/results into its explicitly joined shared session; the native office does not create a second scorer or media producer. The service’s additive `display` response supplies learner text while provider `result` retains voice instructions.

## Voice and Visual Controls

The office reuses `QuestJarvisVoice`. `ConnectEncounter` verifies exact encounter, patient and expected role/phase identity before connecting. Patient connections use `/jarvis/connection?agent=patient`, the server’s patient prompt/greeting and selected TTS voice. Attending connections use the existing Jarvis agent and attending prompt. Empty TTS overrides are omitted for attending and surgery. Encounter tools use the office’s bounded handler; the existing six surgery tools retain their coach routes.

Selecting either current adult case plays its authored greeting; selecting a history question can play an exact matching authored answer without a voice provider. The committed corpus contains 42 mono 24 kHz PCM16 clips and 44 patient/topic/text mappings, generated from the actual encounter catalog using installed macOS Samantha/Daniel synthetic voices. These recordings are separate from the CC0 human models. They contain no examination findings, order results or answer-key diagnoses. Lookup requires exact returned display text and patient identity; chart-derived or changed answers stay visual unless separately authored. The encounter service is still required to create a case and return authoritative answers. Source hashes, voice provenance and rebuild instructions are in `Resources/EncounterSpeech/README.md`; `python3 scripts/environments/encounter_patient_speech.py --root . --verify-only` verifies corpus/catalog agreement.

Hold either tracked controller grip to interrupt local playback and talk; release to mute captured speech and hear the reply. Live transport continues sending zero PCM while muted so the provider receives an audio clock and silence. Samples captured before the hold are discarded. Normal playback/pending output gates microphone PCM to silence; hold-to-talk explicitly barges in. Monotonic interruption event IDs discard late audio, and local interruption blocks stale/idless packets until a server interruption or completed user turn. This is half-duplex gating, not hardware echo cancellation. The controller hint appears before first use, and press/release request supported haptics. Editor Space provides the same hold/release action. Open mic is an explicit optional mode. Live free-form patient/Jarvis speech requires configured provider credentials on the service; the current local service reports `jarvis_unconfigured`. No live conversation or microphone was exercised for this checkpoint.

Role/case changes disconnect the previous conversation and invalidate pending responses. Expired or abandoned queued tools cannot mutate another encounter. First-use microphone permission survives the operating-system permission-dialog pause. Later suspension cancels active and pending conversations; focus restoration does not restart them. A completed assessment leaves the attending conversation connected long enough to speak feedback. Live provider behavior remains a separate acceptance check.

The glass panels expose patient selection, paged history/examination/test choices, paged findings/responses, an editable assessment, a controller-ray keyboard and score feedback. They use rounded tinted glass surfaces, subtle borders and unmodified Inter 4.1 static fonts ([upstream release](https://github.com/rsms/inter/releases/tag/v4.1), SIL Open Font License 1.1). The adjacent `Fonts/LICENSE.txt` and `Fonts/README.md` record license and hashes. Actual text-mesh bounds are fitted to each region; world-space text uses depth testing. Glass is a lightweight transparent treatment rather than a full-scene blur pass.

## Prepare, Preview and Build

Open `apps/quest` in Unity **6000.0.66f2**. Select **Scalpal → Encounter Office → Prepare Diagnosis Office**, then open `Assets/Scalpal/EncounterOffice/Scenes/DiagnosisOffice.unity`. Preparation maps exported materials and textures, creates dedicated prefabs, verifies imported face direction and preserves the default surgery build scene. Native floor-space head/controller tracking and before-render pose updates reuse the project’s XR conventions; deliberate trigger activation is rearmed across invalid tracking/focus.

Repeatable checks from the repository root:

```sh
python3 scripts/quest/verify_session.py --suite encounter
python3 scripts/quest/verify_session.py --suite voice
python3 scripts/quest/verify_session.py --suite services
python3 scripts/quest/office-playmode-check/run.py
```

The service gate requires the pinned SpacetimeDB **2.10.2** CLI on `PATH` and its local server. It uses a uniquely named disposable test database. It must never reset the running demo database.

For a mono Editor preview, optionally set `SCALPAL_ENCOUNTER_PREVIEW_LAYOUT=assessment` to inspect assessment and keyboard placement without fabricated case data. Set `SCALPAL_ENCOUNTER_PREVIEW` to an absolute PNG path and execute `Scalpal.EncounterOffice.Editor.EncounterOfficeBuild.CapturePreview` with Unity batch mode and graphics enabled. A rendered preview checks composition/text; it does not establish stereo headset appearance.

For a deterministic talking-patient capture, set `SCALPAL_SPEECH_PREVIEW` to an absolute frame directory and execute `Scalpal.EncounterOffice.Editor.EncounterSpeechPreview.Capture`. It feeds the real greeting PCM through the production audio callback and animation step, rendering CPU skin snapshots in batch Edit mode. It writes 24 fps PNGs and the greeting WAV without starting a microphone, socket or audible Editor playback. Mux these files for visual/audio review; this does not establish a live player or headset session.

Accepted mono renders: [interview](../assets/environments/doctor-office/previews/unity-interview.png) and [assessment/keyboard](../assets/environments/doctor-office/previews/unity-assessment.png).

For an Android development APK, set `SCALPAL_ENCOUNTER_APK` to an absolute APK path and execute `Scalpal.EncounterOffice.Editor.EncounterOfficeBuild.Build`. The build verifies the scene, temporarily selects `com.scalpal.encounteroffice`, and restores the project’s product/package/version/HTTP settings and XR preload assets afterward. It builds DiagnosisOffice and NativeSession as a component player. It does not establish the complete Launch → OR → robot replay → recap experience. To configure its private development endpoint after installing:

```sh
python3 scripts/quest/configure_encounter.py --service-url http://127.0.0.1:8787
```

The helper targets only the office package, creates USB reverse forwarding for localhost, writes private `files/session-config.json`, and starts neither voice nor capture. Restart the office to apply it. A reachable LAN root URL is also supported. The runtime accepts `encounterBaseUrl` or the existing `coachBaseUrl` private configuration key. Provider credentials remain on the service.

## Verification and Acceptance

Previously completed backend gates on consolidated main `3203b29`: 169 preop tests, two provider-live tests skipped; type/catalog checks; companion type/build; 23 gateway integration tests; 19 coach HTTP checks; 156 production relay checks; 10 actual SDK subscription checks; 30 loopback HTTP → production coach bridge → SpacetimeDB encounter checks for both fictional adults. Native voice verification passed 50 actual tool/HTTP checks plus PCM/protocol/encounter identity checks; no WebSocket, microphone or provider was exercised.

On the original stable tissue checkpoint, the existing surgery Unity gate passed bridge 33, instrument 160, input 24, body-registration 76, tissue 5,739, appendectomy 75, attempt 72 and coach-binding 68 assertions. Tissue counts include per-vertex preservation checks. The registration service passed 33 tests. These checks preserve existing boundaries; none establishes a physical wearer playthrough. Consolidated main's later physics has separate verification recorded in the surgical implementation documents; the office does not alter it.

The speech/animation native office gate passed 371 assertions with actual native coroutines and isolated HTTP. Coverage includes both donor skinning/texture bindings, real weighted pose deformation, actual PCM callback/envelope/jaw behavior, silent gaps/stale audio/role/suspension/case cleanup, exact authored recording lookup and mismatch rejection, hold/open-mic mode transitions, world-space text bounds, keyboard/paging, grounded findings, assessment/score, identity mismatches, FIFO voice-tool ordering, foreground keyboard clearance and stale-connection abandonment. Native voice tests also passed PCM/protocol/zero-PCM mute behavior and 50 actual tool/HTTP checks. Provider and headset behavior were not exercised. Mono Editor interview and assessment/keyboard renders passed visual review after correcting glass/text ordering, panel size, typography and actual patient/desk occlusion. No case results were fabricated for the assessment layout preview.

The speech/animation Android development build from `ca735bc` succeeded and repeated all 371 office assertions. `aapt` verified package `com.scalpal.encounteroffice`, version `0.1.0-office` / code 1 and `arm64-v8a`; `apksigner` verified its APK Signature Scheme v2 signature. The delivered APK is 82,567,868 bytes (78.74 MiB), SHA-256 `5fe7f24140ddb8c160ead0285043817da19e08a05bd2c4d73b173059d49c0e45`. This is the file size, distinct from Unity’s larger build-report total. A 6.08-second mono Editor talking preview rendered the actual greeting PCM and production skinned poses with H.264 video and AAC audio. It shows audible authored speech and amplitude-driven mouth motion; it is not a live provider recording. The incremental build preserved the XR preload assets and restored product/package/version settings; remaining SDK serialization whitespace was discarded. No global project settings are part of the office change.

Physical stereo appearance, panel readability/comfort, controller aiming, frame time, microphone/speaker behavior and live spoken patient/attending conversation remain wearer/provider acceptance checkpoints. The APK has not been installed or launched on a headset. No participant capture is started by this experience.

### Audit Integration Verification

The real office-to-OR Play Mode gate initially passed 36 assertions and was committed in `e5ccb10`. After merging the canonical theatre flow in `3c8d996`, the adapted gate passed 58 assertions: actual office startup, fresh throwaway shared database, patient selection, encounter creation, wrong assessment, attending feedback/challenge/consequence, VR-only theatre entry, all six Time-Out acknowledgements, real preop-check, scene transition, exact attempt reuse, native coach creation and patient/score/proposal carryover. XR poses are synthetic; provider fetch is disabled. This validates the office → OR segment, not the complete demo or physical headset behavior.

The full native Unity suite passed after the inactive boundary fixture adopted its injected case's selected identity. The canonical Handoff gate passed 215 assertions, plus 35 shell integration, 36 office binding and 33 recovery assertions. It includes 65 bridge checks, 72 session boundary assertions and 67 coach binding assertions. The service-capability explore gate passed 181 component/HTTP assertions. The isolated shell Play Mode gate passed 17 assertions and explicitly bypasses shared pairing; it covers HTTP selection, fades and pause/back lifecycle separately from the real shared-attempt gate. Voice validation passed 105 actual Unity initiation/tool checks plus PCM/protocol checks. Office validation passed 425 assertions and route validation 52. Tests exercise serialized connection-initiation messages, omitted empty TTS overrides, focus cancellation, output microphone gating and stale audio rejection without a live provider or microphone.

The earlier `ca735bc` APK and talking preview are historical component deliverables; they do not contain the later audit/handoff changes. No current audit APK was installed or physically tested. Outstanding producer dependencies are explicit encounter demographics and strict coach acknowledgement/rejection of invalid or mismatched encounter carryover; those remain in Claude Code's service lane.
