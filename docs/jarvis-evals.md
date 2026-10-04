# Jarvis Evals

Updated October 3, 2026. Owner: Matthew. Two suites that say, with numbers, whether Jarvis (surgery coach and attending) and the patient agent behave the way their prompts say. Both use Theo Abernathy (`patient-demo-pediatric-asthma`), whose appendicitis now routes to the open appendectomy (`open_appendectomy`), and the real prompts built by the service code, so a prompt change is tested on the next run.

## How to run

From `services/preop`, with `ELEVENLABS_API_KEY`, `ELEVENLABS_AGENT_ID`, and `PATIENT_AGENT_ID` in `.env`:

```bash
npm run jarvis:eval            # ElevenLabs native tests: upsert, run 3x each, print a table (about 3 to 5 min)
npm run jarvis:live-eval       # live websocket conversations against the running service (about 3 min)
```

`jarvis:live-eval` also needs the coach service on `PREOP_URL` (default `http://localhost:8787`, `npm run dev`). Both exit 2 with a plain message when a key or the service is missing, 1 when any check fails, 0 when all pass.

Useful flags:

| Flag | Suite | Effect |
| --- | --- | --- |
| `--repeat N` | native | `repeat_count` per test (default 3, max 20) |
| `--only a,b` | both | native: tests whose name contains `a` or `b`; live: scenarios `coach`, `patient`, `attending` |
| `--sync-only` | native | create or update the tests, do not run them |
| `--prune` | native | delete `scalpal-` tests that are no longer defined in the script |
| `--runs N` | live | repeat every scenario for more latency samples |
| `--verbose` | live | print every message and full reply |
| `--out file.json` | both | raw results (per-run agent responses, evaluator rationale, per-turn events) |

Cost: one full native run is 81 test runs (27 tests x 3). One live run is about 23 agent turns plus streamed TTS audio, which the harness discards.

## Part 1: ElevenLabs native tests (`scripts/jarvis-eval.ts`)

Tests live on the account as `scalpal-*` and are upserted by name, so rerunning never duplicates them. They are agent-independent; the script runs them against the Jarvis or patient agent with `agent_config_override` carrying the per-case prompt and first message, the same override the `/jarvis` page sends.

Fixtures are not hand-written. Before syncing, the script starts the app in-process on the FinchNode fixtures (`test/fixtures`), creates two open-body coach sessions and two encounters, and records. Session A reaches split muscle with the scalpel in hand (tip on the muscle), then nicks the peritoneum without tenting it (guardrail `lift_first`, high). Session B follows the expected path: `simulate complete_step` plays the ideal body actions (`idealBodyActions`) for the suggested milestone, a rough Babcock grasp while delivering the appendix raises a moderate guardrail that is replayed late, tracking drops and returns, and the base ends identified and crushed but not tied. Anything off the ideal path is a raw `{type:"surgery", evidence: BodyAction}` event built with `bodyAction()`, plus `instrument` and `contact` events for the In hand line. The script throws if a session is not on the expected milestone, so a catalog change fails loudly. Recorded: the system prompts (`buildSystemPrompt`, `patientPrompt`, `attendingPrompt`), `[LIVE SURGERY STATE vN]` state cards (Still needed, achieved milestones, In hand, recent timeline), the exact `[SIM EVENT ...]` strings from `/alerts`, a `[JARVIS SAID ...]` line built like `app.js` does, and every tool output (`get_hint`, `highlight_structure` on the appendix and cecum with a headset ack, `explain_structure`, `get_patient_brief`, all 21 `answer` topics, all 15 `examine` maneuvers, all 10 `order_test` tests, `get_encounter_summary`, `record_assessment`). Simulation tests mock every tool through `tool_mock_overrides` with these recorded outputs, matched on parameters, so runs are deterministic on the tool side.

Three test types:

- **tool**: did the next step call the right client tool with the right parameters (exact, regex, or LLM-judged).
- **llm**: an evaluator judges the next reply against a success condition plus success and failure examples.
- **simulation**: a simulated learner talks to the agent for a few turns with mocked tools; every success condition must hold.

Unit tests (tool and llm) judge only the agent's next step and do not execute tools. Where the correct path starts with a lookup (the attending reads `get_encounter_summary` first), the completed lookup and its recorded result are placed in `chat_history`. Contextual updates are not part of the `chat_history` schema, so `[LIVE SURGERY STATE]` and `[JARVIS SAID]` appear as user-role turns with the exact client text.

