import {
  EXAM_MANEUVERS,
  HISTORY_TOPICS,
  TESTS,
  type Encounter,
  type ExamManeuver,
  type HistoryTopic,
  type RubricItem,
  type TestId,
} from "./catalog/encounters.js";
import type { SurgicalCase } from "./types.js";

// One pre-op encounter: a 1-on-1 interview with the patient (or a parent), then a case presentation to
// the attending, then a deterministic score. The patient agent can only learn its own facts through
// answer/examine/order_test, so everything it says is authored or charted, and everything the learner
// elicited is logged here.

export type EncounterPhase = "interview" | "attending" | "scored";

export const DEFAULT_PATIENT_VOICES: Record<Encounter["persona"]["voiceKey"], string> = {
  adult_female: "EXAVITQu4vr4xnSDxMaL", // Sarah
  parent_female: "XrExE9yKIg1WjnnlVkGX", // Matilda
  adult_male: "iP95p4xoKVk53GoZ742B", // Chris
};

export interface Assessment {
  diagnosis: string;
  differential: string[];
  procedure: string;
  urgency: string;
}

export interface FoundItem {
  kind: string;
  id: string;
  label: string;
  why: string;
}

export interface ScoreSection {
  id: string;
  label: string;
  score: number;
  max: number;
  found: string[];
  missed: string[];
}

export interface Scorecard {
  total: number;
  max: number;
  grade: string;
  sections: ScoreSection[];
  criticalMissed: FoundItem[];
  criticalFound: FoundItem[];
  diagnosisGiven: string;
  diagnosisExpected: string;
  diagnosisResult: "correct" | "partial" | "incorrect" | "missing";
  differentialNamed: string[];
  differentialSuggestions: string[];
  feedback: string[];
  spoken: string;
}

export interface EncounterLogEntry {
  at: string;
  kind: "history" | "exam" | "test" | "assessment";
  id: string;
  text: string;
}

const LABELS: Record<string, string> = {
  chief_complaint: "chief complaint", onset: "onset", location: "location", migration: "pain migration", character: "character of pain",
  severity: "severity", aggravating_relieving: "what makes it better or worse", nausea_vomiting: "nausea and vomiting", appetite: "appetite",
  fever: "fever", bowel: "bowel habits", urinary: "urinary symptoms", menstrual_pregnancy: "menstrual history and pregnancy", last_meal: "last oral intake",
  past_medical: "past medical history", past_surgical: "past surgical history", medications: "medications", allergies: "allergies",
  social: "social history", family: "family history", recent_illness: "recent illness",
  general_appearance: "general appearance", vitals: "vital signs", abdomen_inspection: "abdominal inspection", abdomen_palpation: "abdominal palpation",
  mcburney_point: "McBurney's point", rebound: "rebound tenderness", guarding_rigidity: "guarding and rigidity", rovsing: "Rovsing sign", psoas: "psoas sign",
  obturator: "obturator sign", murphy: "Murphy sign", cva_tenderness: "costovertebral angle tenderness", chest_lungs: "chest exam", genitourinary: "genitourinary exam",
  pelvic: "pelvic exam", cbc: "CBC", crp: "CRP", bmp: "basic metabolic panel", lactate: "lactate", lipase: "lipase", urinalysis: "urinalysis",
  pregnancy_test: "pregnancy test", ultrasound: "ultrasound", ct_abdomen_pelvis: "CT abdomen and pelvis", type_and_screen: "type and screen",
};
export const labelOf = (id: string) => LABELS[id] ?? id.replaceAll("_", " ");

const URGENCY_WORDS: Record<Encounter["urgency"], string[]> = {
  emergency: ["emergen", "immediately", "right now", "stat", "tonight"],
  urgent: ["urgent", "today", "tonight", "within hours", "asap", "soon"],
  elective: ["elective", "schedule", "outpatient"],
};

const norm = (s: string) => s.toLowerCase().replaceAll("’", "'");

