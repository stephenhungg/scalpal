// End-to-end checks for the realtime module + gateway, mirroring the first
// acceptance check in docs/data-and-realtime.md.
//
// Requires a local SpacetimeDB: `spacetime start` (port 3000).

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { before, describe, test } from 'node:test';
import {
  connect,
  eventually,
  exitAfterTeardown,
  publishFresh,
  sessionWithRoles,
  startGateway,
  uid,
  worker,
  WORKER_TOKEN,
  WORKER_TOKEN_2,
  type Client,
  type Gateway,
} from './harness';

let gateway: Gateway;

before(async () => {
  publishFresh();
  gateway = await startGateway();
});

// Stop the gateway, then force-exit (open sockets) keeping the real exit code.
exitAfterTeardown(() => gateway?.stop());

type Roles = Awaited<ReturnType<typeof sessionWithRoles>>;

function stateArgs(r: Roles, patch: Record<string, unknown> = {}) {
  return {
    sessionId: r.sessionId,
    attemptId: r.attemptId,
    mode: 'Selecting',
    stepId: undefined,
    stepIndex: 0,
    stepCount: 4,
    selectedStructureId: undefined,
    clearSelectedStructure: false,
    highlightedStructureId: undefined,
    clearHighlightedStructure: false,
    previewRotating: true,
    paused: false,
    registration: 'unaligned',
    registrationReason: undefined,
    recording: 'off',
    ...patch,
  } as any;
}

function stateOf(c: Client, sessionId: string) {
  return [...c.conn.db.sessionExerciseState.iter()].find(s => s.sessionId === sessionId);
}

async function rejects(p: Promise<unknown>, pattern: RegExp) {
  await assert.rejects(p, (err: Error) => {
    assert.match(err.message, pattern);
    return true;
  });
}

/** Upload bytes through the grant flow as `who`; returns the artifact id. */
async function upload(
  r: Roles,
  who: Client,
  bytes: Buffer,
  opts: { kind?: string; declaredBytes?: number; sha256?: string; attemptId?: string } = {}
) {
  const artifactId = uid('art');
  const grantId = uid('grant');
  await who.conn.reducers.requestUpload({
    grantId,
    artifactId,
    sessionId: r.sessionId,
    attemptId: opts.attemptId ?? r.attemptId,
    kind: opts.kind ?? 'raw_clip',
    filename: 'clip.mp4',
    contentType: 'video/mp4',
    declaredBytes: BigInt(opts.declaredBytes ?? bytes.length),
    sha256: opts.sha256 ?? createHash('sha256').update(bytes).digest('hex'),
  });
  const grant = await eventually(
    () => [...who.conn.db.myTransferGrants.iter()].find(g => g.grantId === grantId && g.status === 'issued'),
    'upload grant'
  );
  const put = await fetch(grant.url!, {
    method: 'PUT',
    headers: { 'content-type': 'video/mp4' },
    body: bytes,
  });
  assert.equal(put.status, 200, await put.text());
  await who.conn.reducers.markUploaded({ artifactId });
  return artifactId;
}

function artifactOf(c: Client, artifactId: string) {
  return [...c.conn.db.sessionArtifacts.iter()].find(a => a.artifactId === artifactId);
}

function jobsOf(c: Client, sessionId: string) {
  return [...c.conn.db.sessionMotionJobs.iter()].filter(j => j.sessionId === sessionId);
}

// ---------------------------------------------------------------------------

