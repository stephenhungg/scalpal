/** scalpal.run_result.v1. Transport only: the encounter and surgery graders own scores. */
export interface Fact { id: string; label: string; atSeconds: number; timeKnown?: boolean }
export interface FoundItem { kind: string; id: string; label: string; why: string }
// Type-only import: the server remains the sole clinical grading authority.
import type { Scorecard } from '../../../../services/preop/src/encounter';
export type Diagnosis = Scorecard;
export interface RunResult {
  schemaVersion: 'scalpal.run_result.v1'; runId: string; sessionId: string; attemptId: string;
  encounterId: string; patientId: string; procedureId: string; isSample: boolean;
  diagnosisAvailable: boolean; diagnosis: Diagnosis | null;
  surgery: {
    available: boolean; demoAssisted: boolean; total: number; max: number; grade: string;
    rubric?: string; complete?: boolean; completionReason?: string; eventClock?: string;
    missingMilestones?: string[]; missingMetrics?: string[]; hintsAvailable?: boolean;
    decisionSummaryAvailable?: boolean; correctDecisions?: number; decisionCount?: number;
    milestones: Fact[]; guardrailViolations: Fact[]; orderDeviations: Fact[];
    decisions: (Fact & { correct: boolean; correctnessAvailable?: boolean })[]; bloodLossMl: number;
    economy: { available: boolean; leftPathMeters: number; rightPathMeters: number; durationSeconds: number };
    hints: Fact[];
  };
  replay: { jobId: string; status: 'queued' | 'processing' | 'ready' | 'failed'; failureReason: string;
    sourceArtifactId: string; replayArtifactId: string; jobRun: number; source: 'learner' | 'rehearsal' | 'sample' | 'unknown';
    durationSeconds: number; captureStartRunSeconds: number; clockAligned: boolean; eventClock?: string };
  demo: { enabled: boolean; patientId: string; showSuggestedQuestions: boolean; skipMarking: boolean;
    preExpose: boolean; timeLapseNonKeySteps: boolean; replayHighlightSeconds: number };
}

