// Unit checks for gateway configuration. No SpacetimeDB needed:
//   npm run test:unit

import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { loadConfig } from '../../src/config';

const saved = { ...process.env };
afterEach(() => {
  process.env = { ...saved };
});

test('defaults to port 8788 so it does not collide with preop/coach on 8787', () => {
  // The motion worker (services/motion cli.py) targets http://localhost:8788
  // and preop binds 8787; a shared default broke local multi-service runs.
  delete process.env.PORT;
  delete process.env.PUBLIC_BASE_URL;
  const config = loadConfig();
  assert.equal(config.port, 8788);
  assert.equal(config.publicBaseUrl, 'http://localhost:8788');
});
