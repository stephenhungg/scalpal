# Launch and Explore Shell

`Assets/Scalpal/Shell/Scenes/Launch.unity` is the first enabled build scene, followed by `DiagnosisOffice` and the preserved `NativeSession`. Launch and Explore share one full-VR floral hub. The shell does not start camera capture, clinical scoring, or a surgery attempt.

## Runtime route

`HubController` uses the existing `ScalpalPreopService` and `Scalpal.Exercises.Data` DTOs. These clients now have an active scene consumer; **keep them**. `/unity/bundle` supplies case metadata missing from `/patients` (name, demographics and authored presentation); `/patients` supplies current status/procedure/urgency. Selecting a card requests `/patients/:id/brief`. Matching synthetic demo identity and a separate explicit Begin are required. Old detail successes/failures cannot unlock a different patient. HTTP errors retain their error state; connection failures use the cached or packaged `Resources/scalpal_bundle`.

All twelve demo scenarios occupy a 4×3 grid: ten patients and two disabled connection-only scenarios. Ready sorts first. Procedure and urgency filters combine. Ready and needs-review patients allow chart selection; blocked patients do not. Retry fetches the case again, respects its retry delay and reloads the selected brief after recovery. Unknown states fail closed. Missing demographics remain unknown. One sticky `Offline data` banner labels mixed/cached results until an explicit successful refresh; offline charts can be explored, but Begin is disabled because the office encounter engine requires the service.

The service has more authored encounters than the native office can present. The shell's separate native capability gate currently permits only `patient-demo-multi-source` (Priya) and `patient-demo-sparse` (Jonah). Other selectable patients show `Interview coming soon`. This is a native capability limitation, not a fabricated service failure.

For a development player, the hub reads the existing private `session-config.json` keys `encounterBaseUrl` or `coachBaseUrl` from the same location as the office. Default port is 8787; localhost on Quest requires USB reverse forwarding. No provider credentials are stored by the shell.

## Explicit office handoff

`ShellTransition.BeginOffice(patientId, title, serviceUrl)` validates the exact supported patient ID and HTTP(S) endpoint, then stages an immutable `SelectedPatient`. It fades for 0.4 seconds, asynchronously loads `DiagnosisOffice`, waits for scene initialization, consumes the selection once, sets `NativeEncounterSession.baseUrl` and invokes the existing public `StartPatient(patientId)`. No file in `EncounterOffice/**` or `QuestJarvisVoice.cs` is edited. The office creates its authoritative encounter with `POST /encounters`; the shell invents no encounter, session or attempt ID.

The title is world-locked and reads `Office · <name>, <age>` plus a short complaint excerpt and phase stepper. The 0.4-second fade uses a stereo shader overlay; it is **not a compositor layer**. The async scene activates only behind black. The shell never animates the camera. `Load(scene, title, afterLoad)` can be reused by a later office-to-OR adapter, but no such adapter is claimed here.

## Input, pause and presentation

`ShellInput` reads floor-space head/controller poses before rendering. Pointer rays use trigger or OpenXR EXT hand-interaction pointer/pinch actions; the Android hand-interaction profile and optional hands manifest support are enabled. Invalid tracking or a controller/hand switch requires release before another activation. Hover lifts cards 12 mm, leaves a 0.3-second highlight, and sends a light controller haptic. Enter and mouse are Editor fallbacks.

`ShellPause` persists across scenes. Left menu or Editor Escape opens Resume, confirmed Back to Explore, Recenter and the phase stepper. Focus loss, application suspension and reported headset removal pause with explicit resumption. Time scale and audio output are paused, office input is gated and its public `StopVoice()` is called; voice is not automatically restarted. Existing in-flight office HTTP operations and the server encounter clock can continue while paused: a server encounter transaction pause needs office/service owner work. The current menu does not implement palm-up pinch, restart phase, text-size settings or seated/standing adjustment.

Recenter repositions the hub/menu in front of the tracked head; in the office it rotates presentation roots around the unchanged head. Tracking-origin updates are honored. Returning to Explore unloads the office and creates a fresh hub with no selected chart, rather than preserving a hidden encounter.

The hub reuses office Inter and glass/text shaders. Shell-owned materials provide dark-slate panels, a lilac/rose/butter sky and original procedural blossoms/vines. Forty drifting petals use one instanced opaque draw. There is one shadowless directional light and no screen-grab blur. Instancing is skipped when unsupported.

