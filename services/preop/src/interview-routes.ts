import type { Context, Hono } from "hono";
import { ENCOUNTERS_BY_PLAN, type Encounter } from "./catalog/encounters.js";
import { DEFAULT_PATIENT_VOICES } from "./encounter.js";
import { letterFrom, wordMatch, type AnswerClassifier, type SpeechToText } from "./answer-classifier.js";
import { loadPatientContent } from "./interview-content.js";
import { interviewPatientPrompt } from "./interview-prompt.js";
import { InterviewSession } from "./interview.js";
import type { ChoiceKey } from "./interview-types.js";
import { NO_REALTIME, type RealtimeSink } from "./realtime-bridge.js";
import type { Action, SurgicalCase } from "./types.js";

// The pre-op office: a 1:1 interview with the patient voice, driven by committed rounds of four
// clinician moves. Jarvis takes no part here. The scored interview carries into the operating room through
// POST /coach/sessions {encounterId: <interviewId>}.

export interface InterviewRouteOptions {
  loadCase: (patientId: string) => Promise<SurgicalCase | null>;
  planSubjectFor: (kase: SurgicalCase) => Promise<string>;
  now: () => Date;
  classifier?: AnswerClassifier | null;
  speechToText?: SpeechToText | null;
  elevenLabs?: { apiKey: string; patientAgentId?: string; interviewAgentId?: string };
  patientVoices?: Partial<Record<Encounter["persona"]["voiceKey"], string>>;
  realtime?: RealtimeSink;
  contentRoot?: string; // tests
}

const INTERVIEW_ID = /^int-[a-z0-9]{6,40}$/;
const MAX = 50;
const MAX_AUDIO_CHARS = 4_000_000;

const actionsFor = (id: string): Action[] => [
  { id: "interview_state", label: "Interview state", method: "GET", route: `/interviews/${id}` },
  { id: "choose_patient", label: "Choose another patient", method: "GET", route: "/patients" },
];

