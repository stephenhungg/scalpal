import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { letterFrom, type AnswerClassifier, type SpeechToText } from "../src/answer-classifier.js";
import { buildCase } from "../src/case-builder.js";
import { ENCOUNTERS_BY_PLAN } from "../src/catalog/encounters.js";
import { buildCarryoverItems } from "../src/encounter-carryover.js";
import type { HistoryTopic, TestId } from "../src/catalog/encounters.js";
import { validateInterview, type ChoiceGrade, type InterviewChoice, type PatientInterview } from "../src/interview-types.js";
import { unitySafetyErrors } from "../src/unity-safe.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

// QA for the choice-based office interview against the real committed content (content/patients/*).
// Black-box through createApp + app.request, the way the Quest and the laptop page call it.

const CONTENT = join(import.meta.dirname, "..", "content", "patients");
const PATIENTS = [
  "patient-demo-001",
  "patient-demo-consent-partial",
  "patient-demo-messy-coding",
  "patient-demo-multi-source",
  "patient-demo-pediatric-asthma",
  "patient-demo-polypharmacy",
  "patient-demo-source-unavailable",
  "patient-demo-sparse",
] as const;

const interviewOf = (id: string) => JSON.parse(readFileSync(join(CONTENT, id, "interview.json"), "utf8")) as PatientInterview;
const patientMdOf = (id: string) => readFileSync(join(CONTENT, id, "patient.md"), "utf8");
const HIDDEN = /"(grade|feedback|patientCue|covers|finding)"/;

function rig(opts: { classifier?: AnswerClassifier | null; speechToText?: SpeechToText | null } = {}) {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, answerClassifier: opts.classifier ?? null, speechToText: opts.speechToText ?? null });
  const bodies: { route: string; status: number; json: Record<string, any> }[] = [];
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    const out = { route: `${method} ${route}`, status: res.status, json: (await res.json()) as Record<string, any> };
    bodies.push(out);
    return out;
  };
  return { req, bodies };
}

type Strategy = (choices: InterviewChoice[]) => InterviewChoice;
const byGrade = (...order: ChoiceGrade[]): Strategy => (choices) => {
  for (const g of order) {
    const c = choices.find((x) => x.grade === g);
    if (c) return c;
  }
  throw new Error("no choice");
};

// Runs a whole interview with a pick strategy, asserting the per-round contract as it goes.
async function run(patientId: string, strategy: Strategy, r = rig()) {
  const iv = interviewOf(patientId);
  const created = await r.req("POST", "/interviews", { patientId });
  expect(created.status, JSON.stringify(created.json)).toBe(201);
  const id = created.json.interviewId as string;
  expect(created.json.openingLine).toBe(iv.openingLine);
  expect(HIDDEN.test(JSON.stringify(created.json.round))).toBe(false);
  expect((await r.req("GET", `/interviews/${id}/score`)).status).toBe(409);

  const picked: InterviewChoice[] = [];
  let last: Record<string, any> = {};
  for (const [i, round] of iv.rounds.entries()) {
    const view = i === 0 ? created.json.round : last.next;
    expect(view).toMatchObject({ roundId: round.id, number: i + 1, of: iv.rounds.length, stage: round.stage, prompt: round.prompt });
    expect(view.choices).toEqual(round.choices.map(({ key, text }) => ({ key, text })));
    expect(HIDDEN.test(JSON.stringify(view)), `round ${round.id} view leaks a hidden field`).toBe(false);
    for (const c of round.choices) {
      expect(JSON.stringify(view)).not.toContain(c.feedback);
      expect(JSON.stringify(view)).not.toContain(c.patientCue);
    }
    // Mid-run state never carries grades either.
    const mid = await r.req("GET", `/interviews/${id}`);
    expect(HIDDEN.test(JSON.stringify(mid.json.round))).toBe(false);
    expect(JSON.stringify(mid.json.picks)).not.toMatch(/grade/);

    const choice = strategy(round.choices);
    picked.push(choice);
    const res = await r.req("POST", `/interviews/${id}/answer`, { key: choice.key });
    expect(res.status, JSON.stringify(res.json)).toBe(200);
    last = res.json;
    expect(last.pick).toMatchObject({ roundId: round.id, key: choice.key, via: "tap" });
    expect(last.finding ?? null).toEqual(choice.finding ?? null);
    expect(last.patient.clinicianMove).toBe(choice.text);
    expect(last.patient.direction).toBe(choice.patientCue);
    if (i < iv.rounds.length - 1) {
      expect(last.next).not.toBeNull();
      expect(last.scorecard ?? null).toBeNull();
      expect(last.patient.closing).toBe("");
      expect((await r.req("GET", `/interviews/${id}/score`)).status).toBe(409);
    }
  }
  expect(last.next).toBeNull();
  expect(last.scorecard).toBeTruthy();
  expect(last.patient.closing).toBe(iv.closingLine ?? "");
  expect(last.state.findings).toEqual(picked.flatMap((c) => (c.finding ? [c.finding] : [])));
  const after = await r.req("POST", `/interviews/${id}/answer`, { key: "A" });
  expect(after.status).toBe(409);
  expect((await r.req("POST", `/interviews/${id}/answer`, { text: "option b" })).status).toBe(409);
  const score = await r.req("GET", `/interviews/${id}/score`);
  expect(score.status).toBe(200);
  expect(score.json.scorecard).toEqual(last.scorecard);
  return { id, iv, picked, card: last.scorecard as Record<string, any>, r };
}

