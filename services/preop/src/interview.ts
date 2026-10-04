import type { ExamManeuver, HistoryTopic, TestId } from "./catalog/encounters.js";
import { buildCarryoverItems, procedureSite, type CarryoverItem } from "./encounter-carryover.js";
import type { ChoiceGrade, ChoiceKey, InterviewChoice, InterviewRound, PatientInterview } from "./interview-types.js";
import type { SurgicalCase } from "./types.js";

// One run of the choice-based office interview. The patient talks (voice agent embodying patient.md);
// after each patient turn the learner picks the next clinician move. The server owns the rounds, the
// grading and the score; clients only show the card and pass the pick (tap or classified speech).

export type InterviewPhase = "interview" | "scored";

export interface InterviewPick {
  roundId: string;
  key: ChoiceKey;
  grade: ChoiceGrade;
  via: "tap" | "voice";
  heard: string; // what the learner said, when picked by voice
  at: string;
}

export interface RoundResult {
  roundId: string;
  stage: InterviewRound["stage"];
  prompt: string;
  picked: { key: ChoiceKey; text: string; grade: ChoiceGrade; feedback: string };
  best: { key: ChoiceKey; text: string; feedback: string };
  points: number;
  max: number;
}

export interface InterviewScorecard {
  kind: "interview";
  total: number;
  max: number;
  grade: string;
  patientId: string;
  patientName: string;
  procedureId: string; // always the surgery this patient needs
  procedureTitle: string;
  procedureChosenCorrectly: boolean;
  diagnosisResult: "correct" | "partial" | "incorrect" | "missing";
  urgency: string;
  site: string;
  rounds: RoundResult[];
  sections: { id: string; label: string; score: number; max: number }[];
  carryoverItems: CarryoverItem[];
  feedback: string[];
  spoken: string; // empty: nobody speaks the score in the office
}

// What the client needs to show a round: never the grades or feedback.
export interface RoundView {
  roundId: string;
  number: number;
  of: number;
  stage: InterviewRound["stage"];
  prompt: string;
  choices: { key: ChoiceKey; text: string }[];
}

// Optional fields are omitted when absent (never null): Unity's JsonUtility turns null into an empty object.
export interface PickOutcome {
  pick: { roundId: string; key: ChoiceKey; via: "tap" | "voice"; heard: string }; // no grade: scores show only at the end
  choice: { key: ChoiceKey; text: string };
  // What the patient voice is told: the clinician's move, and what to convey in reply.
  patient: { clinicianMove: string; direction: string; closing: string };
  done: boolean;
  finding?: NonNullable<InterviewChoice["finding"]>;
  next?: RoundView;
  scorecard?: InterviewScorecard;
}

const withArticle = (noun: string) => `${/^[aeiou]/.test(noun) ? "an" : "a"} ${noun}`;

const STAGE_LABEL: Record<InterviewRound["stage"], string> = { history: "History", exam: "Examination", tests: "Workup", diagnosis: "Diagnosis", plan: "Plan" };

export class InterviewSession {
  phase: InterviewPhase = "interview";
  readonly picks: InterviewPick[] = [];
  readonly findings: NonNullable<InterviewChoice["finding"]>[] = [];
  private listeners = new Set<(e: { kind: "pick" | "transcript" | "phase"; payload: unknown }) => void>();

  constructor(
    readonly id: string,
    readonly kase: SurgicalCase,
    readonly interview: PatientInterview,
    readonly patientName: string,
    private readonly clock: () => Date = () => new Date(),
  ) {}

  get roundIndex() {
    return this.picks.length;
  }

  current(): RoundView | null {
    const r = this.interview.rounds[this.roundIndex];
    if (!r || this.phase !== "interview") return null;
    return { roundId: r.id, number: this.roundIndex + 1, of: this.interview.rounds.length, stage: r.stage, prompt: r.prompt, choices: r.choices.map(({ key, text }) => ({ key, text })) };
  }