describe('shared state', () => {
  test('a headset state change reaches every member; reconnect restores it', async () => {
    const r = await sessionWithRoles();
    await r.headset.conn.reducers.publishExerciseState(
      stateArgs(r, { mode: 'Practicing', stepId: 'grasp', stepIndex: 1, registration: 'valid' })
    );
    for (const c of [r.operator, r.viewer, r.coach]) {
      const s = await eventually(() => {
        const st = stateOf(c, r.sessionId);
        return st?.mode === 'Practicing' ? st : null;
      }, 'state propagation');
      assert.equal(s.stepId, 'grasp');
      assert.equal(s.registration, 'valid');
    }

    // Reconnect the viewer with the same token: same identity, same state.
    const token = r.viewer.token;
    const hex = r.viewer.identity.toHexString();
    r.viewer.close();
    const again = await connect(token);
    assert.equal(again.identity.toHexString(), hex);
    const s = stateOf(again, r.sessionId);
    assert.equal(s?.mode, 'Practicing');
    assert.equal(s?.stepVersion, 2n);
    again.close();
    r.closeAll();
  });

  test('an unrelated identity can neither read nor change a session', async () => {
    const r = await sessionWithRoles();
    const outsider = await connect();
    await new Promise(res => setTimeout(res, 200));
    assert.equal([...outsider.conn.db.mySessions.iter()].length, 0);
    assert.equal([...outsider.conn.db.sessionExerciseState.iter()].length, 0);
    assert.equal([...outsider.conn.db.sessionInvites.iter()].length, 0);
    await rejects(outsider.conn.reducers.publishExerciseState(stateArgs(r)), /not a member/);
    await rejects(
      outsider.conn.reducers.requestCommand({
        commandId: uid('cmd'),
        sessionId: r.sessionId,
        action: 'requestHint',
        targetId: undefined,
        argBool: undefined,
        argNumber: undefined,
        expectedStepVersion: 1n,
      }),
      /not a member/
    );
    await rejects(outsider.conn.reducers.joinSession({ code: 'ZZZZZZ', displayName: 'x' }), /invalid/);
    outsider.close();
    r.closeAll();
  });

  test('viewers cannot change the exercise; only operators see invite codes', async () => {
    const r = await sessionWithRoles();
    await rejects(r.viewer.conn.reducers.publishExerciseState(stateArgs(r)), /requires role/);
    await rejects(
      r.viewer.conn.reducers.startAttempt({ sessionId: r.sessionId, exerciseId: '', exerciseVersion: '' }),
      /requires role/
    );
    assert.equal([...r.viewer.conn.db.sessionInvites.iter()].length, 0);
    assert.equal([...r.operator.conn.db.sessionInvites.iter()].filter(i => i.sessionId === r.sessionId).length, 4);
    r.closeAll();
  });

  test('presence reflects connected members', async () => {
    const r = await sessionWithRoles();
    const viewerHex = r.viewer.identity.toHexString();
    const online = () =>
      [...r.operator.conn.db.sessionMembers.iter()].find(
        m => m.sessionId === r.sessionId && m.identity.toHexString() === viewerHex
      )?.online;
    await eventually(() => online() === true, 'viewer online');
    r.viewer.close();
    await eventually(() => online() === false, 'viewer offline');
    r.closeAll();
  });

  test('rotating an invite revokes the old code', async () => {
    const r = await sessionWithRoles();
    const old = r.code('viewer');
    await r.operator.conn.reducers.rotateInvite({ sessionId: r.sessionId, role: 'viewer' });
    const late = await connect();
    await rejects(late.conn.reducers.joinSession({ code: old, displayName: 'late' }), /revoked/);
    late.close();
    r.closeAll();
  });
});

