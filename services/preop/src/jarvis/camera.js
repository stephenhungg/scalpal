// Camera test rig: a webcam (or an iPhone through Continuity Camera) stands in for the Quest camera.
// MediaPipe finds the patient's torso and the learner's hand; the fingertip is mapped onto the generic
// anatomy, and the result goes to the live coach session exactly like headset events: focus when the
// finger rests on a structure, touch when the learner pinches, tracking validity when the body is lost.
// The page also plays the headset's part for Jarvis's highlights: it draws them and acks them.
import { FilesetResolver, HandLandmarker, PoseLandmarker } from "https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@1.0.1/vision_bundle.mjs";
import { REGIONS, imageToUV, pinchState, portAt, portToUV, regionAt, torsoFromPose, uvToImage } from "/jarvis/body-map.js";

const $ = (id) => document.getElementById(id);
const api = async (method, path, body) => {
  const res = await fetch(path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  return { ok: res.ok, json: await res.json().catch(() => ({})) };
};
const MODELS = "https://storage.googleapis.com/mediapipe-models";

let pose = null, hands = null, stream = null;
let sid = "", snapshot = null, kase = null, allowed = null, names = new Map();
let pinched = false, focus = "", focusCandidate = "", focusSince = 0, trackingValid = null, lostSince = 0;
let highlighted = "", lastUV = null, frames = 0, fpsAt = performance.now();
const video = $("video"), canvas = $("overlay"), ctx = canvas.getContext("2d");

function feed(text, cls = "") {
  const el = document.createElement("div");
  el.className = cls;
  el.textContent = `${new Date().toLocaleTimeString()}  ${text}`;
  $("feed").prepend(el);
  while ($("feed").children.length > 30) $("feed").lastChild.remove();
}

const nameOf = (id) => names.get(id) ?? id.replaceAll("_", " ");

async function send(event) {
  if (!sid) return;
  const { ok, json } = await api("POST", `/coach/sessions/${sid}/events`, { event: { ...event, eventId: `cam-${crypto.randomUUID().replaceAll("-", "")}` } });
  if (!ok) return feed(`rejected: ${json.error?.message ?? "error"}`, "warning");
  if (json.snapshot) applySnapshot(json.snapshot);
  for (const a of json.alerts ?? []) feed(`${a.kind}: ${a.say}`, a.tier === "warning" ? "warning" : "");
}

function applySnapshot(s) {
  snapshot = s;
  // Draw the newest applied highlight, whoever acked it (this page or the Jarvis page's simulator).
  const latest = [...(s.commands ?? [])].reverse().find((c) => c.status === "applied");
  if (latest) highlighted = latest.action === "highlight" ? latest.targetId : "";
  const st = s.step;
  $("step").textContent = s.status === "completed" ? "Procedure complete" : `Step ${s.stepNumber}/${s.stepCount}: ${st.title}`;
  $("targets").textContent = s.status === "completed" ? "" : [st.remaining.join(", ") || "nothing", st.progressText].filter(Boolean).join(" · ");
  if ($("follow").checked && st.instrumentId && $("instrument").value !== st.instrumentId) $("instrument").value = st.instrumentId;
}

// Find the live coach session (started from the Jarvis page) and keep its state fresh.
async function attach() {
  const cur = await api("GET", "/coach/current");
  if (cur.ok && cur.json.sessionId && cur.json.sessionId !== sid) {
    sid = cur.json.sessionId;
    const kaseRes = await api("GET", `/patients/${cur.json.patientId}/case`);
    kase = kaseRes.json;
    allowed = new Set(kase.anatomy.map((a) => a.id));
    names = new Map(kase.anatomy.map((a) => [a.id, a.displayName]));
    $("instrument").innerHTML = kase.instruments.map((i) => `<option value="${i.id}">${i.displayName}</option>`).join("");
    $("sess").textContent = `${kase.patient.displayLabel} · ${kase.procedure.shortTitle}`;
    $("sess").className = "pill on";
    feed(`attached to ${kase.procedure.title} for ${kase.patient.displayLabel}`);
    trackingValid = null;
  }
  if (sid) {
    const s = await api("GET", `/coach/sessions/${sid}`);
    if (s.ok) applySnapshot(s.json.snapshot);
    // Act as the headset for Jarvis's highlight requests: draw it, then ack.
    const cmds = await api("GET", `/coach/sessions/${sid}/commands`);
    for (const c of cmds.json.commands ?? []) {
      highlighted = c.action === "highlight" ? c.targetId : "";
      await api("POST", `/coach/sessions/${sid}/commands/${c.commandId}/ack`, { status: "applied" });
      feed(c.action === "highlight" ? `Jarvis highlighted ${nameOf(c.targetId)}` : "Jarvis cleared the highlight");
    }
  }
}
setInterval(attach, 700);

async function loadModels() {
  const fileset = await FilesetResolver.forVisionTasks("https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@1.0.1/wasm");
  // GPU when available; some machines and headless browsers only manage CPU.
  const make = async (Task, asset, extra) => {
    try {
      return await Task.createFromOptions(fileset, { baseOptions: { modelAssetPath: asset, delegate: "GPU" }, runningMode: "VIDEO", ...extra });
    } catch {
      feed("GPU unavailable, using CPU for vision");
      return Task.createFromOptions(fileset, { baseOptions: { modelAssetPath: asset, delegate: "CPU" }, runningMode: "VIDEO", ...extra });
    }
  };
  pose = await make(PoseLandmarker, `${MODELS}/pose_landmarker/pose_landmarker_lite/float16/latest/pose_landmarker_lite.task`, { numPoses: 1 });
  hands = await make(HandLandmarker, `${MODELS}/hand_landmarker/hand_landmarker/float16/latest/hand_landmarker.task`, { numHands: 2 });
}

async function listCameras() {
  const tmp = await navigator.mediaDevices.getUserMedia({ video: true }).catch(() => null);
  const cams = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === "videoinput");
  tmp?.getTracks().forEach((t) => t.stop());
  $("camera").innerHTML = cams.map((c, i) => `<option value="${c.deviceId}">${c.label || `Camera ${i + 1}`}</option>`).join("");
  const iphone = cams.find((c) => /iphone|continuity/i.test(c.label));
  if (iphone) $("camera").value = iphone.deviceId;
}

