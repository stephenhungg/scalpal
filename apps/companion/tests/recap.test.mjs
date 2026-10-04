import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseRunResult, feedbackFromFacts, errorMarkers } from '../src/recap/runResult.ts';
import { sampleResult } from '../src/recap/sample.ts';
const fresh = () => structuredClone(sampleResult);
test('sample round-trips and both independent grader scores survive', () => {
  const result = parseRunResult(JSON.stringify(fresh()));
  assert.equal(result.diagnosis.total, 82); assert.equal(result.surgery.total, 76); assert.equal(result.isSample, true);
});
test('supports all gateway states and rejects unsupported states and missing failure reasons', () => {
  for (const status of ['queued', 'processing', 'ready', 'failed']) {
    const r = fresh(); r.replay.status = status; r.replay.failureReason = status === 'failed' ? 'No landmarks' : '';
    assert.equal(parseRunResult(JSON.stringify(r)).replay.status, status);
  }
  const r = fresh(); r.replay.status = 'running'; assert.throws(() => parseRunResult(JSON.stringify(r)), /status/);
  r.replay.status = 'failed'; assert.throws(() => parseRunResult(JSON.stringify(r)), /reason/);
});
test('rejects wrong schema, cross-session result, invalid score and unsafe media URLs', () => {
  const r = fresh(); assert.throws(() => parseRunResult(JSON.stringify(r), 'another-session'), /different session/);
  r.schemaVersion = 'v2'; assert.throws(() => parseRunResult(JSON.stringify(r)), /schemaVersion/);
  r.schemaVersion = sampleResult.schemaVersion; r.surgery.total = 101; assert.throws(() => parseRunResult(JSON.stringify(r)), /score range/);
  r.surgery.total = 76; r.replay.replayVideoUrl = 'javascript:alert(1)'; assert.throws(() => parseRunResult(JSON.stringify(r)), /URLs/);
});
test('feedback is bounded and uses fact fields, ignoring prose and ungraded surgery', () => {
  const r = fresh(); r.diagnosis.feedback = ['Invented excellent surgery']; r.diagnosis.spoken = 'Invented';
  const feedback = feedbackFromFacts(r); assert.equal(feedback.strengths.length, 2); assert.equal(feedback.improvements.length, 2);
  assert.ok(!JSON.stringify(feedback).includes('Invented'));
  r.surgery.available = false; assert.equal(feedbackFromFacts(r).strengths.length, 1);
  r.diagnosis = null; assert.deepEqual(feedbackFromFacts(r).strengths, []);
});
test('markers require learner provenance, verified clock and clip bounds', () => {
  const r = fresh(); r.replay.clockAligned = true; r.replay.durationSeconds = 20; r.replay.captureStartRunSeconds = 60;
  assert.deepEqual(errorMarkers(r), []);
  r.replay.source = 'learner'; assert.equal(errorMarkers(r)[0].clipSeconds, 7);
  r.replay.clockAligned = false; assert.deepEqual(errorMarkers(r), []);
  r.replay.clockAligned = true; r.replay.captureStartRunSeconds = 80; assert.deepEqual(errorMarkers(r), []);
});

test('Unity sample RunResult crosses the real JSON boundary without changing grades', () => {
  const raw = readFileSync(new URL('../../quest/Assets/Scalpal/Recap/Fixtures/sample-run-result.json', import.meta.url), 'utf8');
  const result = parseRunResult(raw);
  assert.deepEqual(result.diagnosis, JSON.parse(raw).diagnosis);
  assert.equal(result.surgery.total, JSON.parse(raw).surgery.total);
  assert.equal(result.replay.status, 'failed');
  assert.equal(result.surgery.available, true);
  assert.equal(result.diagnosis.total, 82);
  assert.equal(result.surgery.total, 76);
});

