// Local-only end-to-end check: Nathan's real gateway + SpacetimeDB module, Silas's real
// motion worker (scalpal-motion gateway-worker) on a real hand clip. Not committed.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { after, before, test } from 'node:test';
import {
  GATEWAY,
  WORKER_TOKEN,
  eventually,
  publishFresh,
  sessionWithRoles,
  startGateway,
  uid,
  type Gateway,
} from './harness';

const CLIP = process.env.E2E_CLIP!;
const MOTION_DIR = process.env.MOTION_DIR!;
let gw: Gateway;

before(async () => {
  publishFresh();
  gw = await startGateway();
});
after(async () => {
  await gw?.stop();
  setTimeout(() => process.exit(0), 200).unref();
});

test('real worker turns a real clip into a ready replay the viewer can download', { timeout: 300_000 }, async () => {
  const r = await sessionWithRoles();
  const bytes = readFileSync(CLIP);
  const artifactId = uid('art');
  const grantId = uid('grant');
  await r.headset.conn.reducers.requestUpload({
    grantId, artifactId, sessionId: r.sessionId, attemptId: r.attemptId, kind: 'raw_clip',
    filename: 'clip.mp4', contentType: 'video/mp4', declaredBytes: BigInt(bytes.length),
    sha256: createHash('sha256').update(bytes).digest('hex'),
  });
  const grant = await eventually(
    () => [...r.headset.conn.db.myTransferGrants.iter()].find(g => g.grantId === grantId && g.status === 'issued'),
    'upload grant'
  );
  const put = await fetch(grant.url!, { method: 'PUT', headers: { 'content-type': 'video/mp4' }, body: bytes });
  assert.equal(put.status, 200);
  await r.headset.conn.reducers.markUploaded({ artifactId });
  await eventually(
    () => [...r.operator.conn.db.sessionArtifacts.iter()].find(a => a.artifactId === artifactId && a.status === 'available'),
    'clip available'
  );

  const jobId = uid('job');
  await r.operator.conn.reducers.requestMotionJob({ jobId, inputArtifactId: artifactId, extraArtifactIds: [], configVersion: 'motion-v1' });

  const t0 = Date.now();
  // Async: the gateway runs in this process, so a blocking spawn would starve it.
  const { stdout: out } = await promisify(execFile)('uv', ['run', 'scalpal-motion', 'gateway-worker', '--once', '--mirrored', '--gateway', GATEWAY], {
    cwd: MOTION_DIR, env: { ...process.env, WORKER_TOKEN }, encoding: 'utf8', maxBuffer: 1 << 24,
  });
  console.log(out.trim(), `\nworker wall time ${((Date.now() - t0) / 1000).toFixed(1)}s`);

  const job = await eventually(
    () => [...r.viewer.conn.db.sessionMotionJobs.iter()].find(j => j.jobId === jobId && (j.status === 'ready' || j.status === 'failed')),
    'job finished'
  );
  assert.equal(job.status, 'ready', JSON.stringify(job, (_, v) => (typeof v === 'bigint' ? v.toString() : v)));
  const arts = job.outputArtifactIds.map(id => [...r.viewer.conn.db.sessionArtifacts.iter()].find(a => a.artifactId === id)!);
  console.log('outputs:', arts.map(a => `${a.kind}:${a.status}`).join(', '));
  console.log('quality:', JSON.stringify(job.quality, (_, v) => (typeof v === 'bigint' ? v.toString() : v)));
  assert.ok(arts.every(a => a.status === 'available'));

  // The viewer can fetch the trajectory the companion renders.
  const traj = arts.find(a => a.kind === 'robot_trajectory')!;
  const dl = uid('grant');
  await r.viewer.conn.reducers.requestDownload({ grantId: dl, artifactId: traj.artifactId });
  const g = await eventually(
    () => [...r.viewer.conn.db.myTransferGrants.iter()].find(x => x.grantId === dl && x.status === 'issued'),
    'download grant'
  );
  const body = await (await fetch(g.url!)).json();
  assert.equal(body.schema, 'scalpal.robot_trajectory.v1');
  console.log(`trajectory: ${body.robot.joints.length} joints x ${body.frames.t.length} frames`);
  r.closeAll();
});
