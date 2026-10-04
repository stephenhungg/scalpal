// Scalpal shared session state.
//
// Every table is private. Clients read through the per-identity views in
// `views.ts`, which only return rows for sessions the caller belongs to.
// Service identities (the gateway) are registered in `service_identity` and
// can see and act on every session.

import { schema, table, t } from 'spacetimedb/server';

// ---------------------------------------------------------------------------
// Identity and membership
// ---------------------------------------------------------------------------

/** Identities trusted to act as the gateway / worker bridge. */
export const serviceIdentity = table(
  { name: 'service_identity' },
  {
    identity: t.identity().primaryKey(),
    label: t.string(),
    addedAt: t.timestamp(),
  }
);

/** One row per live client connection, used for presence. */
export const connection = table(
  { name: 'connection' },
  {
    connectionId: t.connectionId().primaryKey(),
    identity: t.identity().index('btree'),
    connectedAt: t.timestamp(),
  }
);

export const session = table(
  { name: 'session' },
  {
    sessionId: t.string().primaryKey(),
    label: t.string(),
    createdBy: t.identity(),
    createdAt: t.timestamp(),
    /** 'active' | 'ended' */
    status: t.string(),
    exerciseId: t.string(),
    exerciseVersion: t.string(),
    currentAttemptId: t.string(),
    attemptCount: t.u32(),
    endedAt: t.option(t.timestamp()),
  }
);

/** Join codes. Never exposed except to the session's operators. */
export const sessionInvite = table(
  { name: 'session_invite' },
  {
    code: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    /** Role granted by this code. */
    role: t.string(),
    createdAt: t.timestamp(),
    revoked: t.bool(),
  }
);

export const membership = table(
  {
    name: 'membership',
    indexes: [
      {
        accessor: 'by_session_identity',
        algorithm: 'btree',
        columns: ['sessionId', 'identity'],
      },
    ],
  },
  {
    membershipId: t.u64().primaryKey().autoInc(),
    sessionId: t.string().index('btree'),
    identity: t.identity().index('btree'),
    /** 'operator' | 'headset' | 'coach' | 'viewer' */
    role: t.string(),
    displayName: t.string(),
    joinedAt: t.timestamp(),
  }
);

// ---------------------------------------------------------------------------
// Exercise state, events, coach transcript
// ---------------------------------------------------------------------------

/**
 * One row per attempt. A retry creates a new attempt; old attempts keep their
 * learning result and motion jobs.
 */
export const attempt = table(
  { name: 'attempt' },
  {
    attemptId: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    ordinal: t.u32(),
    exerciseId: t.string(),
    exerciseVersion: t.string(),
    startedAt: t.timestamp(),
    endedAt: t.option(t.timestamp()),
    /** 'not_started' | 'in_progress' | 'completed' | 'abandoned' */
    practiceStatus: t.string(),
    /** Authored learning result summary, set by the headset at review. */
    resultSummary: t.option(t.string()),
    stepsCompleted: t.u32(),
    stepsTotal: t.u32(),
    mistakes: t.u32(),
    hintsUsed: t.u32(),
  }
);

/**
 * The confirmed, headset-published exercise state for the session's current
 * attempt. Only the headset role writes it. `stateVersion` bumps on every
 * write; `stepVersion` bumps only when the attempt, mode or step changes and is
 * what voice commands are validated against.
 */
export const exerciseState = table(
  { name: 'exercise_state' },
  {
    sessionId: t.string().primaryKey(),
    attemptId: t.string(),
    exerciseId: t.string(),
    exerciseVersion: t.string(),
    mode: t.string(),
    stepId: t.option(t.string()),
    stepIndex: t.u32(),
    stepCount: t.u32(),
    selectedStructureId: t.option(t.string()),
    highlightedStructureId: t.option(t.string()),
    previewRotating: t.bool(),
    paused: t.bool(),
    /** 'unaligned' | 'valid' | 'uncertain' */
    registration: t.string(),
    registrationReason: t.option(t.string()),
    /** 'off' | 'recording' | 'failed' | 'complete' */
    recording: t.string(),
    stateVersion: t.u64(),
    stepVersion: t.u64(),
    updatedAt: t.timestamp(),
    updatedBy: t.identity(),
  }
);

