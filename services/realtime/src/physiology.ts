// Server-side copy of the simulated patient's physiology, so the database itself advances the patient.
//
// Ported from services/preop/src/physiology.ts (itself a mirror of services/vitals/src/physiology.mjs) and
// services/preop/src/patient-condition.ts. Pure functions, no SpacetimeDB imports, so the coach's vitest
// suite pins parity (services/preop/test/realtime-physiology-parity.test.ts). Change all copies together.
//
// Authored teaching content following ATLS hemorrhage classes, not a validated clinical simulation.
// Every value derived here is simulated, even when the baseline was measured from a real volunteer.

export interface Baseline {
  hr: number;
  rr: number;
  sys: number;
  dia: number;
  source?: string;
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

export const AUTHORED_BASELINE: Baseline = { hr: 72, rr: 14, sys: 118, dia: 76, source: 'authored' };
const EBV_ML_PER_KG = 70;
const BLEED_LOOKAHEAD_MIN = 0.5; // an active bleed counts as this many minutes of extra loss

/** Blood loss and bleed rates are multiplied by this before the model (demo acceleration). */
export const DEMO_HEMORRHAGE_SCALE = 8;
/** Simulated loss of blood volume at which the patient dies. */
export const DEATH_LOSS_PCT = 50;

export const REGIONS = {
  head: { label: 'head', rawBleedMlPerMin: 0, critical: true, catastrophic: true },
  neck: { label: 'neck', rawBleedMlPerMin: 300, critical: true, catastrophic: false },
  chest: { label: 'chest', rawBleedMlPerMin: 150, critical: true, catastrophic: false },
  left_arm: { label: 'left arm', rawBleedMlPerMin: 20, critical: false, catastrophic: false },
  right_arm: { label: 'right arm', rawBleedMlPerMin: 20, critical: false, catastrophic: false },
  left_leg: { label: 'left leg', rawBleedMlPerMin: 20, critical: false, catastrophic: false },
  right_leg: { label: 'right leg', rawBleedMlPerMin: 20, critical: false, catastrophic: false },
} as const;
export type RegionId = keyof typeof REGIONS;
export const REGION_IDS = Object.keys(REGIONS) as RegionId[];

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
    baseline: { hr: b.hr, rr: b.rr, source: b.source ?? 'authored' },
    label: `HR ${round(hr)} · simulated from baseline ${b.hr} (${b.source ?? 'authored'})`,
  };
}

export interface RegionState {
  region: RegionId;
  bleeding: boolean;
}

/** Everything the condition's vitals depend on (raw, unscaled milliliters). */
export interface ConditionInputs {
  baseline: Baseline;
  spo2Baseline: number | null; // null: not charted
  weightKg: number;
  mlPerKg: number;
  bodyLostMl: number;
  bodyBleedMlPerMin: number;
  regionLostMl: number;
  regions: RegionState[];
  died: boolean;
  scale?: number;
}

export interface ConditionVitals extends MonitorVitals {
  spo2: number; // -1: not charted
}

export function regionRate(regions: RegionState[]): number {
  let rate = 0;
  for (const r of regions) if (r.bleeding) rate += REGIONS[r.region].rawBleedMlPerMin;
  return rate;
}

/** Same as PatientCondition.compute() in services/preop/src/patient-condition.ts. */
export function conditionVitals(i: ConditionInputs): ConditionVitals {
  const scale = i.scale ?? DEMO_HEMORRHAGE_SCALE;
  const critical = i.regions.some(r => REGIONS[r.region].critical);
  const v = monitorVitals({
    baseline: i.baseline,
    weightKg: (i.weightKg * i.mlPerKg) / 70, // the shared model assumes 70 ml/kg
    bloodLostMl: (i.bodyLostMl + i.regionLostMl) * scale,
    bleedMlPerMin: (i.bodyBleedMlPerMin + regionRate(i.regions)) * scale,
    criticalInjury: critical,
  });
  const spo2 =
    i.spo2Baseline == null
      ? -1
      : v.hemorrhageClass === 4
        ? Math.max(80, i.spo2Baseline - Math.round((v.bloodLossPct - 40) / 2))
        : i.spo2Baseline;
  if (i.died) return { ...v, hr: 0, rr: 0, sys: 0, dia: 0, spo2: spo2 < 0 ? -1 : 0, label: 'Asystole (simulated)' };
  return { ...v, spo2 };
}

/**
 * Death check of PatientCondition.update(): a catastrophic region, or the blood actually lost (not the
 * look-ahead for active bleeding) reaching DEATH_LOSS_PCT. Returns the cause, or '' while alive.
 */
export function deathCause(i: ConditionInputs): string {
  const scale = i.scale ?? DEMO_HEMORRHAGE_SCALE;
  const catastrophic = i.regions.find(r => REGIONS[r.region].catastrophic);
  if (catastrophic) return `catastrophic injury to the ${REGIONS[catastrophic.region].label}`;
  const lostPct = (((i.bodyLostMl + i.regionLostMl) * scale) / (i.mlPerKg * i.weightKg)) * 100;
  return lostPct >= DEATH_LOSS_PCT ? `hemorrhage (${Math.round(lostPct)}% of blood volume lost, simulated)` : '';
}
