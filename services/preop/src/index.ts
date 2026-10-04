import { createApp } from "./app.js";
import { createFinchNodeClient } from "./finchnode.js";
import { realtimeFromEnv } from "./realtime-bridge.js";
import { frameDetectorFromEnv } from "./frame-detector.js";
import { sceneVisionFromEnv } from "./scene-vision.js";
import { ClaudeAnswerClassifier, ElevenLabsSpeechToText } from "./answer-classifier.js";

// Optional: the shared SpacetimeDB session (SPACETIMEDB_URI). Without it the HTTP coach API works alone.
const realtime = realtimeFromEnv(process.env);
realtime?.start();

// Vercel and other Hono hosts pick up the default export.
export default createApp({
  realtime,
  // Jarvis's eyes (ANTHROPIC_API_KEY): the look_at_scene tool and the background scene watcher.
  vision: sceneVisionFromEnv(process.env),
  watchMs: Number(process.env.JARVIS_WATCH_MS ?? 4000),
  // Real-camera instrument and hand boxes from services/vision (VISION_DETECT_URL, e.g. http://127.0.0.1:8791).
  detector: frameDetectorFromEnv(process.env),
  // Office interview: spoken answers are transcribed (ElevenLabs) and matched to a choice (Claude Haiku).
  speechToText: process.env.ELEVENLABS_API_KEY ? new ElevenLabsSpeechToText(process.env.ELEVENLABS_API_KEY) : null,
  answerClassifier: process.env.ANTHROPIC_API_KEY ? new ClaudeAnswerClassifier() : null,
  client: createFinchNodeClient({
    baseUrl: process.env.FINCHNODE_BASE_URL || undefined,
    // Optional ck_test_ sandbox key: enables real Connect admissions. Without it, demo records only.
    apiKey: process.env.FINCHNODE_API_KEY || undefined,
  }),
  // Comma-separated browser origins allowed besides localhost and same-origin pages, e.g. a deployed companion.
  corsOrigins: (process.env.PREOP_CORS_ORIGINS ?? "").split(",").map((o) => o.trim()).filter(Boolean),
  elevenLabs: { apiKey: process.env.ELEVENLABS_API_KEY ?? "", agentId: process.env.ELEVENLABS_AGENT_ID ?? "", voiceId: process.env.JARVIS_VOICE_ID ?? "", patientAgentId: process.env.PATIENT_AGENT_ID ?? "", interviewAgentId: process.env.INTERVIEW_PATIENT_AGENT_ID ?? "" },
});