/** Authored interaction / feedback events emitted by the headset. */
export const exerciseEvent = table(
  { name: 'exercise_event' },
  {
    eventId: t.u64().primaryKey().autoInc(),
    sessionId: t.string().index('btree'),
    attemptId: t.string(),
    /** e.g. 'step_started', 'step_completed', 'mistake', 'hint', 'registration_lost' */
    kind: t.string(),
    stepId: t.option(t.string()),
    structureId: t.option(t.string()),
    message: t.string(),
    /** Headset monotonic clock in ms, if available. Not comparable to `at`. */
    deviceTimeMs: t.option(t.f64()),
    at: t.timestamp(),
  }
);

/** What Scalpal (and the learner) actually said, as emitted by the coach. */
export const coachMessage = table(
  { name: 'coach_message' },
  {
    messageId: t.u64().primaryKey().autoInc(),
    sessionId: t.string().index('btree'),
    attemptId: t.string(),
    /** 'learner' | 'coach' | 'system' */
    speaker: t.string(),
    text: t.string(),
    at: t.timestamp(),
  }
);

/** Coach connection status as reported by the coach client. */
export const coachStatus = table(
  { name: 'coach_status' },
  {
    sessionId: t.string().primaryKey(),
    /** 'offline' | 'connecting' | 'listening' | 'thinking' | 'speaking' | 'error' */
    status: t.string(),
    detail: t.option(t.string()),
    updatedAt: t.timestamp(),
  }
);

// ---------------------------------------------------------------------------
// Voice action commands
// ---------------------------------------------------------------------------

/**
 * A requested app action. The coach/operator inserts it as 'pending'; the
 * headset validates and resolves it. Scalpal should only announce success after
 * the status becomes 'applied'.
 */
export const command = table(
  { name: 'command' },
  {
    commandId: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    attemptId: t.string(),
    action: t.string(),
    targetId: t.option(t.string()),
    argBool: t.option(t.bool()),
    argNumber: t.option(t.f64()),
    expectedStepVersion: t.u64(),
    requestedBy: t.identity(),
    requestedRole: t.string(),
    requestedAt: t.timestamp(),
    /** 'pending' | 'applied' | 'rejected' | 'unavailable' | 'failed' | 'expired' */
    status: t.string(),
    reason: t.option(t.string()),
    resolvedAt: t.option(t.timestamp()),
    resolvedStateVersion: t.option(t.u64()),
  }
);

// ---------------------------------------------------------------------------
// Artifacts, transfer grants, motion jobs, replay
// ---------------------------------------------------------------------------

export const artifact = table(
  { name: 'artifact' },
  {
    artifactId: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    attemptId: t.string(),
    /**
     * 'raw_clip' | 'capture_manifest' | 'scene_timeline' | 'hand_estimates'
     * | 'robot_trajectory' | 'replay_video' | 'quality_report' | 'other'
     */
    kind: t.string(),
    storageKey: t.string(),
    filename: t.string(),
    contentType: t.string(),
    /** Size declared by the uploader; verified by the gateway. */
    declaredBytes: t.option(t.u64()),
    verifiedBytes: t.option(t.u64()),
    sha256: t.option(t.string()),
    /** 'pending_upload' | 'verifying' | 'available' | 'failed' | 'deleted' */
    status: t.string(),
    statusReason: t.option(t.string()),
    /** Set when produced by a motion job run. */
    jobId: t.option(t.string()),
    jobRun: t.option(t.u32()),
    createdBy: t.identity(),
    createdAt: t.timestamp(),
    availableAt: t.option(t.timestamp()),
  }
);

