# Launch and Explore Shell

`Assets/Scalpal/Shell/Scenes/Launch.unity` opens a full-VR floral hub. Start reveals the patient grid; selecting a chart never launches a scene. A separate Begin hands the exact patient ID and service endpoint to the diagnosis office. The existing `ScalpalPreopService` and `Exercises/Data` DTOs are active dependencies and must not be removed.

## Data and availability

Reload requests the live `/patients` list first. It does not request the expensive `/unity/bundle` in parallel. The cached/packaged bundle supplies fallback data and missing display metadata locally; its status and capability flags never replace a live list. Selecting a non-retry patient requests `/patients/:id/brief`.

Begin requires `PatientListEntry.encounterAvailable`, a selectable ready/needs-review state, and the matching live synthetic demo or sandbox brief. There are no Shell patient-ID capability lists. Canonical ID syntax is checked through the existing office contract. The flag is authoritative for interview availability; the office owns presenting the returned subject. Cached metadata or an unrelated request timeout cannot disable an already live selected brief. An offline brief clears that patient's live eligibility. Any subsequent live response clears the offline banner, and a live matching brief restores Begin without Refresh. Stale identities and malformed/non-synthetic charts fail closed.

Connection-only rows with no patient ID and blocked patients do not occupy cards. One footer note counts unavailable patients. The rate-limited patient remains a Try again card and does not automatically request a brief. Current production `/case` returns HTTP 200 with a retry state and delay; `/brief` can return 429 with `Retry-After`. Both delays are honored, including legacy 429 case responses, and survive switching cards. Refresh aborts/disposes active requests and invalidates their callbacks.

The packaged bundle was regenerated from the existing recorded fixture exporter, including `encounterAvailable` and the correct retry state rather than an old playable rate-limited chart. It contains synthetic fixtures only. The live route can add admitted sandbox patients; these remain subject to the same synthetic-brief and availability checks.

## Layout and appearance

The visible nine fixture patients occupy a four-column grid with room for twelve. Larger admitted catalogs page in groups of twelve; filters reset the page. The footer, status and Refresh stay outside the card area. Five card lines show name, age/sex, one-line complaint, procedure/urgency and status with a geometry icon. Coming-soon detail remains visible when capability is false.

Cards are now 0.50 × 0.32 m on a 2.22 × 1.60 m panel 1.3 m forward. This deliberately replaces the old compact ~41° layout to meet text size. Names use fixed 57 mm glyph height and body/status 43 mm, with width ellipsis rather than shrink-to-fit. The gate measures each card at its actual distance from the camera and fails below 32/24 dmm. The chart panel is angled beside the grid on a roughly 1.7 m radius, with larger text; looking at it requires a head turn. The camera never animates.

Shell-owned frosted glass uses translucent lilac/slate tint, rounded luminous rims, a soft procedural sky-reflection approximation and subtle sheen. It does not blur or refract live scene objects. There is no GrabPass, capture camera, per-panel render texture or postprocessing blur. The pastel sky, light fog, original blossoms/vines and 28 GPU-instanced drifting opaque petals frame the hub. Text fitters run when content changes and are disabled afterward; one shared font-atlas callback handles Inter updates. Hints, banners, countdown labels and material properties update only on change.

## Office handoff, input and pause

`ShellTransition.BeginOffice(patientId, title, serviceUrl)` stages one immutable selection, fades for 0.4 seconds and loads `DiagnosisOffice`. It waits for `EncounterOfficeRig.Ready` before placing the title or calling the existing `NativeEncounterSession.StartPatient`. Two seconds without readiness produces a visible recovery menu rather than revealing an unaligned room. The no-XR desktop Editor explicitly uses the authored preview pose; that exception is not headset evidence.

The shell clears an abandoned `EncounterOfficeRoute` patient selection before loading to avoid a second automatic start, consumes its own selection once, applies the selected endpoint after office configuration, and calls the office entry point. It introduces no encounter, attempt or progression authority. The transition completion event clears Hub transition state on failure. The office owns the later assessment/AR-or-VR surgery route.

Controller trigger or hand pointer/pinch selects. Pinch uses 0.75 down / 0.45 release thresholds and a 150 ms minimum press interval; tracking loss requires a release before rearming. Hover lifts a card 12 mm with a 0.3-second highlight and controller haptic. Enter/mouse are Editor fallbacks.

