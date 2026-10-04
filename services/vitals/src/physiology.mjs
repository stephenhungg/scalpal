// OR monitor physiology: displayed vitals = measured baseline (Presage, at Time-Out) + simulated
// delta from the open-body state. A pure function of logged state, so Unity, the coach and the
// recap can reproduce it exactly. Authored teaching content following ATLS hemorrhage classes,
// not a validated clinical simulation.

export const AUTHORED_BASELINE = { hr: 72, rr: 14, sys: 118, dia: 76, source: "authored" };
const EBV_ML_PER_KG = 70;
const BLEED_LOOKAHEAD_MIN = 0.5; // an active bleed counts as this many minutes of extra loss

const lerp = (a, b, t) => a + (b - a) * Math.max(0, Math.min(1, t));

/** ATLS class 1-4 for a blood loss given as a percentage of estimated blood volume. */
export function hemorrhageClass(lossPct) {
  if (lossPct < 15) return 1;
  if (lossPct < 30) return 2;
  if (lossPct <= 40) return 3;
  return 4;
}

/**
 * @param {object} p
 * @param {{hr:number, rr:number, sys:number, dia:number, source?:string}} [p.baseline]
 * @param {number} [p.weightKg] chart weight if known, else 70
 * @param {number} [p.bloodLostMl]
 * @param {number} [p.bleedMlPerMin] current uncontrolled bleeding rate
 * @param {boolean} [p.criticalInjury] acute spike
 */
export function monitorVitals({ baseline = AUTHORED_BASELINE, weightKg = 70, bloodLostMl = 0, bleedMlPerMin = 0, criticalInjury = false } = {}) {
  const ebv = EBV_ML_PER_KG * weightKg;
  const effective = bloodLostMl + Math.max(0, bleedMlPerMin) * BLEED_LOOKAHEAD_MIN;
  const pct = (effective / ebv) * 100;
  const cls = hemorrhageClass(pct);
  const b = baseline;

  let hr, rr, sys, dia;
  if (cls === 1) {
    hr = b.hr + (pct / 15) * 10;
    rr = b.rr;
    sys = b.sys;
    dia = b.dia;
  } else if (cls === 2) {
    const t = (pct - 15) / 15;
    hr = lerp(100, 120, t);
    rr = lerp(20, 30, t);
    sys = b.sys - 5 * t; // narrowing pulse pressure
    dia = b.dia + 5 * t;
  } else if (cls === 3) {
    const t = (pct - 30) / 10;
    hr = lerp(120, 140, t);
    rr = lerp(30, 40, t);
    sys = lerp(b.sys - 10, 88, t); // falling
    dia = lerp(b.dia, 60, t);
  } else {
    const t = Math.min(1, (pct - 40) / 15);
    hr = lerp(140, 160, t);
    rr = lerp(36, 42, t);
    sys = lerp(85, 65, t); // crashing
    dia = lerp(58, 45, t);
  }
  hr = Math.max(hr, b.hr);
  rr = Math.max(rr, b.rr);
  if (criticalInjury) {
    hr += 15;
    rr += 4;
  }
  const round = (v) => Math.round(v);
  return {
    hr: round(hr),
    rr: round(rr),
    sys: round(sys),
    dia: round(dia),
    hemorrhageClass: cls,
    bloodLossPct: Math.round(pct * 10) / 10,
    baseline: { hr: b.hr, rr: b.rr, source: b.source ?? "authored" },
    label: `HR ${round(hr)} · simulated from baseline ${b.hr} (${b.source ?? "authored"})`,
  };
}

/**
 * Exponential smoothing toward a target so values recover over ~10-30 s (tau seconds).
 * Keeps fractional values (rounding every step would stall the last few beats); round for display.
 */
export function smooth(prev, target, dtSec, tauSec = 15) {
  if (!prev) return target;
  const k = 1 - Math.exp(-dtSec / tauSec);
  const out = { ...target };
  for (const f of ["hr", "rr", "sys", "dia"]) out[f] = prev[f] + (target[f] - prev[f]) * k;
  return out;
}

/** Whole-number values for the monitor. */
export const display = (v) => ({ ...v, hr: Math.round(v.hr), rr: Math.round(v.rr), sys: Math.round(v.sys), dia: Math.round(v.dia) });

/** Time-Out baseline from recent stable readings: median of each, or the authored baseline. */
export function baselineFrom(samples, { minSamples = 5, mode = "live" } = {}) {
  const med = (xs) => {
    const s = [...xs].sort((a, b) => a - b);
    return s.length ? s[Math.floor(s.length / 2)] : null;
  };
  const hrs = samples.map((s) => s.hr).filter((v) => v != null);
  const rrs = samples.map((s) => s.rr).filter((v) => v != null);
  if (hrs.length < minSamples || rrs.length < minSamples) return { ...AUTHORED_BASELINE, note: "not enough confident readings" };
  return {
    ...AUTHORED_BASELINE,
    hr: Math.round(med(hrs)),
    rr: Math.round(med(rrs)),
    source: mode === "live" ? "measured" : "demo",
  };
}