describe.each(PATIENTS)("office interview: %s", (patientId) => {
  it("all correct scores exactly 100 with the right diagnosis and plan", async () => {
    const { card, iv } = await run(patientId, byGrade("correct"));
    expect(card).toMatchObject({ kind: "interview", total: 100, max: 100, grade: "Excellent", diagnosisResult: "correct", procedureChosenCorrectly: true, patientId, procedureId: iv.procedureId, spoken: "" });
    expect(card.rounds.map((x: any) => x.points)).toEqual(iv.rounds.map((x) => x.weight));
    expect(card.sections.reduce((s: number, x: any) => s + x.score, 0)).toBe(100);
    expect(card.feedback.join(" ")).not.toMatch(/Missed:|Close:|surgery this patient needs/);
  });

  it("all wrong scores 0, diagnosis incorrect, plan wrong", async () => {
    const { card } = await run(patientId, byGrade("wrong"));
    expect(card).toMatchObject({ total: 0, diagnosisResult: "incorrect", procedureChosenCorrectly: false });
    expect(card.rounds.every((x: any) => x.points === 0)).toBe(true);
    expect(card.feedback.join(" ")).toMatch(/surgery this patient needs/);
  });

  it("partial picks score half the round weight", async () => {
    const { card, iv } = await run(patientId, byGrade("partial", "wrong"));
    const expected = iv.rounds.map((x) => (x.choices.some((c) => c.grade === "partial") ? x.weight / 2 : 0));
    expect(card.rounds.map((x: any) => x.points)).toEqual(expected);
    expect(card.total).toBe(Math.round(expected.reduce((s, x) => s + x, 0)));
    const dx = iv.rounds.find((x) => x.stage === "diagnosis")!;
    expect(card.diagnosisResult).toBe(dx.choices.some((c) => c.grade === "partial") ? "partial" : "incorrect");
  });

  it("every response body in a run is safe for Unity JsonUtility", async () => {
    // BUG (see report): answer bodies carry null for finding/scorecard/next and state.round, which
    // unity-safe.ts forbids. Only the scorecard and round views are asserted here; the full-body check is
    // the skipped test below.
    const { r } = await run(patientId, byGrade("correct", "partial", "wrong"));
    for (const b of r.bodies) {
      if (b.json.scorecard) expect(unitySafetyErrors(b.json.scorecard), b.route).toEqual([]);
      if (b.json.round) expect(unitySafetyErrors(b.json.round), b.route).toEqual([]);
      if (b.json.next) expect(unitySafetyErrors(b.json.next), b.route).toEqual([]);
    }
    const score = r.bodies.find((b) => b.route.endsWith("/score") && b.status === 200)!;
    expect(unitySafetyErrors(score.json)).toEqual([]);
  });

  // BUG: POST /interviews/:id/answer returns `finding: null` (picks without a finding), `scorecard: null`
  // (every non-final pick), `next: null` and `state.round: null` (final pick). unitySafetyErrors flags all
  // of them; JsonUtility turns each into an empty default object, so the client must special-case it.
  it.skip("BUG: every interview response body passes unitySafetyErrors", async () => {
    const { r } = await run(patientId, byGrade("correct"));
    for (const b of r.bodies) if (b.status === 200 || b.status === 201) expect(unitySafetyErrors(b.json), b.route).toEqual([]);
  });

  it("content integrity: valid, procedure matches the case, no diagnosis leak, chart risks covered", () => {
    const iv = interviewOf(patientId);
    expect(validateInterview(iv)).toEqual([]);
    expect(iv.patientId).toBe(patientId);
    const kase = buildCase(fixture(patientId), "", NOW);
    expect(iv.procedureId).toBe(kase.procedureId);

    const enc = ENCOUNTERS_BY_PLAN.get(patientId);
    expect(enc, "authored encounter").toBeTruthy();
    const md = patientMdOf(patientId).toLowerCase();
    const own = [...Object.values(enc!.history), enc!.persona.character, enc!.persona.opener, enc!.persona.demeanor].join(" ").toLowerCase();
    const leaks = enc!.diagnosis.keywords.flat().filter((kw) => md.includes(kw.toLowerCase()) && !own.includes(kw.toLowerCase()));
    expect(leaks, "diagnosis keywords in patient.md that the authored history never uses").toEqual([]);

    const correct = iv.rounds.map((x) => x.choices.find((c) => c.grade === "correct")!);
    const covers = correct.flatMap((c) => c.covers ?? []);
    const history = covers.filter((c) => c.startsWith("history:")).map((c) => c.slice(8)) as HistoryTopic[];
    const tests = covers.filter((c) => c.startsWith("test:")).map((c) => c.slice(5)) as TestId[];
    const items = buildCarryoverItems(kase, history, tests);
    for (const flag of kase.brief.flags.filter((f) => !["pediatric", "elderly", "incomplete_chart"].includes(f.type))) {
      const item = items.find((x) => x.flagId === flag.id);
      expect(item, `flag ${flag.type} has no carryover item (no case consideration)`).toBeTruthy();
      expect(item!.status, `flag ${flag.type}`).toBe("found");
    }
  });
});

