// The integration harness publishes with --delete-data=always, so it must
// never be pointed at a real database, and it must not hide failures.

import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { assertDisposableTarget } from '../harness';

const here = path.dirname(fileURLToPath(import.meta.url));
const LOCAL = { server: 'local', uri: 'ws://127.0.0.1:3000' };

test('allows a scalpal-test- database on a local server', () => {
  assertDisposableTarget({ ...LOCAL, db: 'scalpal-test-abc123' }, {});
  assertDisposableTarget({ server: 'http://localhost:3000', uri: 'ws://localhost:3000', db: 'scalpal-test-x' }, {});
});

test('refuses to wipe the shared production database name, even locally', () => {
  assert.throws(() => assertDisposableTarget({ ...LOCAL, db: 'scalpal' }, {}), /refusing to wipe database "scalpal"/);
  assert.throws(() => assertDisposableTarget({ ...LOCAL, db: 'my-scalpal-test-db' }, {}), /refusing/);
});

test('refuses a non-local server or URI without an explicit opt-in', () => {
  const db = 'scalpal-test-abc';
  assert.throws(
    () => assertDisposableTarget({ server: 'maincloud', uri: 'wss://maincloud.spacetimedb.com', db }, {}),
    /non-local server "maincloud"/
  );
  // A local CLI nickname with a remote client URI is still remote.
  assert.throws(() => assertDisposableTarget({ server: 'local', uri: 'wss://maincloud.spacetimedb.com', db }, {}), /non-local/);
  assert.throws(() => assertDisposableTarget({ server: 'https://evil.example', uri: LOCAL.uri, db }, {}), /non-local/);
  assertDisposableTarget(
    { server: 'maincloud', uri: 'wss://maincloud.spacetimedb.com', db },
    { TEST_SPACETIME_ALLOW_REMOTE: '1' }
  );
});

test('the opt-in never allows a non-test database name', () => {
  assert.throws(
    () =>
      assertDisposableTarget(
        { server: 'maincloud', uri: 'wss://maincloud.spacetimedb.com', db: 'scalpal' },
        { TEST_SPACETIME_ALLOW_REMOTE: '1' }
      ),
    /refusing to wipe database "scalpal"/
  );
});

function runFixture(failIn: string) {
  // Run standalone (not under `node --test`) so no parent runner re-derives
  // the result: the exit code is exactly what the harness chose.
  const fixture = path.join(here, 'fixtures', 'failing-suite.ts');
  const res = spawnSync(process.execPath, ['--import', 'tsx', fixture], {
    encoding: 'utf8',
    timeout: 20_000,
    env: { ...process.env, FAIL_IN: failIn },
  });
  assert.equal(res.signal, null, 'fixture should exit on its own');
  return res;
}

test('exitAfterTeardown exits non-zero when a test fails', () => {
  const res = runFixture('test');
  assert.notEqual(res.status, 0, `a failing suite must not exit 0\n${res.stdout}`);
});

test('exitAfterTeardown exits non-zero when a before() hook fails', () => {
  const res = runFixture('before');
  assert.notEqual(res.status, 0, `a failed setup must not exit 0\n${res.stdout}`);
});

test('exitAfterTeardown exits 0 when everything passes', () => {
  assert.equal(runFixture('none').status, 0);
});
