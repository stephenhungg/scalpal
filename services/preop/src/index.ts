import { createApp } from "./app.js";
import { createFinchNodeClient } from "./finchnode.js";
import { realtimeFromEnv } from "./realtime-bridge.js";

// Optional: the shared SpacetimeDB session (SPACETIMEDB_URI). Without it the HTTP coach API works alone.
const realtime = realtimeFromEnv(process.env);
realtime?.start();

// Vercel and other Hono hosts pick up the default export.
export default createApp({
  realtime,
  client: createFinchNodeClient({
    baseUrl: process.env.FINCHNODE_BASE_URL || undefined,
    // Optional ck_test_ sandbox key: enables real Connect admissions. Without it, demo records only.
    apiKey: process.env.FINCHNODE_API_KEY || undefined,
  }),
  // Comma-separated browser origins allowed besides localhost and same-origin pages, e.g. a deployed companion.
  corsOrigins: (process.env.PREOP_CORS_ORIGINS ?? "").split(",").map((o) => o.trim()).filter(Boolean),
  elevenLabs: { apiKey: process.env.ELEVENLABS_API_KEY ?? "", agentId: process.env.ELEVENLABS_AGENT_ID ?? "", voiceId: process.env.JARVIS_VOICE_ID ?? "", patientAgentId: process.env.PATIENT_AGENT_ID ?? "" },
});