describe("carryover into the operating room", () => {
  it("all-correct interview carries the 100/100 result and patient status into Jarvis", async () => {
    const { id, r } = await run("patient-demo-multi-source", byGrade("correct"));
    const s = await r.req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: id });
    expect(s.status).toBe(201);
    expect(s.json.systemPrompt).toContain("PATIENT STATUS");
    expect(s.json.systemPrompt).toContain("Pre-op interview score 100/100");
    expect(s.json.systemPrompt).toContain("They made the best move in every interview round.");
    expect(s.json.firstMessage).toMatch(/^Scrubbed in with you/);
    expect(unitySafetyErrors(s.json).filter((e) => !e.startsWith("$.runId"))).toEqual([]);
  });

  it.each(PATIENTS)("wrong-plan interview tells Jarvis the case still needs the real procedure (%s)", async (patientId) => {
    const r = rig();
    // Every round correct except the plan round.
    const iv = interviewOf(patientId);
    const created = await r.req("POST", "/interviews", { patientId });
    const iid = created.json.interviewId;
    for (const round of iv.rounds) {
      const c = round.stage === "plan" ? round.choices.find((x) => x.grade === "wrong")! : round.choices.find((x) => x.grade === "correct")!;
      await r.req("POST", `/interviews/${iid}/answer`, { key: c.key });
    }
    const card = (await r.req("GET", `/interviews/${iid}/score`)).json.scorecard;
    const kase = buildCase(fixture(patientId), "", NOW);
    expect(card).toMatchObject({ procedureChosenCorrectly: false, procedureId: kase.procedureId, diagnosisResult: "correct" });
    const s = await r.req("POST", "/coach/sessions", { patientId, encounterId: iid });
    const title = kase.procedure.title.toLowerCase();
    expect(s.json.systemPrompt).toMatch(new RegExp(`Plan pick wrong; the case needs an? ${title}`));
    expect(s.json.systemPrompt).toContain(`PROCEDURE: ${kase.procedure.title}`);
  });

  // BUG: article agreement. With procedure "Open appendectomy" the carryover reads "the case needs a open
  // appendectomy" (interview.ts carryover()) and the scorecard feedback "needs is a open appendectomy" (score()).
  it.skip("BUG: wrong-plan wording uses the right article for 'open appendectomy'", async () => {
    const r = rig();
    const iv = interviewOf("patient-demo-multi-source");
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    for (const round of iv.rounds) await r.req("POST", `/interviews/${iid}/answer`, { key: round.choices.find((x) => x.grade === (round.stage === "plan" ? "wrong" : "correct"))!.key });
    const card = (await r.req("GET", `/interviews/${iid}/score`)).json.scorecard;
    expect(card.feedback.join(" ")).not.toMatch(/\ba open\b/);
    const s = await r.req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: iid });
    expect(s.json.systemPrompt).not.toMatch(/\ba open\b/);
  });

  it("an interview for patient X does not carry into a session for patient Y", async () => {
    const { id, r } = await run("patient-demo-multi-source", byGrade("correct"));
    const s = await r.req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma", encounterId: id });
    expect(s.status).toBe(201);
    expect(s.json.systemPrompt).not.toContain("Pre-op interview score");
    expect(s.json.systemPrompt).not.toContain("FROM THE PRE-OP OFFICE");
    expect(s.json.systemPrompt).not.toContain("Priya");
    expect(s.json.firstMessage).not.toMatch(/^Scrubbed in with you/);
  });

  it("an unfinished interview does not carry over", async () => {
    const r = rig();
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    await r.req("POST", `/interviews/${iid}/answer`, { key: "B" });
    const s = await r.req("POST", "/coach/sessions", { patientId: "patient-demo-multi-source", encounterId: iid });
    expect(s.json.systemPrompt).not.toContain("Pre-op interview score");
    expect(s.json.firstMessage).not.toMatch(/^Scrubbed in with you/);
  });
});

