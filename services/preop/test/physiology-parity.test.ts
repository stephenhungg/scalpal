import { describe, expect, it } from "vitest";
import { hemorrhageClass, monitorVitals } from "../src/physiology.js";
// @ts-expect-error plain JavaScript module from the vitals service, mirrored by src/physiology.ts
import * as mjs from "../../vitals/src/physiology.mjs";

describe("physiology mirror", () => {
  it("matches services/vitals physiology.mjs on a grid of inputs", () => {
    const baselines = [undefined, { hr: 64, rr: 12, sys: 124, dia: 80, source: "measured" }, { hr: 98, rr: 22, sys: 104, dia: 62, source: "chart" }];
    for (const baseline of baselines)
      for (const weightKg of [20, 61, 90])
        for (const bloodLostMl of [0, 300, 900, 1600, 2400, 4000])
          for (const bleedMlPerMin of [0, 120, 900])
            for (const criticalInjury of [false, true]) {
              const input = { baseline, weightKg, bloodLostMl, bleedMlPerMin, criticalInjury };
              expect(monitorVitals(input)).toEqual(mjs.monitorVitals(input));
            }
    for (const pct of [0, 14.9, 15, 29.9, 30, 40, 40.1, 80]) expect(hemorrhageClass(pct)).toBe(mjs.hemorrhageClass(pct));
  });
});
