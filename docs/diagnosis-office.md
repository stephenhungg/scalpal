# Native Diagnosis Office

The separate full-VR botanical clinic uses Matthew’s patient-interview and attending assessment engine. It preserves the surgery scene, tissue mechanics, registration and scored instrument path. Its dedicated Android package can coexist with the surgery player.

## Source and Ownership

The isolated branch starts at `0145544` (the stable abdominal tissue checkpoint, based on main `fc69836`). It merges Matthew’s encounter/browser/realtime producer through `dda34d9`: interview `1ed5f76`, patient voice tools `94b399e`, clean summary `00926a9`, browser flow `988d825`, realtime producer `f61b1ce`, three-identity helper `ff93cac`, exact coach membership binding `dda34d9`. Remote audit inspected Matthew `e409c22` (browser camera additions without affected encounter/realtime contract changes), Nathan `0d28d43` (documentation-only versus merged `aa182cd`) and unchanged Silas `a901a50`. While the office implementation was running, the project consolidated these team branches and tissue work into main `3203b29`, closed the original stacked PR and deleted its base branch. The office branch subsequently merged that consolidated main, preserving the latest experience flow and surgical work. Its replacement review targets main; the dedicated office remains a component to connect to that broader flow.

Office art lives in `assets/environments/doctor-office/`, with native exports under `apps/quest/Assets/Scalpal/EncounterOffice/Art/`. Runtime, editor tools, materials, fonts and the dedicated scene live under `EncounterOffice/`. The built-in renderer and existing Android OpenXR configuration are reused. The botanical direction follows the requested flowery MHacks theme; [MHacks 2026 describes its theme as Digital Garden](https://www.mhacks.org/). Original flowers, pastel colors and decorative arrangements interpret that direction without claiming official branding.

## Art and Licensing

The room, flowers and furnishings are original Blender geometry. The two patient models replace the initial primitive characters with MakeHuman core CC0 data, authored using MPFB2 2.0.17 at `afb9f530a7c2741dedb8df0ebae2e0b183caec21`. The addon code is GPLv3; the bundled/exported core character data is CC0. The selected official [MakeHuman system asset pack](https://static.makehumancommunity.org/assets/assetpacks/makehuman_system_assets.html) supplies skin, hair, clothes, shoes, eyes and brows. Asset-specific source, legal text and hashes live beside the editable source under `assets/environments/doctor-office/`; the addon itself is not bundled into the player.

`doctor-office.blend` contains editable room and seated characters with packed images. The room contains 42,600 triangles in 17 render meshes; Priya 27,180 and Jonah 36,906 triangles, each in six skinned meshes. Runtime diffuse maps are capped at 1,024 pixels. Only one patient renders at a time. These are measured asset budgets rather than headset frame-time measurements.

Independent FBX re-import checks passed mesh counts, material assignments, node identities and actual weighted head/jaw deformations. Five-degree head rotations moved sampled skin by 12.5/13.6 mm, and jaw rotations by 7.7/9.1 mm for Priya/Jonah. Motion remains decorative listening/speaking feedback, without phoneme or clinically meaningful expression claims. Their appearances are artistic choices for fictional cases, not likenesses of real patients. See the [art README](../assets/environments/doctor-office/README.md) for editable sources and rebuild commands.

## Authoritative Encounter Route

`POST /encounters` creates a fresh authored encounter. The office supports Priya Ramaswamy (female, 40, `patient-demo-multi-source`) and Jonah Okoye (male, 30, `patient-demo-sparse`). These are fictional synthetic demo cases. Source and demographic guards reject real records and mismatched case demographics.

Patient questions, selected examination maneuvers and orders call `/encounters/:id/tools/answer`, `examine` and `order_test`. Findings and results render from returned server state. Selecting an examination is an authored simulated action; it does not measure a learner’s physical examination technique. Unknown maneuvers remain unavailable instead of claiming a normal finding.

`POST /encounters/:id/attending` changes the role to Jarvis. `get_encounter_summary` exposes facts the learner gathered. `record_assessment` accepts the learner’s diagnosis, differential, procedure and urgency; `/encounters/:id/score` exposes the deterministic attending scorecard after assessment. Interview tools cannot mutate an attending/scored encounter, and scored evidence remains fixed. The attending prompt excludes uncollected answer-key facts. Clinical content is an authored teaching exercise, not a validated medical assessment.

There is one authoritative server encounter engine. Unity presents the encounter and forwards actions. The existing optional `RealtimeBridge` mirrors actions/results into its explicitly joined shared session; the native office does not create a second scorer or media producer. The service’s additive `display` response supplies learner text while provider `result` retains voice instructions.

## Voice and Visual Controls

The office reuses `QuestJarvisVoice`. `ConnectEncounter` verifies exact encounter, patient and expected role/phase identity before connecting. Patient connections use `/jarvis/connection?agent=patient`, the server’s patient prompt/greeting and selected TTS voice. Attending connections use the existing Jarvis agent and attending prompt. Empty TTS overrides are omitted for attending and surgery. Encounter tools use the office’s bounded handler; the existing six surgery tools retain their coach routes.

Role/case changes disconnect the previous conversation and invalidate pending responses. Expired or abandoned queued tools cannot mutate another encounter. First-use microphone permission survives the operating-system permission-dialog pause. Later suspension disconnects an active conversation. A completed assessment leaves the attending conversation connected long enough to speak feedback. Live provider behavior remains a separate acceptance check.

The glass panels expose patient selection, paged history/examination/test choices, paged findings/responses, an editable assessment, a controller-ray keyboard and score feedback. They use rounded tinted glass surfaces, subtle borders and unmodified Inter 4.1 static fonts ([upstream release](https://github.com/rsms/inter/releases/tag/v4.1), SIL Open Font License 1.1). The adjacent `Fonts/LICENSE.txt` and `Fonts/README.md` record license and hashes. Actual text-mesh bounds are fitted to each region; world-space text uses depth testing. Glass is a lightweight transparent treatment rather than a full-scene blur pass.

## Prepare, Preview and Build

Open `apps/quest` in Unity **6000.0.66f2**. Select **Scalpal → Encounter Office → Prepare Diagnosis Office**, then open `Assets/Scalpal/EncounterOffice/Scenes/DiagnosisOffice.unity`. Preparation maps exported materials and textures, creates dedicated prefabs, verifies imported face direction and preserves the default surgery build scene. Native floor-space head/controller tracking and before-render pose updates reuse the project’s XR conventions; deliberate trigger activation is rearmed across invalid tracking/focus.

Repeatable checks from the repository root:

```sh
python3 scripts/quest/verify_session.py --suite encounter
python3 scripts/quest/verify_session.py --suite voice
python3 scripts/quest/verify_session.py --suite services
```

The service gate requires the pinned SpacetimeDB **2.10.2** CLI on `PATH` and its local server. It uses a uniquely named disposable test database. It must never reset the running demo database.

For a mono Editor preview, optionally set `SCALPAL_ENCOUNTER_PREVIEW_LAYOUT=assessment` to inspect assessment and keyboard placement without fabricated case data. Set `SCALPAL_ENCOUNTER_PREVIEW` to an absolute PNG path and execute `Scalpal.EncounterOffice.Editor.EncounterOfficeBuild.CapturePreview` with Unity batch mode and graphics enabled. A rendered preview checks composition/text; it does not establish stereo headset appearance.

Accepted mono renders: [interview](../assets/environments/doctor-office/previews/unity-interview.png) and [assessment/keyboard](../assets/environments/doctor-office/previews/unity-assessment.png).

For an Android development APK, set `SCALPAL_ENCOUNTER_APK` to an absolute APK path and execute `Scalpal.EncounterOffice.Editor.EncounterOfficeBuild.Build`. The build verifies the scene, temporarily selects `com.scalpal.encounteroffice`, and restores the project’s product/package/version/HTTP settings and XR preload assets afterward. It builds only the diagnosis scene. To configure its private development endpoint after installing:

```sh
python3 scripts/quest/configure_encounter.py --service-url http://127.0.0.1:8787
```

The helper targets only the office package, creates USB reverse forwarding for localhost, writes private `files/session-config.json`, and starts neither voice nor capture. Restart the office to apply it. A reachable LAN root URL is also supported. The runtime accepts `encounterBaseUrl` or the existing `coachBaseUrl` private configuration key. Provider credentials remain on the service.

## Verification and Acceptance

Completed backend gates on consolidated main: 169 preop tests, two provider-live tests skipped; type/catalog checks; companion type/build; 23 gateway integration tests; 19 coach HTTP checks; 156 production relay checks; 10 actual SDK subscription checks; 30 loopback HTTP → production coach bridge → SpacetimeDB encounter checks for both fictional adults. Native voice verification passed 50 actual tool/HTTP checks plus PCM/protocol/encounter identity checks; no WebSocket, microphone or provider was exercised.

On the original stable tissue checkpoint, the existing surgery Unity gate passed bridge 33, instrument 160, input 24, body-registration 76, tissue 5,739, appendectomy 75, attempt 72 and coach-binding 68 assertions. Tissue counts include per-vertex preservation checks. The registration service passed 33 tests. These checks preserve existing boundaries; none establishes a physical wearer playthrough. Consolidated main's later physics has separate verification recorded in the surgical implementation documents; the office does not alter it.

The final native office gate passed 227 assertions with actual native coroutines and isolated HTTP: both donor skinning/texture bindings, world-space text bounds, controller keyboard and paging, grounded findings, assessment/score, role recovery, identity mismatches, FIFO voice-tool ordering, 400-character draft paging, foreground keyboard clearance and stale-connection abandonment. Provider and headset behavior were not exercised. Mono Editor interview and assessment/keyboard renders passed visual review after correcting glass/text ordering, panel size, typography and actual patient/desk occlusion. No case results were fabricated for the assessment layout preview.

The Android development build on consolidated main succeeded and repeated all 227 office assertions. `aapt` verified package `com.scalpal.encounteroffice`, version `0.1.0-office` / code 1 and `arm64-v8a`; `apksigner` verified its APK Signature Scheme v2 signature. The delivered APK is 92,617,911 bytes (88.33 MiB), SHA-256 `1037e90a98b05d1130d5c8f423d306eaebc67b5421fa000be66003edd1672bc2`. This is the file size, distinct from Unity’s larger build-report total. The incremental build preserved the XR preload assets and restored product/package/version settings; remaining SDK serialization whitespace was discarded. No global project settings are part of the office change.

Physical stereo appearance, panel readability/comfort, controller aiming, frame time, microphone/speaker behavior and live spoken patient/attending conversation remain wearer/provider acceptance checkpoints. The APK has not been installed or launched on a headset. No participant capture is started by this experience.