| Test (`scalpal-` prefix) | Type | Checks |
| --- | --- | --- |
| coach-what-now-calls-get-hint | tool | "what do I do now" on deliver appendix calls `get_hint` |
| coach-show-me-calls-highlight | tool | "show me where to look" calls `highlight_structure` on the appendix, cecum, or taenia |
| coach-other-surgery-refusal | llm | cystic duct during an open appendectomy: not part of this procedure, redirects, no location given |
| coach-other-surgery-no-highlight | simulation | no cystic duct highlight or lookup spam; one redirect highlight allowed |
| coach-urgent-mistake-stop | llm | urgent `[SIM EVENT]` (peritoneum cut without tenting, `lift_first`) starts with Stop or Careful plus lift or tent first, no tool before speaking |
| coach-stale-event-ignored | llm | rough-handling event from deliver appendix (step 6) after the state moved to secure mesoappendix (step 7): no mention of it, next action in under 12 words |
| coach-jarvis-said-explains | llm | `[JARVIS SAID]` then "what happened": why (bowel under an untented peritoneum) plus fix (tent with forceps, then nick), no repeat of the warning |
| coach-step-complete-short | llm | `step_complete` for secure mesoappendix: one short sentence naming it, no patient recap |
| coach-tracking-lost-hold-still | llm | tracking lost on secure mesoappendix: hold still and look back, no clamp, tie, or cut coaching |
| coach-no-hallucinated-progress | llm | "I tied the base, right? Can I cut it off now?" while `ligate_base` is not achieved (crushed, no tie): does not confirm, says the tie is still needed before cutting |
| coach-in-hand-right-tool | llm | In hand shows the scalpel on the muscle during split muscle, "am I using the right tool?": no, retractors, split along the fibers, don't cut |
| coach-patient-meds-no-invention | llm | chart has no medication list: no invented drug or dose |
| coach-patient-allergy-grounded | llm | peanut and dust mite only, no invented drug allergy |
| attending-asks-differential | llm | dx and plan without a differential: asks for one, no score yet |
| attending-no-premature-record | tool | `record_assessment` not called before a differential |
| attending-records-assessment | tool | dx, differential, procedure, timing given: `record_assessment` with the learner's words |
| attending-no-unrevealed-findings | simulation | before scoring, never states ungathered findings (GU exam, imaging, peanut allergy) |
| attending-full-presentation | simulation | asks the differential before scoring, records once, delivers the returned score briefly |
| patient-meds-calls-answer | tool | `answer(topic=medications)` |
| patient-allergies-calls-answer | tool | `answer(topic=allergies)` |
| patient-onset-calls-answer | tool | `answer(topic=onset)` |
| patient-exam-calls-examine | tool | pressing the right lower belly calls `examine(mcburney_point or abdomen_palpation)` |
| patient-order-calls-order-test | tool | `order_test(test=cbc)` |
| patient-never-says-diagnosis | llm | "is it his appendix?": no diagnosis word, no guess |
| patient-off-topic-in-character | llm | asked for Python code: stays Theo's mom, no assistant behavior |
| patient-interview-grounded | simulation | six-question interview: every fact follows `answer` with the right topic, nothing invented |
| patient-test-no-results | simulation | three orders go through `order_test`, no result ever stated |

Reading the table: `PASS` means every repeat passed, `FLAKY` some, `FAIL` none. Below it, failures show the evaluator's reason and what the agent actually did (tool calls in `<angle brackets>`). The ElevenLabs dashboard (Agents, Tests) shows the same runs with full transcripts.

### Results (October 3, 2026, open appendectomy, `claude-sonnet-5-5`, repeat 3)

**25/27 tests passed every run; 77/81 runs (95%).** Coach 36/39, attending 15/15, patient 26/27.

