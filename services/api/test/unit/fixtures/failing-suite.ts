// Fixture for harness.test.ts: a suite that fails (FAIL_IN=test|before|none)
// while holding an open handle, like the integration suite's SpacetimeDB
// sockets, so the process only ends through exitAfterTeardown.
import { before, describe, test } from 'node:test';
import { exitAfterTeardown } from '../../harness';

const failIn = process.env.FAIL_IN ?? 'test';
const handle = setInterval(() => {}, 1000);

before(() => {
  if (failIn === 'before') throw new Error('intentional before() failure');
});

exitAfterTeardown(() => clearInterval(handle), 50);

describe('suite', () => {
  test('passes', () => {});
  test('fails when asked', () => {
    if (failIn === 'test') throw new Error('intentional test failure');
  });
});
