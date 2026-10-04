import { describe, expect, it } from "vitest";
import { ADVANCED_LAP_CASE_ID, withOfflineAdvancedCase } from "../scripts/offline-advanced-case.js";
import { createApp } from "../src/app.js";
import type { SurgicalCase } from "../src/types.js";
import { fixtureClient, NOW } from "./helpers.js";

describe("advanced offline laparoscopic case", () => {
  it("adds the explicit port-scene variant without changing live patients or cases", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW });
    const live = await (await app.request("/unity/bundle")).json() as { cases: SurgicalCase[]; patients: unknown[] };
    const before = structuredClone(live);
    const exported = withOfflineAdvancedCase(live);
    const open = exported.cases.find(kase => kase.caseId === "case_patient-demo-multi-source_open_appendectomy")!;
    const lap = exported.cases.find(kase => kase.caseId === ADVANCED_LAP_CASE_ID)!;
    expect(live).toEqual(before);
    expect(live.cases.some(kase => kase.caseId === ADVANCED_LAP_CASE_ID)).toBe(false);
    expect(exported.patients).toEqual(live.patients);
    expect(exported.cases).toHaveLength(live.cases.length + 1);
    expect(open.procedure.openBody).toBeDefined();
    expect(lap.procedureId).toBe("lap_appendectomy");
    expect(lap.procedure.openBody).toBeUndefined();
    expect(lap.procedure.ports.length).toBeGreaterThan(0);
    expect(lap.presentation).toContain("Advanced offline laparoscopic variant");
    expect(lap.actions.find(action => action.route.startsWith("/procedures/"))?.route).toBe("/procedures/lap_appendectomy");
    expect(lap.brief).toEqual(open.brief);
    expect(lap.brief).not.toBe(open.brief);
    const instruments = new Set(lap.instruments.map(item => item.id));
    for (const step of lap.procedure.steps) expect(instruments.has(step.instrumentId)).toBe(true);
    for (const port of lap.procedure.ports)
      for (const id of port.instrumentIds) expect(instruments.has(id)).toBe(true);
    const steps = new Set(lap.procedure.steps.map(step => step.id));
    for (const consideration of lap.considerations) expect(steps.has(consideration.stepId)).toBe(true);
    expect(withOfflineAdvancedCase(exported)).toBe(exported);
    const routed = await (await app.request("/patients/patient-demo-multi-source/case")).json() as SurgicalCase;
    expect(routed.procedureId).toBe("open_appendectomy");
  });

  it("fails export explicitly if the source case is absent", () => {
    expect(() => withOfflineAdvancedCase({ cases: [] })).toThrow("multi-source open appendectomy");
  });
});