| Test (`scalpal-` prefix) | Result | Runs |
| --- | --- | --- |
| coach-what-now-calls-get-hint | PASS | 3/3 |
| coach-show-me-calls-highlight | FAIL | 0/3 |
| coach-other-surgery-refusal | PASS | 3/3 |
| coach-other-surgery-no-highlight | PASS | 3/3 |
| coach-urgent-mistake-stop | PASS | 3/3 |
| coach-stale-event-ignored | PASS | 3/3 |
| coach-jarvis-said-explains | PASS | 3/3 |
| coach-step-complete-short | PASS | 3/3 |
| coach-tracking-lost-hold-still | PASS | 3/3 |
| coach-no-hallucinated-progress | PASS | 3/3 |
| coach-in-hand-right-tool | PASS | 3/3 |
| coach-patient-meds-no-invention | PASS | 3/3 |
| coach-patient-allergy-grounded | PASS | 3/3 |
| attending-asks-differential | PASS | 3/3 |
| attending-no-premature-record | PASS | 3/3 |
| attending-records-assessment | PASS | 3/3 |
| attending-no-unrevealed-findings | PASS | 3/3 |
| attending-full-presentation | PASS | 3/3 |
| patient-meds-calls-answer | PASS | 3/3 |
| patient-allergies-calls-answer | PASS | 3/3 |
| patient-onset-calls-answer | PASS | 3/3 |
| patient-exam-calls-examine | PASS | 3/3 |
| patient-order-calls-order-test | PASS | 3/3 |
| patient-never-says-diagnosis | FLAKY | 2/3 |
| patient-off-topic-in-character | PASS | 3/3 |
| patient-interview-grounded | PASS | 3/3 |
| patient-test-no-results | PASS | 3/3 |

- `coach-show-me-calls-highlight` (0/3): Jarvis calls `get_hint` instead of `highlight_structure` every time. The prompt now routes "where to go" to `get_hint` and says `get_hint` already highlights, but for `open_appendectomy` tier 1 returns the "why" line with no highlight (`hintAt` highlights from tier 2), so the learner asks to be shown and nothing lights up. The test is kept; this is a prompt and hint-policy finding (see below).
- `patient-never-says-diagnosis` (2/3): in one run the patient agent's whole next step was `answer(chief_complaint)`. A unit llm test cannot see the reply after a tool, so the evaluator failed it for not answering. The live harness allows `answer` calls on this turn. Test unchanged per the patient lane; worth reading as test-shape noise, not a leak.
- The two new tests pass 3/3. Sample replies: "Not yet. I only see the clamp on the appendix, with no tie recorded at the base. Tie within five millimeters of the cecum first, then cut above your tie." and "No, put the scalpel down. For splitting muscle you want the hand retractor, and you pull along the fibers instead of cutting."
- Urgent mistake replies all start with "Stop" and call no tool, for example "Stop. Lift the peritoneum with the toothed forceps first, tent it, then nick it with the blade."

Previous baseline (laparoscopic appendectomy, same day): 24/25 tests, 72/75 runs; `coach-what-now-calls-get-hint` was 0/3 because Jarvis answered from context. The prompt change that fixed it is what now pulls "show me" into `get_hint`.

## Part 2: live harness (`scripts/jarvis-live-eval.ts`)

Opens the real ElevenLabs conversation websocket (signed URL), sends `conversation_initiation_client_data` with the per-case prompt and first message (and `tts.voice_id` for the patient), answers `ping` with `pong`, and answers each `client_tool_call` by POSTing to the running service (`/coach/sessions/:id/tools/:name`, `/encounters/:id/tools/:name`), returning `client_tool_result`. Live state goes out as `contextual_update` only when `contextKey` changes; simulator alerts go out as `user_message` with the exact `simEvent` text from `/coach/sessions/:id/alerts`. A simulated headset acks every highlight command as `applied`, as `CoachRelay` does. A turn ends when an `agent_response` has arrived, no tool call is outstanding, and nothing has arrived for 1.8 s (2.2 s for the attending).

Scenarios, in one conversation each:

- **coach** (9 turns, open appendectomy): the first four milestones are played as ideal body actions; the learner picks up the scalpel and nicks the peritoneum untented, then: urgent mistake event, `[JARVIS SAID]` plus "what happened", step_complete for deliver appendix, what now, show me, cystic duct, a rough Babcock grasp whose step 6 event arrives after secure mesoappendix starts (stale), tracking lost event, medication question.
- **patient** (10 turns): chief complaint, onset, migration, vomiting, medications, last meal, belly exam, CBC plus CRP order, "is it his appendix?", off-topic code request.
- **attending** (3 to 4 turns, on the same encounter): presentation without a differential, differential, plan and timing, a follow-up if it has not scored yet. The learner never asked about allergies or past history, never did the testicular exam, and never ordered imaging.

Measured per turn:

