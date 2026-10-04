import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Readable } from 'node:stream';
import { BASE, runningJob, startFakeGateway } from './gateway-fixture';
import { Unavailable } from '../../src/realtime';

const route = '/v1/sessions/ses_1/replay/job_1';
const headers = { authorization: 'Bearer verified-session-token' };
function fixture(status = 'ready', patch: Record<string, unknown> = {}) {
  const gw = startFakeGateway(async (token, session) => token === 'verified-session-token' && session === 'ses_1');
  gw.jobs.push(runningJob({ status, outputArtifactIds: ['art_replay'], ...patch }));
  gw.artifacts.push(
    { artifactId: 'art_in', sessionId: 'ses_1', attemptId: 'ses_1-a1', kind: 'raw_clip', status: 'available', contentType: 'video/mp4', filename: 'source.mp4', storageKey: 'sessions/ses_1/source.mp4' },
    { artifactId: 'art_replay', sessionId: 'ses_1', attemptId: 'ses_1-a1', kind: 'replay_video', status: 'available', contentType: 'video/mp4', filename: 'replay.mp4', storageKey: 'sessions/ses_1/replay.mp4', jobId: 'job_1', jobRun: 1 },
  );
  return gw;
}

test('replay requires session authorization before looking up any job', async () => {
  const gw = fixture();
  assert.equal((await gw.app.request(route)).status, 401);
  assert.equal((await gw.app.request(route, { headers: { authorization: 'Bearer wrong' } })).status, 403);
  assert.equal((await gw.app.request('/v1/sessions/other/replay/job_1', { headers })).status, 403);
  assert.equal((await gw.app.request('/v1/sessions/ses_1/replay/unknown', { headers })).status, 404);
});

for (const [raw, mapped] of [['queued', 'queued'], ['running', 'processing'], ['ready', 'ready'], ['failed', 'failed'], ['cancelled', 'failed']]) {
  test(`replay maps ${raw} to ${mapped} and only signs completed outputs`, async () => {
    const gw = fixture(raw, { error: raw === 'failed' ? 'no hand landmarks' : undefined, progress: 0.5, stage: 'retarget' });
    const res = await gw.app.request(route, { headers });
    assert.equal(res.status, 200);
    assert.equal(res.headers.get('cache-control'), 'no-store');
    const body = await res.json();
    assert.equal(body.schemaVersion, 'scalpal.replay.v1');
    assert.equal(body.status, mapped);
    assert.match(body.sourceVideoUrl, /sig=/);
    assert.equal(Boolean(body.replayVideoUrl), raw === 'ready');
    if (raw === 'failed') assert.equal(body.reason, 'no hand landmarks');
    if (mapped === 'failed') assert.ok(body.reason.length > 0);
    assert.equal(body.label, 'Your hand motion, retargeted to a robot hand. Kinematic replay, not a trained robot.');
    assert.equal(body.progress, 0.5);
    assert.ok(body.expiresAtUnixMs <= Date.now() + 300_000);
  });
}

for (const patch of [{ sessionId: 'other' }, { attemptId: 'old-attempt' }, { jobRun: 0 }, { jobId: 'other-job' }, { status: 'deleted' }]) {
  test(`replay does not sign unavailable or mismatched output ${JSON.stringify(patch)}`, async () => {
    const gw = fixture();
    Object.assign(gw.artifacts[1], patch);
    const body = await (await gw.app.request(route, { headers })).json();
    assert.equal(body.status, 'failed');
    assert.equal(body.replayVideoUrl, '');
    assert.match(body.reason, /without an available replay video/);
  });
}

test('physics outputs fail explicitly and are never labeled kinematic or signed for playback', async () => {
  const gw = fixture('ready', { quality: { replayKind: 'physics' } });
  const body = await (await gw.app.request(route, { headers })).json();
  assert.equal(body.status, 'failed');
  assert.equal(body.replayKind, 'physics');
  assert.equal(body.replayVideoUrl, '');
  assert.match(body.reason, /unsupported replay kind: physics/);
  assert.match(body.label, /Unsupported replay kind: physics/);
  assert.doesNotMatch(body.label, /Your hand motion/);
});

test('authorization outage is an explicit retryable 503', async () => {
  const gw = startFakeGateway(async () => { throw new Unavailable('session authorization timed out'); });
  const res = await gw.app.request(route, { headers });
  assert.equal(res.status, 503);
});

test('recap voice asks reaction only and leaves self-assessment to the panel', async () => {
  const gw = startFakeGateway();
  const res = await gw.app.request('/v1/recap/voice-prompt?score=100&claim=perfect');
  const body = await res.json();
  assert.equal(res.status, 200);
  assert.equal(body.firstMessage, body.reactionQuestion);
  assert.match(body.prompt, /Clinical reasoning and Procedural skill/);
  assert.match(body.prompt, /Never invent or infer them/);
  assert.match(body.prompt, /Do not ask a self-assessment question or any further question/);
  assert.match(body.prompt, /remain silent and wait for the panel/);
  assert.equal(body.selfAssessmentQuestion, 'What is one thing you would do differently?');
  assert.doesNotMatch(body.prompt, /perfect|100/);
});

test('signed local videos support scrubbing with correct MIME and bounded byte ranges', async () => {
  const gw = startFakeGateway();
  const key = 'sessions/ses_1/replay.mp4';
  await gw.storage.write(key, Readable.from(Buffer.from('0123456789')));
  const signed = await gw.storage.presignGet(key, 60_000);
  const url = signed.url.replace(BASE, '');
  for (const [range, expected] of [['bytes=2-5', '2345'], ['bytes=7-', '789'], ['bytes=-3', '789'], ['bytes=7-90', '789']]) {
    const res = await gw.app.request(url, { headers: { range } });
    assert.equal(res.status, 206);
    assert.equal(res.headers.get('content-type'), 'video/mp4');
    assert.equal(res.headers.get('accept-ranges'), 'bytes');
    assert.equal(await res.text(), expected);
  }
  for (const range of ['bytes=20-', 'bytes=6-2', 'bytes=-0', 'bytes=0-1,5-8', 'bytes=-', 'nonsense']) {
    assert.equal((await gw.app.request(url, { headers: { range } })).status, 416);
  }
  const head = await gw.app.request(url, { method: 'HEAD', headers: { range: 'bytes=2-5' } });
  assert.equal(head.status, 206);
  assert.equal(head.headers.get('content-range'), 'bytes 2-5/10');
  assert.equal(head.headers.get('content-length'), '4');
  assert.equal(await head.text(), '');
});
