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

test('Unity sample RunResult crosses the real JSON boundary unchanged', () => {
  const raw = readFileSync(new URL('../../quest/Assets/Scalpal/Recap/Fixtures/sample-run-result.json', import.meta.url), 'utf8');
  const result = parseRunResult(raw);
  assert.deepEqual(result, JSON.parse(raw));
  assert.equal(result.replay.status, 'failed');
  assert.equal(result.surgery.available, true);
  assert.equal(result.diagnosis.total, 82);
  assert.equal(result.surgery.total, 76);
});
