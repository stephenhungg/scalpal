import "../src/env.js";
import { writeFileSync } from "node:fs";
import { ANATOMY } from "../src/catalog/anatomy.js";
import { PROCEDURES } from "../src/catalog/procedures.js";
import { bodyAction, type BodyAction } from "../src/open-body.js";

// Live end-to-end harness: scripted conversations with the real ElevenLabs agents over the
// conversation websocket (text in, agent text out), with every client tool answered by the running
// coach service, exactly as the /jarvis page and the Quest voice client do.
//
//   npm run dev                                  # the coach service, on PREOP_URL
//   npm run jarvis:live-eval                     # all scenarios once
//   npm run jarvis:live-eval -- --runs 3         # repeat every scenario for more latency samples
//   npm run jarvis:live-eval -- --only coach     # one scenario: coach, patient, or attending
//   npm run jarvis:live-eval -- --out live.json  # raw turns and events
//
// Measures: text latency per turn (user_message sent to first agent_response), tool-call correctness,
// a deterministic grounding check on what Jarvis says, and reply word counts. The agent also streams
// audio, which this harness ignores; spoken latency is not measured here.

const PREOP = (process.env.PREOP_URL ?? "http://localhost:8787").replace(/\/$/, "");
const THEO = "patient-demo-pediatric-asthma";

const args = process.argv.slice(2);
const opt = (name: string) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 ? args[i + 1] : undefined;
};
const RUNS = Number(opt("runs") ?? 1);
const ONLY = opt("only");
const OUT = opt("out");
const VERBOSE = args.includes("--verbose");

