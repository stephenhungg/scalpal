import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { ENCOUNTERS_BY_PLAN } from "../src/catalog/encounters.js";
import { EncounterSession } from "../src/encounter.js";
import { patientPrompt } from "../src/encounter-prompt.js";
import { validateCatalog } from "../src/validate.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

const encounterFor = (subject: string) => new EncounterSession("enc-test", buildCase(fixture(subject), "", NOW), ENCOUNTERS_BY_PLAN.get(subject)!, () => NOW);

describe("encounter catalog", () => {
  it("validates", () => expect(validateCatalog()).toEqual([]));
});

describe("patient facts come only from tools", () => {
  it("keeps facts out of the persona prompt", () => {
    const prompt = patientPrompt(encounterFor("patient-demo-multi-source"));
    for (const fact of ["latex", "two weeks", "soup", "nine", "accountant is", "appendicitis"]) {
      if (fact === "appendicitis") expect(prompt).toMatch(/never say words like appendicitis/);
      else expect(prompt.toLowerCase()).not.toContain(fact);
    }
  });

  it("answers allergies and medications from the real chart when nothing is authored", () => {
    const s = encounterFor("patient-demo-multi-source");
    expect(s.answer("allergies").toLowerCase()).toContain("latex");
    expect(s.answer("medications")).toMatch(/records/);
  });

  it("lets a patient say what an empty chart cannot", () => {
    const s = encounterFor("patient-demo-sparse");
    expect(s.answer("allergies")).toMatch(/Penicillin/);
  });

  it("handles questions that do not apply or are unknown without inventing", () => {
    expect(encounterFor("patient-demo-pediatric-asthma").answer("menstrual_pregnancy")).toMatch(/does not apply/);
    expect(encounterFor("patient-demo-multi-source").answer("favorite_color")).toMatch(/not something you know/);
  });

  it("returns a reaction to say and keeps findings off the patient's tongue", () => {
    const s = encounterFor("patient-demo-multi-source");
    const r = s.examine("rebound");
    expect(r).toMatch(/letting go was worse/);
    expect(r).not.toMatch(/Rebound tenderness in the right lower quadrant/);
    expect(s.state().exams[0]).toMatchObject({ id: "rebound", finding: "Rebound tenderness in the right lower quadrant." });
    expect(s.orderTest("cbc")).not.toMatch(/13\.1/);
    expect(s.state().tests[0]).toMatchObject({ id: "cbc", abnormal: true });
  });
});

describe("scoring", () => {
  it("rewards a complete, correct encounter", () => {
    const s = encounterFor("patient-demo-multi-source");
    const e = s.encounter;
    for (const item of [...e.critical, ...e.expected]) {
      if (item.kind === "history") s.answer(item.id);
      else if (item.kind === "exam") s.examine(item.id);
      else s.orderTest(item.id);
    }
    s.recordAssessment({ diagnosis: "acute appendicitis", differential: ["ectopic pregnancy", "ovarian torsion", "kidney stone"], procedure: "laparoscopic appendectomy", urgency: "urgent, today" });
    const card = s.score();
    expect(card.total).toBe(100);
    expect(card.criticalMissed).toEqual([]);
    expect(card.diagnosisResult).toBe("correct");
  });

  it("puts critical misses first and explains why", () => {
    const s = encounterFor("patient-demo-multi-source");
    s.answer("onset");
    s.examine("abdomen_palpation");
    s.recordAssessment({ diagnosis: "appendicitis", differential: ["gastroenteritis"], procedure: "appendectomy", urgency: "urgent" });
    const card = s.score();
    expect(card.feedback[0]).toMatch(/^Must fix: you did not cover allergies\. .*latex/);
    expect(card.criticalMissed.map((m) => m.id)).toEqual(["allergies", "menstrual_pregnancy", "pregnancy_test", "last_meal"]);
    expect(card.differentialSuggestions.length).toBeGreaterThan(0);
    expect(card.total).toBeLessThan(60);
  });

  it("gives partial credit when perforation is not named", () => {
    const s = encounterFor("patient-demo-sparse");
    s.recordAssessment({ diagnosis: "appendicitis", differential: [], procedure: "appendectomy", urgency: "urgent" });
    const card = s.score();
    expect(card.diagnosisResult).toBe("partial");
    expect(card.feedback.join(" ")).toMatch(/emergency/);
  });

  it("notes CT before ultrasound in a child", () => {
    const s = encounterFor("patient-demo-pediatric-asthma");
    s.orderTest("ct_abdomen_pelvis");
    expect(s.score().feedback.join(" ")).toMatch(/ultrasound comes first/);
    const t = encounterFor("patient-demo-pediatric-asthma");
    t.orderTest("ultrasound");
    t.orderTest("ct_abdomen_pelvis");
    expect(t.score().feedback.join(" ")).not.toMatch(/ultrasound comes first/);
  });
});

describe("encounter routes", () => {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, json: (await res.json()) as Record<string, any> };
  };

  it("runs interview, presentation, and score over HTTP", async () => {
    const created = await req("POST", "/encounters", { patientId: "multi-source-overlap" });
    expect(created.status).toBe(201);
    const id = created.json.encounterId;
    expect(created.json).toMatchObject({ speaker: "patient", speakerName: "Priya Ramaswamy", voiceId: "EXAVITQu4vr4xnSDxMaL" });
    const tool = async (name: string, params: unknown) => (await req("POST", `/encounters/${id}/tools/${name}`, params)).json;
    expect((await tool("answer", { topic: "allergies" })).result.toLowerCase()).toContain("latex");
    await tool("examine", { maneuver: "rebound" });
    await tool("order_test", { test: "pregnancy_test" });
    const attending = await req("POST", `/encounters/${id}/attending`);
    expect(attending.json.phase).toBe("attending");
    expect(attending.json.attendingFirstMessage).toMatch(/Present the patient/);
    expect((await tool("get_encounter_summary", {})).result).toMatch(/allergies/);
    const scored = await tool("record_assessment", { diagnosis: "acute appendicitis", differential: ["ectopic pregnancy", "ovarian torsion"], procedure: "lap appendectomy", urgency: "urgent" });
    expect(scored.result).toMatch(/^Recorded\. Score \d+ of 100/);
    expect(scored.state.phase).toBe("scored");
    const card = (await req("GET", `/encounters/${id}/score`)).json.scorecard;
    expect(card.diagnosisResult).toBe("correct");
    expect((await req("POST", `/encounters/${id}/tools/launch`, {})).status).toBe(404);
  });

  it("uses the parent voice for a child and reports patients without an interview", async () => {
    expect((await req("POST", "/encounters", { patientId: "patient-demo-pediatric-asthma" })).json).toMatchObject({ speaker: "parent", speakerName: "Laura Abernathy", patientName: "Theo Abernathy" });
    expect((await req("POST", "/encounters", { patientId: "patient-demo-polypharmacy" })).json.error.code).toBe("no_encounter");
  });
});
