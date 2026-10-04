// R5: once an artifact leaves pending_upload, a still-valid signed PUT URL
// (15 min TTL) must not be able to replace the bytes that were verified.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { BASE, startFakeGateway } from './gateway-fixture';

const KEY = 'sessions/s/a/art_1/clip.mp4';

async function putWhile(status: string | null) {
  const gw = startFakeGateway();
  if (status) gw.artifacts.push({ artifactId: 'art_1', storageKey: KEY, status, contentType: 'video/mp4' });
  const signed = await gw.storage.presignPut(KEY, 'video/mp4', 60_000);
  const res = await gw.app.request(signed.url.replace(BASE, ''), {
    method: 'PUT',
    headers: signed.headers,
    body: 'clip bytes',
  });
  return { res, gw };
}

test('a signed PUT is accepted while the artifact is pending_upload', async () => {
  const { res, gw } = await putWhile('pending_upload');
  assert.equal(res.status, 200, await res.text());
  assert.equal(await gw.storage.head(KEY), 'clip bytes'.length);
});

for (const status of ['verifying', 'available', 'deleted', 'failed', null]) {
  test(`a signed PUT is refused when the artifact is ${status ?? 'unknown'}`, async () => {
    const { res, gw } = await putWhile(status);
    assert.equal(res.status, 409, await res.text());
    assert.equal(await gw.storage.head(KEY), null, 'nothing may be written');
  });
}