/**
 * A request for a short-lived storage URL. The requester inserts it, the
 * gateway fills in `url`. Only the requester and service identities can see
 * it.
 */
export const transferGrant = table(
  { name: 'transfer_grant' },
  {
    grantId: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    artifactId: t.string(),
    /** 'upload' | 'download' */
    direction: t.string(),
    requestedBy: t.identity().index('btree'),
    requestedAt: t.timestamp(),
    /** 'requested' | 'issued' | 'denied' | 'expired' */
    status: t.string(),
    url: t.option(t.string()),
    /** HTTP method the URL must be used with. */
    method: t.option(t.string()),
    reason: t.option(t.string()),
    expiresAt: t.option(t.timestamp()),
  }
);

/**
 * A request for a provider credential minted by the gateway:
 * - 'voice': scoped voice-agent session (e.g. an ElevenLabs signed URL)
 * - 'ice':   WebRTC ICE server list including short-lived TURN credentials
 * `payload` is the provider's JSON response, visible only to the requester.
 */
export const serviceGrant = table(
  { name: 'service_grant' },
  {
    grantId: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    kind: t.string(),
    requestedBy: t.identity().index('btree'),
    requestedAt: t.timestamp(),
    /** 'requested' | 'issued' | 'denied' | 'expired' */
    status: t.string(),
    payload: t.option(t.string()),
    reason: t.option(t.string()),
    expiresAt: t.option(t.timestamp()),
  }
);

export const QualitySummary = t.object('QualitySummary', {
  framesTotal: t.u32(),
  framesValid: t.u32(),
  invalidIntervals: t.u32(),
  robotModel: t.string(),
  /** 'kinematic' | 'physics' */
  replayKind: t.string(),
  notes: t.string(),
});

export const motionJob = table(
  { name: 'motion_job' },
  {
    jobId: t.string().primaryKey(),
    /** attemptId + inputArtifactId + configVersion */
    dedupeKey: t.string().unique(),
    sessionId: t.string().index('btree'),
    attemptId: t.string(),
    inputArtifactId: t.string(),
    /** Optional companion inputs, e.g. capture manifest / scene timeline. */
    extraArtifactIds: t.array(t.string()),
    configVersion: t.string(),
    /** 'queued' | 'running' | 'ready' | 'failed' | 'cancelled' */
    status: t.string(),
    /** Incremented on every claim. Completion must name the current run. */
    run: t.u32(),
    maxRuns: t.u32(),
    workerId: t.option(t.string()),
    leaseExpiresAt: t.option(t.timestamp()),
    progress: t.option(t.f64()),
    stage: t.option(t.string()),
    outputArtifactIds: t.array(t.string()),
    quality: t.option(QualitySummary),
    error: t.option(t.string()),
    requestedBy: t.identity(),
    createdAt: t.timestamp(),
    updatedAt: t.timestamp(),
  }
);

/** Shared replay transport so the headset and viewers can watch together. */
export const replayState = table(
  { name: 'replay_state' },
  {
    sessionId: t.string().primaryKey(),
    artifactId: t.option(t.string()),
    playing: t.bool(),
    /** Playback position at `updatedAt`. Clients extrapolate while playing. */
    positionMs: t.f64(),
    rate: t.f64(),
    updatedAt: t.timestamp(),
    updatedBy: t.identity(),
  }
);

// ---------------------------------------------------------------------------
// Live media (WebRTC signaling only; frames never enter the database)
// ---------------------------------------------------------------------------

export const mediaSource = table(
  { name: 'media_source' },
  {
    sessionId: t.string().primaryKey(),
    publisher: t.option(t.identity()),
    /** 'off' | 'starting' | 'live' | 'stopped' | 'denied' | 'error' */
    status: t.string(),
    label: t.option(t.string()),
    detail: t.option(t.string()),
    width: t.option(t.u32()),
    height: t.option(t.u32()),
    updatedAt: t.timestamp(),
  }
);

