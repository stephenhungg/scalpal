import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { CASE_PLANS } from "../src/catalog/cases.js";
import { ENCOUNTER_EXCLUSIONS, ENCOUNTERS, ENCOUNTERS_BY_PLAN, type Encounter } from "../src/catalog/encounters.js";
import { DEFAULT_PATIENT_VOICES, EncounterSession, demographicsMatch } from "../src/encounter.js";
import { attendingPrompt, patientPrompt } from "../src/encounter-prompt.js";
import { FinchNodeError } from "../src/finchnode.js";
import type { HealthRecord, Scenario } from "../src/types.js";
import { validateCatalog } from "../src/validate.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

const encounterFor = (subject: string) => new EncounterSession("enc-test", buildCase(fixture(subject), "", NOW), ENCOUNTERS_BY_PLAN.get(subject)!, () => NOW);
const SCENARIOS = (JSON.parse(readFileSync(join(import.meta.dirname, "fixtures", "scenarios.json"), "utf8")) as { data: Scenario[] }).data;

// The assessment a learner would give after reading the answer key: one keyword per group.
const perfectAssessment = (e: Encounter) => ({
  diagnosis: e.diagnosis.keywords.map((g) => g[0]).join(" "),
  differential: e.differential.slice(0, 3).map((d) => d.keywords[0]!),
  procedure: e.procedureKeywords.map((g) => g[0]).join(" "),
  urgency: e.urgency,
});
const elicitAll = (s: EncounterSession) => {
  for (const item of [...s.encounter.critical, ...s.encounter.expected]) {
    if (item.kind === "history") s.answer(item.id);
    else if (item.kind === "exam") s.examine(item.id);
    else s.orderTest(item.id);
  }
};

describe("encounter catalog", () => {
  it("validates", () => expect(validateCatalog()).toEqual([]));

  it("covers every case plan subject that has a procedure, or documents why not", () => {
    for (const [subject, plan] of Object.entries(CASE_PLANS)) {
      if (!plan.procedureId) continue;
      expect(ENCOUNTERS_BY_PLAN.has(subject) || Boolean(ENCOUNTER_EXCLUSIONS[subject]), subject).toBe(true);
    }
  });

  it("covers every FinchNode scenario subject that yields a buildable case", async () => {
    const client = fixtureClient();
    for (const scenario of SCENARIOS) {
      if (!scenario.subject) continue;
      let record: HealthRecord;
      try {
        record = await client.getRecord(scenario.subject);
      } catch (err) {
        expect(err).toBeInstanceOf(FinchNodeError);
        expect(ENCOUNTER_EXCLUSIONS[scenario.subject], scenario.subject).toBeTruthy();
        continue;
      }
      const kase = buildCase(record, scenario.id, NOW, scenario.subject);
      expect(kase.procedureId, scenario.subject).toBeTruthy();
      const encounter = ENCOUNTERS_BY_PLAN.get(scenario.subject);
      expect(encounter, scenario.subject).toBeDefined();
      expect(demographicsMatch(encounter!, kase), scenario.subject).toBe(true);
    }
  });

  it("keeps every authored chart fact consistent with the FinchNode chart", () => {
    for (const e of ENCOUNTERS) {
      const kase = buildCase(fixture(e.planSubject), "", NOW);
      const chart = (section: string) => kase.brief.chart.filter((l) => l.section === section).map((l) => l.text.toLowerCase());
      // Every charted allergy substance appears in an authored allergy answer.
      if (e.history.allergies) {
        for (const line of chart("Allergies")) {
          const aliases: Record<string, string> = { sulfonamide: "sulfa", contrast: "dye", media: "dye" };
          const words = line.replace(/^allergy to /, "").replace(/ allergy/, "").replace(/\s*\(.*\)$/, "").split(" ").filter((w) => w.length > 3).map((w) => aliases[w] ?? w);
          const said = e.history.allergies.toLowerCase();
          expect(words.some((w) => said.includes(w)), `${e.planSubject} ${line}`).toBe(true);
        }
      }
      // Every charted active drug appears in an authored medication answer (the uncoded pill by its reconciled name).
      if (e.history.medications) {
        const aliases: Record<string, string> = { acetaminophen: "tylenol", cholecalciferol: "vitamin d", levothyroxine: "thyroid", pressure: "hydrochlorothiazide" };
        const said = e.history.medications.toLowerCase();
        for (const line of chart("Medications")) {
          const words = line.split(/[^a-z]+/).filter((w) => w.length > 4).map((w) => aliases[w] ?? w);
          expect(words.some((w) => said.includes(w)), `${e.planSubject} ${line}`).toBe(true);
        }
      }
    }
  });

  it("uses distinct voices for every adult patient except where documented", () => {
    const byVoice = new Map<string, string[]>();
    for (const e of ENCOUNTERS) {
      const id = DEFAULT_PATIENT_VOICES[e.persona.voiceKey];
      byVoice.set(id, [...(byVoice.get(id) ?? []), e.planSubject]);
    }
    const shared = [...byVoice.values()].filter((v) => v.length > 1);
    // Six female speakers share five premade female voices: Dolores reuses Matilda, who voices Theo's mother.
    expect(shared).toEqual([["patient-demo-pediatric-asthma", "patient-demo-messy-coding"]]);
  });
});