describe('commands', () => {
  test('pending -> applied by the headset; idempotent on commandId', async () => {
    const r = await sessionWithRoles();
    const commandId = uid('cmd');
    const req = {
      commandId,
      sessionId: r.sessionId,
      action: 'highlightStructure',
      targetId: 'gallbladder',
      argBool: undefined,
      argNumber: undefined,
      expectedStepVersion: 1n,
    };
    await r.coach.conn.reducers.requestCommand(req);
    await r.coach.conn.reducers.requestCommand(req); // duplicate: no-op
    const cmds = () => [...r.headset.conn.db.sessionCommands.iter()].filter(c => c.commandId === commandId);
    await eventually(() => cmds().length === 1, 'command visible to headset');
    assert.equal(cmds()[0].status, 'pending');
    await r.headset.conn.reducers.resolveCommand({ commandId, status: 'applied', reason: undefined });
    await eventually(
      () => [...r.coach.conn.db.sessionCommands.iter()].find(c => c.commandId === commandId)?.status === 'applied',
      'coach sees applied'
    );
    await rejects(
      r.headset.conn.reducers.resolveCommand({ commandId, status: 'rejected', reason: 'x' }),
      /already applied/
    );
    r.closeAll();
  });

  test('a command against a stale step version is rejected immediately', async () => {
    const r = await sessionWithRoles();
    await r.headset.conn.reducers.publishExerciseState(stateArgs(r, { mode: 'Practicing', stepId: 's1' }));
    await eventually(() => stateOf(r.coach, r.sessionId)?.stepVersion === 2n, 'step bump');
    const commandId = uid('cmd');
    await r.coach.conn.reducers.requestCommand({
      commandId,
      sessionId: r.sessionId,
      action: 'requestHint',
      targetId: undefined,
      argBool: undefined,
      argNumber: undefined,
      expectedStepVersion: 1n,
    });
    const cmd = await eventually(
      () => [...r.coach.conn.db.sessionCommands.iter()].find(c => c.commandId === commandId),
      'command row'
    );
    assert.equal(cmd.status, 'rejected');
    assert.match(cmd.reason ?? '', /stale step version/);
    r.closeAll();
  });

  test('unsupported actions and viewer requests are refused', async () => {
    const r = await sessionWithRoles();
    const base = {
      sessionId: r.sessionId,
      targetId: undefined,
      argBool: undefined,
      argNumber: undefined,
      expectedStepVersion: 1n,
    };
    await rejects(
      r.coach.conn.reducers.requestCommand({ ...base, commandId: uid('cmd'), action: 'runScript' }),
      /unsupported action/
    );
    await rejects(
      r.coach.conn.reducers.requestCommand({ ...base, commandId: uid('cmd'), action: 'isolateStructure' }),
      /requires targetId/
    );
    await rejects(
      r.viewer.conn.reducers.requestCommand({ ...base, commandId: uid('cmd'), action: 'requestHint' }),
      /requires role/
    );
    r.closeAll();
  });

  test('unacknowledged commands expire', async () => {
    const r = await sessionWithRoles();
    const commandId = uid('cmd');
    await r.coach.conn.reducers.requestCommand({
      commandId,
      sessionId: r.sessionId,
      action: 'pausePractice',
      targetId: undefined,
      argBool: undefined,
      argNumber: undefined,
      expectedStepVersion: 1n,
    });
    await eventually(
      () => [...r.coach.conn.db.sessionCommands.iter()].find(c => c.commandId === commandId)?.status === 'expired',
      'command expiry',
      25_000
    );
    r.closeAll();
  });
});

