// Builds the committed pre-op office content for each patient: patient.md (the voice patient's persona),
// patient_status.md (clinical context Jarvis reads in the OR) and interview.json (the fixed choice-based
// interview). Inputs: the FinchNode demo record, the authored case plan (buildCase), the authored encounter
// (catalog/encounters.ts) and Stephen's dossier (docs/patients/<subjectId>.md). Claude drafts the files; the
// script validates them and writes content/patients/<subjectId>/. The committed files are the source of
// truth and may be hand-edited after generation; rerun only to regenerate from scratch.
//
//   npm run patients:build                      # every subject with an authored encounter
//   npm run patients:build -- patient-demo-001  # just these subjects
import "../src/env.js";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import Anthropic from "@anthropic-ai/sdk";
import { buildCase } from "../src/case-builder.js";
import { EXAM_MANEUVERS, ENCOUNTERS, ENCOUNTERS_BY_PLAN, HISTORY_TOPICS, TESTS, type Encounter } from "../src/catalog/encounters.js";
import { buildCarryoverItems } from "../src/encounter-carryover.js";
import { createFinchNodeClient } from "../src/finchnode.js";
import { validateInterview, type PatientInterview } from "../src/interview-types.js";
import type { HealthRecord, SurgicalCase } from "../src/types.js";

const MODEL = "claude-opus-5-5";
const HERE = dirname(fileURLToPath(import.meta.url));
const SERVICE = join(HERE, "..");
const REPO = join(SERVICE, "..", "..");
const OUT = join(SERVICE, "content", "patients");
const MAX_ATTEMPTS = 3;

if (!process.env.ANTHROPIC_API_KEY) {
  console.error("ANTHROPIC_API_KEY is not set. Put it in services/preop/.env.");
  process.exit(1);
}
const claude = new Anthropic();

async function loadRecord(subject: string): Promise<HealthRecord> {
  try {
    return await createFinchNodeClient().getRecord(subject);
  } catch (err) {
    const fixture = join(SERVICE, "test", "fixtures", `${subject}.json`);
    if (!existsSync(fixture)) throw err;
    console.warn(`[${subject}] FinchNode demo unavailable (${(err as Error).message}); using the test fixture`);
    return JSON.parse(readFileSync(fixture, "utf8")) as HealthRecord;
  }
}

async function ask(system: string, user: string): Promise<string> {
  const stream = claude.messages.stream({
    model: MODEL,
    max_tokens: 32000,
    thinking: { type: "adaptive" },
    system,
    messages: [{ role: "user", content: user }],
  });
  const msg = await stream.finalMessage();
  return msg.content.flatMap((b) => (b.type === "text" ? [b.text] : [])).join("").trim();
}

const stripFence = (s: string) => s.replace(/^```[a-z]*\n?/i, "").replace(/\n?```\s*$/, "").trim();

// Words the patient may not say about today's problem: the diagnosis and procedure keywords, except
// those the patient already uses in their own authored history (a diagnosis a doctor told them before).
function forbiddenWords(enc: Encounter): string[] {
  const known = Object.values(enc.history).join(" ").toLowerCase() + " " + enc.persona.opener.toLowerCase();
  const words = [
    ...enc.diagnosis.keywords.flat(),
    ...(enc.diagnosis.partial?.keywords.flat() ?? []),
    ...enc.procedureKeywords.flat(),
    ...enc.diagnosis.label.toLowerCase().split(/[^a-z]+/).filter((w) => w.length > 8),
  ].map((w) => w.toLowerCase());
  const generic = new Set(["recurrent", "complicated", "chronic"]);
  return [...new Set(words)].filter((w) => !generic.has(w) && !known.includes(w));
}

