import "../src/env.js";
import { EXAM_MANEUVERS, HISTORY_TOPICS, TESTS } from "../src/catalog/encounters.js";

// Creates or updates the Jarvis ElevenLabs agent and its client tools from code, so the agent config is
// reproducible. The per-case system prompt and first message are sent by the /jarvis page at session
// start (overrides), so this only sets the base agent, LLM, voice, and tool definitions.
//
//   ELEVENLABS_API_KEY=... npm run jarvis:setup        # creates the agent, prints ELEVENLABS_AGENT_ID
//   ELEVENLABS_AGENT_ID=... npm run jarvis:setup       # updates that agent in place

const API = "https://api.elevenlabs.io/v1/convai";
const key = process.env.ELEVENLABS_API_KEY;
if (!key) {
  console.error("ELEVENLABS_API_KEY is not set. Put it in services/preop/.env (see .env.example).");
  process.exit(1);
}

const LLM = process.env.JARVIS_LLM || "claude-sonnet-5-5";
const VOICE = process.env.JARVIS_VOICE_ID || "";
const REASONING = process.env.JARVIS_REASONING || "";

const str = (description: string) => ({ type: "string", description });

const TOOLS = [
  {
    name: "get_surgery_state",
    description: "Fresh live state of the surgery: current step, progress, what is left, danger structures, last event, and how stuck the learner is.",
    parameters: { type: "object", properties: {}, required: [] },
  },
  {
    name: "get_hint",
    description: "The next hint tier for the current step (1 why, 2 where to look, 3 the explicit move). Use when the learner asks for help or is stuck.",
    parameters: { type: "object", properties: {}, required: [] },
  },
  {
    name: "explain_structure",
    description: "Anatomy facts for one structure in this case: what it is, where to find it, blood supply, and why it matters surgically.",
    parameters: { type: "object", properties: { structure: str("Structure name as spoken, for example 'cystic artery' or 'CBD'.") }, required: ["structure"] },
  },
  {
    name: "highlight_structure",
    description: "Ask the headset to highlight a structure in this case. Returns whether the headset applied it.",
    parameters: { type: "object", properties: { structure: str("Structure name as spoken.") }, required: ["structure"] },
  },
  {
    name: "get_patient_brief",
    description: "The patient's chart risks, data gaps, and the pre-op checklist option types.",
    parameters: { type: "object", properties: {}, required: [] },
  },
  {
    name: "check_preop",
    description: "Score the learner's spoken pre-op safety check. Pass the checklist option types they named.",
    parameters: {
      type: "object",
      properties: { selected: { type: "array", items: str("A checklist option type, for example 'bleeding'."), description: "Option types the learner identified." } },
      required: ["selected"],
    },
  },
];

// Jarvis's eyes: the latest point-of-view frame, described by a vision model with the scene's labels.
TOOLS.push({
  name: "look_at_scene",
  description: "Look at the learner's current point of view (camera frame plus labeled objects) and describe what is there and how to approach it. Use for 'what am I looking at', 'where is it', or 'how do I approach this'.",
  parameters: { type: "object", properties: { question: str("The learner's question, in their words.") }, required: [] },
} as (typeof TOOLS)[number]);

// Jarvis as attending during the case presentation, after the patient interview.
const ATTENDING_TOOLS = [
  {
    name: "get_encounter_summary",
    description: "What the trainee actually asked, examined, and ordered in the patient interview, with findings and results. The only source of truth about the interview.",
    parameters: { type: "object", properties: {}, required: [] },
  },
  {
    name: "record_assessment",
    description: "Record the trainee's final assessment once they have given a diagnosis, differential, procedure, and timing. Returns their score and feedback to deliver.",
    parameters: {
      type: "object",
      properties: {
        diagnosis: str("The trainee's diagnosis, in their words."),
        differential: { type: "array", items: str("One alternative diagnosis they named."), description: "Alternatives the trainee named." },
        procedure: str("The procedure they proposed."),
        urgency: str("How soon they said to operate, in their words."),
      },
      required: ["diagnosis", "differential", "procedure", "urgency"],
    },
  },
];

// The patient (or parent) in the 1-on-1 interview. Facts come only from these tools.
const enumParam = (values: readonly string[], description: string) => ({ type: "string", enum: [...values], description });
const PATIENT_TOOLS = [
  {
    name: "answer",
    description: "Look up one fact about yourself before saying it: symptoms, timeline, history, medications, allergies, food, periods, life. Call once per topic.",
    parameters: { type: "object", properties: { topic: enumParam(HISTORY_TOPICS, "The topic of the clinician's question.") }, required: ["topic"] },
  },
  {
    name: "examine",
    description: "The clinician is physically examining you. Returns how you react. Never describe clinical findings.",
    parameters: { type: "object", properties: { maneuver: enumParam(EXAM_MANEUVERS, "The exam being performed.") }, required: ["maneuver"] },
  },
  {
    name: "order_test",
    description: "The clinician ordered a test. Never state results.",
    parameters: { type: "object", properties: { test: enumParam(TESTS, "The test ordered.") }, required: ["test"] },
  },
];

