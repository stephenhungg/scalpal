// Jarvis voice page. State arrives from the coach over SSE; the arbiter decides what is spoken and when.
// Warnings play a pre-rendered clip in Jarvis's voice (no LLM in the loop); cautions become queued LLM
// turns; the agent's background context is updated only when something meaningful changes.
import { Conversation } from "https://esm.sh/@elevenlabs/client@1.26.0";
import { createArbiter, percentile, semanticKey } from "/jarvis/arbiter.js";
import { cleanTranscript, createEncounterFlow } from "/jarvis/encounter.js";

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const api = async (method, path, body) => {
  const res = await fetch(path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  const json = await res.json().catch(() => ({}));
  return { ok: res.ok, status: res.status, json };
};

let sid = "", patientId = "", kase = null, convo = null, feed = null, lastTyped = "";
let encounterConvo = null; // the patient or attending conversation before surgery
let snapshot = null, currentContext = "", currentContextKey = "", lastSemantic = "", contextTimer = 0;
let arbiter = createArbiter();
const reflexClips = new Map(); // reflexKey -> HTMLAudioElement
const traces = []; // one per delivered alert, for the latency panel
let pendingTrace = null; // the LLM turn we are waiting to hear

function log(kind, text, extra = "") {
  const el = document.createElement("div");
  el.className = `msg ${kind} ${extra}`;
  el.textContent = text;
  $("log").append(el);
  $("log").scrollTop = $("log").scrollHeight;
}

async function loadPatients() {
  const { json } = await api("GET", "/patients");
  const list = (json.patients ?? []).filter((p) => p.patientId && p.procedureId);
  $("patient").innerHTML = list.map((p) => `<option value="${esc(p.patientId)}">${esc(p.displayLabel)}: ${esc(p.procedureTitle)} (${esc(p.urgency)})</option>`).join("");
  $("start").disabled = !list.length;
}

function render(snap) {
  const s = snap.step;
  $("caseline").textContent = `${snap.patientLabel} · ${snap.procedureTitle}`;
  $("stepno").textContent = snap.status === "completed" ? "Complete" : `Step ${snap.stepNumber} of ${snap.stepCount}${snap.status === "paused" ? " · PAUSED (tracking lost)" : ""}`;
  $("bar").style.width = `${Math.round((snap.completedCount / snap.stepCount) * 100)}%`;
  $("steptitle").textContent = snap.status === "completed" ? "Procedure complete" : s.title;
  $("instruction").textContent = s.instruction;
  const rows = snap.status === "completed"
    ? [["Mistakes", `${snap.mistakeCount} (${snap.highSeverityMistakeCount} high)`], ["Hints", snap.hintsUsed], ["Time", `${snap.elapsedSeconds}s`]]
    : [
        ["Instrument", `${s.instrumentName}${s.ports.length ? ` via ${s.ports.join(" / ")}` : ""}`],
        ["Progress", s.progressText],
        ["Still needed", s.remaining.join(", ") || "nothing"],
        ["Danger", s.dangers.length ? `<span class="danger">${esc(s.dangers.map((d) => d.name).join(", "))}</span>` : "none flagged"],
        ["Looking at", snap.focusStructure.name || "n/a"],
        ["This patient", s.patientNotes.join(" ") || "no step-specific notes"],
        ["Last event", snap.lastEvent],
        ["Mistakes", `${snap.mistakeCount} total`],
      ];
  $("details").innerHTML = rows.map(([k, v]) => `<div>${esc(k)}</div><div>${k === "Danger" ? v : esc(v)}</div>`).join("");
  $("stucklabel").textContent = `coaching: ${snap.stuckLabel} · ${snap.secondsSinceProgress}s since progress · ${snap.offTargetAttempts} off-target`;
  document.querySelectorAll(".meter span").forEach((el, i) => (el.className = i < snap.stuckLevel ? `lit${snap.stuckLevel}` : ""));
  for (const b of document.querySelectorAll("#simbuttons button")) b.disabled = snap.status === "completed";
}

// Background context: only when a meaningful field changed, debounced, newest version wins.
function syncContext(force = false) {
  clearTimeout(contextTimer);
  contextTimer = setTimeout(() => {
    const key = currentContextKey || semanticKey(snapshot);
    if (!convo || (!force && key === lastSemantic)) return;
    lastSemantic = key;
    try { convo.sendContextualUpdate(currentContext); } catch (e) { console.warn(e); }
  }, force ? 0 : 300);
}

// ---- delivery -------------------------------------------------------------------------------------

function trace(alert, path) {
  const t = { id: alert.id, kind: alert.kind, tier: alert.tier, path, serverAt: Date.parse(alert.at), recvAt: alert._recvAt, sentAt: 0, firstTextAt: 0, speakingAt: 0, playAt: 0 };
  traces.push(t);
  return t;
}

function stateTag() {
  return snapshot ? `v${snapshot.version} step ${snapshot.stepNumber}/${snapshot.stepCount} "${snapshot.step.title}"` : "";
}

// Mirror what Jarvis and the learner actually said, and the voice status, into the shared session.
function mirrorTranscript(speaker, text) {
  if (sid && text) api("POST", `/coach/sessions/${sid}/transcript`, { speaker, text }).catch(() => {});
}
function mirrorStatus(status) {
  if (sid) api("POST", `/coach/sessions/${sid}/voice-status`, { status }).catch(() => {});
}

function speakViaLLM(alert, path) {
  if (!convo) {
    if ("speechSynthesis" in window) speechSynthesis.speak(new SpeechSynthesisUtterance(alert.say));
    return;
  }
  syncContext(true);
  const t = trace(alert, path);
  t.sentAt = performance.timeOrigin + performance.now();
  pendingTrace = t;
  convo.sendUserMessage(`[SIM EVENT ${stateTag()}] kind=${alert.kind} tier=${alert.tier}: ${alert.say}`);
  mirrorTranscript("system", `${alert.kind}: ${alert.say}`);
  renderLatency();
}

function playReflex(alert, { duck = true } = {}) {
  const clip = reflexClips.get(alert.reflexKey);
  if (!clip) return speakViaLLM(alert, "llm-warning");
  const t = trace(alert, alert.tier === "warning" ? "reflex" : "callout");
  arbiter.setReflexPlaying(true);
  if (convo && duck) convo.setVolume({ volume: 0 }); // a warning cuts off whatever the agent was saying
  clip.currentTime = 0;
  clip.onplaying = () => { t.playAt = performance.timeOrigin + performance.now(); renderLatency(); mirrorTranscript("coach", alert.reflexText || alert.say); };
  clip.onended = clip.onerror = () => {
    arbiter.setReflexPlaying(false);
    arbiter.deliveryDone();
    if (!convo) return;
    if (duck) convo.setVolume({ volume: 1 });
    convo.sendContextualUpdate(`[JARVIS SAID ${stateTag()}] "${alert.reflexText || alert.say}"`);
  };
  clip.play().catch(() => { arbiter.setReflexPlaying(false); if (convo) convo.setVolume({ volume: 1 }); speakViaLLM(alert, "llm-warning"); });
}

function deliver(alert) {
  alert._recvAt = performance.timeOrigin + performance.now();
  const { action, reason } = arbiter.offer(alert);
  log("event", `${alert.tier} ${alert.kind}${action === "queued" ? " (queued)" : action === "dropped" ? ` (dropped: ${reason})` : action === "silent" ? " (context only)" : ""}: ${alert.say}`, alert.tier === "warning" ? "urgent" : "");
  if (action === "reflex") playReflex(alert);
  else if (action === "speak_now") speakViaLLM(alert, "llm-warning");
  renderLatency();
}

setInterval(() => {
  const alert = arbiter.next();
  if (!alert) return;
  // Milestone callouts have a pre-rendered clip; everything else is a coached LLM turn.
  if (alert.reflexKey && reflexClips.has(alert.reflexKey)) playReflex(alert, { duck: false });
  else speakViaLLM(alert, "llm-caution");
}, 200);

function ms(v) { return Number.isFinite(v) ? `${Math.round(v)} ms` : "n/a"; }
function renderLatency() {
  const reflex = traces.filter((t) => (t.path === "reflex" || t.path === "callout") && t.playAt);
  const llm = traces.filter((t) => t.path.startsWith("llm") && t.sentAt);
  const col = (rows, f) => rows.map(f).filter((v) => v > 0 && Number.isFinite(v));
  const line = (label, vals) => `${label.padEnd(34)} p50 ${ms(percentile(vals, 50)).padEnd(9)} p95 ${ms(percentile(vals, 95)).padEnd(9)} n=${vals.length}`;
  $("latency").textContent = [
    line("server alert -> page (same Mac clock)", col(traces, (t) => t.recvAt - t.serverAt)),
    line("warning/callout -> clip playing", col(reflex, (t) => t.playAt - t.recvAt)),
    line("caution queue wait", col(llm, (t) => t.sentAt - t.recvAt)),
    line("LLM turn: sent -> first text", col(llm, (t) => t.firstTextAt - t.sentAt)),
    line("LLM turn: sent -> agent speaking", col(llm, (t) => t.speakingAt - t.sentAt)),
    "",
    `arbiter: ${Object.entries(arbiter.stats).map(([k, v]) => `${k} ${v}`).join(" · ")} · queue ${arbiter.queueSize}`,
    "Speaking time is when audio starts in the browser; measure room audio for true voice-to-ear latency.",
  ].join("\n");
}

// ---- state feed ---------------------------------------------------------------------------------

function onState(payload) {
  snapshot = payload.snapshot;
  currentContext = payload.context;
  currentContextKey = payload.contextKey ?? "";
  arbiter.setSnapshot(snapshot);
  render(snapshot);
  $("context").textContent = currentContext;
  syncContext();
  for (const a of payload.alerts ?? []) deliver(a);
  if ($("autoack").checked) {
    for (const c of snapshot.commands.filter((c) => c.status === "pending")) api("POST", `/coach/sessions/${sid}/commands/${c.commandId}/ack`, { status: "applied" });
  }
}

function openFeed() {
  feed?.close();
  feed = new EventSource(`/coach/sessions/${sid}/stream`);
  feed.addEventListener("state", (e) => onState(JSON.parse(e.data)));
  feed.onopen = () => { $("feed").textContent = "state feed live"; $("feed").className = "pill on"; };
  feed.onerror = () => { $("feed").textContent = "state feed reconnecting"; $("feed").className = "pill warn"; };
}

async function loadReflexClips() {
  reflexClips.clear();
  const { ok, json } = await api("GET", `/jarvis/reflex/${sid}`);
  if (!ok || !json.configured) { log("event", "Reflex warnings not configured; urgent alerts will go through the LLM."); return; }
  const results = await Promise.allSettled(json.lines.map(async (l) => {
    const res = await fetch(l.route);
    if (!res.ok) throw new Error(`${l.key}: ${res.status}`);
    const audio = new Audio(URL.createObjectURL(await res.blob()));
    audio.preload = "auto";
    reflexClips.set(l.key, audio);
  }));
  const failed = results.filter((r) => r.status === "rejected").length;
  log("event", `Reflex warnings ready: ${reflexClips.size} clips${failed ? `, ${failed} failed` : ""}.`);
}

// ---- tools --------------------------------------------------------------------------------------

// Tools run on the coach server, shared with the native headset voice client.
const TOOL_NAMES = ["get_surgery_state", "get_hint", "explain_structure", "highlight_structure", "get_patient_brief", "check_preop", "look_at_scene"];
const clientTools = Object.fromEntries(
  TOOL_NAMES.map((name) => [
    name,
    async (params) => {
      const { ok, json } = await api("POST", `/coach/sessions/${sid}/tools/${name}`, params ?? {});
      return ok ? json.result : (json.error?.message ?? `${name} failed.`);
    },
  ]),
);

// ---- voice --------------------------------------------------------------------------------------

async function startVoice() {
  // The server binds the connection to this session and supplies the prompt it built for it.
  const conn = await api("GET", `/jarvis/connection?sessionId=${encodeURIComponent(sid)}`);
  if (!conn.ok) {
    $("voice").textContent = "voice not configured"; $("voice").className = "pill warn";
    log("event", `${conn.json.error?.message ?? "Voice unavailable."} Cautions will use the browser voice.`);
    return;
  }
  await navigator.mediaDevices.getUserMedia({ audio: true });
  convo = await Conversation.startSession({
    ...(conn.json.signedUrl ? { signedUrl: conn.json.signedUrl, connectionType: "websocket" } : { agentId: conn.json.agentId }),
    overrides: { agent: { prompt: { prompt: conn.json.prompt }, firstMessage: conn.json.firstMessage } },
    clientTools,
    onConnect: () => { mirrorStatus("listening"); $("voice").textContent = "voice live"; $("voice").className = "pill on"; lastSemantic = ""; syncContext(true); },
    onDisconnect: () => { mirrorStatus("offline"); $("voice").textContent = "voice off"; $("voice").className = "pill"; convo = null; },
    onError: (e) => log("event", `Voice error: ${e?.message ?? e}`, "urgent"),
    onModeChange: ({ mode }) => {
      arbiter.setMode(mode);
      mirrorStatus(mode === "speaking" ? "speaking" : "listening");
      $("mode").textContent = mode; $("mode").className = `pill ${mode === "speaking" ? "speaking" : ""}`;
      if (mode === "speaking" && pendingTrace && !pendingTrace.speakingAt) { pendingTrace.speakingAt = performance.timeOrigin + performance.now(); renderLatency(); }
    },
    onAgentChatResponsePart: () => {
      if (pendingTrace && !pendingTrace.firstTextAt) { pendingTrace.firstTextAt = performance.timeOrigin + performance.now(); renderLatency(); }
    },
    onIncomingEvent: (e) => { if (e?.type === "agent_response_complete") arbiter.responseComplete(); },
    onVadScore: (v) => { if ((v?.vadScore ?? v?.vad_score ?? 0) > 0.6) arbiter.userSpoke(); },
    onInterruption: () => arbiter.userSpoke(),
    onContextUsage: (u) => {
      const nums = Object.entries(u ?? {}).filter(([, v]) => typeof v === "number");
      $("ctx").textContent = nums.length ? `context ${nums.map(([k, v]) => `${k.replace(/_/g, " ")} ${v}`).join(" / ")}` : "context n/a";
    },
    onMessage: ({ message, source }) => {
      if (source === "user") {
        if (String(message).startsWith("[SIM EVENT]") || String(message).startsWith("[SIM EVENT ")) return;
        if (message === lastTyped) return;
        arbiter.userSpoke();
      }
      mirrorTranscript(source === "user" ? "learner" : "coach", cleanTranscript(message));
      log(source === "user" ? "user" : "ai", cleanTranscript(message));
    },
  });
}

// Pick a patient: interview first when the case has one, otherwise straight to surgery.
const encounter = createEncounterFlow({
  api,
  log,
  Conversation,
  setActiveConvo: (c) => { encounterConvo = c; },
  onStatus: (text, cls) => { $("voice").textContent = text; $("voice").className = `pill ${cls}`; },
  onScrubIn: (encounterId) => startSurgery(encounterId),
});
$("to-attending").onclick = () => encounter.presentToAttending();
$("scrub-in").onclick = () => encounter.scrubIn();
// Encounter failures stay on screen with Retry; a retried start may find no interview and go to surgery.
$("flow-retry").onclick = async () => {
  if ((await encounter.retry()) === "none") await startSurgery();
};

$("start").onclick = async () => {
  $("start").disabled = true;
  $("stop").disabled = false;
  patientId = $("patient").value;
  $("log").innerHTML = "";
  if ((await encounter.start(patientId)) === "none") await startSurgery();
};

async function startSurgery(encounterId = "") {
  // The scored office encounter carries into the operating room prompt.
  const created = await api("POST", "/coach/sessions", { patientId, mode: $("presentation").value, ...(encounterId ? { encounterId } : {}) });
  if (!created.ok) { log("event", created.json.error?.message ?? "Could not start.", "urgent"); $("start").disabled = false; return; }
  log("event", "Surgery: Jarvis is coaching. Use the simulator or the headset.");
  sid = created.json.sessionId;
  arbiter = createArbiter();
  traces.length = 0;
  kase = (await api("GET", `/patients/${patientId}/case`)).json;
  onState({ snapshot: created.json.snapshot, context: created.json.context, alerts: [] });
  openFeed();
  $("stop").disabled = false;
  await loadReflexClips();
  try { await startVoice(); }
  catch (e) { log("event", `Voice failed to start: ${e?.message ?? e}. Cautions will use the browser voice.`, "urgent"); }
  if (!convo) log("ai", created.json.firstMessage);
}

$("stop").onclick = async () => {
  await encounter.stop();
  feed?.close(); feed = null;
  if (convo) await convo.endSession();
  convo = null; sid = "";
  $("feed").textContent = "no state feed"; $("feed").className = "pill";
  $("start").disabled = false; $("stop").disabled = true;
};

document.querySelectorAll("#simbuttons button").forEach((b) => (b.onclick = async () => {
  if (!sid) return;
  const { ok, json } = await api("POST", `/coach/sessions/${sid}/simulate`, { kind: b.dataset.sim });
  if (!ok) log("event", json.error?.message ?? "Simulation failed.", "urgent");
}));

$("say").onsubmit = (e) => {
  e.preventDefault();
  const text = $("sayinput").value.trim();
  if (!text) return;
  $("sayinput").value = "";
  const target = encounterConvo ?? convo;
  if (target) { lastTyped = text; arbiter.userSpoke(); target.sendUserMessage(text); log("user", text); if (target === convo) mirrorTranscript("learner", text); }
  else log("event", "Voice is not connected. Configure ElevenLabs first.");
};

loadPatients();