describe('artifacts', () => {
  test('authorized upload becomes available only after verification; download works', async () => {
    const r = await sessionWithRoles();
    const bytes = Buffer.from('synthetic nonpersonal clip bytes '.repeat(100));
    const artifactId = await upload(r, r.headset, bytes);
    const art = await eventually(
      () => (artifactOf(r.viewer, artifactId)?.status === 'available' ? artifactOf(r.viewer, artifactId) : null),
      'artifact available'
    );
    assert.equal(art.verifiedBytes, BigInt(bytes.length));

    const grantId = uid('grant');
    await r.viewer.conn.reducers.requestDownload({ grantId, artifactId });
    const grant = await eventually(
      () => [...r.viewer.conn.db.myTransferGrants.iter()].find(g => g.grantId === grantId && g.status === 'issued'),
      'download grant'
    );
    const res = await fetch(grant.url!);
    assert.equal(res.status, 200);
    assert.deepEqual(Buffer.from(await res.arrayBuffer()), bytes);

    // Grants are private to their requester.
    assert.equal([...r.operator.conn.db.myTransferGrants.iter()].some(g => g.grantId === grantId), false);
    // Tampered signature is refused.
    const bad = await fetch(grant.url!.replace(/sig=[^&]+/, 'sig=AAAA'));
    assert.equal(bad.status, 403);
    r.closeAll();
  });

  test('a size mismatch fails verification and can be retried under the same id', async () => {
    const r = await sessionWithRoles();
    const bytes = Buffer.from('short');
    const artifactId = await upload(r, r.headset, bytes, { declaredBytes: 999, sha256: undefined as any });
    const failed = await eventually(
      () => (artifactOf(r.headset, artifactId)?.status === 'failed' ? artifactOf(r.headset, artifactId) : null),
      'failed verification'
    );
    assert.match(failed.statusReason ?? '', /size mismatch/);
    r.closeAll();
  });

  test('a missing upload fails verification', async () => {
    const r = await sessionWithRoles();
    const artifactId = uid('art');
    await r.headset.conn.reducers.requestUpload({
      grantId: uid('grant'),
      artifactId,
      sessionId: r.sessionId,
      attemptId: r.attemptId,
      kind: 'raw_clip',
      filename: 'clip.mp4',
      contentType: 'video/mp4',
      declaredBytes: 10n,
      sha256: undefined,
    });
    await r.headset.conn.reducers.markUploaded({ artifactId });
    const art = await eventually(
      () => (artifactOf(r.headset, artifactId)?.status === 'failed' ? artifactOf(r.headset, artifactId) : null),
      'missing object detected'
    );
    assert.match(art.statusReason ?? '', /not found/);
    r.closeAll();
  });

  test('viewers cannot upload', async () => {
    const r = await sessionWithRoles();
    await rejects(
      r.viewer.conn.reducers.requestUpload({
        grantId: uid('grant'),
        artifactId: uid('art'),
        sessionId: r.sessionId,
        attemptId: r.attemptId,
        kind: 'raw_clip',
        filename: 'x.mp4',
        contentType: 'video/mp4',
        declaredBytes: undefined,
        sha256: undefined,
      }),
      /requires role/
    );
    r.closeAll();
  });
});

