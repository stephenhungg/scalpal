import { readFileSync } from "node:fs";
import type { Context, Hono } from "hono";
import { streamSSE } from "hono/streaming";
import { ANATOMY, ANATOMY_BY_ID } from "./catalog/anatomy.js";
import { STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import { CoachSession, PRESENTATION_MODES, reflexLines, renderContext, type CoachEvent, type StuckPolicy } from "./coach.js";
import { ReflexAudio } from "./reflex.js";
import { buildSystemPrompt, firstMessage } from "./coach-prompt.js";
import type { Action, SurgicalCase } from "./types.js";

// Live coach API. Unity (or the SpacetimeDB bridge) posts exercise events here; the Jarvis voice
// page reads state, hints, and alerts from it. Sessions live in memory: fine for one demo laptop,
// and the event contract is what moves to SpacetimeDB later.

export interface CoachRouteOptions {
  loadCase: (patientId: string) => Promise<SurgicalCase | null>;
  now: () => Date;
  stuckPolicy?: StuckPolicy;
  tickMs?: number; // 0 disables the background stuck timer (tests call tick directly)
  elevenLabs?: { apiKey: string; agentId: string; voiceId?: string };
  reflex?: ReflexAudio; // injectable for tests; built from elevenLabs when omitted
}

const MAX_SESSIONS = 50;
const SESSION_ID = /^coach-[a-z0-9]{6,40}$/;

const ALIASES: Record<string, string> = {
  cbd: "common_bile_duct",
  chd: "common_hepatic_duct",
  rha: "right_hepatic_artery",
  ima: "inferior_mesenteric_artery",
  "gall bladder": "gallbladder",
  omentum: "greater_omentum",
  "appendiceal artery": "appendicular_artery",
  ileum: "terminal_ileum",
  bladder: "urinary_bladder",
  ureter: "", // ambiguous: resolved against the case below
};

const coachActions = (sid: string): Action[] => [
  { id: "coach_state", label: "Coach state", method: "GET", route: `/coach/sessions/${sid}` },
  { id: "coach_hint", label: "Get a hint", method: "POST", route: `/coach/sessions/${sid}/hint` },
  { id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" },
];

export function registerCoachRoutes(app: Hono, options: CoachRouteOptions) {
  const sessions = new Map<string, CoachSession>();
  const reflex =
    options.reflex ?? (options.elevenLabs?.apiKey && options.elevenLabs.voiceId ? new ReflexAudio({ apiKey: options.elevenLabs.apiKey, voiceId: options.elevenLabs.voiceId }) : null);
  let ticker: ReturnType<typeof setInterval> | null = null;
  const tickMs = options.tickMs ?? 1000;

  const ensureTicker = () => {
    if (ticker || tickMs <= 0) return;
    ticker = setInterval(() => {
      for (const s of sessions.values()) if (s.listenerCount > 0) s.tick();
    }, tickMs);
    ticker.unref?.();
  };

  const bad = (c: Context, status: 400 | 404 | 409 | 503, code: string, message: string, actions: Action[]) =>
    c.json({ error: { code, message }, actions }, status);

  const getSession = (c: Context): CoachSession | null => {
    const sid = c.req.param("sid") ?? "";
    return SESSION_ID.test(sid) ? (sessions.get(sid) ?? null) : null;
  };

  const missing = (c: Context) =>
    bad(c, 404, "coach_session_not_found", "No live coach session with that id. Start one from a patient.", [
      { id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" },
    ]);

  const body = async (c: Context) => (await c.req.json().catch(() => ({}))) as Record<string, unknown>;

  app.post("/coach/sessions", async (c) => {
    const { patientId, mode: rawMode } = await body(c);
    const mode = rawMode === undefined ? "mixed_reality" : PRESENTATION_MODES.find((m) => m === rawMode);
    if (!mode) return bad(c, 400, "invalid_mode", 'mode must be "mixed_reality" or "virtual".', [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    const kase = typeof patientId === "string" ? await options.loadCase(patientId) : null;
    if (!kase) return bad(c, 404, "patient_not_found", 'Send {"patientId": "<FinchNode subject>"} for a known patient.', [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    if (!kase.procedureId) return bad(c, 409, "case_unavailable", kase.statusReason, kase.actions);

    if (sessions.size >= MAX_SESSIONS) sessions.delete(sessions.keys().next().value!);
    const sid = `coach-${crypto.randomUUID().replaceAll("-", "").slice(0, 16)}`;
    const session = new CoachSession(sid, kase, options.now, options.stuckPolicy, mode);
    sessions.set(sid, session);
    const snapshot = session.snapshot();
    return c.json(
      {
        sessionId: sid,
        snapshot,
        context: renderContext(snapshot),
        systemPrompt: buildSystemPrompt(kase, mode),
        firstMessage: firstMessage(kase),
        actions: coachActions(sid),
      },
      201,
    );
  });

  // The headset adopts the newest live session (the laptop Jarvis page creates it). Single-room demo
  // shortcut; SpacetimeDB session membership replaces it.
  app.get("/coach/current", (c) => {
    const patientId = c.req.query("patientId") ?? "";
    const latest = [...sessions.values()].reverse().find((s) => !patientId || s.kase.patientId === patientId || s.kase.scenarioId === patientId);
    if (!latest) return bad(c, 404, "no_live_session", "No live coach session yet. Start one from the Jarvis page.", [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    return c.json({ sessionId: latest.id, patientId: latest.kase.patientId, procedureId: latest.kase.procedureId, actions: coachActions(latest.id) });
  });

  app.get("/coach/sessions/:sid", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const snapshot = s.snapshot();
    return c.json({ snapshot, context: renderContext(snapshot), actions: coachActions(s.id) });
  });

  app.post("/coach/sessions/:sid/events", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const b = await body(c);
    const raw = Array.isArray(b.events) ? b.events : b.event ? [b.event] : [];
    if (!raw.length) return bad(c, 400, "no_events", 'Send {"event": {...}} or {"events": [...]}.', coachActions(s.id));
    // Each event stands alone: the headset batches events, so one unknown id (say, an instrument the
    // catalog doesn't have yet) must not drop the valid events around it.
    const results = raw.map((e) => {
      const parsed = parseEvent(e, s);
      return typeof parsed === "string" ? { accepted: false, reason: `invalid: ${parsed}`, alerts: [] } : s.handle(parsed);
    });
    if (results.every((r) => r.reason.startsWith("invalid: "))) {
      return bad(c, 400, "invalid_event", results.map((r, i) => `events[${i}]: ${r.reason.slice(9)}`).join("; "), coachActions(s.id));
    }
    const snapshot = s.snapshot();
    return c.json({
      results: results.map((r) => ({ accepted: r.accepted, reason: r.reason })),
      alerts: results.flatMap((r) => r.alerts),
      snapshot,
      context: renderContext(snapshot),
      actions: coachActions(s.id),
    });
  });

  app.post("/coach/sessions/:sid/hint", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    return c.json({ ...s.requestHint(), actions: coachActions(s.id) });
  });

  app.post("/coach/sessions/:sid/explain", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { structure } = await body(c);
    const query = typeof structure === "string" ? structure : "";
    const match = resolveStructure(query, s.kase);
    if (match.kind === "none") {
      return c.json({ found: false, structureId: "", say: `I don't have a structure called "${query}" in this case.`, actions: coachActions(s.id) });
    }
    if (match.kind === "other_case") {
      return c.json({
        found: false,
        structureId: match.id,
        say: `The ${ANATOMY_BY_ID.get(match.id)?.displayName.toLowerCase()} isn't part of the ${s.kase.procedure.title.toLowerCase()}.`,
        actions: coachActions(s.id),
      });
    }
    const a = s.kase.anatomy.find((x) => x.id === match.id)!;
    const f = STRUCTURE_FACTS[a.id];
    return c.json({
      found: true,
      structureId: a.id,
      name: a.displayName,
      what: f?.what ?? "",
      where: f?.where ?? "",
      supply: f?.supply ?? "",
      why: f?.why ?? "",
      say: f ? `${a.displayName}: ${f.what} ${f.where} ${f.why}` : a.displayName,
      actions: coachActions(s.id),
    });
  });

  app.post("/coach/sessions/:sid/commands", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { action, structure } = await body(c);
    if (action !== "highlight" && action !== "clear_highlight") return bad(c, 400, "invalid_command", "action must be highlight or clear_highlight.", coachActions(s.id));
    const match = resolveStructure(typeof structure === "string" ? structure : "", s.kase);
    const targetId = match.kind === "case" ? match.id : action === "clear_highlight" ? "" : null;
    if (targetId == null) return bad(c, 409, "unknown_structure", `"${String(structure)}" is not part of this case's anatomy.`, coachActions(s.id));
    const command = s.requestCommand(action, targetId);
    if ("error" in command) return bad(c, 409, "unknown_structure", command.error, coachActions(s.id));
    return c.json({ command, actions: coachActions(s.id) });
  });

  app.get("/coach/sessions/:sid/commands", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    return c.json({ commands: s.pendingCommands(), actions: coachActions(s.id) });
  });

  app.post("/coach/sessions/:sid/commands/:cid/ack", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { status, reason } = await body(c);
    if (status !== "applied" && status !== "rejected") return bad(c, 400, "invalid_ack", "status must be applied or rejected.", coachActions(s.id));
    const command = s.ackCommand(c.req.param("cid") ?? "", status, typeof reason === "string" ? reason : "");
    if (!command) return bad(c, 404, "command_not_found", "No such command in this session.", coachActions(s.id));
    return c.json({ command, actions: coachActions(s.id) });
  });

  // Demo driver: lets the laptop exercise Jarvis before the headset is wired in.
  app.post("/coach/sessions/:sid/simulate", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { kind } = await body(c);
    let events: CoachEvent[] = [];
    const stepResults: ReturnType<CoachSession["handle"]>[] = [];
    switch (kind) {
      case "correct_action": {
        const e = s.nextCorrectEvent();
        events = e ? [e] : [];
        break;
      }
      case "complete_step": {
        const stepId = s.engine.current?.id;
        for (let i = 0; i < 20 && stepId && s.engine.current?.id === stepId; i++) {
          const e = s.nextCorrectEvent();
          if (!e) break;
          stepResults.push(s.handle(e));
        }
        break;
      }
      case "mistake": {
        const e = s.sampleMistakeEvent();
        events = e ? [e] : [];
        break;
      }
      case "wrong_instrument": {
        const step = s.engine.current;
        const other = [...INSTRUMENTS_BY_ID.keys()].find((id) => id !== step?.instrumentId);
        events = step?.targets[0] && other ? [{ type: "touch", structureId: step.targets[0], instrumentId: other }] : [];
        break;
      }
      case "off_target": {
        const step = s.engine.current;
        const avoid = new Set([...(step?.targets ?? []), ...(step?.mistakes.map((m) => m.structure) ?? [])]);
        const other = s.kase.procedure.structures.find((id) => !avoid.has(id));
        events = step && other ? [{ type: "touch", structureId: other, instrumentId: step.instrumentId }] : [];
        break;
      }
      case "look_at_danger": {
        const step = s.engine.current;
        const danger = step?.mistakes.find((m) => !step.targets.includes(m.structure))?.structure;
        events = danger ? [{ type: "focus", structureId: danger }] : [];
        break;
      }
      case "tracking_lost":
        events = [{ type: "tracking", valid: false }];
        break;
      case "tracking_restored":
        events = [{ type: "tracking", valid: true }];
        break;
      default:
        return bad(c, 400, "invalid_simulation", "kind must be correct_action, complete_step, mistake, wrong_instrument, off_target, look_at_danger, tracking_lost, or tracking_restored.", coachActions(s.id));
    }
    const results = [...stepResults, ...events.map((e) => s.handle(e))];
    const snapshot = s.snapshot();
    return c.json({ alerts: results.flatMap((r) => r.alerts), snapshot, context: renderContext(snapshot), actions: coachActions(s.id) });
  });

  // Server-sent events: one "state" message per change (with any alerts), plus the stuck timer.
  app.get("/coach/sessions/:sid/stream", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    ensureTicker();
    return streamSSE(c, async (stream) => {
      const send = (payload: unknown) => stream.writeSSE({ event: "state", data: JSON.stringify(payload) });
      const first = s.snapshot();
      await send({ snapshot: first, context: renderContext(first), alerts: [] });
      const unsubscribe = s.subscribe((u) => void send({ ...u, context: renderContext(u.snapshot) }));
      stream.onAbort(unsubscribe);
      while (!stream.aborted) {
        await stream.sleep(15000);
        if (!stream.aborted) await stream.writeSSE({ event: "ping", data: "{}" });
      }
      unsubscribe();
    });
  });

  // Pre-rendered warning clips for a session's case. The page fetches them all at start so a warning
  // plays instantly, without an LLM turn.
  app.get("/jarvis/reflex/:sid", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const lines = reflexLines(s.kase, s.mode).map((l) => ({ ...l, route: `/jarvis/reflex/${s.id}/${l.key}` }));
    return c.json({ configured: Boolean(reflex?.configured), lines, actions: coachActions(s.id) });
  });

  app.get("/jarvis/reflex/:sid/:key", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const line = reflexLines(s.kase, s.mode).find((l) => l.key === c.req.param("key"));
    if (!line) return bad(c, 404, "reflex_not_found", "No warning line with that key in this case.", coachActions(s.id));
    if (!reflex?.configured) return bad(c, 503, "reflex_unconfigured", "Set ELEVENLABS_API_KEY and JARVIS_VOICE_ID to pre-render warnings.", coachActions(s.id));
    try {
      const audio = await reflex.render(line.text);
      return c.body(new Uint8Array(audio), 200, { "Content-Type": "audio/mpeg", "Cache-Control": "private, max-age=3600" });
    } catch (err) {
      return bad(c, 503, "reflex_render_failed", err instanceof Error ? err.message : "TTS failed.", coachActions(s.id));
    }
  });

  // Jarvis voice page (laptop browser) and its ElevenLabs connection details.
  app.get("/jarvis", (c) => c.html(readFileSync(new URL("./jarvis/index.html", import.meta.url), "utf8")));
  for (const file of ["app.js", "arbiter.js"]) {
    app.get(`/jarvis/${file}`, (c) =>
      c.body(readFileSync(new URL(`./jarvis/${file}`, import.meta.url), "utf8"), 200, { "Content-Type": "text/javascript; charset=utf-8", "Cache-Control": "no-cache" }),
    );
  }

  app.get("/jarvis/connection", async (c) => {
    const el = options.elevenLabs;
    if (!el?.agentId) {
      return bad(c, 503, "jarvis_unconfigured", "Set ELEVENLABS_AGENT_ID (and ELEVENLABS_API_KEY for a private agent), then run npm run jarvis:setup.", [{ id: "home", label: "Home", method: "GET", route: "/" }]);
    }
    if (!el.apiKey) return c.json({ mode: "public", agentId: el.agentId, signedUrl: "", actions: [] });
    const res = await fetch(`https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?agent_id=${encodeURIComponent(el.agentId)}`, {
      headers: { "xi-api-key": el.apiKey },
    }).catch(() => null);
    if (!res?.ok) return bad(c, 503, "elevenlabs_unreachable", `ElevenLabs signed URL request failed${res ? ` (${res.status})` : ""}.`, [{ id: "home", label: "Home", method: "GET", route: "/" }]);
    const { signed_url } = (await res.json()) as { signed_url: string };
    return c.json({ mode: "signed", agentId: el.agentId, signedUrl: signed_url, actions: [] });
  });
}

