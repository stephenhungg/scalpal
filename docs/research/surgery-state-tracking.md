# Surgical State Tracking for Scalpal's Coach and Grader

Research date: 2026-10-03. Scope: how to engineer procedure-state tracking for an open-body VR/AR sim so that Scalpal (ElevenLabs conversational agent) and the grader get accurate, low-latency context from ground-truth sim state, not video.

Legend: **[S]** = sourced claim (citation follows). **[R]** = my recommendation or inference. **[C]** = observation from the Scalpal code I read (`services/preop/src/open-body.ts`, `engine.ts`, `coach.ts`, `jarvis/app.js`, `docs/surgery-procedure.md`, `docs/data-and-realtime.md`). No repo was edited.

---

## 0. Where Scalpal already stands (code read)

- [C] `BodyState.apply()` is already a deterministic reducer over `BodyAction` events. It rejects invalid, unregistered, duplicate (`actionId` seen) and out-of-order (`timeMs < clock`) actions, then mutates a flat `tissueId:fact -> number` map and appends an immutable `{action, outcomes}` record. Outcomes are case-agnostic (`not_exposed`, `cut_unsecured`, `hollow_leak`, `untented_cut`, `across_fibers`, `critical_injury`, `rough_handling`, ...).
- [C] `StepEngine.handleBody()` matches guardrails against per-action outcome strings, re-evaluates *all* milestones as predicates after every action, records order deviations as soft data, and sets `current` to the first unsatisfied milestone, so a new bleed can un-achieve "no active bleed."
- [C] `CoachSession` wraps the engine with coaching state (time on step, seconds since progress, stuck tiers 15/25/40/60 s, held instruments, focus, tracking validity, timeline of the last 20 lines). It produces alerts in three tiers (warning = pre-rendered reflex clip, caution = queued LLM turn, advisory = context only), following FAA AC 25.1322-1.
- [C] `renderContext()` builds a text block tagged `[LIVE SURGERY STATE vN]` with an explicit "only report measured facts" line. `contextKey()` hashes only the semantic fields, so ticking timers do not cause a resend. `jarvis/app.js` debounces 300 ms and calls `convo.sendContextualUpdate(fullText)` when the key changes. Alerts go out through `sendUserMessage("[SIM EVENT vN] ...")`, and reflex clips are reported back as `[JARVIS SAID vN]` contextual updates. `get_surgery_state` and `get_hint` exist as tools.

The architecture is already the right shape. The gaps below are refinements.

1. [C] **Blood loss integrates only when the next action arrives.** `apply()` computes `dt` from the previous action's `timeMs`. If the learner freezes while a vessel bleeds, `bloodLostMl` and `poolMl` stay put until they act again, so during a stall the coach sees stale (low) blood loss. The fix is in §2.
2. [C] **There are two bleeding sources.** The legacy `CoachEvent {type:"bleeding", rateMlPerMin, totalMl}` from Unity's `NativeVesselSimulation` and the open-body reducer's `flowMlPerSecond` both write `bloodLossMl` and `bleeds`. Per Rule 7, pick one: for open-body cases the reducer should be authoritative, and the vessel sim should be visual only or feed `BodyAction`s. Flag the other path for cleanup.
3. [C] **The `Body facts:` line dumps every fact.** Every `tissue:fact=value` pair goes into each contextual update, and each update is resent in full on every semantic change. This grows the conversation history quickly. See §3.
4. [C] **Guardrails can only see single-action outcomes.** That covers most cases. Window guardrails such as "blade below peritoneum without tenting in the last N s" or "bleeding uncontrolled > 30 s" need a small temporal layer. See §2.

---

## 1. Surgical process modeling and workflow recognition

### What the literature defines