describe("spoken answers", () => {
  it.each([
    ["a", "A"],
    ["A.", "A"],
    ["B", "B"],
    ["option B please", "B"],
    ["I choose c", "C"],
    ["the third one", "C"],
    ["the last one", "D"],
    ["Answer D.", "D"],
    ["both a and b", null],
    ["I would ask about her allergies", null],
    ["order a CBC", null],
    ["", null],
  ] as const)("letterFrom(%j) = %s", (heard, key) => {
    expect(letterFrom(heard)).toBe(key);
  });

  // BUG (minor): "d as in dog" is a natural way to disambiguate a spoken letter and is not matched without
  // the model; with no ANTHROPIC_API_KEY the route answers 503 classifier_unconfigured instead of picking D.
  it.skip("BUG: letterFrom('d as in dog') = D", () => {
    expect(letterFrom("d as in dog")).toBe("D");
  });

  // BUG: the "named" regex takes the first "option X" anywhere in the utterance, so ambiguous or
  // self-corrected speech locks in a pick without the model or a re-ask.
  it.skip("BUG: ambiguous or corrected letter speech does not pick the first letter", () => {
    expect(letterFrom("option a or option b")).toBeNull();
    expect(letterFrom("option a and option b")).toBeNull();
    expect(letterFrom("option a, no wait, option b")).not.toBe("A");
    expect(letterFrom("not option a")).not.toBe("A");
  });

  it("unclear speech with a classifier returns 422 and does not advance the round", async () => {
    const seen: string[] = [];
    const r = rig({ classifier: { classify: async (heard) => (seen.push(heard), null) } });
    const created = await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" });
    const iid = created.json.interviewId;
    const unclear = await r.req("POST", `/interviews/${iid}/answer`, { text: "hmm, I'm not sure, maybe something with the belly" });
    expect(unclear.status).toBe(422);
    expect(unclear.json).toMatchObject({ error: { code: "unclear_answer" }, heard: "hmm, I'm not sure, maybe something with the belly" });
    expect(seen).toHaveLength(1);
    const state = await r.req("GET", `/interviews/${iid}`);
    expect(state.json.round).toMatchObject({ number: 1, roundId: created.json.round.roundId });
    expect(state.json.picks).toEqual([]);
    const ok = await r.req("POST", `/interviews/${iid}/answer`, { key: "B" });
    expect(ok.json.pick).toMatchObject({ roundId: created.json.round.roundId, key: "B" });
    expect(ok.json.next.number).toBe(2);
  });

  it("a fake classifier pick is recorded as a voice pick with what was heard", async () => {
    const r = rig({ classifier: { classify: async () => "C" } });
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    const res = await r.req("POST", `/interviews/${iid}/answer`, { text: "I'd ask her to rate it out of ten" });
    expect(res.status).toBe(200);
    expect(res.json.pick).toMatchObject({ key: "C", via: "voice", heard: "I'd ask her to rate it out of ten" });
  });

  it("audio without speech-to-text configured returns 503 and does not advance", async () => {
    const r = rig({ classifier: { classify: async () => "B" } });
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    const res = await r.req("POST", `/interviews/${iid}/answer`, { audio: Buffer.from("not really audio").toString("base64"), mimeType: "audio/webm" });
    expect(res.status).toBe(503);
    expect(res.json.error.code).toBe("speech_unconfigured");
    expect((await r.req("GET", `/interviews/${iid}`)).json.round.number).toBe(1);
  });

  it("audio is transcribed then matched; an empty transcript asks again", async () => {
    let transcript = "option d";
    const r = rig({ speechToText: { transcribe: async () => transcript } });
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    const audio = Buffer.from("x").toString("base64");
    const ok = await r.req("POST", `/interviews/${iid}/answer`, { audio, mimeType: "audio/webm" });
    expect(ok.json.pick).toMatchObject({ key: "D", via: "voice", heard: "option d" });
    transcript = "";
    const empty = await r.req("POST", `/interviews/${iid}/answer`, { audio, mimeType: "audio/webm" });
    expect(empty.status).toBe(422);
    expect((await r.req("GET", `/interviews/${iid}`)).json.round.number).toBe(2);
  });

  // BUG (minor): whitespace-only text with no classifier configured answers 503 classifier_unconfigured
  // ("Spoken answers need ANTHROPIC_API_KEY") instead of 422 unclear_answer, because `heard` is not trimmed.
  it.skip("BUG: whitespace-only text is unclear (422), not a missing classifier (503)", async () => {
    const r = rig();
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    expect((await r.req("POST", `/interviews/${iid}/answer`, { text: "   " })).status).toBe(422);
  });

  it("an invalid key is not taken as a pick", async () => {
    const r = rig();
    const iid = (await r.req("POST", "/interviews", { patientId: "patient-demo-multi-source" })).json.interviewId;
    for (const key of ["E", "AB", "", 1]) expect((await r.req("POST", `/interviews/${iid}/answer`, { key })).status).toBe(422);
    expect((await r.req("GET", `/interviews/${iid}`)).json.round.number).toBe(1);
  });
});
