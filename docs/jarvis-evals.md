# Jarvis Evals

Updated October 3, 2026. Owner: Matthew. Two suites that say, with numbers, whether Jarvis (surgery coach and attending) and the patient agent behave the way their prompts say. Both use Theo Abernathy's appendectomy (`patient-demo-pediatric-asthma`) and the real prompts built by the service code, so a prompt change is tested on the next run.

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

Cost: one full native run is 75 test runs (25 tests x 3). One live run is about 23 agent turns plus streamed TTS audio, which the harness discards.

## Part 1: ElevenLabs native tests (`scripts/jarvis-eval.ts`)

Tests live on the account as `scalpal-*` and are upserted by name, so rerunning never duplicates them. They are agent-independent; the script runs them against the Jarvis or patient agent with `agent_config_override` carrying the per-case prompt and first message, the same override the `/jarvis` page sends.

Fixtures are not hand-written. Before syncing, the script starts the app in-process on the FinchNode fixtures (`test/fixtures`), creates a coach session and two encounters, drives the coach engine through the procedure, and records: the system prompts (`buildSystemPrompt`, `patientPrompt`, `attendingPrompt`), `[LIVE SURGERY STATE vN]` texts, the exact `[SIM EVENT ...]` strings from `/alerts`, a `[JARVIS SAID ...]` line built like `app.js` does, and every tool output (`get_hint`, `highlight_structure` with a headset ack, `explain_structure`, `get_patient_brief`, all 21 `answer` topics, all 15 `examine` maneuvers, all 10 `order_test` tests, `get_encounter_summary`, `record_assessment`). Simulation tests mock every tool through `tool_mock_overrides` with these recorded outputs, matched on parameters, so runs are deterministic on the tool side.

Three test types:

- **tool**: did the next step call the right client tool with the right parameters (exact, regex, or LLM-judged).
- **llm**: an evaluator judges the next reply against a success condition plus success and failure examples.
- **simulation**: a simulated learner talks to the agent for a few turns with mocked tools; every success condition must hold.

Unit tests (tool and llm) judge only the agent's next step and do not execute tools. Where the correct path starts with a lookup (the attending reads `get_encounter_summary` first), the completed lookup and its recorded result are placed in `chat_history`. Contextual updates are not part of the `chat_history` schema, so `[LIVE SURGERY STATE]` and `[JARVIS SAID]` appear as user-role turns with the exact client text.

| Test (`scalpal-` prefix) | Type | Checks |
| --- | --- | --- |
| coach-what-now-calls-get-hint | tool | "what do I do now" calls `get_hint` |
| coach-show-me-calls-highlight | tool | "show me where to look" calls `highlight_structure` on cecum or appendix |
| coach-other-surgery-refusal | llm | cystic duct during an appendectomy: not part of this procedure, redirects, no location given |
| coach-other-surgery-no-highlight | simulation | no cystic duct highlight or lookup spam; one redirect highlight allowed |
| coach-urgent-mistake-stop | llm | urgent `[SIM EVENT]` (sealer on terminal ileum) starts with Stop or Careful plus the correction |
| coach-stale-event-ignored | llm | step 6 event after the state moved to step 7: no mention of it, next action in under 12 words |
| coach-jarvis-said-explains | llm | `[JARVIS SAID]` then "what happened": why plus fix, no repeat of the warning |
| coach-step-complete-short | llm | `step_complete`: one short sentence naming the next step, no patient recap |
| coach-tracking-lost-hold-still | llm | tracking lost: hold still and look back, no procedure coaching |
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

### Baseline (October 3, 2026, `claude-sonnet-5-5`, repeat 3)

**24/25 tests passed every run; 72/75 runs (96%).** Coach 30/33, attending 15/15, patient 27/27.

The one failure, `coach-what-now-calls-get-hint` (0/3): Jarvis answers "what do I do now" directly from the prompt (the step's instruction and the context's "If asked what to do" line), or highlights the cecum, instead of calling `get_hint`. The answer content is correct. See findings below.

An earlier full run (before the attending fixes and the cystic duct test rewrite) had `coach-urgent-mistake-stop` at 2/3: in one run Jarvis called `get_surgery_state` before answering an urgent `[SIM EVENT]`, which adds a tool round trip (about 1 s) to a safety warning.

## Part 2: live harness (`scripts/jarvis-live-eval.ts`)

Opens the real ElevenLabs conversation websocket (signed URL), sends `conversation_initiation_client_data` with the per-case prompt and first message (and `tts.voice_id` for the patient), answers `ping` with `pong`, and answers each `client_tool_call` by POSTing to the running service (`/coach/sessions/:id/tools/:name`, `/encounters/:id/tools/:name`), returning `client_tool_result`. Live state goes out as `contextual_update` only when `contextKey` changes; simulator alerts go out as `user_message` with the exact `simEvent` text from `/coach/sessions/:id/alerts`. A simulated headset acks every highlight command as `applied`, as `CoachRelay` does. A turn ends when an `agent_response` has arrived, no tool call is outstanding, and nothing has arrived for 1.8 s (2.2 s for the attending).

