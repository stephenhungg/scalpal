import type { RunResult } from './runResult.ts';
/** Authored UI fixture, never a measured performance or a participant recording. */
export const sampleResult: RunResult = {
  schemaVersion: 'scalpal.run_result.v1', runId: 'sample-run', sessionId: 'sample-session', attemptId: 'sample-attempt',
  encounterId: 'sample-encounter', patientId: 'sample-patient', procedureId: 'lap_appendectomy', isSample: true,
  diagnosis: { total: 82, max: 100, grade: 'B', procedureId: 'lap_appendectomy', procedureTitle: 'Appendectomy',
    procedureChosenCorrectly: true, sections: [{ id: 'history', label: 'History', score: 22, max: 30, found: ['Pain history'], missed: ['Medication history'] }],
    criticalFound: [{ kind: 'history', id: 'pain', label: 'pain history', why: 'Documents the presenting symptoms.' }],
    criticalMissed: [{ kind: 'history', id: 'medications', label: 'medication history', why: 'Identify relevant medication risks before surgery.' }],
    diagnosisGiven: 'Appendicitis', diagnosisExpected: 'Appendicitis', diagnosisResult: 'correct', differentialNamed: [], differentialSuggestions: [],
    risksFound: [], risksMissed: [], feedback: [], spoken: '' },
  surgery: { available: true, total: 76, max: 100, grade: 'Developing',
    milestones: [{ id: 'exposure', label: 'abdominal exposure', atSeconds: 42 }, { id: 'secured', label: 'vessel control', atSeconds: 110 }],
    guardrailViolations: [{ id: 'contact', label: 'unintended structure contact', atSeconds: 67 }], orderDeviations: [],
    decisions: [{ id: 'base', label: 'Identify the appendiceal base', atSeconds: 90, correct: true }], bloodLossMl: 12,
    economy: { available: true, leftPathMeters: 1.2, rightPathMeters: 1.8, durationSeconds: 180 }, hints: [] },
  replay: { jobId: '', status: 'ready', failureReason: '', sourceVideoUrl: '', replayVideoUrl: '/recap-sample.mp4',
    source: 'sample', durationSeconds: 20, captureStartRunSeconds: 0, clockAligned: false },
  demo: { enabled: true, patientId: 'sample-patient', showSuggestedQuestions: true, skipMarking: true, preExpose: true, timeLapseNonKeySteps: true, replayHighlightSeconds: 20 },
};
