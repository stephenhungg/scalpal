// Scalpal realtime module: reducers, lifecycle hooks, scheduled sweep, views.
//
// Authorization model
// - A session is joined with an invite code; the code decides the role.
// - Roles: operator, headset, coach, viewer. One identity may hold several
//   roles in a session (one membership row per role).
// - Service identities (the gateway) bypass membership checks and are the only
//   callers allowed to issue storage URLs, confirm artifacts and drive motion
//   jobs. The identity that publishes the module is the first service
//   identity.
// - All tables are private; clients read through the views at the bottom.

import { ScheduleAt, Timestamp } from 'spacetimedb';
import {
  SenderError,
  t,
  type InferSchema,
  type ReducerCtx,
} from 'spacetimedb/server';
import spacetimedb, {
  QualitySummary,
  artifact,
  attempt,
  coachMessage,
  coachStatus,
  command,
  exerciseEvent,
  exerciseState,
  mediaSource,
  membership,
  motionJob,
  replayState,
  rtcSignal,
  session,
  serviceGrant,
  sessionInvite,
  sweepTimer,
  transferGrant,
} from './schema';

export default spacetimedb;

type Ctx = ReducerCtx<InferSchema<typeof spacetimedb>>;
type Identity = Ctx['sender'];

// ---------------------------------------------------------------------------
// Vocabulary (mirrors packages/contracts/realtime-v1.md)
// ---------------------------------------------------------------------------

const ROLES = ['operator', 'headset', 'coach', 'viewer'] as const;
type Role = (typeof ROLES)[number];

const MODES = [
  'Startup',
  'Selecting',
  'Confirmed',
  'Fitting',
  'Practicing',
  'Reviewing',
  'ProcessingMotion',
  'RobotReplay',
  'Recap',
];
const REGISTRATION = ['unaligned', 'valid', 'uncertain'];
const RECORDING = ['off', 'recording', 'failed', 'complete'];
const PRACTICE_STATUS = ['not_started', 'in_progress', 'completed', 'abandoned'];
const SPEAKERS = ['learner', 'coach', 'system'];
const COACH_STATUS = ['offline', 'connecting', 'listening', 'thinking', 'speaking', 'error'];
const ARTIFACT_KINDS = [
  'raw_clip',
  'capture_manifest',
  'scene_timeline',
  'hand_estimates',
  'robot_trajectory',
  'replay_video',
  'quality_report',
  'other',
];
const COMMAND_RESOLUTIONS = ['applied', 'rejected', 'unavailable', 'failed'];
const MEDIA_STATUS = ['off', 'starting', 'live', 'stopped', 'denied', 'error'];
const SIGNAL_KINDS = ['join', 'offer', 'answer', 'ice', 'bye'];

/** Allowlisted voice/app actions and the argument each one requires. */
const ACTIONS: Record<string, 'none' | 'target' | 'bool' | 'number'> = {
  previewExercise: 'target',
  rotatePreview: 'bool',
  zoomPreview: 'number',
  isolateStructure: 'target',
  restoreContext: 'none',
  confirmExercise: 'target',
  highlightStructure: 'target',
  requestHint: 'none',
  pausePractice: 'none',
  resumePractice: 'none',
};

const COMMAND_TTL_MS = 15_000;
const GRANT_REQUEST_TTL_MS = 30_000;
const MAX_GRANT_TTL_MS = 60 * 60 * 1000;
const SIGNAL_TTL_MS = 60_000;
const DEFAULT_LEASE_MS = 60_000;
const MAX_LEASE_MS = 10 * 60 * 1000;
const DEFAULT_MAX_RUNS = 3;
const MAX_TEXT = 4_000;
const MAX_SIGNAL_PAYLOAD = 64_000;
const SWEEP_INTERVAL_MICROS = 2_000_000n;

const CODE_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
const ID_PATTERN = /^[A-Za-z0-9_-]{6,64}$/;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function fail(reason: string): never {
  throw new SenderError(reason);
}

function oneOf(value: string, allowed: readonly string[], field: string) {
  if (!allowed.includes(value)) fail(`invalid ${field}: ${value}`);
}

function checkId(value: string, field: string) {
  if (!ID_PATTERN.test(value)) fail(`invalid ${field}`);
}

function checkText(value: string, field: string, max = MAX_TEXT) {
  if (value.length > max) fail(`${field} too long`);
}

function after(ctx: Ctx, ms: number): Timestamp {
  return new Timestamp(ctx.timestamp.microsSinceUnixEpoch + BigInt(Math.round(ms)) * 1000n);
}

function olderThan(ctx: Ctx, at: Timestamp, ms: number): boolean {
  return ctx.timestamp.microsSinceUnixEpoch - at.microsSinceUnixEpoch > BigInt(ms) * 1000n;
}

function isService(ctx: Ctx): boolean {
  return ctx.db.serviceIdentity.identity.find(ctx.sender) != null;
}

function rolesOf(ctx: Ctx, sessionId: string, who: Identity): Set<string> {
  const roles = new Set<string>();
  for (const m of ctx.db.membership.by_session_identity.filter([sessionId, who])) {
    roles.add(m.role);
  }
  return roles;
}

function getSession(ctx: Ctx, sessionId: string) {
  return ctx.db.session.sessionId.find(sessionId) ?? fail('unknown session');
}

function activeSession(ctx: Ctx, sessionId: string) {
  const s = getSession(ctx, sessionId);
  if (s.status !== 'active') fail('session has ended');
  return s;
}

/**
 * Require the sender to hold one of `allowed` roles in the session (service
 * identities always pass). Returns the matching role for auditing.
 */
function requireRole(ctx: Ctx, sessionId: string, allowed: readonly Role[]): string {
  if (isService(ctx)) return 'service';
  const roles = rolesOf(ctx, sessionId, ctx.sender);
  if (roles.size === 0) fail('not a member of this session');
  for (const r of allowed) if (roles.has(r)) return r;
  fail(`requires role: ${allowed.join(' or ')}`);
}

function requireMember(ctx: Ctx, sessionId: string): string {
  return requireRole(ctx, sessionId, ROLES);
}

function requireService(ctx: Ctx) {
  if (!isService(ctx)) fail('service identity required');
}

function randomCode(ctx: Ctx, length: number): string {
  let out = '';
  for (let i = 0; i < length; i++) {
    out += CODE_ALPHABET[ctx.random.integerInRange(0, CODE_ALPHABET.length - 1)];
  }
  return out;
}

function newInviteCode(ctx: Ctx): string {
  for (let i = 0; i < 20; i++) {
    const code = randomCode(ctx, 6);
    if (!ctx.db.sessionInvite.code.find(code)) return code;
  }
  fail('could not allocate invite code');
}

function attemptIdFor(sessionId: string, ordinal: number) {
  return `${sessionId}-a${ordinal}`;
}

function safeFilename(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 120);
  return cleaned.length > 0 ? cleaned : 'file';
}

function storageKeyFor(sessionId: string, attemptId: string, artifactId: string, filename: string) {
  return `sessions/${sessionId}/${attemptId}/${artifactId}/${safeFilename(filename)}`;
}

function bumpState(
  ctx: Ctx,
  sessionId: string,
  patch: Partial<Omit<typeof exerciseState.rowType.type, 'sessionId'>>,
  stepChanged: boolean
) {
  const current = ctx.db.exerciseState.sessionId.find(sessionId) ?? fail('no exercise state');
  ctx.db.exerciseState.sessionId.update({
    ...current,
    ...patch,
    stateVersion: current.stateVersion + 1n,
    stepVersion: stepChanged ? current.stepVersion + 1n : current.stepVersion,
    updatedAt: ctx.timestamp,
    updatedBy: ctx.sender,
  });
}

