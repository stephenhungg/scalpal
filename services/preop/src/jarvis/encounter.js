// Pre-op encounter on the laptop page: 1-on-1 patient interview (the patient agent, in that patient's
// own voice), then a case presentation to Jarvis as attending, then the scorecard. All facts and scoring
// live on the server; this file only connects voices and renders what the server returns.
import { Conversation } from "https://esm.sh/@elevenlabs/client@1.26.0";

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
// Expressive audio tags like [wince] shape the voice; keep them out of the transcript.
export const cleanTranscript = (text) => String(text ?? "").replace(/\[[a-z ]{2,24}\]\s*/gi, "").replace(/\s{2,}/g, " ").trim();

export function createEncounterFlow({ api, log, setActiveConvo, onStatus, onScrubIn }) {
  let enc = null; // server response from POST /encounters
  let convo = null;

  const toolsFor = (names) =>
    Object.fromEntries(
      names.map((name) => [
        name,
        async (params) => {
          const { ok, json } = await api("POST", `/encounters/${enc.encounterId}/tools/${name}`, params ?? {});
          if (json.state) renderChart(json.state);
          if (name === "record_assessment" && ok) setTimeout(showScore, 300);
          return ok ? json.result : (json.error?.message ?? `${name} failed.`);
        },
      ]),
    );

  function stage(name) {
    for (const el of document.querySelectorAll("#stages [data-stage]")) el.className = `pill ${el.dataset.stage === name ? "on" : ""}`;
    $("to-attending").disabled = name !== "interview";
    $("scrub-in").disabled = name !== "scored";
  }

  function renderChart(state) {
    $("enc-who").textContent = state.speaker === "parent" ? `${state.speakerName} (parent of ${state.patientName})` : state.patientName;
    $("enc-history").textContent = state.historyAsked.map((h) => h.label).join(", ") || "nothing asked yet";
    $("enc-exam").innerHTML = state.exams.map((x) => `<div><b>${esc(x.label)}:</b> ${esc(x.finding)}</div>`).join("") || '<span class="muted">no exam yet</span>';
    $("enc-tests").innerHTML = state.tests.map((x) => `<div class="${x.abnormal ? "danger" : ""}"><b>${esc(x.label)}:</b> ${esc(x.result)}</div>`).join("") || '<span class="muted">no tests ordered</span>';
  }

  async function connect(agent, prompt, firstMessage, voiceId, tools, speakerLabel) {
    const conn = await api("GET", `/jarvis/connection${agent === "patient" ? "?agent=patient" : ""}`);
    if (!conn.ok) {
      log("event", conn.json.error?.message ?? "Voice not configured.", "urgent");
      return null;
    }
    await navigator.mediaDevices.getUserMedia({ audio: true });
    const c = await Conversation.startSession({
      ...(conn.json.signedUrl ? { signedUrl: conn.json.signedUrl, connectionType: "websocket" } : { agentId: conn.json.agentId }),
      overrides: { agent: { prompt: { prompt }, firstMessage }, ...(voiceId ? { tts: { voiceId } } : {}) },
      clientTools: tools,
      onConnect: () => onStatus(`${speakerLabel} live`, "on"),
      onDisconnect: () => onStatus("voice off", ""),
      onError: (e) => log("event", `Voice error: ${e?.message ?? e}`, "urgent"),
      onModeChange: ({ mode }) => { $("mode").textContent = mode; $("mode").className = `pill ${mode === "speaking" ? "speaking" : ""}`; },
      onMessage: ({ message, source }) => {
        const text = cleanTranscript(message);
        if (text) log(source === "user" ? "user" : "ai", source === "user" ? text : `${speakerLabel}: ${text}`);
        api("POST", `/encounters/${enc.encounterId}/transcript`, { speaker: source === "user" ? "learner" : agent === "patient" ? "patient" : "coach", text }).catch(() => {});
      },
    });
    setActiveConvo(c);
    return c;
  }

  async function end() {
    if (convo) await convo.endSession().catch(() => {});
    convo = null;
    setActiveConvo(null);
  }

  async function showScore() {
    const { json } = await api("GET", `/encounters/${enc.encounterId}/score`);
    const card = json.scorecard;
    stage("scored");
    $("scorecard").innerHTML = `
      <div class="step-title">${card.total}/100 · ${esc(card.grade)}</div>
      <div class="kv">${card.sections.map((s) => `<div>${esc(s.label)}</div><div>${s.score}/${s.max}${s.missed.length ? ` <span class="muted">missed: ${esc(s.missed.join(", "))}</span>` : ""}</div>`).join("")}</div>
      <div style="margin-top:10px">${card.feedback.map((f) => `<div class="${f.startsWith("Must fix") ? "danger" : f.startsWith("Good") ? "" : "muted"}">${esc(f)}</div>`).join("")}</div>`;
  }

  return {
    // Returns false when this patient has no authored interview (go straight to surgery).
    async start(patientId) {
      const created = await api("POST", "/encounters", { patientId });
      if (!created.ok) return false;
      enc = created.json;
      $("encounter").hidden = false;
      $("surgery").hidden = true;
      $("scorecard").innerHTML = "";
      renderChart(enc.state);
      stage("interview");
      log("event", `Interview: ${enc.speaker === "parent" ? `${enc.speakerName}, ${enc.patientName}'s parent` : enc.patientName}. Take a history, examine, and order tests by voice. Findings and results appear on the left.`);
      try {
        convo = await connect("patient", enc.patientPrompt, enc.patientFirstMessage, enc.voiceId, toolsFor(["answer", "examine", "order_test"]), enc.speakerName.split(" ")[0]);
      } catch (e) {
        log("event", `Voice failed to start: ${e?.message ?? e}`, "urgent");
      }
      return true;
    },

    async presentToAttending() {
      await end();
      const { json } = await api("POST", `/encounters/${enc.encounterId}/attending`);
      stage("attending");
      log("event", "Present the patient to your attending.");
      try {
        convo = await connect("jarvis", json.attendingPrompt, json.attendingFirstMessage, "", toolsFor(["get_encounter_summary", "record_assessment"]), "Jarvis");
      } catch (e) {
        log("event", `Voice failed to start: ${e?.message ?? e}`, "urgent");
      }
    },

    async scrubIn() {
      await end();
      $("encounter").hidden = true;
      $("surgery").hidden = false;
      onScrubIn(enc?.encounterId ?? "");
    },

    async stop() {
      await end();
      enc = null;
      $("encounter").hidden = true;
      $("surgery").hidden = false;
    },
  };
}
