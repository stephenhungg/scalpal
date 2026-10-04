// Camera test rig: a webcam (or an iPhone through Continuity Camera) stands in for the Quest camera.
// MediaPipe finds the patient's torso and the learner's hand; the fingertip is mapped onto the generic
// anatomy, and the result goes to the live coach session exactly like headset events: focus when the
// finger rests on a structure, touch when the learner pinches, tracking validity when the body is lost.
// Acting as the headset (sending those events and acking Jarvis's highlights) is opt-in via the
// "act as the headset" toggle and needs a running camera; by default the page only observes, so it never
// competes with a real Quest on the same session. The camera is requested only when Start is clicked.
import { FilesetResolver, HandLandmarker, ObjectDetector, PoseLandmarker } from "https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@1.0.1/vision_bundle.mjs";
import { REGIONS, imageToUV, pinchState, portAt, portToUV, regionAt, torsoFromPose, uvToImage } from "/jarvis/body-map.js";
import { createFrameLoop, createRigSession } from "/jarvis/camera-rig.js";

const $ = (id) => document.getElementById(id);
const api = async (method, path, body) => {
  const res = await fetch(path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  return { ok: res.ok, json: await res.json().catch(() => ({})) };
};
const MODELS = "https://storage.googleapis.com/mediapipe-models";

let pose = null, hands = null, objects = null, stream = null;
let detections = [], held = "", frameNo = 0;
// Real objects the 80-class COCO detector knows that can stand in for an instrument in the hand.
const OBJECT_TO_INSTRUMENT = { scissors: "lap_scissors", knife: "scalpel", fork: "atraumatic_grasper", toothbrush: "hook_cautery", spoon: "suction_irrigator" };
let snapshot = null, kase = null, allowed = null, names = new Map();
let pinched = false, focus = "", focusCandidate = "", focusSince = 0, trackingValid = null, lostSince = 0;
let highlighted = "", lastUV = null, frames = 0, fpsAt = performance.now(), lastTorso = null, lastFrameAt = 0, sendingFrame = false;
const FRAME_EVERY_MS = 2000;
const video = $("video"), canvas = $("overlay"), ctx = canvas.getContext("2d");

function feed(text, cls = "") {
  const el = document.createElement("div");
  el.className = cls;
  el.textContent = `${new Date().toLocaleTimeString()}  ${text}`;
  $("feed").prepend(el);
  while ($("feed").children.length > 30) $("feed").lastChild.remove();
}

const nameOf = (id) => names.get(id) ?? id.replaceAll("_", " ");
const nameOfInstrument = (id) => kase?.instruments.find((i) => i.id === id)?.displayName.toLowerCase() ?? id.replaceAll("_", " ");

async function send(event) {
  const res = await rig.send(event).catch((e) => { feed(`event failed: ${e?.message ?? e}`, "warning"); return null; });
  if (!res) return; // observing only, or no session yet
  const { ok, json } = res;
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

const option = (value, text) => Object.assign(document.createElement("option"), { value, textContent: text });

// Find the live coach session (started from the Jarvis page) and keep its state fresh.
const rig = createRigSession({
  api,
  actsAsHeadset: () => $("headset").checked && loop.running,
  onAttach: (k) => {
    kase = k;
    allowed = new Set(kase.anatomy.map((a) => a.id));
    names = new Map(kase.anatomy.map((a) => [a.id, a.displayName]));
    $("instrument").replaceChildren(...kase.instruments.map((i) => option(i.id, i.displayName)));
    $("sess").textContent = `${kase.patient.displayLabel} · ${kase.procedure.shortTitle}`;
    $("sess").className = "pill on";
    feed(`attached to ${kase.procedure.title} for ${kase.patient.displayLabel}`);
    trackingValid = null;
  },
  onSnapshot: applySnapshot,
  // Acting as the headset for Jarvis's highlight requests: draw it (the ack already went out).
  onCommand: (c) => {
    highlighted = c.action === "highlight" ? c.targetId : "";
    feed(c.action === "highlight" ? `Jarvis highlighted ${nameOf(c.targetId)}` : "Jarvis cleared the highlight");
  },
  feed,
});
setInterval(rig.attach, 700);

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
  // Same COCO classes as the Quest MultiObjectDetection sample (Stephen's bottle test), in the browser.
  objects = await make(ObjectDetector, `${MODELS}/object_detector/efficientdet_lite0/float16/latest/efficientdet_lite0.tflite`, { scoreThreshold: 0.4, maxResults: 6 });
}

// Lists cameras without asking for permission; before Start grants it, browsers hide names and ids,
// so the list is just "Default camera".
async function listCameras(selected = "") {
  const cams = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === "videoinput" && d.deviceId);
  $("camera").replaceChildren(...(cams.length ? cams.map((c, i) => option(c.deviceId, c.label || `Camera ${i + 1}`)) : [option("", "Default camera")]));
  const iphone = selected ? null : cams.find((c) => /iphone|continuity/i.test(c.label));
  if (selected || iphone) $("camera").value = selected || iphone.deviceId;
}

