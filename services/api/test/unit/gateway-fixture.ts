// In-process gateway for unit tests: the SpacetimeDB connection is replaced
// by an in-memory cache plus a log of reducer calls.

import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { loadConfig } from '../../src/config';
import { createApp } from '../../src/http';
import type { Realtime } from '../../src/realtime';
import { LocalStorage } from '../../src/storage';

export const W1 = 'unit-worker-token-w1-0000';
export const W2 = 'unit-worker-token-w2-0000';
export const BASE = 'http://gateway.test';

export interface Call {
  name: string;
  args: Record<string, unknown>;
}

export function runningJob(patch: Record<string, unknown> = {}) {
  return {
    jobId: 'job_1',
    sessionId: 'ses_1',
    attemptId: 'ses_1-a1',
    inputArtifactId: 'art_in',
    extraArtifactIds: [],
    configVersion: 'motion-v1',
    status: 'running',
    run: 1,
    maxRuns: 3,
    workerId: 'w1',
    createdAt: { microsSinceUnixEpoch: 1n },
    ...patch,
  };
}

export function startFakeGateway() {
  const saved = { ...process.env };
  Object.assign(process.env, {
    PUBLIC_BASE_URL: BASE,
    WORKER_TOKENS: `w1:${W1},w2:${W2}`,
    URL_SIGNING_SECRET: 'unit-signing-secret',
    LOCAL_STORAGE_DIR: mkdtempSync(path.join(tmpdir(), 'scalpal-unit-')),
    STORAGE_DRIVER: 'local',
  });
  const config = loadConfig();
  process.env = saved;

  const jobs: Record<string, unknown>[] = [];
  const artifacts: Record<string, unknown>[] = [];
  const calls: Call[] = [];
  const effects: Record<string, (args: Record<string, unknown>) => void> = {};
  const conn = {
    db: {
      sessionMotionJobs: { iter: () => jobs.values() },
      sessionArtifacts: { iter: () => artifacts.values() },
    },
    reducers: new Proxy(
      {},
      {
        get: (_target, name: string) => async (args: Record<string, unknown>) => {
          calls.push({ name, args });
          effects[name]?.(args);
        },
      }
    ),
  };
  // Only the members createApp touches are provided.
  const rt = { isReady: true, isService: true, identityHex: 'ab', connection: conn, require: () => conn };
  const storage = new LocalStorage(config.storage.local, config.publicBaseUrl);
  const app = createApp(config, rt as unknown as Realtime, storage);

  return {
    app,
    storage,
    jobs,
    artifacts,
    calls,
    effects,
    worker(token: string, pathname: string, body: unknown = {}) {
      return app.request(`/v1/worker${pathname}`, {
        method: 'POST',
        headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
        body: JSON.stringify(body),
      });
    },
  };
}