export function registerInterviewRoutes(app: Hono, options: InterviewRouteOptions): { get(id: string): InterviewSession | null } {
  const sessions = new Map<string, InterviewSession>();
  const voices = { ...DEFAULT_PATIENT_VOICES, ...(options.patientVoices ?? {}) };
  const realtime = options.realtime ?? NO_REALTIME;
  const meta = new Map<string, { prompt: string; firstMessage: string; voiceId: string }>();

  const bad = (c: Context, status: 400 | 404 | 409 | 422 | 503, code: string, message: string, actions: Action[], extra: object = {}) =>
    c.json({ error: { code, message }, ...extra, actions }, status);
  const home: Action[] = [{ id: "choose_patient", label: "Choose a patient", method: "GET", route: "/patients" }];
  const get = (c: Context) => {
    const id = c.req.param("id") ?? "";
    return INTERVIEW_ID.test(id) ? (sessions.get(id) ?? null) : null;
  };
  const missing = (c: Context) => bad(c, 404, "interview_not_found", "No live interview with that id. Start one from a patient.", home);
  const body = async (c: Context): Promise<Record<string, unknown>> => {
    const v: unknown = await c.req.json().catch(() => ({}));
    return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : {};
  };

  app.post("/interviews", async (c) => {
    const { patientId } = await body(c);
    const kase = typeof patientId === "string" ? await options.loadCase(patientId) : null;
    if (!kase) return bad(c, 404, "patient_not_found", 'Send {"patientId": "<FinchNode subject>"} for a known patient.', home);
    if (!kase.procedureId) return bad(c, 409, "case_unavailable", kase.statusReason, kase.actions);
    const planSubject = await options.planSubjectFor(kase);
    const content = loadPatientContent(kase.patientId, options.contentRoot) ?? loadPatientContent(planSubject, options.contentRoot);
    if (!content?.interview || !content.patientMd.trim()) return bad(c, 404, "no_interview", "This patient has no authored interview; go straight to surgery.", home);
    const persona = ENCOUNTERS_BY_PLAN.get(planSubject)?.persona;
    const patientName = persona?.patientName ?? kase.patient.name ?? kase.patient.displayLabel;
    if (sessions.size >= MAX) {
      const oldest = sessions.keys().next().value!;
      sessions.delete(oldest);
      meta.delete(oldest);
    }
    const id = `int-${crypto.randomUUID().replaceAll("-", "").slice(0, 16)}`;
    // The OR always loads the case's surgery; the interview's procedure is the same authored answer.
    const s = new InterviewSession(id, kase, content.interview, patientName, options.now);
    sessions.set(id, s);
    meta.set(id, { prompt: interviewPatientPrompt(content, kase), firstMessage: content.interview.openingLine, voiceId: voices[persona?.voiceKey ?? "adult_female"] ?? "" });
    realtime.attachInterview?.(s);
    // Demographics seat the right avatars in the Quest office: the patient, and for a parent speaker the parent beside them.
    const speaker = persona?.speaker ?? "patient";
    const patientAge = persona?.age ?? kase.patient.age ?? 0;
    const patientSex = persona?.sex ?? kase.patient.sex ?? "";
    const demographics = {
      patientAge,
      patientSex,
      speakerAge: speaker === "parent" ? (persona?.speakerAge ?? 0) : patientAge,
      speakerSex: speaker === "parent" ? (persona?.speakerSex ?? "") : patientSex,
    };
    return c.json({ ...s.state(), speakerName: persona?.name ?? patientName, speaker, ...demographics, openingLine: content.interview.openingLine, actions: actionsFor(id) }, 201);
  });

  app.get("/interviews/:id", (c) => {
    const s = get(c);
    return s ? c.json({ ...s.state(), actions: actionsFor(s.id) }) : missing(c);
  });

  // Voice binding for the patient: prompt, first line and voice are server-built; the client never authors them.
  app.get("/interviews/:id/connection", async (c) => {
    const s = get(c);
    if (!s) return missing(c);
    const m = meta.get(s.id)!;
    const el = options.elevenLabs;
    const agentId = el?.interviewAgentId || el?.patientAgentId || "";
    if (!el || !agentId) return bad(c, 503, "voice_unconfigured", "Set INTERVIEW_PATIENT_AGENT_ID (or PATIENT_AGENT_ID) and run npm run jarvis:setup.", actionsFor(s.id));
    const binding = { role: "patient", agentId, prompt: m.prompt, firstMessage: m.firstMessage, voiceId: m.voiceId };
    if (!el.apiKey) return c.json({ mode: "public", signedUrl: "", ...binding, actions: actionsFor(s.id) });
    const res = await fetch(`https://api.elevenlabs.io/v1/convai/conversation/get-signed-url?agent_id=${encodeURIComponent(agentId)}`, { headers: { "xi-api-key": el.apiKey } }).catch(() => null);
    if (!res?.ok) return bad(c, 503, "elevenlabs_unreachable", `ElevenLabs signed URL request failed${res ? ` (${res.status})` : ""}.`, actionsFor(s.id));
    const { signed_url } = (await res.json()) as { signed_url: string };
    return c.json({ mode: "signed", signedUrl: signed_url, ...binding, actions: actionsFor(s.id) });
  });

  // One pick per round: {key} for a tap, {text} for speech already transcribed, {audio, mimeType} for raw
  // speech (base64), which is transcribed and matched to a choice. Unclear speech asks again.
  app.post("/interviews/:id/answer", async (c) => {
    const s = get(c);
    if (!s) return missing(c);
    const round = s.current();
    if (!round) return bad(c, 409, "invalid_phase", "The interview is already scored.", actionsFor(s.id));
    const b = await body(c);
    let key: ChoiceKey | null = typeof b.key === "string" && /^[ABCD]$/.test(b.key) ? (b.key as ChoiceKey) : null;
    let heard = typeof b.text === "string" ? b.text.trim().slice(0, 1000) : "";
    const via = key ? "tap" : "voice";
    if (!key && typeof b.audio === "string") {
      if (!options.speechToText) return bad(c, 503, "speech_unconfigured", "Speech answers need ELEVENLABS_API_KEY; tap a choice instead.", actionsFor(s.id));
      if (b.audio.length > MAX_AUDIO_CHARS) return bad(c, 400, "audio_too_large", "Keep spoken answers under about 20 seconds.", actionsFor(s.id));
      try {
        heard = (await options.speechToText.transcribe(Buffer.from(b.audio, "base64"), typeof b.mimeType === "string" ? b.mimeType : "audio/webm")).trim();
      } catch (e) {
        return bad(c, 503, "speech_failed", `Could not transcribe that (${(e as Error).message}). Tap a choice or try again.`, actionsFor(s.id));
      }
    }
    // A bare letter ("B", "option c") needs no model; anything else goes to the classifier.
    if (!key && heard) key = letterFrom(heard);
    // The model when configured; shared content words otherwise (or when the model is unreachable or unsure).
    if (!key && heard) key = (options.classifier ? await options.classifier.classify(heard, round.choices) : null) ?? wordMatch(heard, round.choices);
    if (!key) return bad(c, 422, "unclear_answer", heard ? `Heard "${heard}", which did not match one choice. Say A, B, C or D, or tap one.` : "Send key, text or audio.", actionsFor(s.id), { heard });
    if (heard) s.transcript("learner", heard);
    const out = s.pick(key, via, heard)!;
    if (out.scorecard) realtime.interviewResult?.(s, out.scorecard);
    return c.json({ heard, ...out, state: s.state(), actions: actionsFor(s.id) });
  });

  app.get("/interviews/:id/score", (c) => {
    const s = get(c);
    if (!s) return missing(c);
    if (s.phase !== "scored") return bad(c, 409, "invalid_phase", `Answer all ${s.interview.rounds.length} rounds first.`, actionsFor(s.id));
    return c.json({ scorecard: s.score(), actions: actionsFor(s.id) });
  });

  // The client mirrors what the patient voice said, for the companion and the recap.
  app.post("/interviews/:id/transcript", async (c) => {
    const s = get(c);
    if (!s) return missing(c);
    const { speaker, text } = await body(c);
    if ((speaker !== "patient" && speaker !== "learner") || typeof text !== "string" || !text.trim()) return bad(c, 400, "invalid_transcript", 'Send {"speaker": "patient" | "learner", "text": "..."}.', actionsFor(s.id));
    s.transcript(speaker, text.slice(0, 2000));
    return c.json({ ok: true });
  });

  return { get: (id) => (INTERVIEW_ID.test(id) ? (sessions.get(id) ?? null) : null) };
}