async function start() {
  $("go").disabled = true;
  feed("loading body and hand models...");
  if (!pose) await loadModels();
  stream?.getTracks().forEach((t) => t.stop());
  stream = await navigator.mediaDevices.getUserMedia({ video: { deviceId: { exact: $("camera").value }, width: { ideal: 1280 }, height: { ideal: 720 } } });
  video.srcObject = stream;
  await video.play();
  feed("camera live. Lie down in view (or face the camera), then point at the abdomen.");
  $("go").disabled = false;
  $("go").textContent = "Switch camera";
  requestAnimationFrame(loop);
}

function setTracking(valid) {
  if (valid === trackingValid) return;
  trackingValid = valid;
  $("track").textContent = valid ? "body locked" : "body lost";
  $("track").className = `pill ${valid ? "on" : "warn"}`;
  if (sid) send({ type: "tracking", valid });
}

function loop() {
  if (!stream) return;
  const now = performance.now();
  if (video.readyState >= 2) {
    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    const poseRes = pose.detectForVideo(video, now);
    const handRes = hands.detectForVideo(video, now);
    const torso = torsoFromPose(poseRes.landmarks?.[0]);

    // Body validity: a short dropout is ignored, a sustained one pauses the coach.
    if (torso) { lostSince = 0; setTracking(true); }
    else if (!lostSince) lostSince = now;
    else if (now - lostSince > 800) setTracking(false);

    // Use the hand whose fingertip is over the abdomen, else the first hand.
    let hand = null, uv = null;
    for (const h of handRes.landmarks ?? []) {
      const candidate = torso ? imageToUV(torso, h[8]) : null;
      if (!hand || (candidate && regionAt(candidate, allowed))) { hand = h; uv = candidate; }
    }
    lastUV = uv;
    const region = torso && uv ? regionAt(uv, allowed) : "";

    // Focus: report a structure once the finger has rested on it briefly.
    if (region !== focusCandidate) { focusCandidate = region; focusSince = now; }
    if (focusCandidate !== focus && now - focusSince > 250 && trackingValid) {
      focus = focusCandidate;
      $("focus").textContent = focus ? nameOf(focus) : "nothing";
      send({ type: "focus", structureId: focus });
    }

    // Pinch edge: use the instrument on what the finger is over (or place a port on port steps).
    const nowPinched = hand ? pinchState(hand, pinched) : false;
    if (nowPinched && !pinched && trackingValid && uv) {
      const step = snapshot?.step;
      const port = step?.action === "place_port" ? portAt(uv, kase?.procedure.ports ?? []) : "";
      if (port) { feed(`placed port: ${port.replaceAll("_", " ")}`); send({ type: "place_port", portId: port }); }
      else if (region) { feed(`${$("instrument").selectedOptions[0]?.text ?? "instrument"} on ${nameOf(region)}`); send({ type: "touch", structureId: region, instrumentId: $("instrument").value }); }
    }
    pinched = nowPinched;

    draw(torso, hand, region);
    frames += 1;
  }
  if (now - fpsAt > 1000) { $("fps").textContent = `${frames} fps`; frames = 0; fpsAt = now; }
  requestAnimationFrame(loop);
}

