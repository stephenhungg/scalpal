import { AUTHORED_BASELINE, monitorVitals, type Baseline, type MonitorVitals } from "./physiology.js";

// The simulated patient's condition in the operating room (docs/operation-flow.md, "Vitals" and "Body
// model"): blood loss from the open-body reducer plus coarse injuries outside the surgical field, turned
// into monitor vitals by the shared physiology model, and the case outcome when the patient dies.
//
// Demo acceleration: blood loss and bleed rates are multiplied by DEMO_HEMORRHAGE_SCALE before the
// physiology model, so an uncontrolled bleed becomes dangerous in about 30 to 90 s instead of many
// minutes. Raw milliliters are reported unchanged next to the simulated loss percentage.
//
// Honesty: the baseline may be the real volunteer's (Presage, AR). The volunteer's real vitals never
// react to the virtual surgery; every value derived here is labeled simulated.

export const DEMO_HEMORRHAGE_SCALE = 8;
export const DEATH_LOSS_PCT = 50; // simulated loss of blood volume at which the patient dies

export const REGIONS = {
  head: { label: "head", rawBleedMlPerMin: 0, critical: true, catastrophic: true, alarm: "What are you doing? That's the patient's head!" },
  neck: { label: "neck", rawBleedMlPerMin: 300, critical: true, catastrophic: false, alarm: "Stop! That's the patient's neck. You've opened a major vessel." },
  chest: { label: "chest", rawBleedMlPerMin: 150, critical: true, catastrophic: false, alarm: "Stop. That's the chest, not the surgical field." },
  left_arm: { label: "left arm", rawBleedMlPerMin: 20, critical: false, catastrophic: false, alarm: "Careful, that's the left arm. The field is the lower right abdomen." },
  right_arm: { label: "right arm", rawBleedMlPerMin: 20, critical: false, catastrophic: false, alarm: "Careful, that's the right arm. The field is the lower right abdomen." },
  left_leg: { label: "left leg", rawBleedMlPerMin: 20, critical: false, catastrophic: false, alarm: "Careful, that's the left leg. The field is the lower right abdomen." },
  right_leg: { label: "right leg", rawBleedMlPerMin: 20, critical: false, catastrophic: false, alarm: "Careful, that's the right leg. The field is the lower right abdomen." },
} as const;
export type RegionId = keyof typeof REGIONS;
export const REGION_IDS = Object.keys(REGIONS) as RegionId[];

export const CLASS_LINES: Record<2 | 3 | 4, string> = {
  2: "Heart rate is climbing. She's losing blood. Find the bleeder.",
  3: "Pressure is dropping. Control the bleeding now.",
  4: "She's crashing. Stop everything and control the bleeding.",
};
export const DEATH_LINE = "We've lost the patient.";

export interface RegionInjury {
  region: RegionId;
  label: string;
  bleeding: boolean;
  rawBleedMlPerMin: number;
  at: number; // ms on the coach clock
}

export interface ConditionView {
  vitals: MonitorVitals & { spo2: number; simulated: true; scale: number }; // spo2 -1: not charted (Unity JSON has no null)
  baselineSource: string;
  weightKg: number;
  rawBloodLossMl: number; // body reducer plus regions, unscaled
  regions: RegionInjury[];
  outcome: { result: "in_progress" | "completed" | "died"; cause: string; at: string };
}

export type ConditionChange = { kind: "class"; from: number; to: 1 | 2 | 3 | 4 } | { kind: "died"; cause: string };

export class PatientCondition {
  private baseline: Baseline = AUTHORED_BASELINE;
  private spo2Baseline: number | null = null;
  private weightKg = 70;
  private regions = new Map<RegionId, RegionInjury>();
  private regionLostMl = 0;
  private lastMs: number | null = null; // set on first use: the owner's clock may not exist yet at construction
  private lastClass: 1 | 2 | 3 | 4 = 1;
  private outcome: ConditionView["outcome"] = { result: "in_progress", cause: "", at: "" };
  private body = { lostMl: 0, bleedMlPerMin: 0 };

  constructor(
    private readonly clock: () => number,
    private readonly scale = DEMO_HEMORRHAGE_SCALE,
  ) {}

  get died() {
    return this.outcome.result === "died";
  }

  setBaseline(baseline: Baseline, opts: { weightKg?: number; spo2?: number | null } = {}) {
    this.baseline = baseline;
    if (opts.weightKg && opts.weightKg > 0) this.weightKg = opts.weightKg;
    if (opts.spo2 !== undefined) this.spo2Baseline = opts.spo2;
  }

