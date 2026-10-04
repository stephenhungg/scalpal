// Scalpal vitals service. Publishes the volunteer's breathing and pulse (Presage) to the headset,
// the coach and the companion:
//   GET /vitals         latest snapshot (JSON)
//   GET /vitals/stream  server-sent events, ~10 Hz
//   GET /health
// Values are only reported as numbers when Presage marks them stable; otherwise they are null
// with a "measuring" reason. Demo mode is labelled in every payload.
import { createServer } from "node:http";
import { startDemo } from "./demo.mjs";

const PORT = Number(process.env.PORT || 8790);
const key = process.env.PRESAGE_API_KEY?.trim();
const demo = process.env.PRESAGE_DEMO === "1" || !key;

let snapshot = {
  schema: "scalpal.vitals/0",
  mode: demo ? "demo" : "live",
  label: demo ? "DEMO · synthetic vitals" : "Volunteer · real vitals · Presage",
  status: { ok: false, reason: "Starting", code: null },
  pulse: null,
  breathing: null,
  traces: { pulse: [], breathing: [] },
  updatedAt: null,
};

const reading = (r) => (r && r.stable ? { bpm: Math.round(r.bpm * 10) / 10, confidence: r.confidence } : null);

function onUpdate(s) {
  snapshot = {
    ...snapshot,
    status: s.status,
    pulse: reading(s.pulse),
    breathing: reading(s.breathing),
    measuring: { pulse: !s.pulse?.stable, breathing: !s.breathing?.stable },
    traces: { pulse: s.traces.pulse.slice(-120), breathing: s.traces.breathing.slice(-300) },
    updatedAt: Date.now(),
  };
}

if (demo) {
  if (!key) console.log("[vitals] no PRESAGE_API_KEY: running in labelled DEMO mode");
  startDemo({ onUpdate });
} else {
  const { startPresage } = await import("./presage.mjs");
  startPresage({ apiKey: key, cameraId: process.env.PRESAGE_CAMERA_ID?.trim() || undefined, onUpdate });
}

const cors = { "Access-Control-Allow-Origin": "*", "Cache-Control": "no-store" };
const clients = new Set();

createServer((req, res) => {
  const path = (req.url || "/").split("?")[0];
  if (path === "/health") {
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify({ ok: true, mode: snapshot.mode, status: snapshot.status }));
  }
  if (path === "/vitals") {
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify(snapshot));
  }
  if (path === "/vitals/stream") {
    res.writeHead(200, { ...cors, "Content-Type": "text/event-stream", Connection: "keep-alive" });
    clients.add(res);
    req.on("close", () => clients.delete(res));
    return;
  }
  res.writeHead(404, cors);
  res.end();
}).listen(PORT, () => console.log(`[vitals] ${snapshot.mode} mode on http://localhost:${PORT}/vitals`));

setInterval(() => {
  if (!clients.size) return;
  const msg = `data: ${JSON.stringify(snapshot)}\n\n`;
  for (const c of clients) c.write(msg);
}, 100);
