import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { ClaudeAnswerClassifier } from "../src/answer-classifier.js";
import type { PatientInterview } from "../src/interview-types.js";

// Live QA of the spoken-answer classifier (Claude Haiku) against the committed interviews. It calls the
// real Anthropic API, so it only runs with QA_LIVE=1:  QA_LIVE=1 npx vitest run test/qa-interview-live.test.ts
// The paraphrases are written as a student would say them out loud, not copied from the card text.

const LIVE = process.env.QA_LIVE === "1";
if (LIVE && existsSync(".env")) process.loadEnvFile(".env"); // same as src/env.ts; the key is never printed

const CONTENT = join(import.meta.dirname, "..", "content", "patients");
const interviewOf = (id: string) => JSON.parse(readFileSync(join(CONTENT, id, "interview.json"), "utf8")) as PatientInterview;

// round id -> what a student might say for the correct move
const SPOKEN: Record<string, Record<string, string>> = {
  "patient-demo-multi-source": {
    history_onset: "I'd want to know when this all kicked off and if the pain has shifted anywhere since it began",
    history_associated: "let's go through the other symptoms, like is she queasy, is she eating, any fevers, and how are her bowels and peeing",
    history_pregnancy: "I need to know when her last menstrual period was and if there's any chance she's pregnant",
    history_background: "um, I'd get her full background, past illnesses and operations, what meds she's on, allergies, and when she last had anything to eat or drink",
    exam_vitals_abdomen: "get a set of vitals first and then examine the belly gently, starting on the side that doesn't hurt",
    exam_peritoneal: "I'd look for peritoneal signs, so rebound, Rovsing's, and the psoas test",
    tests_workup: "send a beta hCG, a full blood count, CRP, electrolytes and creatinine, a urine dip and a group and save, and get a CT once we know she's not pregnant",
    diagnosis: "I think this is just straightforward appendicitis",
    plan: "she needs her appendix out today, open approach, so start IV antibiotics, keep her NPO and make sure the theatre is latex free",
  },
  "patient-demo-001": {
    history_onset: "how long has she been getting these episodes, how many has she had, and what seems to trigger them",
    history_red_flags: "I'd screen for red flags, any fevers or rigors, is her urine dark, are her stools pale or fatty",
    history_meds_allergies: "go over her medical problems and every medication she's on, and find out what actually happened when she had penicillin",
    history_pregnancy: "when was her last period, and what is she using for birth control",
    exam_abdomen: "examine her abdomen and then do Murphy's, pressing under the right rib cage while she takes a deep breath",
    tests_workup: "right upper quadrant ultrasound, LFTs, full blood count, a BMP and a urine hCG",
    diagnosis: "I'd call it biliary colic from symptomatic gallstones",
    plan: "offer her an elective lap chole, and have her skip the metformin and lisinopril the morning of the operation",
  },
};

const VAGUE = ["um, I'm not totally sure, maybe something else", "can you repeat the question", "I'd do the right thing for her", "the one about the stuff"];

describe.skipIf(!LIVE)("live answer classifier (QA_LIVE=1)", () => {
  const classifier = new ClaudeAnswerClassifier();

  it("matches natural paraphrases of the correct choice, and returns null for vague speech", { timeout: 120_000 }, async () => {
    expect(process.env.ANTHROPIC_API_KEY, "ANTHROPIC_API_KEY missing from services/preop/.env").toBeTruthy();
    let right = 0;
    let total = 0;
    const misses: string[] = [];
    for (const [patientId, lines] of Object.entries(SPOKEN)) {
      const iv = interviewOf(patientId);
      for (const round of iv.rounds) {
        const said = lines[round.id];
        expect(said, `${patientId} ${round.id} has a paraphrase`).toBeTruthy();
        const want = round.choices.find((c) => c.grade === "correct")!.key;
        const got = await classifier.classify(said!, round.choices.map(({ key, text }) => ({ key, text })));
        total++;
        if (got === want) right++;
        else misses.push(`${patientId} ${round.id}: said "${said}" -> ${got ?? "null"} (want ${want})`);
        expect.soft(got, `${patientId} ${round.id}`).toBe(want);
      }
    }
    const round0 = interviewOf("patient-demo-multi-source").rounds[0]!.choices.map(({ key, text }) => ({ key, text }));
    let vagueNull = 0;
    for (const v of VAGUE) {
      const got = await classifier.classify(v, round0);
      if (got === null) vagueNull++;
      else misses.push(`vague "${v}" -> ${got} (want null)`);
      expect.soft(got, `vague: ${v}`).toBeNull();
    }
    console.log(`[qa-live] paraphrase accuracy ${right}/${total}; vague -> null ${vagueNull}/${VAGUE.length}`);
    for (const m of misses) console.log(`[qa-live] miss: ${m}`);
  });
});
