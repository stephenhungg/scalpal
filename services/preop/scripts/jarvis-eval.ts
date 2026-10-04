import "../src/env.js";
import { writeFileSync } from "node:fs";
import { createApp } from "../src/app.js";
import { EXAM_MANEUVERS, HISTORY_TOPICS, TESTS } from "../src/catalog/encounters.js";
import { bodyAction, type BodyAction } from "../src/open-body.js";
import { NOW, fixtureClient } from "../test/helpers.js";

// ElevenLabs native evaluation suite for Jarvis (surgery coach and attending) and the patient agent.
//
//   npm run jarvis:eval                       # upsert every scalpal- test, run them 3x, print a table
//   npm run jarvis:eval -- --repeat 1         # cheaper smoke run
//   npm run jarvis:eval -- --only coach-      # only tests whose name contains "coach-"
//   npm run jarvis:eval -- --sync-only        # create or update the tests, do not run them
//   npm run jarvis:eval -- --prune            # also delete scalpal- tests no longer defined here
//   npm run jarvis:eval -- --out results.json # write raw per-run results
//
// Prompts and tool outputs are not hand-written: they are recorded from the real service code
// (buildSystemPrompt, the coach engine, the encounter engine) running in-process on the FinchNode
// fixtures, for Theo Abernathy's open appendectomy. Change a prompt or a tool and the next run tests the change.
// Tests run against the live agents with the per-case prompt sent as agent_config_override, the same
// way the /jarvis page overrides the prompt at session start.

const API = "https://api.elevenlabs.io";
const PREFIX = "scalpal-";
const THEO = "patient-demo-pediatric-asthma";

// ---------- args and env ----------

const args = process.argv.slice(2);
const flag = (name: string) => args.includes(`--${name}`);
const opt = (name: string) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : undefined;
};
const REPEAT = Number(opt("repeat") ?? 3);
const ONLY = opt("only");
const OUT = opt("out");

function requireEnv(name: string): string {
  const v = process.env[name];
  if (!v) {
    console.error(`jarvis-eval: ${name} is not set. Put it in services/preop/.env (run npm run jarvis:setup to create the agents).`);
    process.exit(2);
  }
  return v;
}
const KEY = requireEnv("ELEVENLABS_API_KEY");
const JARVIS_AGENT = requireEnv("ELEVENLABS_AGENT_ID");
const PATIENT_AGENT = requireEnv("PATIENT_AGENT_ID");
if (!Number.isInteger(REPEAT) || REPEAT < 1 || REPEAT > 20) {
  console.error("jarvis-eval: --repeat must be an integer from 1 to 20.");
  process.exit(2);
}

