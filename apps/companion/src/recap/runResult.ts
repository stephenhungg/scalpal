/** scalpal.run_result.v1. Transport only: the encounter and surgery graders own scores. */
export interface Fact { id: string; label: string; atSeconds: number }
export interface FoundItem { kind: string; id: string; label: string; why: string }
export interface Diagnosis {
  total: number; max: number; grade: string; procedureId: string; procedureTitle: string;
  procedureChosenCorrectly: boolean;
  sections: { id: string; label: string; score: number; max: number; found: string[]; missed: string[] }[];
  criticalMissed: FoundItem[]; criticalFound: FoundItem[];
  diagnosisGiven: string; diagnosisExpected: string; diagnosisResult: 'correct' | 'partial' | 'incorrect' | 'missing';
  differentialNamed: string[]; differentialSuggestions: string[];
  risksFound: { id: string; type: string; label: string; severity: string; source: string }[];
  risksMissed: Diagnosis['risksFound']; feedback: string[]; spoken: string;
}
export interface RunResult {
  schemaVersion: 'scalpal.run_result.v1'; runId: string; sessionId: string; attemptId: string;
  encounterId: string; patientId: string; procedureId: string; isSample: boolean;
  diagnosis: Diagnosis | null;
  surgery: {
    available: boolean; total: number; max: number; grade: string;
    milestones: Fact[]; guardrailViolations: Fact[]; orderDeviations: Fact[];
    decisions: (Fact & { correct: boolean })[]; bloodLossMl: number;
    economy: { available: boolean; leftPathMeters: number; rightPathMeters: number; durationSeconds: number };
    hints: Fact[];
  };
  replay: { jobId: string; status: 'queued' | 'processing' | 'ready' | 'failed'; failureReason: string;
    sourceVideoUrl: string; replayVideoUrl: string; source: 'learner' | 'rehearsal' | 'sample';
    durationSeconds: number; captureStartRunSeconds: number; clockAligned: boolean };
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
function facts(value: unknown) { return Array.isArray(value) && value.every(x => object(x) && typeof x.id === 'string' && typeof x.label === 'string' && finite(x.atSeconds)); }
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
  if (v.diagnosis !== null) {
    const d = v.diagnosis; ensure(object(d), 'diagnosis must be a scorecard or null.');
    numberFields(d, ['total', 'max']); ensure(Number(d.max) > 0 && Number(d.total) <= Number(d.max), 'Invalid diagnosis score range.');
    textFields(d, ['grade', 'procedureId', 'procedureTitle', 'diagnosisGiven', 'diagnosisExpected', 'spoken']);
    boolFields(d, ['procedureChosenCorrectly']);
    ensure(['correct', 'partial', 'incorrect', 'missing'].includes(String(d.diagnosisResult)), 'Invalid diagnosis result.');
    ['differentialNamed', 'differentialSuggestions', 'feedback'].forEach(k => ensure(strings(d[k]), `Invalid diagnosis ${k}.`));
    ['criticalFound', 'criticalMissed'].forEach(k => ensure(Array.isArray(d[k]) && d[k].every(x => object(x) && ['kind', 'id', 'label', 'why'].every(f => typeof x[f] === 'string')), `Invalid ${k}.`));
    ['risksFound', 'risksMissed'].forEach(k => ensure(Array.isArray(d[k]) && d[k].every(x => object(x) && ['id', 'type', 'label', 'severity', 'source'].every(f => typeof x[f] === 'string')), `Invalid ${k}.`));
    ensure(Array.isArray(d.sections) && d.sections.every(x => object(x) && typeof x.id === 'string' && typeof x.label === 'string' && finite(x.score) && finite(x.max) && strings(x.found) && strings(x.missed)), 'Invalid diagnosis sections.');
  }
  const s = v.surgery; ensure(object(s), 'Missing surgery.'); boolFields(s, ['available']);
  numberFields(s, ['total', 'max', 'bloodLossMl']); textFields(s, ['grade']);
  ensure(!s.available || (Number(s.max) > 0 && Number(s.total) <= Number(s.max)), 'Invalid surgery score range.');
  ['milestones', 'guardrailViolations', 'orderDeviations', 'hints', 'decisions'].forEach(k => ensure(facts(s[k]), `Invalid surgery ${k}.`));
  ensure((s.decisions as Record<string, unknown>[]).every(x => typeof x.correct === 'boolean'), 'Invalid decision result.');
  ensure(object(s.economy), 'Missing economy.'); boolFields(s.economy, ['available']); numberFields(s.economy, ['leftPathMeters', 'rightPathMeters', 'durationSeconds']);
  const r = v.replay; ensure(object(r), 'Missing replay.');
  textFields(r, ['jobId', 'failureReason']); boolFields(r, ['clockAligned']); numberFields(r, ['durationSeconds', 'captureStartRunSeconds']);
  ensure(['queued', 'processing', 'ready', 'failed'].includes(String(r.status)), 'Invalid replay status.');
  ensure(['learner', 'rehearsal', 'sample'].includes(String(r.source)), 'Invalid replay source.');
  ensure(videoUrl(r.sourceVideoUrl) && videoUrl(r.replayVideoUrl), 'Replay URLs must be HTTP(S) or local absolute paths.');
  ensure(r.status !== 'ready' || !!r.replayVideoUrl, 'Ready replay requires a video URL.');
  ensure(r.status !== 'failed' || !!r.failureReason, 'Failed replay requires a reason.');
  const d = v.demo; ensure(object(d), 'Missing demo flags.'); textFields(d, ['patientId']);
  boolFields(d, ['enabled', 'showSuggestedQuestions', 'skipMarking', 'preExpose', 'timeLapseNonKeySteps']); numberFields(d, ['replayHighlightSeconds']);
  return v as unknown as RunResult;
}

export function feedbackFromFacts(result: RunResult) {
  const strengths: string[] = [];
  const improvements: string[] = [];
  for (const fact of result.diagnosis?.criticalFound ?? []) if (fact.label && strengths.length < 2) strengths.push(`Elicited: ${fact.label}`);
  for (const fact of result.diagnosis?.criticalMissed ?? []) if (fact.label && improvements.length < 2) improvements.push(`Revisit: ${fact.label}`);
  if (result.surgery.available) {
    for (const fact of result.surgery.milestones) if (strengths.length < 2) strengths.push(`Reached: ${fact.label}`);
    for (const fact of result.surgery.guardrailViolations) if (improvements.length < 2) improvements.push(`Review: ${fact.label} at ${fact.atSeconds.toFixed(1)} s`);
  }
  return { strengths, improvements,
    takeaway: improvements.length ? `Next attempt — ${improvements[0]}` : strengths.length ? `Carry forward — ${strengths[0]}` : 'No logged facts yet. Reflect on one deliberate action for your next attempt.' };
}
export function errorMarkers(result: RunResult) {
  const r = result.replay;
  if (!r.clockAligned || r.source !== 'learner' || !result.surgery.available) return [];
  return [...result.surgery.guardrailViolations, ...result.surgery.orderDeviations]
    .map(f => ({ ...f, clipSeconds: f.atSeconds - r.captureStartRunSeconds }))
    .filter(f => f.clipSeconds >= 0 && f.clipSeconds <= r.durationSeconds);
}
