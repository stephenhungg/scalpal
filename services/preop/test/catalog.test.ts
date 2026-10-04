import { describe, expect, it } from "vitest";
import { buildCase, checklistFor, scorePreopCheck } from "../src/case-builder.js";
import { CASE_PLANS, fallbackPlan } from "../src/catalog/cases.js";
import { PROCEDURES } from "../src/catalog/procedures.js";
import { StepEngine, perfectEvents } from "../src/engine.js";
import { validateCatalog } from "../src/validate.js";
import { NOW, fixture } from "./helpers.js";

describe("catalog integrity", () => {
  it.each(["patient-demo-pediatric-asthma", "patient-demo-multi-source", "patient-demo-sparse"])("routes appendicitis patient %s to the open case", (id) => {
    const kase = buildCase(fixture(id), "", NOW);
    expect(kase.procedureId).toBe("open_appendectomy");
    expect(kase.procedure.firstStep).toBe("mark_incision");
    expect(kase.procedure.ports).toEqual([]);
    expect(kase.procedure.openBody?.milestones).toHaveLength(10);
    expect(kase.considerations.every(c => kase.procedure.steps.some(step => step.id === c.stepId))).toBe(true);
    expect(kase.considerations.map(c => c.note).join(" ")).not.toMatch(/insufflat|trocar|pneumoperitoneum/i);
  });
  it("uses the open approach for appendicitis fallback while retaining the advanced lap catalog", () => {
    expect(fallbackPlan(20).procedureId).toBe("open_appendectomy");
    const advanced = PROCEDURES.find(p => p.id === "lap_appendectomy")!;
    expect(advanced.ports.length).toBeGreaterThan(0);
    expect(advanced.openBody).toBeUndefined();
  });
  it("has no dangling anatomy, instrument, port, or step references", () => {
    expect(validateCatalog()).toEqual([]);
  });
});

describe("step engine", () => {
  for (const procedure of PROCEDURES) {
    it(`${procedure.id} can be completed start to finish`, () => {
      const engine = new StepEngine(procedure);
      const visited: string[] = [];
      while (!engine.completed) {
        const step = engine.current!;
        visited.push(step.id);
        let advanced = false;
        for (const event of perfectEvents(step)) advanced = engine.handle(event).advanced || advanced;
        expect(advanced, `step ${step.id} did not advance on its own perfect events`).toBe(true);
      }
      expect(visited).toEqual(procedure.steps.map((s) => s.id));
      expect(engine.mistakes).toEqual([]);
    });
  }

  it("records a mistake without advancing", () => {
    const chole = PROCEDURES.find((p) => p.id === "lap_cholecystectomy")!;
    const engine = new StepEngine(chole);
    while (engine.current?.id !== "critical_view") {
      for (const e of perfectEvents(engine.current!)) engine.handle(e);
    }
    const result = engine.handle({ type: "identify", structureId: "common_bile_duct" });
    expect(result.mistake?.id).toBe("cbd_as_cystic");
    expect(result.advanced).toBe(false);
    expect(engine.current?.id).toBe("critical_view");
  });

  it("ignores the wrong instrument", () => {
    const appy = PROCEDURES.find((p) => p.id === "lap_appendectomy")!;
    const engine = new StepEngine(appy);
    while (engine.current?.id !== "divide_mesoappendix") {
      for (const e of perfectEvents(engine.current!)) engine.handle(e);
    }
    expect(engine.handle({ type: "touch", structureId: "appendicular_artery", instrumentId: "lap_scissors" }).advanced).toBe(false);
    expect(engine.handle({ type: "touch", structureId: "appendicular_artery", instrumentId: "vessel_sealer" }).advanced).toBe(true);
  });
});

describe("cases", () => {
  for (const subject of Object.keys(CASE_PLANS)) {
    it(`${subject} produces a complete case`, () => {
      const kase = buildCase(fixture(subject), "test", NOW);
      expect(kase.procedure.id).toBe(CASE_PLANS[subject]?.procedureId);
      const anatomy = new Set(kase.anatomy.map((a) => a.id));
      for (const s of kase.procedure.structures) expect(anatomy.has(s)).toBe(true);
      for (const s of kase.brief.highlightStructures) expect(anatomy.has(s)).toBe(true);
      const instruments = new Set(kase.instruments.map((i) => i.id));
      for (const step of kase.procedure.steps) expect(instruments.has(step.instrumentId)).toBe(true);
      const stepIds = new Set(kase.procedure.steps.map((s) => s.id));
      for (const c of kase.considerations) expect(stepIds.has(c.stepId)).toBe(true);
      const offered = new Set(kase.checklistOptions.map((o) => o.type));
      for (const f of kase.brief.flags) expect(offered.has(f.type)).toBe(true);
      expect(kase.checklistOptions.length - kase.brief.flags.length).toBeGreaterThanOrEqual(2);
    });
  }

  it("scales children down", () => {
    expect(buildCase(fixture("patient-demo-pediatric-asthma"), "", NOW).bodyScale).toBeLessThan(1);
    expect(buildCase(fixture("patient-demo-001"), "", NOW).bodyScale).toBe(1);
  });

  it("attaches chart risks to the right surgical steps", () => {
    const kase = buildCase(fixture("patient-demo-polypharmacy"), "", NOW);
    const at = (stepId: string) => kase.considerations.filter((c) => c.stepId === stepId).map((c) => c.flagId);
    expect(at("liver_bed")).toContain("flag_bleeding");
    expect(at("critical_view")).toContain("flag_contrast");
    expect(at("access_umbilical")).toEqual(expect.arrayContaining(["flag_cardiac", "flag_renal"]));
  });

  it("checklists are stable per patient", () => {
    expect(checklistFor("patient-demo-001", ["allergy"])).toEqual(checklistFor("patient-demo-001", ["allergy"]));
  });

  it("scores the pre-op safety check", () => {
    const kase = buildCase(fixture("patient-demo-001"), "", NOW);
    const perfect = scorePreopCheck(kase, ["allergy", "diabetes"]);
    expect(perfect).toMatchObject({ score: 2, total: 2, passed: true });
    const sloppy = scorePreopCheck(kase, ["allergy", kase.checklistOptions.find((o) => o.type !== "allergy" && o.type !== "diabetes")!.type]);
    expect(sloppy.passed).toBe(false);
    expect(sloppy.missed.map((m) => m.type)).toEqual(["diabetes"]);
    expect(sloppy.falseAlarms).toHaveLength(1);
    expect(scorePreopCheck(kase, ["not_a_real_type"]).falseAlarms).toEqual([]);
  });
});