- [S] **Surgical process models (SPMs).** Lalys & Jannin's review defines an SPM as a simplified, formal or semi-formal representation of a surgical procedure. It is organized by granularity from coarse to fine: procedure > phase > step > activity > motion (and low-level signals). An *activity* is formalized as a tuple of action verb, instrument and anatomical target, plus actor and time. They also note that data can come from human observation, sensors or video. ([Lalys & Jannin 2014, IJCARS, PubMed 24014322](https://pubmed.ncbi.nlm.nih.gov/24014322/); [HAL full text](https://inserm.hal.science/inserm-00926470/document))
- [S] Neumuth et al. proposed structured recording of intraoperative workflows at several granularity levels, with activities, events and states as components. Later work defined similarity metrics for comparing SPMs along granularity, content, time, order and frequency. These are directly reusable as grader dimensions. ([Neumuth et al. 2006, SPIE](https://ui.adsabs.harvard.edu/abs/2006SPIE.6145...54N/abstract); [Neumuth et al. 2012, Artif Intell Med](https://www.iccas.de/wp-content/uploads/2012/03/Neumuth-et-al.-2012-Artif-Intell-Med.pdf))
- [S] **Ontologies.** OntoSPM is a shared vocabulary for surgical actions, instruments, actors and roles, built on the BFO upper ontology. LapOntoSPM extends it for laparoscopy and was used for phase recognition. ([Gibaud et al. 2018, IJCARS](https://link.springer.com/article/10.1007/s11548-018-1824-5); [OntoSPM site](https://ontospm.univ-rennes.fr/ontology))
- [S] **The SAGES annotation consensus** standardizes a surgical-video hierarchy of phases, steps, tasks and actions, plus events such as bleeding. ([Meireles et al. 2021, Surg Endosc, PubMed 34231065](https://pubmed.ncbi.nlm.nih.gov/34231065/))
- [S] **Action triplets.** CholecT50 annotates `<instrument, verb, target>` triplets (6 instruments, 10 verbs, 15 targets, 100 valid triplet classes) on laparoscopic cholecystectomy video. Rendezvous recognizes them with attention. ([Nwoye et al. 2022, Med Image Anal](https://www.sciencedirect.com/science/article/abs/pii/S1361841522000846); [code](https://github.com/CAMMA-public/rendezvous); [schema summary, arXiv 2511.00643](https://arxiv.org/html/2511.00643v1))
- [S] **Gestures and kinematics.** JIGSAWS provides 76-dimensional da Vinci kinematics at 30 Hz, labeled with a 15-element gesture vocabulary and global skill ratings. It is the canonical precedent for recognizing workflow and skill from tool motion rather than pixels. ([Gao et al. 2014, JIGSAWS](https://www.semanticscholar.org/paper/JHU-ISI-Gesture-and-Skill-Assessment-Working-Set-(-Gao-Vedula/efe03a2940e09547bb15035d35e7e07ed59848bf))
- [S] **OR digital twins and scene graphs.** 4D-OR represents the OR as semantic scene graphs (actor, relation, object) from RGB-D (macro F1 0.75). Twin-S mirrors tools, anatomy and cameras in a real-time simulation using optical tracking, so drilling state is known from simulation instead of being inferred from video. ([Özsoy et al. 2022, MICCAI](https://conferences.miccai.org/2022/papers/003-Paper1512.html); [Shu et al. 2023, Twin-S, IJCARS](https://pubmed.ncbi.nlm.nih.gov/37160583/), [arXiv](https://arxiv.org/html/2211.11863v2))
- [S] Surgical data science frames all of the above as the data foundation for context-aware assistance. ([Maier-Hein et al. 2017, Nat Biomed Eng](https://www.nature.com/articles/s41551-017-0132-7))

### What transfers when you have ground-truth sim state

- [R] **Keep the vocabulary, drop the recognizers.** Video systems spend almost all of their effort *estimating* triplets and phases with uncertainty. In a sim, a `BodyAction` already *is* a triplet (`instrumentId`, `verb`, `tissueId`) plus measured geometry. That is Lalys & Jannin's activity level with exact timing. Phases and steps become **deterministic predicates over the projected state**. That is what Scalpal's milestones are, and it is strictly better than a classifier.
- [R] **Map the levels explicitly** so the coach can speak at the right altitude:
  - Procedure = case.
  - Phase = a group of milestones: access (mark, skin, fascia, split, peritoneum), mobilization (deliver), vascular control (mesoappendix), base (decision, crush, tie, cut), check/close.
  - Step = milestone.
  - Activity = `BodyAction` triplet.
  - Motion = raw tool poses (do not send these to the LLM).

  Adding a `phase` field to each milestone is cheap and gives Scalpal a stable coarse label ("you're in vascular control").
- [R] **What still needs inference.** Even with ground truth, three things remain judgment calls:
  1. **Intent.** Is the learner hovering near the cecum exploring, or about to cut it? Use proximity, hand speed and instrument state, not the LLM.
  2. **Quality.** Economy and tissue respect come from motion metrics (§4).
  3. **AR registration validity.** The world model is only true if the overlay is registered.

  Neumuth's similarity metrics (order, time, frequency) give the grader a principled way to compare the learner's activity sequence against the expected path without gating it, which matches the "soft order" design.
- [R] **Adopt CholecT50-style closed vocabularies** for verbs, targets and outcomes, and keep them as enums in data. This is how video-trained models and annotations could later be aligned with sim logs. It also keeps the LLM's input vocabulary small and unambiguous.

---

## 2. Engineering patterns

### Event sourcing, projections, determinism

- [S] Event sourcing stores every state change as an immutable event. The current state is derived by replaying the events, and the store is the system of record. ([Fowler, Focusing on Events](https://martinfowler.com/eaaDev/EventNarrative.html); [Microsoft, Event Sourcing pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing))
- [S] It is commonly paired with CQRS and **materialized views (projections)**. These are read models tailored to one query, fully disposable and rebuildable from the source, which makes them effectively a specialized cache. ([Microsoft, Materialized View](https://learn.microsoft.com/en-us/azure/architecture/patterns/materialized-view))
- [S] Deterministic lockstep sends only *inputs*, and every peer runs the same deterministic simulation. Bandwidth scales with input size, not state size. Floating-point determinism across platforms is the hazard. ([Fiedler, Deterministic Lockstep](https://gafferongames.com/post/deterministic_lockstep/); [Floating Point Determinism](https://gafferongames.com/post/floating_point_determinism/))

[R] Recommended layering for Scalpal (most of it already exists):

| Layer | Content | Owner | Consumers |
|---|---|---|---|
| L0 raw motion | Tool poses at 72–90 Hz, contacts | Unity only, never networked raw | Motion-metric accumulators |
| L1 semantic events | `BodyAction` (triplet + geometry + `actionId` + monotonic `timeMs`), plus `tick`, `tracking`, `held`, `focus`, `hint_given`, `alert_spoken` | Unity emits; append-only | Reducer, replay, grader |
| L2 body projection | `BodyState.facts` (reducer output) | Deterministic reducer, run identically in Unity (CaseRunner.cs) and TS | Milestones, guardrails |
| L3 case projection | Achieved milestones, current/next, phase, guardrail hits, order deviations, decisions | Case evaluator (pure function of L2 + L1) | Coach, grader, UI |
| L4 coach projection | Salient, versioned, compact view (§3) | Coach service | Scalpal |
| L5 grade | Final scorecard | Pure function over the full L1 log | Recap LLM (writes prose only) |

Specific engineering points:

- [R] **Idempotency and ordering.** Keep `actionId` dedup and monotonic `timeMs`. Add an `eventSeq` (a per-session monotonically increasing integer assigned by Unity) so consumers can detect gaps ("I have seq 41, got 43, request 42"). This mirrors the command-number scheme Gambetta describes, where the server tells the client the last processed input so it can reconcile ([Gambetta, Client-Side Prediction and Server Reconciliation](https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html)). Today an out-of-order action is silently dropped (`apply` returns `null`). Surface it as a counted `rejected_out_of_order` diagnostic rather than dropping it silently.
- [R] **Time-driven state needs a tick event.** Bleeding, pool growth, `activeBleedSeconds` and stall detection depend on elapsed time, not just actions. Emit a synthetic `{verb:"tick", timeMs}` event at 1 Hz (or whenever any bleed is active) into the same reducer. That keeps the log replayable and deterministic, unlike reading a wall clock inside the reducer, and fixes gap 1 above. An alternative is to compute `bloodLost(now)` lazily in the projection, but then replay must pass `now` explicitly.
- [R] **Temporal guardrails.** Add a tiny windowed-predicate layer that reads L1 and L2: `{fact, op, value, forMs}` (for example `"":activeBleeds gte 1 for 30000`) and `{outcome, withinMs, after: outcome}`. Evaluate it on every event, including ticks. This keeps guardrails as data.
- [R] **Snapshots.** A 5-minute session is a few hundred semantic events, so replay from zero is cheap and you do not need snapshots for performance. Use them only for:
  1. **Reconnect.** Send `{schemaVersion, caseVersion, eventSeq, facts}` so the server can verify its replay hash.
  2. **Desync detection.** Unity and TS both compute a hash of the facts after each event. A mismatch should set `desynced`. That flag already exists, but today it compares step IDs. Comparing a fact hash is stronger.
- [R] **Versioning.** Stamp every event with `schemaVersion`, and every session with `caseVersion` (already on `OpenBodyCase.version`) and `reducerVersion`. The grader must replay with the reducer version that produced the session, or old logs will re-grade differently. Treat the TS reducer as the spec and keep the Unity port honest with shared golden fixtures. You already have the 65 Unity synthetic assertions. Run the *same* JSON fixture logs through both and compare fact hashes in CI.
- [R] **Floating point.** The reducer is cheap arithmetic. Keep it in integers or rounded mm and ml at the event boundary, so that C# and JS produce identical hashes. Fiedler's warning applies.

### Where authority lives

- [S] SpacetimeDB reducers are transactional: atomic, isolated, rolled back on error. They are the only mutation path, and subscriptions stream row deltas to clients. Reducers cannot do network or file I/O. ([SpacetimeDB reducers](https://spacetimedb.com/docs/functions/reducers/); [Transactions and atomicity](https://spacetimedb.com/docs/databases/transactions-atomicity/); [Subscription semantics](https://spacetimedb.com/docs/subscriptions/semantics/)). This matches the repo's `docs/data-and-realtime.md`.
- [S] Client-side prediction exists because a round trip through an authoritative server adds visible input latency. ([Gambetta](https://www.gabrielgambetta.com/client-side-prediction-server-reconciliation.html))
- [R] **Unity is authoritative for body state, and the server is authoritative for the shared record.** Physical consequences (bleeding visuals, `not_exposed` blocking, reflex alarms) must not wait on a network round trip, and the sim must work offline or in airplane-mode demos. Unity therefore runs the reducer locally and is the sole *producer* of L1 events. The server (TS coach or SpacetimeDB module) re-runs the same reducer as a *verifier* and projector. It never invents body events. This is lockstep in spirit: ship inputs (`BodyAction`s), and every party derives identical state.
- [R] **Mapping onto SpacetimeDB.** Use these tables:
  - `session_event` (append-only, private; written via a reducer that checks `eventSeq == last+1` and dedups `actionId`).
  - `session_state` (one row per session: `version`, `eventSeq`, `factsHash`, phase, current milestone, active bleeds, stuck level). This is the small row the companion, observer and coach subscribe to.
  - `coach_alert` (with `stateVersion`, so stale alerts are dropped).

  Optionally the module runs the reducer itself to compute `session_state`, which works because reducers are pure and transactional. Do **not** subscribe broad clients to the raw event table. The repo doc already notes that a filtered subscription is not a security boundary.
- [R] **Offline.** Unity buffers L1 events with `eventSeq` and flushes them on reconnect. The server replays them, and the coach resumes from the verified state. During an outage Scalpal can still play local reflex clips. The LLM simply misses context for those seconds.

---

## 3. Feeding the LLM agent structured state

### ElevenLabs specifics (documented)

- [S] **Contextual updates** (`contextual_update` / SDK `sendContextualUpdate`) are *non-interrupting background information*. ElevenLabs advises sending concise, relevant info, avoiding overwhelming the LLM, and **grouping multiple small changes into a single update**. ([ElevenLabs, Client-to-server events](https://elevenlabs.io/docs/eleven-agents/customization/events/client-to-server-events))
- [S] **User messages** (`sendUserMessage`) are processed as if the user spoke them and *trigger a response*. **User activity** resets the turn-timeout timer without adding content. (same page)
- [S] **Client tools** are registered in client code. With "Wait for response" enabled, the agent waits and appends the tool result to the conversation context. Tool results can also assign dynamic variables. ([ElevenLabs, Client tools](https://elevenlabs.io/docs/eleven-agents/customization/tools/client-tools))
- [S] **Dynamic variables** (`{{var}}`) template the system prompt, messages and tools. They are set at conversation start, and tool calls that return a valid JSON object can create or update them. `system__*` variables include `system__time_utc`, `system__call_duration_secs`, `system__agent_turns` and a lazily evaluated `system__conversation_history`. ([ElevenLabs, Dynamic variables](https://elevenlabs.io/docs/eleven-agents/customization/personalization/dynamic-variables); [Personalization](https://elevenlabs.io/docs/eleven-agents/customization/personalization))
- [S] **Conversation flow.**
  - Turn timeout ("take turn after silence") is configurable from 1 to 30 s.
  - Soft timeout speaks a filler phrase if the LLM is slow (default 3 s, range 0.5–8 s).
  - Interruption must be enabled as a client event.
  - `agent_response_complete` fires once, after LLM generation, tool chains and audio playback all finish.

  ([ElevenLabs, Conversation flow](https://elevenlabs.io/docs/agents-platform/customization/conversation-flow); [Client events](https://elevenlabs.io/docs/agents-platform/customization/events/client-events))

### Context-packing best practices

- [S] Find "the smallest set of high-signal tokens that maximize the likelihood of your desired outcome." Keep context informative but tight, and prefer just-in-time retrieval through tools over pre-loading everything. ([Anthropic, Effective context engineering for AI agents](https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents))
- [S] Tools should be token-efficient, unambiguous and non-overlapping, and should return meaningful, high-signal fields rather than raw IDs and dumps. ([Anthropic, Writing effective tools for agents](https://www.anthropic.com/engineering/writing-tools-for-agents))
- [S] LLMs use information at the start and end of the context best and degrade for information in the middle. ([Liu et al. 2024, "Lost in the Middle", TACL](https://aclanthology.org/2024.tacl-1.9/))

[R] Concrete design for Scalpal:

1. **Push a small, salient "state card," and pull detail through a tool.** Every contextual update should be ≤ ~120 tokens with a fixed order, so that the most important line is first and the "now" line is last (Liu et al.):
   ```
   [STATE v57 seq=212 t=3:41] phase=vascular_control milestone=6/10 "Divide the mesoappendix"
   HAZARD: bleeding appendiceal_artery 18 ml/min, 2 s, pool 9 ml (none controlled)
   Next expected: clamp x2 -> cut between -> tie both. Done here: clamps 1/2.
   Hands: L babcock (holding appendix, lift 18 mm)  R metzenbaum (touching mesoappendix)
   Stalled 12 s, hint tier 1/4. Errors this step: cut_unsecured.
   Last: 3:38 scissors cut mesoappendix (started bleeding); 3:30 hemostat clamped mesoappendix
   ```
   Replace the full `Body facts:` dump with **only facts referenced by the current and next milestone predicates, plus active hazards**. The milestone predicates already tell you which facts are salient. The full fact table stays behind `get_surgery_state(detail="facts")`.
2. **Deltas vs full state.** Contextual updates accumulate in the transcript, so sending full state on every change makes the history grow and the stale copies compete with the new one. Send the **full card** on phase or milestone change and every ~10 s. In between, send **one-line deltas** tagged with the version (`[Δ v58] clamped mesoappendix (clamps 2/2); bleeding stopped`). The system prompt rule should be: "the highest `v` wins; ignore older STATE lines." Batch everything within a 300 ms debounce into one update, which ElevenLabs explicitly recommends. Your `contextKey` gate is already the right mechanism. Keep it, and exclude ticking counters except for threshold crossings (stall tier changes, bleeding > 10 s).
3. **Facts-only grounding.**
   - Keep the explicit rule "only state progress that appears in STATE or a tool result; if unsure, call `get_surgery_state`."
   - Have the card state negatives explicitly ("appendix NOT yet delivered"), because the absence of a fact invites guessing.
   - Have `get_surgery_state` return the same version number, so Scalpal can cite it.
   - Add an eval check that flags any agent utterance naming a milestone not in `achievedMilestones` at that version. You have `jarvis-live-eval.ts`, so extend it.
4. **Tool vs push.**
   - Use **push** (contextual updates) for anything Scalpal must be aware of without being asked.
   - Use **pull** (`get_surgery_state`, `get_hint`, `explain_structure`) for detail and for escalation side effects. A hint tier increments only when actually delivered, which you already do.
   - Use **`sendUserMessage("[SIM EVENT]")`** only for cautions that *should* produce a spoken turn now.
   - Do not rely on dynamic variables for live state. They are best for session-static data (patient, case, mode). Tool results can update them, but that only happens when a tool is called.
5. **Latency budget** (the ~1–2 s LLM+TTS turn is given):
   - **Safety warnings (≤ 150 ms from event to audio):** pre-rendered reflex clip played locally on the Quest, no network. You already do this. Afterwards, a `[JARVIS SAID]` contextual update tells the LLM what was said so it does not repeat it.
   - **Cautions (≤ 2–2.5 s):** state is projected locally (< 10 ms), then one `sendUserMessage`, then the LLM turn. Keep the context small; prompt size is the controllable part of time to first token.
   - **Advisories:** silent context only.
   - **Staleness rule:** drop any queued caution whose `stateVersion` is older than the current milestone or hazard set. Your alert `version` field exists for this.
6. **Interrupts.** Pre-rendered clips should *duck or barge in on* Scalpal audio for warnings. Gate the next LLM turn until `agent_response_complete` so cautions do not stack. Rate-limit spoken cautions (for example, at most one per 6 s unless the tier rises) to avoid alarm fatigue. This is consistent with your FAA-derived tiering.
7. **Grader/recap LLM.** Give it the deterministic scorecard (L5) plus a compressed event narrative. Instruct it to cite event times. It writes prose only and never computes scores.

---

## 4. Derived signals that matter for coaching

[S] Background. Objective psychomotor assessment uses time, errors, path length, economy of movement, number of movements, speed and smoothness from instrument tracking ([Oropesa et al. 2011, J Surg Res](https://pubmed.ncbi.nlm.nih.gov/21924741/); [JIGSAWS](https://www.semanticscholar.org/paper/JHU-ISI-Gesture-and-Skill-Assessment-Working-Set-(-Gao-Vedula/efe03a2940e09547bb15035d35e7e07ed59848bf)). The Virtual Operative Assistant showed that a sim tutor driven by explainable, metric-based feedback is feasible ([Mirchi et al. 2020, PLOS ONE](https://journals.plos.org/plosone/article?id=10.1371/journal.pone.0229596)). Evidence on feedback timing is mixed. Concurrent feedback helped novices on lumbar puncture checklists ([Medical Teacher 2023](https://pubmed.ncbi.nlm.nih.gov/36931315)), while terminal feedback transferred better in colonoscopy simulation ([Walsh et al. 2009, Acad Med](https://academic.oup.com/academicmedicine/article/84/Supplement_1/S54/8353508)). This supports your rule: speak only at milestone start, on stall or on error, and save critique for the recap.

[R] Signal inventory, where it comes from, and where it goes:

| Signal | Derivation | Push to Scalpal? | Grader? |
|---|---|---|---|
| Phase / current milestone / n of N | L3, first unsatisfied milestone + phase label | Yes (card header) | Time per phase |
| Next expected milestone + its unmet predicates in plain words | Evaluate `predicates` that are false and render them ("clamps 1/2", "tie not within 5 mm") | Yes; this is the most useful coaching fact | — |
| Stall time / attempts since progress | Time since the last milestone *or* the last fact change on salient tissues | Only when crossing a tier | Hints used |
| Last N actions (N≈5) | L1 rendered via `describeBodyAction` | Yes (tail of card) | Full log |
| Active hazards | Active bleeds (tissue, rate, duration, pool), contamination, leaking, critical-structure proximity (tip distance < X mm to a `critical` tissue while holding a cutting instrument) | Yes, first line; warnings as reflex clips | Bleed time, blood loss, contamination |
| Instrument in each hand + what its tip touches/holds | Unity contact + held events (exists) | Yes (one line) | Wrong-instrument count |
| Tissue exposure | Deepest opened layer by `order` (derived from `opened` facts) | One token ("exposed to: peritoneum open") | Layer order |
| Errors / guardrail hits | L3, with step context and severity | This step only; earlier ones as count | All, weighted |
| Order deviations | L3 (soft) | Only if asked | Neumuth-style order metric |
| Decisions | `decision_*` facts vs `correctChoice` | When pending | Decision score |
| Hints given / tier | Coach | Yes | Hints used |
| Economy: path length per hand, idle time, movements, mean speed, rough-handling count | Unity accumulators at L0, reported as cumulative per-milestone summaries (not streamed) | No (recap only), except rough handling as a caution | Economy 20%, tissue respect 10% |
| Max tissue stretch / force proxy | Unity per-tissue max | Caution on threshold | Tissue respect |
| AR registration validity | Tracking event + registration residual (landmark error mm) + age of last good registration | Yes; invalid sets PAUSED and suppresses progress talk | Pause scoring (exists); log residual per action (`registered` flag exists) |
| Gaze/focus | Focus event (exists) | Only when relevant to a danger ("looking at the iliac vessels") | — |

[R] Specific suggestions beyond what exists:

1. **Predicate-to-language for the "remaining" field.** Generate "still needed" automatically from false predicates (fact label table plus op/value). That way the coach's guidance is always derived from the same data as the grader.
2. **Critical-proximity hazard.** This is the only hazard that needs continuous geometry. Compute it in Unity (closest-point distance from an active blade or energy tip to `critical` tissue colliders), quantize it (`>20 mm` / `10–20` / `<10`), and emit an event only on band change.
3. **Registration as a first-class state.** In AR, add `registrationErrorMm` and `registrationAgeMs` to the card when degraded. Unity should refuse to emit `registered:true` actions above a threshold. The reducer already ignores unregistered actions.

---

## 5. Prioritized next steps (recommendations)

1. Add a `tick` event and windowed guardrails. This fixes stale blood loss during stalls and enables "bleeding uncontrolled > N s."
2. Make the reducer the single bleeding authority for open-body cases, and retire the parallel `bleeding` CoachEvent path for them.
3. Replace the full `Body facts` dump with milestone-salient facts. Send full cards on milestone or phase change and every ~10 s, and versioned deltas in between.
4. Add `eventSeq` plus a fact hash in both the Unity and TS reducers, shared golden JSON fixtures in CI, and hash-based `desynced`.
5. Add a `phase` label per milestone, and predicate-to-language for the "still needed" field.
6. Extend `jarvis-live-eval` with a hallucinated-progress check: any milestone claim not in state at that version fails.
7. Model `session_state` and `session_event` in SpacetimeDB with Unity as producer and the server as verifier.

## Caveats

- Thresholds (stall tiers, rates, token budgets, the 150 ms reflex budget) are design targets, not measured on Quest 3S.
- I did not verify ElevenLabs' internal handling of how many contextual updates are retained or truncated in the LLM prompt. The docs only advise conciseness and batching.
- The SPM granularity labels vary slightly between authors (SAGES uses phase/step/task/action). Pick one set and keep it consistent.