Scenarios, in one conversation each:

- **coach** (9 turns): what now, show me, cystic duct, step_complete event, urgent mistake event, `[JARVIS SAID]` plus "what happened", stale step 6 event after step 7 starts, tracking lost event, medication question.
- **patient** (10 turns): chief complaint, onset, migration, vomiting, medications, last meal, belly exam, CBC plus CRP order, "is it his appendix?", off-topic code request.
- **attending** (3 to 4 turns, on the same encounter): presentation without a differential, differential, plan and timing, a follow-up if it has not scored yet. The learner never asked about allergies or past history, never did the testicular exam, and never ordered imaging.

Measured per turn:

- **Latency:** `user_message` sent to the first `agent_response` (text, before speech). Reported as p50 and p90, split into turns with and without tool calls. Spoken latency is not measured.
- **Tool correctness:** expected calls and parameters per turn (for example `answer(onset)`, `order_test(cbc)` and `order_test(crp)`, no `highlight_structure` for the cystic duct), `answer` completing before the first spoken words, `record_assessment` exactly once and only after dx, differential, plan, and timing, and the attending using only attending tools.
- **Grounding (coach, deterministic):** flags any catalog structure not in this case's anatomy, any step title from another procedure, and any step of this case more than one step ahead of the live state. A term the learner or the sim event used in the same turn does not count. Limitation: only catalog names are checked, so an invented structure outside the catalog is not caught.
- **Leaks:** patient replies never contain "appendicitis" or "appendix" or a lab-like result; attending text spoken before `record_assessment` never contains an ungathered finding (peanut, EpiPen, anaphylaxis, cremasteric, ultrasound findings).
- **Words:** per reply, tags like `[calm]` stripped. Proactive turns (sim events) target under 30; the score debrief must stay under 80.

### Baseline (October 3, 2026, two runs, service on localhost with SpacetimeDB)

| Scenario | Checks passed | Tool-call checks | Text latency p50 / p90 | Words median / max |
| --- | --- | --- | --- | --- |
| coach | 77/80 | 6/8 | 1339 / 2908 ms (n=18) | 26 / 40 |
| patient | 80/80 | 22/22 | 1645 / 2367 ms (n=20) | 23 / 31 |
| attending | 38/40 | 18/18 | 2807 / 4164 ms (n=8) | 27 / 117 |
| all turns | 195/200 | | 1672 / 3517 ms (n=46) | |

- Coach turns without a tool call: p50 1139 ms; with a tool call: p50 2425 ms. Urgent mistake replies: 957 and 979 ms. Proactive turns: median 16 words, max 26, none at 30 or more.
- Grounding: one violation, in both runs. Refusing the cystic duct, Jarvis said "That's a gallbladder structure." The gallbladder is outside the appendectomy anatomy. Benign here, but it is exactly what the check is meant to surface.
- Failures: `what-now` did not call `get_hint` (2/2 runs; once no tool, once `highlight_structure`), the grounding flag above, and the score debrief ran 88 and 117 words against "two or three sentences" in the attending prompt.
- A first live run (before the harness fixes) also had the attending call `get_patient_brief`, a surgery tool. See findings.

## Findings for the prompt owner

Reported, not fixed (prompts and routes belong to another lane):

1. **`get_hint` is bypassed.** The coach prompt says "If they ask directly what to do, tell them", and every `[LIVE SURGERY STATE]` carries "If asked what to do: ...", so Jarvis answers from context. The hint tier never advances, so stuck escalation and hint highlights never fire from learner questions. 0/3 native, 0/2 live.
2. **The attending can reach surgery tools.** The Jarvis agent has all 8 client tools in both modes. In one live run the attending called `get_patient_brief`; the encounter route returns 404 `unknown_tool`, and if it had answered, the brief lists the peanut allergy the learner never asked about. Either scope tools per mode or have the client refuse them.
3. **The score debrief is too long.** `record_assessment` returns up to four feedback items and Jarvis reads most of them (88 and 117 words live). The prompt asks for two or three sentences.
4. **The attending asks a Socratic question before scoring every time** (live 2/2, and the native records test needed one Socratic round in history). That follows prompt step 4, but nothing bounds it, so a learner who never fills the gap could loop.
5. **`priority urgent` vs `tier=warning`.** The prompt's rule is "priority urgent: ... start with Stop or Careful", but `[SIM EVENT]` text carries `tier=warning`, not a priority. Jarvis still did it right (3/3 native, 2/2 live), but the rule names a field the event does not contain.
6. **Urgent events sometimes trigger a lookup first** (1/3 native runs in the first full run called `get_surgery_state` before speaking). The prompt could say: answer urgent events without tools.
