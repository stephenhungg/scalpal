// HTTP surface of the gateway:
//
//   GET  /healthz                         liveness + realtime/storage status
//   PUT  /files/<key>?m=PUT&exp&sig        local-storage upload (signed)
//   GET  /files/<key>?m=GET&exp&sig        local-storage download (signed)
//   POST /v1/worker/claim                  motion worker: claim next job
//   POST /v1/worker/jobs/:job/runs/:run/heartbeat
//   POST /v1/worker/jobs/:job/runs/:run/outputs
//   POST /v1/worker/jobs/:job/runs/:run/complete
//   POST /v1/worker/jobs/:job/runs/:run/fail
//
// Browsers and the Quest never call worker routes. Replay reads verify the
// existing SpacetimeDB client token and return scoped short-lived signed URLs.

import { randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { Readable } from 'node:stream';
import { Hono, type Context } from 'hono';
import { cors } from 'hono/cors';
import type { Config } from './config';
import { log } from './log';
import { Unavailable, waitFor, type Realtime } from './realtime';
import { LocalStorage, type Storage } from './storage';
import { registerRecapRoutes, type AuthorizeSession } from './recap';

type WorkerVars = { Variables: { workerId: string } };

const QUALITY_FIELDS = ['framesTotal', 'framesValid', 'invalidIntervals'] as const;

function reducerError(c: Context, err: unknown) {
  const message = err instanceof Error ? err.message : String(err);
  if (err instanceof Unavailable) return c.json({ error: message }, 503);
  if (/stale|no runs left|is (queued|ready|failed|cancelled)/.test(message)) {
    return c.json({ error: message }, 409);
  }
  return c.json({ error: message }, 400);
}

export function createApp(config: Config, rt: Realtime, storage: Storage, authorizeSession?: AuthorizeSession) {
  const app = new Hono();

  app.use(
    '*',
    cors({
      origin: origin => {
        if (config.corsOrigins.includes('*')) return origin || '*';
        return config.corsOrigins.includes(origin) ? origin : null;
      },
      allowMethods: ['GET', 'PUT', 'POST', 'HEAD', 'OPTIONS'],
      allowHeaders: ['content-type', 'authorization', 'range'],
      exposeHeaders: ['content-length', 'content-range', 'accept-ranges'],
      maxAge: 600,
    })
  );

  app.get('/healthz', c =>
    c.json({
      ok: rt.isReady && rt.isService,
      realtime: { connected: rt.isReady, identity: rt.identityHex, service: rt.isService },
      storage: storage.driver,
      voiceConfigured: Boolean(config.voice.elevenLabsApiKey && config.voice.elevenLabsAgentId),
      turnConfigured: Boolean(
        (config.ice.cloudflareTurnKeyId && config.ice.cloudflareTurnApiToken) || config.ice.turnUrls?.length
      ),
      workers: config.workers.tokens.size,
    })
  );

  // -------------------------------------------------------------------------
  // Local storage file route
  // -------------------------------------------------------------------------

  if (storage instanceof LocalStorage) {
    const keyOf = (c: Context) =>
      decodeURIComponent(new URL(c.req.url).pathname.replace(/^\/files\//, ''))
        .split('/')
        .join('/');

    app.put('/files/*', async c => {
      const key = keyOf(c);
      const contentType = (c.req.header('content-type') ?? '').split(';')[0].trim();
      if (c.req.query('m') !== 'PUT' || !storage.verify('PUT', key, c.req.query('exp'), c.req.query('sig'), contentType)) {
        return c.json({ error: 'invalid or expired signature' }, 403);
      }
      // A signed URL outlives the upload it was issued for. Once the artifact
      // leaves pending_upload (verifying, available, deleted, failed), its
      // bytes are final, so a replayed PUT must not replace them.
      let art;
      try {
        art = [...rt.require().db.sessionArtifacts.iter()].find(a => a.storageKey === key);
      } catch (err) {
        return reducerError(c, err);
      }
      if (art?.status !== 'pending_upload') {
        return c.json({ error: `artifact is ${art?.status ?? 'unknown'}; upload refused` }, 409);
      }
      const body = c.req.raw.body;
      if (!body) return c.json({ error: 'empty body' }, 400);
      try {
        const bytes = await storage.write(key, Readable.fromWeb(body as any));
        return c.json({ ok: true, bytes });
      } catch (err) {
        return c.json({ error: String((err as Error).message) }, 413);
      }
    });

    app.on(['GET', 'HEAD'], '/files/*', async c => {
      const key = keyOf(c);
      if (c.req.query('m') !== 'GET' || !storage.verify('GET', key, c.req.query('exp'), c.req.query('sig'))) {
        return c.json({ error: 'invalid or expired signature' }, 403);
      }
      const size = await storage.head(key);
      if (size == null) return c.json({ error: 'not found' }, 404);
      const dl = c.req.query('dl');
      const headers: Record<string, string> = {
        'content-length': String(size),
        'cache-control': 'private, max-age=60',
        'content-type': 'application/octet-stream',
      };
      if (dl) headers['content-disposition'] = `inline; filename="${dl.replace(/"/g, '')}"`;
      // VideoPlayer and browser scrubbing require single byte ranges. A signed
      // GET capability covers the same object whether read fully or in ranges.
      if (key.toLowerCase().endsWith('.mp4')) headers['content-type'] = 'video/mp4';
      else if (key.toLowerCase().endsWith('.webm')) headers['content-type'] = 'video/webm';
      headers['accept-ranges'] = 'bytes';
      const range = c.req.header('range');
      let start = 0, end = size - 1;
      if (range) {
        const match = /^bytes=(\d*)-(\d*)$/.exec(range);
        if (!match || (!match[1] && !match[2])) {
          return new Response(null, { status: 416, headers: { 'content-range': `bytes */${size}` } });
        }
        start = match[1] ? Number(match[1]) : Math.max(0, size - Number(match[2]));
        end = match[1] && match[2] ? Math.min(Number(match[2]), size - 1) : size - 1;
        if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || start > end || start >= size) {
          return new Response(null, { status: 416, headers: { 'content-range': `bytes */${size}` } });
        }
        headers['content-range'] = `bytes ${start}-${end}/${size}`;
        headers['content-length'] = String(end - start + 1);
      }
      const status = range ? 206 : 200;
      if (c.req.method === 'HEAD') return new Response(null, { status, headers });
      const stream = Readable.toWeb(createReadStream(storage.pathFor(key), range ? { start, end } : undefined)) as ReadableStream;
      return new Response(stream, { status, headers });
    });
  }

  // -------------------------------------------------------------------------
  // Motion worker API
  // -------------------------------------------------------------------------

  const worker = new Hono<WorkerVars>();

  worker.use('*', async (c, next) => {
    const auth = c.req.header('authorization') ?? '';
    const token = auth.startsWith('Bearer ') ? auth.slice(7) : '';
    const workerId = config.workers.tokens.get(token);
    if (!workerId) return c.json({ error: 'unauthorized worker' }, 401);
    c.set('workerId', workerId);
    await next();
  });

  // Claims are serialized in-process so concurrent polls don't race on the
  // same job; the reducer's expectedRun check guards across gateways.
  let claimChain: Promise<unknown> = Promise.resolve();

  worker.post('/claim', async c => {
    const body = (await c.req.json().catch(() => ({}))) as { leaseMs?: number };
    const leaseMs = Math.min(Math.max(body.leaseMs ?? config.workers.defaultLeaseMs, config.workers.minLeaseMs), 600_000);
    const workerId = c.get('workerId');

    const result = claimChain.then(async () => {
      const conn = rt.require();
      const queued = [...conn.db.sessionMotionJobs.iter()]
        .filter(j => j.status === 'queued' && j.run < j.maxRuns)
        .sort((a, b) => Number(a.createdAt.microsSinceUnixEpoch - b.createdAt.microsSinceUnixEpoch));
      for (const job of queued) {
        try {
          await conn.reducers.claimMotionJob({ jobId: job.jobId, expectedRun: job.run, workerId, leaseMs });
        } catch (err) {
          log.warn('claim lost', { jobId: job.jobId, err: String((err as Error).message) });
          continue;
        }
        const run = job.run + 1;
        try {
          return await waitFor(() =>
            [...conn.db.sessionMotionJobs.iter()].find(
              j => j.jobId === job.jobId && j.run === run && j.status === 'running'
            )
          );
        } catch {
          // The claim succeeded but the cache never showed it. Claiming another
          // job would strand this one until its lease expires, so requeue it
          // now and let the worker poll again.
          log.error('claimed job not visible in cache; releasing it', { jobId: job.jobId, run, workerId });
          await conn.reducers
            .failMotionJob({ jobId: job.jobId, run, error: 'gateway lost track of the claim; requeued', retryable: true })
            .catch(err => log.error('could not release claim', { jobId: job.jobId, run, err: String(err) }));
          throw new Unavailable('claimed job not visible yet; retry the claim');
        }
      }
      return null;
    });
    claimChain = result.catch(() => undefined);

    let claimed;
    try {
      claimed = await result;
    } catch (err) {
      return reducerError(c, err);
    }
    if (!claimed) return c.body(null, 204);

    const conn = rt.require();
    const artifacts = [...conn.db.sessionArtifacts.iter()];
    const inputIds = [claimed.inputArtifactId, ...claimed.extraArtifactIds];
    const inputs = await Promise.all(
      inputIds.map(async (id, i) => {
        const art = artifacts.find(a => a.artifactId === id);
        if (!art) throw new Error(`input artifact ${id} missing from cache`);
        const download = await storage.presignGet(art.storageKey, config.workers.inputUrlTtlMs, art.filename);
        return {
          role: i === 0 ? 'input' : 'extra',
          artifactId: art.artifactId,
          kind: art.kind,
          filename: art.filename,
          contentType: art.contentType,
          bytes: art.verifiedBytes != null ? Number(art.verifiedBytes) : null,
          sha256: art.sha256 ?? null,
          download,
        };
      })
    );
    const base = `/v1/worker/jobs/${encodeURIComponent(claimed.jobId)}/runs/${claimed.run}`;
    log.info('job claimed', { jobId: claimed.jobId, run: claimed.run, workerId });
    return c.json({
      job: {
        jobId: claimed.jobId,
        run: claimed.run,
        sessionId: claimed.sessionId,
        attemptId: claimed.attemptId,
        configVersion: claimed.configVersion,
        leaseMs,
      },
      inputs,
      endpoints: {
        heartbeat: `${base}/heartbeat`,
        outputs: `${base}/outputs`,
        complete: `${base}/complete`,
        fail: `${base}/fail`,
      },
    });
  });

  const runParams = (c: Context) => ({ jobId: c.req.param('job')!, run: Number(c.req.param('run')) });

  // A run belongs to the worker that claimed it. The run reducers take no
  // worker argument, so ownership is enforced here from the cache: each
  // (jobId, run) is claimed exactly once, and /claim answers only after the
  // cache shows that claim, so a matching cached run names its true owner.
  // Throws Unavailable (503) when realtime is down.
  const refuseUnownedRun = (c: Context<WorkerVars>, jobId: string, run: number) => {
    const job = [...rt.require().db.sessionMotionJobs.iter()].find(j => j.jobId === jobId);
    if (!job) return c.json({ error: 'unknown job' }, 404);
    if (job.status !== 'running' || job.run !== run) {
      return c.json({ error: `stale run ${run}: job is ${job.status} on run ${job.run}` }, 409);
    }
    if (job.workerId !== c.get('workerId')) {
      return c.json({ error: `stale run ${run}: claimed by another worker` }, 409);
    }
    return null;
  };

  worker.post('/jobs/:job/runs/:run/heartbeat', async c => {
    const { jobId, run } = runParams(c);
    const body = (await c.req.json().catch(() => ({}))) as { progress?: number; stage?: string; leaseMs?: number };
    try {
      const refused = refuseUnownedRun(c, jobId, run);
      if (refused) return refused;
      await rt.require().reducers.heartbeatMotionJob({
        jobId,
        run,
        progress: typeof body.progress === 'number' ? Math.min(Math.max(body.progress, 0), 1) : undefined,
        stage: typeof body.stage === 'string' ? body.stage.slice(0, 120) : undefined,
        leaseMs: Math.min(Math.max(body.leaseMs ?? config.workers.defaultLeaseMs, config.workers.minLeaseMs), 600_000),
      });
      return c.json({ ok: true });
    } catch (err) {
      return reducerError(c, err);
    }
  });

  worker.post('/jobs/:job/runs/:run/outputs', async c => {
    const { jobId, run } = runParams(c);
    const body = (await c.req.json().catch(() => ({}))) as {
      kind?: string;
      filename?: string;
      contentType?: string;
    };
    if (!body.kind || !body.filename || !body.contentType) {
      return c.json({ error: 'kind, filename and contentType are required' }, 400);
    }
    const artifactId = `art_${randomUUID().replace(/-/g, '')}`;
    try {
      const refused = refuseUnownedRun(c, jobId, run);
      if (refused) return refused;
      const conn = rt.require();
      await conn.reducers.registerJobOutput({
        jobId,
        run,
        artifactId,
        kind: body.kind,
        filename: body.filename,
        contentType: body.contentType,
      });
      const art = await waitFor(() => [...conn.db.sessionArtifacts.iter()].find(a => a.artifactId === artifactId));
      const upload = await storage.presignPut(art.storageKey, art.contentType, config.storage.uploadTtlMs);
      return c.json({ artifactId, upload });
    } catch (err) {
      return reducerError(c, err);
    }
  });

  worker.post('/jobs/:job/runs/:run/complete', async c => {
    const { jobId, run } = runParams(c);
    const body = (await c.req.json().catch(() => ({}))) as {
      outputArtifactIds?: string[];
      quality?: Record<string, unknown>;
    };
    const ids = body.outputArtifactIds ?? [];
    const q = body.quality ?? {};
    for (const f of QUALITY_FIELDS) {
      if (typeof q[f] !== 'number' || !Number.isInteger(q[f]) || (q[f] as number) < 0) {
        return c.json({ error: `quality.${f} must be a non-negative integer` }, 400);
      }
    }
    try {
      const refused = refuseUnownedRun(c, jobId, run);
      if (refused) return refused;
      const conn = rt.require();
      const artifacts = [...conn.db.sessionArtifacts.iter()];
      const outputs = [];
      for (const id of ids) {
        const art = artifacts.find(a => a.artifactId === id);
        if (!art) return c.json({ error: `unknown output ${id}` }, 400);
        const size = await storage.head(art.storageKey);
        if (size == null) return c.json({ error: `output ${id} was not uploaded` }, 422);
        outputs.push({ artifactId: id, verifiedBytes: BigInt(size) });
      }
      await conn.reducers.completeMotionJob({
        jobId,
        run,
        outputs,
        quality: {
          framesTotal: q.framesTotal as number,
          framesValid: q.framesValid as number,
          invalidIntervals: q.invalidIntervals as number,
          robotModel: String(q.robotModel ?? 'unspecified').slice(0, 120),
          replayKind: q.replayKind === 'physics' || q.replayKind === 'kinematic' ? q.replayKind : 'unknown',
          notes: String(q.notes ?? '').slice(0, 2000),
        },
      });
      log.info('job completed', { jobId, run, outputs: ids.length });
      return c.json({ ok: true });
    } catch (err) {
      return reducerError(c, err);
    }
  });

  worker.post('/jobs/:job/runs/:run/fail', async c => {
    const { jobId, run } = runParams(c);
    const body = (await c.req.json().catch(() => ({}))) as { error?: string; retryable?: boolean };
    try {
      const refused = refuseUnownedRun(c, jobId, run);
      if (refused) return refused;
      await rt.require().reducers.failMotionJob({
        jobId,
        run,
        error: String(body.error ?? 'worker reported failure').slice(0, 2000),
        retryable: Boolean(body.retryable),
      });
      log.info('job run failed', { jobId, run, retryable: Boolean(body.retryable) });
      return c.json({ ok: true });
    } catch (err) {
      return reducerError(c, err);
    }
  });

  app.route('/v1/worker', worker);
  registerRecapRoutes(app, config, rt, storage, authorizeSession);

  app.notFound(c => c.json({ error: 'not found' }, 404));
  app.onError((err, c) => {
    log.error('unhandled', { err: String(err?.stack ?? err) });
    return c.json({ error: 'internal error' }, 500);
  });

  return app;
}
