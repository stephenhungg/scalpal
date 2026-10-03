import { Hono, type Context } from "hono";
import { cors } from "hono/cors";
import { ANATOMY } from "./catalog/anatomy.js";
import { INSTRUMENTS } from "./catalog/instruments.js";
import { PROCEDURES, PROCEDURES_BY_ID } from "./catalog/procedures.js";
import { buildBrief, DISCLAIMER } from "./brief.js";
import { buildCase, routes, scorePreopCheck, unavailableCase } from "./case-builder.js";
import { FinchNodeError, createFinchNodeClient, type FinchNodeClient } from "./finchnode.js";
import type { Action, Scenario, SurgicalCase } from "./types.js";

export interface AppOptions {
  client?: FinchNodeClient;
  now?: () => Date;
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
  actions: Action[];
}

const ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,79}$/;

export function createApp(options: AppOptions = {}) {
  const client = options.client ?? createFinchNodeClient();
  const now = options.now ?? (() => new Date());
  const app = new Hono();

  app.use("*", cors());

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
    const list = await scenarios();
    const byScenario = list.find((s) => s.id === id && s.subject);
    if (byScenario?.subject) return { subject: byScenario.subject, scenarioId: byScenario.id };
    return { subject: id, scenarioId: list.find((s) => s.subject === id)?.id ?? "" };
  }

  async function loadCase(subject: string, scenarioId: string): Promise<SurgicalCase | FinchNodeError> {
    try {
      return buildCase(await client.getRecord(subject), scenarioId, now());
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

  async function listPatients(): Promise<PatientListEntry[]> {
    const list = await scenarios();
    return Promise.all(
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
          actions: [routes.caseFor(s.subject)],
        };
      }),
    );
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
    const body = (await c.req.json().catch(() => ({}))) as { selected?: unknown };
    if (!Array.isArray(body.selected) || body.selected.some((s) => typeof s !== "string")) {
      return fail(c, 400, "invalid_selection", 'Send {"selected": ["bleeding", ...]} using checklistOptions types.', [routes.caseFor(target.subject)]);
    }
    const kase = await caseOrUnavailable(target.subject, target.scenarioId);
    if (!kase) return fail(c, 404, "patient_not_found", "No synthetic patient with that id.", [routes.patients()]);
    if (!kase.procedureId) return fail(c, 409, "case_unavailable", kase.statusReason, kase.actions);
    return c.json(scorePreopCheck(kase, body.selected as string[]));
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

  app.notFound((c) => fail(c, 404, "route_not_found", `No route ${c.req.method} ${c.req.path}.`, [routes.index()]));
  app.onError((err, c) => {
    console.error(err);
    return c.json({ error: { code: "internal_error", message: "Unexpected server error." }, actions: [routes.index()] }, 500);
  });

  return app;
}
