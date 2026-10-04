// The pre-op office interview, authored once per patient and committed under content/patients/<subjectId>/.
// The patient (an ElevenLabs agent embodying patient.md) talks; after each patient turn the learner picks
// the next clinician move from four choices, by tap or by voice. Exactly one choice is correct, at most one
// is partially correct, the rest are wrong. The same rounds run in the same order every time, so scores
// compare across runs. Jarvis takes no part; he reads patient_status.md and the result later, in the OR.

export type ChoiceKey = "A" | "B" | "C" | "D";
export type ChoiceGrade = "correct" | "partial" | "wrong";
export type InterviewStage = "history" | "exam" | "tests" | "diagnosis" | "plan";

export interface InterviewChoice {
  key: ChoiceKey;
  text: string; // what the clinician says or does, as shown on the card ("Ask where the pain is now")
  grade: ChoiceGrade;
  feedback: string; // one sentence for the scorecard: why this pick was right, half right, or wrong
  patientCue: string; // what the patient conveys in reply, from their own point of view, using patient.md facts only
  covers?: string[]; // interview items this pick covers: "history:<topic>", "exam:<maneuver>", "test:<id>"
  finding?: { label: string; text: string; abnormal?: boolean }; // exam or test result shown on screen, never spoken by the patient
}

export interface InterviewRound {
  id: string;
  stage: InterviewStage;
  prompt: string; // the question on the card, e.g. "What do you do next?"
  choices: InterviewChoice[];
  weight: number; // points for the correct pick; partial earns half
}

export interface PatientInterview {
  version: 1;
  patientId: string; // FinchNode subject id
  procedureId: string; // the surgery this patient needs; the OR always loads it
  openingLine: string; // the patient's first words
  rounds: InterviewRound[];
  closingLine?: string; // what the patient says after the plan round
}

export const STAGE_ORDER: InterviewStage[] = ["history", "exam", "tests", "diagnosis", "plan"];

// Returns every problem with an interview; empty means valid. Run by tests on every committed file.
export function validateInterview(x: PatientInterview): string[] {
  const problems: string[] = [];
  const keys: ChoiceKey[] = ["A", "B", "C", "D"];
  if (x.version !== 1) problems.push("version must be 1");
  if (!x.patientId || !x.procedureId || !x.openingLine?.trim()) problems.push("patientId, procedureId and openingLine are required");
  if (x.rounds.length < 6 || x.rounds.length > 10) problems.push(`6 to 10 rounds expected, got ${x.rounds.length}`);
  const ids = new Set<string>();
  let lastStage = 0;
  for (const r of x.rounds) {
    const where = `round ${r.id}`;
    if (ids.has(r.id)) problems.push(`${where}: duplicate id`);
    ids.add(r.id);
    const stage = STAGE_ORDER.indexOf(r.stage);
    if (stage < 0) problems.push(`${where}: unknown stage ${r.stage}`);
    if (stage < lastStage) problems.push(`${where}: stage ${r.stage} out of order`);
    lastStage = Math.max(lastStage, stage);
    if (!(r.weight > 0)) problems.push(`${where}: weight must be positive`);
    if (r.choices.length !== 4 || r.choices.some((c, i) => c.key !== keys[i])) problems.push(`${where}: exactly four choices keyed A to D in order`);
    const grades = r.choices.map((c) => c.grade);
    if (grades.filter((g) => g === "correct").length !== 1) problems.push(`${where}: exactly one correct choice`);
    if (grades.filter((g) => g === "partial").length > 1) problems.push(`${where}: at most one partial choice`);
    for (const c of r.choices) {
      if (!c.text?.trim() || !c.feedback?.trim() || !c.patientCue?.trim()) problems.push(`${where} ${c.key}: text, feedback and patientCue are required`);
      for (const cov of c.covers ?? []) if (!/^(history|exam|test):[a-z_]+$/.test(cov)) problems.push(`${where} ${c.key}: bad covers entry ${cov}`);
    }
  }
  for (const stage of ["diagnosis", "plan"] as const) if (!x.rounds.some((r) => r.stage === stage)) problems.push(`a ${stage} round is required`);
  const total = x.rounds.reduce((s, r) => s + r.weight, 0);
  if (total !== 100) problems.push(`round weights must sum to 100, got ${total}`);
  const correctKeys = new Set(x.rounds.map((r) => r.choices.find((c) => c.grade === "correct")?.key));
  if (x.rounds.length >= 4 && correctKeys.size < 3) problems.push("the correct answer must vary across rounds (use at least three different letters)");
  return problems;
}
