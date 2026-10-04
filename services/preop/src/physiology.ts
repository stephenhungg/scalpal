// Typed mirror of services/vitals/src/physiology.mjs (Silas): OR monitor values = baseline (measured by
// Presage in AR, charted in VR) + a simulated hemorrhage delta following ATLS classes. Authored teaching
// content, not a validated clinical simulation. test/physiology-parity.test.ts checks this file against
// the .mjs on a grid of inputs, so the coach, the vitals service and Unity stay identical.

export interface Baseline {
  hr: number;
  rr: number;
  sys: number;
  dia: number;
  source?: string; // "measured" (Presage, AR), "demo", "chart" (VR), "authored"
}

export interface MonitorVitals {
  hr: number;
  rr: number;
  sys: number;
  dia: number;
  hemorrhageClass: 1 | 2 | 3 | 4;
  bloodLossPct: number;
  baseline: { hr: number; rr: number; source: string };
  label: string;
}

export const AUTHORED_BASELINE: Baseline = { hr: 72, rr: 14, sys: 118, dia: 76, source: "authored" };
const EBV_ML_PER_KG = 70;
const BLEED_LOOKAHEAD_MIN = 0.5; // an active bleed counts as this many minutes of extra loss

const lerp = (a: number, b: number, t: number) => a + (b - a) * Math.max(0, Math.min(1, t));

export function hemorrhageClass(lossPct: number): 1 | 2 | 3 | 4 {
  if (lossPct < 15) return 1;
  if (lossPct < 30) return 2;
  if (lossPct <= 40) return 3;
  return 4;
}

export function monitorVitals({
  baseline = AUTHORED_BASELINE,
  weightKg = 70,
  bloodLostMl = 0,
  bleedMlPerMin = 0,
  criticalInjury = false,
}: { baseline?: Baseline; weightKg?: number; bloodLostMl?: number; bleedMlPerMin?: number; criticalInjury?: boolean } = {}): MonitorVitals {
  const ebv = EBV_ML_PER_KG * weightKg;
  const effective = bloodLostMl + Math.max(0, bleedMlPerMin) * BLEED_LOOKAHEAD_MIN;
  const pct = (effective / ebv) * 100;
  const cls = hemorrhageClass(pct);
  const b = baseline;

  let hr: number, rr: number, sys: number, dia: number;
  if (cls === 1) {
    hr = b.hr + (pct / 15) * 10;
    rr = b.rr;
    sys = b.sys;
    dia = b.dia;
  } else if (cls === 2) {
    const t = (pct - 15) / 15;
    hr = lerp(100, 120, t);
    rr = lerp(20, 30, t);
    sys = b.sys - 5 * t;
    dia = b.dia + 5 * t;
  } else if (cls === 3) {
    const t = (pct - 30) / 10;
    hr = lerp(120, 140, t);
    rr = lerp(30, 40, t);
    sys = lerp(b.sys - 10, 88, t);
    dia = lerp(b.dia, 60, t);
  } else {
    const t = Math.min(1, (pct - 40) / 15);
    hr = lerp(140, 160, t);
    rr = lerp(36, 42, t);
    sys = lerp(85, 65, t);
    dia = lerp(58, 45, t);
  }
  hr = Math.max(hr, b.hr);
  rr = Math.max(rr, b.rr);
  if (criticalInjury) {
    hr += 15;
    rr += 4;
  }
  const round = (v: number) => Math.round(v);
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