// Negation-aware keyword check for what the learner commits to (diagnosis, procedure, timing): "not
// appendicitis" or "not urgent" must not earn credit. A keyword counts when at least one mention is not
// negated within a few words before it (or by a trailing "unlikely"/"ruled out") in the same clause.
const NEGATED_BEFORE = /\b(?:not|no|never|nor|isn't|isnt|aren't|wasn't|don't|dont|doesn't|won't|wouldn't|unlikely|doubt|doubtful|without|less likely|rules? out|ruled out|ruling out|exclude[sd]?|excluding)\b/;
const NEGATED_AFTER = /^\s+(?:is\s+|are\s+|was\s+|seems\s+|looks\s+)?(?:very\s+|quite\s+)?(?:unlikely|less likely|not likely|ruled out|excluded|doubtful)\b/;
const NEGATION_WINDOW_WORDS = 4;
const CLAUSE_BREAK = /[,;.!?:()]|\bbut\b|\bhowever\b|\bthough\b/;

function mentions(text: string, keyword: string): boolean {
  for (const clause of norm(text).split(CLAUSE_BREAK)) {
    for (let at = clause.indexOf(keyword); at !== -1; at = clause.indexOf(keyword, at + 1)) {
      const before = clause.slice(0, at).trim().split(/\s+/).slice(-NEGATION_WINDOW_WORDS).join(" ");
      const wordEnd = clause.slice(at + keyword.length).search(/\s|$/);
      const after = clause.slice(at + keyword.length + wordEnd);
      if (!NEGATED_BEFORE.test(before) && !NEGATED_AFTER.test(after)) return true;
    }
  }
  return false;
}
const matchesAllGroups = (text: string, groups: string[][]) => groups.every((g) => g.some((k) => mentions(text, k)));

export class EncounterSession {
  phase: EncounterPhase = "interview";
  readonly history = new Map<HistoryTopic, number>();
  readonly exams = new Map<ExamManeuver, number>();
  readonly tests: TestId[] = [];
  readonly log: EncounterLogEntry[] = [];
  assessment: Assessment | null = null;
  readonly transcript: { at: string; speaker: "learner" | "patient" | "coach"; text: string }[] = [];
  private listeners = new Set<(event: { kind: string; payload: unknown }) => void>();
  version = 0;
  private startedAt: number;

  constructor(
    readonly id: string,
    readonly kase: SurgicalCase,
    readonly encounter: Encounter,
    private readonly clock: () => Date = () => new Date(),
  ) {
    this.startedAt = clock().getTime();
  }

  // Observers (the realtime bridge) see every logged step and transcript line as it happens.
  subscribe(fn: (event: { kind: string; payload: unknown }) => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }

  emit(kind: string, payload: unknown) {
    for (const fn of this.listeners) fn({ kind, payload });
  }

  private record(kind: EncounterLogEntry["kind"], id: string, text: string) {
    const entry = { at: this.clock().toISOString(), kind, id, text };
    this.log.push(entry);
    this.version += 1;
    this.emit("log", entry);
  }

  addTranscript(speaker: "learner" | "patient" | "coach", text: string) {
    const line = { at: this.clock().toISOString(), speaker, text: text.slice(0, 2000) };
    this.transcript.push(line);
    if (this.transcript.length > 500) this.transcript.shift();
    this.emit("transcript", line);
  }

  private chartLines(section: string): string[] {
    return this.kase.brief.chart.filter((l) => l.section === section && l.text).map((l) => l.text);
  }

  // The patient agent's only way to know its own history.
  answer(topic: string): string {
    if (!(HISTORY_TOPICS as readonly string[]).includes(topic)) {
      return "That is not something you know about. Answer naturally and briefly in character, without inventing medical facts.";
    }
    const t = topic as HistoryTopic;
    this.history.set(t, (this.history.get(t) ?? 0) + 1);
    const p = this.encounter.persona;
    const authored = this.encounter.history[t];
    let fact: string | null = authored ?? null;
    if (!fact && (t === "medications" || t === "allergies" || t === "past_medical")) {
      const section = t === "medications" ? "Medications" : t === "allergies" ? "Allergies" : "Problems";
      const lines = this.chartLines(section);
      if (lines.length) {
        fact = `From ${p.speaker === "parent" ? `${p.patientName}'s` : "your"} records: ${lines.join("; ")}.`;
      }
    }
    if (!fact && t === "menstrual_pregnancy" && (p.age < 12 || this.kase.patient.sex.toLowerCase().startsWith("m"))) {
      fact = "This question does not apply.";
    }
    this.record("history", t, fact ?? "unknown");
    if (!fact) return "You honestly don't know or don't remember this. Say so naturally, without making anything up.";
    return `FACT for you to say in character, in your own words, only what was asked: ${fact}`;
  }

  examine(maneuver: string): string {
    if (!(EXAM_MANEUVERS as readonly string[]).includes(maneuver)) {
      return "That exam is not part of this encounter. React naturally and briefly.";
    }
    const m = maneuver as ExamManeuver;
    this.exams.set(m, (this.exams.get(m) ?? 0) + 1);
    const found = this.encounter.exam[m];
    this.record("exam", m, found?.finding ?? "Finding not available for this case.");
    const reaction = found?.reaction || "No patient reaction is authored for this exam. Do not invent a reaction.";
    return `Your reaction (say only this, in character): ${reaction} Never read out clinical findings; they appear on the clinician's screen.`;
  }

  orderTest(test: string): string {
    if (!(TESTS as readonly string[]).includes(test)) {
      return "That test is not available here. React naturally and briefly.";
    }
    const t = test as TestId;
    if (!this.tests.includes(t)) this.tests.push(t);
    this.record("test", t, this.encounter.tests[t]?.result ?? "Result not available for this case.");
    return "The test has been ordered. Respond briefly like a patient would (for example 'okay' or 'will that hurt?'). Never state or guess results; they appear on the clinician's screen.";
  }

  // What the learner has gathered so far, for the attending to probe without giving answers away.
  summary(): string {
    const hx = [...this.history.keys()].map(labelOf);
    const historyFacts = [...this.history.keys()].map((topic) => {
      const entry = this.log.findLast((item) => item.kind === "history" && item.id === topic);
      return `${labelOf(topic)}: ${entry?.text ?? "unknown"}`;
    });
    const ex = [...this.exams.keys()].map(labelOf);
    const results = this.tests.map((t) => `${labelOf(t)}: ${this.encounter.tests[t]?.result ?? "not available"}`);
    const findings = [...this.exams.keys()].map((m) => `${labelOf(m)}: ${this.encounter.exam[m]?.finding ?? "finding not available"}`);
    return [
      `Patient: ${this.kase.patient.displayLabel}${this.encounter.persona.speaker === "parent" ? `, history given by ${this.encounter.persona.name}` : ""}.`,
      `History the learner asked about: ${hx.join(", ") || "nothing yet"}.`,
      `History returned: ${historyFacts.join(" ") || "none"}`,
      `Exam performed: ${ex.join(", ") || "none"}.`,
      `Findings: ${findings.join(" ") || "none"}`,
      `Tests ordered and results: ${results.join(" ") || "none"}`,
    ].join("\n");
  }

  recordAssessment(a: Partial<Assessment>): void {
    const list = (v: unknown) => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : typeof v === "string" ? v.split(/[,;]/).map((x) => x.trim()).filter(Boolean) : []);
    this.assessment = {
      diagnosis: typeof a.diagnosis === "string" ? a.diagnosis : "",
      differential: list(a.differential),
      procedure: typeof a.procedure === "string" ? a.procedure : "",
      urgency: typeof a.urgency === "string" ? a.urgency : "",
    };
    this.record("assessment", "assessment", JSON.stringify(this.assessment));
  }

  private has(item: RubricItem): boolean {
    if (item.kind === "history") return this.history.has(item.id);
    if (item.kind === "exam") return this.exams.has(item.id);
    return this.tests.includes(item.id);
  }

  score(): Scorecard {
    const e = this.encounter;
    const a = this.assessment ?? { diagnosis: "", differential: [], procedure: "", urgency: "" };
    const items = [...e.critical, ...e.expected.filter((x) => !e.critical.some((c) => c.kind === x.kind && c.id === x.id))];
    const weight = (x: RubricItem) => (e.critical.some((c) => c.kind === x.kind && c.id === x.id) ? 2 : 1);

    const section = (kind: RubricItem["kind"], id: string, label: string, max: number): ScoreSection => {
      const mine = items.filter((x) => x.kind === kind);
      const total = mine.reduce((s, x) => s + weight(x), 0);
      const got = mine.filter((x) => this.has(x)).reduce((s, x) => s + weight(x), 0);
      return {
        id,
        label,
        max,
        score: total ? Math.round((got / total) * max) : max,
        found: mine.filter((x) => this.has(x)).map((x) => labelOf(x.id)),
        missed: mine.filter((x) => !this.has(x)).map((x) => labelOf(x.id)),
      };
    };

    const history = section("history", "history", "History", 25);
    const exam = section("exam", "exam", "Examination", 15);
    const workup = section("test", "workup", "Workup", 15);

    const full = matchesAllGroups(a.diagnosis, e.diagnosis.keywords);
    const partial = !full && e.diagnosis.partial ? matchesAllGroups(a.diagnosis, e.diagnosis.partial.keywords) : false;
    const diagnosisResult: Scorecard["diagnosisResult"] = !a.diagnosis ? "missing" : full ? "correct" : partial ? "partial" : "incorrect";
    const diagnosis: ScoreSection = {
      id: "diagnosis",
      label: "Diagnosis",
      max: 20,
      score: full ? 20 : partial ? 12 : 0,
      found: full || partial ? [a.diagnosis] : [],
      missed: full ? [] : [e.diagnosis.label],
    };

    const procedureOk = matchesAllGroups(a.procedure, e.procedureKeywords) || matchesAllGroups(a.diagnosis, e.procedureKeywords);
    const urgencyOk = URGENCY_WORDS[e.urgency].some((w) => mentions(`${a.urgency}. ${a.procedure}`, w));
    const plan: ScoreSection = {
      id: "plan",
      label: "Plan",
      max: 10,
      score: (procedureOk ? 6 : 0) + (urgencyOk ? 4 : 0),
      found: [...(procedureOk ? ["procedure"] : []), ...(urgencyOk ? [`${e.urgency} timing`] : [])],
      missed: [...(procedureOk ? [] : [this.kase.procedure.title.toLowerCase()]), ...(urgencyOk ? [] : [`${e.urgency} timing`])],
    };

    const ddxText = norm(a.differential.join(" "));
    const named = e.differential.filter((d) => d.keywords.some((k) => ddxText.includes(k)));
    const differential: ScoreSection = {
      id: "differential",
      label: "Differential",
      max: 15,
      score: Math.round((Math.min(named.length, 3) / 3) * 15),
      found: named.map((d) => d.label),
      missed: named.length >= 3 ? [] : e.differential.filter((d) => !named.includes(d)).slice(0, 3).map((d) => d.label),
    };

    const sections = [history, exam, workup, diagnosis, plan, differential];
    const total = sections.reduce((s, x) => s + x.score, 0);
    const toItem = (x: RubricItem): FoundItem => ({ kind: x.kind, id: x.id, label: labelOf(x.id), why: x.why });
    const criticalMissed = e.critical.filter((x) => !this.has(x)).map(toItem);
    const criticalFound = e.critical.filter((x) => this.has(x)).map(toItem);

    const feedback: string[] = [];
    for (const m of criticalMissed) feedback.push(`Must fix: you did not cover ${m.label}. ${m.why}`);
    if (diagnosisResult === "partial") feedback.push(`Close: you named ${e.diagnosis.partial?.label.toLowerCase()}, but the full picture is ${e.diagnosis.label.toLowerCase()}.`);
    if (diagnosisResult === "incorrect" || diagnosisResult === "missing") feedback.push(`The diagnosis was ${e.diagnosis.label.toLowerCase()}.`);
    if (!urgencyOk) feedback.push(`Timing: this case is ${e.urgency}.`);
    if (named.length < 3) feedback.push(`Broaden the differential: consider ${differential.missed.join(", ").toLowerCase()}.`);
    for (const x of items.filter((i) => !this.has(i) && !e.critical.includes(i)).slice(0, 3)) feedback.push(`Also missed ${labelOf(x.id)}: ${x.why}`);
    for (const [test, note] of Object.entries(e.testNotes ?? {})) {
      if (this.tests.includes(test as TestId)) {
        const usIdx = this.tests.indexOf("ultrasound");
        if (test !== "ct_abdomen_pelvis" || usIdx === -1 || usIdx > this.tests.indexOf("ct_abdomen_pelvis")) feedback.push(`Note on ${labelOf(test)}: ${note}`);
      }
    }
    for (const f of criticalFound) feedback.push(`Good: you covered ${f.label}.`);

    const grade = total >= 85 ? "Excellent" : total >= 70 ? "Solid" : total >= 50 ? "Developing" : "Needs work";
    const spokenParts = [
      `${grade}: ${total} out of 100.`,
      criticalFound.length ? `You did well to cover ${criticalFound.map((x) => x.label).join(" and ")}.` : "",
      criticalMissed[0] ? `The big miss: ${criticalMissed[0].label}. ${criticalMissed[0].why}` : diagnosisResult === "correct" ? `Your diagnosis of ${e.diagnosis.label.toLowerCase()} was right.` : `The diagnosis was ${e.diagnosis.label.toLowerCase()}.`,
    ].filter(Boolean);

    return {
      total,
      max: 100,
      grade,
      sections,
      criticalMissed,
      criticalFound,
      diagnosisGiven: a.diagnosis,
      diagnosisExpected: e.diagnosis.label,
      diagnosisResult,
      differentialNamed: named.map((d) => d.label),
      differentialSuggestions: differential.missed,
      feedback,
      spoken: spokenParts.join(" "),
    };
  }

  state() {
    return {
      encounterId: this.id,
      phase: this.phase,
      version: this.version,
      patientId: this.kase.patientId,
      patientName: this.encounter.persona.patientName,
      speakerName: this.encounter.persona.name,
      speaker: this.encounter.persona.speaker,
      elapsedSeconds: Math.round((this.clock().getTime() - this.startedAt) / 1000),
      historyAsked: [...this.history.keys()].map((id) => ({ id, label: labelOf(id) })),
      exams: [...this.exams.keys()].map((id) => ({ id, label: labelOf(id), finding: this.encounter.exam[id]?.finding ?? "Finding not available for this case." })),
      tests: this.tests.map((id) => ({ id, label: labelOf(id), result: this.encounter.tests[id]?.result ?? "Not available.", abnormal: this.encounter.tests[id]?.abnormal ?? false })),
      assessment: this.assessment ?? { diagnosis: "", differential: [], procedure: "", urgency: "" },
    };
  }
}
