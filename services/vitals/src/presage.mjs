// Live source: Presage SmartSpectra on a fixed camera (ideally a phone on a stand via Continuity
// Camera). Processing is on-device; no footage is stored by Scalpal.
import { CameraSelection, SmartSpectraSDK, ValidationCode, breathingMetrics, cardioMetrics } from "@smartspectra/node-sdk";
import { decodeMetrics } from "@smartspectra/node-sdk/messages";

// Presage's own "stable" thresholds: heart rate >= 40 (+/-3 bpm), breathing >= 45 (+/-1 br/min).
const STABLE = { pulse: 40, breathing: 45 };
const TRACE_KEEP = 300; // samples kept per waveform

const REASONS = Object.fromEntries(Object.entries(ValidationCode).map(([k, v]) => [v, k.replace(/^k/, "")]));

const latest = (list) => (Array.isArray(list) && list.length ? list[list.length - 1] : null);
const num = (v) => (v == null ? null : typeof v === "number" ? v : Number(v));

export function startPresage({ apiKey, cameraId, onUpdate, log = console }) {
  const state = {
    pulse: { bpm: null, confidence: null, stable: false, t: null },
    breathing: { bpm: null, confidence: null, stable: false, t: null },
    traces: { pulse: [], breathing: [] },
    status: { ok: false, reason: "Starting", code: null },
  };
  const push = (arr, samples) => {
    for (const s of samples ?? []) {
      if (s?.value == null) continue;
      arr.push([num(s.timestamp), s.value]);
    }
    if (arr.length > TRACE_KEEP) arr.splice(0, arr.length - TRACE_KEEP);
  };

  const sdk = new SmartSpectraSDK({ apiKey, requestedMetrics: [...breathingMetrics, ...cardioMetrics] });

  sdk.on("validationStatus", (code, _ts, hint) => {
    state.status = { ok: code === ValidationCode.kOk, code, reason: code === ValidationCode.kOk ? "Measuring" : hint || REASONS[code] || "Adjusting" };
    onUpdate(state);
  });

  sdk.on("metrics", (buf) => {
    const m = decodeMetrics(buf);
    const hr = latest(m.cardio?.pulseRate);
    const br = latest(m.breathing?.rate);
    if (hr) state.pulse = { bpm: hr.value, confidence: hr.confidence, stable: (hr.confidence ?? 0) >= STABLE.pulse, t: num(hr.timestamp) };
    if (br) state.breathing = { bpm: br.value, confidence: br.confidence, stable: (br.confidence ?? 0) >= STABLE.breathing, t: num(br.timestamp) };
    push(state.traces.pulse, m.cardio?.arterialPressureTrace);
    push(state.traces.breathing, m.breathing?.upperTrace);
    onUpdate(state);
  });

  sdk.on("error", (code, message, retryable) => {
    log.error(`[presage] error ${code}: ${message}${retryable ? " (retryable)" : ""}`);
    state.status = { ok: false, code, reason: `Error: ${message}` };
    onUpdate(state);
  });

  sdk.useCamera(cameraId ? CameraSelection.byId(cameraId) : CameraSelection.default);
  sdk.start();
  log.log(`[presage] started on ${cameraId ? `camera ${cameraId}` : "the default camera"}`);
  return { stop: () => sdk.stop?.() };
}
