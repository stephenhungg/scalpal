// SpacetimeDB verifies client bearer tokens and scopes myMemberships. Never
// decode an unverified JWT, trust a client identity, or accept worker secrets.
import type { Hono } from 'hono';
import { createHash } from 'node:crypto';
import type { Artifact, MotionJob } from './module_bindings/types';
import type { Config } from './config';
import { DbConnection, tables } from './module_bindings';
import { Unavailable, type Realtime } from './realtime';
import type { Storage } from './storage';

export type AuthorizeSession = (token: string, sessionId: string) => Promise<boolean>;

export interface MembershipLease {
  hasSession(sessionId: string): boolean;
  active(): boolean;
  close(): void;
}

// Retain a live subscription, not a cached allow decision: removals are reflected
// on the next request. Token hashes are keys; raw tokens never enter logs/cache keys.
export function createSessionAuthorizer(
  config: Config['spacetime'],
  connect: (token: string) => Promise<MembershipLease> = token => connectMemberships(config, token),
  now = Date.now,
): AuthorizeSession {
  const leases = new Map<string, { pending: Promise<MembershipLease>; expires: number; close: () => void }>();
  return async (token, sessionId) => {
    const key = createHash('sha256').update(token).digest('hex');
    for (const [k, item] of leases) if (item.expires <= now()) { leases.delete(k); item.close(); }
    let item = leases.get(key);
    if (!item) {
      if (leases.size >= 64) throw new Unavailable('session authorization capacity reached');
      const pending = connect(token);
      const close = () => { void pending.then(l => l.close()).catch(() => {}); };
      item = { pending, expires: now() + 30_000, close };
      leases.set(key, item);
      const entry = item;
      setTimeout(() => { if (leases.get(key) === entry) { leases.delete(key); close(); } }, 30_000).unref();
    }
    try {
      const lease = await item.pending;
      if (!lease.active()) throw new Unavailable('session authorization disconnected');
      return lease.hasSession(sessionId);
    } catch (error) { leases.delete(key); item.close(); throw error; }
  };
}

function connectMemberships(config: Config['spacetime'], token: string): Promise<MembershipLease> {
  return new Promise((resolve, reject) => {
    let connection: DbConnection | undefined;
    let active = true;
    let settled = false;
    const fail = (error: Error) => {
      active = false;
      clearTimeout(timer);
      if (!settled) { settled = true; reject(error); }
      connection?.disconnect();
    };
    const timer = setTimeout(() => fail(new Unavailable('session authorization timed out')), 5000);
    try {
      connection = DbConnection.builder().withUri(config.uri).withDatabaseName(config.database).withToken(token)
        .onConnect(conn => {
          conn.subscriptionBuilder().onApplied(() => {
            clearTimeout(timer);
            if (!active || settled) return;
            settled = true;
            resolve({
              hasSession: id => [...conn.db.myMemberships.iter()].some(m => m.sessionId === id),
              active: () => active,
              close: () => { active = false; conn.disconnect(); },
            });
          }).onError(() => fail(new Unavailable('session membership unavailable'))).subscribe([tables.myMemberships]);
        })
        .onConnectError(() => fail(new Error('invalid session token')))
        .onDisconnect(() => { active = false; clearTimeout(timer); if (!settled) { settled = true; reject(new Error('session disconnected')); } })
        .build();
    } catch (error) { fail(error instanceof Error ? error : new Error('session connection failed')); }
  });
}

// New explicit upload contract; existing raw_clip rows have no provenance field.
// A manifest must be an available capture_manifest named in this job's extraArtifactIds.
export async function replaySource(job: MotionJob, artifacts: Artifact[], storage: Storage): Promise<'learner' | 'rehearsal' | 'sample' | 'unknown'> {
  const manifests = artifacts.filter(a => job.extraArtifactIds.includes(a.artifactId) && a.kind === 'capture_manifest'
    && a.status === 'available' && a.sessionId === job.sessionId && a.attemptId === job.attemptId);
  if (manifests.length !== 1) return 'unknown';
  try {
    const m = JSON.parse((await storage.readSmall(manifests[0].storageKey, 16_384)).toString('utf8'));
    if (m.schemaVersion !== 'scalpal.capture-provenance.v1' || m.sessionId !== job.sessionId
      || m.attemptId !== job.attemptId || m.inputArtifactId !== job.inputArtifactId) return 'unknown';
    return ['learner', 'rehearsal', 'sample'].includes(m.source) ? m.source : 'unknown';
  } catch { return 'unknown'; }
}