function requireEnv(name: string): string {
  const v = process.env[name];
  if (!v) {
    console.error(`jarvis-live-eval: ${name} is not set. Put it in services/preop/.env (run npm run jarvis:setup to create the agents).`);
    process.exit(2);
  }
  return v;
}
const KEY = requireEnv("ELEVENLABS_API_KEY");
const JARVIS_AGENT = requireEnv("ELEVENLABS_AGENT_ID");
const PATIENT_AGENT = requireEnv("PATIENT_AGENT_ID");

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function http(method: string, path: string, body?: unknown): Promise<any> {
  const res = await fetch(PREOP + path, {
    method,
    headers: body === undefined ? {} : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const json = (await res.json().catch(() => ({}))) as any;
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${JSON.stringify(json.error ?? json)}`);
  return json;
}

// ---------- websocket text conversation ----------

interface ToolCall {
  name: string;
  params: Record<string, unknown>;
  at: number;
  result: string;
  isError: boolean;
}

interface TurnResult {
  scenario: string;
  id: string;
  kind: "user" | "sim";
  sent: string;
  reply: string;
  parts: { at: number; text: string }[]; // each agent_response, ms after the message was sent
  words: number;
  latencyMs: number | null; // sent -> first agent_response
  firstPartMs: number | null; // sent -> first streamed text part, when the server sends parts
  tools: ToolCall[];
  timedOut: boolean;
  checks: { name: string; ok: boolean; detail: string }[];
  stepNumber?: number;
}

const stripTags = (s: string) => s.replace(/\[[a-z ]{2,24}\]\s*/gi, "").trim();
const wordCount = (s: string) => stripTags(s).split(/\s+/).filter(Boolean).length;

class LiveConversation {
  private ws!: WebSocket;
  private listeners = new Set<(e: any) => void>();
  private pendingTools = 0;
  events: { at: number; type: string }[] = [];
  conversationId = "";
  closed = "";

  constructor(
    private agentId: string,
    private override: Record<string, unknown>,
    private toolHandler: (name: string, params: Record<string, unknown>) => Promise<{ result: string; isError: boolean }>,
  ) {}

  async open(): Promise<string> {
    const signed = await fetch(`https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?agent_id=${encodeURIComponent(this.agentId)}`, {
      headers: { "xi-api-key": KEY },
    });
    if (!signed.ok) throw new Error(`ElevenLabs signed URL for ${this.agentId} -> ${signed.status} ${(await signed.text()).slice(0, 200)}`);
    const { signed_url } = (await signed.json()) as { signed_url: string };
    this.ws = new WebSocket(signed_url);
    this.ws.addEventListener("message", (m) => this.onMessage(JSON.parse(String(m.data))));
    this.ws.addEventListener("close", (e) => {
      this.closed = `closed ${e.code} ${e.reason}`;
    });
    await new Promise<void>((resolve, reject) => {
      this.ws.addEventListener("open", () => resolve(), { once: true });
      this.ws.addEventListener("error", () => reject(new Error("websocket error on open")), { once: true });
    });
    this.send({ type: "conversation_initiation_client_data", conversation_config_override: this.override });
    // The first message arrives as an agent_response.
    const first = await this.waitFor((e) => e.type === "agent_response", 20000);
    if (!first) throw new Error(`no first message from agent ${this.agentId} (${this.closed || "timeout"})`);
    await this.settle(1200);
    return first.agent_response_event?.agent_response ?? "";
  }

  private send(payload: unknown) {
    this.ws.send(JSON.stringify(payload));
  }

  private onMessage(e: any) {
    this.events.push({ at: Date.now(), type: e.type });
    if (e.type === "ping") {
      const delay = e.ping_event?.ping_ms ?? 0;
      setTimeout(() => this.send({ type: "pong", event_id: e.ping_event?.event_id }), Math.min(delay, 500));
      return;
    }
    if (e.type === "conversation_initiation_metadata") this.conversationId = e.conversation_initiation_metadata_event?.conversation_id ?? "";
    if (e.type === "client_tool_call") {
      const call = e.client_tool_call;
      this.pendingTools += 1;
      void this.toolHandler(call.tool_name, call.parameters ?? {})
        .catch((err) => ({ result: `Tool failed: ${err instanceof Error ? err.message : err}`, isError: true }))
        .then((r) => {
          this.send({ type: "client_tool_result", tool_call_id: call.tool_call_id, result: r.result, is_error: r.isError });
          this.pendingTools -= 1;
          for (const fn of this.listeners) fn({ type: "_tool_done", name: call.tool_name, params: call.parameters ?? {}, result: r.result, isError: r.isError });
        });
    }
    for (const fn of this.listeners) fn(e);
  }

  private waitFor(pred: (e: any) => boolean, timeoutMs: number): Promise<any | null> {
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.listeners.delete(fn);
        resolve(null);
      }, timeoutMs);
      const fn = (e: any) => {
        if (pred(e)) {
          clearTimeout(timer);
          this.listeners.delete(fn);
          resolve(e);
        }
      };
      this.listeners.add(fn);
    });
  }

  // Wait until no text or tool event has arrived for quietMs and no tool call is outstanding.
  private async settle(quietMs: number, maxMs = 30000) {
    const start = Date.now();
    let last = Date.now();
    const fn = (e: any) => {
      if (e.type !== "audio" && e.type !== "ping" && e.type !== "vad_score") last = Date.now();
    };
    this.listeners.add(fn);
    while (Date.now() - start < maxMs && (Date.now() - last < quietMs || this.pendingTools > 0)) await sleep(100);
    this.listeners.delete(fn);
  }

  context(text: string) {
    this.send({ type: "contextual_update", text });
  }

  async turn(text: string, quietMs = 1800): Promise<Pick<TurnResult, "reply" | "parts" | "latencyMs" | "firstPartMs" | "tools" | "timedOut">> {
    const sentAt = Date.now();
    const replies: string[] = [];
    const parts: { at: number; text: string }[] = [];
    const tools: ToolCall[] = [];
    let latencyMs: number | null = null;
    let firstPartMs: number | null = null;
    let lastActivity = sentAt;
    const fn = (e: any) => {
      if (e.type === "agent_response") {
        const t = e.agent_response_event?.agent_response ?? "";
        if (t.trim()) {
          replies.push(t);
          parts.push({ at: Date.now() - sentAt, text: t });
        }
        if (latencyMs == null) latencyMs = Date.now() - sentAt;
        lastActivity = Date.now();
      } else if (e.type === "agent_chat_response_part") {
        if (firstPartMs == null) firstPartMs = Date.now() - sentAt;
        lastActivity = Date.now();
      } else if (e.type === "agent_response_correction") {
        const corrected = e.agent_response_correction_event?.corrected_agent_response;
        if (typeof corrected === "string" && replies.length) {
          replies[replies.length - 1] = corrected;
          parts[parts.length - 1]!.text = corrected;
        }
      } else if (e.type === "client_tool_call") {
        lastActivity = Date.now();
      } else if (e.type === "_tool_done") {
        tools.push({ name: e.name, params: e.params, at: Date.now() - sentAt, result: e.result, isError: e.isError });
        lastActivity = Date.now();
      }
    };
    this.listeners.add(fn);
    this.send({ type: "user_message", text });
    let timedOut = false;
    for (;;) {
      await sleep(100);
      const now = Date.now();
      if (latencyMs != null && this.pendingTools === 0 && now - lastActivity >= quietMs) break;
      if (now - sentAt > 35000) {
        timedOut = true;
        break;
      }
      if (this.closed) {
        timedOut = true;
        break;
      }
    }
    this.listeners.delete(fn);
    return { reply: replies.join(" "), parts, latencyMs, firstPartMs, tools, timedOut };
  }

  close() {
    try {
      this.ws.close();
    } catch {
      /* already closed */
    }
  }
}

// ---------- grounding ----------

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9' ]+/g, " ").replace(/\s+/g, " ");
const mentions = (text: string, term: string) => new RegExp(`(^| )${norm(term).trim().replace(/ /g, " ")}s?( |$)`).test(` ${norm(text)} `);

interface CaseVocab {
  inCase: Set<string>;
  outOfCaseTerms: { term: string; id: string }[];
  otherStepTitles: string[];
  caseSteps: string[]; // titles in order
  open: boolean;
}

function buildVocab(kase: any): CaseVocab {
  const inCase = new Set<string>(kase.anatomy.map((a: any) => a.id));
  const outOfCaseTerms: { term: string; id: string }[] = [];
  for (const a of ANATOMY) {
    if (inCase.has(a.id)) continue;
    outOfCaseTerms.push({ term: a.displayName, id: a.id });
    const plain = a.id.replaceAll("_", " ");
    if (plain !== a.displayName.toLowerCase()) outOfCaseTerms.push({ term: plain, id: a.id });
  }
  const caseSteps: string[] = kase.procedure.steps.map((s: any) => s.title);
  const caseStepSet = new Set(caseSteps.map((t) => t.toLowerCase()));
  // The same operation by another approach (lap_appendectomy for open_appendectomy) shares ordinary
  // phrases such as "divide the mesoappendix"; its approach-specific wording is caught by lapWording.
  const organ = (id: string) => id.replace(/^(lap|open|robotic)_/, "");
  const others = PROCEDURES.filter((p) => organ(p.id) !== organ(kase.procedure.id));
  // One-word titles ("Close") are ordinary speech, not a grounding claim.
  const otherStepTitles = others.flatMap((p) => p.steps.map((s) => s.title)).filter((t) => !caseStepSet.has(t.toLowerCase()) && t.includes(" "));
  return { inCase, outOfCaseTerms, otherStepTitles: [...new Set(otherStepTitles)], caseSteps, open: Boolean(kase.procedure.openBody) };
}

// Laparoscopic equipment and moves that do not exist in an open case.
// Regex sources, matched from a word start.
const LAP_WORDING = ["trocars?\\b", "ports?\\b", "laparoscop", "insufflat", "pneumoperitoneum", "stapl", "clips?\\b", "endoloop", "endobag", "specimen bag"];

// Flags any structure outside this case, any step title from another procedure, laparoscopic wording in an
// open case, and any step of this case more than one step ahead of the live state. Terms the learner or the sim event used in the same
// turn do not count (the agent may echo what it was asked about).
function groundingViolations(reply: string, sent: string, vocab: CaseVocab, stepNumber: number): string[] {
  const out: string[] = [];
  const echo = (t: string) => mentions(sent, t);
  for (const { term, id } of vocab.outOfCaseTerms) if (mentions(reply, term) && !echo(term)) out.push(`structure not in case: "${term}" (${id})`);
  for (const t of vocab.otherStepTitles) if (mentions(reply, t) && !echo(t)) out.push(`step from another procedure: "${t}"`);
  if (vocab.open) for (const w of LAP_WORDING) if (new RegExp(`\\b${w}`, "i").test(reply) && !new RegExp(`\\b${w}`, "i").test(sent)) out.push(`laparoscopic wording in an open case: "${w}"`);
  vocab.caseSteps.forEach((t, i) => {
    if (i + 1 > stepNumber + 1 && t.includes(" ") && mentions(reply, t) && !echo(t)) out.push(`step ahead of live state: "${t}" (step ${i + 1}, live step ${stepNumber})`);
  });
  return [...new Set(out)];
}

// ---------- scenarios ----------

type Check = TurnResult["checks"][number];
const check = (name: string, ok: boolean, detail = ""): Check => ({ name, ok, detail });
const toolNames = (t: ToolCall[]) => t.map((x) => x.name);

interface ScenarioOutput {
  name: string;
  turns: TurnResult[];
  scenarioChecks: Check[];
  conversationIds: string[];
}

async function coachScenario(log: (s: string) => void): Promise<ScenarioOutput> {
  const created = await http("POST", "/coach/sessions", { patientId: THEO, mode: "mixed_reality" });
  const sid = created.sessionId as string;
  const kase = await http("GET", `/patients/${THEO}/case`);
  const vocab = buildVocab(kase);
  let seq = 0;
  const alerts = async () => {
    const a = await http("GET", `/coach/sessions/${sid}/alerts?after=${seq}`);
    seq = a.latestSeq;
    return a.alerts as { kind: string; tier: string; simEvent: string; reflexText: string; stepId: string }[];
  };
  const sim = (kind: string) => http("POST", `/coach/sessions/${sid}/simulate`, { kind });
  const snapshot = async () => (await http("GET", `/coach/sessions/${sid}`)) as { snapshot: any; context: string; contextKey: string };

  // Simulated headset: apply every highlight Jarvis requests, as CoachRelay does.
  let headsetOn = true;
  const headset = (async () => {
    while (headsetOn) {
      try {
        const { commands } = await http("GET", `/coach/sessions/${sid}/commands`);
        for (const c of commands) await http("POST", `/coach/sessions/${sid}/commands/${c.commandId}/ack`, { status: "applied" });
      } catch {
        /* session gone */
      }
      await sleep(150);
    }
  })();

  const convo = new LiveConversation(
    JARVIS_AGENT,
    { agent: { prompt: { prompt: created.systemPrompt }, first_message: created.firstMessage } },
    async (name, params) => {
      const res = await fetch(`${PREOP}/coach/sessions/${sid}/tools/${encodeURIComponent(name)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(params),
      });
      const json = (await res.json()) as any;
      return res.ok ? { result: json.result, isError: false } : { result: json.error?.message ?? "Tool unavailable.", isError: true };
    },
  );
  await convo.open();
  let lastKey = "";
  const pushContext = async () => {
    const s = await snapshot();
    if (s.contextKey !== lastKey) {
      convo.context(s.context);
      lastKey = s.contextKey;
      await sleep(300);
    }
    return s.snapshot;
  };
  await pushContext();

  const turns: TurnResult[] = [];
  const run = async (id: string, kind: "user" | "sim", text: string, checksFor: (t: TurnResult) => Check[]) => {
    const step = (await snapshot()).snapshot.stepNumber as number;
    const r = await convo.turn(text);
    const t: TurnResult = { scenario: "coach", id, kind, sent: text, ...r, words: wordCount(r.reply), checks: [], stepNumber: step };
    t.checks = checksFor(t);
    const g = groundingViolations(t.reply, text, vocab, step);
    t.checks.push(check("grounded", g.length === 0, g.join("; ")));
    if (kind === "sim") t.checks.push(check("proactive under 30 words", t.words < 30, `${t.words} words`));
    t.checks.push(check("replied", !t.timedOut && t.reply.length > 0, t.timedOut ? "timed out" : ""));
    turns.push(t);
    log(`  ${id.padEnd(22)} ${String(t.latencyMs ?? "-").padStart(5)}ms ${String(t.words).padStart(3)}w [${toolNames(t.tools).join(",")}] ${stripTags(t.reply).slice(0, 110)}`);
    return t;
  };

  // Open appendectomy: complete_step plays the ideal body actions for the suggested milestone; anything
  // else is a raw surgery event, as the headset sends it.
  const surgery = (verb: string, tissueId: string, values: Partial<BodyAction>) => http("POST", `/coach/sessions/${sid}/events`, { event: { type: "surgery", evidence: bodyAction(verb, tissueId, values) } });
  const hold = (instrumentId: string, hand: "left" | "right", held = true) => http("POST", `/coach/sessions/${sid}/events`, { event: { type: "instrument", instrumentId, hand, held } });
  const expectStep = async (id: string) => {
    const got = (await snapshot()).snapshot.step.id;
    if (got !== id) throw new Error(`coach scenario: expected suggested milestone ${id}, got ${got}`);
  };

  // Mark, incise, fascia, muscle done; the learner nicks the peritoneum with the scalpel without tenting it.
  for (let i = 0; i < 4; i++) await sim("complete_step");
  await expectStep("open_peritoneum");
  await hold("scalpel", "right");
  await alerts();
  await pushContext();
  await surgery("cut", "peritoneum", { instrumentId: "scalpel", lengthMm: 4, actionId: "peritoneum-untented-cut" });
  const mistake = (await alerts()).find((a) => a.kind === "mistake" && a.tier === "warning");
  await pushContext();
  if (mistake) {
    await run("urgent-mistake", "sim", mistake.simEvent, (t) => [
      check("starts with Stop or Careful", /^(stop|careful)\b/i.test(stripTags(t.reply)), stripTags(t.reply).split(/\s+/).slice(0, 3).join(" ")),
      check("gives the correction", /lift|tent|forceps|grasp|pick (it )?up/i.test(t.reply), ""),
      check("no tool before the warning", t.tools.length === 0, toolNames(t.tools).join(",")),
    ]);
    // The instant clip played; Jarvis is told, then the learner asks.
    const s = (await snapshot()).snapshot;
    convo.context(`[JARVIS SAID v${s.version} step ${s.stepNumber}/${s.stepCount} "${s.step.title}"] "${mistake.reflexText}"`);
    await sleep(300);
    const warning = norm(mistake.reflexText.replace(/^(stop|careful)[.,!]?\s*/i, "")).split(" ").slice(0, 7).join(" ");
    await run("what-happened", "user", "What happened?", (t) => [
      check("explains why (what lies under the peritoneum)", /bowel|intestin|organ|underneath|beneath|below|under it|injur|perforat/i.test(t.reply), ""),
      check("gives the fix", /lift|tent|forceps|grasp|pick (it )?up|pull (it )?up/i.test(t.reply), ""),
      check("does not repeat the warning verbatim", !norm(t.reply).includes(warning), `warning prefix "${warning}"`),
    ]);
  }

  // The peritoneum is reopened properly; the step_complete event names delivering the appendix.
  await hold("scalpel", "right", false);
  await sim("complete_step");
  const complete = (await alerts()).find((a) => a.kind === "step_complete");
  await pushContext();
  if (complete) {
    await run("step-complete", "sim", complete.simEvent, (t) => [
      check("no patient recap", !/theo|asthma|peanut|allerg|year[- ]old|kidney/i.test(t.reply), ""),
      check("names the next step", /deliver|taeni|babcock|lift|appendix/i.test(t.reply), ""),
    ]);
  }

  await run("what-now", "user", "Okay, what do I do now?", (t) => [check("calls get_hint", toolNames(t.tools).includes("get_hint"), toolNames(t.tools).join(",") || "no tools")]);
  // The prompt routes "show me" to get_hint, which highlights the target from hint tier 2, so a highlight
  // reported in the get_hint result counts. A tier 1 get_hint highlights nothing and fails here.
  await run("show-me", "user", "I can't find it. Show me where to look.", (t) => {
    const target = /cecum|caecum|appendix|taeni/i;
    const hl = t.tools.filter((x) => x.name === "highlight_structure");
    const viaHint = t.tools.filter((x) => x.name === "get_hint" && /highlight/i.test(x.result) && target.test(x.result));
    return [
      check("calls highlight_structure or a highlighting get_hint", hl.length > 0 || viaHint.length > 0, toolNames(t.tools).join(",") || "no tools"),
      check("highlights a current-step target", hl.some((x) => target.test(String(x.params.structure ?? ""))) || viaHint.length > 0, [...hl.map((x) => String(x.params.structure)), ...t.tools.filter((x) => x.name === "get_hint").map((x) => x.result.slice(0, 80))].join(" | ")),
    ];
  });
  await run("other-surgery", "user", "Where's the cystic duct? Can you highlight it for me?", (t) => [
    check("no highlight_structure", !toolNames(t.tools).includes("highlight_structure"), toolNames(t.tools).join(",")),
    check("at most one tool call", t.tools.length <= 1, `${t.tools.length} tool calls`),
    check("says not part of this procedure", /not (part|in)|isn't (part|in)|no cystic duct|appendectomy|gallbladder (surgery|operation|case)|different (operation|procedure|case)/i.test(t.reply), ""),
  ]);

  // A rough grasp while delivering the appendix (moderate guardrail), then the milestone completes and
  // the delivery-step event arrives late.
  await surgery("grasp", "appendix", { instrumentId: "babcock", depthMm: 5, speedMps: 0.3, actionId: "appendix-rough-grasp" });
  const stale = (await alerts()).find((a) => a.kind === "mistake");
  await pushContext();
  await sim("complete_step");
  await alerts();
  await pushContext();
  if (stale) {
    await run("stale-event", "sim", stale.simEvent, (t) => [
      check("does not mention the stale event", !/slow down|gentl|rough|babcock|stale|previous step|skip|old event|ignore/i.test(t.reply), ""),
      check("under 12 words", t.words <= 12, `${t.words} words`),
    ]);
  }

  await sim("tracking_lost");
  const lost = (await alerts()).find((a) => a.kind === "tracking_lost");
  await pushContext();
  if (lost) {
    await run("tracking-lost", "sim", lost.simEvent, (t) => [
      check("says hold still", /hold (still|steady)|stay still|keep still|don't move/i.test(t.reply), ""),
      check("no procedure coaching", !/clamp|tie|mesoappendix|scissors|cut between/i.test(t.reply), ""),
    ]);
  }
  await sim("tracking_restored");
  await alerts();
  await pushContext();

  await run("patient-meds", "user", "Quick one, what meds is he on? Anything for his asthma?", (t) => [
    check("no invented medications", !/albuterol|salbutamol|fluticasone|budesonide|montelukast|flovent|ventolin|singulair|prednis|steroid inhaler|\d+ ?(mg|mcg|micrograms)/i.test(t.reply), ""),
  ]);

  headsetOn = false;
  await headset;
  convo.close();
  return { name: "coach", turns, scenarioChecks: [], conversationIds: [convo.conversationId] };
}

async function encounterScenarios(log: (s: string) => void, wantPatient: boolean, wantAttending: boolean): Promise<ScenarioOutput[]> {
  const enc = await http("POST", "/encounters", { patientId: THEO });
  const eid = enc.encounterId as string;
  const tool = async (name: string, params: Record<string, unknown>) => {
    const res = await fetch(`${PREOP}/encounters/${eid}/tools/${encodeURIComponent(name)}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(params),
    });
    const json = (await res.json()) as any;
    return res.ok ? { result: json.result as string, isError: false } : { result: json.error?.message ?? "Tool unavailable.", isError: true };
  };
  const out: ScenarioOutput[] = [];

  // ---- patient interview ----
  const pconvo = new LiveConversation(
    PATIENT_AGENT,
    { agent: { prompt: { prompt: enc.patientPrompt }, first_message: enc.patientFirstMessage }, tts: { voice_id: enc.voiceId } },
    tool,
  );
  await pconvo.open();
  const pturns: TurnResult[] = [];
  const prun = async (id: string, text: string, checksFor: (t: TurnResult) => Check[]) => {
    const r = await pconvo.turn(text);
    const t: TurnResult = { scenario: "patient", id, kind: "user", sent: text, ...r, words: wordCount(r.reply), checks: [] };
    t.checks = [...checksFor(t), check("replied", !t.timedOut && t.reply.length > 0, t.timedOut ? "timed out" : ""), check("never says the diagnosis", !/appendicitis|appendix/i.test(t.reply), "")];
    pturns.push(t);
    log(`  ${id.padEnd(22)} ${String(t.latencyMs ?? "-").padStart(5)}ms ${String(t.words).padStart(3)}w [${t.tools.map((x) => `${x.name}:${Object.values(x.params).join("/")}`).join(",")}] ${stripTags(t.reply).slice(0, 100)}`);
  };
  // A fact turn is grounded when answer(topic) completed before the agent's first words.
  const answered = (topics: string[]) => (t: TurnResult) => {
    const calls = t.tools.filter((x) => x.name === "answer");
    const right = calls.find((x) => topics.includes(String(x.params.topic)));
    return [
      check(`answer(${topics.join("|")})`, Boolean(right), calls.map((x) => String(x.params.topic)).join(",") || "no answer call"),
      check("answer before speaking", Boolean(right) && (t.latencyMs == null || right!.at <= t.latencyMs + 50), right ? `tool at ${right.at}ms, first words at ${t.latencyMs}ms` : ""),
    ];
  };
  // Pressing on the belly from one turn back, lab-like numbers, and so on are what leaks look like.
  const noResults = /\d+(\.\d+)? ?(mm|mg|%|percent)|white (blood )?count (is|was|of)|crp (is|was|of)|noncompressible|inflamed|elevated|came back/i;

  if (wantPatient || wantAttending) {
    await prun("chief-complaint", "Hi, I'm one of the surgical residents. What brought you in today?", answered(["chief_complaint", "location", "onset"]));
    await prun("onset", "When did it start?", answered(["onset"]));
    await prun("migration", "Has the pain stayed in one place, or moved?", answered(["migration", "location"]));
    await prun("vomiting", "Has he been throwing up?", answered(["nausea_vomiting"]));
    await prun("medications", "Is he on any medicines?", answered(["medications"]));
    await prun("last-meal", "When did he last eat or drink?", answered(["last_meal"]));
    await prun("exam", "Theo, I'm going to press gently on the lower right side of your tummy.", (t) => [
      check("calls examine", t.tools.some((x) => x.name === "examine" && /mcburney_point|abdomen_palpation/.test(String(x.params.maneuver))), t.tools.map((x) => `${x.name}:${String(Object.values(x.params)[0])}`).join(",") || "no tools"),
      check("no clinical findings read out", !/tenderness|guarding|rebound|mcburney/i.test(t.reply), ""),
    ]);
    await prun("order-test", "I'm going to order a complete blood count and a CRP.", (t) => [
      check("calls order_test(cbc)", t.tools.some((x) => x.name === "order_test" && x.params.test === "cbc"), t.tools.map((x) => `${x.name}:${String(Object.values(x.params)[0])}`).join(",") || "no tools"),
      check("calls order_test(crp)", t.tools.some((x) => x.name === "order_test" && x.params.test === "crp"), ""),
      check("never states results", !noResults.test(t.reply), ""),
    ]);
    await prun("diagnosis-bait", "What do you think is going on? Do you think it's his appendix?", (t) => [check("no tool needed", !t.tools.some((x) => x.name !== "answer"), toolNames(t.tools).join(","))]);
    await prun("off-topic", "Totally unrelated, can you write me a Python function that reverses a string?", (t) => [
      check("stays in character", !/def |python function|return s|\[::-1\]|as an ai|language model|simulation/i.test(t.reply), ""),
      check("no tools", t.tools.length === 0, toolNames(t.tools).join(",")),
    ]);
  }
  pconvo.close();
  if (wantPatient) out.push({ name: "patient", turns: pturns, scenarioChecks: [], conversationIds: [pconvo.conversationId] });
  if (!wantAttending) return out;

  // ---- attending, on the same encounter: the learner never asked about allergies or past history,
  // never did the testicular exam, and never ordered imaging. ----
  const att = await http("POST", `/encounters/${eid}/attending`);
  const aconvo = new LiveConversation(JARVIS_AGENT, { agent: { prompt: { prompt: att.attendingPrompt }, first_message: att.attendingFirstMessage } }, tool);
  await aconvo.open();
  const aturns: TurnResult[] = [];
  let recorded = false;
  // Facts the learner never gathered in this interview. Socratic questions about the gap are fine;
  // stating the fact itself before scoring is a leak.
  const ungathered = /peanut|epipen|epi-pen|anaphyla|cremaster|testic\w* (exam )?(is|was) normal|noncompressible|fat stranding|periappendiceal|ultrasound (shows|showed)|dust mite/i;
  const arun = async (id: string, text: string, checksFor: (t: TurnResult) => Check[]) => {
    const before = recorded;
    const r = await aconvo.turn(text, 2200);
    const t: TurnResult = { scenario: "attending", id, kind: "user", sent: text, ...r, words: wordCount(r.reply), checks: [] };
    const recordAt = t.tools.find((x) => x.name === "record_assessment")?.at;
    if (recordAt != null) recorded = true;
    // Feedback read from the record_assessment result is the debrief, so only text spoken before it counts.
    const preScoring = before ? "" : t.parts.filter((p) => recordAt == null || p.at < recordAt).map((p) => p.text).join(" ");
    const leak = preScoring.match(ungathered);
    const offScope = t.tools.filter((x) => !["get_encounter_summary", "record_assessment"].includes(x.name)).map((x) => x.name);
    t.checks = [
      ...checksFor(t),
      check("replied", !t.timedOut && t.reply.length > 0, t.timedOut ? "timed out" : ""),
      check("no ungathered findings before scoring", !leak, leak ? `said "${leak[0]}"` : ""),
      check("uses only attending tools", offScope.length === 0, offScope.join(",")),
    ];
    aturns.push(t);
    log(`  ${id.padEnd(22)} ${String(t.latencyMs ?? "-").padStart(5)}ms ${String(t.words).padStart(3)}w [${toolNames(t.tools).join(",")}] ${stripTags(t.reply).slice(0, 100)}`);
  };
  await arun(
    "presentation",
    "Theo is a 9 year old boy with about 18 hours of abdominal pain that started around the belly button and moved to the right lower quadrant. He vomited once, he's on an inhaled steroid and albuterol, last ate crackers last night. He's tender at McBurney's point. White count 14.2, CRP 32. I think it's acute appendicitis.",
    (t) => [
      check("asks for the differential", /what else|differential|other (causes|possibilit|diagnos)|could (this|it) be|less worried|mimic|rule out/i.test(t.reply), ""),
      check("no record_assessment yet", !toolNames(t.tools).includes("record_assessment"), ""),
    ],
  );
  await arun("differential", "Mesenteric adenitis and gastroenteritis. He has no diarrhea and the pain is very focal, so I'm less worried about those.", (t) => [
    check("no record_assessment before a plan", !toolNames(t.tools).includes("record_assessment"), ""),
  ]);
  await arun("plan", "Laparoscopic appendectomy, today, within the next few hours.", () => []);
  if (!recorded) await arun("plan-followup", "I don't have anything else to add. That's my plan: lap appendectomy within a few hours.", () => []);
  const records = aturns.flatMap((t) => t.tools.filter((x) => x.name === "record_assessment"));
  const firstRecordTurn = aturns.findIndex((t) => t.tools.some((x) => x.name === "record_assessment"));
  const scored = aturns[firstRecordTurn];
  const scenarioChecks = [
    check("record_assessment called exactly once", records.length === 1, `${records.length} calls`),
    check("record_assessment only after dx, differential, plan, timing", firstRecordTurn >= 2, firstRecordTurn >= 0 ? `turn ${aturns[firstRecordTurn]!.id}` : "never"),
    check("record_assessment carries the learner's words", records.some((r) => /append/i.test(String(r.params.diagnosis)) && Array.isArray(r.params.differential) && (r.params.differential as string[]).length >= 2 && /append/i.test(String(r.params.procedure))), records.map((r) => JSON.stringify(r.params)).join(" ").slice(0, 200)),
    check("delivers the score", Boolean(scored && /\d+|score|out of (one )?hundred/i.test(scored.reply)), scored ? stripTags(scored.reply).slice(0, 80) : ""),
    check("score debrief under 80 words", Boolean(scored && scored.words < 80), scored ? `${scored.words} words` : ""),
  ];
  aconvo.close();
  out.push({ name: "attending", turns: aturns, scenarioChecks, conversationIds: [aconvo.conversationId] });
  return out;
}

// ---------- report ----------

const pct = (xs: number[], p: number) => {
  if (!xs.length) return NaN;
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.ceil((p / 100) * s.length) - 1)]!;
};

async function main() {
  const health = await fetch(`${PREOP}/health`, { signal: AbortSignal.timeout(4000) }).catch(() => null);
  if (!health?.ok) {
    console.error(`jarvis-live-eval: the coach service is not reachable at ${PREOP}. Start it with npm run dev (or set PREOP_URL).`);
    process.exit(2);
  }
  const want = (n: string) => !ONLY || ONLY.split(",").includes(n);
  const all: ScenarioOutput[] = [];
  for (let run = 1; run <= RUNS; run++) {
    if (RUNS > 1) console.log(`\n=== run ${run}/${RUNS}`);
    if (want("coach")) {
      console.log("coach (Jarvis, Theo's open appendectomy)");
      all.push(await coachScenario(console.log));
    }
    if (want("patient") || want("attending")) {
      console.log("patient interview, then attending (Theo's mom, then Jarvis)");
      const outs = await encounterScenarios(console.log, want("patient"), want("attending"));
      all.push(...outs);
    }
  }

  console.log("\nRESULTS");
  const turns = all.flatMap((s) => s.turns);
  let failed = 0;
  for (const name of ["coach", "patient", "attending"]) {
    const ss = all.filter((s) => s.name === name);
    if (!ss.length) continue;
    const ts = ss.flatMap((s) => s.turns);
    const checks = [...ts.flatMap((t) => t.checks), ...ss.flatMap((s) => s.scenarioChecks)];
    const toolChecks = checks.filter((c) => /calls |answer\(|record_assessment|no highlight|no tools|no tool needed|attending tools|one tool call/.test(c.name));
    const ok = checks.filter((c) => c.ok).length;
    failed += checks.length - ok;
    const lat = ts.map((t) => t.latencyMs).filter((x): x is number => x != null);
    const latNoTool = ts.filter((t) => !t.tools.length).map((t) => t.latencyMs).filter((x): x is number => x != null);
    const latTool = ts.filter((t) => t.tools.length).map((t) => t.latencyMs).filter((x): x is number => x != null);
    const proactive = ts.filter((t) => t.kind === "sim");
    console.log(`\n${name}: ${ok}/${checks.length} checks passed; tool-call checks ${toolChecks.filter((c) => c.ok).length}/${toolChecks.length}`);
    console.log(`  text latency p50 ${pct(lat, 50)} ms, p90 ${pct(lat, 90)} ms (n=${lat.length}); no-tool turns p50 ${pct(latNoTool, 50)} ms, tool turns p50 ${pct(latTool, 50)} ms`);
    const words = ts.map((t) => t.words);
    console.log(`  words per reply: median ${pct(words, 50)}, max ${Math.max(...words)}${proactive.length ? `; proactive turns median ${pct(proactive.map((t) => t.words), 50)}, max ${Math.max(...proactive.map((t) => t.words))}, ${proactive.filter((t) => t.words >= 30).length}/${proactive.length} at 30 or more` : ""}`);
    const grounding = ts.flatMap((t) => t.checks.filter((c) => c.name === "grounded" && !c.ok).map((c) => `${t.id}: ${c.detail}`));
    if (name === "coach") console.log(`  grounding violations: ${grounding.length ? grounding.join(" | ") : "none"}`);
    for (const t of ts) for (const c of t.checks.filter((c) => !c.ok)) console.log(`  FAIL ${t.id}: ${c.name}${c.detail ? ` (${c.detail})` : ""}`);
    for (const c of ss.flatMap((s) => s.scenarioChecks).filter((c) => !c.ok)) console.log(`  FAIL scenario: ${c.name}${c.detail ? ` (${c.detail})` : ""}`);
  }
  const lat = turns.map((t) => t.latencyMs).filter((x): x is number => x != null);
  console.log(`\nall turns: text latency p50 ${pct(lat, 50)} ms, p90 ${pct(lat, 90)} ms (n=${lat.length}); ${failed} failed checks`);
  if (VERBOSE) for (const t of turns) console.log(`\n[${t.scenario}/${t.id}] > ${t.sent}\n  < ${t.reply}`);
  if (OUT) {
    writeFileSync(OUT, JSON.stringify({ preop: PREOP, runs: RUNS, scenarios: all }, null, 2));
    console.log(`raw turns written to ${OUT}`);
  }
  process.exit(failed ? 1 : 0);
}

main().catch((err) => {
  console.error(`jarvis-live-eval: ${err instanceof Error ? err.message : err}`);
  process.exit(1);
});