Left menu/Escape opens Resume, confirmed Back to Explore, Recenter and the phase stepper. Focus loss, suspension and headset removal pause time/audio and stop office voice. Fade/title overlays hide while paused, including mid-transition. Startup origin events are ignored until office alignment; recenter repositions presentation around the unchanged tracked head. In-flight HTTP and the server encounter clock can continue while paused; an authoritative transaction pause remains owner work. Hand-only pause gestures, restart phase and text-size settings are not implemented.

## Single-player build contract — integration owner

The intended shipping player has one application ID, `com.scalpal.nativeworkbench`, and enabled order **Launch → DiagnosisOffice → NativeSession**, followed by every other existing scene with its enabled state preserved. A Shell and a surgery component APK using that ID replace each other when installed; they are not separate applications.

`Handoff/Editor/ScalpalPlayerBuild` is now the integration owner’s unified player builder and resolves Launch ahead of Office and NativeSession. `ShellBuild.EnsureSceneOrder()` idempotently restores this order, removes duplicate entries and preserves other scenes. Shell Prepare and Build both call it. **Integration-owner follow-up:** surgery prepare methods that assign a one-element scene array must merge the shared list or call this helper after preparing and before `BuildPipeline.BuildPlayer`. Do not reset the shared player to NativeSession only. No surgery prepare method or office-owned file is changed by this Shell review fix.

The pinned OpenXR manifest generator makes eye tracking required if Quest Pro is targeted. Shell preparation preserves Quest 3/3S and disables the Quest Pro target to avoid requiring unavailable eye tracking on Quest 3S. Development players read the existing private `session-config.json` endpoint keys; localhost on Quest requires USB reverse forwarding. Secrets remain service-side.

## Verification and previews

Use Unity 6000.0.66f2 and install preop dependencies with `npm ci` in `services/preop`.

```sh
UNITY=/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents/MacOS/Unity
"$UNITY" -batchmode -projectPath apps/quest -executeMethod Scalpal.Shell.Editor.ShellBuild.PrepareAndVerify -quit -logFile /tmp/scalpal-shell-verify.log
"$UNITY" -batchmode -projectPath apps/quest -executeMethod Scalpal.Shell.Editor.ShellValidation.RunPlayMode -logFile /tmp/scalpal-shell-playmode.log
SCALPAL_SHELL_APK=/absolute/path/ScalpalShell.apk "$UNITY" -batchmode -projectPath apps/quest -buildTarget Android -executeMethod Scalpal.Shell.Editor.ShellBuild.Build -quit -logFile /tmp/scalpal-shell-build.log
```

The rebased review component gate passed 221 assertions, including actual HTTP/offline recovery, all eight server-advertised encounters, canonical IDs, production and controlled legacy retry responses, cancellation, 25-patient paging, fixed glyph sizes, scene-order preservation and 25 synthetic input checks. Measured card lines are 26.81–42.65 dmm, with names separately required to meet 32 dmm; chart lines are 29.31–44.74 dmm. These are mono Editor geometry measurements, not headset readability or performance measurements. Play Mode passed 22 assertions through the actual HTTP encounter, pause and return route. The test explicitly disables the external realtime bridge before office Start (`isolatedRealtime=true`); production pairing stays required. Provider voice and shared-attempt exchange are outside this component result.

Previews: [Launch](../assets/previews/shell/launch.png), [Explore](../assets/previews/shell/explore.png), [selected chart](../assets/previews/shell/explore-detail.png). Wide mono Editor overview cameras show the expanded layout; these are not headset screenshots. Physical startup within four seconds, 72+ FPS, stereo glass/fade, controller haptics, hand tracking and comfort still require Quest testing. The reviewed ARM64 development APK built successfully from Shell commit `7349bbd` on integration snapshot `249f9ff` (105,680,597 bytes, version `0.6.0-shell` / code 11). APK v2 signature verification passed; hand tracking is optional and eye tracking is not required. SHA-256: `d3ba43af5c6ddeeed350a77c698f17aa46f5ec87d1395fb5b447cae7b88ebcb6`. This artifact predates subsequent upstream OR changes; the merged editor checks are recorded separately.
