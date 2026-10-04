import { Hono, type Context } from "hono";
import { cors } from "hono/cors";
import { ANATOMY } from "./catalog/anatomy.js";
import { INSTRUMENTS } from "./catalog/instruments.js";
import { PROCEDURES, PROCEDURES_BY_ID } from "./catalog/procedures.js";
import { buildBrief, DISCLAIMER } from "./brief.js";
import { buildCase, routes, scorePreopCheck, unavailableCase } from "./case-builder.js";
import type { StuckPolicy } from "./coach.js";
import { registerCoachRoutes } from "./coach-routes.js";
import { registerEncounterRoutes } from "./encounter-routes.js";
import { registerInterviewRoutes } from "./interview-routes.js";
import { patientStatusFor } from "./interview-content.js";
import { chartBaseline } from "./chart-vitals.js";
import type { AnswerClassifier, SpeechToText } from "./answer-classifier.js";
import { ENCOUNTERS_BY_PLAN } from "./catalog/encounters.js";
import type { RealtimeBridge } from "./realtime-bridge.js";
import type { SceneVision } from "./scene-vision.js";
import type { FrameDetector } from "./frame-detector.js";
import type { ReflexAudio } from "./reflex.js";
import { FinchNodeError, createFinchNodeClient, type FinchNodeClient } from "./finchnode.js";
import type { Action, AdmissionStatus, DataGap, Scenario, SandboxSession, SurgicalCase } from "./types.js";

export interface AppOptions {
  client?: FinchNodeClient;
  now?: () => Date;
  // Use FinchNode's sandbox simulate shortcut instead of the hosted consent link. Off by default:
  // as of Oct 3 simulated sessions stall at partial sync, while the hosted link completes.
  simulateAdmissions?: boolean;
  coachTickMs?: number;
  stuckPolicy?: StuckPolicy;
  elevenLabs?: { apiKey: string; agentId: string; voiceId?: string; patientAgentId?: string; interviewAgentId?: string };
  // Choice-based office interview: spoken answers (speech-to-text, then a classifier), and the content root (tests).
  answerClassifier?: AnswerClassifier | null;
  speechToText?: SpeechToText | null;
  interviewContentRoot?: string;
  vitalsUrl?: string; // services/vitals (Presage) for the AR Time-Out baseline
  reflex?: ReflexAudio;
  toolAckWaitMs?: number;
  realtime?: RealtimeBridge | null;
  vision?: SceneVision | null;
  watchMs?: number;
  detector?: FrameDetector | null;
  // Extra browser origins allowed besides localhost/127.0.0.1 (any port) and same-origin pages
  // (PREOP_CORS_ORIGINS in index.ts). Native clients send no Origin header and are unaffected.
  corsOrigins?: readonly string[];
}

const LOCAL_ORIGIN = /^https?:\/\/(?:localhost|127\.0\.0\.1|\[::1\])(?::\d+)?$/;

// Same-origin pages (/jarvis, /jarvis/camera served by this service, at whatever host the laptop uses)
// compare by host so a TLS-terminating tunnel still counts as same-origin.
export function originAllowed(origin: string, requestUrl: string, extra: readonly string[] = []): boolean {
  if (LOCAL_ORIGIN.test(origin) || extra.includes(origin)) return true;
  try {
    return new URL(origin).host === new URL(requestUrl).host;
  } catch {
    return false;
  }
}

export interface PatientListEntry {
  patientId: string;
  scenarioId: string;
  kind: string;
  title: string;
  displayLabel: string;
  procedureId: string;
  procedureTitle: string;
  urgency: string;
  status: string;
  flagCount: number;
  // True when an authored diagnosis-office interview exists for this patient (explore page "Begin encounter").
  encounterAvailable: boolean;
  actions: Action[];
}

const ID_PATTERN = /^[a-z0-9][a-z0-9_-]{0,79}$/;
const SESSION_PATTERN = /^cs_[a-z0-9]{6,64}$/;

