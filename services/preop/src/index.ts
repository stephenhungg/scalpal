import { createApp } from "./app.js";
import { createFinchNodeClient } from "./finchnode.js";
import { realtimeFromEnv } from "./realtime-bridge.js";
import { sceneVisionFromEnv } from "./scene-vision.js";

// Optional: the shared SpacetimeDB session (SPACETIMEDB_URI). Without it the HTTP coach API works alone.
const realtime = realtimeFromEnv(process.env);
realtime?.start();

// Vercel and other Hono hosts pick up the default export.
export default createApp({
  realtime,
  // Jarvis's eyes (ANTHROPIC_API_KEY): the look_at_scene tool and the background scene watcher.
  vision: sceneVisionFromEnv(process.env),
  watchMs: Number(process.env.JARVIS_WATCH_MS ?? 4000),
  client: createFinchNodeClient({
    baseUrl: process.env.FINCHNODE_BASE_URL || undefined,
    // Optional ck_test_ sandbox key: enables real Connect admissions. Without it, demo records only.
    apiKey: process.env.FINCHNODE_API_KEY || undefined,
  }),
  elevenLabs: { apiKey: process.env.ELEVENLABS_API_KEY ?? "", agentId: process.env.ELEVENLABS_AGENT_ID ?? "", voiceId: process.env.JARVIS_VOICE_ID ?? "", patientAgentId: process.env.PATIENT_AGENT_ID ?? "" },
});