const PATIENT_BASE_PROMPT = "You are a patient in a surgical teaching simulation. Your character and instructions are supplied when the session starts.";

const BASE_PROMPT =
  "You are Jarvis, a real-time surgical coach in the Scalpal mixed-reality simulator. The full case prompt is supplied when each session starts. Keep replies to one or two spoken sentences.";

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${API}${path}`, {
    method,
    headers: { "xi-api-key": key!, "Content-Type": "application/json" },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}: ${text.slice(0, 500)}`);
  return (text ? JSON.parse(text) : {}) as T;
}

interface ToolDef {
  name: string;
  description: string;
  parameters: object;
}

async function upsertTools(list: ToolDef[]): Promise<string[]> {
  const existing = await call<{ tools?: { id: string; tool_config: { name: string } }[] }>("GET", "/tools");
  const ids: string[] = [];
  for (const t of list) {
    const tool_config = { type: "client", name: t.name, description: t.description, parameters: t.parameters, expects_response: true, response_timeout_secs: 10 };
    const found = existing.tools?.find((x) => x.tool_config?.name === t.name);
    if (found) {
      await call("PATCH", `/tools/${found.id}`, { tool_config });
      ids.push(found.id);
      console.log(`updated tool ${t.name} (${found.id})`);
    } else {
      const created = await call<{ id: string }>("POST", "/tools", { tool_config });
      ids.push(created.id);
      console.log(`created tool ${t.name} (${created.id})`);
    }
  }
  return ids;
}

async function upsertAgent(envName: string, agent: object, label: string) {
  const agentId = process.env[envName];
  if (agentId) {
    await call("PATCH", `/agents/${agentId}`, agent);
    console.log(`updated ${label} ${agentId} (llm ${LLM})`);
  } else {
    const created = await call<{ agent_id: string }>("POST", "/agents/create", agent);
    console.log(`created ${label} (llm ${LLM}). Add this to services/preop/.env:\n${envName}=${created.agent_id}`);
  }
}

async function main() {
  const toolIds = await upsertTools([...TOOLS, ...ATTENDING_TOOLS]);
  const agent = {
    name: "Scalpal Jarvis",
    tags: ["scalpal"],
    conversation_config: {
      agent: {
        first_message: "Jarvis here.",
        language: "en",
        prompt: { prompt: BASE_PROMPT, llm: LLM, temperature: 0.3, tool_ids: toolIds, ...(REASONING ? { reasoning_effort: REASONING } : {}) },
      },
      ...(VOICE ? { tts: { voice_id: VOICE } } : {}),
    },
    platform_settings: {
      overrides: { conversation_config_override: { agent: { prompt: { prompt: true }, first_message: true } } },
    },
  };

  await upsertAgent("ELEVENLABS_AGENT_ID", agent, "Jarvis agent");

  // The patient agent: its voice changes per patient, so the TTS voice is overridable per session.
  const patientToolIds = await upsertTools(PATIENT_TOOLS);
  await upsertAgent(
    "PATIENT_AGENT_ID",
    {
      name: "Scalpal Patient",
      tags: ["scalpal"],
      conversation_config: {
        agent: { first_message: "Hi.", language: "en", prompt: { prompt: PATIENT_BASE_PROMPT, llm: LLM, temperature: 0.6, tool_ids: patientToolIds } },
      },
      platform_settings: {
        overrides: { conversation_config_override: { agent: { prompt: { prompt: true }, first_message: true }, tts: { voice_id: true } } },
      },
    },
    "patient agent",
  );

  // The choice-based office interview: the patient voice only replies to the learner's picks, so it has
  // no tools. Character and the opening line come per session from the committed patient.md.
  await upsertAgent(
    "INTERVIEW_PATIENT_AGENT_ID",
    {
      name: "Scalpal Patient (interview)",
      tags: ["scalpal"],
      conversation_config: {
        agent: { first_message: "Hi.", language: "en", prompt: { prompt: PATIENT_BASE_PROMPT, llm: LLM, temperature: 0.6, tool_ids: [] } },
        turn: { turn_timeout: 30 },
      },
      platform_settings: {
        overrides: { conversation_config_override: { agent: { prompt: { prompt: true }, first_message: true }, tts: { voice_id: true } } },
      },
    },
    "interview patient agent",
  );
}

main().catch((err) => {
  console.error(err instanceof Error ? err.message : err);
  process.exit(1);
});
