import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { OPEN_BODY } from "../src/catalog/open-appendectomy.js";
import { BodyState, bodyAction } from "../src/open-body.js";

// Grader parity with the simulated robot (services/motion scalpal_motion/mark/grader.py). The same strokes and
// facts are tested there; here the facts go through the real reducer and the catalog's mark_incision milestone,
// so a threshold change in the catalog fails this test until the robot's grader and fixture follow.
const golden = JSON.parse(readFileSync(join(import.meta.dirname, "fixtures", "robot-mark-strokes.json"), "utf8")) as {
  predicates: { tissueId: string; fact: string; op: string; value: number }[];
  cases: { name: string; expected: { markErrorMm: number; markLengthMm: number; markAngleDegrees: number }; passes: boolean; failed: string[] }[];
};

describe("robot mark_incision grader parity", () => {
  it("uses exactly the catalog's mark_incision predicates", () => {
    expect(OPEN_BODY.milestones.find((m) => m.id === "mark_incision")!.predicates).toEqual(golden.predicates);
  });

  it.each(golden.cases.map((c) => [c.name, c] as const))("%s: the reducer reaches the same verdict as the robot grader", (_name, c) => {
    const body = new BodyState(OPEN_BODY.tissues);
    const record = body.apply(bodyAction("mark", "skin", { instrumentId: "skin_marker", distanceMm: c.expected.markErrorMm, lengthMm: c.expected.markLengthMm, angleDegrees: c.expected.markAngleDegrees }));
    expect(record?.outcomes).toEqual([]);
    const predicates = OPEN_BODY.milestones.find((m) => m.id === "mark_incision")!.predicates;
    const failed = [...new Set(predicates.filter((p) => !body.test(p)).map((p) => p.fact))];
    expect(failed).toEqual(c.failed);
    expect(failed.length === 0).toBe(c.passes);
  });
});