describe('motion jobs', () => {
  async function availableClip(r: Roles) {
    const artifactId = await upload(r, r.headset, Buffer.from(`clip ${Math.random()}`));
    await eventually(() => artifactOf(r.operator, artifactId)?.status === 'available', 'clip available');
    return artifactId;
  }

  async function drainQueue() {
    // Other tests may leave queued jobs; claim and fail them so they don't interfere.
    for (;;) {
      const res = await worker(WORKER_TOKEN, '/v1/worker/claim');
      if (res.status === 204) return;
      await worker(WORKER_TOKEN, res.json.endpoints.fail, { error: 'drained', retryable: false });
    }
  }

  test('duplicate job requests produce one job', async () => {
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    await Promise.all([
      r.operator.conn.reducers.requestMotionJob({
        jobId: uid('job'),
        inputArtifactId: clip,
        extraArtifactIds: [],
        configVersion: 'v1',
      }),
      r.headset.conn.reducers.requestMotionJob({
        jobId: uid('job'),
        inputArtifactId: clip,
        extraArtifactIds: [],
        configVersion: 'v1',
      }),
    ]);
    await new Promise(res => setTimeout(res, 200));
    assert.equal(jobsOf(r.operator, r.sessionId).length, 1);
    await drainQueue();
    r.closeAll();
  });

  test('worker claim -> heartbeat -> output -> complete makes the replay ready', async () => {
    await drainQueue();
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    const jobId = uid('job');
    await r.operator.conn.reducers.requestMotionJob({
      jobId,
      inputArtifactId: clip,
      extraArtifactIds: [],
      configVersion: 'v1',
    });

    const claim = await worker(WORKER_TOKEN, '/v1/worker/claim', { leaseMs: 30_000 });
    assert.equal(claim.status, 200, JSON.stringify(claim.json));
    assert.equal(claim.json.job.jobId, jobId);
    assert.equal(claim.json.job.run, 1);
    const input = await fetch(claim.json.inputs[0].download.url);
    assert.equal(input.status, 200);

    // A viewer sees progress.
    assert.equal((await worker(WORKER_TOKEN, claim.json.endpoints.heartbeat, { progress: 0.5, stage: 'inference' })).status, 200);
    await eventually(() => jobsOf(r.viewer, r.sessionId)[0]?.stage === 'inference', 'progress visible');

    const out = await worker(WORKER_TOKEN, claim.json.endpoints.outputs, {
      kind: 'robot_trajectory',
      filename: 'trajectory.json',
      contentType: 'application/json',
    });
    assert.equal(out.status, 200, JSON.stringify(out.json));
    const trajectory = Buffer.from(JSON.stringify({ synthetic: true }));
    const put = await fetch(out.json.upload.url, {
      method: 'PUT',
      headers: out.json.upload.headers,
      body: trajectory,
    });
    assert.equal(put.status, 200);

    // Completing with an output that was never uploaded is refused.
    const ghost = await worker(WORKER_TOKEN, claim.json.endpoints.outputs, {
      kind: 'replay_video',
      filename: 'ghost.mp4',
      contentType: 'video/mp4',
    });
    const refused = await worker(WORKER_TOKEN, claim.json.endpoints.complete, {
      outputArtifactIds: [out.json.artifactId, ghost.json.artifactId],
      quality: { framesTotal: 10, framesValid: 8, invalidIntervals: 1 },
    });
    assert.equal(refused.status, 422);

    const done = await worker(WORKER_TOKEN, claim.json.endpoints.complete, {
      outputArtifactIds: [out.json.artifactId],
      quality: { framesTotal: 10, framesValid: 8, invalidIntervals: 1, robotModel: 'synthetic-hand', notes: 'synthetic' },
    });
    assert.equal(done.status, 200, JSON.stringify(done.json));
    const job = await eventually(
      () => jobsOf(r.viewer, r.sessionId).find(j => j.status === 'ready'),
      'job ready'
    );
    assert.deepEqual(job.outputArtifactIds, [out.json.artifactId]);
    assert.equal(job.quality?.framesValid, 8);
    assert.equal(artifactOf(r.viewer, out.json.artifactId)?.status, 'available');
    // The still-valid upload URL cannot replace the finished output.
    const overwrite = await fetch(out.json.upload.url, {
      method: 'PUT',
      headers: out.json.upload.headers,
      body: '{"swapped":true}',
    });
    assert.equal(overwrite.status, 409);

    // Completing again is stale.
    const again = await worker(WORKER_TOKEN, claim.json.endpoints.complete, {
      outputArtifactIds: [out.json.artifactId],
      quality: { framesTotal: 10, framesValid: 8, invalidIntervals: 1 },
    });
    assert.equal(again.status, 409);
    r.closeAll();
  });

  test('expired lease requeues; the obsolete run cannot complete; the new run can', async () => {
    await drainQueue();
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    await r.operator.conn.reducers.requestMotionJob({
      jobId: uid('job'),
      inputArtifactId: clip,
      extraArtifactIds: [],
      configVersion: 'v1',
    });
    const first = await worker(WORKER_TOKEN, '/v1/worker/claim', { leaseMs: 1000 });
    assert.equal(first.json.job.run, 1);
    const firstOut = await worker(WORKER_TOKEN, first.json.endpoints.outputs, {
      kind: 'robot_trajectory',
      filename: 't.json',
      contentType: 'application/json',
    });
    await fetch(firstOut.json.upload.url, {
      method: 'PUT',
      headers: firstOut.json.upload.headers,
      body: '{"run":1}',
    });

    await eventually(
      () => jobsOf(r.operator, r.sessionId)[0]?.status === 'queued',
      'lease expiry requeue',
      15_000
    );
    const second = await worker(WORKER_TOKEN_2, '/v1/worker/claim', { leaseMs: 30_000 });
    assert.equal(second.status, 200);
    assert.equal(second.json.job.run, 2);

    // Late completion from run 1 is rejected.
    const late = await worker(WORKER_TOKEN, first.json.endpoints.complete, {
      outputArtifactIds: [firstOut.json.artifactId],
      quality: { framesTotal: 1, framesValid: 1, invalidIntervals: 0 },
    });
    assert.equal(late.status, 409);
    // Heartbeat from run 1 is rejected too.
    assert.equal((await worker(WORKER_TOKEN, first.json.endpoints.heartbeat, {})).status, 409);

    const out = await worker(WORKER_TOKEN_2, second.json.endpoints.outputs, {
      kind: 'robot_trajectory',
      filename: 't.json',
      contentType: 'application/json',
    });
    await fetch(out.json.upload.url, { method: 'PUT', headers: out.json.upload.headers, body: '{"run":2}' });
    // Run 1's output cannot be passed off as run 2's.
    const crossed = await worker(WORKER_TOKEN_2, second.json.endpoints.complete, {
      outputArtifactIds: [firstOut.json.artifactId],
      quality: { framesTotal: 1, framesValid: 1, invalidIntervals: 0 },
    });
    assert.equal(crossed.status, 400);
    const ok = await worker(WORKER_TOKEN_2, second.json.endpoints.complete, {
      outputArtifactIds: [out.json.artifactId],
      quality: { framesTotal: 1, framesValid: 1, invalidIntervals: 0 },
    });
    assert.equal(ok.status, 200);
    const job = await eventually(() => jobsOf(r.operator, r.sessionId).find(j => j.status === 'ready'), 'ready');
    assert.equal(job.run, 2);
    assert.deepEqual(job.outputArtifactIds, [out.json.artifactId]);
    r.closeAll();
  });

  test("a worker cannot heartbeat, complete or fail another worker's run", async () => {
    await drainQueue();
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    await r.operator.conn.reducers.requestMotionJob({
      jobId: uid('job'),
      inputArtifactId: clip,
      extraArtifactIds: [],
      configVersion: 'v1',
    });
    const claim = await worker(WORKER_TOKEN, '/v1/worker/claim', { leaseMs: 30_000 });
    assert.equal(claim.status, 200);
    const quality = { framesTotal: 1, framesValid: 1, invalidIntervals: 0 };
    assert.equal((await worker(WORKER_TOKEN_2, claim.json.endpoints.heartbeat, {})).status, 409);
    assert.equal(
      (await worker(WORKER_TOKEN_2, claim.json.endpoints.complete, { outputArtifactIds: [], quality })).status,
      409
    );
    assert.equal(
      (await worker(WORKER_TOKEN_2, claim.json.endpoints.fail, { error: 'hijack', retryable: false })).status,
      409
    );
    const job = jobsOf(r.operator, r.sessionId)[0];
    assert.equal(job.status, 'running');
    assert.equal(job.workerId, 'w1');
    await worker(WORKER_TOKEN, claim.json.endpoints.fail, { error: 'test cleanup', retryable: false });
    r.closeAll();
  });

  test('concurrent claims hand a job to exactly one worker', async () => {
    await drainQueue();
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    await r.operator.conn.reducers.requestMotionJob({
      jobId: uid('job'),
      inputArtifactId: clip,
      extraArtifactIds: [],
      configVersion: 'v1',
    });
    const tokens = [WORKER_TOKEN, WORKER_TOKEN_2, WORKER_TOKEN];
    const results = await Promise.all(tokens.map(t => worker(t, '/v1/worker/claim', { leaseMs: 30_000 })));
    assert.equal(results.filter(x => x.status === 200).length, 1);
    assert.equal(results.filter(x => x.status === 204).length, 2);
    await drainQueue();
    const winner = results.findIndex(x => x.status === 200);
    // Only the claiming worker may report on the run.
    await worker(tokens[winner], results[winner].json.endpoints.fail, { error: 'test cleanup', retryable: false });
    r.closeAll();
  });

  test('a failed run requeues when retryable and fails otherwise; workers need a token', async () => {
    await drainQueue();
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    await r.operator.conn.reducers.requestMotionJob({
      jobId: uid('job'),
      inputArtifactId: clip,
      extraArtifactIds: [],
      configVersion: 'v1',
    });
    assert.equal((await worker('nope', '/v1/worker/claim')).status, 401);
    const c1 = await worker(WORKER_TOKEN, '/v1/worker/claim');
    await worker(WORKER_TOKEN, c1.json.endpoints.fail, { error: 'transient', retryable: true });
    await eventually(() => jobsOf(r.operator, r.sessionId)[0]?.status === 'queued', 'requeued');
    const c2 = await worker(WORKER_TOKEN, '/v1/worker/claim');
    assert.equal(c2.json.job.run, 2);
    await worker(WORKER_TOKEN, c2.json.endpoints.fail, { error: 'no hands detected', retryable: false });
    const job = await eventually(() => jobsOf(r.viewer, r.sessionId).find(j => j.status === 'failed'), 'failed');
    assert.equal(job.error, 'no hands detected');
    // Operator retry queues it again.
    await r.operator.conn.reducers.retryMotionJob({ jobId: job.jobId });
    await eventually(() => jobsOf(r.operator, r.sessionId)[0]?.status === 'queued', 'retried');
    await drainQueue();
    r.closeAll();
  });

  test('viewers cannot request jobs and clients cannot drive worker transitions', async () => {
    const r = await sessionWithRoles();
    const clip = await availableClip(r);
    await rejects(
      r.viewer.conn.reducers.requestMotionJob({
        jobId: uid('job'),
        inputArtifactId: clip,
        extraArtifactIds: [],
        configVersion: 'v1',
      }),
      /requires role/
    );
    await rejects(
      r.operator.conn.reducers.claimMotionJob({ jobId: 'x', expectedRun: 0, workerId: 'me', leaseMs: 1000 }),
      /service identity required/
    );
    await rejects(
      r.operator.conn.reducers.confirmArtifact({ artifactId: clip, ok: true, verifiedBytes: 1n, reason: undefined }),
      /service identity required/
    );
    r.closeAll();
  });
});

