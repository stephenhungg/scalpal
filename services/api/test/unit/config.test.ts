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

// R15: on Fly/Docker the token file cannot persist, so without
// SPACETIMEDB_TOKEN every restart is a new, unregistered identity and every
// grant and job is refused. Refuse to start instead of degrading silently.
test('production refuses to start without SPACETIMEDB_TOKEN', () => {
  process.env.NODE_ENV = 'production';
  delete process.env.SPACETIMEDB_TOKEN;
  assert.throws(() => loadConfig(), /SPACETIMEDB_TOKEN is required/);
  process.env.SPACETIMEDB_TOKEN = '';
  assert.throws(() => loadConfig(), /SPACETIMEDB_TOKEN is required/);
});

test('production starts with SPACETIMEDB_TOKEN; local dev may still bootstrap without one', () => {
  process.env.NODE_ENV = 'production';
  process.env.SPACETIMEDB_TOKEN = 'service-token';
  assert.equal(loadConfig().spacetime.token, 'service-token');
  delete process.env.NODE_ENV;
  delete process.env.SPACETIMEDB_TOKEN;
  assert.equal(loadConfig().spacetime.token, undefined);
});
