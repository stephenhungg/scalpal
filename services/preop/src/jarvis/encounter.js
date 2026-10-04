// Pre-op encounter on the laptop page: 1-on-1 patient interview (the patient agent, in that patient's
// own voice), then a case presentation to Jarvis as attending, then the scorecard. All facts and scoring
// live on the server; this file only connects voices and renders what the server returns.
//
// Failures are shown with a Retry button and never skip ahead: only "this patient has no authored
// interview" goes straight to surgery, and a voice agent is never connected without the prompt the
// server built for its role. Unit tested in test/encounter-flow.test.ts with a fake page and voice.

// Expressive audio tags like [wince] shape the voice; keep them out of the transcript.
export const cleanTranscript = (text) => String(text ?? "").replace(/\[[a-z ]{2,24}\]\s*/gi, "").replace(/\s{2,}/g, " ").trim();

const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

export function createEncounterFlow({
  api,
  log,
  setActiveConvo,
  onStatus,
  onScrubIn,
  Conversation,
  getMicrophone = () => navigator.mediaDevices.getUserMedia({ audio: true }),
  doc = globalThis.document,
}) {
  const $ = (id) => doc.getElementById(id);
  let enc = null; // server response from POST /encounters
  let convo = null;
  let retry = null; // the step that failed, re-run by the Retry button

  // Network errors become ordinary failed responses so every caller handles one shape.
  const call = async (method, path, body) => {
    try {
      return await api(method, path, body);
    } catch (e) {
      return { ok: false, status: 0, json: { error: { code: "network_error", message: `The service is unreachable (${e?.message ?? e}).` } } };
    }
  };

  function fail(message, again) {
    retry = again;
    $("flow-error-text").textContent = message;
    $("flow-error").hidden = false;
    log("event", `${message} Use Retry to try again.`, "urgent");
    onStatus("needs retry", "warn");
    return false;
  }

  function clearError() {
    retry = null;
    $("flow-error").hidden = true;
  }

  const toolsFor = (encounterId, names) =>
    Object.fromEntries(
      names.map((name) => [
        name,
        async (params) => {
          const { ok, json } = await call("POST", `/encounters/${encounterId}/tools/${name}`, params ?? {});
          if (json.state) renderChart(json.state);
          if (name === "record_assessment" && ok) setTimeout(() => showScore(encounterId), 300);
          return ok ? json.result : (json.error?.message ?? `${name} failed.`);
        },
      ]),
    );

  function stage(name) {
    for (const el of doc.querySelectorAll("#stages [data-stage]")) el.className = `pill ${el.dataset.stage === name ? "on" : ""}`;
    $("to-attending").disabled = name !== "interview";
    $("scrub-in").disabled = name !== "scored";
  }

  function renderChart(state) {
    $("enc-who").textContent = state.speaker === "parent" ? `${state.speakerName} (parent of ${state.patientName})` : state.patientName;
    $("enc-history").textContent = state.historyAsked.map((h) => h.label).join(", ") || "nothing asked yet";
    $("enc-exam").innerHTML = state.exams.map((x) => `<div><b>${esc(x.label)}:</b> ${esc(x.finding)}</div>`).join("") || '<span class="muted">no exam yet</span>';
    $("enc-tests").innerHTML = state.tests.map((x) => `<div class="${x.abnormal ? "danger" : ""}"><b>${esc(x.label)}:</b> ${esc(x.result)}</div>`).join("") || '<span class="muted">no tests ordered</span>';
  }

  // Connects the voice for the encounter's current role. The server picks the agent from the phase and
  // returns the prompt it built; without that prompt nothing connects.
  async function connect(encounterId, role) {
    const conn = await call("GET", `/jarvis/connection?encounterId=${encodeURIComponent(encounterId)}`);
    if (!conn.ok) return fail(`Could not connect the ${role} voice: ${conn.json.error?.message ?? `error ${conn.status}`}`, () => connect(encounterId, role));
    if (conn.json.role !== role || !conn.json.prompt || !conn.json.firstMessage) {
      return fail(`The service did not return the ${role} prompt, so the voice was not connected.`, () => connect(encounterId, role));
    }
    const speakerLabel = role === "patient" ? enc.speakerName.split(" ")[0] : "Jarvis";
    const tools = role === "patient" ? toolsFor(encounterId, ["answer", "examine", "order_test"]) : toolsFor(encounterId, ["get_encounter_summary", "record_assessment"]);
    try {
      await getMicrophone();
      const c = await Conversation.startSession({
        ...(conn.json.signedUrl ? { signedUrl: conn.json.signedUrl, connectionType: "websocket" } : { agentId: conn.json.agentId }),
        overrides: { agent: { prompt: { prompt: conn.json.prompt }, firstMessage: conn.json.firstMessage }, ...(conn.json.voiceId ? { tts: { voiceId: conn.json.voiceId } } : {}) },
        clientTools: tools,
        onConnect: () => onStatus(`${speakerLabel} live`, "on"),
        onDisconnect: () => onStatus("voice off", ""),
        onError: (e) => log("event", `Voice error: ${e?.message ?? e}`, "urgent"),
        onModeChange: ({ mode }) => { $("mode").textContent = mode; $("mode").className = `pill ${mode === "speaking" ? "speaking" : ""}`; },
        onMessage: ({ message, source }) => {
          const text = cleanTranscript(message);
          if (text) log(source === "user" ? "user" : "ai", source === "user" ? text : `${speakerLabel}: ${text}`);
          call("POST", `/encounters/${encounterId}/transcript`, { speaker: source === "user" ? "learner" : role === "patient" ? "patient" : "coach", text });
        },
      });
      convo = c;
      setActiveConvo(c);
      clearError();
      return true;
    } catch (e) {
      return fail(`Voice failed to start: ${e?.message ?? e}.`, () => connect(encounterId, role));
    }
  }

  async function end() {
    if (convo) await convo.endSession().catch((e) => console.warn("endSession failed", e));
    convo = null;
    setActiveConvo(null);
  }

  async function showScore(encounterId) {
    const { ok, status, json } = await call("GET", `/encounters/${encounterId}/score`);
    if (!ok) return fail(`Could not load the scorecard: ${json.error?.message ?? `error ${status}`}`, () => showScore(encounterId));
    const card = json.scorecard;
    clearError();
    stage("scored");
    $("scorecard").innerHTML = `
      <div class="step-title">${card.total}/100 · ${esc(card.grade)}</div>
      <div class="kv">${card.sections.map((s) => `<div>${esc(s.label)}</div><div>${s.score}/${s.max}${s.missed.length ? ` <span class="muted">missed: ${esc(s.missed.join(", "))}</span>` : ""}</div>`).join("")}</div>
      <div style="margin-top:10px">${card.feedback.map((f) => `<div class="${f.startsWith("Must fix") ? "danger" : f.startsWith("Good") ? "" : "muted"}">${esc(f)}</div>`).join("")}</div>`;
    return true;
  }

  async function presentToAttending(encounterId) {
    await end();
    const { ok, status, json } = await call("POST", `/encounters/${encounterId}/attending`);
    if (!ok) return fail(`Could not hand over to the attending: ${json.error?.message ?? `error ${status}`}`, () => presentToAttending(encounterId));
    stage("attending");
    log("event", "Present the patient to your attending.");
    return connect(encounterId, "attending");
  }

  async function start(patientId) {
    const created = await call("POST", "/encounters", { patientId });
    if (!created.ok) {
      if (created.json.error?.code === "no_encounter") return "none";
      $("encounter").hidden = false;
      $("surgery").hidden = true;
      fail(`Could not start the interview: ${created.json.error?.message ?? `error ${created.status}`}`, () => start(patientId));
      return "failed";
    }
    enc = created.json;
    clearError();
    $("encounter").hidden = false;
    $("surgery").hidden = true;
    $("scorecard").innerHTML = "";
    renderChart(enc.state);
    stage("interview");
    log("event", `Interview: ${enc.speaker === "parent" ? `${enc.speakerName}, ${enc.patientName}'s parent` : enc.patientName}. Take a history, examine, and order tests by voice. Findings and results appear on the left.`);
    await connect(enc.encounterId, "patient");
    return "encounter";
  }

  return {
    // "encounter": interview running (or waiting on Retry for its voice); "none": this patient has no
    // authored interview, go straight to surgery; "failed": shown to the learner with Retry.
    start,

    async presentToAttending() {
      if (!enc) return false;
      return presentToAttending(enc.encounterId);
    },

    // Re-runs whatever failed last. A retried start can resolve to "none" (go to surgery).
    async retry() {
      const again = retry;
      if (!again) return null;
      return again();
    },

    async scrubIn() {
      await end();
      clearError();
      $("encounter").hidden = true;
      $("surgery").hidden = false;
      onScrubIn();
    },

    async stop() {
      await end();
      enc = null;
      clearError();
      $("encounter").hidden = true;
      $("surgery").hidden = false;
    },
  };
}