function caseContext(kase: SurgicalCase) {
  return {
    patient: kase.patient,
    urgency: kase.urgency,
    indication: kase.indication,
    presentation: kase.presentation,
    procedureId: kase.procedureId,
    procedureTitle: kase.procedure.title,
    flags: kase.brief.flags.map((f) => ({ type: f.type, severity: f.severity, title: f.title, detail: f.detail })),
    considerations: kase.considerations.map((c) => ({ flag: kase.brief.flags.find((f) => f.id === c.flagId)?.type, step: c.stepId, note: c.note })),
    chart: kase.brief.chart,
    dataGaps: kase.brief.dataGaps,
    sources: kase.brief.sources,
  };
}

const SHARED = `You write teaching content for Scalpal, a surgical training simulation. All patients are synthetic. The authored encounter (catalog/encounters.ts) is the authoritative source of the patient's story, exam and test results; the case plan (cases.ts via buildCase) is authoritative for the procedure, urgency and indication; the dossier adds color and consistency notes (respect resolved notes) but where it names a different procedure, the case plan wins. Never contradict the encounter. Do not use em dashes or en dashes anywhere; use commas, periods, colons or parentheses.`;

function inputsBlock(subject: string, enc: Encounter, kase: SurgicalCase, dossier: string) {
  return `SUBJECT: ${subject}

CASE PLAN AND CHART (from FinchNode + buildCase):
${JSON.stringify(caseContext(kase), null, 2)}

AUTHORED ENCOUNTER:
${JSON.stringify(enc, null, 2)}

DOSSIER (docs/patients/${subject}.md):
${dossier}`;
}

async function buildPatientMd(inputs: string, enc: Encounter, banned: string[], feedback = ""): Promise<string> {
  const speaker = enc.persona.speaker === "parent"
    ? `The voice agent is ${enc.persona.name}, the parent speaking for ${enc.persona.patientName} (${enc.persona.age}). Write in second person to the parent ("You are ${enc.persona.name}, ${enc.persona.patientName.split(" ")[0]}'s mother..."), including what the child says and does.`
    : `Write in second person ("You are ${enc.persona.name}, ${enc.persona.age}...").`;
  const system = `${SHARED}

Write patient.md: the system prompt for an ElevenLabs voice agent that embodies the patient in a pre-op office interview. ${speaker}
Markdown with short headed sections: who you are; personality and how you talk (speech habits, verbal tics, emotional arc); how you feel right now; what you think is going on (lay words, wrong or vague guesses are fine); your story (symptom timeline, where it is, what it feels like, how bad, what makes it better or worse, associated symptoms, appetite, fever, bowels, urine, periods if relevant); your health background (past medical and surgical history, medications with how you take them, allergies with the reaction, social and family history, recent illness, last meal and drink); what you feel when examined (only the bodily sensation, using the encounter's exam reactions); how to behave (answer only what is asked, short replies, never name a diagnosis for today's problem, never state test results or clinical findings, react to plans naturally).
Rules: plain language a patient would use. NEVER name today's diagnosis or the operation for it, never give exam findings or test results in clinical words, never include the clinician answer key, differential, or scoring. Diagnoses a doctor told them in the past may appear in their own lay words exactly as the encounter's history phrases them. Every fact must come from the encounter history (or the chart where the encounter is silent). Forbidden words (do not use them at all, in any form): ${banned.join(", ") || "(none)"}.
Length: 400 to 700 words. Output only the markdown, no code fence.${feedback}`;
  return stripFence(await ask(system, inputs));
}

async function buildStatusMd(inputs: string): Promise<string> {
  const system = `${SHARED}

Write patient_status.md: clinical context for Jarvis, the attending surgeon coach, to use later in the operating room. Third person, concise, clinical. Markdown sections: identity and demographics; presentation; key history, exam and test findings (use the encounter's authored results); working diagnosis; procedure (use the case plan's procedureTitle and procedureId) and urgency; chart risk flags and exactly how each changes the operation (tie to the considerations and steps); data gaps and chart discrepancies; what a learner should have elicited in the interview (the critical items first). Length: 250 to 450 words. Output only the markdown, no code fence.`;
  return stripFence(await ask(system, inputs));
}