function parseEvent(e: unknown, s: CoachSession): CoachEvent | string {
  if (!e || typeof e !== "object") return "event must be an object";
  const ev = e as Record<string, unknown>;
  const str = (k: string) => (typeof ev[k] === "string" ? (ev[k] as string) : "");
  switch (ev.type) {
    case "place_port":
      return s.kase.procedure.ports.some((p) => p.id === str("portId")) ? { type: "place_port", portId: str("portId") } : `unknown portId "${str("portId")}"`;
    case "touch":
      if (!ANATOMY_BY_ID.has(str("structureId"))) return `unknown structureId "${str("structureId")}"`;
      if (!INSTRUMENTS_BY_ID.has(str("instrumentId"))) return `unknown instrumentId "${str("instrumentId")}"`;
      return { type: "touch", structureId: str("structureId"), instrumentId: str("instrumentId") };
    case "identify":
      return ANATOMY_BY_ID.has(str("structureId")) ? { type: "identify", structureId: str("structureId") } : `unknown structureId "${str("structureId")}"`;
    case "confirm":
      return { type: "confirm" };
    case "focus":
      return str("structureId") === "" || ANATOMY_BY_ID.has(str("structureId")) ? { type: "focus", structureId: str("structureId") } : `unknown structureId "${str("structureId")}"`;
    case "tracking":
      return typeof ev.valid === "boolean" ? { type: "tracking", valid: ev.valid } : "tracking needs a boolean valid";
    default:
      return "type must be place_port, touch, identify, confirm, focus, or tracking";
  }
}

