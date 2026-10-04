import { describe, expect, it } from "vitest";
import { PatientCondition, REGIONS as COACH_REGIONS, DEATH_LOSS_PCT, DEMO_HEMORRHAGE_SCALE, type RegionId } from "../src/patient-condition.js";
import { monitorVitals } from "../src/physiology.js";
import * as moduleModel from "../../realtime/src/physiology.js";

// The SpacetimeDB module advances the patient with its own copy of the physiology
// (services/realtime/src/physiology.ts). The headset monitor, Jarvis and the dashboard read whichever copy
// is live, so the module must produce the same numbers the coach would for the same facts; otherwise the
// monitor would jump when the coach falls back to its local model.
describe("SpacetimeDB module physiology parity", () => {
  it("monitorVitals matches the coach's on a grid of inputs", () => {
    const baselines = [undefined, { hr: 64, rr: 12, sys: 124, dia: 80, source: "measured" }, { hr: 98, rr: 22, sys: 104, dia: 62, source: "chart" }];
    for (const baseline of baselines)
      for (const weightKg of [20, 61, 90])
        for (const bloodLostMl of [0, 300, 900, 1600, 2400, 4000])
          for (const bleedMlPerMin of [0, 120, 900])
            for (const criticalInjury of [false, true]) {
              const input = { baseline, weightKg, bloodLostMl, bleedMlPerMin, criticalInjury };
              expect(moduleModel.monitorVitals(input)).toEqual(monitorVitals(input));
            }
  });

  it("shares the demo constants and the region table", () => {
    expect(moduleModel.DEMO_HEMORRHAGE_SCALE).toBe(DEMO_HEMORRHAGE_SCALE);
    expect(moduleModel.DEATH_LOSS_PCT).toBe(DEATH_LOSS_PCT);
    for (const id of Object.keys(COACH_REGIONS) as RegionId[]) {
      const { label, rawBleedMlPerMin, critical, catastrophic } = COACH_REGIONS[id];
      expect(moduleModel.REGIONS[id]).toEqual({ label, rawBleedMlPerMin, critical, catastrophic });
    }
  });

  // Same facts into the coach's PatientCondition and the module's conditionVitals/deathCause.
  const scenarios: { name: string; minutes: number; bodyLost: number; bodyRate: number; regions: RegionId[]; spo2: number | null; weightKg: number; mlPerKg: number }[] = [
    { name: "arterial bleed, early", minutes: 0, bodyLost: 20, bodyRate: 135, regions: [], spo2: 98, weightKg: 70, mlPerKg: 70 },
    { name: "arterial bleed, class 3", minutes: 0, bodyLost: 160, bodyRate: 135, regions: [], spo2: 98, weightKg: 70, mlPerKg: 70 },
    { name: "child, class 4", minutes: 0, bodyLost: 140, bodyRate: 40, regions: [], spo2: 99, weightKg: 22, mlPerKg: 80 },
    { name: "neck cut for 20 s", minutes: 1 / 3, bodyLost: 0, bodyRate: 0, regions: ["neck"], spo2: null, weightKg: 70, mlPerKg: 70 },
    { name: "arm cut plus bleed", minutes: 0.5, bodyLost: 50, bodyRate: 60, regions: ["left_arm"], spo2: 97, weightKg: 85, mlPerKg: 70 },
    { name: "exsanguinated", minutes: 0, bodyLost: 320, bodyRate: 0, regions: [], spo2: 98, weightKg: 70, mlPerKg: 70 },
    { name: "head", minutes: 0, bodyLost: 0, bodyRate: 0, regions: ["head"], spo2: 98, weightKg: 70, mlPerKg: 70 },
  ];
  for (const sc of scenarios) {
    it(`vitals and death match the coach: ${sc.name}`, () => {
      let now = 0;
      const coach = new PatientCondition(() => now);
      const baseline = { hr: 72, rr: 14, sys: 118, dia: 76, source: "chart" };
      coach.setBaseline(baseline, { weightKg: sc.weightKg, spo2: sc.spo2, mlPerKg: sc.mlPerKg });
      coach.update(); // starts the coach clock
      for (const r of sc.regions) coach.injure(r);
      now += sc.minutes * 60_000;
      coach.setBodyBleeding(sc.bodyLost, sc.bodyRate);
      coach.update();
      const view = coach.view();
      const inputs = {
        baseline,
        spo2Baseline: sc.spo2,
        weightKg: sc.weightKg,
        mlPerKg: sc.mlPerKg,
        bodyLostMl: sc.bodyLost,
        bodyBleedMlPerMin: sc.bodyRate,
        regionLostMl: sc.regions.reduce((sum, r) => sum + moduleModel.REGIONS[r].rawBleedMlPerMin, 0) * sc.minutes,
        regions: sc.regions.map((region) => ({ region, bleeding: moduleModel.REGIONS[region].rawBleedMlPerMin > 0 })),
        died: false,
      };
      const cause = moduleModel.deathCause(inputs);
      expect(cause).toBe(view.outcome.result === "died" ? view.outcome.cause : "");
      const { simulated: _s, scale: _k, ...coachVitals } = view.vitals;
      expect(moduleModel.conditionVitals({ ...inputs, died: Boolean(cause) })).toEqual(coachVitals);
    });
  }
});
