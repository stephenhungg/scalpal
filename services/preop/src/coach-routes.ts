import { validBodyAction, type BodyAction } from "./open-body.js";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import type { Context, Hono } from "hono";
import { streamSSE } from "hono/streaming";
import { ANATOMY, ANATOMY_BY_ID } from "./catalog/anatomy.js";
import { STRUCTURE_FACTS } from "./catalog/coach-knowledge.js";
import { INSTRUMENTS_BY_ID } from "./catalog/instruments.js";
import { CoachSession, PRESENTATION_MODES, bleedingStructures, contextKey, reflexLines, renderContext, type CoachEvent, type StuckPolicy } from "./coach.js";
import { explainStructure, runTool } from "./coach-tools.js";
import type { Frame, FrameMark, SceneVision } from "./scene-vision.js";
import type { FrameDetector } from "./frame-detector.js";
import { ReflexAudio } from "./reflex.js";
import { openBodySimulation, restampForBody } from "./open-body-sim.js";
import { REGION_IDS, type RegionId } from "./patient-condition.js";
import type { Baseline } from "./physiology.js";
import { plausibleBaseline } from "./chart-vitals.js";
import { NO_REALTIME, type RealtimeBridge, type RealtimeSink } from "./realtime-bridge.js";
import { buildSystemPrompt, firstMessage } from "./coach-prompt.js";
import { briefingLines } from "./briefing.js";
import { createContextFeed, type ContextFeed } from "./jarvis/context-feed.js";
import type { EncounterVoices } from "./encounter-routes.js";
import type { Action, SurgicalCase } from "./types.js";
import { registerRobotRoutes } from "./robot-routes.js";

// Live coach API. Unity (or the SpacetimeDB bridge) posts exercise events here; the Scalpal voice
// page reads state, hints, and alerts from it. Sessions live in memory: fine for one demo laptop,
// and the event contract is what moves to SpacetimeDB later.

export interface CoachRouteOptions {
  loadCase: (patientId: string) => Promise<SurgicalCase | null>;
  now: () => Date;
  stuckPolicy?: StuckPolicy;
  tickMs?: number; // 0 disables the background stuck timer (tests call tick directly)
  elevenLabs?: { apiKey: string; agentId: string; voiceId?: string; patientAgentId?: string };
  reflex?: ReflexAudio; // injectable for tests; built from elevenLabs when omitted
  toolAckWaitMs?: number; // how long a highlight tool waits for the headset ack (tests shorten it)
  realtime?: RealtimeSink;
  bridge?: RealtimeBridge | null; // the live connection, for /realtime status and join
  encounters?: EncounterVoices; // live encounters, so /jarvis/connection can bind a voice to one
  encounterFor?: (id: string) => { kase: SurgicalCase; carryover(): string } | null;
  patientStatus?: (patientId: string) => string; // authored patient_status.md for Scalpal's OR context
  vision?: SceneVision | null; // Scalpal's eyes; null when no vision model is configured
  watchMs?: number; // minimum gap between background scene summaries (0 disables watching)
  detector?: FrameDetector | null; // real-camera instrument and hand boxes (services/vision); null when not running
  // Baseline vitals for a case from the patient's chart (VR, and AR until the Presage baseline is captured).
  baselineFor?: (kase: SurgicalCase) => Promise<{ baseline: Baseline; weightKg: number; spo2: number | null; mlPerKg?: number } | null>;
  vitalsUrl?: string; // services/vitals (Presage); POST /baseline/capture at Time-Out in AR
  robotDataDir?: string; // robot demos and replay videos (gitignored); SCALPAL_ROBOT_DIR or services/preop/.robot
}

const MAX_FRAME_CHARS = 4_000_000; // about 3 MB of JPEG
const FRAME_FRESH_MS = 8000;

function parseMarks(raw: unknown): FrameMark[] {
  if (!Array.isArray(raw)) return [];
  const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? Math.min(Math.max(v, 0), 1) : 0);
  return raw.slice(0, 50).flatMap((m) => {
    if (!m || typeof m !== "object") return [];
    const o = m as Record<string, unknown>;
    const box = (o.box ?? {}) as Record<string, unknown>;
    if (typeof o.label !== "string" || !o.label.trim()) return [];
    return [{
      label: o.label.slice(0, 80),
      id: typeof o.id === "string" ? o.id.slice(0, 120) : "",
      source: o.source === "scene" ? "scene" : "detector",
      box: { x: num(box.x), y: num(box.y), w: num(box.w), h: num(box.h) },
    }];
  });
}