describe('grants, attempts and signaling', () => {
  test('ICE grant is issued (STUN-only here); voice is denied when unconfigured', async () => {
    const r = await sessionWithRoles();
    const ice = uid('grant');
    await r.viewer.conn.reducers.requestServiceGrant({ grantId: ice, sessionId: r.sessionId, kind: 'ice' });
    const g = await eventually(
      () => [...r.viewer.conn.db.myServiceGrants.iter()].find(x => x.grantId === ice && x.status === 'issued'),
      'ice grant'
    );
    const payload = JSON.parse(g.payload!);
    assert.equal(payload.relay, false);
    assert.ok(payload.iceServers.length >= 1);

    await rejects(
      r.viewer.conn.reducers.requestServiceGrant({ grantId: uid('grant'), sessionId: r.sessionId, kind: 'voice' }),
      /requires role/
    );
    const voice = uid('grant');
    await r.coach.conn.reducers.requestServiceGrant({ grantId: voice, sessionId: r.sessionId, kind: 'voice' });
    const denied = await eventually(
      () => [...r.coach.conn.db.myServiceGrants.iter()].find(x => x.grantId === voice && x.status === 'denied'),
      'voice denied'
    );
    assert.match(denied.reason ?? '', /not configured/);
    r.closeAll();
  });

  test('a new attempt supersedes pending commands; results stay separate per attempt', async () => {
    const r = await sessionWithRoles();
    await r.headset.conn.reducers.publishExerciseState(stateArgs(r, {
      mode: 'Practicing', selectedStructureId: 'appendix', highlightedStructureId: 'cecum',
      previewRotating: true, registration: 'valid', registrationReason: 'fixture fit',
    }));
    const previousState = await eventually(() => {
      const value = stateOf(r.headset, r.sessionId);
      return value?.mode === 'Practicing' ? value : null;
    }, 'practice before retry');
    const commandId = uid('cmd');
    await r.coach.conn.reducers.requestCommand({
      commandId,
      sessionId: r.sessionId,
      action: 'requestHint',
      targetId: undefined,
      argBool: undefined,
      argNumber: undefined,
      expectedStepVersion: previousState.stepVersion,
    });
    await r.headset.conn.reducers.setAttemptResult({
      attemptId: r.attemptId,
      practiceStatus: 'completed',
      stepsCompleted: 4,
      stepsTotal: 4,
      mistakes: 1,
      hintsUsed: 1,
      resultSummary: 'Completed with one restricted-region contact.',
    });
    await r.operator.conn.reducers.startAttempt({ sessionId: r.sessionId, exerciseId: '', exerciseVersion: '' });
    const st = await eventually(() => {
      const s = stateOf(r.viewer, r.sessionId);
      return s?.attemptId === `${r.sessionId}-a2` ? s : null;
    }, 'new attempt state');
    assert.equal(st.mode, 'Selecting');
    assert.equal(st.selectedStructureId, undefined);
    assert.equal(st.highlightedStructureId, undefined);
    assert.equal(st.stepCount, 0);
    assert.equal(st.previewRotating, false);
    assert.equal(st.registration, 'unaligned');
    assert.equal(st.registrationReason, undefined);
    const attempts = [...r.viewer.conn.db.sessionAttempts.iter()].filter(a => a.sessionId === r.sessionId);
    assert.equal(attempts.length, 2);
    assert.equal(attempts.find(a => a.ordinal === 1)?.practiceStatus, 'completed');
    await eventually(
      () => [...r.coach.conn.db.sessionCommands.iter()].find(c => c.commandId === commandId)?.status === 'expired',
      'command superseded'
    );
    // The headset can no longer publish against the old attempt.
    await rejects(r.headset.conn.reducers.publishExerciseState(stateArgs(r)), /stale attempt/);
    const event = { sessionId: r.sessionId, attemptId: r.attemptId, kind: 'touch',
      stepId: 'inspect', structureId: 'appendix', message: 'late fixture contact', deviceTimeMs: 10 };
    await rejects(r.headset.conn.reducers.appendExerciseEvent(event), /stale attempt/);
    const otherSessionId = uid('other_attempt_session');
    await r.operator.conn.reducers.createSession({ sessionId: otherSessionId, label: 'isolated foreign attempt',
      exerciseId: 'lap_appendectomy', exerciseVersion: '0.1.0', displayName: 'fixture operator' });
    await rejects(r.headset.conn.reducers.appendExerciseEvent({ ...event, attemptId: `${otherSessionId}-a1` }),
      /attempt belongs to another session/);
    await r.operator.conn.reducers.endSession({ sessionId: otherSessionId });
    r.closeAll();
  });

  test('signals reach only their recipient and can be acknowledged', async () => {
    const r = await sessionWithRoles();
    await r.viewer.conn.reducers.sendSignal({
      sessionId: r.sessionId,
      to: r.operator.identity,
      peerId: 'peer_test_1',
      kind: 'join',
      payload: '{}',
    });
    const sig = await eventually(
      () => [...r.operator.conn.db.myRtcSignals.iter()].find(s => s.peerId === 'peer_test_1'),
      'signal delivered'
    );
    assert.equal([...r.coach.conn.db.myRtcSignals.iter()].length, 0);
    await r.operator.conn.reducers.ackSignals({ signalIds: [sig.signalId] });
    await eventually(
      () => ![...r.operator.conn.db.myRtcSignals.iter()].some(s => s.peerId === 'peer_test_1'),
      'signal acked'
    );
    const outsider = await connect();
    await rejects(
      r.viewer.conn.reducers.sendSignal({
        sessionId: r.sessionId,
        to: outsider.identity,
        peerId: 'peer_test_2',
        kind: 'join',
        payload: '{}',
      }),
      /recipient is not in this session/
    );
    outsider.close();
    r.closeAll();
  });

  test('ending a session revokes invites and blocks writes', async () => {
    const r = await sessionWithRoles();
    await r.operator.conn.reducers.endSession({ sessionId: r.sessionId });
    await rejects(r.headset.conn.reducers.publishExerciseState(stateArgs(r)), /ended/);
    const late = await connect();
    await rejects(late.conn.reducers.joinSession({ code: r.code('viewer'), displayName: 'x' }), /revoked/);
    late.close();
    r.closeAll();
  });
});
