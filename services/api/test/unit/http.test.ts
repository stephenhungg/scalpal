// R1: a run belongs to the worker that claimed it. The run reducers take no
// worker argument, so the gateway is the only place that can enforce this.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { runningJob, startFakeGateway, W1, W2 } from './gateway-fixture';

const RUN_ENDPOINTS: Array<[string, unknown]> = [
  ['heartbeat', { progress: 0.5 }],
  ['outputs', { kind: 'robot_trajectory', filename: 't.json', contentType: 'application/json' }],
  ['complete', { outputArtifactIds: [], quality: { framesTotal: 1, framesValid: 1, invalidIntervals: 0 } }],
  ['fail', { error: 'hijack', retryable: false }],
];

for (const [endpoint, body] of RUN_ENDPOINTS) {
  test(`${endpoint}: another worker cannot act on a claimed run`, async () => {
    const gw = startFakeGateway();
    gw.jobs.push(runningJob());
    const res = await gw.worker(W2, `/jobs/job_1/runs/1/${endpoint}`, body);
    assert.equal(res.status, 409, await res.text());
    assert.deepEqual(gw.calls, [], 'no reducer may run for a run the caller does not own');
  });
}

test('the claiming worker can still heartbeat and fail its own run', async () => {
  const gw = startFakeGateway();
  gw.jobs.push(runningJob());
  assert.equal((await gw.worker(W1, '/jobs/job_1/runs/1/heartbeat', { progress: 0.5 })).status, 200);
  assert.equal((await gw.worker(W1, '/jobs/job_1/runs/1/fail', { error: 'x', retryable: true })).status, 200);
  assert.deepEqual(
    gw.calls.map(c => c.name),
    ['heartbeatMotionJob', 'failMotionJob']
  );
});

test('a superseded run is refused before reaching the reducer', async () => {
  const gw = startFakeGateway();
  gw.jobs.push(runningJob({ run: 2 }));
  const res = await gw.worker(W1, '/jobs/job_1/runs/1/heartbeat');
  assert.equal(res.status, 409);
  assert.deepEqual(gw.calls, []);
});

test('an unknown job is 404 and reaches no reducer', async () => {
  const gw = startFakeGateway();
  const res = await gw.worker(W1, '/jobs/job_x/runs/1/complete', {
    outputArtifactIds: [],
    quality: { framesTotal: 1, framesValid: 1, invalidIntervals: 0 },
  });
  assert.equal(res.status, 404);
  assert.deepEqual(gw.calls, []);
});