import { producedScorecard } from './preop-producer.ts';
test('accepts the actual EncounterSession.score producer, without invented risk arrays', () => {
  const r = fresh(); r.diagnosisAvailable = true; r.diagnosis = producedScorecard();
  assert.equal('risksFound' in r.diagnosis, false);
  assert.deepEqual(parseRunResult(JSON.stringify(r)).diagnosis, r.diagnosis);
});
test('explicit unavailable normalizes a Unity zero-filled diagnosis to null', () => {
  const r = fresh(); r.diagnosisAvailable = false; r.diagnosis = { total: 0, max: 0 };
  assert.equal(parseRunResult(JSON.stringify(r)).diagnosis, null);
});
test('ready exports carry artifact IDs and do not require expiring URLs', () => {
  const r = fresh(); r.replay.source = 'learner'; r.replay.jobId = 'job'; r.replay.sourceArtifactId = 'source'; r.replay.replayArtifactId = 'robot'; r.replay.jobRun = 1;
  delete r.replay.sourceVideoUrl; delete r.replay.replayVideoUrl;
  assert.equal(parseRunResult(JSON.stringify(r)).replay.replayArtifactId, 'robot');
});

test('judge highlight seeks near the first logged error rather than clip start', async () => {
  const { highlightWindow } = await import('../src/recap/runResult.ts');
  const r = fresh(); r.replay.source = 'learner'; r.replay.clockAligned = true;
  r.replay.captureStartRunSeconds = 10; r.replay.durationSeconds = 180;
  assert.deepEqual(highlightWindow(r), { start: 54, end: 74 });
});
test('signed URL rotation preserves artifact playback identity and position', async () => {
  const { playbackRefresh } = await import('../src/recap/replay.ts');
  const old = { replayArtifactId: 'robot-a', replayVideoUrl: 'https://example.test/old' };
  const next = { replayArtifactId: 'robot-a', replayVideoUrl: 'https://example.test/new' };
  assert.deepEqual(playbackRefresh(old, next, 12, false), { replace: false, position: 12 });
  assert.deepEqual(playbackRefresh(old, next, 12, true), { replace: true, position: 12 });
  assert.deepEqual(playbackRefresh(old, { ...next, replayArtifactId: 'robot-b' }, 12, false), { replace: true, position: 0 });
});
test('missing result stays missing unless demo sample is explicitly selected', async () => {
  const { initialResult } = await import('../src/recap/sample.ts');
  assert.equal(initialResult(false), null);
  assert.equal(initialResult(true)?.isSample, true);
});

test('available malformed diagnosis fails; unavailable survives lossless import and demo flags are immutable', () => {
  const r = fresh(); r.diagnosisAvailable = true; r.diagnosis.max = 0;
  assert.throws(() => parseRunResult(JSON.stringify(r)), /score range/);
  r.diagnosisAvailable = false;
  const parsed = parseRunResult(JSON.stringify(r));
  assert.equal(parsed.diagnosis, null); assert.equal(parsed.surgery.demoAssisted, true);
  assert.throws(() => { parsed.demo.enabled = false; }, TypeError);
});
test('durable import discards signed URLs and unknown provenance cannot become ready', () => {
  const r = fresh(); r.replay.replayVideoUrl = 'https://example.test/replay?expires=old';
  assert.equal('replayVideoUrl' in parseRunResult(JSON.stringify(r)).replay, false);
  r.replay.source = 'unknown';
  assert.throws(() => parseRunResult(JSON.stringify(r)), /requires|provenance/);
});
test('replay response rejects a stale attempt even with the same session and job', async () => {
  const { acceptReplayView } = await import('../src/recap/replay.ts');
  const r = fresh(); r.replay.jobId = 'j';
  const view = { schemaVersion: 'scalpal.replay.v1', sessionId: r.sessionId, attemptId: 'old-attempt', jobId: 'j' };
  assert.throws(() => acceptReplayView(r, view), /attempt/);
});
test('highlight falls back to incision and clamps the final window to video length', async () => {
  const { highlightWindow } = await import('../src/recap/runResult.ts');
  const r = fresh(); r.replay.source = 'learner'; r.replay.clockAligned = true; r.replay.captureStartRunSeconds = 60; r.replay.durationSeconds = 60;
  r.surgery.guardrailViolations = []; r.surgery.milestones = [{ id: 'first_incision', label: 'First incision', atSeconds: 118 }];
  assert.deepEqual(highlightWindow(r), { start: 40, end: 60 });
  r.replay.clockAligned = false; assert.deepEqual(highlightWindow(r), { start: 0, end: 20 });
});