  subscribe(fn: (e: { kind: "pick" | "transcript" | "phase"; payload: unknown }) => void) {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  transcript(speaker: "learner" | "patient", text: string) {
    for (const l of this.listeners) l({ kind: "transcript", payload: { speaker, text } });
  }

  pick(key: ChoiceKey, via: "tap" | "voice" = "tap", heard = ""): PickOutcome | null {
    const round = this.interview.rounds[this.roundIndex];
    const choice = round?.choices.find((c) => c.key === key);
    if (!round || !choice || this.phase !== "interview") return null;
    const pick: InterviewPick = { roundId: round.id, key, grade: choice.grade, via, heard, at: this.clock().toISOString() };
    this.picks.push(pick);
    if (choice.finding) this.findings.push(choice.finding);
    for (const l of this.listeners) l({ kind: "pick", payload: { pick, round, choice } });
    const done = this.roundIndex >= this.interview.rounds.length;
    if (done) {
      this.phase = "scored";
      for (const l of this.listeners) l({ kind: "phase", payload: this.phase });
    }
    const next = this.current();
    return {
      pick: { roundId: pick.roundId, key, via, heard },
      choice: { key, text: choice.text },
      patient: { clinicianMove: choice.text, direction: choice.patientCue, closing: done ? (this.interview.closingLine ?? "") : "" },
      done,
      ...(choice.finding ? { finding: choice.finding } : {}),
      ...(next ? { next } : {}),
      ...(done ? { scorecard: this.score() } : {}),
    };
  }

  private covered(prefix: string): string[] {
    const out = new Set<string>();
    this.interview.rounds.forEach((r, i) => {
      const p = this.picks[i];
      if (!p || p.grade === "wrong") return;
      for (const c of r.choices.find((x) => x.key === p.key)?.covers ?? []) if (c.startsWith(`${prefix}:`)) out.add(c.slice(prefix.length + 1));
    });
    return [...out];
  }

  score(): InterviewScorecard {
    const rounds: RoundResult[] = this.interview.rounds.map((r, i) => {
      const best = r.choices.find((c) => c.grade === "correct")!;
      const p = this.picks[i];
      const picked = p ? r.choices.find((c) => c.key === p.key)! : null;
      const points = !picked ? 0 : picked.grade === "correct" ? r.weight : picked.grade === "partial" ? r.weight / 2 : 0;
      return {
        roundId: r.id,
        stage: r.stage,
        prompt: r.prompt,
        picked: picked ? { key: picked.key, text: picked.text, grade: picked.grade, feedback: picked.feedback } : { key: "A", text: "(no answer)", grade: "wrong", feedback: "Not answered." },
        best: { key: best.key, text: best.text, feedback: best.feedback },
        points,
        max: r.weight,
      };
    });
    const total = Math.round(rounds.reduce((s, r) => s + r.points, 0));
    const sections = (Object.keys(STAGE_LABEL) as InterviewRound["stage"][])
      .map((stage) => {
        const rs = rounds.filter((r) => r.stage === stage);
        return { id: stage, label: STAGE_LABEL[stage], score: Math.round(rs.reduce((s, r) => s + r.points, 0)), max: rs.reduce((s, r) => s + r.max, 0) };
      })
      .filter((s) => s.max > 0);
    const gradeOf = (stage: InterviewRound["stage"]) => rounds.find((r) => r.stage === stage && this.picks.some((p) => p.roundId === r.roundId))?.picked.grade;
    const dx = gradeOf("diagnosis");
    const plan = gradeOf("plan");
    const feedback = [
      ...rounds.filter((r) => r.picked.grade === "wrong").map((r) => `Missed: ${r.prompt} The best move was "${r.best.text}". ${r.best.feedback}`),
      ...rounds.filter((r) => r.picked.grade === "partial").map((r) => `Close: "${r.picked.text}". ${r.picked.feedback} Best: "${r.best.text}".`),
      ...(plan === "correct" ? [] : [`The surgery this patient needs is ${withArticle(this.kase.procedure.title.toLowerCase())}, and that is what you will do in the operating room.`]),
      ...rounds.filter((r) => r.picked.grade === "correct").map((r) => `Good: ${r.picked.feedback}`),
    ];
    return {
      kind: "interview",
      total,
      max: 100,
      grade: total >= 85 ? "Excellent" : total >= 70 ? "Solid" : total >= 50 ? "Developing" : "Needs work",
      patientId: this.kase.patientId,
      patientName: this.patientName,
      procedureId: this.kase.procedureId,
      procedureTitle: this.kase.procedure.title,
      procedureChosenCorrectly: plan === "correct",
      diagnosisResult: dx === "correct" ? "correct" : dx === "partial" ? "partial" : dx === "wrong" ? "incorrect" : "missing",
      urgency: this.kase.urgency,
      site: procedureSite(this.kase.procedureId),
      rounds,
      sections,
      carryoverItems: buildCarryoverItems(this.kase, this.covered("history") as HistoryTopic[], this.covered("test") as TestId[]),
      feedback,
      spoken: "",
    };
  }

  // What Jarvis learns about the office, for the operating room prompt.
  carryover(): string {
    if (this.phase !== "scored") return "";
    const card = this.score();
    const missed = card.rounds.filter((r) => r.picked.grade !== "correct");
    const risks = (status: CarryoverItem["status"]) => card.carryoverItems.filter((i) => i.status === status).map((i) => i.label);
    return [
      `Pre-op interview score ${card.total}/100 (${card.grade}). Diagnosis pick: ${card.diagnosisResult}. Plan pick ${card.procedureChosenCorrectly ? "correct" : `wrong; the case needs ${withArticle(this.kase.procedure.title.toLowerCase())}`}.`,
      missed.length ? `Interview moves they missed or half-got: ${missed.map((r) => `${r.prompt} (best: ${r.best.text})`).join("; ")}. Bring these up only when they matter during the operation.` : "They made the best move in every interview round.",
      risks("found").length ? `Chart risks they found: ${risks("found").join(", ")}.` : "",
      risks("missed").length ? `Chart risks they did not elicit: ${risks("missed").join(", ")}.` : "",
    ]
      .filter(Boolean)
      .join(" ");
  }

  state() {
    return {
      interviewId: this.id,
      phase: this.phase,
      patientId: this.kase.patientId,
      patientName: this.patientName,
      done: this.phase === "scored",
      ...(this.current() ? { round: this.current()! } : {}),
      picks: this.picks.map(({ roundId, key, via }) => ({ roundId, key, via })),
      findings: this.findings,
    };
  }
}

export type { ExamManeuver };