describe("patient facts come only from tools", () => {
  it("keeps facts out of the persona prompt", () => {
    const prompt = patientPrompt(encounterFor("patient-demo-multi-source"));
    for (const fact of ["latex", "two weeks", "soup", "nine", "accountant is"]) expect(prompt.toLowerCase()).not.toContain(fact);
  });

  // The authored character gives the voice a person to play, but never a clinical fact the learner should
  // earn by asking: no chart drug, allergy, or problem, and no phrase from an authored answer, exam, or result.
  it("renders the authored character without clinical answers", () => {
    const generic = new Set(["tablet", "allergy", "sodium", "essential", "disorder", "disease", "chronic"]);
    const norm = (t: string) => t.toLowerCase().replace(/[^a-z' ]+/g, " ").replace(/\s+/g, " ");
    for (const e of ENCOUNTERS) {
      const character = e.persona.character;
      expect(character, e.planSubject).toBeTruthy();
      const s = encounterFor(e.planSubject);
      expect(patientPrompt(s)).toContain(character!);
      const said = norm(character!);
      const chartWords = s.kase.brief.chart
        .filter((l) => ["Medications", "Allergies", "Problems"].includes(l.section))
        .flatMap((l) => l.text.toLowerCase().split(/[^a-z]+/))
        .filter((w) => w.length > 5 && !generic.has(w));
      for (const w of chartWords) expect(said, `${e.planSubject}: ${w}`).not.toContain(w);
      const clinical = [
        ...Object.values(e.history),
        ...Object.values(e.exam).flatMap((x) => [x!.reaction, x!.finding]),
        ...Object.values(e.tests).map((x) => x!.result),
      ];
      for (const text of clinical) {
        const words = norm(text!).split(" ").filter(Boolean);
        for (let i = 0; i + 5 <= words.length; i++) {
          const phrase = words.slice(i, i + 5).join(" ");
          expect(said, `${e.planSubject}: "${phrase}"`).not.toContain(phrase);
        }
      }
    }
  });

  // The patient model must not be told its diagnosis, even as a word to avoid, and the rule must work for
  // any future case, not just appendicitis.
  it("never tells the patient model its diagnosis", () => {
    for (const e of ENCOUNTERS) {
      const prompt = patientPrompt(encounterFor(e.planSubject)).toLowerCase();
      expect(prompt).toContain("never name or guess any diagnosis or medical condition for your current problem");
      // Elective patients (interval cholecystectomy, recurrent diverticulitis) were told earlier diagnoses;
      // they may repeat what a tool returns, but never guess today's.
      expect(prompt).toContain("a condition a doctor already told you about in the past");
      for (const word of [e.diagnosis.label, ...e.diagnosis.keywords.flat(), ...(e.diagnosis.partial?.keywords.flat() ?? [])]) {
        expect(prompt, `${e.planSubject}: ${word}`).not.toContain(word.toLowerCase());
      }
    }
  });

  it("uses the child's own pronouns when a parent relays", () => {
    const daughter = fixture("patient-demo-pediatric-asthma");
    daughter.data.demographics = { ...daughter.data.demographics, gender: "female" };
    const prompt = patientPrompt(new EncounterSession("enc-test", buildCase(daughter, "", NOW), ENCOUNTERS_BY_PLAN.get("patient-demo-pediatric-asthma")!, () => NOW));
    expect(prompt).toMatch(/she says it hurts more when she walks/);
    expect(prompt.split("\n")[0]).not.toMatch(/\b(he|him|his)\b/); // the generated persona line; the demeanor is authored text
    expect(patientPrompt(encounterFor("patient-demo-pediatric-asthma"))).toMatch(/he says it hurts more when he walks/);
  });

  it("answers allergies and medications from the real chart when nothing is authored", () => {
    const authored = ENCOUNTERS_BY_PLAN.get("patient-demo-multi-source")!;
    const { allergies: _a, medications: _m, ...history } = authored.history;
    const s = new EncounterSession("enc-test", buildCase(fixture(authored.planSubject), "", NOW), { ...authored, history }, () => NOW);
    expect(s.answer("allergies").toLowerCase()).toContain("latex");
    expect(s.answer("medications")).toMatch(/records/);
  });

  it("lets a multi-source patient explain her chart in her own words", () => {
    const s = encounterFor("patient-demo-multi-source");
    expect(s.answer("allergies")).toMatch(/Latex gloves give me an itchy red rash/);
    expect(s.answer("medications")).toMatch(/levothyroxine/);
    expect(s.answer("past_medical")).toMatch(/underactive thyroid/);
  });

  // A referred patient may repeat what she was told, but parroting the greeting or the chief complaint
  // must not score the diagnosis for the learner.
  it("does not hand over the diagnosis in an opener or chief complaint", () => {
    for (const e of ENCOUNTERS) {
      for (const said of [e.persona.opener, e.history.chief_complaint ?? ""]) {
        const s = encounterFor(e.planSubject);
        s.recordAssessment({ diagnosis: said, differential: [], procedure: "", urgency: "" });
        expect(s.score().diagnosisResult, `${e.planSubject}: ${said}`).toBe("incorrect");
      }
    }
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

  it("puts the seated people in state: the patient, and an adult parent beside a child", () => {
    // The office seats an avatar for every playable patient; missing demographics left the chair empty.
    for (const e of ENCOUNTERS) {
      const st = encounterFor(e.planSubject).state();
      expect(st.patientAge, e.planSubject).toBe(e.persona.age);
      expect(st.patientSex, e.planSubject).toBe(e.persona.sex);
      expect(st.speakerAge, e.planSubject).toBeGreaterThanOrEqual(18);
      expect(["female", "male"], e.planSubject).toContain(st.speakerSex);
      if (e.persona.speaker === "patient") expect([st.speakerAge, st.speakerSex]).toEqual([st.patientAge, st.patientSex]);
    }
    expect(encounterFor("patient-demo-pediatric-asthma").state()).toMatchObject({ speaker: "parent", patientName: "Theo Abernathy", patientAge: 9, patientSex: "male", speakerName: "Laura Abernathy", speakerAge: 41, speakerSex: "female" });
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
  it.each(ENCOUNTERS.map((e) => [e.planSubject]))("scores a perfect run of %s at 100", (subject) => {
    const s = encounterFor(subject);
    elicitAll(s);
    s.recordAssessment(perfectAssessment(s.encounter));
    const card = s.score();
    expect(card.criticalMissed).toEqual([]);
    expect(card.diagnosisResult).toBe("correct");
    expect(card.total).toBe(100);
  });

  it("names the patient's own procedure when the plan is missed", () => {
    const s = encounterFor("patient-demo-polypharmacy");
    s.recordAssessment({ diagnosis: "gallstones", differential: [], procedure: "watch and wait", urgency: "urgent" });
    const card = s.score();
    expect(card.diagnosisResult).toBe("partial");
    expect(card.sections.find((x) => x.id === "plan")!.missed).toEqual(["laparoscopic cholecystectomy"]);
    expect(card.feedback[0]).toMatch(/^Must fix: you did not cover medications\. .*apixaban/);
  });

  it("does not give elective credit to an emergency plan for colic", () => {
    const s = encounterFor("patient-demo-001");
    s.recordAssessment({ diagnosis: "acute cholecystitis", differential: [], procedure: "cholecystectomy", urgency: "emergency" });
    const card = s.score();
    expect(card.diagnosisResult).toBe("incorrect");
    expect(card.feedback.join(" ")).toMatch(/Timing: this case is elective/);
  });

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

  // Feedback must name this case's operation; a future cholecystitis patient must not be told to do an appendectomy.
  it("names the case's own procedure when the plan misses it", () => {
    const s = encounterFor("patient-demo-multi-source");
    s.recordAssessment({ diagnosis: "appendicitis", differential: [], procedure: "", urgency: "urgent" });
    expect(s.score().sections.find((x) => x.id === "plan")!.missed).toEqual([s.kase.procedure.title.toLowerCase()]);
    const other = new EncounterSession("enc-test", { ...s.kase, procedure: { ...s.kase.procedure, title: "Laparoscopic cholecystectomy" } }, s.encounter, () => NOW);
    other.recordAssessment({ diagnosis: "appendicitis", differential: [], procedure: "", urgency: "urgent" });
    expect(other.score().sections.find((x) => x.id === "plan")!.missed).toEqual(["laparoscopic cholecystectomy"]);
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
    expect((await req("POST", "/encounters", { patientId: "patient-demo-pediatric-asthma" })).json).toMatchObject({ speaker: "parent", speakerName: "Laura Abernathy", patientName: "Theo Abernathy", voiceId: DEFAULT_PATIENT_VOICES.parent_female });
    // A FinchNode subject with no authored plan still gets a fallback case, but no interview.
    const client = fixtureClient();
    const original = client.getRecord;
    client.getRecord = async (subject) => (subject === "patient-demo-new" ? { ...(await original("patient-demo-001")), id: subject } : original(subject));
    const res = await createApp({ client, now: () => NOW, coachTickMs: 0 }).request("/encounters", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-new" }) });
    expect(res.status).toBe(404);
    expect((await res.json()).error.code).toBe("no_encounter");
  });

  it("starts an authored interview for every encounter patient", async () => {
    for (const e of ENCOUNTERS) {
      const created = await req("POST", "/encounters", { patientId: e.planSubject });
      expect(created.status, e.planSubject).toBe(201);
      expect(created.json).toMatchObject({ speaker: e.persona.speaker, speakerName: e.persona.name, patientName: e.persona.patientName, voiceId: DEFAULT_PATIENT_VOICES[e.persona.voiceKey] });
      expect(created.json.patientFirstMessage).toBe(e.persona.opener);
    }
  });

  it("starts the interview for the sandbox subject that mirrors each scenario", async () => {
    const sandbox = createApp({ client: fixtureClient({ sandbox: true }), now: () => NOW, coachTickMs: 0 });
    for (const e of ENCOUNTERS) {
      const scenario = SCENARIOS.find((s) => s.subject === e.planSubject)!;
      const subject = `u_test_${scenario.id.replace(/-/g, "_")}`;
      const res = await sandbox.request("/encounters", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: subject }) });
      expect(res.status, subject).toBe(201);
    }
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

  // Patient sex is authored on the encounter, not guessed from the voice: a mother can speak for a daughter.
  it("checks demographics against the authored patient sex, not the voice", async () => {
    const theo = ENCOUNTERS_BY_PLAN.get("patient-demo-pediatric-asthma")!;
    const original = { ...theo.persona };
    const client = fixtureClient();
    const getRecord = client.getRecord;
    client.getRecord = async (subject) => {
      const record = await getRecord(subject);
      return { ...record, data: { ...record.data, demographics: { ...record.data.demographics, gender: "female" } } };
    };
    const girls = createApp({ client, now: () => NOW, coachTickMs: 0 });
    const start = async () => (await girls.request("/encounters", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-pediatric-asthma" }) })).status;
    try {
      expect(await start()).toBe(409); // authored as a boy
      Object.assign(theo.persona, { sex: "female" });
      expect(await start()).toBe(201); // same parent_female voice, authored girl
    } finally {
      Object.assign(theo.persona, original);
    }
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

  // Starts an encounter for `subject` after rewriting its record's demographics.
  const withDemographics = async (subject: string, demographics: Record<string, unknown> | undefined) => {
    const client = fixtureClient();
    const original = client.getRecord;
    client.getRecord = async (s) => {
      const record = await original(s);
      const data = { ...record.data } as Record<string, unknown>;
      if (demographics) data.demographics = { ...(record.data.demographics ?? {}), ...demographics };
      else delete data.demographics;
      return { ...record, data } as HealthRecord;
    };
    const res = await createApp({ client, now: () => NOW, coachTickMs: 0 }).request("/encounters", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: subject }) });
    return { status: res.status, code: res.status === 201 ? "" : ((await res.json()) as any).error.code };
  };

  it("checks the sick patient's sex, not the speaking parent's voice", async () => {
    const theo = ENCOUNTERS_BY_PLAN.get("patient-demo-pediatric-asthma")!.persona;
    expect(theo).toMatchObject({ speaker: "parent", sex: "male", voiceKey: "parent_female" });
    expect(await withDemographics("patient-demo-pediatric-asthma", { gender: "male" })).toEqual({ status: 201, code: "" });
    expect(await withDemographics("patient-demo-pediatric-asthma", { gender: "female" })).toEqual({ status: 409, code: "demographics_mismatch" });
    // An adult woman with a female voice is still checked against her own chart.
    expect(await withDemographics("patient-demo-polypharmacy", { gender: "male" })).toEqual({ status: 409, code: "demographics_mismatch" });
    // A named persona never attaches to a chart that lost its demographics.
    expect(await withDemographics("patient-demo-messy-coding", undefined)).toEqual({ status: 409, code: "demographics_mismatch" });
  });

  it("attaches the unnamed-chart persona only while the chart really has no demographics", async () => {
    expect(ENCOUNTERS.filter((e) => e.persona.chartDemographics).map((e) => e.planSubject)).toEqual(["patient-demo-consent-partial"]);
    expect(await withDemographics("patient-demo-consent-partial", undefined)).toEqual({ status: 201, code: "" });
    const sam = ENCOUNTERS_BY_PLAN.get("patient-demo-consent-partial")!.persona;
    // Even demographics that agree with the persona are refused: the persona was authored for an unshared chart.
    expect(await withDemographics("patient-demo-consent-partial", { name: sam.patientName, birthDate: "1965-01-15", gender: sam.sex })).toEqual({ status: 409, code: "demographics_mismatch" });
    expect(await withDemographics("patient-demo-consent-partial", { gender: "male" })).toEqual({ status: 409, code: "demographics_mismatch" });
  });
});

describe("office to operating room", () => {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, json: (await res.json()) as Record<string, any> };
  };

  it("loads the right surgery after a wrong plan and tells Jarvis what was missed", async () => {
    const id = (await req("POST", "/encounters", { patientId: "patient-demo-multi-source" })).json.encounterId;
    await req("POST", `/encounters/${id}/tools/answer`, { topic: "onset" });
    await req("POST", `/encounters/${id}/attending`);
    await req("POST", `/encounters/${id}/tools/record_assessment`, { diagnosis: "kidney stone", differential: ["appendicitis"], procedure: "ureteroscopy", urgency: "elective" });
    const card = (await req("GET", `/encounters/${id}/score`)).json.scorecard;
    expect(card).toMatchObject({ procedureId: "open_appendectomy", procedureChosenCorrectly: false, diagnosisResult: "incorrect" });
    expect(card.feedback.join(" ")).toMatch(/needs is a[n]? open appendectomy/);
    const surgery = await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: id });
    expect(surgery.json.snapshot.procedureId).toBe("open_appendectomy");
    expect(surgery.json.systemPrompt).toMatch(/FROM THE PRE-OP OFFICE/);
    expect(surgery.json.systemPrompt).toMatch(/proposed "ureteroscopy"/);
    expect(surgery.json.systemPrompt).toMatch(/missed: .*allergies/);
  });

  it("splits chart risks into found and missed and opens the OR with the time-out", async () => {
    const id = (await req("POST", "/encounters", { patientId: "patient-demo-multi-source" })).json.encounterId;
    await req("POST", `/encounters/${id}/tools/answer`, { topic: "allergies" });
    await req("POST", `/encounters/${id}/attending`);
    await req("POST", `/encounters/${id}/tools/record_assessment`, { diagnosis: "appendicitis", differential: [], procedure: "laparoscopic appendectomy", urgency: "urgent" });
    const card = (await req("GET", `/encounters/${id}/score`)).json.scorecard;
    const byType = Object.fromEntries(card.carryoverItems.map((i: { type: string; status: string }) => [i.type, i.status]));
    expect(byType).toMatchObject({ latex: "found", anemia: "missed" });
    const surgery = await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: id });
    expect(surgery.json.firstMessage).toBe("Scrubbed in with you. Time-out: confirm patient, procedure and site.");
    expect(surgery.json.systemPrompt).toMatch(/did not elicit: Anemia/);
    const plain = await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source" });
    expect(plain.json.firstMessage).toMatch(/^Jarvis here/);
  });

  it("ignores an encounter that belongs to a different patient or is not scored", async () => {
    const id = (await req("POST", "/encounters", { patientId: "patient-demo-multi-source" })).json.encounterId;
    expect((await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: id })).json.systemPrompt).not.toMatch(/PRE-OP OFFICE/);
    expect((await req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma", encounterId: id })).json.systemPrompt).not.toMatch(/PRE-OP OFFICE/);
  });
});
