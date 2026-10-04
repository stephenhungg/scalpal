import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { letterFrom, wordMatch, type AnswerClassifier } from "../src/answer-classifier.js";
import { validateInterview, type InterviewChoice, type InterviewRound, type PatientInterview } from "../src/interview-types.js";
import { NOW, fixtureClient } from "./helpers.js";

const choice = (key: "A" | "B" | "C" | "D", grade: InterviewChoice["grade"], text: string, extra: Partial<InterviewChoice> = {}): InterviewChoice => ({
  key, grade, text, feedback: `${text} feedback.`, patientCue: `reply to ${text}`, ...extra,
});
const round = (id: string, stage: InterviewRound["stage"], weight: number, correct: "A" | "B" | "C" | "D", extra: Partial<Record<"A" | "B" | "C" | "D", Partial<InterviewChoice>>> = {}): InterviewRound => ({
  id, stage, weight, prompt: `What next (${id})?`,
  choices: (["A", "B", "C", "D"] as const).map((k, i) => choice(k, k === correct ? "correct" : i === 3 && correct !== "D" ? "partial" : "wrong", `${id} option ${k}`, extra[k])),
});

const INTERVIEW: PatientInterview = {
  version: 1, patientId: "patient-demo-multi-source", procedureId: "open_appendectomy", openingLine: "Hi, it really hurts.",
  rounds: [
    round("location", "history", 10, "A"),
    round("allergies", "history", 10, "B", { B: { covers: ["history:allergies"] } }),
    round("history", "history", 10, "C", { C: { covers: ["history:past_medical"] } }),
    round("exam", "exam", 15, "A", { A: { covers: ["exam:abdomen_palpation"], finding: { label: "Abdomen", text: "RLQ tenderness", abnormal: true } } }),
    round("labs", "tests", 15, "B", { B: { covers: ["test:cbc"], finding: { label: "CBC", text: "WBC 14.2", abnormal: true } } }),
    round("dx", "diagnosis", 25, "C"),
    round("plan", "plan", 15, "A"),
  ],
  closingLine: "Okay, let's do it.",
};

function rig(classifier: AnswerClassifier | null = null) {
  const root = mkdtempSync(join(tmpdir(), "content-"));
  const dir = join(root, "patient-demo-multi-source");
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "interview.json"), JSON.stringify(INTERVIEW));
  writeFileSync(join(dir, "patient.md"), "You are Priya Ramaswamy, 40. Your belly hurts on the right.");
  writeFileSync(join(dir, "patient_status.md"), "Priya Ramaswamy, 40 F. Latex allergy. Anemia (Hb 11.6).");
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, interviewContentRoot: root, answerClassifier: classifier });
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, json: (await res.json()) as Record<string, any> };
  };
  return { req };
}

describe("interview content", () => {
  it("validates the committed shape", () => {
    expect(validateInterview(INTERVIEW)).toEqual([]);
    const broken = { ...INTERVIEW, rounds: INTERVIEW.rounds.map((r, i) => (i === 0 ? { ...r, choices: r.choices.map((c) => ({ ...c, grade: "correct" as const })) } : r)) };
    expect(validateInterview(broken).join(" ")).toMatch(/exactly one correct/);
  });

  it("matches spoken letters without a model", () => {
    expect(letterFrom("B")).toBe("B");
    expect(letterFrom("option c.")).toBe("C");
    expect(letterFrom("I'll go with d")).toBe("D");
    expect(letterFrom("the second one")).toBe("B");
    expect(letterFrom("I would ask about her allergies")).toBeNull();
  });

  // Without ANTHROPIC_API_KEY a paraphrased move must still land: learners speak the question, not the option.
  it("matches a paraphrased move by shared words, and refuses a tie or a vague reply", () => {
    const choices = [
      { key: "A" as const, text: "Ask whether anyone in her family has had bowel cancer" },
      { key: "B" as const, text: "Ask when the pain started and whether it has moved since then" },
      { key: "C" as const, text: "Ask her to rate the pain from zero to ten" },
      { key: "D" as const, text: "Ask what usually sets off her migraines" },
    ];
    expect(wordMatch("When did your pain start, and has it moved anywhere?", choices)).toBe("B");
    expect(wordMatch("Does anyone in your family have bowel cancer?", choices)).toBe("A");
    expect(wordMatch("On a scale of zero to ten how bad is it", choices)).toBe("C");
    expect(wordMatch("tell me about the pain", choices)).toBeNull();
    expect(wordMatch("hmm I'm not sure", choices)).toBeNull();
  });
});

