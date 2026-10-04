// The Quest capture uploader (apps/quest Assets/Scalpal/Capture) against this
// gateway. `fixtures/quest-capture-upload.json` is the exact reducer arguments and
// capture manifest the headset flow produced in Unity's CaptureValidation (which
// fails if the fixture drifts). The rules below are read from the real reducer
// and worker sources rather than restated.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { BASE, startFakeGateway } from './gateway-fixture';

const fixture = JSON.parse(readFileSync(new URL('./fixtures/quest-capture-upload.json', import.meta.url), 'utf8'));
const reducers = readFileSync(new URL('../../../realtime/src/index.ts', import.meta.url), 'utf8');
const worker = readFileSync(new URL('../../../motion/scalpal_motion/gateway_worker.py', import.meta.url), 'utf8');
const ID_PATTERN = new RegExp(/const ID_PATTERN = \/(.+)\/;/.exec(reducers)![1]);
const ARTIFACT_KINDS = [.../const ARTIFACT_KINDS = \[([^\]]+)\]/.exec(reducers)![1].matchAll(/'([a-z_]+)'/g)].map(m => m[1]);
const WORKER_CONFIG = /SUPPORTED_CONFIG_VERSION = "([^"]+)"/.exec(worker)![1];

type Upload = { grantId: string; artifactId: string; sessionId: string; attemptId: string; kind: string; filename: string; contentType: string; declaredBytes: number; sha256: string };
const uploads: Upload[] = fixture.requestUpload;
const job = fixture.requestMotionJob as { jobId: string; inputArtifactId: string; extraArtifactIds: string[]; configVersion: string };

test('headset upload requests satisfy the requestUpload reducer checks', () => {
  assert.ok(ARTIFACT_KINDS.includes('raw_clip') && ARTIFACT_KINDS.includes('capture_manifest'), 'parsed reducer kinds');
  assert.equal(new Set(uploads.map(u => u.grantId)).size, uploads.length, 'grant ids are unique');
  for (const u of uploads) {
    assert.match(u.grantId, ID_PATTERN);
    assert.match(u.artifactId, ID_PATTERN);
    assert.ok(ARTIFACT_KINDS.includes(u.kind), u.kind);
    assert.ok(u.filename.length <= 200 && u.contentType.length <= 120);
    assert.match(u.sha256, /^[a-f0-9]{64}$/);
    assert.ok(Number.isSafeInteger(u.declaredBytes) && u.declaredBytes > 0);
    assert.equal(u.sessionId, uploads[0].sessionId);
    assert.equal(u.attemptId, uploads[0].attemptId);
  }
  assert.deepEqual(uploads.map(u => u.kind), ['raw_clip', 'other', 'capture_manifest']);
  assert.match(uploads[0].contentType, /^video\//, 'recap signs only video/* sources');
});

test('headset motion job matches requestMotionJob and the worker configuration', () => {
  assert.match(job.jobId, ID_PATTERN);
  assert.equal(job.configVersion, WORKER_CONFIG);
  const input = uploads.find(u => u.artifactId === job.inputArtifactId)!;
  assert.equal(input.kind, 'raw_clip');
  assert.ok(job.extraArtifactIds.length <= 8);
  const manifests = job.extraArtifactIds.map(id => uploads.find(u => u.artifactId === id)!).filter(u => u.kind === 'capture_manifest');
  assert.equal(manifests.length, 1, 'exactly one capture_manifest extra');
  for (const id of job.extraArtifactIds) assert.equal(uploads.find(u => u.artifactId === id)!.sessionId, input.sessionId);
});

test('headset manifest is under the gateway bound and binds the clip to the attempt', () => {
  const text = JSON.stringify(fixture.manifest);
  assert.ok(Buffer.byteLength(text) <= 16_384);
  const m = fixture.manifest;
  assert.equal(m.schemaVersion, 'scalpal.capture-provenance.v1');
  assert.equal(m.inputArtifactId, job.inputArtifactId);
  assert.equal(m.attemptId, uploads[0].attemptId);
  assert.equal(m.capture.rawCameraFrames, true);
  assert.equal(m.capture.compositedFrames, false);
  assert.equal(m.capture.frames.timingArtifactId, uploads.find(u => u.kind === 'other')!.artifactId);
  // Synthetic editor frames must never be labeled as learner footage.
  assert.equal(m.source, 'sample');
});

async function uploadThroughGateway(manifest: unknown) {
  const gw = startFakeGateway(async (token, session) => token === 'headset-token' && session === uploads[0].sessionId);
  for (const u of uploads) {
    const storageKey = `sessions/${u.sessionId}/${u.attemptId}/${u.artifactId}/${u.filename}`;
    gw.artifacts.push({ artifactId: u.artifactId, sessionId: u.sessionId, attemptId: u.attemptId, kind: u.kind, status: 'pending_upload', contentType: u.contentType, filename: u.filename, storageKey });
    // The reconciler presigns with the declared content type; the headset sends the same header.
    const signed = await gw.storage.presignPut(storageKey, u.contentType, 60_000);
    const body = u.kind === 'capture_manifest' ? JSON.stringify(manifest) : `bytes for ${u.kind}`;
    const res = await gw.app.request(signed.url.replace(BASE, ''), { method: 'PUT', headers: { 'Content-Type': u.contentType }, body });
    assert.equal(res.status, 200, `${u.kind} PUT: ${await res.text()}`);
    gw.artifacts[gw.artifacts.length - 1].status = 'available';
  }
  gw.jobs.push({ ...job, sessionId: uploads[0].sessionId, attemptId: uploads[0].attemptId, status: 'queued', run: 0, maxRuns: 3, outputArtifactIds: [], createdAt: { microsSinceUnixEpoch: 1n } });
  const res = await gw.app.request(`/v1/sessions/${uploads[0].sessionId}/replay/${job.jobId}`, { headers: { authorization: 'Bearer headset-token' } });
  assert.equal(res.status, 200);
  return res.json();
}

test('the replay route resolves the headset clip, job and manifest provenance', async () => {
  const body = await uploadThroughGateway(fixture.manifest);
  assert.equal(body.status, 'queued');
  assert.equal(body.jobId, job.jobId);
  assert.equal(body.attemptId, uploads[0].attemptId);
  assert.equal(body.sourceArtifactId, job.inputArtifactId);
  assert.equal(body.source, 'sample');
  assert.match(body.sourceVideoUrl, /sig=/);
});

test('a manifest bound to another attempt is not trusted', async () => {
  const body = await uploadThroughGateway({ ...fixture.manifest, attemptId: 'another-attempt' });
  assert.equal(body.source, 'unknown');
});