export const rtcSignal = table(
  { name: 'rtc_signal' },
  {
    signalId: t.u64().primaryKey().autoInc(),
    sessionId: t.string().index('btree'),
    from: t.identity(),
    to: t.identity().index('btree'),
    /** Distinguishes peer connections between the same pair. */
    peerId: t.string(),
    /** 'join' | 'offer' | 'answer' | 'ice' | 'bye' */
    kind: t.string(),
    payload: t.string(),
    createdAt: t.timestamp(),
  }
);

// ---------------------------------------------------------------------------
// Housekeeping
// ---------------------------------------------------------------------------

export const sweepTimer = table(
  { name: 'sweep_timer' },
  {
    scheduledId: t.u64().primaryKey().autoInc(),
    scheduledAt: t.scheduleAt(),
  }
);

// ---------------------------------------------------------------------------
// Pre-op encounter (Matthew's Scalpal lane): patient interview, case
// presentation to the attending, and the deterministic scorecard.
// ---------------------------------------------------------------------------

/** One pre-op encounter per attempt: who the learner interviewed and how it scored. */
export const encounter = table(
  { name: 'encounter' },
  {
    encounterId: t.string().primaryKey(),
    sessionId: t.string().index('btree'),
    attemptId: t.string(),
    patientId: t.string(),
    patientName: t.string(),
    /** 'patient' | 'parent' */
    speaker: t.string(),
    speakerName: t.string(),
    /** 'interview' | 'attending' | 'scored' */
    phase: t.string(),
    scoreTotal: t.option(t.u32()),
    grade: t.option(t.string()),
    /** Full scorecard as JSON, written once at scoring. */
    scorecardJson: t.option(t.string()),
    startedAt: t.timestamp(),
    updatedAt: t.timestamp(),
  }
);

/** What the learner elicited, examined, ordered, and said, in order. */
export const encounterEvent = table(
  { name: 'encounter_event' },
  {
    eventId: t.u64().primaryKey().autoInc(),
    sessionId: t.string().index('btree'),
    encounterId: t.string(),
    /** 'history' | 'exam' | 'test' | 'assessment' | 'transcript' */
    kind: t.string(),
    /** Topic, maneuver, or test id; '' for transcript lines. */
    itemId: t.string(),
    /** For transcript lines: 'learner' | 'patient' | 'coach'. */
    speaker: t.option(t.string()),
    text: t.string(),
    at: t.timestamp(),
  }
);

// ---------------------------------------------------------------------------
// Operating-room logs (companion dashboard)
// ---------------------------------------------------------------------------

/**
 * One line of the operating-room log, posted by the coach: what the state
 * tracker saw, Scalpal alerts, vitals samples, checklist changes and the case
 * outcome. Capped per session (oldest rows are dropped first).
 */
export const simLog = table(
  { name: 'sim_log' },
  {
    id: t.u64().primaryKey().autoInc(),
    sessionId: t.string().index('btree'),
    /** The coach's own session id (services/preop), not the SpacetimeDB session. */
    coachSessionId: t.string(),
    /** 'event' | 'alert' | 'vitals' | 'checklist' | 'outcome' */
    kind: t.string(),
    /** One human-readable line, at most 2000 chars. */
    text: t.string(),
    /** Structured payload as JSON, at most 8000 chars; '' when absent. */
    dataJson: t.string(),
    at: t.timestamp(),
  }
);

const spacetimedb = schema({
  serviceIdentity,
  connection,
  session,
  sessionInvite,
  membership,
  attempt,
  exerciseState,
  exerciseEvent,
  coachMessage,
  coachStatus,
  command,
  artifact,
  transferGrant,
  serviceGrant,
  motionJob,
  replayState,
  mediaSource,
  rtcSignal,
  sweepTimer,
  encounter,
  encounterEvent,
  simLog,
});

export default spacetimedb;
