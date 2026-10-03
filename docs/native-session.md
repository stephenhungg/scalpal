# Native Appendectomy Integration

This milestone assembles one full-VR rehearsal in `Assets/Scalpal/Quest/Scenes/NativeSession.unity`. It preserves the native tracked-head/controller rig and the user-confirmed held-tool motion fix. It connects Matthew's authored appendectomy case, selected anatomy, coach HTTP service, a native client for his existing Jarvis agent, and Nathan's actual SpacetimeDB session adapter.

The exercise is illustrative. Tool contact advances authored procedural checks; the organs do not have validated deformable-tissue physics. The static virtual patient fit is explicitly authored. This scene does not perform real-person registration, passthrough capture, video streaming, or robot replay.

## One Session

1. Load the synthetic `patient-demo-multi-source` case from Matthew's running service; reject unknown patients, procedures, nonsynthetic briefs and unavailable cases.
2. Join a genuine headset invitation and create a fresh `lap_appendectomy@0.1.0` attempt. Freeze its shared session/attempt IDs so external attempt changes cannot receive the old local runner's progress.
3. Show a rotating preview containing the nine required/context anatomy meshes. **B** opens the synthetic case review. **B** again acknowledges the brief and requests a fresh coach session for that exact case in `virtual` mode.
4. Bind one `AnatomyExerciseBinding`/`CaseRunner`. Practice remains gated until the coach acknowledges tracking and the headset has valid floor/head/focus tracking. Grip picks up tools; trigger activates them. Port placement requires actual tip overlap, the current step's instrument, and the port allowlist. **X** identifies anatomy under the actual held tip or head ray. **B** satisfies only an authored confirmation step.
5. Publish confirmed coarse phase/step/selection/registration state and actual events to SpacetimeDB. XR poses stay local. Coach scoring events retain IDs across bounded retries and use the step that validated the action. Applied scene-command acknowledgements wait for a committed snapshot.
6. After all ten authored steps, show a local recap and send the actual learning result. A successful send is distinct from the backend's committed acknowledgement. **A** abandons the previous local coach binding, restores tools and preview, and requests a new shared attempt.

**Y** explicitly enables/disables microphone voice. If pressed before practice, it opts into the next confirmed session. Native conversational procedure selection is not yet wired: this first slice exposes one appendectomy through controller confirmation. Do not describe it as the completed conversational catalog experience.

## Service Boundaries

| Component | Local development route | Native consumer |
| --- | --- | --- |
| Matthew's preop/coach | HTTP `localhost:8787` | Reviewed case, fresh explicit coach session, ordered events, highlights, context |
| Matthew's existing ElevenLabs agent | Signed WSS URL supplied by `/jarvis/connection` | `QuestJarvisVoice`: negotiated PCM microphone/speaker transport and existing bounded tools |
| Nathan's SpacetimeDB | WS `127.0.0.1:3000`, database `scalpal` | `QuestSessionBridge`: real invitation, subscriptions, reducer acknowledgements |
| Nathan's gateway | HTTP `localhost:8788` | Artifact/worker service available separately; native capture adapter still absent |
| Nathan's companion | HTTP `localhost:5173` | Joined observer reads actual native snapshots and command acknowledgements |

These are local development processes, not production deployment URLs. USB reverse routes make localhost services reachable by the headset. Development HTTP is enabled only for development players. Provider credentials remain on the preop service; no key, invitation or auth token belongs in a committed scene.

Use `scripts/quest/configure_session.py --config /absolute/private/session-config.json` after installing a development APK. Its five fields are `uri`, `database`, `joinCode`, `preferredSessionId`, and `coachBaseUrl`. The helper sends private JSON over stdin into the app's private internal files directory and establishes needed localhost USB routes. Restart the app to read it. The native player reads this configuration only in development builds. Tokens persist per database endpoint in the app directory and are never logged.

## Geometry and Input

The scene contains two instances of a nine-mesh appendectomy subset, 93,399 triangles per instance. Selection preview colliders are disabled. The practice instance preserves authored renderer/collider defaults and hides geometry until its validity gate opens. Three runtime FBXs are present; the complete source atlas remains on Matthew's anatomy branch with attribution and preparation scripts included here.

The exported anatomy is upright with a source foot origin. The illustrative fit uses X=90 degrees and uniform `1.75/1.743` scale: source +Y becomes the reclining patient's +Z. Source anterior -Z is assumed to become +Y. The source foot maps to the virtual patient's actual foot extent. Port coordinates use meters, +X patient-left, +Y anterior, +Z cranial, with an authored approximate skin umbilicus origin. These assumptions require visual headset validation and must never be reused as measured participant registration.

`NativeProcedureInput` is the sole scored contact adapter. It reuses `InstrumentTipContact`, verifies selected anatomy ownership and actual tip penetration, and deduplicates per structure/port per activation cycle. Do not install an additional case director or contact scorer. `NativeCaseSession` owns the only coach command handler; no extra `AnatomyCoachBinding` is installed in this scene.

Device event time is Unity monotonic elapsed milliseconds. Spacetime server timestamps use the server clock. These clocks are not calibrated and do not support cross-device motion alignment.

## Build and Verification

Use Unity 6000.0.66f2 with Android modules, ARM64 IL2CPP and Vulkan. `Scalpal.Quest.Editor.NativeSessionBuild.Prepare` regenerates the scene; `Validate` checks unique bindings, actual part/triangle counts, metric fit, port identities, materials and model dependencies. `Build` consumes an absolute `SCALPAL_QUEST_APK` output path and makes a development APK.

Verification distinguishes real backend exchanges, synthetic editor checks and physical headset evidence. The backend suite passed 23 real local-server tests; preop passed 130 tests (two live-provider tests skipped); native PCM/protocol checks passed against actual Unity Android assemblies. The existing instrument runtime passed 160 editor checks. The native input fixture passed 24 checks, actual scene playthrough passed 75 checks across ten steps/thirteen actions, and attempt/retry boundary fixture passed 72 checks. These editor poses/identities are synthetic and cannot establish physical usability. The final ARM64 development APK `0.2.0-session` built successfully and installed over USB. Private pairing configuration and service reverse routes were written. The launch check found no running app process; a read of the real shared session found no joined headset. Headset wake/controllers/launch confirmation is therefore the next physical checkpoint. The full-session headset result, native voice authentication/audio and actual observer state remain unverified.

## Remaining Required Interfaces

- Existing ElevenLabs agent credentials are currently absent locally; `/jarvis/connection` reports unconfigured. Spoken headset input/output remains unverified.
- Conversational selection and native visual preview actions need to be connected to the same single agent; controller selection is the bounded first integration.
- MR body registration and camera acquisition remain in the separate camera experiment, not this scene.
- Native video production/WebRTC publishing and upload are absent; the companion must not claim a live headset video feed.
- Silas's real video-to-motion processor still needs Nathan's lease/artifact worker adapter and actual permitted Quest-video validation. Backend synthetic job tests do not prove this route.

See [system integration](system-integration.md) for exact audited commits and [native workbench evidence](native-workbench.md) for the earlier physical controller result.