describe("choice-based office interview", () => {
  it("runs the fixed rounds, hides grades, scores, and carries into the OR prompt with the patient status", async () => {
    const said: string[] = [];
    const { req } = rig({ classify: async (heard) => (said.push(heard), heard.includes("allerg") ? "B" : null) });
    const created = await req("POST", "/interviews", { patientId: "patient-demo-multi-source" });
    expect(created.status).toBe(201);
    const id = created.json.interviewId;
    expect(created.json.openingLine).toBe("Hi, it really hurts.");
    expect(created.json.round).toMatchObject({ number: 1, of: 7, stage: "history" });
    expect(JSON.stringify(created.json.round)).not.toMatch(/grade|feedback|patientCue/);

    const first = await req("POST", `/interviews/${id}/answer`, { key: "A" });
    expect(first.json.patient).toMatchObject({ clinicianMove: "location option A", direction: "reply to location option A" });
    expect(first.json.next.number).toBe(2);

    const unclear = await req("POST", `/interviews/${id}/answer`, { text: "hmm let me think" });
    expect(unclear.status).toBe(422);
    expect(unclear.json.error.code).toBe("unclear_answer");
    const spoken = await req("POST", `/interviews/${id}/answer`, { text: "I'd ask about her allergies" });
    expect(spoken.json.pick).toMatchObject({ key: "B", via: "voice" });
    expect(spoken.json.pick.grade).toBeUndefined();

    for (const key of ["D", "A", "A", "C"]) await req("POST", `/interviews/${id}/answer`, { key }); // history partial, exam, labs wrong, dx
    const last = await req("POST", `/interviews/${id}/answer`, { key: "B" }); // plan wrong
    expect(last.json.next).toBeUndefined();
    expect(last.json.done).toBe(true);
    expect(last.json.patient.closing).toBe("Okay, let's do it.");
    const card = last.json.scorecard;
    expect(card).toMatchObject({ kind: "interview", procedureId: expect.any(String), procedureChosenCorrectly: false, diagnosisResult: "correct" });
    // 10 + 10 + 5 (partial) + 15 + 0 (labs wrong) + 25 + 0 (plan wrong)
    expect(card.total).toBe(65);
    expect(card.feedback.join(" ")).toMatch(/needs is an? .*appendectomy/);
    expect(Object.fromEntries(card.carryoverItems.map((i: any) => [i.type, i.status]))).toMatchObject({ latex: "found" });
    expect((await req("POST", `/interviews/${id}/answer`, { key: "A" })).status).toBe(409);

    const surgery = await req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: id });
    expect(surgery.json.systemPrompt).toMatch(/PATIENT STATUS[^\n]*\nPriya Ramaswamy, 40 F\. Latex allergy/);
    expect(surgery.json.systemPrompt).toMatch(/Pre-op interview score 65\/100/);
    expect(surgery.json.firstMessage).toMatch(/^Scrubbed in with you/);
  });

  it("returns the seated speakers' demographics, including a parent speaking for a child", async () => {
    const { req } = rig();
    const adult = await req("POST", "/interviews", { patientId: "patient-demo-multi-source" });
    // The Quest seats a 40-year-old woman who speaks for herself.
    expect(adult.json).toMatchObject({ speaker: "patient", patientAge: 40, patientSex: "female", speakerAge: 40, speakerSex: "female" });
    // Committed content: Theo (9) is seated as a child and his mother Laura speaks from the companion chair.
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const theo = (await (await app.request("/interviews", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-pediatric-asthma" }) })).json()) as Record<string, unknown>;
    expect(theo).toMatchObject({ speaker: "parent", speakerName: "Laura Abernathy", patientName: "Theo Abernathy", patientAge: 9, patientSex: "male", speakerSex: "female" });
    expect(theo.speakerAge).toBeGreaterThanOrEqual(18);
  });

  it("matches a spoken letter without a classifier, and asks again when nothing matched", async () => {
    const { req } = rig(null); // no ANTHROPIC_API_KEY: saying the letter must still work
    const id = (await req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    const lettered = await req("POST", `/interviews/${id}/answer`, { text: "option c" });
    expect(lettered.status).toBe(200);
    expect(lettered.json.pick).toMatchObject({ key: "C", via: "voice" });
    const unclear = await req("POST", `/interviews/${id}/answer`, {});
    expect(unclear.status).toBe(422);
    expect(unclear.json).toMatchObject({ error: { code: "unclear_answer" }, heard: "" });
  });

  it("says when a patient has no authored interview", async () => {
    const { req } = rig();
    expect((await req("POST", "/interviews", { patientId: "patient-demo-sparse" })).json.error.code).toBe("no_interview");
  });
});

describe("spoken answer fallback", () => {
  it("uses shared words only without a model or when the model fails, never over a model's unclear", async () => {
    const { wordMatch } = await import("../src/answer-classifier.js");
    const choices = [{ key: "A" as const, text: "Ask where the pain is now" }, { key: "B" as const, text: "Order a chest x-ray" }];
    expect(wordMatch("where is your pain right now", choices)).toBe("A");
    const run = async (classifier: AnswerClassifier) => {
      const root = mkdtempSync(join(tmpdir(), "content-"));
      mkdirSync(join(root, "patient-demo-multi-source"), { recursive: true });
      writeFileSync(join(root, "patient-demo-multi-source", "interview.json"), JSON.stringify(INTERVIEW));
      writeFileSync(join(root, "patient-demo-multi-source", "patient.md"), "You are Priya.");
      const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, interviewContentRoot: root, answerClassifier: classifier });
      const post = async (route: string, body: unknown) => (await app.request(route, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) })).status;
      const id = ((await (await app.request("/interviews", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-multi-source" }) })).json()) as any).interviewId;
      return post(`/interviews/${id}/answer`, { text: "location option A or maybe location option B" });
    };
    expect(await run({ classify: async () => null })).toBe(422); // the model said unclear: ask again
    expect(await run({ classify: async () => { throw new Error("down"); } })).toBe(422); // two matches tie: still unclear
  });
});
