// The pre-op office on the laptop page: a 1:1 interview with the patient voice, driven by committed
// rounds of four clinician moves. The learner taps a choice or holds the button and says it; the server
// transcribes, matches and grades. The patient voice only hears the picks (its mic is muted) and replies in
// character. Jarvis is not in this flow; the scorecard is on screen only.

const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
export const cleanLine = (text) => String(text ?? "").replace(/\[[a-z ]{2,24}\]\s*/gi, "").replace(/\s{2,}/g, " ").trim();

const toBase64 = async (blob) => {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(bin);
};

export function createInterviewFlow({ api, log, Conversation, setActiveConvo, onStatus, onScrubIn, doc = globalThis.document }) {
  const $ = (id) => doc.getElementById(id);
  let iv = null; // POST /interviews response
  let convo = null;
  let round = null;
  let busy = false; // a pick is in flight or the patient is replying
  let recorder = null;

  const call = async (method, path, body) => {
    try {
      return await api(method, path, body);
    } catch (e) {
      return { ok: false, status: 0, json: { error: { code: "network_error", message: `The service is unreachable (${e?.message ?? e}).` } } };
    }
  };

  function stage(name) {
    for (const el of doc.querySelectorAll("#iv-stages [data-stage]")) el.className = `pill ${el.dataset.stage === name ? "on" : ""}`;
    $("scrub-in").disabled = name !== "scored";
  }

  function setBusy(on) {
    busy = on;
    for (const b of doc.querySelectorAll("#iv-choices button")) b.disabled = on;
    $("iv-talk").disabled = on;
  }

  function renderRound(r) {
    round = r;
    $("iv-card").hidden = !r;
    if (!r) return;
    $("iv-progress").textContent = `Round ${r.number} of ${r.of} · ${r.stage}`;
    $("iv-prompt").textContent = r.prompt;
    $("iv-choices").innerHTML = r.choices.map((c) => `<button data-key="${c.key}" class="choice"><b>${c.key}</b> ${esc(c.text)}</button>`).join("");
    for (const b of doc.querySelectorAll("#iv-choices button")) b.onclick = () => answer({ key: b.dataset.key });
    $("iv-heard").textContent = "";
  }

  function addFinding(f) {
    if (!f) return;
    $("iv-findings").insertAdjacentHTML("beforeend", `<div class="${f.abnormal ? "danger" : ""}"><b>${esc(f.label)}:</b> ${esc(f.text)}</div>`);
  }

  function showScore(card) {
    stage("scored");
    renderRound(null);
    $("iv-score").innerHTML = `
      <div class="step-title">${card.total}/100 · ${esc(card.grade)}</div>
      <div class="kv">${card.sections.map((s) => `<div>${esc(s.label)}</div><div>${s.score}/${s.max}</div>`).join("")}</div>
      <div style="margin-top:10px">${card.rounds
        .map((r) => `<div class="${r.picked.grade === "wrong" ? "danger" : r.picked.grade === "partial" ? "" : "muted"}"><b>${esc(r.prompt)}</b> You: ${esc(r.picked.text)}${r.picked.grade === "correct" ? " ✓" : ` · Best: ${esc(r.best.text)}`}. ${esc(r.picked.grade === "correct" ? r.picked.feedback : r.best.feedback)}</div>`)
        .join("")}</div>`;
    log("event", `Interview scored ${card.total}/100 (${card.grade}). Scrub in when ready.`);
  }

  // The patient replies to the pick: a silent direction for what to convey, then the move as the user turn.
  function patientReplies(patient) {
    if (!convo) return setBusy(false);
    const direction = patient.closing ? `${patient.direction} Then close with, in your own words: "${patient.closing}"` : patient.direction;
    try {
      convo.sendContextualUpdate(`[DIRECTION] ${direction}`);
      convo.sendUserMessage(`[CLINICIAN] ${patient.clinicianMove}`);
    } catch (e) {
      log("event", `Patient voice error: ${e?.message ?? e}`, "urgent");
      setBusy(false);
    }
  }

  async function answer(body) {
    if (!iv || busy || !round) return;
    setBusy(true);
    const res = await call("POST", `/interviews/${iv.interviewId}/answer`, body);
    if (!res.ok) {
      $("iv-heard").textContent = res.json.error?.message ?? `error ${res.status}`;
      setBusy(false);
      return;
    }
    const out = res.json;
    if (out.heard) $("iv-heard").textContent = `Heard: "${out.heard}" → ${out.choice.key}`;
    log("user", `${out.choice.key}) ${out.choice.text}`);
    addFinding(out.finding);
    renderRound(out.next);
    if (out.scorecard) showScore(out.scorecard);
    patientReplies(out.patient);
    if (!convo) setBusy(false);
  }

  async function startTalk() {
    if (busy || recorder) return;
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const chunks = [];
      recorder = new MediaRecorder(stream);
      recorder.ondataavailable = (e) => e.data.size && chunks.push(e.data);
      recorder.onstop = async () => {
        stream.getTracks().forEach((t) => t.stop());
        const blob = new Blob(chunks, { type: recorder.mimeType || "audio/webm" });
        recorder = null;
        $("iv-talk").textContent = "Hold to answer";
        if (blob.size < 2000) return void ($("iv-heard").textContent = "Too short. Hold the button while you speak.");
        $("iv-heard").textContent = "Listening back…";
        await answer({ audio: await toBase64(blob), mimeType: blob.type });
      };
      recorder.start();
      $("iv-talk").textContent = "Release to send";
    } catch (e) {
      $("iv-heard").textContent = `Microphone unavailable (${e?.message ?? e}). Tap a choice instead.`;
    }
  }
  const stopTalk = () => recorder?.state === "recording" && recorder.stop();

  async function connect() {
    const conn = await call("GET", `/interviews/${iv.interviewId}/connection`);
    if (!conn.ok) {
      log("event", `Patient voice unavailable: ${conn.json.error?.message ?? conn.status}. Tap through the interview silently.`, "urgent");
      setBusy(false);
      return;
    }
    const j = conn.json;
    const who = iv.speakerName.split(" ")[0];
    try {
      convo = await Conversation.startSession({
        ...(j.signedUrl ? { signedUrl: j.signedUrl, connectionType: "websocket" } : { agentId: j.agentId }),
        overrides: { agent: { prompt: { prompt: j.prompt }, firstMessage: j.firstMessage }, ...(j.voiceId ? { tts: { voiceId: j.voiceId } } : {}) },
        onConnect: () => {
          onStatus(`${who} live`, "on");
          convo?.setMicMuted?.(true); // the patient hears only the picks
        },
        onDisconnect: () => onStatus("voice off", ""),
        onError: (e) => log("event", `Voice error: ${e?.message ?? e}`, "urgent"),
        onModeChange: ({ mode }) => {
          $("mode").textContent = mode;
          $("mode").className = `pill ${mode === "speaking" ? "speaking" : ""}`;
          if (mode === "listening" && round) setBusy(false);
        },
        onMessage: ({ message, source }) => {
          if (source === "user") return; // picks are logged when made
          const text = cleanLine(message);
          if (!text) return;
          log("ai", `${who}: ${text}`);
          call("POST", `/interviews/${iv.interviewId}/transcript`, { speaker: "patient", text });
        },
      });
      setActiveConvo(convo);
    } catch (e) {
      log("event", `Voice failed to start: ${e?.message ?? e}. Tap through the interview silently.`, "urgent");
      setBusy(false);
    }
  }

  async function end() {
    if (convo) await convo.endSession().catch(() => {});
    convo = null;
    setActiveConvo(null);
  }

  $("iv-talk").onmousedown = startTalk;
  $("iv-talk").onmouseup = stopTalk;
  $("iv-talk").onmouseleave = stopTalk;
  $("iv-talk").ontouchstart = (e) => (e.preventDefault(), startTalk());
  $("iv-talk").ontouchend = stopTalk;
  doc.addEventListener("keydown", (e) => {
    if (!iv || busy || !round || e.target?.tagName === "INPUT") return;
    const key = e.key.toUpperCase();
    if (["A", "B", "C", "D"].includes(key)) answer({ key });
  });

  return {
    // "interview": running; "none": no authored interview, go straight to surgery; "failed": shown in the log.
    async start(patientId) {
      const created = await call("POST", "/interviews", { patientId });
      if (!created.ok) {
        if (created.json.error?.code === "no_interview") return "none";
        log("event", `Could not start the interview: ${created.json.error?.message ?? created.status}`, "urgent");
        return "failed";
      }
      iv = created.json;
      $("encounter").hidden = false;
      $("surgery").hidden = true;
      $("iv-who").textContent = iv.speaker === "parent" ? `${iv.speakerName} (parent of ${iv.patientName})` : iv.patientName;
      $("iv-findings").innerHTML = "";
      $("iv-score").innerHTML = "";
      stage("interview");
      renderRound(iv.round);
      setBusy(true); // until the patient finishes the opening line
      log("event", `Interview with ${iv.speakerName}. Listen, then pick your next move: tap A to D, press the key, or hold the button and say it.`);
      await connect();
      return "interview";
    },
    async scrubIn() {
      await end();
      $("encounter").hidden = true;
      $("surgery").hidden = false;
      onScrubIn(iv?.interviewId ?? "");
    },
    async stop() {
      await end();
      iv = null;
      round = null;
      $("encounter").hidden = true;
      $("surgery").hidden = false;
    },
  };
}