  // Latest blood loss and active bleed rate from the body reducer (or the laparoscopic bleeding events).
  setBodyBleeding(lostMl: number, bleedMlPerMin: number) {
    this.body = { lostMl: Math.max(0, lostMl), bleedMlPerMin: Math.max(0, bleedMlPerMin) };
  }

  markCompleted() {
    if (this.outcome.result === "in_progress") this.outcome = { result: "completed", cause: "", at: new Date(this.clock()).toISOString() };
  }

  // A cutting tool hit a region outside the surgical field. Returns false when the region was already injured.
  injure(region: RegionId): { first: boolean; catastrophic: boolean } {
    this.advance();
    const def = REGIONS[region];
    const existing = this.regions.get(region);
    if (existing) {
      if (!existing.bleeding && def.rawBleedMlPerMin > 0) existing.bleeding = true;
      return { first: false, catastrophic: def.catastrophic };
    }
    this.regions.set(region, { region, label: def.label, bleeding: def.rawBleedMlPerMin > 0, rawBleedMlPerMin: def.rawBleedMlPerMin, at: this.clock() });
    return { first: true, catastrophic: def.catastrophic };
  }

  control(region: RegionId): boolean {
    this.advance();
    const r = this.regions.get(region);
    if (!r?.bleeding) return false;
    r.bleeding = false;
    return true;
  }

  private regionRate() {
    let rate = 0;
    for (const r of this.regions.values()) if (r.bleeding) rate += r.rawBleedMlPerMin;
    return rate;
  }

  // Region bleeding accumulates on the coach clock (regions are not in the body reducer).
  private advance() {
    const now = this.clock();
    const dtMin = this.lastMs == null ? 0 : Math.max(0, now - this.lastMs) / 60000;
    this.lastMs = now;
    if (!this.died) this.regionLostMl += this.regionRate() * dtMin;
  }

  private compute(): ConditionView["vitals"] {
    const critical = [...this.regions.values()].some((r) => REGIONS[r.region].critical);
    const v = monitorVitals({
      baseline: this.baseline,
      weightKg: this.weightKg,
      bloodLostMl: (this.body.lostMl + this.regionLostMl) * this.scale,
      bleedMlPerMin: (this.body.bleedMlPerMin + this.regionRate()) * this.scale,
      criticalInjury: critical,
    });
    // SpO2 is not in the shared model; it is shown as charted and only falls in class 4.
    const spo2 = this.spo2Baseline == null ? -1 : v.hemorrhageClass === 4 ? Math.max(80, this.spo2Baseline - Math.round((v.bloodLossPct - 40) / 2)) : this.spo2Baseline;
    if (this.died) return { ...v, hr: 0, rr: 0, sys: 0, dia: 0, spo2: spo2 < 0 ? -1 : 0, simulated: true, scale: this.scale, label: "Asystole (simulated)" };
    return { ...v, spo2, simulated: true, scale: this.scale };
  }

  // Re-evaluates the condition; returns class changes and death since the last call.
  update(): ConditionChange[] {
    if (this.outcome.result !== "in_progress") return [];
    this.advance();
    const changes: ConditionChange[] = [];
    const catastrophic = [...this.regions.values()].find((r) => REGIONS[r.region].catastrophic);
    const v = this.compute();
    if (v.hemorrhageClass !== this.lastClass) {
      if (v.hemorrhageClass > this.lastClass) changes.push({ kind: "class", from: this.lastClass, to: v.hemorrhageClass });
      this.lastClass = v.hemorrhageClass;
    }
    const cause = catastrophic ? `catastrophic injury to the ${catastrophic.label}` : v.bloodLossPct >= DEATH_LOSS_PCT ? `hemorrhage (${Math.round(v.bloodLossPct)}% of blood volume, simulated)` : "";
    if (cause) {
      this.outcome = { result: "died", cause, at: new Date(this.clock()).toISOString() };
      changes.push({ kind: "died", cause });
    }
    return changes;
  }

  view(): ConditionView {
    return {
      vitals: this.compute(),
      baselineSource: this.baseline.source ?? "authored",
      weightKg: this.weightKg,
      rawBloodLossMl: Math.round(this.body.lostMl + this.regionLostMl),
      regions: [...this.regions.values()].map((r) => ({ ...r })),
      outcome: { ...this.outcome },
    };
  }
}