test('ready gateway replay requires an explicitly kinematic worker result', async () => {
  const { acceptReplayView } = await import('../src/recap/replay.ts');
  const r = fresh(); r.replay.jobId = 'job'; r.replay.jobRun = 1;
  // Exact response fields emitted by services/api/src/recap.ts, including quality-derived replayKind.
  const view = { schemaVersion: 'scalpal.replay.v1', sessionId: r.sessionId, attemptId: r.attemptId,
    jobId: 'job', jobRun: 1, sourceArtifactId: 'raw', replayArtifactId: 'robot', source: 'rehearsal',
    status: 'ready', reason: '', progress: 1, stage: 'complete', sourceVideoUrl: 'https://example.test/source',
    replayVideoUrl: 'https://example.test/replay', expiresAtUnixMs: Date.now() + 300000, replayKind: 'kinematic', label: 'Rehearsal hand motion' };
  assert.equal(acceptReplayView(r, view), view);
  for (const replayKind of ['physics', 'unknown', undefined]) assert.throws(() => acceptReplayView(r, { ...view, replayKind }), /kinematic/);
});

test('decoded duration opens an imported zero-duration replay at its logged highlight', async () => {
  const { loadedReplayMetadata } = await import('../src/recap/replay.ts');
  const { highlightWindow } = await import('../src/recap/runResult.ts');
  const r = fresh(); r.replay.source = 'learner'; r.replay.clockAligned = true; r.replay.captureStartRunSeconds = 10; r.replay.durationSeconds = 0;
  const loaded = loadedReplayMetadata(r, 180, 0);
  assert.equal(loaded.result.replay.durationSeconds, 180);
  assert.deepEqual(highlightWindow(loaded.result), { start: 54, end: 74 });
  assert.equal(loaded.position, 54);
  assert.equal(loadedReplayMetadata(loaded.result, 180, 62).position, 62);
  assert.equal(loadedReplayMetadata(loaded.result, 60, 170).position, 60);
  assert.throws(() => loadedReplayMetadata(r, Infinity, 0), /duration/);
});

test('bearer credentials cannot be sent to a cleartext remote gateway', async () => {
  const { secureGatewayBase } = await import('../src/recap/replay.ts');
  assert.equal(secureGatewayBase('http://127.0.0.1:8788/'), 'http://127.0.0.1:8788');
  assert.equal(secureGatewayBase('https://gateway.example.test/'), 'https://gateway.example.test');
  assert.equal(secureGatewayBase('http://[::1]:8788'), 'http://[::1]:8788');
  for (const url of ['http://192.168.1.25:8788', 'http://localhost.example.test', 'ftp://localhost', 'https://user:pass@example.test'])
    assert.throws(() => secureGatewayBase(url), /HTTPS|credentials/);
});

test('shared Unity scorecard fixture stays identical to the current real preop producer', () => {
  const saved = JSON.parse(readFileSync(new URL('./fixtures/preop-scorecard.json', import.meta.url), 'utf8'));
  const actual = producedScorecard();
  assert.equal(actual.procedureId, 'open_appendectomy');
  assert.deepEqual(saved, actual);
});

test('real open-body grade preserves illustrative denominator, incomplete goals and unmeasured metrics', async () => {
  const { producedBodyGrade } = await import('./preop-producer.ts');
  const { decisionSummaryText, hintsSummaryText, timedFactText } = await import('../src/recap/runResult.ts');
  const grade = producedBodyGrade();
  const r = fresh();
  // Consumer DTO projection only: all scores, counts and completeness come from the real grader.
  r.surgery = { ...r.surgery, total: grade.earnedPoints, max: grade.availablePoints, grade: 'Illustrative · uncalibrated',
    rubric: grade.rubric, complete: grade.complete, completionReason: grade.reason,
    missingMilestones: [...grade.missingMilestones], missingMetrics: [...grade.missingMetrics],
    hintsAvailable: false, decisionSummaryAvailable: true, correctDecisions: grade.correctDecisions, decisionCount: grade.decisionCount,
    milestones: grade.metMilestones.map(id => ({ id, label: id, atSeconds: 0, timeKnown: false })),
    guardrailViolations: grade.guardrailIds.map(id => ({ id, label: id, atSeconds: 0, timeKnown: false })),
    decisions: [{ id: 'raw-choice', label: 'Unassessed raw choice', atSeconds: 0, timeKnown: false, correct: false, correctnessAvailable: false }],
    hints: [], bloodLossMl: grade.bloodLostMl };
  const parsed = parseRunResult(JSON.stringify(r));
  assert.equal(parsed.surgery.max, 80); assert.equal(parsed.surgery.total, grade.earnedPoints);
  assert.equal(parsed.surgery.rubric, 'illustrative_v1_uncalibrated'); assert.equal(parsed.surgery.complete, false);
  assert.deepEqual(parsed.surgery.missingMilestones, grade.missingMilestones);
  assert.equal(hintsSummaryText(parsed.surgery), 'Not measured');
  assert.equal(decisionSummaryText(parsed.surgery), `${grade.correctDecisions} / ${grade.decisionCount}`);
  assert.match(timedFactText(parsed.surgery.guardrailViolations[0]), /time not recorded/);
});

