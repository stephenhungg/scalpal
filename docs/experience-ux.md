# Experience and UX Spec

Updated October 3, 2026. This is the recommended UX for the [latest experience flow](current-direction.md#latest-experience-flow): launch → explore patients → full-VR diagnosis office → operating room in AR (real person) or VR (virtual patient) → robot replay → recap. It is a design target, not a description of built behavior; the [integration map](system-integration.md#explore--office--or-route) records what exists. Recommendations come from the cited research in [VR shell UX](research/ux-vr-shell.md) and [medical simulation UX](research/ux-medical-sim.md). Numbers marked *derived* are our sizing from published rules, to be checked on the Quest 3S.

## Principles

1. **The learner never moves artificially; scenes come to them.** No artificial locomotion and no camera motion. Every VR transition is a fade and the learner is spawned facing the focal point. In AR mode the OR is passthrough: the real room, a real participant on a table, virtual anatomy registered onto them. In VR mode it is the virtual OR.
2. **One click to content.** No upfront controls tutorial. Teach each mechanic the first time it is needed.
3. **The LLM talks; the engine decides.** Exam findings, test results, scoring and step progression come only from authored deterministic state. Voice agents never invent clinical facts or scene effects.
4. **One voice role at a time, one per space.** Patient in the office, Jarvis as the attending in the office, Jarvis as the coach in the OR. Never two agents speaking at once.
5. **Commit, consequence, reflect.** The learner may be wrong, sees the result, and the run still finishes in the correct surgery.
6. **Two separate scores.** Clinical reasoning (office) and procedural skill (OR) are never blended.
7. **Short by default, deep on request.** Full run about 13–18 minutes; the judge fast path is about 5–6 minutes and samples every stage.

## Global Shell

| Element | Spec | Source |
| --- | --- | --- |
| Launch | Head-tracked visuals or a VR loading indicator within 4 s | Meta VRC Performance.3 |
| Pause | Pause sim and voice capture when the headset is removed or the system menu opens; on return show "Paused, press to continue" (never auto-resume mid-surgery) | VRC Functional.2 |
| Recenter | Honor recenter; floor tracking origin (stage origin swallows recenters); re-orient the scene root, re-spawn menus in front | VRC Functional.9 |
| Menus | World-locked or lazy-follow, never head-locked | VRC Functional.10 |
| Pause menu | Left ≡ button (palm-up pinch with hands), world-locked about 1 m ahead: Resume, Restart phase, Back to explore (confirm), Recenter, Seated/standing, Text size, phase stepper | Derived from Meta guidance |
| Phase stepper | Explore → Office → OR → Replay → Recap, on every title card and the pause menu | Derived |
| Hands | Hide hands when tracking confidence is low; support controller ↔ hand switching | VRC Input.6/7 |
| Comfort | Seated-friendly; all UI within ±30° of eye level; OR table height set from head height plus an "Adjust height" control | Meta comfort/head guidance |
| Text | Inter, off-white on dark slate (8–12:1, not pure white on black); labels ≥32 dmm, body ≥24 dmm, never <16 dmm (derived for ~20 px/degree 3S optics) | Meta typography, Kojić et al. |
| Targets | ≥22×22 mm, 12 mm spacing; ray for distant panels, poke only within arm's reach | Meta hands interaction |
| Ray feedback | Hover highlight 0.3 s + light haptic; select = press haptic + click sound | Meta raycasting specs |
| Captions | All dialogue captioned about 1 m away, lazy-follow, ≤32 characters × 2 rows, speaker name and color | Meta accessibility; VRC subtitles |

### Scene transitions

User presses an explicit "Begin" → 0.3–0.5 s fade to black (compositor-level) → async load with activation held until fully black → world-locked title card on a compositor layer ("Office · Morgan Rivera, 38 · abdominal pain") → 0.5 s fade in, facing the focal point at the same floor height. Keep the explore hub loaded and the office/OR additive if memory allows, so "back to explore" is instant. Each phase has its own lighting palette and ambient audio bed. A walk-through door between the hub and the office is optional polish; the fade is the baseline.

## 1. Launch

The start screen *is* the explore hub environment: one world-locked panel about 1–1.5 m ahead at or just below eye level with the logo, one line ("Diagnose and operate on synthetic patients"), one **Start** button and a modality hint ("Pull trigger" / "Pinch"), updated live when the input changes. Enter on a keyboard (editor) or trigger/pinch starts. Pointing at Start teaches pointing; nothing else is taught here.

## 2. Explore Patients

**Layout.** All 12 FinchNode demo scenarios at once: 10 patient cards plus the 2 connection-only scenarios (`connect-cancelled`, `connect-failed`), which have no patient and show as connect-state cards. A 4×3 grid of cards (~0.22 × 0.16 m) on a panel about 1.3 m away, within the ~41° comfort zone. No carousel, no paging. Flat is fine; if it curves, the radius equals the viewing distance.

**Card.** Name, age/sex, one-line presenting complaint, procedure badge, urgency, status. Synthetic data is labeled "Synthetic record" once in the header, not on every card.

**Status, never by color alone.**

| Service status | Card | Selectable |
| --- | --- | --- |
| `ready` | Check icon, "Ready" | Yes |
| `needs_review` | Amber flag, "Chart has gaps" (the gaps become something to catch) | Yes |
| `blocked` | Grey lock, reason ("Consent revoked") | No; explains why |
| `retry` | Loop icon, "Try again in Ns" | Retry action |

Sort ready first. Filter chips above the grid: All, by procedure (appendix, gallbladder, colon), by urgency. Offline bundle data shows a single "Offline data" banner.

**Hover and select.** Hover lifts the card 1–2 cm with a haptic tick. Select opens a detail panel beside the grid: chart highlights from `/patients/:id/brief`, a 3D patient bust/preview, and one large **Begin encounter** button. A single accidental click never launches a scene.

### Implemented shell differences

The [Launch/Explore shell](shell.md) implements the above navigation with controller trigger, OpenXR pointer/pinch and Editor Enter. The 4×3 card geometry is retained, but dense card text is below the stated 32/24 mm equivalent minima; a larger selected chart improves detail readability but does not resolve that card-size conflict. The optional patient bust is omitted. The current native office supports two adult IDs, so all other selectable charts say “Interview coming soon.” Offline browsing is labeled and Begin stays disabled until the service reconnects.

The transition uses a stereo geometry fade, not a compositor layer, and unloads/reloads scenes rather than keeping them additive. Pause includes Resume, confirmed Back to Explore, Recenter and the stepper. Restart phase, palm-up pause, seated/standing and text-size controls remain unimplemented. The office's in-flight HTTP operations may complete while presentation and voice are paused. See the component document for verification and physical limits.

## 3. Diagnosis Office

**Order of work** (i-Human structure): greet → history → exam → orders → present. The learner may move freely between history, exam and orders; presenting ends the encounter.

**Voice.**
- **Hold-to-talk is the default** (grip or trigger) with a haptic tick on press and release and a controller-anchored "Hold grip to talk" hint shown the first time. Open mic is a settings toggle; a hackathon floor breaks voice activity detection.
- **Latency target:** first audible reply ≤1.0 s after the learner stops talking (≤1.5 s acceptable). Stream TTS sentence by sentence.
- **Cover waits with behavior, not icons:** an instant pre-recorded filler ("mm…", a wince, shifting in the chair) while the model generates. Only the learner's own HUD shows a small listening glyph.
- **Barge-in:** pressing talk (or 300 ms of speech in open-mic mode) cuts the agent within ~200 ms. Backchannels ("ok", "mm-hm") do not interrupt.
- **Misrecognition:** low confidence or a fragment gets an in-character "Sorry, what was that?", plus 3–4 suggested-question chips that double as tier-1 hints. Bias recognition toward the chart's drug names and exam terms.
- **Transcript:** the learner's words appear immediately (grey partials), then the reply, labeled by speaker. The full transcript lives on a clipboard panel and feeds the presentation and debrief.

**Grounding the patient.** The patient model receives only what the patient knows: symptoms, timeline, history, medications, social history, feelings and lay beliefs ("I think it's food poisoning"). It never receives the diagnosis, exam findings or lab values. An output filter catches diagnosis terms and synonyms (appendicitis, cholecystitis, diverticulitis) and lab/imaging words and regenerates. Facts asked are tracked in code for scoring. Improvised details (about 14% of questions fall outside an authored script) are logged so the debrief cannot contradict them.

**Exam and orders.** Exam maneuvers are picked from a short in-world list near the patient (no deep menus); findings render from encounter state. Orders return after a short, believable delay. Every maneuver and order carries a tier: Critical, Reasonable, Neutral, Unnecessary (Full Code pattern), so "order everything" shows up on the scorecard.

**Time.** A soft clock, about 6–8 minutes. At ~75%, an in-world cue (the patient says it's getting worse, a nurse knocks). At 100%, Jarvis asks "Ready to present?" Never a hard fail.

**Hints, three tiers, each logged:** patient nudge → Jarvis Socratic question → explicit suggestion.

**Presenting.** Jarvis enters (distinct avatar, voice, caption color, spatial position) with an explicit handoff line: "Okay, present your patient." The patient connection closes first; pending patient replies are discarded. The learner states diagnosis, differential, procedure and urgency.

## 4. Diagnosis → Surgery Handoff

Recommended (resolves the open decision in [decisions](decisions.md) unless Stephen chooses otherwise):

1. Jarvis records exactly what the learner said and the reasoning scorecard grades it.
2. **If the plan is wrong,** Jarvis challenges once, stating an observation and then asking for the learner's reasoning: "I heard gastroenteritis. I'm worried about the right lower quadrant rebound and a white count of 14. What made you lean away from appendicitis?" The learner may revise once (logged as "revised after prompt").
3. **If it is still wrong,** a short consequence card ("6 hours later: perforated, febrile") with worsening vitals, then "The surgical team takes the case. You'll scrub in." The run continues to the **correct** procedure, flagged "case escalated".
4. The learner never operates on the wrong procedure: there is no authored content for it and it would teach a false operation.

Evidence: productive failure beats instruction-first on conceptual knowledge and transfer (d = 0.36, up to 0.58; Sinha & Kapur 2021), provided the run does not dead-end.

## 5. Operating Room (AR or VR)

The learner picks AR (real participant) or VR (virtual patient) at the handoff; both share the same tools, steps, coaching and scoring. In VR the office fades into the virtual OR. In AR the office fades into passthrough. The participant reclines on the table; the app detects their body with MediaPipe, shows registration progress and quality, and overlays the case's anatomy once the fit is stable. The operator confirms the fit. Lost registration hides the anatomy and pauses scoring until it reacquires; tools freeze rather than drop. The detailed handoff spec is [office to OR handoff](office-to-or-handoff.md).


- **Onboarding, 60–90 s, skippable after the first run:** before the case, touch a target with each instrument; learn grip, trigger and the camera/trocar. A ghost hand demonstrates each.
- **Steps:** 6–10 per procedure, shown on the OR wall monitor with the current step highlighted. Chart risks found in the office (anticoagulation, allergy, incomplete chart) appear in the pre-op check and are pinned to their steps.
- **Coaching:** one voice line under 12 words at each step start. Speak again only after a ~15 s stall or an error. After a stall, pulse the target anatomy outline (escalating cues). Wearing the headset already raises cognitive load substantially (+66% in one RCT), so silence is the default.
- **Decision points:** 1–2 per procedure, as single-best-answer questions at critical moments (for example, critical view of safety before clipping), so reasoning is assessed without requiring fine motor fidelity.
- **Errors:** immediate and non-blocking: red flash on the tissue, haptic buzz, one coach line ("Careful, that's the cystic artery"). Logged; critique is saved for the recap (feedback after the case transfers better than feedback during it).
- **Safety lines** from the coach cannot be barged in on; an interrupted safety line is repeated.
- **Recording:** a visible "Recording hands" indicator during the defined segment, so the learner knows what will feed the replay.
- **Metrics:** time per step, path length per hand (from the same recording), wrong-target contacts, hints used, decision-point answers, shown against a proficiency band rather than as raw numbers alone.

## 6. Robot Replay (Required)

A world-locked panel (or a robot hand on the OR table) plays the derived Shadow-hand replay next to the learner's own recorded segment. Error timestamps from the OR are marked on the timeline and are scrubbable. Processing state is honest: queued → processing → ready, or failed with a reason; a failure never hides the learning scores. Label it plainly: "Your hand motion, retargeted to a robot hand. Kinematic replay, not a trained robot."

## 7. Recap

PEARLS-lite, about 60–90 s:

1. Jarvis asks one reaction question by voice ("How did that feel?"), unscored.
2. One self-assessment question ("One thing you'd do differently?").
3. Reveal the two scorecards:
   - **Clinical reasoning:** critical history items asked, missed questions listed, critical exam maneuvers done, orders by tier, diagnosis, whether the differential included the must-not-miss items, procedure and urgency, hints used, time.
   - **Procedural skill:** steps completed in order, time per step versus benchmark, path length, errors by type, decision-point answers.
4. Jarvis gives at most two strengths and two improvements, generated from the deterministic event log so it cannot invent events.
5. One take-away line, then **Choose another patient** (back to explore) or **Retry surgery**.

## Judge Fast Path

A "Demo mode" toggle preselects the best-tuned case, shows suggested-question chips, and time-lapses non-key surgical steps by fast-forwarding the recorded motion. Every stage still appears.

| Stage | Full run | Fast path |
| --- | --- | --- |
| Pick patient | 30 s | 10 s (preselected) |
| History | 3–4 min | 60–90 s (3–4 key questions, chips) |
| Exam and orders | 1–2 min | 30 s |
| Present to Jarvis | 1 min | 30 s |
| Reasoning scorecard | 30 s | 15 s |
| OR onboarding | 60–90 s | 20 s (skipped if done) |
| Surgery | 4–6 min | 90 s (2–3 key steps + 1 decision point) |
| Robot replay | 30–60 s | 20 s highlight |
| Recap | 60–90 s | 30 s |
| **Total** | **~13–18 min** | **~5–6 min** |

Show the depth explicitly: the 12-card wall (10 patients) from synthetic chart data; a patient who will not name their diagnosis; "the model never invents labs"; a second run that deliberately misses the diagnosis to show the consequence branch; the dual scorecard; the robot replay. Cast the headset view to a laptop with captions on. Keep fallbacks: a prerecorded full run, offline TTS fillers and canned patient answers if the network fails. Meta's health guidance (sessions ≤30 min) also favors the short run.

## Open Items

- Confirm the handoff recommendation in section 4 (Stephen).
- Validate text sizes, frame time and caption placement on the physical Quest 3S; the ~20 px/degree figure is from secondary sources.
- Measure actual voice round-trip latency with the chosen provider against the ≤1.0–1.5 s target.
- Author tiers (Critical/Reasonable/Neutral/Unnecessary), hint ladders and must-not-miss differentials for every encounter, and decision points for each procedure.

## Run-ending implementation checkpoint (October 3)

The independently openable `Recap/Scenes/RunEnding.unity` implements replay → unscored reaction → self-assessment → separate scorecards, using the office glass/Inter assets. Jarvis's one reaction question uses the existing voice client and a server-built static prompt; self-assessment remains on screen. Facts, strengths and improvements are selected deterministically rather than generated by an LLM, so no extra provider dependency can invent outcomes. Voice failure retains both questions.

A bundled 20-second **synthetic Shadow-hand sample** supplies the fallback; it is never labeled as the learner's recording or a successful processing job. Job state and fallback provenance remain separate. Error timestamps seek only with an explicit OR-to-clip clock mapping. Demo mode in this component caps the replay window at 20 seconds; preselection, question chips, marking/pre-exposure and surgery time-lapse remain flags for their scene owners. See the [ending contract](system-integration.md#run-ending-contract--recap-lane-october-3).

The companion `/recap` and `/s/:id/recap` display the same JSON contract via explicit import and include sample previews. Automatic RunResult publication, the surgery aggregate grade exporter, actual capture/queue integration, OR-to-recap routing, retry reset and physical headset validation remain open interfaces; the standalone sample does not establish a completed live run.