function ensure(test: unknown, message: string): asserts test { if (!test) throw new Error(message); }
function object(value: unknown): value is Record<string, unknown> { return !!value && typeof value === 'object' && !Array.isArray(value); }
function strings(value: unknown): value is string[] { return Array.isArray(value) && value.every(x => typeof x === 'string'); }
function finite(value: unknown) { return typeof value === 'number' && Number.isFinite(value) && value >= 0; }
function textFields(value: Record<string, unknown>, fields: string[]) { fields.forEach(k => ensure(typeof value[k] === 'string', `Missing or invalid ${k}.`)); }
function numberFields(value: Record<string, unknown>, fields: string[]) { fields.forEach(k => ensure(finite(value[k]), `Invalid ${k}: expected a nonnegative finite number.`)); }
function boolFields(value: Record<string, unknown>, fields: string[]) { fields.forEach(k => ensure(typeof value[k] === 'boolean', `Invalid ${k}: expected boolean.`)); }
function facts(value: unknown) { return Array.isArray(value) && value.every(x => object(x) && typeof x.id === 'string' && typeof x.label === 'string' && finite(x.atSeconds) && (x.timeKnown === undefined || typeof x.timeKnown === 'boolean')); }
function videoUrl(value: unknown) {
  if (typeof value !== 'string') return false;
  if (!value) return true;
  if (value.startsWith('/') && !value.startsWith('//') && !value.includes('\\')) return true;
  try { return ['https:', 'http:'].includes(new URL(value).protocol); } catch { return false; }
}
export function parseRunResult(raw: string, expectedSessionId?: string): RunResult {
  const v: unknown = JSON.parse(raw);
  ensure(object(v), 'Expected a RunResult object.');
  ensure(v.schemaVersion === 'scalpal.run_result.v1', 'Unsupported RunResult schemaVersion.');
  textFields(v, ['runId', 'sessionId', 'attemptId', 'encounterId', 'patientId', 'procedureId']);
  ensure(v.runId && v.sessionId && v.attemptId, 'Run, session and attempt IDs are required.');
  ensure(!expectedSessionId || v.sessionId === expectedSessionId, 'This result belongs to a different session.');
  boolFields(v, ['isSample']);
  if (v.diagnosisAvailable === undefined) v.diagnosisAvailable = object(v.diagnosis) && Number(v.diagnosis.max) > 0;
  boolFields(v, ['diagnosisAvailable']);
  if (!v.diagnosisAvailable) v.diagnosis = null;
  if (v.diagnosisAvailable) {
    const d = v.diagnosis; ensure(object(d), 'diagnosis must be a scorecard or null.');
    numberFields(d, ['total', 'max']); ensure(Number(d.max) > 0 && Number(d.total) <= Number(d.max), 'Invalid diagnosis score range.');
    textFields(d, ['patientId', 'patientName', 'site', 'grade', 'procedureId', 'procedureTitle', 'diagnosisGiven', 'diagnosisExpected', 'spoken']);
    boolFields(d, ['procedureChosenCorrectly']);
    ensure(['correct', 'partial', 'incorrect', 'missing'].includes(String(d.diagnosisResult)), 'Invalid diagnosis result.');
    ['differentialNamed', 'differentialSuggestions', 'feedback'].forEach(k => ensure(strings(d[k]), `Invalid diagnosis ${k}.`));
    ['criticalFound', 'criticalMissed'].forEach(k => ensure(Array.isArray(d[k]) && d[k].every(x => object(x) && ['kind', 'id', 'label', 'why'].every(f => typeof x[f] === 'string')), `Invalid ${k}.`));
    ensure(['urgent', 'emergency', 'elective'].includes(String(d.urgency)), 'Invalid diagnosis urgency.');
    ensure(Array.isArray(d.carryoverItems) && d.carryoverItems.every(x => object(x) && ['flagId', 'type', 'severity', 'label', 'detail'].every(k => typeof x[k] === 'string') && ['found', 'missed', 'chart_only'].includes(String(x.status)) && ['historyTopics', 'testIds', 'stepIds'].every(k => strings(x[k]))), 'Invalid carryoverItems.');
    ensure(Array.isArray(d.sections) && d.sections.every(x => object(x) && typeof x.id === 'string' && typeof x.label === 'string' && finite(x.score) && finite(x.max) && strings(x.found) && strings(x.missed)), 'Invalid diagnosis sections.');
  }
  const s = v.surgery; ensure(object(s), 'Missing surgery.'); boolFields(s, ['available']);
  numberFields(s, ['total', 'max', 'bloodLossMl']); textFields(s, ['grade']);
  ensure(!s.available || (Number(s.max) > 0 && Number(s.total) <= Number(s.max)), 'Invalid surgery score range.');
  ['milestones', 'guardrailViolations', 'orderDeviations', 'hints', 'decisions'].forEach(k => ensure(facts(s[k]), `Invalid surgery ${k}.`));
  ensure((s.decisions as Record<string, unknown>[]).every(x => typeof x.correct === 'boolean' && (x.correctnessAvailable === undefined || typeof x.correctnessAvailable === 'boolean')), 'Invalid decision result.');
  s.eventClock ??= 'run'; textFields(s, ['eventClock']); ensure(s.eventClock, 'Missing surgery event clock.');
  for (const field of ['rubric', 'completionReason']) if (s[field] !== undefined) textFields(s, [field]);
  for (const field of ['complete', 'hintsAvailable', 'decisionSummaryAvailable']) if (s[field] !== undefined) boolFields(s, [field]);
  for (const field of ['missingMilestones', 'missingMetrics']) if (s[field] !== undefined) ensure(strings(s[field]), `Invalid surgery ${field}.`);
  for (const field of ['correctDecisions', 'decisionCount']) if (s[field] !== undefined) {
    numberFields(s, [field]); ensure(Number.isInteger(s[field]), `Invalid surgery ${field}.`);
  }
  if (s.decisionSummaryAvailable === true) {
    numberFields(s, ['correctDecisions', 'decisionCount']);
    ensure(Number(s.correctDecisions) <= Number(s.decisionCount), 'Invalid decision aggregate.');
  }
  ensure(object(s.economy), 'Missing economy.'); boolFields(s.economy, ['available']); numberFields(s.economy, ['leftPathMeters', 'rightPathMeters', 'durationSeconds']);
  const r = v.replay; ensure(object(r), 'Missing replay.');
  r.eventClock ??= 'run'; textFields(r, ['eventClock']); ensure(r.eventClock, 'Missing replay event clock.');
  textFields(r, ['jobId', 'failureReason']); boolFields(r, ['clockAligned']); numberFields(r, ['durationSeconds', 'captureStartRunSeconds']);
  ensure(['queued', 'processing', 'ready', 'failed'].includes(String(r.status)), 'Invalid replay status.');
  ensure(['learner', 'rehearsal', 'sample', 'unknown'].includes(String(r.source)), 'Invalid replay source.');
  // URLs from legacy exports must never become durable playback authority.
  for (const field of ['sourceVideoUrl', 'replayVideoUrl']) {
    ensure(r[field] === undefined || videoUrl(r[field]), 'Replay URLs must be HTTP(S) or local absolute paths.');
    delete r[field];
  }
  r.sourceArtifactId ??= ''; r.replayArtifactId ??= ''; r.jobRun ??= 0;
  textFields(r, ['sourceArtifactId', 'replayArtifactId']); numberFields(r, ['jobRun']);
  ensure(r.status !== 'ready' || r.source === 'sample' || (!!r.jobId && !!r.replayArtifactId), 'Ready replay requires a job and artifact ID.');
  ensure(r.status !== 'ready' || r.source !== 'unknown', 'Replay provenance unavailable.');
  ensure(r.status !== 'failed' || !!r.failureReason, 'Failed replay requires a reason.');
  const d = v.demo; ensure(object(d), 'Missing demo flags.'); textFields(d, ['patientId']);
  boolFields(d, ['enabled', 'showSuggestedQuestions', 'skipMarking', 'preExpose', 'timeLapseNonKeySteps']); numberFields(d, ['replayHighlightSeconds']);
  if (s.demoAssisted !== undefined) boolFields(s, ['demoAssisted']);
  s.demoAssisted = s.demoAssisted === true || d.enabled || d.skipMarking || d.preExpose || d.timeLapseNonKeySteps;
  Object.freeze(d);
  return v as unknown as RunResult;
}

