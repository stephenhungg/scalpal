// SpacetimeDB verifies client bearer tokens and scopes myMemberships. Never
// decode an unverified JWT, trust a client identity, or accept worker secrets.
import type { Hono } from 'hono';
import type { Config } from './config';
import { DbConnection, tables } from './module_bindings';
import { Unavailable, type Realtime } from './realtime';
import type { Storage } from './storage';

export type AuthorizeSession = (token: string, sessionId: string) => Promise<boolean>;

export function createSessionAuthorizer(config: Config['spacetime']): AuthorizeSession {
  return (token, sessionId) => new Promise<boolean>((resolve, reject) => {
    let connection: DbConnection | undefined;
    let settled = false;
    const finish = (allowed: boolean, error?: Error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      connection?.disconnect();
      if (error) reject(error); else resolve(allowed);
    };
    const timer = setTimeout(() => finish(false, new Unavailable('session authorization timed out')), 5000);
    try {
      connection = DbConnection.builder()
        .withUri(config.uri)
        .withDatabaseName(config.database)
        .withToken(token)
        .onConnect(conn => {
          conn.subscriptionBuilder()
            .onApplied(() => finish([...conn.db.myMemberships.iter()].some(m => m.sessionId === sessionId)))
            .onError(() => finish(false, new Unavailable('session membership unavailable')))
            .subscribe([tables.myMemberships]);
        })
        .onConnectError(() => finish(false))
        .onDisconnect(() => finish(false))
        .build();
    } catch {
      finish(false);
    }
  });
}

export const RECAP_VOICE_PROMPT = {
  prompt: 'You are Jarvis, a calm surgical education attending opening a brief PEARLS-lite reflection. Ask exactly one reaction question: "How did that feel?" Wait for the learner to answer, then briefly acknowledge their answer without judging performance. After that acknowledgement, remain silent and wait for the panel. Do not ask a self-assessment question or any further question: the panel owns that next step and all progression. No scores, performance facts, diagnoses, strengths, improvements, or surgical outcomes are supplied to this conversation. Never invent or infer them. Do not provide medical advice or execute scene actions. The panel presents the separate Clinical reasoning and Procedural skill scorecards and the fact-based debrief.',
  firstMessage: 'How did that feel?',
  reactionQuestion: 'How did that feel?',
  selfAssessmentQuestion: 'What is one thing you would do differently?',
};

export function registerRecapRoutes(
  app: Hono, config: Config, rt: Realtime, storage: Storage,
  authorize: AuthorizeSession = createSessionAuthorizer(config.spacetime),
) {
  // Static instructional copy, never session or patient data.
  app.get('/v1/recap/voice-prompt', c => c.json(RECAP_VOICE_PROMPT));

  app.get('/v1/sessions/:sessionId/replay/:jobId', async c => {
    c.header('Cache-Control', 'no-store');
    const auth = c.req.header('authorization') ?? '';
    const token = auth.startsWith('Bearer ') ? auth.slice(7).trim() : '';
    if (!token || token.length > 8192) return c.json({ error: 'session bearer token required' }, 401);
    const { sessionId, jobId } = c.req.param();
    try {
      if (!await authorize(token, sessionId)) return c.json({ error: 'session access denied' }, 403);
      const conn = rt.require();
      const job = [...conn.db.sessionMotionJobs.iter()].find(j => j.jobId === jobId && j.sessionId === sessionId);
      if (!job) return c.json({ error: 'replay job not found in session' }, 404);
      const artifacts = [...conn.db.sessionArtifacts.iter()].filter(a =>
        a.sessionId === sessionId && a.attemptId === job.attemptId && a.status === 'available' && a.contentType.startsWith('video/'));
      const input = artifacts.find(a => a.artifactId === job.inputArtifactId && a.kind === 'raw_clip');
      const replay = artifacts.find(a => job.outputArtifactIds.includes(a.artifactId) && a.kind === 'replay_video' && a.jobId === jobId && a.jobRun === job.run);
      const ttlMs = Math.min(config.storage.downloadTtlMs, 300_000);
      let status = job.status === 'running' ? 'processing' : job.status;
      let reason = job.error ?? '';
      const replayKind = job.quality?.replayKind ?? 'kinematic';
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
      if (status === 'failed' && !reason) reason = 'motion processing failed without a worker reason';
      const [sourceVideoUrl, replayVideoUrl] = await Promise.all([
        input ? storage.presignGet(input.storageKey, ttlMs, input.filename).then(s => s.url) : '',
        replay && status === 'ready' ? storage.presignGet(replay.storageKey, ttlMs, replay.filename).then(s => s.url) : '',
      ]);
      return c.json({
        schemaVersion: 'scalpal.replay.v1', sessionId, attemptId: job.attemptId, jobId,
        status, reason, progress: job.progress ?? 0, stage: job.stage ?? '',
        sourceVideoUrl, replayVideoUrl, expiresAtUnixMs: Date.now() + ttlMs,
        replayKind,
        label: replayKind === 'kinematic'
          ? 'Your hand motion, retargeted to a robot hand. Kinematic replay, not a trained robot.'
          : `Unsupported replay kind: ${replayKind}. This recap requires a kinematic replay.`,
      });
    } catch (error) {
      if (error instanceof Unavailable) return c.json({ error: error.message }, 503);
      throw error;
    }
  });
}