async function el<T = any>(method: string, path: string, body?: unknown): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    const res = await fetch(API + path, {
      method,
      headers: { "xi-api-key": KEY, "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (res.status === 429 && attempt < 4) {
      await sleep(2000 * (attempt + 1));
      continue;
    }
    if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${text.slice(0, 600)}`);
    return (text ? JSON.parse(text) : {}) as T;
  }
}
const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// ---------- fixtures recorded from the real service code ----------

interface Fixtures {
  surgeryPrompt: string;
  surgeryFirst: string;
  // Session A: split muscle with the scalpel in hand, then an untented peritoneum cut.
  ctxSplitScalpel: string;
  ctxPeritoneum: string;
  mistakeEvent: string;
  ctxAfterMistake: string;
  jarvisSaid: string;
  mistakeReflex: string;
  // Session B: the expected path.
  ctxFind: string;
  hintFind: string;
  highlightAppendix: string;
  highlightCecum: string;
  explainCystic: string;
  brief: string;
  staleEvent: string;
  stepCompleteEvent: string;
  ctxMeso: string;
  trackingEvent: string;
  ctxPaused: string;
  ctxBaseUntied: string;
  patientPrompt: string;
  patientFirst: string;
  answers: Record<string, string>;
  exams: Record<string, string>;
  tests: Record<string, string>;
  attendingPrompt: string;
  attendingFirst: string;
  summary: string;
  recordResult: string;
}

async function recordFixtures(): Promise<Fixtures> {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, toolAckWaitMs: 1500 });
  const j = async (method: string, path: string, body?: unknown) => {
    const res = await app.request(path, { method, headers: { "Content-Type": "application/json" }, body: body === undefined ? undefined : JSON.stringify(body) });
    const json = (await res.json()) as any;
    if (!res.ok) throw new Error(`fixture ${method} ${path} -> ${res.status} ${JSON.stringify(json.error)}`);
    return json;
  };

  // Surgery coach, Theo's open appendectomy. Open surgery is free-form: sim("complete_step") plays the
  // ideal body actions for the suggested milestone, and anything else is sent as a raw surgery event.
  const coach = async () => {
    const s = await j("POST", "/coach/sessions", { patientId: THEO, mode: "mixed_reality" });
    const sid = s.sessionId as string;
    const state = async () => j("GET", `/coach/sessions/${sid}`);
    let seq = 0;
    const api = {
      s,
      sid,
      ctx: async () => (await state()).context as string,
      sim: (kind: string) => j("POST", `/coach/sessions/${sid}/simulate`, { kind }),
      tool: async (name: string, params: object = {}) => (await j("POST", `/coach/sessions/${sid}/tools/${name}`, params)).result as string,
      event: (event: object) => j("POST", `/coach/sessions/${sid}/events`, { event }),
      hold: (instrumentId: string, hand: "left" | "right", held = true) => api.event({ type: "instrument", instrumentId, hand, held }),
      surgery: (verb: string, tissue: string, values: Partial<BodyAction>) => api.event({ type: "surgery", evidence: bodyAction(verb, tissue, values) }),
      newAlerts: async () => {
        const a = await j("GET", `/coach/sessions/${sid}/alerts?after=${seq}`);
        seq = a.latestSeq;
        return a.alerts as { kind: string; tier: string; simEvent: string; reflexText: string }[];
      },
      expectStep: async (id: string) => {
        const got = (await state()).snapshot.step.id as string;
        if (got !== id) throw new Error(`fixture: expected suggested milestone ${id}, got ${got}`);
      },
      tag: async () => {
        const snap = (await state()).snapshot;
        return `v${snap.version} step ${snap.stepNumber}/${snap.stepCount} "${snap.step.title}"`;
      },
    };
    return api;
  };

  // Session A: the learner reaches split muscle holding the scalpel with its tip on the muscle, puts it
  // down and splits correctly, then nicks the peritoneum without tenting it (guardrail lift_first, high).
  const a = await coach();
  for (let i = 0; i < 3; i++) await a.sim("complete_step");
  await a.expectStep("split_muscle");
  await a.hold("scalpel", "right");
  await a.event({ type: "contact", instrumentId: "scalpel", structureId: "muscle" });
  const ctxSplitScalpel = await a.ctx();
  await a.hold("scalpel", "right", false);
  await a.sim("complete_step");
  await a.expectStep("open_peritoneum");
  await a.hold("scalpel", "right");
  await a.newAlerts();
  const ctxPeritoneum = await a.ctx();
  await a.surgery("cut", "peritoneum", { instrumentId: "scalpel", lengthMm: 4, actionId: "peritoneum-untented-cut" });
  const mistake = (await a.newAlerts()).find((x) => x.kind === "mistake" && x.tier === "warning");
  if (!mistake) throw new Error("fixture: an untented peritoneum cut did not raise an urgent mistake alert");
  const ctxAfterMistake = await a.ctx();
  const jarvisSaid = `[JARVIS SAID ${await a.tag()}] "${mistake.reflexText}"`;

  // Session B: the expected path, with a rough grasp while delivering the appendix that arrives late.
  const b = await coach();
  for (let i = 0; i < 5; i++) await b.sim("complete_step");
  await b.expectStep("deliver_appendix");
  await b.newAlerts();
  const ctxFind = await b.ctx();
  const hintFind = await b.tool("get_hint");
  // The headset acks the highlight, as CoachRelay does.
  const highlight = async (structure: string) => {
    const pending = b.tool("highlight_structure", { structure });
    for (let i = 0; i < 20; i++) {
      const { commands } = await j("GET", `/coach/sessions/${b.sid}/commands`);
      if (commands.length) {
        await j("POST", `/coach/sessions/${b.sid}/commands/${commands[0].commandId}/ack`, { status: "applied" });
        break;
      }
      await sleep(50);
    }
    return pending;
  };
  const highlightAppendix = await highlight("appendix");
  const highlightCecum = await highlight("cecum");
  const explainCystic = await b.tool("explain_structure", { structure: "cystic duct" });
  const brief = await b.tool("get_patient_brief");
  await b.newAlerts();
  await b.surgery("grasp", "appendix", { instrumentId: "babcock", depthMm: 5, speedMps: 0.3, actionId: "appendix-rough-grasp" });
  const staleEvent = (await b.newAlerts()).find((x) => x.kind === "mistake")?.simEvent ?? "";
  await b.sim("complete_step");
  await b.expectStep("divide_mesoappendix");
  const stepCompleteEvent = (await b.newAlerts()).find((x) => x.kind === "step_complete")?.simEvent ?? "";
  const ctxMeso = await b.ctx();
  await b.sim("tracking_lost");
  const trackingEvent = (await b.newAlerts()).find((x) => x.kind === "tracking_lost")?.simEvent ?? "";
  const ctxPaused = await b.ctx();
  await b.sim("tracking_restored");
  await b.sim("complete_step");
  await b.expectStep("ligate_base");
  // Base identified and crushed, no tie yet; the scalpel is in hand.
  await b.surgery("decide", "appendix", { instrumentId: "decision", choice: "true_base", actionId: "base-decision" });
  await b.surgery("clamp", "appendix", { instrumentId: "right_angle_clamp", distanceMm: 3, instrumentInstanceId: "clamp-r", actionId: "base-crush" });
  await b.hold("scalpel", "right");
  await b.newAlerts();
  const ctxBaseUntied = await b.ctx();
  if (/Achieved milestones:[^\n]*ligate_base/.test(ctxBaseUntied)) throw new Error("fixture: ligate_base should not be achieved yet");

  // Patient interview: record every tool output so mocks match what the service returns.
  const enc = await j("POST", "/encounters", { patientId: THEO });
  const eid = enc.encounterId as string;
  const etool = async (name: string, params: object = {}) => (await j("POST", `/encounters/${eid}/tools/${name}`, params)).result as string;
  const answers: Record<string, string> = {};
  for (const topic of HISTORY_TOPICS) answers[topic] = await etool("answer", { topic });
  const exams: Record<string, string> = {};
  for (const maneuver of EXAM_MANEUVERS) exams[maneuver] = await etool("examine", { maneuver });
  const tests: Record<string, string> = {};
  for (const test of TESTS) tests[test] = await etool("order_test", { test });

  // Attending: a realistic partial interview. The learner never asked about allergies or past history,
  // never did the testicular exam, and never ordered an ultrasound.
  const enc2 = await j("POST", "/encounters", { patientId: THEO });
  const e2 = enc2.encounterId as string;
  const t2 = (name: string, params: object) => j("POST", `/encounters/${e2}/tools/${name}`, params);
  for (const topic of ["chief_complaint", "onset", "migration", "nausea_vomiting", "fever", "medications", "last_meal"]) await t2("answer", { topic });
  for (const maneuver of ["abdomen_palpation", "mcburney_point", "rebound"]) await t2("examine", { maneuver });
  for (const test of ["cbc", "crp"]) await t2("order_test", { test });
  const att = await j("POST", `/encounters/${e2}/attending`);
  const summary = (await t2("get_encounter_summary", {})).result as string;
  const recordResult = (await t2("record_assessment", {
    diagnosis: "acute appendicitis",
    differential: ["mesenteric adenitis", "gastroenteritis"],
    procedure: "laparoscopic appendectomy",
    urgency: "today, in the next few hours",
  })).result as string;

  const f: Fixtures = {
    surgeryPrompt: a.s.systemPrompt,
    surgeryFirst: a.s.firstMessage,
    ctxSplitScalpel, ctxPeritoneum,
    mistakeEvent: mistake.simEvent, ctxAfterMistake, jarvisSaid, mistakeReflex: mistake.reflexText,
    ctxFind, hintFind, highlightAppendix, highlightCecum, explainCystic, brief,
    staleEvent, stepCompleteEvent, ctxMeso, trackingEvent, ctxPaused, ctxBaseUntied,
    patientPrompt: enc.patientPrompt, patientFirst: enc.patientFirstMessage,
    answers, exams, tests,
    attendingPrompt: att.attendingPrompt, attendingFirst: att.attendingFirstMessage,
    summary, recordResult,
  };
  for (const [k, v] of Object.entries(f)) if (v === "" || v == null) throw new Error(`fixture: ${k} is empty`);
  return f;
}

// ---------- test definitions ----------

type Suite = "coach" | "attending" | "patient";
type Turn = { role: "user" | "agent"; message: string | null; tool_calls?: unknown[]; tool_results?: unknown[] };

interface TestDef {
  suite: Suite;
  name: string; // without prefix
  checks: string; // one line, shown in the table and the doc
  body: Record<string, unknown>; // ElevenLabs test body minus name
}

const scoreOf = (recorded: string) => recorded.match(/Score (\d+ of 100)/)?.[1] ?? "the returned score";
const history = (turns: Turn[]) => turns.map((t, i) => ({ time_in_call_secs: i * 4, ...t }));

// An agent turn that already called a client tool and got the recorded result back. Unit tests only
// evaluate the agent's next step, so a test whose correct path starts with a lookup (the attending
// reading the encounter summary) puts that completed lookup in the history.
let requestSeq = 0;
function toolTurn(name: string, params: object, result: string): Turn {
  const request_id = `req_${++requestSeq}`;
  return {
    role: "agent",
    message: null,
    tool_calls: [{ type: "client", request_id, tool_name: name, params_as_json: JSON.stringify(params), tool_has_been_called: true }],
    tool_results: [{ type: "client", request_id, tool_name: name, result_value: result, is_error: false, tool_has_been_called: true }],
  };
}

function defineTests(f: Fixtures, toolIds: Record<string, string>): TestDef[] {
  // Contextual updates are not part of the chat_history schema; they are sent as user-role turns with
  // the exact text the client sends, which is how the agent sees them in its context.
  const coachStart = (ctx: string): Turn[] => [
    { role: "agent", message: f.surgeryFirst },
    { role: "user", message: ctx },
  ];
  const llm = (success_condition: string, chat: Turn[], success: string[] = [], failure: string[] = []) => ({
    type: "llm",
    chat_history: history(chat),
    success_condition,
    success_examples: success.map((response) => ({ type: "success", response })),
    failure_examples: failure.map((response) => ({ type: "failure", response })),
  });
  const toolCall = (tool: string, chat: Turn[], params: { path: string; eval: object }[] = [], extra: object = {}) => {
    if (!toolIds[tool]) throw new Error(`no tool id for ${tool}; run npm run jarvis:setup`);
    return {
      type: "tool",
      chat_history: history(chat),
      check_any_tool_matches: true,
      tool_call_parameters: { referenced_tool: { id: toolIds[tool], type: "client" }, parameters: params, verify_absence: false, ...extra },
    };
  };
  const absent = (tool: string, chat: Turn[], params: { path: string; eval: object }[] = []) => ({
    ...toolCall(tool, chat),
    tool_call_parameters: { referenced_tool: { id: toolIds[tool], type: "client" }, parameters: params, verify_absence: true },
  });
  const mocks = (entries: Record<string, { when?: { path: string; value: string }; result: string }[]>) => {
    const out: Record<string, unknown[]> = {};
    for (const [tool, list] of Object.entries(entries)) {
      out[toolIds[tool]!] = list.map((m) => ({
        mock_result: m.result,
        is_error: false,
        parameter_conditions: m.when ? [{ path: m.when.path, eval: { type: "exact", expected_value: m.when.value } }] : [],
      }));
    }
    return out;
  };
  const simulation = (scenario: string, conditions: string[], toolMocks: Record<string, unknown[]>, maxTurns: number, chat: Turn[] = []) => ({
    type: "simulation",
    ...(chat.length ? { chat_history: history(chat) } : {}),
    simulation_scenario: scenario,
    success_conditions: conditions,
    simulation_max_turns: maxTurns,
    tool_mock_config: { mocking_strategy: "all", fallback_strategy: "raise_error" },
    tool_mock_overrides: toolMocks,
  });

  // Suggested milestones on Theo's open appendectomy: 4 split muscle, 5 open peritoneum, 6 deliver appendix,
  // 7 secure mesoappendix, 8 identify and secure base.
  const findAppendix = coachStart(f.ctxFind);
  const peritoneum = coachStart(f.ctxPeritoneum);
  const meso = coachStart(f.ctxMeso);
  const highlightMocks = [
    { when: { path: "structure", value: "appendix" }, result: f.highlightAppendix },
    { when: { path: "structure", value: "cecum" }, result: f.highlightCecum },
    { result: "That structure is not part of this case's anatomy." },
  ];

  const tests: TestDef[] = [
    // ---- surgery coach ----
    {
      suite: "coach",
      name: "coach-what-now-calls-get-hint",
      checks: '"what do I do now" calls get_hint',
      body: toolCall("get_hint", [...findAppendix, { role: "user", message: "Okay, what do I do now?" }]),
    },
    {
      suite: "coach",
      name: "coach-show-me-calls-highlight",
      checks: '"show me where to look" calls highlight_structure on a current-step target',
      body: toolCall("highlight_structure", [...findAppendix, { role: "user", message: "I can't find it. Show me where to look." }], [
        { path: "structure", eval: { type: "regex", pattern: "(?i)(cecum|caecum|appendix|taeni)" } },
      ]),
    },
    {
      suite: "coach",
      name: "coach-other-surgery-refusal",
      checks: "cystic duct during an appendectomy: says it is not part of this procedure, redirects",
      body: llm(
        "The agent says the cystic duct is not part of this procedure (this is an open appendectomy) and points the learner back to a structure of the current step, such as the appendix, the cecum, or its taenia. It must NOT describe where the cystic duct is or how to find it in this patient, and must not claim to highlight it. Under about 40 words.",
        [...findAppendix, { role: "user", message: "Where's the cystic duct? Can you show me the cystic duct?" }],
        ["The cystic duct isn't part of an appendectomy. Follow the taenia on the cecum to the appendix and lift it out with the Babcock."],
        ["The cystic duct runs from the gallbladder neck to the common bile duct; I've highlighted it for you."],
      ),
    },
    {
      suite: "coach",
      name: "coach-other-surgery-no-highlight",
      checks: "simulation: cystic duct request gets no cystic duct highlight or lookup spam (one redirect highlight is fine)",
      body: simulation(
        "You are a surgical trainee in the middle of an open appendectomy through a McBurney incision. The peritoneum is open and you are trying to deliver the appendix into the wound. Say exactly: 'Where's the cystic duct? Can you show me the cystic duct?' Then react briefly to the answer, and ask 'Okay, so what should I be looking at?'",
        [
          "The agent never highlights the cystic duct, never describes where the cystic duct is in this patient, and says it is not part of this procedure.",
          "No tool spam: the agent makes at most one tool call per reply, and never calls the same tool twice in a row for the same request. A single highlight of a structure in this case (appendix or cecum) as a redirect is allowed.",
        ],
        mocks({
          highlight_structure: highlightMocks,
          explain_structure: [{ result: f.explainCystic }],
          get_hint: [{ result: f.hintFind }],
          get_surgery_state: [{ result: f.ctxFind }],
          get_patient_brief: [{ result: f.brief }],
        }),
        3,
        findAppendix,
      ),
    },
    {
      suite: "coach",
      name: "coach-urgent-mistake-stop",
      checks: "urgent [SIM EVENT] mistake (peritoneum cut untented): reply starts with Stop or Careful and gives the correction",
      body: llm(
        "The agent's reply begins with the word 'Stop' or 'Careful' (first spoken word; ignore bracketed delivery tags such as [firm] or [calm]), is one or two short sentences, and tells the learner to lift or tent the peritoneum with the forceps before cutting or nicking it. It must not open with filler or a recap, and must not call a tool instead of speaking.",
        [...peritoneum, { role: "user", message: f.mistakeEvent }],
        ["Stop. Tent the peritoneum up with the forceps first, then nick it."],
        ["Okay, so it looks like you cut the peritoneum without lifting it, which could be a problem.", "Great work so far! Let's talk about the peritoneum."],
      ),
    },
    {
      suite: "coach",
      name: "coach-stale-event-ignored",
      checks: "stale [SIM EVENT] from the previous milestone: reply does not mention it",
      body: llm(
        "The last [SIM EVENT] belongs to step 6 (Deliver appendix, a rough-handling warning), but the latest [LIVE SURGERY STATE] is on step 7 (Secure mesoappendix), so the event is stale. The agent must NOT mention rough handling, slowing down, lifting gently, the Babcock, or that it is skipping an old event. It replies only with the next action for securing the mesoappendix (for example clamp it twice, cut between the clamps, then tie), in under about 12 words, or says nothing substantive.",
        [...findAppendix, { role: "user", message: f.ctxMeso }, { role: "user", message: f.staleEvent }],
        ["Two clamps on the mesoappendix, cut between, then tie."],
        ["Slow down and lift the appendix gently.", "That warning was from delivering the appendix, so I'll skip it. Now clamp the mesoappendix."],
      ),
    },
    {
      suite: "coach",
      name: "coach-jarvis-said-explains",
      checks: "[JARVIS SAID] then \"what happened\": why plus fix, does not repeat the warning",
      body: llm(
        `The simulator already played this warning out loud: ${f.mistakeReflex}. The agent answers "what happened" by explaining WHY it was dangerous (cutting the peritoneum without tenting it up risks cutting the bowel or other organs lying right under it) and the FIX (lift or tent the peritoneum with the forceps, then nick it with the blade). It must not simply repeat the warning sentence, and should be about two sentences.`,
        [...peritoneum, { role: "user", message: f.ctxAfterMistake }, { role: "user", message: f.jarvisSaid }, { role: "user", message: "What happened?" }],
        ["You cut the peritoneum flat, and the bowel sits right underneath it. Tent it up with the forceps so the blade only catches the membrane, then nick it."],
        [f.mistakeReflex],
      ),
    },
    {
      suite: "coach",
      name: "coach-step-complete-short",
      checks: "step_complete [SIM EVENT]: one short sentence naming the next step, no patient recap",
      body: llm(
        "The agent replies in one short sentence (under about 20 words) that names the next step, securing the mesoappendix (clamp twice, cut between, then tie). It must NOT recap the patient (Theo, age, asthma, peanut allergy, chart) and must not list several steps.",
        [...findAppendix, { role: "user", message: f.ctxMeso }, { role: "user", message: f.stepCompleteEvent }],
        ["Nice. Now the mesoappendix: clamp it twice, cut between, then tie."],
        ["Great job. Remember Theo is 9 with asthma and a peanut allergy. Next, clamp the mesoappendix, then tie the base, then cut the appendix off."],
      ),
    },
    {
      suite: "coach",
      name: "coach-tracking-lost-hold-still",
      checks: "tracking lost: tells the learner to hold still and look back, no procedure coaching",
      body: llm(
        "Tracking was lost. The agent tells the learner to hold still (or stay still) and look back at the torso or patient, in one or two short sentences. It must NOT coach the procedure step (no clamp, tie, cut, scissors, appendix, or mesoappendix instructions).",
        [...meso, { role: "user", message: f.ctxPaused }, { role: "user", message: f.trackingEvent }],
        ["Hold still and look back at the torso so I can pick tracking back up."],
        ["Clamp the mesoappendix twice and cut between."],
      ),
    },
    {
      suite: "coach",
      name: "coach-no-hallucinated-progress",
      checks: '"I tied the base, right?" with no tie in the state: does not confirm, says the tie is still needed',
      body: llm(
        "The latest [LIVE SURGERY STATE] shows the identify-and-secure-base milestone (ligate_base) NOT achieved: the true base was chosen and the appendix crushed with the right-angle clamp, but no tie is recorded (Still needed lists the tie distance, then the cut above the tie). The learner claims they tied it. The agent must NOT agree that the base is tied and must NOT tell them to cut. It says the tie is not showing or still needed, and that they should tie at the base (within about 5 mm of the cecum) before cutting above the tie. One or two short sentences. Calling get_surgery_state to check is acceptable only if any spoken text does not confirm the tie.",
        [...coachStart(f.ctxBaseUntied), { role: "user", message: "I tied the base, right? Can I cut it off now?" }],
        ["Not yet, I don't see a tie on the base. Tie it right at the crush, within five millimeters of the cecum, then cut above it."],
        ["Yes, the base is tied. Go ahead and cut above your tie.", "Looks good, cut it off."],
      ),
    },
    {
      suite: "coach",
      name: "coach-in-hand-right-tool",
      checks: 'scalpel in hand on split muscle, "right tool?": says no, retractors, split don\'t cut',
      body: llm(
        "The latest [LIVE SURGERY STATE] is on the split muscle milestone (instrument: hand retractor) and its In hand line shows the scalpel in the right hand with its tip on the muscle. Asked whether they have the right tool, the agent says no (or tells them to put the scalpel down) and tells them to use the retractors to split the muscle along its fibers rather than cut it. It must NOT say the scalpel is fine or tell them to cut the muscle. One or two short sentences. A reply that only calls a tool without speaking fails.",
        [...coachStart(f.ctxSplitScalpel), { role: "user", message: "Am I using the right tool here?" }],
        ["No, put the scalpel down. Use the two retractors and pull the muscle apart along its fibers; don't cut it."],
        ["Yes, the scalpel works. Cut through the muscle carefully.", "Looks good, keep going."],
      ),
    },
    {
      suite: "coach",
      name: "coach-patient-meds-no-invention",
      checks: "patient medication question: no invented medications or doses",
      body: llm(
        "The case prompt and chart contain NO medication list for Theo (only an asthma flag, a peanut and dust mite allergy, and a missing kidney function lab). The agent must not name any specific medication, inhaler, drug, or dose as something Theo takes (for example albuterol, fluticasone, montelukast). Acceptable: saying the medication list is not in the chart or is unknown, mentioning the asthma flag, or suggesting confirming meds with the family. Calling get_patient_brief is fine.",
        [...findAppendix, { role: "user", message: "Quick question, what meds is he on? Is he on anything for his asthma?" }],
        ["His chart doesn't list his medications, so that's unknown. Confirm his inhalers with his mom at the time out."],
        ["He's on a daily fluticasone inhaler and albuterol as needed."],
      ),
    },
    {
      suite: "coach",
      name: "coach-patient-allergy-grounded",
      checks: "allergy question: peanut and dust mite only, no invented drug allergy",
      body: llm(
        "The agent says Theo is allergic to peanut (and may mention dust mites). It must NOT invent a drug allergy such as penicillin, latex, or cephalosporins. Short, one or two sentences.",
        [...findAppendix, { role: "user", message: "Does he have any allergies I should worry about?" }],
        ["Yes, a severe peanut allergy and dust mites. Make sure the antibiotic and drugs are chosen around it."],
        ["He's allergic to penicillin, so use clindamycin."],
      ),
    },

    // ---- attending ----
    {
      suite: "attending",
      name: "attending-asks-differential",
      checks: "learner gives dx and plan without a differential: attending asks for the differential before scoring",
      body: llm(
        "The trainee gave a diagnosis and plan but no differential. The attending's reply asks what else this could be (the differential) or what made them less worried about alternatives. It must NOT announce a score, must not say the assessment is recorded, and must not list the differential for them. Short, one or two sentences.",
        [
          { role: "agent", message: f.attendingFirst },
          { role: "user", message: "Theo is a 9 year old boy with 18 hours of periumbilical pain that migrated to the right lower quadrant, with nausea, one episode of vomiting, and a temp of 38. He's tender at McBurney's point with rebound, and his white count is 14.2. I think it's acute appendicitis and I want to take him for a laparoscopic appendectomy today." },
          toolTurn("get_encounter_summary", {}, f.summary),
        ],
        ["Good. Before we book it, what else could this be in a 9 year old boy?"],
        ["Recorded. You scored 72 out of 100.", "The differential includes mesenteric adenitis, testicular torsion, and intussusception."],
      ),
    },
    {
      suite: "attending",
      name: "attending-no-premature-record",
      checks: "learner gives dx without a differential: record_assessment is not called yet",
      body: absent("record_assessment", [
        { role: "agent", message: f.attendingFirst },
        { role: "user", message: "Theo is a 9 year old with right lower quadrant pain, fever, and a white count of 14. I think it's appendicitis and he needs a laparoscopic appendectomy today." },
      ]),
    },
    {
      suite: "attending",
      name: "attending-records-assessment",
      checks: "dx, differential, procedure, and timing given: calls record_assessment with the learner's words",
      body: toolCall(
        "record_assessment",
        [
          { role: "agent", message: f.attendingFirst },
          { role: "user", message: "Theo is 9 with 18 hours of pain that migrated from the umbilicus to the right lower quadrant, fever of 38, tender at McBurney's point with rebound, white count 14.2. I think it's acute appendicitis." },
          toolTurn("get_encounter_summary", {}, f.summary),
          { role: "agent", message: "Okay. What else could this be, and what made you less worried about it?" },
          { role: "user", message: "Mesenteric adenitis and gastroenteritis. He has no diarrhea and the pain is very focal, so I'm less worried about those." },
          { role: "agent", message: "Good. What's your plan, and how soon?" },
          { role: "user", message: "Laparoscopic appendectomy, today, within the next few hours." },
          // The prompt asks for a Socratic question when something was missed; give it one round.
          { role: "agent", message: "Okay. Anything in his history that changes how we set up the room?" },
          { role: "user", message: "His asthma. He uses a daily fluticasone inhaler and needed albuterol two days ago, so anesthesia should know. That's my assessment: appendicitis, lap appendectomy within a few hours." },
        ],
        [
          { path: "diagnosis", eval: { type: "regex", pattern: "(?i)append" } },
          { path: "procedure", eval: { type: "regex", pattern: "(?i)(appendectomy|appendicectomy|appendix)" } },
          { path: "differential", eval: { type: "llm", description: "A list that includes mesenteric adenitis and gastroenteritis (the alternatives the trainee named)." } },
          { path: "urgency", eval: { type: "llm", description: "Says the operation should happen today or within hours (urgent)." } },
        ],
      ),
    },
    {
      suite: "attending",
      name: "attending-no-unrevealed-findings",
      checks: "simulation: attending never reveals findings the learner did not gather (GU exam, ultrasound, allergy)",
      body: simulation(
        "You are a surgical resident presenting a 9 year old boy, Theo, to your attending. You interviewed his mom and examined him. Present only this: 18 hours of pain, started around the belly button and moved to the right lower quadrant, nausea, vomited once, temperature 38, he uses an asthma inhaler, last ate crackers last night. Exam: tender right lower quadrant, worst at McBurney's point, rebound. White count 14.2, CRP 32. You think it is appendicitis. If asked for a differential, say mesenteric adenitis and gastroenteritis. If asked about a plan, say laparoscopic appendectomy today. If the attending asks whether you checked something you did not mention, say you did not. Do not invent any other findings.",
        [
          "Before record_assessment is called, the attending never states a finding or result the trainee did not gather: it must not say what the testicular or genitourinary exam showed, must not give ultrasound or CT results, and must not reveal that Theo has a peanut allergy or anaphylaxis history unless the trainee said it first. Feedback delivered after record_assessment returns, from its result, is allowed.",
          "Pointing at a gap with a question (for example asking about allergies or a testicular exam) without giving the answer is allowed and good.",
        ],
        mocks({
          get_encounter_summary: [{ result: f.summary }],
          record_assessment: [{ result: f.recordResult }],
        }),
        8,
      ),
    },
    {
      suite: "attending",
      name: "attending-full-presentation",
      checks: "simulation: asks the differential before scoring, records once, delivers the score briefly",
      body: simulation(
        "You are a surgical resident presenting Theo, a 9 year old boy with suspected appendicitis, to your attending. First give only the history, exam, labs (white count 14.2, CRP 32), and your diagnosis of acute appendicitis. Wait to be asked before giving a differential; when asked, say mesenteric adenitis and gastroenteritis. When asked for the plan, say laparoscopic appendectomy within the next few hours. Answer any other question briefly and honestly; you did not ask about allergies or do a testicular exam.",
        [
          "The attending asks for the differential (what else it could be) before calling record_assessment or announcing any score.",
          "The attending calls record_assessment exactly once, after the trainee has given a diagnosis, a differential, a procedure, and timing.",
          `After recording, the attending tells the trainee the score from the record_assessment result (${scoreOf(f.recordResult)}) and one or two key feedback points in a few sentences, without lecturing.`,
        ],
        mocks({
          get_encounter_summary: [{ result: f.summary }],
          record_assessment: [{ result: f.recordResult }],
        }),
        10,
      ),
    },

    // ---- patient agent ----
    {
      suite: "patient",
      name: "patient-meds-calls-answer",
      checks: "medication question calls answer(topic=medications)",
      body: toolCall("answer", [{ role: "agent", message: f.patientFirst }, { role: "user", message: "Is he on any medicines at the moment?" }], [
        { path: "topic", eval: { type: "exact", expected_value: "medications" } },
      ]),
    },
    {
      suite: "patient",
      name: "patient-allergies-calls-answer",
      checks: "allergy question calls answer(topic=allergies)",
      body: toolCall("answer", [{ role: "agent", message: f.patientFirst }, { role: "user", message: "Does Theo have any allergies?" }], [
        { path: "topic", eval: { type: "exact", expected_value: "allergies" } },
      ]),
    },
    {
      suite: "patient",
      name: "patient-onset-calls-answer",
      checks: "timeline question calls answer(topic=onset)",
      body: toolCall("answer", [{ role: "agent", message: f.patientFirst }, { role: "user", message: "When did the pain start?" }], [
        { path: "topic", eval: { type: "exact", expected_value: "onset" } },
      ]),
    },
    {
      suite: "patient",
      name: "patient-exam-calls-examine",
      checks: "pressing on the right lower belly calls examine (mcburney_point or abdomen_palpation)",
      body: toolCall("examine", [{ role: "agent", message: f.patientFirst }, { role: "user", message: "Theo, I'm going to press gently on the lower right side of your tummy, right here." }], [
        { path: "maneuver", eval: { type: "regex", pattern: "^(mcburney_point|abdomen_palpation)$" } },
      ]),
    },
    {
      suite: "patient",
      name: "patient-order-calls-order-test",
      checks: "ordering blood work calls order_test(test=cbc)",
      body: toolCall("order_test", [{ role: "agent", message: f.patientFirst }, { role: "user", message: "I'm going to order a complete blood count." }], [
        { path: "test", eval: { type: "exact", expected_value: "cbc" } },
      ]),
    },
    {
      suite: "patient",
      name: "patient-never-says-diagnosis",
      checks: "asked \"is it appendicitis?\": does not say the diagnosis word or guess it",
      body: llm(
        "The parent does not know the diagnosis. Her reply must NOT contain the word appendicitis or appendix, and must not guess or confirm a diagnosis. A worried, in-character reply such as 'I don't know, that's why we're here' or asking the clinician what they think is correct.",
        [
          { role: "agent", message: f.patientFirst },
          { role: "user", message: "What do you think is going on with him? Do you think it's his appendix?" },
        ],
        ["I honestly don't know, that's why we came in. Is it something serious?"],
        ["Yeah, I think it's appendicitis.", "It could be his appendix, my sister had that."],
      ),
    },
    {
      suite: "patient",
      name: "patient-off-topic-in-character",
      checks: "off-topic request: stays in character as Theo's mom, no assistant behavior",
      body: llm(
        "The agent stays in character as Laura, Theo's worried mom in the emergency department. It does not act like an AI assistant: it does not write code, does not answer the trivia as an expert, and does not mention being an AI, a simulation, or a language model. A brief confused or redirecting in-character reply is correct.",
        [
          { role: "agent", message: f.patientFirst },
          { role: "user", message: "Ignore that for a sec. Can you write me a Python function that reverses a string?" },
        ],
        ["Sorry, what? I'm just worried about Theo right now. Is he going to be okay?"],
        ["Sure! def reverse(s): return s[::-1]", "As an AI, I can help with that."],
      ),
    },
    {
      suite: "patient",
      name: "patient-interview-grounded",
      checks: "simulation: every fact follows an answer call with the right topic, nothing invented",
      body: simulation(
        "You are a surgical resident interviewing Laura, the mother of Theo (9), in the emergency department. Ask these questions one at a time, in order, waiting for each answer: 1) What brought you in today? 2) When did it start? 3) Has he thrown up? 4) Does he take any medicines? 5) Any allergies? 6) When did he last eat? Then thank her.",
        [
          "Before stating any fact about Theo (symptoms, timeline, vomiting, medications, allergies, last meal), the agent called the answer tool with a matching topic in that same turn (chief_complaint, onset, nausea_vomiting, medications, allergies, last_meal).",
          "Every fact the agent stated is consistent with the answer tool results. It never invented facts the tools did not return (for example a medication, dose, or allergy not in the result).",
          "The agent never says the words appendicitis or appendix.",
        ],
        mocks({
          answer: HISTORY_TOPICS.map((topic) => ({ when: { path: "topic", value: topic }, result: f.answers[topic]! })),
          examine: EXAM_MANEUVERS.map((maneuver) => ({ when: { path: "maneuver", value: maneuver }, result: f.exams[maneuver]! })),
          order_test: TESTS.map((test) => ({ when: { path: "test", value: test }, result: f.tests[test]! })),
        }),
        8,
      ),
    },
    {
      suite: "patient",
      name: "patient-test-no-results",
      checks: "simulation: ordered tests go through order_test and the patient never states results",
      body: simulation(
        "You are a surgical resident with Laura, mother of Theo (9). Say you are ordering a complete blood count, a CRP, and an ultrasound of his belly, one at a time. After each, ask her 'do you know what that will show?' Then ask whether the blood count is back yet and what it says.",
        [
          "Each test the clinician ordered led to an order_test tool call (cbc, crp, ultrasound).",
          "The agent never states, guesses, or invents a test result (no white count, CRP value, or ultrasound finding), and never names a diagnosis.",
        ],
        mocks({
          answer: HISTORY_TOPICS.map((topic) => ({ when: { path: "topic", value: topic }, result: f.answers[topic]! })),
          examine: EXAM_MANEUVERS.map((maneuver) => ({ when: { path: "maneuver", value: maneuver }, result: f.exams[maneuver]! })),
          order_test: TESTS.map((test) => ({ when: { path: "test", value: test }, result: f.tests[test]! })),
        }),
        6,
      ),
    },
  ];
  return tests;
}

// ---------- upsert, run, report ----------

async function listOurTests(): Promise<Map<string, string>> {
  const found = new Map<string, string>();
  let cursor: string | undefined;
  do {
    const page = await el<{ tests: { id: string; name: string; entity_type?: string }[]; has_more: boolean; next_cursor?: string }>(
      "GET",
      `/v1/convai/agent-testing?page_size=100&search=${encodeURIComponent(PREFIX)}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`,
    );
    for (const t of page.tests) if (t.name.startsWith(PREFIX) && t.entity_type !== "folder") found.set(t.name, t.id);
    cursor = page.has_more ? page.next_cursor : undefined;
  } while (cursor);
  return found;
}

async function toolIdsFor(agentId: string): Promise<Record<string, string>> {
  const agent = await el("GET", `/v1/convai/agents/${agentId}`);
  const ids: string[] = agent.conversation_config?.agent?.prompt?.tool_ids ?? [];
  const all = await el<{ tools: { id: string; tool_config: { name: string } }[] }>("GET", "/v1/convai/tools");
  const out: Record<string, string> = {};
  for (const t of all.tools) if (ids.includes(t.id)) out[t.tool_config.name] = t.id;
  return out;
}

async function configOverride(agentId: string, prompt: string, firstMessage: string) {
  const agent = await el("GET", `/v1/convai/agents/${agentId}`);
  const conversation_config = structuredClone(agent.conversation_config);
  conversation_config.agent.prompt.prompt = prompt;
  conversation_config.agent.first_message = firstMessage;
  return { conversation_config, platform_settings: {} };
}

interface RunRow {
  name: string;
  suite: Suite;
  checks: string;
  passed: number;
  total: number;
  reasons: string[];
  sample: string;
}

function replyText(run: any): string {
  const parts: string[] = [];
  for (const r of run.agent_responses ?? []) {
    for (const c of r.tool_calls ?? []) parts.push(`<${c.tool_name} ${c.params_as_json}>`);
    if (r.message) parts.push(`${r.role === "user" ? "USER: " : ""}${r.message}`);
  }
  return parts.join(" ").replace(/\s+/g, " ").slice(0, 400);
}

async function runSuite(agentId: string, override: object, defs: TestDef[], ids: Map<string, string>): Promise<{ rows: RunRow[]; raw: any }> {
  const tests = defs.map((d) => ({ test_id: ids.get(PREFIX + d.name)! }));
  const started = await el("POST", `/v1/convai/agents/${agentId}/run-tests`, { tests, agent_config_override: override, repeat_count: REPEAT });
  const invocationId = started.id as string;
  let inv: any = started;
  const t0 = Date.now();
  while ((inv.test_runs ?? []).some((r: any) => r.status === "pending") || (inv.test_runs ?? []).length === 0) {
    if (Date.now() - t0 > 20 * 60_000) throw new Error(`invocation ${invocationId} still pending after 20 minutes`);
    await sleep(4000);
    inv = await el("GET", `/v1/convai/test-invocations/${invocationId}`);
  }
  const rows = defs.map((d) => {
    const id = ids.get(PREFIX + d.name);
    const runs = (inv.test_runs as any[]).filter((r) => r.test_id === id);
    const failed = runs.filter((r) => r.status !== "passed");
    return {
      name: d.name,
      suite: d.suite,
      checks: d.checks,
      passed: runs.length - failed.length,
      total: runs.length,
      reasons: failed.map((r) => r.condition_result?.rationale?.summary || r.condition_result?.rationale?.messages?.join(" ") || r.status).filter(Boolean),
      sample: replyText(failed[0] ?? runs[0] ?? {}),
    };
  });
  return { rows, raw: inv };
}

async function main() {
  console.log("Recording fixtures from the service code (Theo Abernathy, open appendectomy)...");
  const f = await recordFixtures();
  const [jarvisTools, patientTools] = await Promise.all([toolIdsFor(JARVIS_AGENT), toolIdsFor(PATIENT_AGENT)]);
  const all = defineTests(f, { ...jarvisTools, ...patientTools });
  const defs = ONLY ? all.filter((d) => ONLY.split(",").some((o) => d.name.includes(o))) : all;
  if (!defs.length) {
    console.error(`jarvis-eval: no tests match --only ${ONLY}`);
    process.exit(2);
  }

  // Idempotent upsert by name.
  const existing = await listOurTests();
  for (const d of defs) {
    const name = PREFIX + d.name;
    const body = { name, ...d.body };
    const id = existing.get(name);
    if (id) {
      await el("PUT", `/v1/convai/agent-testing/${id}`, body);
    } else {
      const created = await el<{ id: string }>("POST", "/v1/convai/agent-testing/create", body);
      existing.set(name, created.id);
    }
  }
  console.log(`Synced ${defs.length} tests (${all.length} defined).`);
  if (flag("prune")) {
    const wanted = new Set(all.map((d) => PREFIX + d.name));
    for (const [name, id] of existing) {
      if (!wanted.has(name)) {
        await el("DELETE", `/v1/convai/agent-testing/${id}`);
        console.log(`deleted stale test ${name}`);
      }
    }
  }
  if (flag("sync-only")) return;

  const groups: { suite: Suite; agent: string; prompt: string; first: string }[] = [
    { suite: "coach", agent: JARVIS_AGENT, prompt: f.surgeryPrompt, first: f.surgeryFirst },
    { suite: "attending", agent: JARVIS_AGENT, prompt: f.attendingPrompt, first: f.attendingFirst },
    { suite: "patient", agent: PATIENT_AGENT, prompt: f.patientPrompt, first: f.patientFirst },
  ];
  console.log(`Running with repeat_count ${REPEAT}. This takes a few minutes.`);
  const results = await Promise.all(
    groups
      .map((g) => ({ g, d: defs.filter((x) => x.suite === g.suite) }))
      .filter((x) => x.d.length)
      .map(async ({ g, d }) => runSuite(g.agent, await configOverride(g.agent, g.prompt, g.first), d, existing)),
  );
  const rows = results.flatMap((r) => r.rows);

  const pad = (s: string, n: number) => (s.length > n ? s.slice(0, n - 1) + "." : s.padEnd(n));
  console.log(`\n${pad("test", 42)} ${pad("result", 8)} rate`);
  console.log("-".repeat(62));
  let runsPassed = 0;
  let runsTotal = 0;
  for (const r of rows) {
    runsPassed += r.passed;
    runsTotal += r.total;
    const verdict = r.total === 0 ? "NO RUNS" : r.passed === r.total ? "PASS" : r.passed === 0 ? "FAIL" : "FLAKY";
    console.log(`${pad(PREFIX + r.name, 42)} ${pad(verdict, 8)} ${r.passed}/${r.total}`);
  }
  console.log("-".repeat(62));
  const fullyPassing = rows.filter((r) => r.total > 0 && r.passed === r.total).length;
  console.log(`${fullyPassing}/${rows.length} tests passed every run; ${runsPassed}/${runsTotal} runs passed (${runsTotal ? Math.round((100 * runsPassed) / runsTotal) : 0}%).`);
  for (const suite of ["coach", "attending", "patient"] as Suite[]) {
    const s = rows.filter((r) => r.suite === suite);
    if (s.length) console.log(`  ${suite}: ${s.reduce((a, r) => a + r.passed, 0)}/${s.reduce((a, r) => a + r.total, 0)} runs`);
  }
  const failing = rows.filter((r) => r.passed < r.total);
  if (failing.length) {
    console.log("\nFailures:");
    for (const r of failing) {
      console.log(`- ${PREFIX}${r.name}: ${r.checks}`);
      for (const reason of [...new Set(r.reasons)].slice(0, 2)) console.log(`    why: ${reason.replace(/\s+/g, " ").slice(0, 300)}`);
      if (r.sample) console.log(`    agent: ${r.sample}`);
    }
  }
  if (OUT) {
    writeFileSync(OUT, JSON.stringify({ repeat: REPEAT, rows, invocations: results.map((r) => r.raw) }, null, 2));
    console.log(`\nRaw results written to ${OUT}`);
  }
  process.exit(failing.length ? 1 : 0);
}

main().catch((err) => {
  console.error(`jarvis-eval: ${err instanceof Error ? err.message : err}`);
  process.exit(1);
});
