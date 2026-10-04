// Scalpal vitals service. Publishes the volunteer's breathing and pulse (Presage) to the headset,
// the coach and the companion:
//   GET /vitals         latest snapshot (JSON)
//   GET /vitals/stream  server-sent events, ~10 Hz
//   GET /baseline                 current Time-Out baseline (measured, demo, or authored)
//   POST /baseline/capture        freeze the baseline from the last ~12 s of confident readings
//   GET /monitor?bloodLostMl=&bleedMlPerMin=&weightKg=&critical=1
//                                 OR monitor values: baseline + simulated hemorrhage delta
//   GET /preview.jpg              one frame from the configured camera, to check framing (no quota;
//                                 unavailable while a live session holds the camera)
//   GET /health
// Values are only reported as numbers when Presage marks them stable; otherwise they are null
// with a "measuring" reason. Demo mode is labelled in every payload.
import { createServer } from "node:http";
import { startDemo } from "./demo.mjs";
import { grabFrame } from "./preview.mjs";
import { budget } from "./quota.mjs";
import { AUTHORED_BASELINE, baselineFrom, display, monitorVitals } from "./physiology.mjs";

const PORT = Number(process.env.PORT || 8791);
const key = process.env.PRESAGE_API_KEY?.trim();
// Quota guard: the key has a fixed budget of live measurement minutes, so live mode needs an
// explicit PRESAGE_LIVE=1, stops itself after PRESAGE_LIVE_MINUTES, and is refused once the
// logged usage reaches the budget. Everything else runs in labelled demo mode (no quota).
const LIVE_MINUTES = Number(process.env.PRESAGE_LIVE_MINUTES || 5);
// A live session with no face in view for this long ends itself, so a bad setup doesn't burn quota.
const NO_FACE_SECONDS = Number(process.env.PRESAGE_NO_FACE_SECONDS || 15);
// A reading that drops below Presage's stable threshold keeps showing (marked held, with its age)
// for this long before falling back to "measuring". Stops brief dips from flickering the display.
const HOLD_MS = Number(process.env.PRESAGE_HOLD_SECONDS ?? 10) * 1000;
const wantLive = process.env.PRESAGE_LIVE === "1";
const quota = budget(Number(process.env.PRESAGE_BUDGET_MINUTES || 50));
let demo = true;
if (wantLive && !key) console.log("[vitals] PRESAGE_LIVE=1 but no PRESAGE_API_KEY: staying in DEMO mode");
else if (wantLive && quota.remainingMinutes() <= 0.25) console.log(`[vitals] live budget used up (${quota.usedMinutes().toFixed(1)} min): staying in DEMO mode`);
else if (wantLive) demo = false;
// Test hook: run the live path (guard, auto-stop, ledger) with the demo source instead of Presage.
// "1" streams demo vitals, "noface" reports no face, "fail" can't open the camera.
const fake = demo ? undefined : process.env.PRESAGE_FAKE_LIVE || undefined;
const fakeLive = Boolean(fake);
let liveRunning = false;
let sourceStopped = false; // ignore late updates once a live session has ended

let snapshot = {
  schema: "scalpal.vitals/0",
  mode: demo ? "demo" : "live",
  label: demo ? "DEMO · synthetic vitals" : fakeLive ? "TEST · fake live source" : "Volunteer · real vitals · Presage",
  status: { ok: false, reason: "Starting", code: null },
  pulse: null,
  breathing: null,
  traces: { pulse: [], breathing: [] },
  updatedAt: null,
};

const round1 = (v) => Math.round(v * 10) / 10;
const lastStable = { pulse: null, breathing: null };
function reading(key, r, now) {
  if (r?.stable) {
    lastStable[key] = { bpm: r.bpm, confidence: r.confidence, at: now };
    return { bpm: round1(r.bpm), confidence: r.confidence };
  }
  const h = lastStable[key];
  if (h && now - h.at <= HOLD_MS) return { bpm: round1(h.bpm), confidence: h.confidence, heldMs: now - h.at };
  return null;
}

// last ~12 s of stable readings, for the Time-Out baseline
const recent = [];
let baseline = { ...AUTHORED_BASELINE };

function onUpdate(s) {
  if (sourceStopped) return;
  const now = Date.now();
  if (s.pulse?.stable || s.breathing?.stable) {
    recent.push({ at: now, hr: s.pulse?.stable ? s.pulse.bpm : null, rr: s.breathing?.stable ? s.breathing.bpm : null });
    while (recent.length && now - recent[0].at > 12_000) recent.shift();
  }
  snapshot = {
    ...snapshot,
    status: s.status,
    pulse: reading("pulse", s.pulse, now),
    breathing: reading("breathing", s.breathing, now),
    measuring: { pulse: !s.pulse?.stable, breathing: !s.breathing?.stable },
    traces: { pulse: s.traces.pulse.slice(-120), breathing: s.traces.breathing.slice(-300) },
    updatedAt: now,
  };
}