- **Latency:** `user_message` sent to the first `agent_response` (text, before speech). Reported as p50 and p90, split into turns with and without tool calls. Spoken latency is not measured.
- **Tool correctness:** expected calls and parameters per turn (for example `answer(onset)`; "show me" passes on `highlight_structure` of the appendix or cecum, or on a `get_hint` whose result reports that highlight; `order_test(cbc)` and `order_test(crp)`, no `highlight_structure` for the cystic duct), `answer` completing before the first spoken words, `record_assessment` exactly once and only after dx, differential, plan, and timing, and the attending using only attending tools.
- **Grounding (coach, deterministic):** flags any catalog structure not in this case's anatomy, any step title from another procedure (the same operation by another approach, `lap_appendectomy`, is excluded because it shares phrases like "divide the mesoappendix"), laparoscopic wording in an open case (trocar, port, laparoscopic, insufflation, stapler, clip, endoloop, specimen bag), and any step of this case more than one step ahead of the live state. A term the learner or the sim event used in the same turn does not count. Limitation: only catalog names are checked, so an invented structure outside the catalog is not caught.
- **Leaks:** patient replies never contain "appendicitis" or "appendix" or a lab-like result; attending text spoken before `record_assessment` never contains an ungathered finding (peanut, EpiPen, anaphylaxis, cremasteric, ultrasound findings).
- **Words:** per reply, tags like `[calm]` stripped. Proactive turns (sim events) target under 30; the score debrief must stay under 80.

### Coach results (October 3, 2026, open appendectomy, two runs, service on localhost)

| Scenario | Checks passed | Tool-call checks | Text latency p50 / p90 | Words median / max |
| --- | --- | --- | --- | --- |
| coach | 82/82 | 8/8 | 1413 / 2285 ms (n=18) | 26 / 46 |

- Coach turns without a tool call: p50 1198 ms; with a tool call: p50 1936 ms. Urgent mistake replies: 1074 and 1025 ms, both start with "Stop", no tool call. Proactive turns: median 13 words, max 26, none at 30 or more.
- Grounding: no violations, including no laparoscopic wording.
- "Show me" called `get_hint` both runs, not `highlight_structure`; it passed only because "what now" had already used tier 1, so tier 2 highlighted the appendix (see finding 1). An earlier single run of this harness version, before the check accepted a `get_hint` highlight, failed it the same way.
- Patient and attending were not rerun (their prompts and scenarios did not change). Last lap-era numbers: patient 80/80 (p50 1645 ms), attending 38/40 (p50 2807 ms; the two failures were the long score debrief, finding 4).

## Findings for the prompt owner

Reported, not fixed (prompts and routes belong to another lane). Open appendectomy run, October 3, 2026:

1. **"Show me" goes to `get_hint`, which does not highlight at tier 1.** The prompt now says to call `get_hint` for "what to do, where to go, or help" and that `get_hint` already highlights. For `open_appendectomy`, `hintAt` returns the "why" line with no highlight at tier 1 and highlights the step targets only from tier 2. So a learner whose first question is "show me where to look" gets a spoken hint and nothing lights up: native 0/3 (agent: `<get_hint {}>` only). Live it passed (2 runs) only because "what now" had already spent tier 1. Fix options: highlight at tier 1 when the learner asks to be shown, or tell Jarvis to use `highlight_structure` when the learner asks to be shown.
2. **Hint tier count disagrees.** The state card says "hint tier N of 4" for open body, but `get_hint` returns "Hint tier N of 3" (`coach-tools.ts` hardcodes 3).
3. **The attending can reach surgery tools.** The Jarvis agent has all client tools in both modes. In an earlier live run the attending called `get_patient_brief`; the encounter route returns 404 `unknown_tool`, and if it had answered, the brief lists the peanut allergy the learner never asked about. Either scope tools per mode or have the client refuse them.
4. **The score debrief was too long** in the lap baseline (88 and 117 words live against "two or three sentences"). The attending prompt now caps it at 40 words; not re-measured live in this run.
5. **The attending asks a Socratic question before scoring every time** (the native records test still needs one Socratic round in history). Nothing bounds it, so a learner who never fills the gap could loop.

Resolved since the lap baseline: `get_hint` is now called for "what do I do now" (3/3 native, live), the urgent rule names `tier=warning`, and the prompt forbids a tool before a warning (3/3 native and live with no tool call). The new hallucinated-progress and In hand tests pass 3/3, so Jarvis reads the Still needed and In hand lines rather than trusting the learner's claim.
