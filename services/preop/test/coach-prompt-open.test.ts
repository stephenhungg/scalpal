import { describe, expect, it } from "vitest";
import { buildCase } from "../src/case-builder.js";
import { PROCEDURES_BY_ID } from "../src/catalog/procedures.js";
import { buildSystemPrompt } from "../src/coach-prompt.js";
import { NOW, fixture } from "./helpers.js";

describe("coach prompt for open surgery", () => {
  const kase = buildCase(fixture("patient-demo-pediatric-asthma"), "", NOW);
  const open = PROCEDURES_BY_ID.get("open_appendectomy")!;

  it("frames the steps as an expected path judged from body state, with guardrails and the decision", () => {
    const prompt = buildSystemPrompt({ ...kase, procedure: open, procedureId: open.id });
    expect(prompt).not.toMatch(/Ports:|must complete them in this order/);
    expect(prompt).toMatch(/expected path, not a gate/);
    expect(prompt).toMatch(/Lift the peritoneum before nicking it\./);
    expect(prompt).toMatch(/Where is the true base\?/);
  });

  it("keeps the ordered port-based framing for laparoscopic cases", () => {
    expect(buildSystemPrompt(kase)).toMatch(/Ports: .*\nOrdered steps\. The learner must complete them in this order:/);
  });
});