async function start() {
  $("go").disabled = true;
  feed("loading body and hand models...");
  if (!pose) await loadModels();
  stream?.getTracks().forEach((t) => t.stop());
  const deviceId = $("camera").value;
  stream = await navigator.mediaDevices.getUserMedia({ video: { ...(deviceId ? { deviceId: { exact: deviceId } } : {}), width: { ideal: 1280 }, height: { ideal: 720 } } });
  video.srcObject = stream;
  await video.play();
  // Permission is granted now, so the list can show real camera names.
  await listCameras(stream.getVideoTracks()[0]?.getSettings().deviceId ?? "").catch((e) => feed(`camera list failed: ${e.message ?? e}`, "warning"));
  feed("camera live. Lie down in view (or face the camera), then point at the abdomen.");
  $("go").disabled = false;
  $("go").textContent = "Switch camera";
  loop.start(); // no-op when switching cameras: one detection loop only
}

function setTracking(valid) {
  if (valid === trackingValid) return;
  trackingValid = valid;
  $("track").textContent = valid ? "body locked" : "body lost";
  $("track").className = `pill ${valid ? "on" : "warn"}`;
  send({ type: "tracking", valid });
}

function frame() {
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

    // Bounding boxes every third frame; the object under the learner's fingertip counts as held.
    if (objects && frameNo++ % 3 === 0) {
      detections = (objects.detectForVideo(video, now).detections ?? []).map((d) => ({
        label: d.categories[0]?.categoryName ?? "object",
        score: d.categories[0]?.score ?? 0,
        box: { x: d.boundingBox.originX / video.videoWidth, y: d.boundingBox.originY / video.videoHeight, w: d.boundingBox.width / video.videoWidth, h: d.boundingBox.height / video.videoHeight },
      })).filter((d) => d.label !== "person");
      const tip = hand?.[8];
      const pad = 0.04;
      const inHand = tip ? detections.find((d) => tip.x > d.box.x - pad && tip.x < d.box.x + d.box.w + pad && tip.y > d.box.y - pad && tip.y < d.box.y + d.box.h + pad) : null;
      const label = inHand?.label ?? "";
      if (label !== held) {
        held = label;
        $("held").textContent = held ? `holding ${held}` : "holding nothing";
        $("held").className = `pill ${held ? "on" : ""}`;
        const mapped = OBJECT_TO_INSTRUMENT[held];
        const available = kase?.instruments.some((i) => i.id === mapped);
        if (held) feed(`sees you holding a ${held}${mapped ? (available ? ` (as the ${nameOfInstrument(mapped)})` : " (no matching instrument in this case)") : ""}`);
        if ($("byobject").checked && mapped && available) { $("follow").checked = false; $("instrument").value = mapped; }
      }
    }

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
    lastTorso = torso;
    if (sid && now - lastFrameAt > FRAME_EVERY_MS && !sendingFrame) { lastFrameAt = now; sendFrame(); }
    frames += 1;
  }
  if (now - fpsAt > 1000) { $("fps").textContent = `${frames} fps`; frames = 0; fpsAt = now; }
}
const loop = createFrameLoop((cb) => requestAnimationFrame(cb), frame, (e) => feed(`vision frame failed: ${e?.message ?? e}`, "warning"));