export function feedbackFromFacts(result: RunResult) {
  const strengths: string[] = [];
  const improvements: string[] = [];
  for (const fact of result.diagnosis?.criticalFound ?? []) if (fact.label && strengths.length < 2) strengths.push(`Elicited: ${fact.label}`);
  for (const fact of result.diagnosis?.criticalMissed ?? []) if (fact.label && improvements.length < 2) improvements.push(`Revisit: ${fact.label}`);
  if (result.surgery.available) {
    for (const fact of result.surgery.milestones) if (strengths.length < 2) strengths.push(`Reached: ${fact.label}`);
    for (const fact of result.surgery.guardrailViolations) if (improvements.length < 2) improvements.push(`Review: ${timedFactText(fact)}`);
  }
  return { strengths, improvements,
    takeaway: improvements.length ? `Next attempt — ${improvements[0]}` : strengths.length ? `Carry forward — ${strengths[0]}` : 'No logged facts yet. Reflect on one deliberate action for your next attempt.' };
}
export function errorMarkers(result: RunResult) {
  const r = result.replay;
  if (!replayClocksAligned(result) || r.source !== 'learner' || !result.surgery.available) return [];
  return [...result.surgery.guardrailViolations, ...result.surgery.orderDeviations]
    .filter(f => f.timeKnown !== false)
    .map(f => ({ ...f, clipSeconds: f.atSeconds - r.captureStartRunSeconds }))
    .filter(f => f.clipSeconds >= 0 && f.clipSeconds <= r.durationSeconds);
}

/** One honest window centered near a logged key event; times are capture-relative. */
export function highlightWindow(result: RunResult) {
  const duration = result.replay.durationSeconds;
  if (!result.demo.enabled || result.demo.replayHighlightSeconds <= 0) return { start: 0, end: duration };
  const length = Math.min(20, result.demo.replayHighlightSeconds, duration);
  let at = 0;
  if (result.replay.source === 'learner' && replayClocksAligned(result) && result.surgery.available) {
    const toClip = (f: Fact) => f.atSeconds - result.replay.captureStartRunSeconds;
    const valid = (f: Fact) => f.timeKnown !== false && toClip(f) >= 0 && toClip(f) <= duration;
    const key = result.surgery.guardrailViolations.filter(valid).sort((a, b) => a.atSeconds - b.atSeconds)[0]
      ?? result.surgery.milestones.filter(f => /incis|ligat/i.test(`${f.id} ${f.label}`) && valid(f)).sort((a, b) => a.atSeconds - b.atSeconds)[0];
    if (key) at = Math.max(0, toClip(key) - 3);
  }
  const start = Math.min(at, Math.max(0, duration - length));
  return { start, end: start + length };
}

/** Optional v1 additions default to legacy measured facts when omitted. */
export function timedFactText(fact: Fact) {
  return `${fact.label} · ${fact.timeKnown === false ? 'time not recorded' : `${fact.atSeconds.toFixed(1)} s`}`;
}
export function hintsSummaryText(surgery: RunResult['surgery']) {
  return surgery.hintsAvailable === false ? 'Not measured' : String(surgery.hints.length);
}
export function decisionSummaryText(surgery: RunResult['surgery']) {
  if (surgery.decisionSummaryAvailable === true) return `${surgery.correctDecisions} / ${surgery.decisionCount}`;
  const assessed = surgery.decisions.filter(d => d.correctnessAvailable !== false);
  if (!assessed.length && surgery.decisions.length) return 'Not measured';
  const label = `${assessed.filter(d => d.correct).length} / ${assessed.length}`;
  return assessed.length === surgery.decisions.length ? label : `${label} assessed`;
}

export function replayClocksAligned(result: RunResult) {
  return result.replay.clockAligned && (result.replay.eventClock ?? 'run') === (result.surgery.eventClock ?? 'run');
}
