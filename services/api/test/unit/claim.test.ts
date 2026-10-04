// R3: a claim that succeeded in the database but never showed up in the
// gateway cache must not be abandoned while the gateway claims another job.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { runningJob, startFakeGateway, W1 } from './gateway-fixture';

test('a claim the cache never reflects is released, and no second job is claimed', async () => {
  const gw = startFakeGateway();
  gw.jobs.push(
    runningJob({ jobId: 'job_a', status: 'queued', run: 0, workerId: undefined }),
    runningJob({ jobId: 'job_b', status: 'queued', run: 0, workerId: undefined, createdAt: { microsSinceUnixEpoch: 2n } })
  );
  // claimMotionJob "succeeds" but the cache never updates (waits out the 5 s cache timeout).
  const res = await gw.worker(W1, '/claim', { leaseMs: 30_000 });
  assert.equal(res.status, 503, await res.text());
  const claims = gw.calls.filter(c => c.name === 'claimMotionJob');
  assert.deepEqual(claims.map(c => c.args.jobId), ['job_a']);
  const release = gw.calls.find(c => c.name === 'failMotionJob');
  assert.ok(release, 'the orphaned claim must be released right away, not left for the lease sweep');
  assert.equal(release.args.jobId, 'job_a');
  assert.equal(release.args.run, 1);
  assert.equal(release.args.retryable, true);
});

test('a claim the reducer rejects moves on to the next queued job', async () => {
  const gw = startFakeGateway();
  gw.jobs.push(
    runningJob({ jobId: 'job_a', status: 'queued', run: 0, workerId: undefined }),
    runningJob({ jobId: 'job_b', status: 'queued', run: 0, workerId: undefined, createdAt: { microsSinceUnixEpoch: 2n } })
  );
  gw.artifacts.push({ artifactId: 'art_in', storageKey: 'k/in.mp4', filename: 'in.mp4', kind: 'raw_clip' });
  gw.effects.claimMotionJob = args => {
    if (args.jobId === 'job_a') throw new Error('stale claim: run is 1');
    // Like the real cache, an update replaces the row object.
    const i = gw.jobs.findIndex(j => j.jobId === args.jobId);
    gw.jobs[i] = { ...gw.jobs[i], status: 'running', run: 1, workerId: args.workerId };
  };
  const res = await gw.worker(W1, '/claim', { leaseMs: 30_000 });
  const text = await res.text();
  assert.equal(res.status, 200, text);
  assert.equal((JSON.parse(text) as { job: { jobId: string } }).job.jobId, 'job_b');
  assert.equal(gw.calls.filter(c => c.name === 'failMotionJob').length, 0);
});