// Jarvis's eyes: the composited view (camera plus overlay) with labeled boxes, every couple of seconds.
// Region boxes come from the body map (exact for this rig); object boxes come from the detector.
const frameCanvas = document.createElement("canvas");
function regionMarks(torso) {
  if (!torso) return [];
  return REGIONS.filter((r) => !allowed || allowed.has(r.id)).map((r) => {
    const pts = [[r.u[0], r.v[0]], [r.u[1], r.v[0]], [r.u[1], r.v[1]], [r.u[0], r.v[1]]].map(([u, v]) => uvToImage(torso, u, v));
    const xs = pts.map((p) => p.x), ys = pts.map((p) => p.y);
    const x = Math.max(0, Math.min(...xs)), y = Math.max(0, Math.min(...ys));
    return { label: nameOf(r.id), id: r.id, source: "scene", box: { x, y, w: Math.min(1, Math.max(...xs)) - x, h: Math.min(1, Math.max(...ys)) - y } };
  }).filter((m) => m.box.w > 0 && m.box.h > 0);
}
async function sendFrame() {
  sendingFrame = true;
  try {
    const scale = Math.min(1, 1024 / video.videoWidth);
    frameCanvas.width = Math.round(video.videoWidth * scale);
    frameCanvas.height = Math.round(video.videoHeight * scale);
    const fctx = frameCanvas.getContext("2d");
    fctx.drawImage(video, 0, 0, frameCanvas.width, frameCanvas.height);
    fctx.drawImage(canvas, 0, 0, frameCanvas.width, frameCanvas.height);
    const image = frameCanvas.toDataURL("image/jpeg", 0.7);
    const marks = [...regionMarks(lastTorso), ...detections.map((d) => ({ label: d.label, id: OBJECT_TO_INSTRUMENT[d.label] ?? "", source: "detector", box: d.box }))];
    await api("POST", `/coach/sessions/${sid}/frame`, { image, marks, source: "camera" });
  } catch (e) {
    console.warn("frame upload failed", e);
  } finally {
    sendingFrame = false;
  }
}

function draw(torso, hand, region) {
  const W = canvas.width, H = canvas.height;
  ctx.clearRect(0, 0, W, H);
  for (const d of detections) {
    ctx.strokeStyle = d.label === held ? "#fbbf24" : "#4ade80";
    ctx.lineWidth = 3;
    ctx.strokeRect(d.box.x * W, d.box.y * H, d.box.w * W, d.box.h * H);
    ctx.fillStyle = "rgba(0,0,0,.6)";
    ctx.fillRect(d.box.x * W, d.box.y * H - 22, 150, 22);
    ctx.fillStyle = d.label === held ? "#fbbf24" : "#4ade80";
    ctx.font = "16px ui-sans-serif, system-ui";
    ctx.fillText(`${d.label} ${(d.score * 100).toFixed(0)}%`, d.box.x * W + 6, d.box.y * H - 6);
  }
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
$("headset").onchange = () => {
  if (!$("headset").checked) return feed("observing only: no events or acks are sent");
  feed(loop.running ? "acting as the headset: events and highlight acks go to the coach session" : "acting as the headset once the camera starts");
  if (trackingValid !== null) send({ type: "tracking", valid: trackingValid });
};
listCameras().catch((e) => feed(`camera list failed: ${e.message ?? e}`, "warning"));
rig.attach();