function interviewProblems(x: PatientInterview, subject: string, kase: SurgicalCase): string[] {
  const problems = validateInterview(x);
  if (x.patientId !== subject) problems.push(`patientId must be ${subject}`);
  if (x.procedureId !== kase.procedureId) problems.push(`procedureId must be ${kase.procedureId}`);
  const valid = new Set([...HISTORY_TOPICS.map((t) => `history:${t}`), ...EXAM_MANEUVERS.map((t) => `exam:${t}`), ...TESTS.map((t) => `test:${t}`)]);
  for (const r of x.rounds) {
    for (const c of r.choices) for (const cov of c.covers ?? []) if (!valid.has(cov)) problems.push(`round ${r.id} ${c.key}: unknown covers id ${cov}`);
    const correct = r.choices.find((c) => c.grade === "correct");
    if ((r.stage === "exam" || r.stage === "tests") && correct && !correct.finding) problems.push(`round ${r.id}: the correct ${r.stage} pick needs a finding`);
  }
  // Only correct picks count toward chart risks, so a learner who picks right always clears the Time-Out.
  const correctHistory: string[] = [];
  const correctTests: string[] = [];
  for (const r of x.rounds) for (const c of r.choices) if (c.grade === "correct") for (const cov of c.covers ?? []) {
    const [kind, id] = cov.split(":");
    if (kind === "history") correctHistory.push(id!);
    if (kind === "test") correctTests.push(id!);
  }
  for (const item of buildCarryoverItems(kase, correctHistory as never, correctTests as never)) {
    if (item.status === "missed") problems.push(`chart risk "${item.label}" (${item.type}) is not covered by any correct pick; add the needed history/test covers to a correct choice`);
  }
  return problems;
}

async function buildInterview(inputs: string, patientMd: string, subject: string, kase: SurgicalCase): Promise<PatientInterview> {
  const types = readFileSync(join(SERVICE, "src", "interview-types.ts"), "utf8");
  const system = `${SHARED}

Write interview.json: a PatientInterview exactly matching this TypeScript (validateInterview must return no problems):
\`\`\`ts
${types}
\`\`\`
Flow: the patient speaks first (openingLine, close to the encounter opener). Each round's prompt is a "What do you do next?" style question, then four clinician moves A to D: exactly one correct (clearly the single best move at that point), at most one partial (reasonable but not the best), the rest clearly wrong but plausible for a student. Rounds in stage order: 3 or 4 history, 1 or 2 exam, 1 tests, then 1 diagnosis, then 1 plan (7 to 9 rounds). Round ids are short snake_case (e.g. "history_onset"). Weights sum to exactly 100; diagnosis and plan are heaviest (for example diagnosis 20, plan 15). Vary the correct letter across rounds (use all four letters if you can, never the same letter more than three times).
patientCue: what the patient conveys in reply, from their own point of view, using only facts in patient.md (below). For a wrong pick, a natural reaction: answers the irrelevant question truthfully, looks confused, or pushes back. For exam and test picks, put the clinician-visible result in finding {label, text, abnormal} using the encounter's authored exam findings and test results verbatim where they exist (wrong exam/test picks also get their authored finding if one exists), and the patientCue is only the patient's reaction ("ow, right there").
covers: each correct or partial pick that elicits something lists ids like "history:allergies", "history:medications", "history:past_medical", "exam:abdomen_palpation", "test:cbc", using only these ids: history: ${HISTORY_TOPICS.join(", ")}; exam: ${EXAM_MANEUVERS.join(", ")}; test: ${TESTS.join(", ")}. A choice can cover several (e.g. a combined medications and allergies question). Every chart risk flag in the case (allergies, anticoagulants, kidney function, anemia, airway, diabetes, incomplete chart, etc.) must be covered by a CORRECT pick in some history or tests round, so skipping it shows up as a missed risk at the OR Time-Out. The encounter's critical items should be the correct picks or covered by them.
Diagnosis round: four diagnoses; correct = the case diagnosis; partial = a close one (the encounter's diagnosis.partial if it has one); wrong = differential items. Plan round: correct = the procedure the case needs (procedureId ${kase.procedureId}, "${kase.procedure.title}") with the right urgency (${kase.urgency}); partial = right operation, wrong timing or approach detail; wrong = plausible but wrong management. closingLine: the patient's reaction to the plan.
Feedback: one short, specific sentence each ("Migration from the umbilicus to the right lower quadrant is the classic appendicitis pattern.").
Output only the JSON object, no prose, no code fence.

patient.md (the patient's knowledge):
${patientMd}`;
  let feedback = "";
  let last: string[] = [];
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    const text = await ask(system, inputs + feedback);
    let parsed: PatientInterview;
    try {
      const raw = stripFence(text);
      parsed = JSON.parse(raw.slice(raw.indexOf("{"), raw.lastIndexOf("}") + 1)) as PatientInterview;
    } catch (err) {
      last = [`not valid JSON: ${(err as Error).message}`];
      feedback = `\n\nYOUR PREVIOUS ATTEMPT FAILED: ${last.join("; ")}. Output only the JSON object.`;
      continue;
    }
    last = interviewProblems(parsed, subject, kase);
    if (!last.length) return parsed;
    console.warn(`[${subject}] interview attempt ${attempt}: ${last.length} problem(s)`);
    feedback = `\n\nYOUR PREVIOUS ATTEMPT:\n${JSON.stringify(parsed)}\n\nIT HAD THESE PROBLEMS, fix every one and output the whole corrected JSON:\n- ${last.join("\n- ")}`;
  }
  throw new Error(`interview still invalid after ${MAX_ATTEMPTS} attempts: ${last.join("; ")}`);
}

