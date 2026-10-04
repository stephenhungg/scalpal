# Scalpal — Quest 3S Shell & Navigation UX Research

Scope: launch -> explore (12 cases) -> VR doctor's office (voice dx) -> VR OR (surgery sim) -> robot-hand replay -> recap -> back to explore. Full VR, Unity + OpenXR, controllers primary, hands optional.

Confidence key: **[Meta]** = Meta official docs/VRC; **[Apple]**, **[Android XR]**, **[MS]**, **[Google]** = other platform guidelines (transferable); **[Research]** = peer-reviewed; **[Derived]** = my arithmetic/judgment from the cited numbers, not a published rule.

---

## 0. Hardware facts that drive sizing (Quest 3S)

- Quest 3S uses Quest 2-class Fresnel lenses; Quest 3 is ~25 PPD vs ~20 PPD on Quest 2-class optics. Treat 3S as **~20 px per degree** — so text must be larger than Quest 3-tuned UI. [Wikipedia, Quest 3S](https://en.wikipedia.org/wiki/Meta_Quest_3S); [Quest 3 PPD figure via search result](https://electronics.alibaba.com/question/quest-3-resolution-explained-native-vs-rendered) (secondary source — verify on device).
- Display is ~100 nits; darkest distinguishable level is ~13/255 sRGB — don't put important UI in near-black values. [Meta Display](https://developers.meta.com/horizon/design/display/)

---

## 1. Launch / start screen and first-run onboarding

### Hard requirements
- **VRC.Quest.Performance.3:** show head-tracked graphics within **4 s** of launch, or a loading indicator in VR. Never a frozen/black head-locked frame. [VRC.Quest.Performance.3](https://developers.meta.com/horizon/resources/vrc-quest-performance-3/)
- **VRC.Quest.Functional.2:** single-player apps must pause when the headset is removed or the Universal Menu opens (Unity: `OnApplicationPause(true)` / `OnApplicationFocus(false)`). [Common VRC failures](https://developers.meta.com/horizon/resources/publish-common-vrc-failures/), [Functional.2](https://developers.meta.com/horizon/resources/vrc-quest-functional-2/)
- **VRC.Quest.Functional.9:** must honor recenter (long-press Meta button resets forward). [Forum thread on Functional.9](https://communityforums.atmeta.com/discussions/dev-unity/app-rejection-due-to-vrc-quest-functional-9/1239752)

### What to show / how fast
- Start screen = a **world-locked** title panel ~1–1.5 m in front at/slightly below eye height, inside a calm, low-detail environment (same environment as the hub so the start screen *is* the hub). Single primary action ("Enter" / trigger / pinch). [Meta Display: menus ≥0.5 m, ~1 m comfortable](https://developers.meta.com/horizon/design/display/)
- Show the **input glyph for the active modality** (controller trigger vs pinch) next to the button. Detect switch and update (VRC.Quest.Input.7 requires respecting controller<->hand switching). [Common VRC failures](https://developers.meta.com/horizon/resources/publish-common-vrc-failures/)
- Time-to-content: aim for **one click** from start screen to explore page. Supernatural's design principle: menus exist to get people into the activity fast; most UI disappears once inside. [Supernatural design writeup (Deocadiz-Smith)](https://deocadiz.com/project/supernatural); [Laptop Mag review](https://www.laptopmag.com/reviews/supernatural-review)
- Onboarding: teach by doing, contextually, one mechanic at a time — trivial guided first task, multi-modal instructions (text + voice + visual cue). Don't front-load a controls tutorial screen. [Meta community: VR onboarding (Horizon Start mentors)](https://communityforums.atmeta.com/category/horizon-developer-forum/discussions/Community_Resources); Owlchemy's "VR for everyone" approach — affordances in-world rather than menus. [Owlchemy case study](https://developers.meta.com/vr/blog/owlchemy-labs-case-study-lessons-learned-from-job-to-vacation/), [GDC: Job Sim -> Vacation Sim](https://www.gdcvault.com/play/1025757/Lessons-Learned-from-Job-Simulator)
- Apple's pacing rule (transferable): start in something familiar (a window/panel), set expectations, then transition into full immersion. [Apple HIG Immersive experiences](https://developer.apple.com/design/human-interface-guidelines/immersive-experiences) (content via search summary; page body didn't render for fetch)

**Scalpal recommendation [Derived]:** Start screen: logo + one-line pitch + "Start" + small modality hint ("Pull trigger / Pinch to select"). For the demo, skip a separate tutorial — teach "point + trigger" on the start button itself, teach "hold A / talk" as a just-in-time prompt the first time the doctor's office voice step appears, and teach surgical grabs in the OR with a ghost-hand hint.

---

## 2. Browsing / catalog UI (explore page)

### Distance, placement, curvature
- Menus/GUIs: **≥0.5 m**, **~1 m comfortable** for extended fixation. [Meta Display](https://developers.meta.com/horizon/design/display/)
- Android XR launches panels at **1.75 m**; optimal-comfort primary content zone **~41° FOV**. [Android XR visual design](https://developer.android.com/design/ui/xr/guides/visual-design)
- visionOS initial window ~2 m; comfortable reading ≥1 m. [visionOS HIG summary](https://github.com/yue1123/apple-hig-skills/blob/master/apple-hig/references/platforms/visionos.md); [WWDC23 spatial design](https://developer.apple.com/videos/play/wwdc2023/10073/)
- MS: optimal hologram zone **1.25–5 m**. [MS Comfort](https://learn.microsoft.com/en-us/windows/mixed-reality/design/comfort)
- Keep important UI near eye level, within ~±30° vertical of horizontal; peripheral placement forces repeated neck motion. [Meta Head](https://developers.meta.com/horizon/design/head/), [Meta Comfort](https://developers.meta.com/vr/design/comfort/)
- "Keep the main controls in a single UI panel" rather than many floating windows. [Meta Comfort](https://developers.meta.com/vr/design/comfort/)
- Meta ISDK supports **curved canvases** (cylinder surface) for ray + poke; no official radius is published. [ISDK Curved Canvases](https://developers.meta.com/vr/documentation/unity/unity-isdk-curved-canvases/), [Create a Curved or Flat UI](https://developers.meta.com/vr/documentation/unity/unity-isdk-create-ui/)
- **[Derived]:** a cylinder radius equal to viewing distance (centered on the user's head) keeps every tile equidistant — use radius ≈ 1.3–1.5 m for a ray-driven wall. Only curve if the panel spans >~40° horizontally; otherwise flat is fine.

### Panel size / dp
- Horizon OS panel: default **1024×640 dp**, min 384×500, max 1440×1000 dp. [Meta Panels](https://developers.meta.com/horizon/design/panels/), [Layouts](https://developers.meta.com/horizon/design/styles_layouts/)
- Android XR conversion: **1 dp = 0.868 dmm**; at 1.75 m, 1024 dp ≈ 1.56 m wide. [Android XR](https://developer.android.com/design/ui/xr/guides/visual-design)

### Text legibility
- Meta: font **≥14 px minimum, ≥18 px comfortable**; typeface **Inter** (high x-height). [Meta Typography](https://developers.meta.com/horizon/design/styles_typography/)
- Horizon OS type scale (dp): H1 32/36, H2 24/28, H3 20/24, Body1 14/20, Body2 11/16. [Meta typography via search summary](https://developers.meta.com/horizon/design/styles_typography/)
- Google Daydream dmm (mm of height at 1 m): **body text 24 dmm**, ray hit targets **≥64×64 dmm with 16 dmm padding**. [Ryan Hinojosa on dmm](https://www.ryanhinojosa.com/2018/01/08/device-independent/), [Future Text Lab](https://futuretextlab.info/2022/02/08/distance-independent-millimeters/)
- Reading study (Oculus Go/Quest): preferred **~27–32 dmm for 2-word labels, ~16–18 dmm for paragraphs**, contrast ≥7:1 but not max contrast (pure white on pure black rated worse). [Kojić et al., arXiv 2004.01545](https://arxiv.org/html/2004.01545v1)
- MS: absolute minimum **0.35°–0.4°** text height (≈12–14 mm at 2 m); avoid light/thin weights. [MS Text in Unity](https://learn.microsoft.com/en-us/windows/mixed-reality/develop/unity/text-in-unity)
- Accessibility: ≤32 characters per row and ≤2 rows for captions; contrast 4.5:1 normal / 3:1 large text; offer text scale 50–200%. [Meta Accessibility](https://developers.meta.com/horizon/design/accessibility/)
- **[Derived] for Quest 3S at 1.3 m:** labels/names ≥ 32 dmm (~42 mm em height, ~1.8°, ~37 px); body/chart highlights ≥ 24 dmm (~31 mm, ~1.4°); never below 16 dmm. Use Inter/semibold-regular, off-white on dark slate (~8–12:1).

### Targets, spacing, interaction
- Meta: min target **22×22 mm** (≈48 dp / ~3° at ~0.4 m), **12 mm** spacing between interactables. [Meta Hands interaction types](https://developers.meta.com/horizon/design/hands-interaction-types/), [Accessibility](https://developers.meta.com/horizon/design/accessibility/)
- visionOS: 60 pt targets, centers ≥60 pt apart. Android XR: 56 dp targets, 8 dp spacing. [visionOS summary](https://github.com/yue1123/apple-hig-skills/blob/master/apple-hig/references/platforms/visionos.md), [Android XR](https://developer.android.com/design/ui/xr/guides/visual-design)
- Ray vs poke: **ray for distant panels/selection**, **poke for near UI (within arm's reach, ~0.4–0.6 m)**; poke = index finger only, select only on real contact; ray select = pinch/trigger; don't rely on ray alone. [Meta Hands interaction types](https://developers.meta.com/horizon/design/hands-interaction-types/)
- Ray feedback spec (Meta): hover beam/cursor white, select dark blue (#001E78); select cursor shrinks to ~83%; cursor keeps constant angular size; hover highlight 0.3 s, press 0.08 s, release 0.1 s; hover = light haptic, select = press haptic + spatial click sound. [Meta Raycasting specs](https://developers.meta.com/vr/design/raycasting_specs/)
- Interactor lifecycle states: Disabled / Normal / Hover / Select — design all four. [ISDK lifecycle](https://developers.meta.com/horizon/documentation/unity/unity-isdk-interactor-interactable-lifecycle/)
- Long-press: ~0.5 s; cooldown 0.25 s. [Meta Hands interaction types](https://developers.meta.com/horizon/design/hands-interaction-types/)

### Layout pattern: grid vs carousel vs spatial gallery
- Comparable products: Osso VR — tile dashboard, select a tile, thumbstick scroll for overflow. [Osso support](https://www.ossovr.com/support-articles/how-to-log-in-and-access-scenarios). Supernatural — few large floating category cards + filters (duration, intensity, music). [Laptop Mag](https://www.laptopmag.com/reviews/supernatural-review). Oxford Medical Simulation — scenario list; reviewers complain about unintuitive navigation/controls; interactables glow blue on gaze. [Unbound Medicine review](https://www.unboundmedicine.com/oadn/view/Virtual%20Simulation%20Reviews/2455046/all/Oxford_Medical_Simulation)
- (Medal of Honor and Body Interact case-select menus: no authoritative written source found — not cited.)
- **[Derived] for 12 cases:** 12 is small enough to show **all at once — no carousel, no paging**. At 1.3 m, a ~41° comfort zone is ~0.97 m wide; a 4×3 grid of ~0.22 × 0.16 m cards (~9.5° wide each, ~20× the 22 mm min target) fits with ~2 cm gutters. Carousels hide content and add a scroll interaction; skip them. A "spatial gallery" (patients as 3D figures in rooms) is great for wow but costs readability and head turning — hybrid: grid of cards + a **3D patient preview/bust on hover/select** beside the grid.
- Two-stage selection: card hover = highlight + lift (z +1–2 cm) + haptic tick; card select = opens a **detail panel** (name, age, complaint, chart highlights, status) with a single big "Begin encounter" button. Avoid launching a scene on a single accidental click.
- Status: color + icon + text label (never color alone): ready (green check), needs_review (amber), blocked (grey, lock, disabled state, non-selectable but explains why), retry (blue loop). Filter as 4 chip toggles above the grid + "All". Sort ready first.

---

## 3. Scene transitions

- Comfort: avoid moving the camera without user input; no sudden acceleration or unnatural camera motion; prefer teleport/blink over smooth motion. [Meta Comfort](https://developers.meta.com/vr/design/comfort/), [Locomotion comfort & usability](https://developers.meta.com/horizon/design/locomotion-comfort-usability/), [Locomotion best practices](https://developers.meta.com/horizon/design/locomotion-best-practices/)
- Blinks/fades + spatial sound reduce disorientation on camera discontinuities; independent visual backgrounds give a stable reference. [Locomotion comfort & usability](https://developers.meta.com/horizon/design/locomotion-comfort-usability/)
- Apple: don't encourage movement in full immersion; transition gradually. [Apple HIG Immersive experiences](https://developer.apple.com/design/human-interface-guidelines/immersive-experiences)
- Research: Portals/orbs beat cuts/fades on presence & continuity, but portals caused more disorientation (re-orientation); fast cut/fade is preferred for frequent task switching; portals suit infrequent transitions between very different environments. Advance visual cues of an impending change help users re-orient. [Husung & Langbehn, "Of Portals and Orbs" (MUC 2019)](https://www.edit.fis.uni-hamburg.de/ws/files/12484141/MUC_Portals_Paper.pdf); [Scene transitions & teleportation (spatial awareness/sickness)](https://www.researchgate.net/publication/329332173_Scene_Transitions_and_Teleportation_in_Virtual_Reality_and_the_Implications_for_Spatial_Awareness_and_Sickness); [CHI '24 cinematography for VR navigation](https://dl.acm.org/doi/10.1145/3613904.3642412)
- Unity implementation (no hitch): fade to black (ideally at compositor level, `OVRManager.SetColorScaleAndOffset` / OVROverlay), load async with `allowSceneActivation = false`, activate only once fully black, show a **world-locked** compositor-layer loading indicator (cubemap + quad) during the heavy frame, then fade in. World-locked layers are timewarped; head-locked layers judder. [Meta OVROverlay](https://developers.meta.com/horizon/documentation/unity/unity-ovroverlay/), [Meta forum: scene loading with compositor](https://communityforums.atmeta.com/discussions/dev-unity/how-to-handle-scene-loading-in-unity-like-when-using-steamvr-compositor/611801), [fade timing pitfall](https://bugnet.io/blog/how-to-fix-fade-to-black-finishing-before-scene-load-completes)

**[Derived] for Scalpal:**
- Use **one consistent pattern**: user-initiated "Begin" -> 0.3–0.5 s fade to black (or to the brand color) -> title card in black void ("Encounter 1 of 3 — Clinic: Jane Doe, 54, chest pain") -> fade in 0.5 s. Transitions are infrequent and the user is static, so a **door/portal** is optional polish for hub->clinic (walk-through = user motion, comfortable), but fade is the safe baseline.
- **Never move the rig** during transitions; spawn the user in the new scene facing the focal point (patient / table), aligned to their current yaw (re-orient the scene, not the camera), at the same floor height.
- Phase signal: persistent phase stepper (Clinic -> OR -> Replay -> Recap) shown on the title card and in the wrist/pause menu; distinct spatial audio bed per environment; distinct lighting palette.
- Keep scenes additive if memory allows (Hub scene persistent, load Clinic/OR additively) so "back to explore" is instant.

---

## 4. Persistent UI, pause/back, exit, tracking loss, recenter

- **Avoid head-locked menus/status** (VRC.Quest.Functional.10, recommended). Use world-locked, or body-locked "lazy follow" with smoothing. [VRC.Quest.Functional.10](https://developers.meta.com/horizon/resources/vrc-quest-functional-10/), [Meta MR key considerations](https://developers.meta.com/horizon/design/mr-design-guideline/)
- Hand/wrist menus: don't put buttons near the wrist system button — accidental triggers. [MS hand menu](https://learn.microsoft.com/th-th/windows/mixed-reality/design/hand-menu)
- Menu button: the left controller "≡" button is the conventional in-app menu; the Meta button is reserved for the system. Universal Menu open must pause (Functional.2).
- Recenter: OpenXR — enable Meta's reset-view handling / subscribe to `XRInputSubsystem.trackingOriginUpdated`; OVR — `OVRManager.display.RecenteredPose`. Use Floor/Local-floor origin so recenters propagate (stage mode swallows them). Re-place world-locked hub UI in front of the user after recenter. [Unity discussion](https://discussions.unity.com/t/how-to-detect-recenter-on-quest-meta-devices/911610), [Unity floor-origin recenter bug](https://discussions.unity.com/t/case-1415194-recentering-in-tracking-origin-mode-floor-with-openxr-on-oculus-quest-2-is-broken/875664)
- Tracking loss: hide hands when tracking confidence is low/lost (VRC.Quest.Input.6); handle controller<->hands switching (Input.7). [Common VRC failures](https://developers.meta.com/horizon/resources/publish-common-vrc-failures/)
- Head ray fallback (system): ray fades after inactivity; head-ray fallback after 20 s. [Raycasting specs](https://developers.meta.com/vr/design/raycasting_specs/)

**[Derived] for Scalpal:**
- Pause menu on **left ≡ button** (and palm-up + pinch for hands): opens a world-locked panel spawned ~1 m in front at current gaze yaw; scene audio ducks and sim time pauses. Contents: Resume, Restart phase, **Back to Explore** (confirm dialog), Recenter view, Seated/Standing toggle, Text size, Phase stepper.
- Optional wrist widget (controllers: small tile on the left controller; hands: palm-up) showing only phase + timer + "menu" — glanceable, not interactive-heavy.
- On recenter: re-orient scene root to match new forward; re-spawn menus in front.
- On headset off / focus loss: pause sim + voice capture; on resume show "Paused — press to continue" instead of auto-resuming mid-surgery.

---

## 5. Accessibility & comfort basics

- Session length: Meta health guidance — start with **≤30 min sessions then ~15 min breaks**, increase gradually. Target the full loop (clinic + OR + replay + recap) at **≤10–15 min** for a hackathon demo. [Meta H&S Quest 3S](https://www.meta.com/legal/quest/health-and-safety-warnings/quest-3s/)
- Seated/standing/roomscale must match declared play modes (VRC.Quest.Tracking.1). [Common VRC failures](https://developers.meta.com/horizon/resources/publish-common-vrc-failures/)
- Support seated, reclining, stationary; single-controller remapping; voice. [Meta Accessibility](https://developers.meta.com/horizon/design/accessibility/)
- Place interactables at comfortable height so users don't constantly look down. [Meta Head](https://developers.meta.com/horizon/design/head/)
- Captions: ~1 m away, top/bottom of a 40° FOV, ≤32 chars/row, ≤2 rows, ~0.5 s roll-up — relevant to the voice encounter (show patient + user transcripts). [Meta Accessibility](https://developers.meta.com/horizon/design/accessibility/)
- Contrast: WCAG 4.5:1 text, 3:1 UI; focus indicator ≥2 px. [Meta Accessibility](https://developers.meta.com/horizon/design/accessibility/)
- Frame rate: keep it consistent — judder drives discomfort. [Locomotion best practices](https://developers.meta.com/horizon/design/locomotion-best-practices/). (The 72 fps Quest minimum is from VRC performance requirements, which I did not re-check in this session.)

**[Derived] for Scalpal:** Default to **seated-friendly**: OR table height auto-set from the user's head height at phase start (e.g. table top ~0.45–0.55 m below eye height), plus a "Adjust height" button. All UI reachable without turning >±30°. No artificial locomotion anywhere — the user stays put; scenes come to them.

---

## Open questions / gaps
- Apple HIG pages didn't render via fetch; Apple points come from search summaries and a community mirror — verify wording if quoting.
- No official Meta curvature radius found; radius recommendation is derived.
- No written source found for Medal of Honor or Body Interact menus.
- Quest 3S PPD (~20) is from secondary sources; confirm legibility on device.
