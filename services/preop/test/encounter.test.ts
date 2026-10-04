import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { ENCOUNTERS_BY_PLAN } from "../src/catalog/encounters.js";
import { EncounterSession } from "../src/encounter.js";
import { attendingPrompt, patientPrompt } from "../src/encounter-prompt.js";
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

  it("keeps missing exam findings unknown in the log, state, and attending summary", () => {
    const s = encounterFor("patient-demo-sparse");
    const maneuver = "pelvic";
    expect(s.encounter.exam[maneuver]).toBeUndefined();
    s.examine(maneuver);
    expect(s.log[0]?.text).toBe("Finding not available for this case.");
    expect(s.state().exams[0]?.finding).toBe("Finding not available for this case.");
    expect(s.summary()).toContain("pelvic exam: finding not available");
    expect(s.summary().toLowerCase()).not.toContain("unremarkable");
  });

  it("grounds attending history in returned facts and withholds uncollected answer keys", () => {
    const s = encounterFor("patient-demo-multi-source");
    expect(attendingPrompt(s).toLowerCase()).not.toContain("latex");
    expect(attendingPrompt(s).toLowerCase()).not.toContain("appendicitis");
    expect(s.summary().toLowerCase()).not.toContain("latex");
    s.answer("allergies");
    expect(s.summary().toLowerCase()).toContain("latex");
    expect(s.summary()).not.toMatch(/Say this in your own|FACT for you/);
    expect(s.summary().toLowerCase()).not.toContain("soup");
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

  // A learner who explicitly rejects the diagnosis, the operation or the urgency must not be told they were
  // right: the attending reads this score aloud as feedback.
  describe("negation", () => {
    const scored = (subject: string, a: { diagnosis?: string; differential?: string[]; procedure?: string; urgency?: string }) => {
      const s = encounterFor(subject);
      s.recordAssessment({ diagnosis: "", differential: [], procedure: "", urgency: "", ...a });
      return s.score();
    };
    const plan = (card: ReturnType<EncounterSession["score"]>) => card.sections.find((x) => x.id === "plan")!;

    it("does not credit a diagnosis the learner negated", () => {
      for (const diagnosis of ["not appendicitis; ovarian torsion", "I doubt appendicitis", "rule out appendicitis", "appendicitis is unlikely", "less likely appendicitis, probably torsion", "it isn't the appendix"]) {
        expect(scored("patient-demo-multi-source", { diagnosis }).diagnosisResult, diagnosis).toBe("incorrect");
      }
      expect(scored("patient-demo-multi-source", { diagnosis: "acute appendicitis, not ovarian torsion" }).diagnosisResult).toBe("correct");
      expect(scored("patient-demo-multi-source", { diagnosis: "no fever earlier but appendicitis" }).diagnosisResult).toBe("correct");
    });

    it("drops to partial when the learner denies the perforation", () => {
      expect(scored("patient-demo-sparse", { diagnosis: "perforated appendicitis" }).diagnosisResult).toBe("correct");
      expect(scored("patient-demo-sparse", { diagnosis: "appendicitis without perforation" }).diagnosisResult).toBe("partial");
    });

    it("does not credit a negated procedure or urgency", () => {
      const refused = plan(scored("patient-demo-multi-source", { diagnosis: "appendicitis", procedure: "do not take out the appendix", urgency: "not urgent, elective" }));
      expect(refused.score).toBe(0);
      expect(refused.found).toEqual([]);
      const accepted = plan(scored("patient-demo-multi-source", { diagnosis: "appendicitis", procedure: "laparoscopic appendectomy", urgency: "urgent, today" }));
      expect(accepted.score).toBe(10);
    });

    it("still counts 'rule out X' as naming X in the differential", () => {
      expect(scored("patient-demo-multi-source", { differential: ["rule out ectopic pregnancy", "ovarian torsion", "kidney stone"] }).differentialNamed).toHaveLength(3);
    });
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
    const answer = await tool("answer", { topic: "onset" });
    expect(answer.display).toContain("yesterday morning");
    expect(answer.display).not.toContain("FACT for you");
    expect((await tool("examine", { maneuver: "rebound" })).display).toContain("letting go was worse");
    expect((await tool("order_test", { test: "pregnancy_test" })).display).toBe("Test ordered. See the clinician chart.");
    const attending = await req("POST", `/encounters/${id}/attending`);
    expect(attending.json.phase).toBe("attending");
    expect(attending.json.attendingFirstMessage).toMatch(/Present the patient/);
    expect((await tool("get_encounter_summary", {})).result).toMatch(/allergies/);
    const scored = await tool("record_assessment", { diagnosis: "acute appendicitis", differential: ["ectopic pregnancy", "ovarian torsion"], procedure: "lap appendectomy", urgency: "urgent" });
    expect(scored.result).toMatch(/^Recorded\. Score \d+ of 100/);
    expect(scored.display).toMatch(/out of 100/);
    expect(scored.display).not.toContain("Tell them the score");
    expect(scored.state.phase).toBe("scored");
    const card = (await req("GET", `/encounters/${id}/score`)).json.scorecard;
    expect(card.diagnosisResult).toBe("correct");
    expect((await req("POST", `/encounters/${id}/tools/launch`, {})).status).toBe(404);
  });

  it("uses the parent voice for a child and reports patients without an interview", async () => {
    expect((await req("POST", "/encounters", { patientId: "patient-demo-pediatric-asthma" })).json).toMatchObject({ speaker: "parent", speakerName: "Laura Abernathy", patientName: "Theo Abernathy" });
    expect((await req("POST", "/encounters", { patientId: "patient-demo-polypharmacy" })).json.error.code).toBe("no_encounter");
  });

  it("rejects invalid tools and payloads without recording an action", async () => {
    const id = (await req("POST", "/encounters", { patientId: "patient-demo-sparse" })).json.encounterId;
    for (const [name, params] of [["answer", { topic: "made_up" }], ["examine", { maneuver: "made_up" }], ["order_test", { test: "made_up" }], ["answer", null], ["answer", []]] as const) {
      expect((await req("POST", `/encounters/${id}/tools/${name}`, params)).status).toBe(400);
    }
    const state = (await req("GET", `/encounters/${id}`)).json.state;
    expect(state).toMatchObject({ version: 0, historyAsked: [], exams: [], tests: [] });
    await req("POST", `/encounters/${id}/attending`);
    expect((await req("POST", `/encounters/${id}/tools/record_assessment`, { diagnosis: "appendicitis", differential: [42], procedure: "appendectomy", urgency: "urgent" })).status).toBe(400);
    expect((await req("GET", `/encounters/${id}`)).json.state.phase).toBe("attending");
  });

  it("enforces phase boundaries and freezes scored evidence", async () => {
    const id = (await req("POST", "/encounters", { patientId: "patient-demo-sparse" })).json.encounterId;
    const assessment = { diagnosis: "perforated appendicitis", differential: ["gastroenteritis", "kidney stone", "diverticulitis"], procedure: "appendectomy", urgency: "emergency" };
    expect((await req("GET", `/encounters/${id}/score`)).status).toBe(409);
    expect((await req("POST", `/encounters/${id}/tools/record_assessment`, assessment)).status).toBe(409);
    await req("POST", `/encounters/${id}/tools/answer`, { topic: "allergies" });
    await req("POST", `/encounters/${id}/attending`);
    expect((await req("GET", `/encounters/${id}/score`)).status).toBe(409);
    expect((await req("POST", `/encounters/${id}/tools/answer`, { topic: "last_meal" })).status).toBe(409);
    const scored = await req("POST", `/encounters/${id}/tools/record_assessment`, assessment);
    expect(scored.status).toBe(200);
    const original = (await req("GET", `/encounters/${id}/score`)).json.scorecard;
    for (const [name, params] of [["answer", { topic: "onset" }], ["examine", { maneuver: "abdomen_palpation" }], ["order_test", { test: "cbc" }], ["record_assessment", assessment]] as const) {
      expect((await req("POST", `/encounters/${id}/tools/${name}`, params)).status).toBe(409);
    }
    expect((await req("POST", `/encounters/${id}/attending`)).status).toBe(409);
    expect((await req("GET", `/encounters/${id}/score`)).json.scorecard).toEqual(original);
    expect((await req("GET", `/encounters/${id}`)).json.state.version).toBe(scored.json.state.version);
  });

  // The summary carries exam findings and test results. During the interview only the patient agent is
  // connected, and the patient must never learn findings it is told not to say.
  it("withholds the attending summary while the patient interview is running", async () => {
    const id = (await req("POST", "/encounters", { patientId: "multi-source-overlap" })).json.encounterId;
    await req("POST", `/encounters/${id}/tools/examine`, { maneuver: "rebound" });
    await req("POST", `/encounters/${id}/tools/order_test`, { test: "cbc" });
    const early = await req("POST", `/encounters/${id}/tools/get_encounter_summary`, {});
    expect(early.status).toBe(409);
    expect(early.json.error.code).toBe("invalid_phase");
    expect(JSON.stringify(early.json)).not.toMatch(/Rebound tenderness in the right lower quadrant|13\.1/);
    await req("POST", `/encounters/${id}/attending`);
    expect((await req("POST", `/encounters/${id}/tools/get_encounter_summary`, {})).json.result).toMatch(/Rebound tenderness/);
    await req("POST", `/encounters/${id}/tools/record_assessment`, { diagnosis: "appendicitis", differential: [], procedure: "appendectomy", urgency: "urgent" });
    expect((await req("POST", `/encounters/${id}/tools/get_encounter_summary`, {})).status).toBe(200);
  });

  it("does not attach authored fictional symptoms to real or mismatched records", async () => {
    for (const changes of [{ synthetic: false }, { patient: { name: "Different Patient", age: 30, sex: "male", displayLabel: "Different Patient" } }, { patient: { name: "Jonah Okoye", age: 40, sex: "male", displayLabel: "Jonah Okoye" } }, { patient: { name: "Jonah Okoye", age: 30, sex: "female", displayLabel: "Jonah Okoye" } }]) {
      const client = fixtureClient();
      const original = client.getRecord;
      client.getRecord = async (subject) => {
        const record = await original(subject);
        if ("synthetic" in changes) return { ...record, synthetic: changes.synthetic };
        return { ...record, data: { ...record.data, demographics: { ...record.data.demographics, name: changes.patient!.name, birthDate: changes.patient!.age === 40 ? "1986-02-14" : "1996-02-14", gender: changes.patient!.sex } } };
      };
      const guarded = createApp({ client, now: () => NOW, coachTickMs: 0 });
      const res = await guarded.request("/encounters", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-sparse" }) });
      expect(res.status).toBe(409);
      expect((await res.json()).error.code).toBe("synthetic" in changes ? "synthetic_only" : "demographics_mismatch");
    }
  });
});