if (demo) {
  console.log("[vitals] labelled DEMO mode (no quota used). Set PRESAGE_LIVE=1 for real measurement.");
  startDemo({ onUpdate });
} else {
  const { startPresage } = fakeLive ? { startPresage: (o) => startDemo({ ...o, fake }) } : await import("./presage.mjs");
  const minutes = Math.min(LIVE_MINUTES, quota.remainingMinutes());
  console.log(`[vitals] LIVE: ${quota.usedMinutes().toFixed(1)} of ${quota.totalMinutes} min used; this session stops after ${minutes.toFixed(1)} min`);
  const session = quota.begin();
  let live = null;
  let ended = false;
  let timer;
  let faceCheck;
  const end = (why) => {
    if (ended) return;
    ended = true;
    liveRunning = false;
    sourceStopped = true;
    clearTimeout(timer);
    clearInterval(faceCheck);
    live?.stop();
    const used = session.end();
    console.log(`[vitals] live session ended (${why}): ${used.toFixed(1)} min this session, ${quota.usedMinutes().toFixed(1)} of ${quota.totalMinutes} min used`);
    lastStable.pulse = lastStable.breathing = null;
    snapshot = { ...snapshot, status: { ok: false, code: null, reason: `Live session ended (${why})` }, pulse: null, breathing: null };
  };
  try {
    live = startPresage({ apiKey: key, cameraId: process.env.PRESAGE_CAMERA_ID?.trim() || undefined, onUpdate, onFatal: (msg) => end(`error: ${msg}`) });
    liveRunning = !ended;
  } catch (err) {
    console.log(`[vitals] could not start Presage: ${err.message}`);
    end(`could not start: ${err.message}`);
  }
  if (ended) live?.stop();
  else {
    timer = setTimeout(() => end("time limit"), minutes * 60_000);
    let noFaceSince = null;
    faceCheck = setInterval(() => {
      if (!snapshot.status?.noFace) return void (noFaceSince = null);
      noFaceSince ??= Date.now();
      if (Date.now() - noFaceSince >= NO_FACE_SECONDS * 1000) end(`no face for ${NO_FACE_SECONDS} s`);
    }, 250);
  }
  for (const sig of ["SIGINT", "SIGTERM"]) {
    process.once(sig, () => {
      end("stopped");
      process.exit(0);
    });
  }
}

const cors = { "Access-Control-Allow-Origin": "*", "Cache-Control": "no-store" };
const clients = new Set();

createServer((req, res) => {
  const path = (req.url || "/").split("?")[0];
  if (path === "/health") {
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify({ ok: true, mode: snapshot.mode, status: snapshot.status }));
  }
  if (path === "/baseline" && req.method === "GET") {
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify(baseline));
  }
  if (path === "/baseline/capture" && req.method === "POST") {
    baseline = baselineFrom(recent, { mode: snapshot.mode });
    console.log(`[vitals] baseline captured: HR ${baseline.hr}, RR ${baseline.rr} (${baseline.source})`);
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify(baseline));
  }
  if (path === "/monitor") {
    const q = new URL(req.url, "http://x").searchParams;
    const n = (k, d) => (q.has(k) ? Number(q.get(k)) : d);
    const v = display(
      monitorVitals({
        baseline,
        weightKg: n("weightKg", 70),
        bloodLostMl: n("bloodLostMl", 0),
        bleedMlPerMin: n("bleedMlPerMin", 0),
        criticalInjury: q.get("critical") === "1",
      }),
    );
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify({ ...v, simulated: true, mode: snapshot.mode }));
  }
  if (path === "/preview.jpg") {
    if (liveRunning) {
      res.writeHead(409, { ...cors, "Content-Type": "application/json" });
      return res.end(JSON.stringify({ error: "The live session is using the camera" }));
    }
    grabFrame(process.env.PRESAGE_CAMERA_ID?.trim()).then(
      (jpg) => {
        res.writeHead(200, { ...cors, "Content-Type": "image/jpeg" });
        res.end(jpg);
      },
      (err) => {
        res.writeHead(503, { ...cors, "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: err.message }));
      },
    );
    return;
  }
  if (path === "/vitals") {
    res.writeHead(200, { ...cors, "Content-Type": "application/json" });
    return res.end(JSON.stringify({ ...snapshot, cameraBusy: liveRunning }));
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
  const msg = `data: ${JSON.stringify({ ...snapshot, cameraBusy: liveRunning })}\n\n`;
  for (const c of clients) c.write(msg);
}, 100);
