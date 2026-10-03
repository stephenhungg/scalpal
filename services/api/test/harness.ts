// Integration harness: publishes the realtime module to a throwaway database
// on a local SpacetimeDB (`spacetime start`), runs an in-process gateway
// registered as a service identity, and creates independent client
// identities.

import { execFileSync } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { serve, type ServerType } from '@hono/node-server';
import type { Identity } from 'spacetimedb';
import { DbConnection, tables } from '../src/module_bindings/index';

const here = path.dirname(fileURLToPath(import.meta.url));
export const MODULE_PATH = path.resolve(here, '../../realtime');
export const URI = process.env.TEST_SPACETIMEDB_URI ?? 'ws://127.0.0.1:3000';
export const SERVER = process.env.TEST_SPACETIME_SERVER ?? 'local';
export const DB = process.env.TEST_SPACETIMEDB_DB ?? `scalpal-test-${Date.now().toString(36)}`;
export const GATEWAY_PORT = Number(process.env.TEST_GATEWAY_PORT ?? 8799);
export const GATEWAY = `http://127.0.0.1:${GATEWAY_PORT}`;
export const WORKER_TOKEN = 'test-worker-token-0123456789';
export const WORKER_TOKEN_2 = 'test-worker-token-abcdefghij';

function spacetime(...args: string[]) {
  return execFileSync('spacetime', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
}

export function publishFresh() {
  spacetime('publish', '--server', SERVER, '--yes', '--delete-data=always', DB, '--module-path', MODULE_PATH);
}

export function registerService(identityHex: string) {
  spacetime('call', '--server', SERVER, DB, 'add_service_identity', `"0x${identityHex}"`, '"test gateway"');
}

export type Client = {
  conn: DbConnection;
  identity: Identity;
  token: string;
  close(): void;
};

/** Connect a client (new identity unless `token` is given) subscribed to every view. */
export function connect(token?: string): Promise<Client> {
  return new Promise((resolve, reject) => {
    const conn = DbConnection.builder()
      .withUri(URI)
      .withDatabaseName(DB)
      .withToken(token)
      .onConnect((c, identity, tok) => {
        c.subscriptionBuilder()
          .onApplied(() => resolve({ conn: c, identity, token: tok, close: () => c.disconnect() }))
          .onError(ctx => reject(new Error(`subscription failed: ${String(ctx.event)}`)))
          .subscribe([
            tables.mySessions,
            tables.myMemberships,
            tables.amService,
            tables.sessionMembers,
            tables.sessionInvites,
            tables.sessionAttempts,
            tables.sessionExerciseState,
            tables.sessionEvents,
            tables.sessionCoachMessages,
            tables.sessionCoachStatus,
            tables.sessionCommands,
            tables.sessionArtifacts,
            tables.sessionMotionJobs,
            tables.sessionReplayState,
            tables.sessionMediaSource,
            tables.myTransferGrants,
            tables.myServiceGrants,
            tables.myRtcSignals,
          ]);
      })
      .onConnectError((_ctx, err) => reject(err))
      .build();
  });
}

export async function eventually<T>(
  read: () => T | undefined | null | false,
  what = 'condition',
  timeoutMs = 8000
): Promise<T> {
  const start = Date.now();
  for (;;) {
    const v = read();
    if (v) return v;
    if (Date.now() - start > timeoutMs) throw new Error(`timed out waiting for ${what}`);
    await new Promise(r => setTimeout(r, 25));
  }
}

export function uid(prefix: string) {
  return `${prefix}_${Math.random().toString(36).slice(2, 10)}${Date.now().toString(36)}`;
}

export type Gateway = { identityHex: string; stop(): Promise<void> };

/** Start the real gateway in-process against the test database. */
export async function startGateway(extraEnv: Record<string, string> = {}): Promise<Gateway> {
  const dir = mkdtempSync(path.join(tmpdir(), 'scalpal-gw-'));
  Object.assign(process.env, {
    PORT: String(GATEWAY_PORT),
    PUBLIC_BASE_URL: GATEWAY,
    CORS_ORIGINS: '*',
    SPACETIMEDB_URI: URI,
    SPACETIMEDB_DB: DB,
    SPACETIMEDB_TOKEN: '',
    SPACETIMEDB_TOKEN_FILE: path.join(dir, 'token'),
    STORAGE_DRIVER: 'local',
    LOCAL_STORAGE_DIR: path.join(dir, 'artifacts'),
    URL_SIGNING_SECRET: 'test-signing-secret',
    WORKER_TOKENS: `w1:${WORKER_TOKEN},w2:${WORKER_TOKEN_2}`,
    WORKER_MIN_LEASE_MS: '1000',
    ELEVENLABS_API_KEY: '',
    ELEVENLABS_AGENT_ID: '',
    CLOUDFLARE_TURN_KEY_ID: '',
    CLOUDFLARE_TURN_API_TOKEN: '',
    ...extraEnv,
  });
  const { loadConfig } = await import('../src/config');
  const { Realtime } = await import('../src/realtime');
  const { Reconciler } = await import('../src/reconciler');
  const { createStorage } = await import('../src/storage');
  const { createApp } = await import('../src/http');

  const config = loadConfig();
  const storage = createStorage(config);
  const rt = new Realtime(config.spacetime);
  const reconciler = new Reconciler(rt, storage, config);
  const app = createApp(config, rt, storage);
  rt.start();
  await eventually(() => rt.isReady, 'gateway subscription');
  const identityHex = rt.identityHex!;
  registerService(identityHex);
  await eventually(() => rt.isService, 'gateway service registration');
  reconciler.start();
  const server: ServerType = serve({ fetch: app.fetch, port: GATEWAY_PORT });
  return {
    identityHex,
    stop: () =>
      new Promise(resolve => {
        reconciler.stop();
        rt.stop();
        server.close(() => resolve());
      }),
  };
}

export async function worker(
  token: string,
  pathname: string,
  body: unknown = {}
): Promise<{ status: number; json: any }> {
  const res = await fetch(`${GATEWAY}${pathname}`, {
    method: 'POST',
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : null };
}

/** Full happy-path setup: operator session + headset + viewer + coach. */
export async function sessionWithRoles() {
  const operator = await connect();
  const sessionId = uid('ses');
  await operator.conn.reducers.createSession({
    sessionId,
    label: 'test session',
    exerciseId: 'instrument-transfer',
    exerciseVersion: '0.1.0',
    displayName: 'Operator',
  });
  const invites = await eventually(() => {
    const rows = [...operator.conn.db.sessionInvites.iter()].filter(i => i.sessionId === sessionId);
    return rows.length === 4 ? rows : null;
  }, 'invites');
  const code = (role: string) => invites.find(i => i.role === role)!.code;

  const headset = await connect();
  await headset.conn.reducers.joinSession({ code: code('headset'), displayName: 'Quest' });
  const viewer = await connect();
  await viewer.conn.reducers.joinSession({ code: code('viewer'), displayName: 'Judge' });
  const coach = await connect();
  await coach.conn.reducers.joinSession({ code: code('coach'), displayName: 'Jarvis' });
  await eventually(
    () => [...viewer.conn.db.mySessions.iter()].some(s => s.sessionId === sessionId),
    'viewer sees session'
  );
  const attemptId = `${sessionId}-a1`;
  return {
    sessionId,
    attemptId,
    operator,
    headset,
    viewer,
    coach,
    code,
    closeAll() {
      for (const c of [operator, headset, viewer, coach]) c.close();
    },
  };
}