const MAX_SESSIONS = 50;
const SESSION_ID = /^coach-[a-z0-9]{6,40}$/;
const RUN_ID = /^(?:[a-f0-9]{32}|[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12})$/i;

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
  // Canonical HandoffTicket run ids survive coach recovery/retry. Bindings share
  // the bounded live-session lifetime; a service restart intentionally loses them.
  const runSessions = new Map<string, string>();
  // Office context for sessions started from a scored encounter, so a later voice connect keeps it.
  const officeCarryover = new Map<string, string>();
  // One server-side context feed per session for the native Quest voice, the same feed the laptop
  // page runs in the browser (jarvis/context-feed.js), so both agents get cards and deltas alike.
  const voiceFeeds = new Map<string, { feed: ContextFeed; lastKey: string }>();
  const frames = new Map<string, Frame>();
  const watching = new Map<string, { inFlight: boolean; lastAt: number }>();
  // Latest detector boxes per session. Detection runs about once a second, so it trails the newest frame.
  const detected = new Map<string, { marks: FrameMark[]; at: number; inFlight: boolean }>();
  const DETECT_FRESH_MS = 4000;
  const DETECT_GENERIC_LABELS = ["hand", "gloved hand", "scissors", "scalpel", "forceps", "clamp", "needle holder", "suture"];
  const withDetections = (sid: string, frame: Frame): Frame => {
    const d = detected.get(sid);
    if (!d || !d.marks.length || frame.at - d.at > DETECT_FRESH_MS) return frame;
    return { ...frame, marks: [...frame.marks, ...d.marks] };
  };
  const watchMs = options.watchMs ?? 4000;
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
    const { patientId, mode: rawMode, encounterId, runId: rawRunId } = await body(c);
    const runId = rawRunId === undefined || rawRunId === null || rawRunId === "" ? undefined : rawRunId;
    if (runId !== undefined && (typeof runId !== "string" || !RUN_ID.test(runId)))
      return bad(c, 400, "invalid_run_id", "runId must be the canonical HandoffTicket UUID.", []);
    // The operating room is mixed reality on a real reclining person (latest flow); full VR is still supported.
    const mode = rawMode === undefined ? "mixed_reality" : PRESENTATION_MODES.find((m) => m === rawMode);
    if (!mode) return bad(c, 400, "invalid_mode", 'mode must be "mixed_reality" or "virtual".', [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    const kase = typeof patientId === "string" ? await options.loadCase(patientId) : null;
    if (!kase) return bad(c, 404, "patient_not_found", 'Send {"patientId": "<FinchNode subject>"} for a known patient.', [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    if (!kase.procedureId) return bad(c, 409, "case_unavailable", kase.statusReason, kase.actions);
    const priorRun = typeof runId === "string" ? sessions.get(runSessions.get(runId) ?? "") : undefined;
    if (priorRun && priorRun.kase.patientId !== kase.patientId)
      return bad(c, 409, "run_patient_mismatch", "This run is already bound to a different patient.", []);

    if (sessions.size >= MAX_SESSIONS) {
      const oldest = sessions.keys().next().value!;
      sessions.delete(oldest);
      officeCarryover.delete(oldest);
      voiceFeeds.delete(oldest);
      for (const [run, session] of runSessions) if (session === oldest) runSessions.delete(run);
    }
    const sid = `coach-${crypto.randomUUID().replaceAll("-", "").slice(0, 16)}`;
    const session = new CoachSession(sid, kase, options.now, options.stuckPolicy, mode);
    const chart = await options.baselineFor?.(kase).catch(() => null);
    if (chart) session.setBaseline(chart.baseline, { weightKg: chart.weightKg, spo2: chart.spo2, mlPerKg: chart.mlPerKg, quiet: true });
    forwardLogs(session);
    forwardCondition(session);
    // Office to operating room: the scored encounter for this patient informs the surgery coaching.
    const office = typeof encounterId === "string" ? options.encounterFor?.(encounterId) : null;
    const preop = office && office.kase.patientId === kase.patientId ? office.carryover() : "";
    sessions.set(sid, session);
    if (typeof runId === "string") runSessions.set(runId, sid);
    if (preop) officeCarryover.set(sid, preop);
    // Bleeding, vitals and stall hints advance even when no client holds the SSE stream.
    ensureTicker();
    const snapshot = session.snapshot();
    return c.json(
      {
        sessionId: sid,
        runId,
        snapshot,
        context: renderContext(snapshot), contextKey: contextKey(snapshot),
        systemPrompt: buildSystemPrompt(kase, mode, preop, (options.patientStatus?.(kase.patientId) ?? "")),
        firstMessage: firstMessage(kase, Boolean(preop)),
        actions: coachActions(sid),
      },
      201,
    );
  });

  // The headset adopts the newest live session (the laptop Scalpal page creates it). Single-room demo
  // shortcut; SpacetimeDB session membership replaces it.
  app.get("/coach/current", (c) => {
    const patientId = c.req.query("patientId") ?? "";
    const latest = [...sessions.values()].reverse().find((s) => !patientId || s.kase.patientId === patientId || s.kase.scenarioId === patientId);
    if (!latest) return bad(c, 404, "no_live_session", "No live coach session yet. Start one from the Scalpal page.", [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    return c.json({ sessionId: latest.id, patientId: latest.kase.patientId, procedureId: latest.kase.procedureId, actions: coachActions(latest.id) });
  });

  app.get("/coach/sessions/:sid", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const snapshot = s.snapshot();
    return c.json({ snapshot, context: renderContext(snapshot), contextKey: contextKey(snapshot), actions: coachActions(s.id) });
  });

  // Native voice context, mirroring the laptop page's syncContext: nothing unless contextKey changed
  // (or force after a voice reconnect), then the feed's full card on structural change or ~10 s, else a
  // one-line [STATE DELTA vN]. kind "none" means send nothing. The feed state advances on each call.
  app.post("/coach/sessions/:sid/voice-context", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const force = (await body(c)).force === true;
    const snapshot = s.snapshot();
    const key = contextKey(snapshot);
    let state = voiceFeeds.get(s.id);
    if (!state) {
      state = { feed: createContextFeed({ now: () => options.now().getTime() }), lastKey: "" };
      voiceFeeds.set(s.id, state);
    }
    const update = force || key !== state.lastKey ? state.feed.next(snapshot, renderContext(snapshot), { force }) : null;
    state.lastKey = key;
    return c.json({ sessionId: s.id, version: snapshot.version, contextKey: key, kind: update?.kind ?? "none", text: update?.text ?? "", actions: coachActions(s.id) });
  });

  // Recap has no conversational agent, client prompt override, or surgery tools.
  // Canonical routes resolve only ids bound at session creation; recap request
  // bodies cannot select another coach or override what Scalpal says.
  const reactionQuestion = "How did that feel?";
  const selfAssessmentQuestion = "What is one thing you would do differently?";
  const recapSession = (c: Context) => {
    const runId = c.req.param("runId");
    return runId === undefined ? getSession(c) : sessions.get(runSessions.get(runId) ?? "") ?? null;
  };
  const recapMetadata = (c: Context) => {
    const s = recapSession(c);
    if (!s) return missing(c);
    c.header("Cache-Control", "no-store");
    const runId = c.req.param("runId");
    const route = runId ? `/coach/runs/${runId}` : `/coach/sessions/${s.id}`;
    return c.json({ runId: runId ?? s.id, reactionQuestion, selfAssessmentQuestion,
      reactionAudioRoute: `${route}/recap/reaction.mp3`,
      voiceConfigured: Boolean(reflex?.configured) });
  };
  const recapAudio = async (c: Context) => {
    const s = recapSession(c);
    if (!s) return missing(c);
    if (!reflex?.configured) return bad(c, 503, "recap_voice_unconfigured", "Scalpal speech is unavailable. Use the reflection panel.", []);
    try {
      const audio = await reflex.render(reactionQuestion);
      return c.body(new Uint8Array(audio), 200, { "Content-Type": "audio/mpeg", "Cache-Control": "no-store" });
    } catch {
      return bad(c, 503, "recap_voice_failed", "Scalpal speech is unavailable. Use the reflection panel.", []);
    }
  };
  app.post("/coach/runs/:runId/recap", recapMetadata);
  app.get("/coach/runs/:runId/recap/reaction.mp3", recapAudio);
  // Compatibility for callers which still use a server-issued coach id.
  app.post("/coach/sessions/:sid/recap", recapMetadata);
  app.get("/coach/sessions/:sid/recap/reaction.mp3", recapAudio);

  app.post("/coach/sessions/:sid/events", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const b = await body(c);
    const raw = Array.isArray(b.events) ? b.events : b.event ? [b.event] : [];
    if (!raw.length) return bad(c, 400, "no_events", 'Send {"event": {...}} or {"events": [...]}.', coachActions(s.id));
    // Each event stands alone: the headset batches events, so one unknown id (say, an instrument the
    // catalog doesn't have yet) must not drop the valid events around it.
    const results = raw.map((e) => {
      const parsed = parseEvent(e);
      if (typeof parsed === "string") return { accepted: false, reason: `invalid: ${parsed}`, alerts: [] };
      const meta = e as { eventId?: unknown; stepId?: unknown };
      const opt = (v: unknown) => (typeof v === "string" && EVENT_ID.test(v) ? v : undefined);
      return s.receive(parsed, { eventId: opt(meta.eventId), stepId: opt(meta.stepId) });
    });
    if (results.every((r) => r.reason.startsWith("invalid: "))) {
      return bad(c, 400, "invalid_event", results.map((r, i) => `events[${i}]: ${r.reason.slice(9)}`).join("; "), coachActions(s.id));
    }
    const snapshot = s.snapshot();
    return c.json({
      // accepted: a delivered event for this step, including safe duplicate retries.
      // Desynchronized steps are rejected so the native relay fails closed.
      // applied: it reached scoring (false while tracking is lost or
      // after the case is complete, matching what CaseRunner does on the headset).
      results: results.map((r) => ({ accepted: !r.reason.startsWith("invalid: ") && r.reason !== "step_desynchronized", applied: r.accepted, reason: r.reason })),
      // duplicate (same eventId seen before) is accepted but not applied, so a relay can retry safely.
      alerts: results.flatMap((r) => r.alerts),
      snapshot,
      context: renderContext(snapshot), contextKey: contextKey(snapshot),
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
    const result = explainStructure(s, query, resolveStructure);
    const facts = result.found ? STRUCTURE_FACTS[result.structureId] : undefined;
    return c.json({
      ...result,
      name: result.found ? (s.kase.anatomy.find((a) => a.id === result.structureId)?.displayName ?? "") : "",
      what: facts?.what ?? "",
      where: facts?.where ?? "",
      supply: facts?.supply ?? "",
      why: facts?.why ?? "",
      actions: coachActions(s.id),
    });
  });

  // Point-of-view frames from the camera rig or the Quest (passthrough plus overlay), with labeled boxes.
  app.post("/coach/sessions/:sid/frame", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const b = await body(c);
    const image = typeof b.image === "string" ? b.image.replace(/^data:image\/jpeg;base64,/, "") : "";
    if (!image || image.length > MAX_FRAME_CHARS || !/^[A-Za-z0-9+/=]+$/.test(image.slice(0, 200))) {
      return bad(c, 400, "invalid_frame", 'Send {"image": "<base64 JPEG>", "marks": [...]} under about 3 MB.', coachActions(s.id));
    }
    const frame: Frame = { jpegBase64: image, marks: parseMarks(b.marks), source: b.source === "quest" ? "quest" : "camera", at: options.now().getTime() };
    frames.set(s.id, frame);
    const d = detected.get(s.id) ?? { marks: [], at: 0, inFlight: false };
    detected.set(s.id, d);
    if (options.detector && !d.inFlight) {
      d.inFlight = true;
      // Generic tools too, so a wrong instrument in hand still gets named.
      const labels = [...new Set([...DETECT_GENERIC_LABELS, ...s.kase.instruments.map((i) => i.id)])].slice(0, 32);
      void options.detector
        .detect(image, labels)
        .then((marks) => Object.assign(d, { marks, at: frame.at }))
        .catch(() => {})
        .finally(() => (d.inFlight = false));
    }
    // Background watcher: a fresh one-line summary at most every watchMs, never two at once.
    const w = watching.get(s.id) ?? { inFlight: false, lastAt: 0 };
    watching.set(s.id, w);
    const due = Boolean(options.vision) && watchMs > 0 && !w.inFlight && frame.at - w.lastAt >= watchMs;
    if (due && options.vision) {
      w.inFlight = true;
      w.lastAt = frame.at;
      void options.vision
        .watch(withDetections(s.id, frame), s.snapshot())
        .then((summary) => s.setScene(summary, frame.source))
        .catch(() => {})
        .finally(() => (w.inFlight = false));
    }
    return c.json({ stored: true, marks: frame.marks.length, watching: due, actions: coachActions(s.id) });
  });

  // One implementation of Scalpal's client tools for every voice client (laptop page, Quest native voice).
  app.post("/coach/sessions/:sid/tools/:name", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    if (c.req.param("name") === "look_at_scene") {
      const { question } = await body(c);
      const frame = frames.get(s.id);
      let result: string;
      if (!options.vision) result = "My vision isn't set up yet, so I can only go by the simulator's state.";
      else if (!frame || options.now().getTime() - frame.at > FRAME_FRESH_MS) result = "I can't see your view right now. Make sure the camera feed is on.";
      else result = await options.vision.look(withDetections(s.id, frame), s.snapshot(), typeof question === "string" ? question : "");
      return c.json({ result, actions: coachActions(s.id) });
    }
    const result = await runTool(s, c.req.param("name") ?? "", await body(c), {
      renderContext: (x) => renderContext(x.snapshot()),
      resolveStructure,
      ackWaitMs: options.toolAckWaitMs,
      realtime: options.realtime,
    });
    if (result == null) return bad(c, 404, "unknown_tool", `No Scalpal tool named "${c.req.param("name")}".`, coachActions(s.id));
    return c.json({ result, actions: coachActions(s.id) });
  });

  // Voice clients mirror what was actually said and the agent's status into the shared session.
  const SPEAKERS = new Set(["learner", "coach", "system"]);
  const VOICE_STATUS = new Set(["offline", "connecting", "listening", "thinking", "speaking", "error"]);
  app.post("/coach/sessions/:sid/transcript", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { speaker, text } = await body(c);
    if (typeof speaker !== "string" || !SPEAKERS.has(speaker) || typeof text !== "string" || !text.trim()) {
      return bad(c, 400, "invalid_transcript", 'Send {"speaker": "learner" | "coach" | "system", "text": "..."}.', coachActions(s.id));
    }
    (options.realtime ?? NO_REALTIME).coachMessage(speaker as "learner" | "coach" | "system", text.trim());
    return c.json({ ok: true, actions: coachActions(s.id) });
  });

  app.post("/coach/sessions/:sid/voice-status", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { status, detail } = await body(c);
    if (typeof status !== "string" || !VOICE_STATUS.has(status)) return bad(c, 400, "invalid_status", "Unknown voice status.", coachActions(s.id));
    (options.realtime ?? NO_REALTIME).coachStatus(status as "listening", typeof detail === "string" ? detail : undefined);
    return c.json({ ok: true, actions: coachActions(s.id) });
  });

  // The shared SpacetimeDB session: status, and joining with the session's coach invite code.
  app.get("/realtime", (c) => c.json({ ...(options.bridge ? options.bridge.status() : { configured: false, connected: false, identity: "", sessionId: "", lastError: "" }), actions: [] }));
  app.post("/realtime/join", async (c) => {
    if (!options.bridge) return bad(c, 503, "realtime_unconfigured", "Set SPACETIMEDB_URI (and SPACETIMEDB_DB) for the coach service.", []);
    const { code, sessionId: existing } = await body(c);
    // Rebind to a session this coach identity already joined (after a restart).
    if (typeof existing === "string" && existing) {
      return options.bridge.bind(existing)
        ? c.json({ ...options.bridge.status(), actions: [] })
        : bad(c, 409, "not_a_member", "This coach identity has not joined that session; send its coach invite code.", []);
    }
    if (typeof code !== "string" || !/^[A-Za-z0-9]{4,12}$/.test(code.trim())) return bad(c, 400, "invalid_code", "Send the session's coach invite code, or the sessionId it already joined.", []);
    try {
      const sessionId = await options.bridge.join(code);
      return c.json({ ...options.bridge.status(), sessionId, actions: [] });
    } catch (err) {
      return bad(c, 409, "join_failed", String(err instanceof Error ? err.message : err), []);
    }
  });

  // Alerts for clients without SSE. Each carries its tier, an optional reflex clip route, and the exact
  // [SIM EVENT] text to send when it becomes an LLM turn. Poll with after=<latestSeq from the last call>.
  app.get("/coach/sessions/:sid/alerts", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const after = Number(c.req.query("after") ?? "0");
    if (!Number.isSafeInteger(after) || after < 0) return bad(c, 400, "invalid_cursor", "after must be a nonnegative integer.", coachActions(s.id));
    // Native Quest polling has no SSE listener: advance stall hints on this active poll too.
    s.tick();
    const { alerts, latestSeq } = s.alertsAfter(after);
    return c.json({
      alerts: alerts.map((a) => ({ ...a, reflexRoute: a.reflexKey && reflex?.configured ? `/jarvis/reflex/${s.id}/${a.reflexKey}` : "" })),
      latestSeq,
      snapshot: s.snapshot(),
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

  // Feeds Stephen's shared patient_condition row (SpacetimeDB advances it at 1 Hz for the Live OR panel)
  // from the same facts the coach uses: the baseline, body blood loss and active bleeds, region injuries and
  // the end of the case. Death is detected there too, from the same physiology.
  function feedPatientCondition(session: CoachSession) {
    const sink = options.realtime;
    if (!sink?.patientCondition) return;
    const send = sink.patientCondition.bind(sink);
    const startView = session.condition.view();
    const b = session.condition.currentBaseline;
    send({ kind: "start", coachSessionId: session.id, baseline: { hr: b.hr, rr: b.rr, sys: b.sys, dia: b.dia, spo2: startView.vitals.spo2 >= 0 ? startView.vitals.spo2 : 98, source: b.source ?? "authored" }, weightKg: startView.weightKg, mlPerKg: session.condition.bloodVolumeMlPerKg });
    let baselineKey = JSON.stringify(b);
    let bodyKey = "";
    const regions = new Map<string, boolean>();
    let ended = false;
    session.subscribe(({ snapshot }) => {
      const cb = session.condition.currentBaseline;
      const bk = JSON.stringify(cb);
      if (bk !== baselineKey) {
        baselineKey = bk;
        send({ kind: "baseline", baseline: { hr: cb.hr, rr: cb.rr, sys: cb.sys, dia: cb.dia, source: cb.source ?? "authored" } });
      }
      const bleeds = snapshot.activeBleeds.map((x) => ({ name: x.structure.name, rateMlPerMin: x.rateMlPerMin }));
      const key = JSON.stringify([snapshot.bloodLossMl, bleeds]);
      if (key !== bodyKey) {
        bodyKey = key;
        send({ kind: "body", bloodLostMl: snapshot.bloodLossMl, bleeds });
      }
      for (const r of snapshot.condition.regions) {
        if (regions.get(r.region) !== r.bleeding) {
          regions.set(r.region, r.bleeding);
          send({ kind: "injury", region: r.region, controlled: !r.bleeding });
        }
      }
      const result = snapshot.condition.outcome.result;
      if (!ended && (result === "completed" || result === "ended")) {
        ended = true;
        send({ kind: "end", result, cause: snapshot.condition.outcome.cause });
      }
    });
  }

  // Operating-room logs for the companion dashboard: new timeline lines, alerts, a vitals sample at most
  // every 2 s (and on every class change), checklist progress and the outcome. Logs only, no video.
  function forwardLogs(session: CoachSession) {
    const sink = options.realtime;
    feedPatientCondition(session);
    if (!sink?.simLog) return;
    const log = (kind: "event" | "alert" | "vitals" | "checklist" | "outcome", text: string, data?: unknown) => sink.simLog!({ coachSessionId: session.id, kind, text, data });
    let lastLine = "";
    let lastVitalsAt = 0;
    let lastClass = 0;
    let doneCount = 0;
    let outcome = "in_progress";
    session.subscribe(({ snapshot, alerts }) => {
      const timeline = snapshot.timeline;
      const from = timeline.findIndex((t) => `${t.atSeconds}|${t.text}` === lastLine);
      for (const t of from === -1 ? timeline.slice(-3) : timeline.slice(from + 1)) log("event", t.text, { atSeconds: t.atSeconds });
      if (timeline.length) lastLine = `${timeline.at(-1)!.atSeconds}|${timeline.at(-1)!.text}`;
      for (const a of alerts) log("alert", a.say, { kind: a.kind, tier: a.tier });
      const v = snapshot.condition.vitals;
      const now = options.now().getTime();
      // Always sample on an outcome change, so the dashboard's pinned vitals show asystole after a death.
      if (now - lastVitalsAt >= 2000 || v.hemorrhageClass !== lastClass || snapshot.condition.outcome.result !== outcome) {
        lastVitalsAt = now;
        lastClass = v.hemorrhageClass;
        log("vitals", `HR ${v.hr} · BP ${v.sys}/${v.dia} · RR ${v.rr}${v.spo2 >= 0 ? ` · SpO2 ${v.spo2}` : ""} · loss ${v.bloodLossPct}% (class ${v.hemorrhageClass}, simulated from ${snapshot.condition.baselineSource})`, { ...v, rawBloodLossMl: snapshot.condition.rawBloodLossMl });
      }
      const done = snapshot.checklist.filter((c) => c.done).length;
      if (done !== doneCount) {
        doneCount = done;
        log("checklist", `${done}/${snapshot.checklist.length} steps done${snapshot.checklist.find((c) => c.current) ? `; now: ${snapshot.checklist.find((c) => c.current)!.title}` : ""}`, snapshot.checklist);
      }
      if (snapshot.condition.outcome.result !== outcome) {
        outcome = snapshot.condition.outcome.result;
        log("outcome", outcome === "died" ? `Patient died: ${snapshot.condition.outcome.cause}` : outcome === "completed" ? "Case goals reached" : outcome, snapshot.condition.outcome);
      }
    });
  }

  // The simulated patient in SpacetimeDB (patient_condition): started from this session's baseline, fed the
  // body facts and injuries as they change (bleed set and injuries at once, blood loss at most once a second;
  // the module accrues between reports), and read back as the snapshot condition while its row is fresh.
  function forwardCondition(session: CoachSession) {
    const sink = options.realtime;
    if (!sink?.startCondition || !sink.readCondition) return;
    let started = false;
    let baselineSig = "";
    let bleedSig = "";
    let lost = -1;
    let reportedAt = -Infinity;
    let ended = false;
    const regions = new Map<string, boolean>(); // region -> bleeding, as last reported
    const sig = (b: Baseline) => `${b.hr}/${b.rr}/${b.sys}/${b.dia}/${b.source}`;
    const start = () => {
      if (!sink.bound) return false;
      const p = session.condition.params;
      sink.startCondition!(session.id, { baseline: p.baseline, spo2: p.spo2, weightKg: p.weightKg, mlPerKg: p.mlPerKg });
      baselineSig = sig(p.baseline);
      started = true;
      return true;
    };
    start();
    session.conditionFeed = { read: () => sink.readCondition!(session.id) };
    session.subscribe(({ snapshot }) => {
      if (!started && !start()) return;
      const local = session.condition.view();
      const b = session.condition.currentBaseline;
      if (sig(b) !== baselineSig) {
        baselineSig = sig(b);
        sink.setConditionBaseline?.(session.id, b);
      }
      for (const r of local.regions) {
        const prev = regions.get(r.region);
        if (prev === undefined || prev !== r.bleeding) sink.reportInjury?.(session.id, r.region, prev !== undefined && !r.bleeding);
        regions.set(r.region, r.bleeding);
      }
      const bleeds = snapshot.activeBleeds.map((x) => ({ name: x.structure.name, rateMlPerMin: x.rateMlPerMin }));
      const now = options.now().getTime();
      if (JSON.stringify(bleeds) !== bleedSig || (snapshot.bloodLossMl !== lost && now - reportedAt >= 1000)) {
        bleedSig = JSON.stringify(bleeds);
        lost = snapshot.bloodLossMl;
        reportedAt = now;
        sink.reportBody?.(session.id, snapshot.bloodLossMl, bleeds);
      }
      if (!ended && (local.outcome.result === "completed" || local.outcome.result === "ended")) {
        ended = true;
        sink.endCondition?.(session.id, local.outcome.result, local.outcome.cause);
      }
    });
  }

  // Time-Out in AR: freeze the volunteer's measured baseline (Presage, services/vitals) and use it for the
  // simulated monitor. A body {baseline: {hr, rr, sys, dia}} sets it directly (tests, or a headset that
  // reads the vitals service itself). Without either, the chart baseline stays.
  app.post("/coach/sessions/:sid/vitals/baseline", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const b = (await body(c)).baseline as Record<string, unknown> | undefined;
    const ok = (x: unknown) => typeof x === "number" && Number.isFinite(x) && x > 0 && x < 400;
    let baseline: Baseline | null = null;
    if (b && ok(b.hr) && ok(b.rr) && ok(b.sys) && ok(b.dia) && plausibleBaseline(b as { hr: number; rr: number; sys: number; dia: number })) {
      baseline = { hr: b.hr as number, rr: b.rr as number, sys: b.sys as number, dia: b.dia as number, source: typeof b.source === "string" ? b.source.slice(0, 20) : "measured" };
    } else if (options.vitalsUrl) {
      const res = await fetch(`${options.vitalsUrl.replace(/\/$/, "")}/baseline/capture`, { method: "POST", signal: AbortSignal.timeout(4000) }).catch(() => null);
      const j = res?.ok ? ((await res.json().catch(() => null)) as Record<string, unknown> | null) : null;
      // Presage measures heart and breathing rate only; blood pressure stays as charted (its sys/dia are authored).
      const current = s.condition.currentBaseline;
      if (j && ok(j.hr) && ok(j.rr)) baseline = { hr: j.hr as number, rr: j.rr as number, sys: current.sys, dia: current.dia, source: typeof j.source === "string" ? j.source : "measured", bpSource: current.source ?? "authored" };
      else return bad(c, 503, "vitals_unavailable", "The vitals service did not return a baseline; the chart baseline stays.", coachActions(s.id));
    } else {
      return bad(c, 400, "invalid_baseline", 'Send {"baseline": {"hr", "rr", "sys", "dia", "source"}} or set VITALS_URL for Presage capture.', coachActions(s.id));
    }
    s.setBaseline(baseline);
    return c.json({ baseline, condition: s.snapshot().condition, actions: coachActions(s.id) });
  });

  // Robot hand attempts (services/motion teleop, scalpal.robot_attempt.v1): the simulated Shadow hand driven
  // by the Quest controllers, labeled with the surgery step. Kept per session and logged to the dashboard.
  const robotAttempts = new Map<string, Record<string, unknown>[]>();
  const pushRobotAttempt = (sid: string, attempt: Record<string, unknown>) => {
    const list = robotAttempts.get(sid) ?? [];
    list.push(attempt);
    if (list.length > 50) list.shift();
    robotAttempts.set(sid, list);
    return list;
  };
  app.post("/coach/sessions/:sid/robot-attempts", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const a = await body(c);
    if (a.schema !== "scalpal.robot_attempt.v1" || typeof a.attemptId !== "string" || typeof a.success !== "boolean") {
      return bad(c, 400, "invalid_robot_attempt", 'Send a scalpal.robot_attempt.v1 body with attemptId and success.', coachActions(s.id));
    }
    const str = (v: unknown, n = 200) => (typeof v === "string" ? v.slice(0, n) : "");
    const num = (v: unknown) => (typeof v === "number" && Number.isFinite(v) ? v : 0);
    const attempt = {
      attemptId: str(a.attemptId, 80), stepId: str(a.stepId, 80), stepTitle: str(a.stepTitle), task: str(a.task, 80),
      success: a.success, frames: num(a.frames), durationS: num(a.durationS), labeledFraction: num(a.labeledFraction),
      heldInstruments: Array.isArray(a.heldInstruments) ? a.heldInstruments.filter((x): x is string => typeof x === "string").slice(0, 4) : [],
      source: str(a.source), createdAt: str(a.createdAt, 40),
    };
    const list = pushRobotAttempt(s.id, attempt);
    options.realtime?.simLog?.({
      coachSessionId: s.id,
      kind: "event",
      text: `Robot hand attempt ${attempt.success ? "succeeded" : "failed"}${attempt.stepTitle ? ` during "${attempt.stepTitle}"` : ""}: ${attempt.frames} frames, ${attempt.durationS.toFixed(1)} s (simulated Shadow hand, Quest controller teleop).`,
      data: { robotAttempt: attempt },
    });
    return c.json({ stored: true, count: list.length, actions: coachActions(s.id) }, 201);
  });
  app.get("/coach/sessions/:sid/robot-attempts", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const list = robotAttempts.get(s.id) ?? [];
    return c.json({ attempts: list, successes: list.filter((x) => x.success).length, actions: coachActions(s.id) });
  });

  // Headset demos -> simulated arm + hand policy -> graded replay (services/motion robot-serve).
  registerRobotRoutes(app, {
    dataDir: options.robotDataDir ?? process.env.SCALPAL_ROBOT_DIR ?? fileURLToPath(new URL("../.robot", import.meta.url)),
    now: options.now,
    session: (sid) => (SESSION_ID.test(sid) ? (sessions.get(sid) ?? null) : null),
    realtime: options.realtime,
    recordAttempt: (sid, attempt) => void pushRobotAttempt(sid, attempt),
  });

  // Demo driver: lets the laptop exercise Scalpal before the headset is wired in.
  app.post("/coach/sessions/:sid/simulate", async (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const { kind } = await body(c);
    let events: CoachEvent[] = [];
    const stepResults: ReturnType<CoachSession["handle"]>[] = [];
    s.simulated = true;
    const region = typeof kind === "string" ? ({ cut_neck: "neck", cut_head: "head", cut_chest: "chest", cut_arm: "right_arm" } as Record<string, RegionId>)[kind] : undefined;
    if (region) events = [{ type: "injury", region, instrumentId: "scalpel" }];
    else if (kind === "control_injury") {
      const bleedingRegion = s.condition.view().regions.find((r) => r.bleeding)?.region;
      events = bleedingRegion ? [{ type: "injury", region: bleedingRegion, instrumentId: "hemostat", controlled: true }] : [];
    }
    const open = region || kind === "control_injury" ? null : typeof kind === "string" ? openBodySimulation(s, kind) : null;
    if (open) events = open;
    else if (!region && kind !== "control_injury") switch (kind) {
      case "correct_action": {
        const e = restampForBody(s, s.nextCorrectEvent());
        events = e ? [e] : [];
        break;
      }
      case "complete_step": {
        const stepId = s.engine.current?.id;
        for (let i = 0; i < 20 && stepId && s.engine.current?.id === stepId; i++) {
          const e = restampForBody(s, s.nextCorrectEvent());
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
      case "bleed":
      case "stop_bleed": {
        // Open (or control) a bleed in the vessel nearest this step: a step target first, else any case vessel.
        const vessels = bleedingStructures(s.kase).map((v) => v.id);
        const step = s.engine.current;
        const vessel = vessels.find((v) => step?.targets.includes(v)) ?? vessels.find((v) => step?.mistakes.some((m) => m.structure === v)) ?? vessels[0];
        const total = s.snapshot().bloodLossMl;
        events = vessel ? [{ type: "bleeding", structureId: vessel, active: kind === "bleed", rateMlPerMin: kind === "bleed" ? 45 : 0, totalMl: total + (kind === "bleed" ? 20 : 35) }] : [];
        break;
      }
      case "tracking_restored":
        events = [{ type: "tracking", valid: true }];
        break;
      default:
        return bad(c, 400, "invalid_simulation", "kind must be correct_action, complete_step, mistake, wrong_instrument, off_target, look_at_danger, tracking_lost, tracking_restored, bleed, stop_bleed, cut_neck, cut_head, cut_chest, cut_arm, or control_injury.", coachActions(s.id));
    }
    const results = [...stepResults, ...events.map((e) => s.handle(e))];
    const snapshot = s.snapshot();
    return c.json({ alerts: results.flatMap((r) => r.alerts), snapshot, context: renderContext(snapshot), contextKey: contextKey(snapshot), actions: coachActions(s.id) });
  });

  // Server-sent events: one "state" message per change (with any alerts), plus the stuck timer.
  app.get("/coach/sessions/:sid/stream", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    ensureTicker();
    return streamSSE(c, async (stream) => {
      const send = (payload: unknown) => stream.writeSSE({ event: "state", data: JSON.stringify(payload) });
      const first = s.snapshot();
      await send({ snapshot: first, context: renderContext(first), contextKey: contextKey(first), alerts: [] });
      const unsubscribe = s.subscribe((u) => void send({ ...u, context: renderContext(u.snapshot), contextKey: contextKey(u.snapshot) }));
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
  // The flythrough narration: one line per briefing beat with its pre-rendered clip route. The headset
  // plays clip N when the camera reaches beat N; the text doubles as captions.
  app.get("/coach/sessions/:sid/briefing", (c) => {
    const s = getSession(c);
    if (!s) return missing(c);
    const lines = briefingLines(s.kase).map((l) => ({ ...l, route: reflex?.configured ? `/jarvis/reflex/${s.id}/${l.key}` : "" }));
    return c.json({ sessionId: s.id, procedureId: s.kase.procedureId, lines, actions: coachActions(s.id) });
  });

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

  // Scalpal voice page (laptop browser) and its ElevenLabs connection details.
  app.get("/jarvis", (c) => c.html(readFileSync(new URL("./jarvis/index.html", import.meta.url), "utf8")));
  // The coach is called Scalpal now; /jarvis paths stay so existing headset builds keep working.
  app.get("/scalpal", (c) => c.redirect("/jarvis"));
  app.get("/scalpal/camera", (c) => c.redirect("/jarvis/camera"));
  app.get("/scalpal/connection", (c) => c.redirect(`/jarvis/connection${new URL(c.req.url).search}`));
  app.get("/scalpal/reflex/:sid/:key", (c) => c.redirect(`/jarvis/reflex/${c.req.param("sid")}/${c.req.param("key")}`));
  // Camera test rig: a webcam or iPhone (Continuity Camera) stands in for the Quest camera.
  app.get("/jarvis/camera", (c) => c.html(readFileSync(new URL("./jarvis/camera.html", import.meta.url), "utf8")));
  for (const file of ["app.js", "arbiter.js", "context-feed.js", "interview.js", "encounter.js", "camera.js", "camera-rig.js", "body-map.js"]) {
    app.get(`/jarvis/${file}`, (c) =>
      c.body(readFileSync(new URL(`./jarvis/${file}`, import.meta.url), "utf8"), 200, { "Content-Type": "text/javascript; charset=utf-8", "Cache-Control": "no-cache" }),
    );
  }

  // A connection is minted only for something live, and the prompt comes from the server:
  //   ?sessionId=coach-...   Scalpal coaching that surgery session (prompt = the session's system prompt)
  //   ?encounterId=enc-...   the patient agent during the interview, Scalpal as attending afterwards
  // Legacy calls without an id (the Quest client: none, or ?agent=patient) still work, but only while a
  // matching coach session or encounter phase is live; they carry no prompt.
  app.get("/jarvis/connection", async (c) => {
    const el = options.elevenLabs;
    const home: Action[] = [{ id: "home", label: "Home", method: "GET", route: "/" }];
    const sessionId = c.req.query("sessionId");
    const encounterId = c.req.query("encounterId");
    let role: "jarvis" | "patient" = c.req.query("agent") === "patient" ? "patient" : "jarvis";
    let bound: { prompt: string; firstMessage: string; voiceId: string; role: string } | null = null;
    if (encounterId !== undefined) {
      const voice = options.encounters?.voiceFor(encounterId) ?? null;
      if (!voice) return bad(c, 404, "encounter_not_found", "No live encounter with that id. Start one from a patient.", home);
      if (voice === "scored") return bad(c, 409, "invalid_phase", "This encounter is already scored; there is no conversation left to connect.", home);
      role = voice.role === "patient" ? "patient" : "jarvis";
      bound = voice;
    } else if (sessionId !== undefined) {
      const s = SESSION_ID.test(sessionId) ? sessions.get(sessionId) : undefined;
      if (!s) return missing(c);
      role = "jarvis";
      const preop = officeCarryover.get(sessionId) ?? "";
      bound = { role: "coach", prompt: buildSystemPrompt(s.kase, s.mode, preop, (options.patientStatus?.(s.kase.patientId) ?? "")), firstMessage: firstMessage(s.kase, Boolean(preop)), voiceId: "" };
    }
    const agentId = role === "patient" ? el?.patientAgentId : el?.agentId;
    if (!el || !agentId) {
      return bad(c, 503, "jarvis_unconfigured", "Set ELEVENLABS_AGENT_ID and PATIENT_AGENT_ID (and ELEVENLABS_API_KEY for private agents), then run npm run jarvis:setup.", home);
    }
    if (!bound) {
      const live = role === "patient" ? Boolean(options.encounters?.anyLive("patient")) : sessions.size > 0 || Boolean(options.encounters?.anyLive("attending"));
      if (!live) return bad(c, 409, "no_live_session", "Start a coach session or encounter before connecting voice, and pass its sessionId or encounterId.", home);
    }
    const binding = bound ? { role: bound.role, prompt: bound.prompt, firstMessage: bound.firstMessage, voiceId: bound.voiceId } : {};
    if (!el.apiKey) return c.json({ mode: "public", agentId, signedUrl: "", ...binding, actions: [] });
    const res = await fetch(`https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?agent_id=${encodeURIComponent(agentId)}`, {
      headers: { "xi-api-key": el.apiKey },
    }).catch(() => null);
    if (!res?.ok) return bad(c, 503, "elevenlabs_unreachable", `ElevenLabs signed URL request failed${res ? ` (${res.status})` : ""}.`, home);
    const { signed_url } = (await res.json()) as { signed_url: string };
    return c.json({ mode: "signed", agentId, signedUrl: signed_url, ...binding, actions: [] });
  });
}

// Ids only need to be well formed. The atlas has thousands of parts outside the catalog (a rib, a nerve);
// touching one is a real off-target attempt the coach should count, not a malformed request. Rejecting it
// would also make the headset relay stop syncing for the rest of the session.
const EVENT_ID = /^[a-z0-9][a-z0-9_.:-]{0,119}$/;

function parseEvent(e: unknown): CoachEvent | string {
  if (!e || typeof e !== "object") return "event must be an object";
  const ev = e as Record<string, unknown>;
  const str = (k: string) => (typeof ev[k] === "string" ? (ev[k] as string) : "");
  const id = (k: string) => (EVENT_ID.test(str(k)) ? "" : `${k} "${str(k)}" is not a well-formed id`);
  switch (ev.type) {
    case "place_port":
      return id("portId") || { type: "place_port", portId: str("portId") };
    case "touch":
      return id("structureId") || id("instrumentId") || { type: "touch", structureId: str("structureId"), instrumentId: str("instrumentId") };
    case "identify":
      return id("structureId") || { type: "identify", structureId: str("structureId") };
    case "confirm":
      return { type: "confirm" };
    case "finish":
      return { type: "finish" };
    case "surgery": {
      if (!ev.evidence || typeof ev.evidence !== "object" || Array.isArray(ev.evidence)) return "surgery needs evidence";
      const evidence = ev.evidence as unknown as BodyAction;
      if (!validBodyAction(evidence)) return "invalid body action measurements, tool verb, or coordinate frame";
      for (const value of [evidence.actionId, evidence.instrumentId, evidence.instrumentInstanceId, evidence.tissueId, evidence.layer]) {
        if (!EVENT_ID.test(value)) return "invalid body action identifier";
      }
      if (evidence.secondaryInstanceId && !EVENT_ID.test(evidence.secondaryInstanceId)) return "invalid secondary tool instance";
      if (evidence.choice && !EVENT_ID.test(evidence.choice)) return "invalid body decision choice";
      return { type: "surgery", evidence };
    }
    case "focus":
      return str("structureId") === "" ? { type: "focus", structureId: "" } : id("structureId") || { type: "focus", structureId: str("structureId") };
    case "instrument":
      if (ev.hand !== "left" && ev.hand !== "right") return 'instrument needs hand "left" or "right"';
      if (typeof ev.held !== "boolean") return "instrument needs a boolean held";
      return id("instrumentId") || { type: "instrument", instrumentId: str("instrumentId"), hand: ev.hand, held: ev.held };
    case "contact":
      return id("instrumentId") || id("structureId") || { type: "contact", instrumentId: str("instrumentId"), structureId: str("structureId") };
    case "injury": {
      const region = str("region") as RegionId;
      if (!REGION_IDS.includes(region)) return `injury needs region one of ${REGION_IDS.join(", ")}`;
      if (ev.controlled !== undefined && typeof ev.controlled !== "boolean") return "injury controlled must be a boolean";
      return id("instrumentId") || { type: "injury", region, instrumentId: str("instrumentId"), ...(ev.controlled === true ? { controlled: true } : {}) };
    }
    case "tracking":
      return typeof ev.valid === "boolean" ? { type: "tracking", valid: ev.valid } : "tracking needs a boolean valid";
    case "bleeding": {
      const num = (k: string) => (typeof ev[k] === "number" && Number.isFinite(ev[k]) && (ev[k] as number) >= 0 ? (ev[k] as number) : null);
      if (typeof ev.active !== "boolean") return "bleeding needs a boolean active";
      const rate = num("rateMlPerMin"), total = num("totalMl");
      if (rate == null || total == null) return "bleeding needs non-negative rateMlPerMin and totalMl";
      return id("structureId") || { type: "bleeding", structureId: str("structureId"), active: ev.active, rateMlPerMin: rate, totalMl: total };
    }
    default:
      return "type must be place_port, touch, identify, confirm, surgery, instrument, contact, injury, focus, tracking, or bleeding";
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