function draw(torso, hand, region) {
  const W = canvas.width, H = canvas.height;
  ctx.clearRect(0, 0, W, H);
  if (!torso) return;
  const P = (u, v) => { const p = uvToImage(torso, u, v); return [p.x * W, p.y * H]; };
  const poly = (pts, stroke, fill, width = 2) => {
    ctx.beginPath();
    pts.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    if (fill) { ctx.fillStyle = fill; ctx.fill(); }
    if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = width; ctx.stroke(); }
  };
  const rect = (r) => [P(r.u[0], r.v[0]), P(r.u[1], r.v[0]), P(r.u[1], r.v[1]), P(r.u[0], r.v[1])];
  poly([P(0, 0), P(1, 0), P(1, 1), P(0, 1)], "rgba(92,200,255,.6)");

  const targets = new Set(snapshot?.step?.targets?.map((t) => t.id) ?? []);
  const dangers = new Set(snapshot?.step?.dangers?.map((t) => t.id) ?? []);
  for (const r of REGIONS) {
    if (allowed && !allowed.has(r.id)) continue;
    const fill = r.id === highlighted ? "rgba(251,191,36,.45)" : r.id === region ? "rgba(255,255,255,.28)" : targets.has(r.id) ? "rgba(92,200,255,.18)" : dangers.has(r.id) ? "rgba(248,113,113,.12)" : "";
    poly(rect(r), r.id === highlighted ? "#fbbf24" : targets.has(r.id) ? "rgba(92,200,255,.8)" : "rgba(255,255,255,.12)", fill, r.id === highlighted ? 3 : 1);
  }
  if (snapshot?.step?.action === "place_port") {
    for (const port of kase?.procedure.ports ?? []) {
      const c = portToUV(port.position);
      const [x, y] = P(c.u, c.v);
      ctx.beginPath(); ctx.arc(x, y, 10, 0, Math.PI * 2); ctx.strokeStyle = "#4ade80"; ctx.lineWidth = 2; ctx.stroke();
    }
  }
  if (hand) {
    const tip = hand[8];
    ctx.beginPath(); ctx.arc(tip.x * W, tip.y * H, pinched ? 14 : 9, 0, Math.PI * 2);
    ctx.fillStyle = pinched ? "#f87171" : "#ffffff"; ctx.fill();
  }
}

$("go").onclick = () => start().catch((e) => { feed(`camera failed: ${e.message ?? e}`, "warning"); $("go").disabled = false; });
$("mirror").onchange = () => $("stage").classList.toggle("mirror", $("mirror").checked);
$("identify").onclick = () => { if (focus) { feed(`identified ${nameOf(focus)}`); send({ type: "identify", structureId: focus }); } else feed("point at a structure first", "warning"); };
$("confirm").onclick = () => { feed("confirmed step"); send({ type: "confirm" }); };
$("instrument").onchange = () => { $("follow").checked = false; };
listCameras().catch((e) => feed(`camera list failed: ${e.message ?? e}`, "warning"));
attach();