export function createApp(options: AppOptions = {}) {
  const client = options.client ?? createFinchNodeClient();
  const now = options.now ?? (() => new Date());
  const app = new Hono();

  // Browsers may call this service only from allowed origins. A disallowed Origin is refused outright,
  // not just denied CORS headers, because simple (no-preflight) requests would otherwise still execute.
  const corsOrigins = options.corsOrigins ?? [];
  app.use("*", async (c, next) => {
    const origin = c.req.header("Origin");
    if (origin !== undefined && !originAllowed(origin, c.req.url, corsOrigins)) {
      return c.json({ error: { code: "origin_not_allowed", message: `Browser origin ${origin} is not allowed. Add it to PREOP_CORS_ORIGINS.` }, actions: [] }, 403);
    }
    await next();
  });
  app.use("*", cors({ origin: (origin, c) => (originAllowed(origin, c.req.url, corsOrigins) ? origin : null) }));

  // Sandbox admissions live in memory: sessionId -> scenario, and admitted subject -> scenario.
  const admissions = new Map<string, string>();
  const connectUrls = new Map<string, string>();
  const subjectScenario = new Map<string, string>();

  const fail = (c: Context, status: 400 | 404 | 409 | 429 | 502, code: string, message: string, actions: Action[]) =>
    c.json({ error: { code, message }, actions: actions.length ? actions : [routes.index()] }, status);

  async function scenarios(): Promise<Scenario[]> {
    try {
      return await client.listScenarios();
    } catch {
      return [];
    }
  }

  // Accept either a FinchNode subject ("patient-demo-001") or a scenario id ("baseline-adult").
  async function resolve(id: string): Promise<{ subject: string; scenarioId: string } | null> {
    if (!ID_PATTERN.test(id)) return null;
    const admitted = subjectScenario.get(id);
    if (admitted) return { subject: id, scenarioId: admitted };
    const list = await scenarios();
    const byScenario = list.find((s) => s.id === id && s.subject);
    if (byScenario?.subject) return { subject: byScenario.subject, scenarioId: byScenario.id };
    return { subject: id, scenarioId: list.find((s) => s.subject === id)?.id ?? "" };
  }

  // Sandbox sources are labeled "<health system> · <scenario title>", which maps a consented subject
  // back to the scenario whose authored case plan it should use.
  async function scenariosFromOrganizations(organizations: string[]): Promise<string[]> {
    const list = await scenarios();
    const found: string[] = [];
    for (const org of organizations) {
      const title = org.split(" \u00b7 ").slice(1).join(" \u00b7 ").trim();
      const match = title ? list.find((s) => s.title === title && s.subject) : undefined;
      if (match && !found.includes(match.id)) found.push(match.id);
    }
    return found;
  }

  // The most recently connected source decides the case plan.
  async function scenarioFromOrganizations(organizations: string[]): Promise<string> {
    return (await scenariosFromOrganizations(organizations)).at(-1) ?? "";
  }

  async function loadCase(subject: string, scenarioId: string): Promise<SurgicalCase | FinchNodeError> {
    try {
      const record = await client.getRecord(subject);
      const extraGaps: DataGap[] = [];
      if (!subject.startsWith("patient-demo-")) {
        const matched = await scenariosFromOrganizations((record.sources ?? []).map((s) => s.organization));
        // Sources can change after consent (connect, disconnect), so the live record wins over any cached scenario.
        scenarioId = matched.at(-1) ?? scenarioId;
        if (scenarioId) subjectScenario.set(subject, scenarioId);
        // One FinchNode account connected sources that describe different people.
        if (matched.length > 1) {
          extraGaps.push({
            code: "identity_mismatch",
            message: `Connected sources describe different patients (${matched.join(", ")}). Confirm identity and disconnect the wrong source before surgery.`,
          });
        }
      }
      const planSubject = (await scenarios()).find((s) => s.id === scenarioId)?.subject ?? subject;
      return buildCase(record, scenarioId, now(), planSubject, extraGaps);
    } catch (err) {
      if (err instanceof FinchNodeError) return err;
      throw err;
    }
  }

  async function caseOrUnavailable(subject: string, scenarioId: string): Promise<SurgicalCase | null> {
    const result = await loadCase(subject, scenarioId);
    if (!(result instanceof FinchNodeError)) return result;
    if (result.status === 404) return null;
    return unavailableCase(subject, scenarioId, result, now());
  }

  const scenarioDemoSubject = new Map<string, string>();

  async function listPatients(): Promise<PatientListEntry[]> {
    const list = await scenarios();
    for (const s of list) if (s.subject) scenarioDemoSubject.set(s.id, s.subject);
    const entries = await Promise.all(
      list.map(async (s): Promise<PatientListEntry> => {
        if (!s.subject) {
          return {
            patientId: "",
            scenarioId: s.id,
            kind: s.kind,
            title: s.title,
            displayLabel: s.title,
            procedureId: "",
            procedureTitle: "",
            urgency: "",
            status: "connect",
            flagCount: 0,
            encounterAvailable: false,
            actions: [routes.connect(s.id, "Start health system connection"), routes.patients()],
          };
        }
        const kase = (await caseOrUnavailable(s.subject, s.id)) ?? unavailableCase(s.subject, s.id, new FinchNodeError(404, "patient_not_found", "Patient not found"), now());
        return {
          patientId: s.subject,
          scenarioId: s.id,
          kind: s.kind,
          title: s.title,
          displayLabel: kase.patient.displayLabel,
          procedureId: kase.procedureId,
          procedureTitle: kase.procedure.title,
          urgency: kase.urgency,
          status: kase.status,
          flagCount: kase.brief.flags.length,
          encounterAvailable: ENCOUNTERS_BY_PLAN.has(s.subject) && kase.status !== "blocked",
          actions: client.sandbox && s.kind !== "session" ? [routes.caseFor(s.subject), routes.admit(s.id)] : [routes.caseFor(s.subject)],
        };
      }),
    );
    if (client.sandbox) {
      try {
        for (const user of await client.sandbox.listUsers()) {
          if (subjectScenario.has(user.id)) continue;
          subjectScenario.set(user.id, await scenarioFromOrganizations((user.sources ?? []).map((src) => src.organization)));
        }
      } catch {
        // Listing consented patients is best effort; demo patients still list.
      }
    }
    const admitted = await Promise.all(
      [...subjectScenario].map(async ([subject, scenarioId]): Promise<PatientListEntry> => {
        const kase = (await caseOrUnavailable(subject, scenarioId)) ?? unavailableCase(subject, scenarioId, new FinchNodeError(404, "patient_not_found", "Patient not found"), now());
        return {
          patientId: subject,
          scenarioId,
          kind: "sandbox",
          title: `Admitted via FinchNode Connect (${scenarioId})`,
          displayLabel: kase.patient.displayLabel,
          procedureId: kase.procedureId,
          procedureTitle: kase.procedure.title,
          urgency: kase.urgency,
          status: kase.status,
          flagCount: kase.brief.flags.length,
          // Sandbox patients use the encounter of the demo patient their scenario mirrors.
          encounterAvailable: ENCOUNTERS_BY_PLAN.has(scenarioDemoSubject.get(scenarioId) ?? subject) && kase.status !== "blocked",
          actions: [routes.caseFor(subject)],
        };
      }),
    );
    return [...entries, ...admitted];
  }

  function admissionStatus(session: SandboxSession, scenarioId: string): AdmissionStatus {
    const demoSubject = scenarioDemoSubject.get(scenarioId) ?? "";
    const demoCase = demoSubject ? [routes.caseFor(demoSubject, "Use the demo record instead")] : [];
    const simState = session.simulation?.state ?? "";
    const done = simState === "completed" && !!session.subject;
    const failed = simState === "failed" || ["canceled", "expired", "abandoned", "failed"].includes(session.status);
    if (done && session.subject) subjectScenario.set(session.subject, scenarioId);
    const base = {
      sessionId: session.id,
      scenarioId,
      connectUrl: session.url ?? connectUrls.get(session.id) ?? "",
      sessionStatus: session.status,
      syncStatus: session.sync?.status ?? "",
      patientId: session.subject ?? "",
      organization: session.organization ?? "",
    };
    if (done) {
      return { ...base, state: "completed", say: `Records connected from ${session.organization ?? "the health system"} with the patient's consent.`, actions: [routes.caseFor(session.subject ?? ""), routes.patients()] };
    }
    if (failed) {
      return { ...base, state: "failed", say: "The health system connection didn't finish. Try again, or use the demo record.", actions: [routes.admit(scenarioId), ...demoCase, routes.patients()] };
    }
    return {
      ...base,
      state: "connecting",
      say: base.connectUrl
        ? "Open the FinchNode Connect link on the laptop, choose the patient's health system, and approve sharing. I'll pick up the records as soon as consent is recorded."
        : "Connecting to the patient's health system and waiting for their consent.",
      actions: [routes.admission(session.id), ...demoCase, routes.patients()],
    };
  }

  app.get("/", (c) =>
    c.json({
      service: "scalpal-preop",
      description: "FinchNode synthetic health records turned into pre-op briefs and Unity-ready surgical cases.",
      disclaimer: DISCLAIMER,
      actions: [
        routes.patients(),
        { id: "list_procedures", label: "Procedures", method: "GET", route: "/procedures" },
        { id: "list_anatomy", label: "Anatomy catalog", method: "GET", route: "/anatomy" },
        { id: "list_instruments", label: "Instruments", method: "GET", route: "/instruments" },
        { id: "unity_bundle", label: "Unity offline bundle", method: "GET", route: "/unity/bundle" },
        { id: "health", label: "Health", method: "GET", route: "/health" },
      ] satisfies Action[],
    }),
  );

  app.get("/health", (c) => c.json({ ok: true, actions: [routes.index()] }));

  app.get("/patients", async (c) => c.json({ patients: await listPatients(), actions: [routes.index()] }));

  app.get("/patients/:id/brief", async (c) => {
    const target = await resolve(c.req.param("id"));
    if (!target) return fail(c, 400, "invalid_patient_id", "Patient ids are lowercase letters, digits, and dashes.", [routes.patients()]);
    try {
      const brief = buildBrief(await client.getRecord(target.subject), now());
      return c.json({ ...brief, actions: [routes.caseFor(target.subject), routes.patients()] });
    } catch (err) {
      if (!(err instanceof FinchNodeError)) throw err;
      if (err.status === 404) return fail(c, 404, err.code, err.message, [routes.patients()]);
      const kase = unavailableCase(target.subject, target.scenarioId, err, now());
      if (err.status === 429) c.header("Retry-After", String(err.retryAfterSeconds));
      return fail(c, kase.status === "retry" ? 429 : 409, err.code, kase.statusReason, kase.actions);
    }
  });

  app.get("/patients/:id/case", async (c) => {
    const target = await resolve(c.req.param("id"));
    if (!target) return fail(c, 400, "invalid_patient_id", "Patient ids are lowercase letters, digits, and dashes.", [routes.patients()]);
    const kase = await caseOrUnavailable(target.subject, target.scenarioId);
    if (!kase) return fail(c, 404, "patient_not_found", "No synthetic patient with that id.", [routes.patients()]);
    // Unavailable cases still return 200: they are renderable states with their own actions.
    return c.json(kase);
  });

  app.post("/patients/:id/preop-check", async (c) => {
    const target = await resolve(c.req.param("id"));
    if (!target) return fail(c, 400, "invalid_patient_id", "Patient ids are lowercase letters, digits, and dashes.", [routes.patients()]);
    const body = (await c.req.json().catch(() => ({}))) as { selected?: unknown; scope?: unknown };
    if (body.scope !== undefined && body.scope !== "" && body.scope !== "chart" && body.scope !== "surgical") {
      return fail(c, 400, "invalid_scope", 'scope is "chart" (default) or "surgical".', [routes.caseFor(target.subject)]);
    }
    if (!Array.isArray(body.selected) || body.selected.some((s) => typeof s !== "string")) {
      return fail(c, 400, "invalid_selection", 'Send {"selected": ["bleeding", ...]} using checklistOptions types.', [routes.caseFor(target.subject)]);
    }
    const kase = await caseOrUnavailable(target.subject, target.scenarioId);
    if (!kase) return fail(c, 404, "patient_not_found", "No synthetic patient with that id.", [routes.patients()]);
    if (!kase.procedureId) return fail(c, 409, "case_unavailable", kase.statusReason, kase.actions);
    return c.json(scorePreopCheck(kase, body.selected as string[], body.scope === "surgical" ? "surgical" : "chart"));
  });

  app.get("/procedures", (c) =>
    c.json({
      procedures: PROCEDURES.map((p) => ({ id: p.id, title: p.title, shortTitle: p.shortTitle, summary: p.summary, stepCount: p.steps.length, actions: [routes.procedure(p.id)] })),
      actions: [routes.index()],
    }),
  );

  app.get("/procedures/:id", (c) => {
    const procedure = PROCEDURES_BY_ID.get(c.req.param("id"));
    if (!procedure) return fail(c, 404, "procedure_not_found", "Unknown procedure.", [{ id: "list_procedures", label: "Procedures", method: "GET", route: "/procedures" }]);
    return c.json({ ...procedure, actions: [{ id: "list_procedures", label: "All procedures", method: "GET", route: "/procedures" }, routes.patients()] });
  });

  app.get("/anatomy", (c) => c.json({ structures: ANATOMY, actions: [routes.index()] }));
  app.get("/instruments", (c) => c.json({ instruments: INSTRUMENTS, actions: [routes.index()] }));

  // Sandbox Connect: create a real session, let a synthetic patient consent, then poll for the subject.
  app.post("/admit/:scenarioId", async (c) => {
    const scenarioId = c.req.param("scenarioId");
    const scenario = (await scenarios()).find((s) => s.id === scenarioId && s.subject);
    if (!scenario?.subject) return fail(c, 404, "scenario_not_found", "Unknown or non-patient FinchNode scenario.", [routes.patients()]);
    scenarioDemoSubject.set(scenarioId, scenario.subject);
    const demoCase = routes.caseFor(scenario.subject, "Use the demo record instead");
    if (!client.sandbox) {
      return c.json({
        sessionId: "", scenarioId, connectUrl: "", state: "unavailable", sessionStatus: "", syncStatus: "", patientId: "", organization: "",
        say: "Live admission needs a FinchNode sandbox key on the server. Using the demo record works the same.",
        actions: [demoCase, routes.patients()],
      } satisfies AdmissionStatus);
    }
    try {
      const session = await client.sandbox.admit(scenarioId, { simulate: options.simulateAdmissions === true });
      connectUrls.set(session.id, session.url ?? "");
      admissions.set(session.id, scenarioId);
      return c.json(admissionStatus(session, scenarioId));
    } catch (err) {
      if (!(err instanceof FinchNodeError)) throw err;
      return fail(c, 502, err.code, err.message, [routes.admit(scenarioId), demoCase, routes.patients()]);
    }
  });

  app.get("/admissions/:sessionId", async (c) => {
    const sessionId = c.req.param("sessionId");
    const scenarioId = admissions.get(sessionId);
    if (!SESSION_PATTERN.test(sessionId) || !scenarioId || !client.sandbox) {
      return fail(c, 404, "admission_not_found", "No admission with that id on this server.", [routes.patients()]);
    }
    try {
      return c.json(admissionStatus(await client.sandbox.getSession(sessionId), scenarioId));
    } catch (err) {
      if (!(err instanceof FinchNodeError)) throw err;
      return fail(c, 502, err.code, err.message, [routes.admission(sessionId), routes.patients()]);
    }
  });

  app.post("/connect/:scenarioId", async (c) => {
    const scenarioId = c.req.param("scenarioId");
    const scenario = (await scenarios()).find((s) => s.id === scenarioId);
    if (!scenario) return fail(c, 404, "scenario_not_found", "Unknown FinchNode scenario.", [routes.patients()]);
    try {
      const session = await client.createConnectSession(scenarioId);
      const patientId = session.patient_id ?? scenario.subject ?? "";
      const failed = session.status === "failed" || session.status === "cancelled";
      return c.json({
        sessionId: session.id,
        scenarioId,
        status: session.status,
        failureCode: session.failure_code ?? "",
        failureMessage: session.failure_message ?? "",
        patientId,
        say: failed
          ? `The health system connection ${session.status === "cancelled" ? "was cancelled" : "failed"}${session.failure_message ? `: ${session.failure_message}` : "."} Pick a patient whose records are already connected.`
          : "Connection started.",
        actions: [...(patientId && !failed ? [routes.caseFor(patientId)] : []), routes.connect(scenarioId, "Try connecting again"), routes.patients()],
      });
    } catch (err) {
      if (!(err instanceof FinchNodeError)) throw err;
      return fail(c, 502, err.code, err.message, [routes.connect(scenarioId, "Try connecting again"), routes.patients()]);
    }
  });

  // One payload with everything Unity needs to run offline: catalogs plus every patient case.
  app.get("/unity/bundle", async (c) => {
    const list = await scenarios();
    const cases = (
      await Promise.all(list.filter((s) => s.subject).map((s) => caseOrUnavailable(s.subject ?? "", s.id)))
    ).filter((k): k is SurgicalCase => k != null);
    return c.json({
      generatedAt: now().toISOString(),
      anatomy: ANATOMY,
      instruments: INSTRUMENTS,
      procedures: PROCEDURES,
      patients: await listPatients(),
      cases,
      disclaimer: DISCLAIMER,
      actions: [routes.index()],
    });
  });

  // ElevenLabs server tools: always HTTP 200 with a `say` line so Jarvis can speak failures too.
  app.post("/tools/list_patients", async (c) => {
    const patients = (await listPatients()).filter((p) => p.patientId);
    return c.json({
      patients: patients.map((p) => ({ patientId: p.patientId, label: p.displayLabel, procedure: p.procedureTitle, urgency: p.urgency, status: p.status })),
      say: `There are ${patients.length} patients: ${patients.map((p) => `${p.displayLabel} for ${p.procedureTitle.toLowerCase() || "no procedure"}`).join("; ")}.`,
    });
  });

  app.post("/tools/get_case", async (c) => {
    const body = (await c.req.json().catch(() => ({}))) as { patientId?: string };
    const target = await resolve(String(body.patientId ?? ""));
    const kase = target ? await caseOrUnavailable(target.subject, target.scenarioId) : null;
    if (!kase) return c.json({ status: "not_found", say: "I can't find that patient. Ask me to list the patients." });
    if (!kase.procedureId) return c.json({ status: kase.status, say: kase.brief.say, retryAfterSeconds: kase.retryAfterSeconds });
    return c.json({
      status: kase.status,
      caseId: kase.caseId,
      patient: kase.patient.displayLabel,
      procedure: kase.procedure.title,
      urgency: kase.urgency,
      indication: kase.indication,
      presentation: kase.presentation,
      flags: kase.brief.flags.map((f) => ({ type: f.type, severity: f.severity, title: f.title, detail: f.detail })),
      dataGaps: kase.brief.dataGaps.map((g) => g.message),
      checklistOptions: kase.checklistOptions,
      say: `${kase.patient.displayLabel}, here for ${kase.procedure.title.toLowerCase()}: ${kase.indication.toLowerCase()}. ${kase.brief.say}`,
    });
  });

  app.post("/tools/check_preop", async (c) => {
    const body = (await c.req.json().catch(() => ({}))) as { patientId?: string; selected?: unknown };
    const target = await resolve(String(body.patientId ?? ""));
    const kase = target ? await caseOrUnavailable(target.subject, target.scenarioId) : null;
    if (!kase || !kase.procedureId) return c.json({ status: "unavailable", say: "That case isn't available to check right now." });
    const selected = Array.isArray(body.selected) ? body.selected.filter((s): s is string => typeof s === "string") : [];
    const result = scorePreopCheck(kase, selected);
    return c.json({ status: result.passed ? "passed" : "needs_work", score: result.score, total: result.total, feedback: result.feedback, say: result.say });
  });

  app.post("/tools/get_step", async (c) => {
    const body = (await c.req.json().catch(() => ({}))) as { patientId?: string; stepId?: string };
    const target = await resolve(String(body.patientId ?? ""));
    const kase = target ? await caseOrUnavailable(target.subject, target.scenarioId) : null;
    if (!kase || !kase.procedureId) return c.json({ status: "unavailable", say: "That case isn't available right now." });
    const stepId = body.stepId || kase.procedure.firstStep;
    const step = kase.procedure.steps.find((s) => s.id === stepId);
    if (!step) return c.json({ status: "unknown_step", say: `That step isn't part of ${kase.procedure.title.toLowerCase()}.`, steps: kase.procedure.steps.map((s) => s.id) });
    const notes = kase.considerations.filter((n) => n.stepId === step.id).map((n) => n.note);
    const next = kase.procedure.steps.find((s) => s.id === step.next);
    return c.json({
      status: "ok",
      stepId: step.id,
      title: step.title,
      instruction: step.instruction,
      hints: step.hints,
      patientNotes: notes,
      nextStepId: step.next,
      say: [step.instruction, ...notes, next ? `Next up: ${next.title.toLowerCase()}.` : "That's the last step."].join(" "),
    });
  });

  const encounters = registerEncounterRoutes(app, {
    now,
    realtime: options.realtime ?? undefined,
    loadCase: async (id) => {
      const target = await resolve(id);
      return target ? caseOrUnavailable(target.subject, target.scenarioId) : null;
    },
    // Sandbox patients use the encounter authored for the demo patient their scenario mirrors.
    planSubjectFor: async (kase) => (await scenarios()).find((s) => s.id === kase.scenarioId)?.subject ?? kase.patientId,
  });

  // The pre-op office (current flow): committed patient content, choice-based interview, no Jarvis.
  const interviews = registerInterviewRoutes(app, {
    now,
    realtime: options.realtime ?? undefined,
    classifier: options.answerClassifier ?? null,
    speechToText: options.speechToText ?? null,
    elevenLabs: options.elevenLabs,
    contentRoot: options.interviewContentRoot,
    loadCase: async (id) => {
      const target = await resolve(id);
      return target ? caseOrUnavailable(target.subject, target.scenarioId) : null;
    },
    planSubjectFor: async (kase) => (await scenarios()).find((s) => s.id === kase.scenarioId)?.subject ?? kase.patientId,
  });

  registerCoachRoutes(app, {
    now,
    tickMs: options.coachTickMs,
    stuckPolicy: options.stuckPolicy,
    elevenLabs: options.elevenLabs,
    reflex: options.reflex,
    toolAckWaitMs: options.toolAckWaitMs,
    realtime: options.realtime ?? undefined,
    bridge: options.realtime ?? null,
    encounters,
    encounterFor: (id) => encounters.get(id) ?? interviews.get(id),
    patientStatus: (id) => patientStatusFor(id, options.interviewContentRoot),
    vitalsUrl: options.vitalsUrl,
    // VR baseline (and AR until Presage is captured): the patient's latest charted vitals and weight.
    baselineFor: async (kase) => {
      const record = await client.getRecord(kase.patientId).catch(() => null);
      if (!record) return null;
      const chart = chartBaseline((record.data as { vitals?: never[] } | undefined)?.vitals ?? [], kase.patient.age);
      return { baseline: chart.baseline, weightKg: chart.weightKg, spo2: chart.spo2, mlPerKg: chart.mlPerKg };
    },
    vision: options.vision ?? null,
    watchMs: options.watchMs,
    detector: options.detector ?? null,
    loadCase: async (id) => {
      const target = await resolve(id);
      return target ? caseOrUnavailable(target.subject, target.scenarioId) : null;
    },
  });

  app.notFound((c) => fail(c, 404, "route_not_found", `No route ${c.req.method} ${c.req.path}.`, [routes.index()]));
  app.onError((err, c) => {
    console.error(err);
    return c.json({ error: { code: "internal_error", message: "Unexpected server error." }, actions: [routes.index()] }, 500);
  });

  return app;
}
