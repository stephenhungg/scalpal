import type { Context, Hono } from "hono";
import { ENCOUNTERS_BY_PLAN, EXAM_MANEUVERS, HISTORY_TOPICS, TESTS, type Encounter } from "./catalog/encounters.js";
import { DEFAULT_PATIENT_VOICES, EncounterSession, demographicsMatch } from "./encounter.js";
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

// What /jarvis/connection needs to bind a voice connection to a live encounter, with the server-built prompt.
export interface EncounterVoice {
  role: "patient" | "attending";
  prompt: string;
  firstMessage: string;
  voiceId: string;
}
export interface EncounterVoices {
  // null: no live encounter with that id; "scored": nothing left to talk about.
  voiceFor(encounterId: string): EncounterVoice | "scored" | null;
  anyLive(role: EncounterVoice["role"]): boolean;
}

const ENCOUNTER_ID = /^enc-[a-z0-9]{6,40}$/;
const MAX = 50;

const encounterActions = (id: string): Action[] => [
  { id: "encounter_state", label: "Encounter state", method: "GET", route: `/encounters/${id}` },
  { id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" },
];

export function registerEncounterRoutes(app: Hono, options: EncounterRouteOptions): EncounterVoices & { get(id: string): EncounterSession | null } {
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
  const body = async (c: Context): Promise<Record<string, unknown>> => {
    const value: unknown = await c.req.json().catch(() => ({}));
    return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
  };
  const wrongPhase = (c: Context, s: EncounterSession, required: string) =>
    bad(c, 409, "invalid_phase", `This action requires ${required}; encounter is ${s.phase}.`, encounterActions(s.id));

  app.post("/encounters", async (c) => {
    const { patientId } = await body(c);
    const kase = typeof patientId === "string" ? await options.loadCase(patientId) : null;
    if (!kase) return bad(c, 404, "patient_not_found", 'Send {"patientId": "<FinchNode subject>"} for a known patient.', [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }]);
    if (!kase.procedureId) return bad(c, 409, "case_unavailable", kase.statusReason, kase.actions);
    const encounter = ENCOUNTERS_BY_PLAN.get(await options.planSubjectFor(kase));
    if (!encounter) {
      return bad(c, 404, "no_encounter", "This patient has no authored interview yet. Go straight to surgery.", [{ id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" }]);
    }
    // Authored demo symptoms must never be attached to real records or a different patient.
    if (!kase.brief.synthetic) return bad(c, 409, "synthetic_only", "Authored interviews are available only for synthetic demo patients.", kase.actions);
    if (!demographicsMatch(encounter, kase)) {
      return bad(c, 409, "demographics_mismatch", "The chart demographics do not match this authored demo interview.", kase.actions);
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
    if (s.phase === "scored") return wrongPhase(c, s, "interview or attending");
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
    if (s.phase !== "scored") return wrongPhase(c, s, "scored");
    return c.json({ scorecard: s.score(), state: s.state(), actions: encounterActions(s.id) });
  });

  app.post("/encounters/:id/tools/:name", async (c) => {
    const s = get(c);
    if (!s) return missing(c);
    const p = await body(c);
    const str = (k: string) => (typeof p[k] === "string" ? (p[k] as string) : "");
    const name = c.req.param("name");
    const interviewTools: Record<string, { key: string; ids: readonly string[] }> = {
      answer: { key: "topic", ids: HISTORY_TOPICS },
      examine: { key: "maneuver", ids: EXAM_MANEUVERS },
      order_test: { key: "test", ids: TESTS },
    };
    const interviewTool = interviewTools[name ?? ""];
    if (interviewTool) {
      if (s.phase !== "interview") return wrongPhase(c, s, "interview");
      if (!interviewTool.ids.includes(str(interviewTool.key))) {
        return bad(c, 400, "invalid_tool_argument", `Send a supported ${interviewTool.key}.`, encounterActions(s.id));
      }
    }
    // The summary includes exam findings and test results, so the patient agent (interview phase) must never get it.
    if (name === "get_encounter_summary" && s.phase === "interview") return wrongPhase(c, s, "attending or scored");
    if (name === "record_assessment") {
      if (s.phase !== "attending") return wrongPhase(c, s, "attending");
      if (!["diagnosis", "procedure", "urgency"].every((key) => typeof p[key] === "string") ||
          !Array.isArray(p.differential) || !p.differential.every((item) => typeof item === "string")) {
        return bad(c, 400, "invalid_assessment", "Send diagnosis, procedure, urgency as strings and differential as an array of strings.", encounterActions(s.id));
      }
    }
    let result: string;
    let display: string;
    switch (name) {
      case "answer":
        result = s.answer(str("topic"));
        display = s.log.at(-1)?.text ?? "Unknown.";
        if (display === "unknown") display = "I don't know or don't remember.";
        break;
      case "examine":
        result = s.examine(str("maneuver"));
        display = s.encounter.exam[str("maneuver") as keyof typeof s.encounter.exam]?.reaction || "Patient response not available for this case.";
        break;
      case "order_test":
        result = s.orderTest(str("test"));
        display = "Test ordered. See the clinician chart.";
        break;
      case "get_encounter_summary":
        result = s.summary();
        display = result;
        break;
      case "record_assessment": {
        s.recordAssessment({ diagnosis: str("diagnosis"), differential: p.differential as string[], procedure: str("procedure"), urgency: str("urgency") });
        s.phase = "scored";
        const card = s.score();
        realtime.encounterResult(s, card);
        result = `Recorded. Score ${card.total} of 100 (${card.grade}). Key feedback, most important first: ${card.feedback.slice(0, 4).join(" ")} Tell them the score and the most important one or two points in your own words, briefly.`;
        display = card.spoken;
        break;
      }
      default:
        return bad(c, 404, "unknown_tool", `No encounter tool named "${c.req.param("name")}".`, encounterActions(s.id));
    }
    return c.json({ result, display, state: s.state(), actions: encounterActions(s.id) });
  });

  const roleOf = (s: EncounterSession) => (s.phase === "interview" ? "patient" : s.phase === "attending" ? "attending" : null);
  return {
    voiceFor(encounterId) {
      const s = ENCOUNTER_ID.test(encounterId) ? sessions.get(encounterId) : undefined;
      if (!s) return null;
      const role = roleOf(s);
      if (role === "patient") return { role, prompt: patientPrompt(s), firstMessage: patientFirstMessage(s), voiceId: voices[s.encounter.persona.voiceKey] };
      if (role === "attending") return { role, prompt: attendingPrompt(s), firstMessage: attendingFirstMessage(s), voiceId: "" };
      return "scored";
    },
    anyLive: (role) => [...sessions.values()].some((s) => roleOf(s) === role),
    // The scored encounter for office-to-operating-room carryover.
    get: (id: string) => (ENCOUNTER_ID.test(id) ? (sessions.get(id) ?? null) : null),
  };
}