export function registerRecapRoutes(
  app: Hono, config: Config, rt: Realtime, storage: Storage,
  authorize: AuthorizeSession = createSessionAuthorizer(config.spacetime),
) {
  // Bound both token traffic and unauthenticated/rotating-token traffic before connecting.
  const requests = new Map<string, { until: number; count: number }>();
  const admit = (key: string, limit: number) => {
    const now = Date.now();
    for (const [k, value] of requests) if (value.until <= now) requests.delete(k);
    const value = requests.get(key) ?? { until: now + 60_000, count: 0 };
    if (requests.size >= 512 && !requests.has(key)) return false;
    requests.set(key, value);
    return ++value.count <= limit;
  };

  app.get('/v1/sessions/:sessionId/replay/:jobId', async c => {
    c.header('Cache-Control', 'no-store');
    const auth = c.req.header('authorization') ?? '';
    const token = auth.startsWith('Bearer ') ? auth.slice(7).trim() : '';
    const remote = (c.env as { incoming?: { socket?: { remoteAddress?: string } } } | undefined)?.incoming?.socket?.remoteAddress ?? 'unattributed';
    if (!admit(`ip:${remote}`, 240) || !admit(`token:${createHash('sha256').update(token).digest('hex')}`, 30)) {
      c.header('Retry-After', '60');
      return c.json({ error: 'replay request rate exceeded' }, 429);
    }
    if (!token || token.length > 8192) return c.json({ error: 'session bearer token required' }, 401);
    const { sessionId, jobId } = c.req.param();
    try {
      if (!await authorize(token, sessionId)) return c.json({ error: 'session access denied' }, 403);
      const conn = rt.require();
      const job = [...conn.db.sessionMotionJobs.iter()].find(j => j.jobId === jobId && j.sessionId === sessionId);
      if (!job) return c.json({ error: 'replay job not found in session' }, 404);
      const allArtifacts = [...conn.db.sessionArtifacts.iter()];
      const source = await replaySource(job, allArtifacts, storage);
      const artifacts = allArtifacts.filter(a =>
        a.sessionId === sessionId && a.attemptId === job.attemptId && a.status === 'available' && a.contentType.startsWith('video/'));
      const input = artifacts.find(a => a.artifactId === job.inputArtifactId && a.kind === 'raw_clip');
      const replay = artifacts.find(a => job.outputArtifactIds.includes(a.artifactId) && a.kind === 'replay_video' && a.jobId === jobId && a.jobRun === job.run);
      const ttlMs = Math.min(config.storage.downloadTtlMs, 300_000);
      let status = job.status === 'running' ? 'processing' : job.status;
      let reason = job.error ?? '';
      const replayKind = job.quality?.replayKind ?? 'unknown';
      if (!['queued', 'processing', 'ready', 'failed'].includes(status)) {
        status = 'failed';
        reason = reason || `motion job is ${job.status}`;
      }
      if (status === 'ready' && !replay) {
        status = 'failed';
        reason = 'motion job completed without an available replay video';
      }
      if (status === 'ready' && replayKind !== 'kinematic') {
        status = 'failed';
        reason = `unsupported replay kind: ${replayKind}; this recap requires a kinematic replay`;
      }
      if (status === 'ready' && source === 'unknown') {
        status = 'failed'; reason = 'capture provenance is unavailable or invalid';
      }
      if (status === 'failed' && !reason) reason = 'motion processing failed without a worker reason';
      const [sourceVideoUrl, replayVideoUrl] = await Promise.all([
        input ? storage.presignGet(input.storageKey, ttlMs, input.filename).then(s => s.url) : '',
        replay && status === 'ready' ? storage.presignGet(replay.storageKey, ttlMs, replay.filename).then(s => s.url) : '',
      ]);
      return c.json({
        schemaVersion: 'scalpal.replay.v1', sessionId, attemptId: job.attemptId, jobId, jobRun: job.run,
        source, sourceArtifactId: input?.artifactId ?? '', replayArtifactId: replay?.artifactId ?? '',
        status, reason, progress: job.progress ?? 0, stage: job.stage ?? '',
        sourceVideoUrl, replayVideoUrl, expiresAtUnixMs: Date.now() + ttlMs,
        replayKind,
        label: source === 'unknown' ? 'Capture provenance unavailable. This is not verified learner footage.' : replayKind === 'kinematic'
          ? `${source === 'learner' ? 'Your hand motion' : source === 'rehearsal' ? 'Rehearsal hand motion' : 'Sample hand motion'}, retargeted to a robot hand. Kinematic replay, not a trained robot.`
          : `Unsupported replay kind: ${replayKind}. This recap requires a kinematic replay.`,
      });
    } catch (error) {
      if (error instanceof Unavailable) return c.json({ error: error.message }, 503);
      if (error instanceof Error && /invalid session token|session disconnected/.test(error.message)) return c.json({ error: 'session access denied' }, 403);
      throw error;
    }
  });
}