type Resolution = { kind: "case"; id: string } | { kind: "other_case"; id: string } | { kind: "none" };

// Matches spoken names ("the CBD", "cystic artery", "common_bile_duct") to anatomy ids, preferring this case.
export function resolveStructure(query: string, kase: SurgicalCase): Resolution {
  const q = query.toLowerCase().replaceAll("_", " ").replace(/^(the|a|an)\s+/, "").replace(/[^a-z0-9' ]/g, "").trim();
  if (!q) return { kind: "none" };
  const inCase = new Set(kase.anatomy.map((a) => a.id));
  const norm = (s: string) => s.toLowerCase().replaceAll("_", " ");

  const alias = ALIASES[q];
  if (q === "ureter" || alias === "") {
    const ureter = [...inCase].find((id) => id.endsWith("_ureter"));
    if (ureter) return { kind: "case", id: ureter };
  } else if (alias) {
    return inCase.has(alias) ? { kind: "case", id: alias } : { kind: "other_case", id: alias };
  }

  const score = (id: string, display: string) => {
    const d = norm(display);
    if (norm(id) === q || d === q) return 3;
    if (d.includes(q) || q.includes(d)) return 2;
    return 0;
  };
  const best = (ids: { id: string; displayName: string }[]) =>
    ids.map((a) => ({ id: a.id, s: score(a.id, a.displayName) })).filter((x) => x.s > 0).sort((a, b) => b.s - a.s)[0];

  const hit = best(kase.anatomy);
  if (hit) return { kind: "case", id: hit.id };
  const other = best(ANATOMY);
  if (other) return { kind: "other_case", id: other.id };
  return { kind: "none" };
}