test('unknown event timestamps never produce a video marker, highlight or precise feedback time', async () => {
  const { highlightWindow } = await import('../src/recap/runResult.ts');
  const r = fresh(); r.diagnosis = null; r.diagnosisAvailable = false;
  r.replay.source = 'learner'; r.replay.clockAligned = true; r.replay.captureStartRunSeconds = 0; r.replay.durationSeconds = 180;
  r.surgery.guardrailViolations = [{ id: 'unmapped-guard', label: 'Guardrail hit', atSeconds: 67, timeKnown: false }];
  r.surgery.orderDeviations = []; r.surgery.milestones = [{ id: 'first_incision', label: 'First incision', atSeconds: 100, timeKnown: false }];
  assert.deepEqual(errorMarkers(r), []);
  assert.deepEqual(highlightWindow(r), { start: 0, end: 20 });
  assert.doesNotMatch(feedbackFromFacts(r).improvements[0], /67\.0 s/);
});

test('new availability flags validate types and legacy timed/decision records stay measured', async () => {
  const { decisionSummaryText, hintsSummaryText, timedFactText } = await import('../src/recap/runResult.ts');
  const r = fresh();
  r.surgery.guardrailViolations[0].timeKnown = 'yes';
  assert.throws(() => parseRunResult(JSON.stringify(r)), /guardrail/);
  delete r.surgery.guardrailViolations[0].timeKnown;
  r.surgery.decisionSummaryAvailable = true; r.surgery.correctDecisions = 2; r.surgery.decisionCount = 1;
  assert.throws(() => parseRunResult(JSON.stringify(r)), /decision/i);
  delete r.surgery.decisionSummaryAvailable; delete r.surgery.correctDecisions; delete r.surgery.decisionCount;
  const parsed = parseRunResult(JSON.stringify(r));
  assert.equal(hintsSummaryText(parsed.surgery), String(parsed.surgery.hints.length));
  assert.equal(decisionSummaryText(parsed.surgery), `${parsed.surgery.decisions.filter(d => d.correct).length} / ${parsed.surgery.decisions.length}`);
  assert.match(timedFactText(parsed.surgery.guardrailViolations[0]), /67\.0 s/);
  parsed.surgery.decisions.forEach(d => { d.correctnessAvailable = false; });
  assert.equal(decisionSummaryText(parsed.surgery), 'Not measured');
});

test('active-interaction grader clocks cannot masquerade as run-time video alignment', async () => {
  const { highlightWindow } = await import('../src/recap/runResult.ts');
  const r = fresh(); r.isSample = false; r.replay.jobId = 'clock-job'; r.replay.replayArtifactId = 'clock-artifact'; r.replay.source = 'learner'; r.replay.clockAligned = true; r.replay.captureStartRunSeconds = 10; r.replay.durationSeconds = 180;
  r.surgery.eventClock = 'active_interaction'; r.replay.eventClock = 'run';
  assert.deepEqual(errorMarkers(r), []);
  assert.deepEqual(highlightWindow(r), { start: 0, end: 20 });
  r.replay.eventClock = 'active_interaction';
  const parsed = parseRunResult(JSON.stringify(r));
  assert.equal(parsed.surgery.eventClock, 'active_interaction');
  assert.equal(parsed.replay.eventClock, 'active_interaction');
  assert.deepEqual(highlightWindow(parsed), { start: 54, end: 74 });
  assert.equal(errorMarkers(parsed)[0].clipSeconds, 57);
});