### Readability constraint

The requested 0.22×0.16 m cards at 1.3 m, six information fields, 4×3 layout and 32/24 mm text at 1 m equivalent cannot all fit together. This slice keeps the requested card geometry and approximately 39° grid width. Measured mono Editor card line heights are 11.84–18.46 mm at 1 m equivalent, below those minimums, with visibly ellipsized excerpts; selected detail lines measure 27.44–41.88 mm. This is an explicit unresolved accessibility requirement, not a passed Quest readability check. Header and body text sit on dark glass; actual stereo contrast, readability, comfort and frame time still require headset measurement. Enlarging cards/grid or reducing card fields needs a design decision.

## Repeatable checks

Use Unity 6000.0.66f2 with the pinned project packages. Install the preop development dependencies with its committed lockfile before validation (`npm ci` in `services/preop`). The validation starts a disposable loopback fixture, not the running demo service.

```sh
UNITY=/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents/MacOS/Unity
"$UNITY" -batchmode -projectPath apps/quest -executeMethod Scalpal.Shell.Editor.ShellBuild.PrepareAndVerify -quit -logFile /tmp/scalpal-shell-verify.log
"$UNITY" -batchmode -projectPath apps/quest -executeMethod Scalpal.Shell.Editor.ShellValidation.RunPlayMode -logFile /tmp/scalpal-shell-playmode.log
SCALPAL_SHELL_APK=/absolute/path/scalpal-shell.apk "$UNITY" -batchmode -projectPath apps/quest -buildTarget Android -executeMethod Scalpal.Shell.Editor.ShellBuild.Build -quit -logFile /tmp/scalpal-shell-build.log
```

`PrepareAndVerify` generates the committed scene/settings, throws on failed validation, and captures mono Editor previews. `ShellValidation.Run` also runs independently. Preview outputs are [Launch](../assets/previews/shell/launch.png), [Explore](../assets/previews/shell/explore.png), and [selected chart](../assets/previews/shell/explore-detail.png). They use packaged synthetic charts and show offline state; they are not headset screenshots.

The component gate covers actual production-client HTTP/bundle/failure callbacks, all four status states, stale detail rejection, filters, physical card/target geometry, actual Start/card/filter button actions, offline Begin rejection, live detail readiness and the one-use handoff contract. The play-mode gate runs the actual async scene load and creates the exact selected authoritative encounter via loopback HTTP, then exercises pause/resume and confirmed Back to a fresh Explore. Run it without `-quit`; the gate exits the Editor itself with an explicit success/failure code. A successful component or play-mode gate is not physical headset or complete-session evidence. Startup within four seconds, 72+ FPS, stereo fade coverage, hand pinch, controller haptics, headset removal, recenter comfort and provider voice remain physical acceptance checks.

Verified shell checkpoint: 134 Editor/HTTP/UI/input assertions passed, including actual measured glyph bounds, long-content layout regressions and 16 synthetic events through the OpenXR hand device layout. A separate Play Mode run on main `c05671e` plus the shell passed 16 async scene/encounter/pause/return assertions (`/tmp/scalpal-shell-playmode-shipping.log`). Android ARM64 development packaging succeeded (`com.scalpal.nativeworkbench`, version `0.6.0-shell`, code 11); the APK is generated locally at `apps/quest/Builds/ScalpalShell.apk`, not committed. Final APK inspection verified ARM64, optional hand tracking, no eye-tracking requirement, and APK v2 signing. The 105,502,055-byte local artifact has SHA-256 `d160b96941f6e20b7f2aba3f972fa491b70061a7d83032bd0d9a60257b3a2f19`. Build/check log: `/tmp/scalpal-shell-build-integrated.log`.

The pinned Unity OpenXR manifest generator makes eye tracking required whenever Quest Pro is targeted, despite disabled eye-gaze input. Shell preparation disables the Quest Pro target while preserving Quest 3/3S targets, avoiding an unsupported required feature on Quest 3S.

APK and regenerated previews include main `2f64d5e` plus shell runtime at `8ed4ee9`. Later main synchronization is tracked separately in the integration audit; rebuilding unrelated concurrent surgery changes is not claimed for this artifact.
