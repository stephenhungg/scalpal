import type { Context, Hono } from "hono";
import { ENCOUNTERS_BY_PLAN, type Encounter } from "./catalog/encounters.js";
import { DEFAULT_PATIENT_VOICES, EncounterSession } from "./encounter.js";
import { attendingFirstMessage, attendingPrompt, patientFirstMessage, patientPrompt } from "./encounter-prompt.js";
import { NO_REALTIME, type RealtimeSink } from "./realtime-bridge.js";
import type { Action, SurgicalCase } from "./types.js";

// Pre-op encounter API: patient interview (patient agent), case presentation (Jarvis as attending),
// then a deterministic scorecard. Tools are implemented here once for every voice client.

export interface EncounterRouteOptions {
  loadCase: (patientId: string) => Promise<SurgicalCase | null>;
  planSubjectFor: (kase: SurgicalCase) => Promise<string>;
  now: () => Date;
  patientVoices?: Partial<Record<Encounter["persona"]["voiceKey"], string>>;
  realtime?: RealtimeSink;
}

const ENCOUNTER_ID = /^enc-[a-z0-9]{6,40}$/;
const MAX = 50;

const encounterActions = (id: string): Action[] => [
  { id: "encounter_state", label: "Encounter state", method: "GET", route: `/encounters/${id}` },
  { id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" },
];

export function registerEncounterRoutes(app: Hono, options: EncounterRouteOptions) {
  const sessions = new Map<string, EncounterSession>();
  const voices = { ...DEFAULT_PATIENT_VOICES, ...(options.patientVoices ?? {}) };
  const realtime = options.realtime ?? NO_REALTIME;

  const bad = (c: Context, status: 400 | 404 | 409, code: string, message: string, actions: Action[]) => c.json({ error: { code, message }, actions }, status);
  const get = (c: Context) => {
    const id = c.req.param("id") ?? "";
    return ENCOUNTER_ID.test(id) ? (sessions.get(id) ?? null) : null;
  };
  const missing = (c: Context) =>
    bad(c, 404, "encounter_not_found", "No live encounter with that id. Start one from a patient.", [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
  const body = async (c: Context) => (await c.req.json().catch(() => ({}))) as Record<string, unknown>;

  app.post("/encounters", async (c) => {
    const { patientId } = await body(c);
    const kase = typeof patientId === "string" ? await options.loadCase(patientId) : null;
    if (!kase) return bad(c, 404, "patient_not_found", 'Send {"patientId": "<FinchNode subject>"} for a known patient.', [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    if (!kase.procedureId) return bad(c, 409, "case_unavailable", kase.statusReason, kase.actions);
    const encounter = ENCOUNTERS_BY_PLAN.get(await options.planSubjectFor(kase));
    if (!encounter) {
      return bad(c, 404, "no_encounter", "This patient has no authored interview yet. Go straight to surgery.", [{ id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" }]);
    }
    if (sessions.size >= MAX) sessions.delete(sessions.keys().next().value!);
    const id = `enc-${crypto.randomUUID().replaceAll("-", "").slice(0, 16)}`;
    const s = new EncounterSession(id, kase, encounter, options.now);
    sessions.set(id, s);
    realtime.attachEncounter(s);
    return c.json(
      {
        encounterId: id,
        speaker: encounter.persona.speaker,
        speakerName: encounter.persona.name,
        patientName: encounter.persona.patientName,
        voiceId: voices[encounter.persona.voiceKey],
        patientPrompt: patientPrompt(s),
        patientFirstMessage: patientFirstMessage(s),
        state: s.state(),
        actions: encounterActions(id),
      },
      201,
    );
  });

  app.get("/encounters/:id", (c) => {
    const s = get(c);
    if (!s) return missing(c);
    return c.json({ state: s.state(), actions: encounterActions(s.id) });
  });

  // Interview over: hand the learner to Jarvis as attending for the case presentation.
  app.post("/encounters/:id/attending", (c) => {
    const s = get(c);
    if (!s) return missing(c);
    if (s.phase === "interview") {
      s.phase = "attending";
      realtime.encounterPhase(s);
    }
    return c.json({ phase: s.phase, attendingPrompt: attendingPrompt(s), attendingFirstMessage: attendingFirstMessage(s), state: s.state(), actions: encounterActions(s.id) });
  });

  app.post("/encounters/:id/transcript", async (c) => {
    const s = get(c);
    if (!s) return missing(c);
    const { speaker, text } = await body(c);
    if ((speaker !== "learner" && speaker !== "patient" && speaker !== "coach") || typeof text !== "string" || !text.trim()) {
      return bad(c, 400, "invalid_transcript", 'Send {"speaker": "learner" | "patient" | "coach", "text": "..."}.', encounterActions(s.id));
    }
    s.addTranscript(speaker, text.trim());
    return c.json({ lines: s.transcript.length, actions: encounterActions(s.id) });
  });

  app.get("/encounters/:id/score", (c) => {
    const s = get(c);
    if (!s) return missing(c);
    return c.json({ scorecard: s.score(), state: s.state(), actions: encounterActions(s.id) });
  });

  app.post("/encounters/:id/tools/:name", async (c) => {
    const s = get(c);
    if (!s) return missing(c);
    const p = await body(c);
    const str = (k: string) => (typeof p[k] === "string" ? (p[k] as string) : "");
    let result: string;
    switch (c.req.param("name")) {
      case "answer":
        result = s.answer(str("topic"));
        break;
      case "examine":
        result = s.examine(str("maneuver"));
        break;
      case "order_test":
        result = s.orderTest(str("test"));
        break;
      case "get_encounter_summary":
        result = s.summary();
        break;
      case "record_assessment": {
        s.recordAssessment({ diagnosis: str("diagnosis"), differential: p.differential as string[], procedure: str("procedure"), urgency: str("urgency") });
        s.phase = "scored";
        const card = s.score();
        realtime.encounterResult(s, card);
        result = `Recorded. Score ${card.total} of 100 (${card.grade}). Key feedback, most important first: ${card.feedback.slice(0, 4).join(" ")} Tell them the score and the most important one or two points in your own words, briefly.`;
        break;
      }
      default:
        return bad(c, 404, "unknown_tool", `No encounter tool named "${c.req.param("name")}".`, encounterActions(s.id));
    }
    return c.json({ result, state: s.state(), actions: encounterActions(s.id) });
  });
}
