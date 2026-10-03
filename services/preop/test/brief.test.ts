import { describe, expect, it } from "vitest";
import { buildBrief, labDirection, parseRange } from "../src/brief.js";
import { NOW, fixture } from "./helpers.js";

const types = (subject: string) => buildBrief(fixture(subject), NOW).flags.map((f) => f.type).sort();
const gapCodes = (subject: string) => buildBrief(fixture(subject), NOW).dataGaps.map((g) => g.code);

describe("parseRange", () => {
  it("reads every range format FinchNode uses", () => {
    expect(parseRange("0.6 - 1.2 mg/dL")).toEqual({ low: 0.6, high: 1.2 });
    expect(parseRange("Synthetic reference: 70–99 mg/dL")).toEqual({ low: 70, high: 99 });
    expect(parseRange(">= 60 mL/min/{1.73_m2}")).toEqual({ low: 60, high: null });
    expect(parseRange("<= 5.6 %")).toEqual({ low: null, high: 5.6 });
    expect(parseRange("Synthetic reference: below 5.7%")).toEqual({ low: null, high: 5.7 });
    expect(parseRange(null)).toEqual({ low: null, high: null });
  });

  it("prefers the explicit interpretation over the range", () => {
    expect(labDirection({ value: "1", referenceRange: "2 - 3", interpretation: "N" })).toBe("normal");
    expect(labDirection({ value: "1", referenceRange: "2 - 3" })).toBe("low");
    expect(labDirection({ value: null, referenceRange: "2 - 3" })).toBe("unknown");
  });
});

describe("buildBrief", () => {
  it("flags the polypharmacy senior's real surgical risks with evidence", () => {
    const brief = buildBrief(fixture("patient-demo-polypharmacy"), NOW);
    expect(brief.flags.map((f) => f.type).sort()).toEqual(
      ["allergy", "anemia", "bleeding", "cardiac", "contrast", "diabetes", "elderly", "metformin_renal", "polypharmacy", "renal"].sort(),
    );
    const bleeding = brief.flags.find((f) => f.type === "bleeding");
    expect(bleeding?.severity).toBe("high");
    expect(bleeding?.evidence.map((e) => e.label.toLowerCase()).join(" ")).toMatch(/apixaban.*aspirin/);
    const renal = brief.flags.find((f) => f.type === "renal");
    expect(renal?.title).toContain("31");
    expect(renal?.structures).toEqual(["right_kidney", "left_kidney"]);
    expect(brief.highlightStructures).toEqual(expect.arrayContaining(["heart", "right_kidney", "left_kidney", "pancreas"]));
    expect(brief.patient).toMatchObject({ name: "Harriet Lindqvist", age: 78, sex: "female" });
    expect(brief.flags[0]?.severity).toBe("high");
    expect(brief.say).toMatch(/^Harriet Lindqvist, 78\. Four high priority/);
  });

  it("keeps the baseline adult mild", () => {
    expect(types("patient-demo-001")).toEqual(["allergy", "diabetes"]);
  });

  it("separates latex and severe allergies, and ignores 'no known allergy'", () => {
    expect(types("patient-demo-multi-source")).toContain("latex");
    const brief = buildBrief(fixture("patient-demo-multi-source"), NOW);
    expect(brief.chart.some((l) => /no known/i.test(l.text))).toBe(false);
    const theo = buildBrief(fixture("patient-demo-pediatric-asthma"), NOW);
    expect(theo.flags.find((f) => f.type === "allergy")?.severity).toBe("high");
    expect(theo.flags.find((f) => f.type === "allergy")?.spoken).toBe("a high severity allergy to peanut plus an allergy to House dust mite");
  });

  it("flags pediatric airway risk", () => {
    expect(types("patient-demo-pediatric-asthma")).toEqual(expect.arrayContaining(["airway", "pediatric"]));
  });

  it("never treats an empty chart as a safe chart", () => {
    const brief = buildBrief(fixture("patient-demo-sparse"), NOW);
    expect(brief.flags.map((f) => f.type)).toEqual(["incomplete_chart"]);
    expect(brief.flags[0]?.severity).toBe("high");
    expect(gapCodes("patient-demo-sparse")).toEqual(["missing_medications", "missing_allergies", "missing_conditions", "missing_labs"]);
  });

  it("reports consent-limited categories as not shared", () => {
    expect(gapCodes("patient-demo-consent-partial")).toEqual(
      expect.arrayContaining(["not_shared_conditions", "not_shared_labs", "no_demographics"]),
    );
    expect(buildBrief(fixture("patient-demo-consent-partial"), NOW).patient.displayLabel).toBe("Unnamed patient (limited chart)");
  });

  it("surfaces messy coding instead of guessing", () => {
    expect(gapCodes("patient-demo-messy-coding")).toEqual(
      expect.arrayContaining(["uncoded_medication", "medication_status_unknown", "lab_no_value", "no_kidney_function"]),
    );
  });

  it("passes through an unavailable source", () => {
    expect(gapCodes("patient-demo-source-unavailable")).toContain("source_unavailable");
  });
});