async function buildSubject(subject: string) {
  const enc = ENCOUNTERS_BY_PLAN.get(subject);
  if (!enc) throw new Error(`no authored encounter for ${subject}`);
  const record = await loadRecord(subject);
  const kase = buildCase(record, subject);
  const dossierPath = join(REPO, "docs", "patients", `${subject}.md`);
  const dossier = existsSync(dossierPath) ? readFileSync(dossierPath, "utf8") : "(no dossier)";
  const inputs = inputsBlock(subject, enc, kase, dossier);
  const banned = forbiddenWords(enc);

  const statusPromise = buildStatusMd(inputs);
  let patientMd = "";
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    patientMd = await buildPatientMd(inputs, enc, banned, attempt > 1 ? `\n\nYour previous draft used forbidden words. Remove every one of: ${banned.join(", ")}.` : "");
    const leaks = banned.filter((w) => patientMd.toLowerCase().includes(w));
    if (!leaks.length) break;
    console.warn(`[${subject}] patient.md attempt ${attempt} leaked: ${leaks.join(", ")}`);
    if (attempt === MAX_ATTEMPTS) throw new Error(`patient.md still leaks ${leaks.join(", ")}`);
  }
  const interview = await buildInterview(inputs, patientMd, subject, kase);
  const statusMd = await statusPromise;

  const dir = join(OUT, subject);
  mkdirSync(dir, { recursive: true });
  writeFileSync(join(dir, "patient.md"), patientMd + "\n");
  writeFileSync(join(dir, "patient_status.md"), statusMd + "\n");
  writeFileSync(join(dir, "interview.json"), JSON.stringify(interview, null, 2) + "\n");
  console.log(`[${subject}] wrote ${interview.rounds.length} rounds`);
}

const subjects = process.argv.slice(2).filter((a) => !a.startsWith("-"));
const targets = subjects.length ? subjects : ENCOUNTERS.map((e) => e.planSubject);
const results = await Promise.allSettled(targets.map((s) => buildSubject(s)));
let failed = 0;
results.forEach((r, i) => {
  if (r.status === "rejected") {
    failed++;
    console.error(`[${targets[i]}] FAILED: ${(r.reason as Error).message}`);
  }
});
process.exit(failed ? 1 : 0);