function emit(
  ctx: Ctx,
  sessionId: string,
  attemptId: string,
  kind: string,
  message: string,
  extra: { stepId?: string; structureId?: string; deviceTimeMs?: number } = {}
) {
  ctx.db.exerciseEvent.insert({
    eventId: 0n,
    sessionId,
    attemptId,
    kind,
    stepId: extra.stepId,
    structureId: extra.structureId,
    message,
    deviceTimeMs: extra.deviceTimeMs,
    at: ctx.timestamp,
  });
}

function hasLiveConnection(ctx: Ctx, who: Identity): boolean {
  for (const _ of ctx.db.connection.identity.filter(who)) return true;
  return false;
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

export const init = spacetimedb.init(ctx => {
  ctx.db.serviceIdentity.insert({
    identity: ctx.sender,
    label: 'module owner',
    addedAt: ctx.timestamp,
  });
  ctx.db.sweepTimer.insert({
    scheduledId: 0n,
    scheduledAt: ScheduleAt.interval(SWEEP_INTERVAL_MICROS),
  });
});

export const onConnect = spacetimedb.clientConnected(ctx => {
  if (!ctx.connectionId) return;
  ctx.db.connection.insert({
    connectionId: ctx.connectionId,
    identity: ctx.sender,
    connectedAt: ctx.timestamp,
  });
});

export const onDisconnect = spacetimedb.clientDisconnected(ctx => {
  if (ctx.connectionId) ctx.db.connection.connectionId.delete(ctx.connectionId);
  if (hasLiveConnection(ctx, ctx.sender)) return;
  // The publisher's last connection dropped: the live view is gone.
  for (const src of ctx.db.mediaSource.iter()) {
    if (src.publisher && src.publisher.isEqual(ctx.sender) && src.status === 'live') {
      ctx.db.mediaSource.sessionId.update({
        ...src,
        status: 'stopped',
        detail: 'publisher disconnected',
        updatedAt: ctx.timestamp,
      });
    }
  }
});

/** Expire leases, commands, grants and signals. Runs every 2 seconds. */
export const sweep = spacetimedb.reducer(
  { onSchedule: sweepTimer },
  { timer: sweepTimer.rowType },
  (ctx, _args) => {
    // Only the scheduler (the database itself) may run the sweep.
    if (!ctx.sender.isEqual(ctx.databaseIdentity)) fail('sweep is scheduled only');
    const now = ctx.timestamp.microsSinceUnixEpoch;

    for (const job of [...ctx.db.motionJob.iter()]) {
      if (job.status !== 'running' || !job.leaseExpiresAt) continue;
      if (job.leaseExpiresAt.microsSinceUnixEpoch > now) continue;
      const exhausted = job.run >= job.maxRuns;
      ctx.db.motionJob.jobId.update({
        ...job,
        status: exhausted ? 'failed' : 'queued',
        workerId: undefined,
        leaseExpiresAt: undefined,
        error: exhausted
          ? `lease expired on run ${job.run}; no runs left`
          : `lease expired on run ${job.run}; requeued`,
        updatedAt: ctx.timestamp,
      });
    }

    for (const cmd of [...ctx.db.command.iter()]) {
      if (cmd.status === 'pending' && olderThan(ctx, cmd.requestedAt, COMMAND_TTL_MS)) {
        ctx.db.command.commandId.update({
          ...cmd,
          status: 'expired',
          reason: 'headset did not acknowledge in time',
          resolvedAt: ctx.timestamp,
        });
      }
    }

    for (const g of [...ctx.db.transferGrant.iter()]) {
      if (g.status === 'requested' && olderThan(ctx, g.requestedAt, GRANT_REQUEST_TTL_MS)) {
        ctx.db.transferGrant.grantId.update({
          ...g,
          status: 'expired',
          reason: 'gateway did not respond',
        });
      } else if (
        g.status === 'issued' &&
        g.expiresAt &&
        g.expiresAt.microsSinceUnixEpoch <= now
      ) {
        ctx.db.transferGrant.grantId.update({ ...g, status: 'expired', url: undefined });
      }
    }

    for (const g of [...ctx.db.serviceGrant.iter()]) {
      if (g.status === 'requested' && olderThan(ctx, g.requestedAt, GRANT_REQUEST_TTL_MS)) {
        ctx.db.serviceGrant.grantId.update({ ...g, status: 'expired', reason: 'gateway did not respond' });
      } else if (g.status === 'issued' && g.expiresAt && g.expiresAt.microsSinceUnixEpoch <= now) {
        ctx.db.serviceGrant.grantId.update({ ...g, status: 'expired', payload: undefined });
      }
    }

    for (const s of [...ctx.db.rtcSignal.iter()]) {
      if (olderThan(ctx, s.createdAt, SIGNAL_TTL_MS)) ctx.db.rtcSignal.signalId.delete(s.signalId);
    }
  }
);

// ---------------------------------------------------------------------------
// Service identities
// ---------------------------------------------------------------------------

export const addServiceIdentity = spacetimedb.reducer(
  { identity: t.identity(), label: t.string() },
  (ctx, { identity, label }) => {
    requireService(ctx);
    checkText(label, 'label', 120);
    if (ctx.db.serviceIdentity.identity.find(identity)) return;
    ctx.db.serviceIdentity.insert({ identity, label, addedAt: ctx.timestamp });
  }
);

export const removeServiceIdentity = spacetimedb.reducer(
  { identity: t.identity() },
  (ctx, { identity }) => {
    requireService(ctx);
    if (identity.isEqual(ctx.sender)) fail('cannot remove yourself');
    ctx.db.serviceIdentity.identity.delete(identity);
  }
);

// ---------------------------------------------------------------------------
// Sessions and membership
// ---------------------------------------------------------------------------

export const createSession = spacetimedb.reducer(
  {
    sessionId: t.string(),
    label: t.string(),
    exerciseId: t.string(),
    exerciseVersion: t.string(),
    displayName: t.string(),
  },
  (ctx, { sessionId, label, exerciseId, exerciseVersion, displayName }) => {
    checkId(sessionId, 'sessionId');
    checkText(label, 'label', 120);
    checkText(displayName, 'displayName', 60);
    checkText(exerciseId, 'exerciseId', 120);
    checkText(exerciseVersion, 'exerciseVersion', 60);
    if (ctx.db.session.sessionId.find(sessionId)) fail('session id already exists');

    const attemptId = attemptIdFor(sessionId, 1);
    ctx.db.session.insert({
      sessionId,
      label: label || 'Scalpal session',
      createdBy: ctx.sender,
      createdAt: ctx.timestamp,
      status: 'active',
      exerciseId,
      exerciseVersion,
      currentAttemptId: attemptId,
      attemptCount: 1,
      endedAt: undefined,
    });
    ctx.db.attempt.insert({
      attemptId,
      sessionId,
      ordinal: 1,
      exerciseId,
      exerciseVersion,
      startedAt: ctx.timestamp,
      endedAt: undefined,
      practiceStatus: 'not_started',
      resultSummary: undefined,
      stepsCompleted: 0,
      stepsTotal: 0,
      mistakes: 0,
      hintsUsed: 0,
    });
    ctx.db.exerciseState.insert({
      sessionId,
      attemptId,
      exerciseId,
      exerciseVersion,
      mode: 'Startup',
      stepId: undefined,
      stepIndex: 0,
      stepCount: 0,
      selectedStructureId: undefined,
      highlightedStructureId: undefined,
      previewRotating: false,
      paused: false,
      registration: 'unaligned',
      registrationReason: undefined,
      recording: 'off',
      stateVersion: 1n,
      stepVersion: 1n,
      updatedAt: ctx.timestamp,
      updatedBy: ctx.sender,
    });
    ctx.db.coachStatus.insert({
      sessionId,
      status: 'offline',
      detail: undefined,
      updatedAt: ctx.timestamp,
    });
    ctx.db.mediaSource.insert({
      sessionId,
      publisher: undefined,
      status: 'off',
      label: undefined,
      detail: undefined,
      width: undefined,
      height: undefined,
      updatedAt: ctx.timestamp,
    });
    ctx.db.replayState.insert({
      sessionId,
      artifactId: undefined,
      playing: false,
      positionMs: 0,
      rate: 1,
      updatedAt: ctx.timestamp,
      updatedBy: ctx.sender,
    });
    for (const role of ROLES) {
      ctx.db.sessionInvite.insert({
        code: newInviteCode(ctx),
        sessionId,
        role,
        createdAt: ctx.timestamp,
        revoked: false,
      });
    }
    ctx.db.membership.insert({
      membershipId: 0n,
      sessionId,
      identity: ctx.sender,
      role: 'operator',
      displayName: displayName || 'Operator',
      joinedAt: ctx.timestamp,
    });
    emit(ctx, sessionId, attemptId, 'session_created', `Session created for ${exerciseId}@${exerciseVersion}`);
  }
);

export const joinSession = spacetimedb.reducer(
  { code: t.string(), displayName: t.string() },
  (ctx, { code, displayName }) => {
    checkText(displayName, 'displayName', 60);
    const invite = ctx.db.sessionInvite.code.find(code.trim().toUpperCase());
    if (!invite || invite.revoked) fail('invalid or revoked code');
    const s = activeSession(ctx, invite.sessionId);
    const existing = [...ctx.db.membership.by_session_identity.filter([s.sessionId, ctx.sender])];
    const same = existing.find(m => m.role === invite.role);
    if (same) {
      if (displayName) ctx.db.membership.membershipId.update({ ...same, displayName });
      return;
    }
    ctx.db.membership.insert({
      membershipId: 0n,
      sessionId: s.sessionId,
      identity: ctx.sender,
      role: invite.role,
      displayName: displayName || invite.role,
      joinedAt: ctx.timestamp,
    });
  }
);

export const setDisplayName = spacetimedb.reducer(
  { sessionId: t.string(), displayName: t.string() },
  (ctx, { sessionId, displayName }) => {
    checkText(displayName, 'displayName', 60);
    for (const m of [...ctx.db.membership.by_session_identity.filter([sessionId, ctx.sender])]) {
      ctx.db.membership.membershipId.update({ ...m, displayName });
    }
  }
);

export const leaveSession = spacetimedb.reducer(
  { sessionId: t.string() },
  (ctx, { sessionId }) => {
    for (const m of [...ctx.db.membership.by_session_identity.filter([sessionId, ctx.sender])]) {
      ctx.db.membership.membershipId.delete(m.membershipId);
    }
  }
);

export const removeMember = spacetimedb.reducer(
  { membershipId: t.u64() },
  (ctx, { membershipId }) => {
    const m = ctx.db.membership.membershipId.find(membershipId) ?? fail('unknown membership');
    requireRole(ctx, m.sessionId, ['operator']);
    ctx.db.membership.membershipId.delete(membershipId);
  }
);

/** Revoke a role's invite code and issue a fresh one. */
export const rotateInvite = spacetimedb.reducer(
  { sessionId: t.string(), role: t.string() },
  (ctx, { sessionId, role }) => {
    requireRole(ctx, sessionId, ['operator']);
    oneOf(role, ROLES, 'role');
    activeSession(ctx, sessionId);
    for (const inv of [...ctx.db.sessionInvite.sessionId.filter(sessionId)]) {
      if (inv.role === role && !inv.revoked) {
        ctx.db.sessionInvite.code.update({ ...inv, revoked: true });
      }
    }
    ctx.db.sessionInvite.insert({
      code: newInviteCode(ctx),
      sessionId,
      role,
      createdAt: ctx.timestamp,
      revoked: false,
    });
  }
);

export const endSession = spacetimedb.reducer(
  { sessionId: t.string() },
  (ctx, { sessionId }) => {
    requireRole(ctx, sessionId, ['operator']);
    const s = activeSession(ctx, sessionId);
    ctx.db.session.sessionId.update({ ...s, status: 'ended', endedAt: ctx.timestamp });
    for (const inv of [...ctx.db.sessionInvite.sessionId.filter(sessionId)]) {
      if (!inv.revoked) ctx.db.sessionInvite.code.update({ ...inv, revoked: true });
    }
    const src = ctx.db.mediaSource.sessionId.find(sessionId);
    if (src && src.status !== 'off') {
      ctx.db.mediaSource.sessionId.update({
        ...src,
        status: 'stopped',
        detail: 'session ended',
        updatedAt: ctx.timestamp,
      });
    }
    emit(ctx, sessionId, s.currentAttemptId, 'session_ended', 'Session ended');
  }
);

// ---------------------------------------------------------------------------
// Attempts and exercise state (headset authority)
// ---------------------------------------------------------------------------

/** Begin a new attempt (retry). Previous attempts keep their results/jobs. */
export const startAttempt = spacetimedb.reducer(
  { sessionId: t.string(), exerciseId: t.string(), exerciseVersion: t.string() },
  (ctx, { sessionId, exerciseId, exerciseVersion }) => {
    requireRole(ctx, sessionId, ['operator', 'headset']);
    const s = activeSession(ctx, sessionId);
    checkText(exerciseId, 'exerciseId', 120);
    checkText(exerciseVersion, 'exerciseVersion', 60);
    const exId = exerciseId || s.exerciseId;
    const exVer = exerciseVersion || s.exerciseVersion;

    const prev = ctx.db.attempt.attemptId.find(s.currentAttemptId);
    if (prev && !prev.endedAt) {
      ctx.db.attempt.attemptId.update({
        ...prev,
        endedAt: ctx.timestamp,
        practiceStatus: prev.practiceStatus === 'completed' ? 'completed' : 'abandoned',
      });
    }
    const ordinal = s.attemptCount + 1;
    const attemptId = attemptIdFor(sessionId, ordinal);
    ctx.db.attempt.insert({
      attemptId,
      sessionId,
      ordinal,
      exerciseId: exId,
      exerciseVersion: exVer,
      startedAt: ctx.timestamp,
      endedAt: undefined,
      practiceStatus: 'not_started',
      resultSummary: undefined,
      stepsCompleted: 0,
      stepsTotal: 0,
      mistakes: 0,
      hintsUsed: 0,
    });
    ctx.db.session.sessionId.update({
      ...s,
      exerciseId: exId,
      exerciseVersion: exVer,
      currentAttemptId: attemptId,
      attemptCount: ordinal,
    });
    bumpState(
      ctx,
      sessionId,
      {
        attemptId,
        exerciseId: exId,
        exerciseVersion: exVer,
        mode: 'Selecting',
        stepId: undefined,
        stepIndex: 0,
        stepCount: 0,
        selectedStructureId: undefined,
        paused: false,
        recording: 'off',
        highlightedStructureId: undefined,
        previewRotating: false,
        registration: 'unaligned',
        registrationReason: undefined,
      },
      true
    );
    // Commands addressed to the old attempt can no longer apply.
    for (const cmd of [...ctx.db.command.sessionId.filter(sessionId)]) {
      if (cmd.status === 'pending') {
        ctx.db.command.commandId.update({
          ...cmd,
          status: 'expired',
          reason: 'superseded by a new attempt',
          resolvedAt: ctx.timestamp,
        });
      }
    }
    emit(ctx, sessionId, attemptId, 'attempt_started', `Attempt ${ordinal} started`);
  }
);

/**
 * Publish the headset's confirmed exercise state. Optional fields left unset
 * keep their current value; use the `clear*` flags to unset them.
 */
export const publishExerciseState = spacetimedb.reducer(
  {
    sessionId: t.string(),
    attemptId: t.string(),
    mode: t.string(),
    stepId: t.option(t.string()),
    stepIndex: t.u32(),
    stepCount: t.u32(),
    selectedStructureId: t.option(t.string()),
    clearSelectedStructure: t.bool(),
    highlightedStructureId: t.option(t.string()),
    clearHighlightedStructure: t.bool(),
    previewRotating: t.bool(),
    paused: t.bool(),
    registration: t.string(),
    registrationReason: t.option(t.string()),
    recording: t.string(),
  },
  (ctx, a) => {
    requireRole(ctx, a.sessionId, ['headset']);
    const s = activeSession(ctx, a.sessionId);
    if (a.attemptId !== s.currentAttemptId) fail('stale attempt');
    oneOf(a.mode, MODES, 'mode');
    oneOf(a.registration, REGISTRATION, 'registration');
    oneOf(a.recording, RECORDING, 'recording');
    const cur = ctx.db.exerciseState.sessionId.find(a.sessionId) ?? fail('no exercise state');
    const stepChanged = cur.mode !== a.mode || cur.stepId !== a.stepId || cur.stepIndex !== a.stepIndex;
    bumpState(
      ctx,
      a.sessionId,
      {
        mode: a.mode,
        stepId: a.stepId,
        stepIndex: a.stepIndex,
        stepCount: a.stepCount,
        selectedStructureId: a.clearSelectedStructure
          ? undefined
          : (a.selectedStructureId ?? cur.selectedStructureId),
        highlightedStructureId: a.clearHighlightedStructure
          ? undefined
          : (a.highlightedStructureId ?? cur.highlightedStructureId),
        previewRotating: a.previewRotating,
        paused: a.paused,
        registration: a.registration,
        registrationReason: a.registrationReason,
        recording: a.recording,
      },
      stepChanged
    );
    const att = ctx.db.attempt.attemptId.find(a.attemptId);
    if (att && att.practiceStatus === 'not_started' && a.mode === 'Practicing') {
      ctx.db.attempt.attemptId.update({ ...att, practiceStatus: 'in_progress' });
    }
  }
);

export const appendExerciseEvent = spacetimedb.reducer(
  {
    sessionId: t.string(),
    attemptId: t.string(),
    kind: t.string(),
    stepId: t.option(t.string()),
    structureId: t.option(t.string()),
    message: t.string(),
    deviceTimeMs: t.option(t.f64()),
  },
  (ctx, a) => {
    requireRole(ctx, a.sessionId, ['headset']);
    const session = activeSession(ctx, a.sessionId);
    const attempt = ctx.db.attempt.attemptId.find(a.attemptId) ?? fail('unknown attempt');
    if (attempt.sessionId !== a.sessionId) fail('attempt belongs to another session');
    if (session.currentAttemptId !== a.attemptId) fail('stale attempt');
    checkText(a.kind, 'kind', 60);
    checkText(a.message, 'message');
    emit(ctx, a.sessionId, a.attemptId, a.kind, a.message, {
      stepId: a.stepId,
      structureId: a.structureId,
      deviceTimeMs: a.deviceTimeMs,
    });
  }
);

/** Record the authored learning result for an attempt. */
export const setAttemptResult = spacetimedb.reducer(
  {
    attemptId: t.string(),
    practiceStatus: t.string(),
    stepsCompleted: t.u32(),
    stepsTotal: t.u32(),
    mistakes: t.u32(),
    hintsUsed: t.u32(),
    resultSummary: t.option(t.string()),
  },
  (ctx, a) => {
    const att = ctx.db.attempt.attemptId.find(a.attemptId) ?? fail('unknown attempt');
    requireRole(ctx, att.sessionId, ['headset']);
    oneOf(a.practiceStatus, PRACTICE_STATUS, 'practiceStatus');
    if (a.resultSummary) checkText(a.resultSummary, 'resultSummary');
    const ended = a.practiceStatus === 'completed' || a.practiceStatus === 'abandoned';
    ctx.db.attempt.attemptId.update({
      ...att,
      practiceStatus: a.practiceStatus,
      stepsCompleted: a.stepsCompleted,
      stepsTotal: a.stepsTotal,
      mistakes: a.mistakes,
      hintsUsed: a.hintsUsed,
      resultSummary: a.resultSummary,
      endedAt: ended ? (att.endedAt ?? ctx.timestamp) : att.endedAt,
    });
  }
);

// ---------------------------------------------------------------------------
// Coach (Jarvis) transcript and status
// ---------------------------------------------------------------------------

export const postCoachMessage = spacetimedb.reducer(
  { sessionId: t.string(), speaker: t.string(), text: t.string() },
  (ctx, { sessionId, speaker, text }) => {
    requireRole(ctx, sessionId, ['coach', 'headset']);
    const s = activeSession(ctx, sessionId);
    oneOf(speaker, SPEAKERS, 'speaker');
    checkText(text, 'text');
    if (!text.trim()) return;
    ctx.db.coachMessage.insert({
      messageId: 0n,
      sessionId,
      attemptId: s.currentAttemptId,
      speaker,
      text,
      at: ctx.timestamp,
    });
  }
);

export const setCoachStatus = spacetimedb.reducer(
  { sessionId: t.string(), status: t.string(), detail: t.option(t.string()) },
  (ctx, { sessionId, status, detail }) => {
    requireRole(ctx, sessionId, ['coach', 'headset']);
    oneOf(status, COACH_STATUS, 'status');
    if (detail) checkText(detail, 'detail', 500);
    const cur = ctx.db.coachStatus.sessionId.find(sessionId) ?? fail('no coach status');
    ctx.db.coachStatus.sessionId.update({ ...cur, status, detail, updatedAt: ctx.timestamp });
  }
);

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

/**
 * Request an allowlisted app action. Idempotent on `commandId`: resending the
 * same id is a no-op. Rejected immediately when `expectedStepVersion` is stale.
 */
export const requestCommand = spacetimedb.reducer(
  {
    commandId: t.string(),
    sessionId: t.string(),
    action: t.string(),
    targetId: t.option(t.string()),
    argBool: t.option(t.bool()),
    argNumber: t.option(t.f64()),
    expectedStepVersion: t.u64(),
  },
  (ctx, a) => {
    const role = requireRole(ctx, a.sessionId, ['coach', 'operator']);
    checkId(a.commandId, 'commandId');
    const existing = ctx.db.command.commandId.find(a.commandId);
    if (existing) {
      if (!existing.requestedBy.isEqual(ctx.sender)) fail('commandId already used');
      return;
    }
    const s = activeSession(ctx, a.sessionId);
    const kind = ACTIONS[a.action] ?? fail(`unsupported action: ${a.action}`);
    if (kind === 'target' && !a.targetId) fail(`${a.action} requires targetId`);
    if (kind === 'bool' && a.argBool == null) fail(`${a.action} requires argBool`);
    if (kind === 'number' && a.argNumber == null) fail(`${a.action} requires argNumber`);
    if (a.targetId) checkText(a.targetId, 'targetId', 120);

    const state = ctx.db.exerciseState.sessionId.find(a.sessionId) ?? fail('no exercise state');
    const stale = a.expectedStepVersion !== state.stepVersion;
    ctx.db.command.insert({
      commandId: a.commandId,
      sessionId: a.sessionId,
      attemptId: s.currentAttemptId,
      action: a.action,
      targetId: a.targetId,
      argBool: a.argBool,
      argNumber: a.argNumber,
      expectedStepVersion: a.expectedStepVersion,
      requestedBy: ctx.sender,
      requestedRole: role,
      requestedAt: ctx.timestamp,
      status: stale ? 'rejected' : 'pending',
      reason: stale
        ? `stale step version: expected ${a.expectedStepVersion}, current ${state.stepVersion}`
        : undefined,
      resolvedAt: stale ? ctx.timestamp : undefined,
      resolvedStateVersion: stale ? state.stateVersion : undefined,
    });
  }
);

/** Headset acknowledges a pending command after validating/applying it. */
export const resolveCommand = spacetimedb.reducer(
  { commandId: t.string(), status: t.string(), reason: t.option(t.string()) },
  (ctx, { commandId, status, reason }) => {
    const cmd = ctx.db.command.commandId.find(commandId) ?? fail('unknown command');
    requireRole(ctx, cmd.sessionId, ['headset']);
    oneOf(status, COMMAND_RESOLUTIONS, 'status');
    if (reason) checkText(reason, 'reason', 500);
    if (cmd.status !== 'pending') fail(`command already ${cmd.status}`);
    const state = ctx.db.exerciseState.sessionId.find(cmd.sessionId);
    ctx.db.command.commandId.update({
      ...cmd,
      status,
      reason,
      resolvedAt: ctx.timestamp,
      resolvedStateVersion: state?.stateVersion,
    });
  }
);

// ---------------------------------------------------------------------------
// Artifacts and transfer grants
// ---------------------------------------------------------------------------

/**
 * Declare an artifact and ask the gateway for an upload URL. Calling again
 * for the same artifact (same uploader) issues a fresh grant for a retry.
 */
export const requestUpload = spacetimedb.reducer(
  {
    grantId: t.string(),
    artifactId: t.string(),
    sessionId: t.string(),
    attemptId: t.string(),
    kind: t.string(),
    filename: t.string(),
    contentType: t.string(),
    declaredBytes: t.option(t.u64()),
    sha256: t.option(t.string()),
  },
  (ctx, a) => {
    requireRole(ctx, a.sessionId, ['headset', 'operator']);
    activeSession(ctx, a.sessionId);
    checkId(a.grantId, 'grantId');
    checkId(a.artifactId, 'artifactId');
    oneOf(a.kind, ARTIFACT_KINDS, 'kind');
    checkText(a.filename, 'filename', 200);
    checkText(a.contentType, 'contentType', 120);
    if (a.sha256 && !/^[a-f0-9]{64}$/.test(a.sha256)) fail('sha256 must be lowercase hex');
    const att = ctx.db.attempt.attemptId.find(a.attemptId) ?? fail('unknown attempt');
    if (att.sessionId !== a.sessionId) fail('attempt belongs to another session');
    if (ctx.db.transferGrant.grantId.find(a.grantId)) fail('grantId already used');

    const existing = ctx.db.artifact.artifactId.find(a.artifactId);
    if (existing) {
      if (!existing.createdBy.isEqual(ctx.sender)) fail('artifactId already used');
      if (existing.status === 'available') fail('artifact already available');
      ctx.db.artifact.artifactId.update({
        ...existing,
        status: 'pending_upload',
        statusReason: undefined,
        declaredBytes: a.declaredBytes,
        sha256: a.sha256,
      });
    } else {
      ctx.db.artifact.insert({
        artifactId: a.artifactId,
        sessionId: a.sessionId,
        attemptId: a.attemptId,
        kind: a.kind,
        storageKey: storageKeyFor(a.sessionId, a.attemptId, a.artifactId, a.filename),
        filename: a.filename,
        contentType: a.contentType,
        declaredBytes: a.declaredBytes,
        verifiedBytes: undefined,
        sha256: a.sha256,
        status: 'pending_upload',
        statusReason: undefined,
        jobId: undefined,
        jobRun: undefined,
        createdBy: ctx.sender,
        createdAt: ctx.timestamp,
        availableAt: undefined,
      });
    }
    ctx.db.transferGrant.insert({
      grantId: a.grantId,
      sessionId: a.sessionId,
      artifactId: a.artifactId,
      direction: 'upload',
      requestedBy: ctx.sender,
      requestedAt: ctx.timestamp,
      status: 'requested',
      url: undefined,
      method: undefined,
      reason: undefined,
      expiresAt: undefined,
    });
  }
);

/** Uploader reports the PUT finished; the gateway then verifies the object. */
export const markUploaded = spacetimedb.reducer(
  { artifactId: t.string() },
  (ctx, { artifactId }) => {
    const art = ctx.db.artifact.artifactId.find(artifactId) ?? fail('unknown artifact');
    if (!art.createdBy.isEqual(ctx.sender) && !isService(ctx)) fail('only the uploader can mark it uploaded');
    if (art.status === 'available' || art.status === 'verifying') return;
    if (art.status !== 'pending_upload') fail(`artifact is ${art.status}`);
    ctx.db.artifact.artifactId.update({ ...art, status: 'verifying', statusReason: undefined });
  }
);

export const requestDownload = spacetimedb.reducer(
  { grantId: t.string(), artifactId: t.string() },
  (ctx, { grantId, artifactId }) => {
    checkId(grantId, 'grantId');
    const art = ctx.db.artifact.artifactId.find(artifactId) ?? fail('unknown artifact');
    requireMember(ctx, art.sessionId);
    if (art.status !== 'available') fail(`artifact is ${art.status}`);
    if (ctx.db.transferGrant.grantId.find(grantId)) fail('grantId already used');
    ctx.db.transferGrant.insert({
      grantId,
      sessionId: art.sessionId,
      artifactId,
      direction: 'download',
      requestedBy: ctx.sender,
      requestedAt: ctx.timestamp,
      status: 'requested',
      url: undefined,
      method: undefined,
      reason: undefined,
      expiresAt: undefined,
    });
  }
);

export const deleteArtifact = spacetimedb.reducer(
  { artifactId: t.string() },
  (ctx, { artifactId }) => {
    const art = ctx.db.artifact.artifactId.find(artifactId) ?? fail('unknown artifact');
    requireRole(ctx, art.sessionId, ['operator']);
    // The gateway watches for 'deleted' and removes the stored object.
    ctx.db.artifact.artifactId.update({ ...art, status: 'deleted', statusReason: 'deleted by operator' });
  }
);

// Gateway-only ---------------------------------------------------------------

export const issueGrant = spacetimedb.reducer(
  { grantId: t.string(), url: t.string(), method: t.string(), ttlMs: t.u32() },
  (ctx, { grantId, url, method, ttlMs }) => {
    requireService(ctx);
    const g = ctx.db.transferGrant.grantId.find(grantId) ?? fail('unknown grant');
    if (g.status !== 'requested') fail(`grant is ${g.status}`);
    ctx.db.transferGrant.grantId.update({
      ...g,
      status: 'issued',
      url,
      method,
      expiresAt: after(ctx, Math.min(ttlMs, MAX_GRANT_TTL_MS)),
    });
  }
);

export const denyGrant = spacetimedb.reducer(
  { grantId: t.string(), reason: t.string() },
  (ctx, { grantId, reason }) => {
    requireService(ctx);
    const g = ctx.db.transferGrant.grantId.find(grantId) ?? fail('unknown grant');
    if (g.status !== 'requested') return;
    ctx.db.transferGrant.grantId.update({ ...g, status: 'denied', reason });
  }
);

/** Gateway reports the result of checking a stored object. */
export const confirmArtifact = spacetimedb.reducer(
  {
    artifactId: t.string(),
    ok: t.bool(),
    verifiedBytes: t.option(t.u64()),
    reason: t.option(t.string()),
  },
  (ctx, { artifactId, ok, verifiedBytes, reason }) => {
    requireService(ctx);
    const art = ctx.db.artifact.artifactId.find(artifactId) ?? fail('unknown artifact');
    if (art.status === 'available' && ok) return;
    if (art.status !== 'verifying' && art.status !== 'pending_upload') fail(`artifact is ${art.status}`);
    ctx.db.artifact.artifactId.update({
      ...art,
      status: ok ? 'available' : 'failed',
      statusReason: reason,
      verifiedBytes,
      availableAt: ok ? ctx.timestamp : undefined,
    });
  }
);

// ---------------------------------------------------------------------------
// Provider credentials (voice session, TURN)
// ---------------------------------------------------------------------------

const SERVICE_GRANT_ROLES: Record<string, readonly Role[]> = {
  voice: ['coach', 'headset', 'operator'],
  ice: ROLES,
};

export const requestServiceGrant = spacetimedb.reducer(
  { grantId: t.string(), sessionId: t.string(), kind: t.string() },
  (ctx, { grantId, sessionId, kind }) => {
    checkId(grantId, 'grantId');
    const allowed = SERVICE_GRANT_ROLES[kind] ?? fail(`unsupported grant kind: ${kind}`);
    requireRole(ctx, sessionId, allowed);
    activeSession(ctx, sessionId);
    if (ctx.db.serviceGrant.grantId.find(grantId)) fail('grantId already used');
    ctx.db.serviceGrant.insert({
      grantId,
      sessionId,
      kind,
      requestedBy: ctx.sender,
      requestedAt: ctx.timestamp,
      status: 'requested',
      payload: undefined,
      reason: undefined,
      expiresAt: undefined,
    });
  }
);

export const issueServiceGrant = spacetimedb.reducer(
  { grantId: t.string(), payload: t.string(), ttlMs: t.u32() },
  (ctx, { grantId, payload, ttlMs }) => {
    requireService(ctx);
    const g = ctx.db.serviceGrant.grantId.find(grantId) ?? fail('unknown grant');
    if (g.status !== 'requested') fail(`grant is ${g.status}`);
    checkText(payload, 'payload', MAX_SIGNAL_PAYLOAD);
    ctx.db.serviceGrant.grantId.update({
      ...g,
      status: 'issued',
      payload,
      expiresAt: after(ctx, Math.min(ttlMs, MAX_GRANT_TTL_MS)),
    });
  }
);

export const denyServiceGrant = spacetimedb.reducer(
  { grantId: t.string(), reason: t.string() },
  (ctx, { grantId, reason }) => {
    requireService(ctx);
    const g = ctx.db.serviceGrant.grantId.find(grantId) ?? fail('unknown grant');
    if (g.status !== 'requested') return;
    ctx.db.serviceGrant.grantId.update({ ...g, status: 'denied', reason });
  }
);

// ---------------------------------------------------------------------------
// Motion jobs
// ---------------------------------------------------------------------------

/**
 * Request processing for an available clip. Deduplicated on
 * attempt + input artifact + config version: a repeat request is a no-op.
 */
export const requestMotionJob = spacetimedb.reducer(
  {
    jobId: t.string(),
    inputArtifactId: t.string(),
    extraArtifactIds: t.array(t.string()),
    configVersion: t.string(),
  },
  (ctx, a) => {
    checkId(a.jobId, 'jobId');
    checkText(a.configVersion, 'configVersion', 60);
    const input = ctx.db.artifact.artifactId.find(a.inputArtifactId) ?? fail('unknown input artifact');
    requireRole(ctx, input.sessionId, ['operator', 'headset']);
    activeSession(ctx, input.sessionId);
    if (input.status !== 'available') fail(`input artifact is ${input.status}`);
    if (input.kind !== 'raw_clip') fail('input artifact must be a raw_clip');
    if (a.extraArtifactIds.length > 8) fail('too many extra artifacts');
    for (const id of a.extraArtifactIds) {
      const extra = ctx.db.artifact.artifactId.find(id) ?? fail(`unknown artifact ${id}`);
      if (extra.sessionId !== input.sessionId) fail('extra artifact belongs to another session');
      if (extra.status !== 'available') fail(`artifact ${id} is ${extra.status}`);
    }
    const dedupeKey = `${input.attemptId}|${input.artifactId}|${a.configVersion}`;
    if (ctx.db.motionJob.dedupeKey.find(dedupeKey)) return;
    if (ctx.db.motionJob.jobId.find(a.jobId)) fail('jobId already used');
    ctx.db.motionJob.insert({
      jobId: a.jobId,
      dedupeKey,
      sessionId: input.sessionId,
      attemptId: input.attemptId,
      inputArtifactId: input.artifactId,
      extraArtifactIds: a.extraArtifactIds,
      configVersion: a.configVersion,
      status: 'queued',
      run: 0,
      maxRuns: DEFAULT_MAX_RUNS,
      workerId: undefined,
      leaseExpiresAt: undefined,
      progress: undefined,
      stage: undefined,
      outputArtifactIds: [],
      quality: undefined,
      error: undefined,
      requestedBy: ctx.sender,
      createdAt: ctx.timestamp,
      updatedAt: ctx.timestamp,
    });
    emit(ctx, input.sessionId, input.attemptId, 'motion_job_queued', `Motion processing queued (${a.configVersion})`);
  }
);

export const retryMotionJob = spacetimedb.reducer(
  { jobId: t.string() },
  (ctx, { jobId }) => {
    const job = ctx.db.motionJob.jobId.find(jobId) ?? fail('unknown job');
    requireRole(ctx, job.sessionId, ['operator']);
    if (job.status !== 'failed' && job.status !== 'cancelled') fail(`job is ${job.status}`);
    ctx.db.motionJob.jobId.update({
      ...job,
      status: 'queued',
      maxRuns: job.run + DEFAULT_MAX_RUNS,
      workerId: undefined,
      leaseExpiresAt: undefined,
      progress: undefined,
      stage: undefined,
      error: undefined,
      updatedAt: ctx.timestamp,
    });
  }
);

export const cancelMotionJob = spacetimedb.reducer(
  { jobId: t.string() },
  (ctx, { jobId }) => {
    const job = ctx.db.motionJob.jobId.find(jobId) ?? fail('unknown job');
    requireRole(ctx, job.sessionId, ['operator']);
    if (job.status !== 'queued' && job.status !== 'running') fail(`job is ${job.status}`);
    ctx.db.motionJob.jobId.update({
      ...job,
      status: 'cancelled',
      workerId: undefined,
      leaseExpiresAt: undefined,
      error: 'cancelled by operator',
      updatedAt: ctx.timestamp,
    });
  }
);

// Gateway-only ---------------------------------------------------------------

/**
 * Claim a queued job for a worker. `expectedRun` must equal the job's current
 * run counter, so two concurrent claims cannot both succeed. On success the
 * job's run becomes `expectedRun + 1`.
 */
export const claimMotionJob = spacetimedb.reducer(
  { jobId: t.string(), expectedRun: t.u32(), workerId: t.string(), leaseMs: t.u32() },
  (ctx, { jobId, expectedRun, workerId, leaseMs }) => {
    requireService(ctx);
    checkText(workerId, 'workerId', 120);
    const job = ctx.db.motionJob.jobId.find(jobId) ?? fail('unknown job');
    if (job.status !== 'queued') fail(`job is ${job.status}`);
    if (job.run !== expectedRun) fail(`stale claim: run is ${job.run}`);
    if (job.run >= job.maxRuns) fail('no runs left');
    const lease = Math.min(leaseMs || DEFAULT_LEASE_MS, MAX_LEASE_MS);
    ctx.db.motionJob.jobId.update({
      ...job,
      status: 'running',
      run: job.run + 1,
      workerId,
      leaseExpiresAt: after(ctx, lease),
      progress: 0,
      stage: 'claimed',
      error: undefined,
      updatedAt: ctx.timestamp,
    });
  }
);

function requireActiveRun(ctx: Ctx, jobId: string, run: number) {
  const job = ctx.db.motionJob.jobId.find(jobId) ?? fail('unknown job');
  if (job.status !== 'running' || job.run !== run) {
    fail(`stale run ${run}: job is ${job.status} on run ${job.run}`);
  }
  return job;
}

export const heartbeatMotionJob = spacetimedb.reducer(
  {
    jobId: t.string(),
    run: t.u32(),
    progress: t.option(t.f64()),
    stage: t.option(t.string()),
    leaseMs: t.u32(),
  },
  (ctx, { jobId, run, progress, stage, leaseMs }) => {
    requireService(ctx);
    const job = requireActiveRun(ctx, jobId, run);
    if (stage) checkText(stage, 'stage', 120);
    ctx.db.motionJob.jobId.update({
      ...job,
      progress: progress ?? job.progress,
      stage: stage ?? job.stage,
      leaseExpiresAt: after(ctx, Math.min(leaseMs || DEFAULT_LEASE_MS, MAX_LEASE_MS)),
      updatedAt: ctx.timestamp,
    });
  }
);

/** Reserve an output artifact for the active run before the worker uploads it. */
export const registerJobOutput = spacetimedb.reducer(
  {
    jobId: t.string(),
    run: t.u32(),
    artifactId: t.string(),
    kind: t.string(),
    filename: t.string(),
    contentType: t.string(),
  },
  (ctx, a) => {
    requireService(ctx);
    const job = requireActiveRun(ctx, a.jobId, a.run);
    checkId(a.artifactId, 'artifactId');
    oneOf(a.kind, ARTIFACT_KINDS, 'kind');
    checkText(a.filename, 'filename', 200);
    checkText(a.contentType, 'contentType', 120);
    if (ctx.db.artifact.artifactId.find(a.artifactId)) fail('artifactId already used');
    ctx.db.artifact.insert({
      artifactId: a.artifactId,
      sessionId: job.sessionId,
      attemptId: job.attemptId,
      kind: a.kind,
      // Run-specific keys: a superseded run can never overwrite current output.
      storageKey: `sessions/${job.sessionId}/${job.attemptId}/jobs/${job.jobId}/run-${a.run}/${a.artifactId}/${safeFilename(a.filename)}`,
      filename: a.filename,
      contentType: a.contentType,
      declaredBytes: undefined,
      verifiedBytes: undefined,
      sha256: undefined,
      status: 'pending_upload',
      statusReason: undefined,
      jobId: job.jobId,
      jobRun: a.run,
      createdBy: ctx.sender,
      createdAt: ctx.timestamp,
      availableAt: undefined,
    });
  }
);

const VerifiedOutput = t.object('VerifiedOutput', {
  artifactId: t.string(),
  verifiedBytes: t.u64(),
});

/**
 * Complete the active run. The gateway verifies every output exists in
 * storage first. Rejected if the run is no longer current (lease expired and
 * reclaimed, cancelled, or already finished).
 */
export const completeMotionJob = spacetimedb.reducer(
  {
    jobId: t.string(),
    run: t.u32(),
    outputs: t.array(VerifiedOutput),
    quality: QualitySummary,
  },
  (ctx, { jobId, run, outputs, quality }) => {
    requireService(ctx);
    const job = requireActiveRun(ctx, jobId, run);
    if (outputs.length === 0) fail('a completed job needs at least one output');
    for (const out of outputs) {
      const art = ctx.db.artifact.artifactId.find(out.artifactId) ?? fail(`unknown output ${out.artifactId}`);
      if (art.jobId !== jobId || art.jobRun !== run) fail(`output ${out.artifactId} is not from this run`);
      ctx.db.artifact.artifactId.update({
        ...art,
        status: 'available',
        verifiedBytes: out.verifiedBytes,
        availableAt: ctx.timestamp,
      });
    }
    ctx.db.motionJob.jobId.update({
      ...job,
      status: 'ready',
      progress: 1,
      stage: 'complete',
      leaseExpiresAt: undefined,
      outputArtifactIds: outputs.map(o => o.artifactId),
      quality,
      error: undefined,
      updatedAt: ctx.timestamp,
    });
    emit(ctx, job.sessionId, job.attemptId, 'motion_job_ready', `Robot replay ready (run ${run})`);
  }
);

export const failMotionJob = spacetimedb.reducer(
  { jobId: t.string(), run: t.u32(), error: t.string(), retryable: t.bool() },
  (ctx, { jobId, run, error, retryable }) => {
    requireService(ctx);
    const job = requireActiveRun(ctx, jobId, run);
    checkText(error, 'error');
    const requeue = retryable && job.run < job.maxRuns;
    ctx.db.motionJob.jobId.update({
      ...job,
      status: requeue ? 'queued' : 'failed',
      workerId: undefined,
      leaseExpiresAt: undefined,
      error,
      updatedAt: ctx.timestamp,
    });
    // Outputs of a failed run never become available.
    for (const art of [...ctx.db.artifact.sessionId.filter(job.sessionId)]) {
      if (art.jobId === jobId && art.jobRun === run && art.status === 'pending_upload') {
        ctx.db.artifact.artifactId.update({ ...art, status: 'failed', statusReason: 'run failed' });
      }
    }
    if (!requeue) {
      emit(ctx, job.sessionId, job.attemptId, 'motion_job_failed', `Motion processing failed: ${error}`);
    }
  }
);

// ---------------------------------------------------------------------------
// Replay transport
// ---------------------------------------------------------------------------

export const setReplayState = spacetimedb.reducer(
  {
    sessionId: t.string(),
    artifactId: t.option(t.string()),
    playing: t.bool(),
    positionMs: t.f64(),
    rate: t.f64(),
  },
  (ctx, a) => {
    requireRole(ctx, a.sessionId, ['operator', 'headset']);
    if (a.artifactId) {
      const art = ctx.db.artifact.artifactId.find(a.artifactId) ?? fail('unknown artifact');
      if (art.sessionId !== a.sessionId) fail('artifact belongs to another session');
      if (art.status !== 'available') fail(`artifact is ${art.status}`);
    }
    if (!(a.rate > 0 && a.rate <= 4)) fail('rate must be in (0, 4]');
    if (!(a.positionMs >= 0)) fail('positionMs must be >= 0');
    const cur = ctx.db.replayState.sessionId.find(a.sessionId) ?? fail('no replay state');
    ctx.db.replayState.sessionId.update({
      ...cur,
      artifactId: a.artifactId,
      playing: a.playing,
      positionMs: a.positionMs,
      rate: a.rate,
      updatedAt: ctx.timestamp,
      updatedBy: ctx.sender,
    });
  }
);

// ---------------------------------------------------------------------------
// Live media signaling
// ---------------------------------------------------------------------------

export const setMediaSource = spacetimedb.reducer(
  {
    sessionId: t.string(),
    status: t.string(),
    label: t.option(t.string()),
    detail: t.option(t.string()),
    width: t.option(t.u32()),
    height: t.option(t.u32()),
  },
  (ctx, a) => {
    requireRole(ctx, a.sessionId, ['operator']);
    activeSession(ctx, a.sessionId);
    oneOf(a.status, MEDIA_STATUS, 'status');
    if (a.label) checkText(a.label, 'label', 200);
    if (a.detail) checkText(a.detail, 'detail', 500);
    const cur = ctx.db.mediaSource.sessionId.find(a.sessionId) ?? fail('no media source');
    if (
      cur.publisher &&
      !cur.publisher.isEqual(ctx.sender) &&
      (cur.status === 'live' || cur.status === 'starting')
    ) {
      fail('another operator is publishing');
    }
    ctx.db.mediaSource.sessionId.update({
      ...cur,
      publisher: ctx.sender,
      status: a.status,
      label: a.label,
      detail: a.detail,
      width: a.width,
      height: a.height,
      updatedAt: ctx.timestamp,
    });
  }
);

export const sendSignal = spacetimedb.reducer(
  {
    sessionId: t.string(),
    to: t.identity(),
    peerId: t.string(),
    kind: t.string(),
    payload: t.string(),
  },
  (ctx, a) => {
    requireMember(ctx, a.sessionId);
    activeSession(ctx, a.sessionId);
    oneOf(a.kind, SIGNAL_KINDS, 'kind');
    checkId(a.peerId, 'peerId');
    checkText(a.payload, 'payload', MAX_SIGNAL_PAYLOAD);
    if (rolesOf(ctx, a.sessionId, a.to).size === 0) fail('recipient is not in this session');
    ctx.db.rtcSignal.insert({
      signalId: 0n,
      sessionId: a.sessionId,
      from: ctx.sender,
      to: a.to,
      peerId: a.peerId,
      kind: a.kind,
      payload: a.payload,
      createdAt: ctx.timestamp,
    });
  }
);

export const ackSignals = spacetimedb.reducer(
  { signalIds: t.array(t.u64()) },
  (ctx, { signalIds }) => {
    for (const id of signalIds) {
      const s = ctx.db.rtcSignal.signalId.find(id);
      if (s && s.to.isEqual(ctx.sender)) ctx.db.rtcSignal.signalId.delete(id);
    }
  }
);

// ---------------------------------------------------------------------------
// Views: the only read path for clients
// ---------------------------------------------------------------------------

type ViewDb = Ctx['db'];

function viewerSessions(db: ViewDb, sender: Identity): Set<string> {
  const ids = new Set<string>();
  if (db.serviceIdentity.identity.find(sender)) {
    for (const s of db.session.iter()) ids.add(s.sessionId);
    return ids;
  }
  for (const m of db.membership.identity.filter(sender)) ids.add(m.sessionId);
  return ids;
}

function operatorSessions(db: ViewDb, sender: Identity): Set<string> {
  const ids = new Set<string>();
  if (db.serviceIdentity.identity.find(sender)) {
    for (const s of db.session.iter()) ids.add(s.sessionId);
    return ids;
  }
  for (const m of db.membership.identity.filter(sender)) {
    if (m.role === 'operator') ids.add(m.sessionId);
  }
  return ids;
}

export const mySessions = spacetimedb.view(
  { name: 'my_sessions', public: true },
  t.array(session.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => {
    const s = ctx.db.session.sessionId.find(id);
    return s ? [s] : [];
  })
);

export const myMemberships = spacetimedb.view(
  { name: 'my_memberships', public: true },
  t.array(membership.rowType),
  ctx => [...ctx.db.membership.identity.filter(ctx.sender)]
);

export const amService = spacetimedb.view(
  { name: 'am_service', public: true },
  t.array(t.row('ServiceFlag', { identity: t.identity().primaryKey(), label: t.string() })),
  ctx => {
    const row = ctx.db.serviceIdentity.identity.find(ctx.sender);
    return row ? [{ identity: row.identity, label: row.label }] : [];
  }
);

const SessionMember = t.row('SessionMember', {
  membershipId: t.u64().primaryKey(),
  sessionId: t.string(),
  identity: t.identity(),
  role: t.string(),
  displayName: t.string(),
  joinedAt: t.timestamp(),
  online: t.bool(),
});

export const sessionMembers = spacetimedb.view(
  { name: 'session_members', public: true },
  t.array(SessionMember),
  ctx => {
    const out = [];
    for (const id of viewerSessions(ctx.db as ViewDb, ctx.sender)) {
      for (const m of ctx.db.membership.sessionId.filter(id)) {
        let online = false;
        for (const _ of ctx.db.connection.identity.filter(m.identity)) {
          online = true;
          break;
        }
        out.push({ ...m, online });
      }
    }
    return out;
  }
);

export const sessionInvites = spacetimedb.view(
  { name: 'session_invites', public: true },
  t.array(sessionInvite.rowType),
  ctx => {
    const out = [];
    for (const id of operatorSessions(ctx.db as ViewDb, ctx.sender)) {
      for (const inv of ctx.db.sessionInvite.sessionId.filter(id)) {
        if (!inv.revoked) out.push(inv);
      }
    }
    return out;
  }
);

export const sessionAttempts = spacetimedb.view(
  { name: 'session_attempts', public: true },
  t.array(attempt.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => [
    ...ctx.db.attempt.sessionId.filter(id),
  ])
);

export const sessionExerciseState = spacetimedb.view(
  { name: 'session_exercise_state', public: true },
  t.array(exerciseState.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => {
    const s = ctx.db.exerciseState.sessionId.find(id);
    return s ? [s] : [];
  })
);

export const sessionEvents = spacetimedb.view(
  { name: 'session_events', public: true },
  t.array(exerciseEvent.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => [
    ...ctx.db.exerciseEvent.sessionId.filter(id),
  ])
);

export const sessionCoachMessages = spacetimedb.view(
  { name: 'session_coach_messages', public: true },
  t.array(coachMessage.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => [
    ...ctx.db.coachMessage.sessionId.filter(id),
  ])
);

export const sessionCoachStatus = spacetimedb.view(
  { name: 'session_coach_status', public: true },
  t.array(coachStatus.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => {
    const s = ctx.db.coachStatus.sessionId.find(id);
    return s ? [s] : [];
  })
);

export const sessionCommands = spacetimedb.view(
  { name: 'session_commands', public: true },
  t.array(command.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => [
    ...ctx.db.command.sessionId.filter(id),
  ])
);

export const sessionArtifacts = spacetimedb.view(
  { name: 'session_artifacts', public: true },
  t.array(artifact.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => [
    ...ctx.db.artifact.sessionId.filter(id),
  ])
);

export const sessionMotionJobs = spacetimedb.view(
  { name: 'session_motion_jobs', public: true },
  t.array(motionJob.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => [
    ...ctx.db.motionJob.sessionId.filter(id),
  ])
);

export const sessionReplayState = spacetimedb.view(
  { name: 'session_replay_state', public: true },
  t.array(replayState.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => {
    const s = ctx.db.replayState.sessionId.find(id);
    return s ? [s] : [];
  })
);

export const sessionMediaSource = spacetimedb.view(
  { name: 'session_media_source', public: true },
  t.array(mediaSource.rowType),
  ctx => [...viewerSessions(ctx.db as ViewDb, ctx.sender)].flatMap(id => {
    const s = ctx.db.mediaSource.sessionId.find(id);
    return s ? [s] : [];
  })
);

/** Grants the caller requested; service identities see every open request. */
export const myTransferGrants = spacetimedb.view(
  { name: 'my_transfer_grants', public: true },
  t.array(transferGrant.rowType),
  ctx => {
    if (ctx.db.serviceIdentity.identity.find(ctx.sender)) {
      return [...ctx.db.transferGrant.iter()].filter(g => g.status === 'requested');
    }
    return [...ctx.db.transferGrant.requestedBy.filter(ctx.sender)];
  }
);

export const myServiceGrants = spacetimedb.view(
  { name: 'my_service_grants', public: true },
  t.array(serviceGrant.rowType),
  ctx => {
    if (ctx.db.serviceIdentity.identity.find(ctx.sender)) {
      return [...ctx.db.serviceGrant.iter()].filter(g => g.status === 'requested');
    }
    return [...ctx.db.serviceGrant.requestedBy.filter(ctx.sender)];
  }
);

export const myRtcSignals = spacetimedb.view(
  { name: 'my_rtc_signals', public: true },
  t.array(rtcSignal.rowType),
  ctx => [...ctx.db.rtcSignal.to.filter(ctx.sender)]
);
