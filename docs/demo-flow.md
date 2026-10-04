# End-to-End Demo Flow

Updated October 3, 2026 for the user's explore → office → operating room flow. Stage-by-stage UX recommendations: [experience UX](experience-ux.md). This is the target user journey, not a completed application. The [system integration map](system-integration.md) records actual components, routes and missing adapters. There are no wallets, payouts or onchain stages. The previous conversational-selection, rotating-preview and real-person MR journey is superseded; see [current direction](current-direction.md#latest-experience-flow).

```
Launch ─Enter─▶ Explore patients ─choose─▶ Diagnosis office ─assessment─▶ Operating room ─▶ Robot replay ─▶ Recap
                   ▲                                                                                        │
                   └──────────────────────────────── choose another patient ◀───────────────────────────────┘
```

## Before the Learner Arrives

The operator confirms the pre-op/encounter service, voice provider and shared-state connectivity, pairs the headset and companion to the same session, and confirms the video capture route for the replay step. The app uses full VR throughout; no real participant reclines on a table.

## 1. Launch

The Quest app opens on a start screen. Pressing Enter (keyboard in editor, controller confirm on the headset) opens the explore page. Launching does not start recording, voice or an attempt.

## 2. Explore Patients

A large browsable explore view shows every synthetic FinchNode demo patient as a case card: name, age, presenting story and chart highlights from `GET /patients` and `GET /patients/:id/brief`. Data is synthetic and labeled as such; the acute presentation is authored fiction.

Cards render actual service states. `needs_review` charts are playable (the gaps become something the learner must catch). `blocked` (revoked consent) and `retry` (rate limit or upstream down) show the reason and a way onward. The offline `/unity/bundle` may populate the page when the service is unreachable, labeled as offline.

Choosing a card fixes the patient for this run and opens the diagnosis office. A patient without an authored encounter is not playable; requirement: every demo patient has one.

## 3. Diagnosis Office

The learner enters a full-VR doctor's office with the chosen patient. The patient is a voice agent grounded in the FinchNode chart and the authored presentation. The learner takes a history by talking, selects examination maneuvers and orders tests. Findings and results come only from the authoritative encounter state (`/encounters/:id/tools/answer`, `examine`, `order_test`), not from the voice model inventing facts.

Selecting an examination is an authored simulated action, not a measurement of real exam technique. The patient voice does not reveal the diagnosis on request.

## 4. Present and Decide the Surgery

The learner presents to the attending (Jarvis). The learner states the diagnosis, differential, procedure and urgency (`record_assessment`). The deterministic attending scorecard (`/encounters/:id/score`) shows what was gathered, missed and decided.

Recommended handoff: see [experience UX](experience-ux.md#4-diagnosis--surgery-handoff) (commit, one challenge, consequence card, then the correct surgery). Earlier proposal, still open until confirmed: the authored case plan, not the learner's free choice, determines the surgery that runs next. If the learner chose the wrong procedure, the scorecard says so and the OR loads the procedure the patient actually needs. Role changes (patient → attending → surgery coach) disconnect the previous conversation and invalidate pending responses.

## 5. Operating Room

The scene transitions to the full-VR operating room with a virtual patient on the table, scaled to that patient (`bodyScale`), with the case's anatomy, instruments, ports and steps from `GET /patients/:id/case`. Chart risks found in the office (anticoagulation, allergies, incomplete chart) carry into the pre-op check and pinned step considerations.

Jarvis guides the current authored step. Tracked virtual tools and supported UI actions pass through one local dispatcher; one deliberate action produces one accepted transition. Wrong-tool/contact feedback follows the authored rubric. Controller tracking loss releases tools. Voice/network failure preserves local pause and the attempt; recovery resynchronizes confirmed state.

Recording of raw passthrough video starts explicitly for a defined surgery segment, with capture, calibration and clock metadata plus a separate virtual scene timeline. The headset renders full VR while the camera records the learner's real hands; raw images contain no virtual tools or organs. Rendered VR footage is never substituted as motion input.

## 6. Robot Replay (Required)

Every run ends with the recorded segment processed into estimated hand motion and replayed on the simulated robot hand. The gateway verifies the private clip, binds its manifest to the same attempt and queues a deduplicated job. The worker processes, heartbeats and uploads compatible outputs. The headset and companion show queued/running/failed/replay-ready honestly, then play the actual derived robot replay with validity labels.

Missing observations remain explicit. A failed reconstruction still ends the run with a clear reason; it does not erase the diagnosis and surgery feedback. Replay is kinematic and does not establish robot contact physics or a learned autonomous policy.

## 7. Recap and Repeat

The recap shows the diagnosis scorecard, surgery feedback (completed steps, mistakes, hints) and replay status as separate results. Feedback does not claim clinical competence. "Choose another patient" returns to the explore page and starts a new run; a retry starts a new attempt with reset targets and held tools.

## Judge-Facing Proof

Rehearse one actual physical-headset journey: launch, explore, a live voice diagnosis, the matching surgery and a replay from a real permitted clip, with a companion observer. Repeat with a second patient leading to a different procedure. Label asynchronous processing and component-only evidence clearly. The [shipping checks](system-integration.md#shipping-checks) define what must pass.
