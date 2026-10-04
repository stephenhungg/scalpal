// One subscription per view for the whole app, exposed through context.
// Every view is already filtered server-side to the caller's sessions.

import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import type { Identity } from 'spacetimedb';
import { useSpacetimeDB, useTable } from 'spacetimedb/react';
import { tables, type DbConnection } from '../module_bindings';
import { TOKEN_KEY } from '../config';
import { save } from '../lib/storage';
import { toMs } from '../lib/format';

function useLiveTables() {
  const [sessions, sessionsReady] = useTable(tables.mySessions);
  const [memberships] = useTable(tables.myMemberships);
  const [members] = useTable(tables.sessionMembers);
  const [invites] = useTable(tables.sessionInvites);
  const [attempts] = useTable(tables.sessionAttempts);
  const [states] = useTable(tables.sessionExerciseState);
  const [events] = useTable(tables.sessionEvents);
  const [coachMessages] = useTable(tables.sessionCoachMessages);
  const [coachStatus] = useTable(tables.sessionCoachStatus);
  const [commands] = useTable(tables.sessionCommands);
  const [simLogs] = useTable(tables.sessionSimLogs);
  const [robotResults] = useTable(tables.sessionRobotResults);
  const [patientConditions] = useTable(tables.sessionPatientCondition);
  const [artifacts] = useTable(tables.sessionArtifacts);
  const [jobs] = useTable(tables.sessionMotionJobs);
  const [replay] = useTable(tables.sessionReplayState);
  const [media] = useTable(tables.sessionMediaSource);
  const [transferGrants] = useTable(tables.myTransferGrants);
  const [serviceGrants] = useTable(tables.myServiceGrants);
  const [signals] = useTable(tables.myRtcSignals);
  return {
    ready: sessionsReady,
    sessions,
    memberships,
    members,
    invites,
    attempts,
    states,
    events,
    coachMessages,
    coachStatus,
    commands,
    simLogs,
    robotResults,
    patientConditions,
    artifacts,
    jobs,
    replay,
    media,
    transferGrants,
    serviceGrants,
    signals,
  };
}

type LiveTables = ReturnType<typeof useLiveTables>;

type LiveContext = LiveTables & {
  conn: DbConnection | null;
  identity: Identity | undefined;
  connected: boolean;
  connectionError?: Error;
};

const Ctx = createContext<LiveContext | null>(null);

export function LiveDataProvider({ children }: { children: ReactNode }) {
  const state = useSpacetimeDB();
  const data = useLiveTables();
  const conn = state.getConnection() as DbConnection | null;

  useEffect(() => {
    if (state.token) save(TOKEN_KEY, state.token);
  }, [state.token]);

  const value = useMemo<LiveContext>(
    () => ({
      ...data,
      conn: state.isActive ? conn : null,
      identity: state.identity,
      connected: state.isActive,
      connectionError: state.connectionError,
    }),
    [data, conn, state.isActive, state.identity, state.connectionError]
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useLive(): LiveContext {
  const v = useContext(Ctx);
  if (!v) throw new Error('useLive outside LiveDataProvider');
  return v;
}

const byTime = <T,>(get: (x: T) => number) => (a: T, b: T) => get(a) - get(b);

/** Everything the session page needs, filtered to one session. */
export function useSession(sessionId: string) {
  const live = useLive();
  return useMemo(() => {
    const me = live.identity?.toHexString();
    const s = live.sessions.find(x => x.sessionId === sessionId) ?? null;
    const roles = new Set(
      live.memberships.filter(m => m.sessionId === sessionId).map(m => m.role)
    );
    const pick = <T extends { sessionId: string }>(rows: readonly T[]) => rows.filter(r => r.sessionId === sessionId);
    const attempts = pick(live.attempts).sort((a, b) => a.ordinal - b.ordinal);
    return {
      session: s,
      me,
      roles,
      isOperator: roles.has('operator'),
      isHeadset: roles.has('headset'),
      isCoach: roles.has('coach'),
      members: pick(live.members).sort(byTime(m => toMs(m.joinedAt))),
      invites: pick(live.invites),
      attempts,
      currentAttempt: attempts.find(a => a.attemptId === s?.currentAttemptId) ?? null,
      state: live.states.find(x => x.sessionId === sessionId) ?? null,
      events: pick(live.events).sort(byTime(e => toMs(e.at))),
      coachMessages: pick(live.coachMessages).sort(byTime(m => toMs(m.at))),
      coachStatus: live.coachStatus.find(x => x.sessionId === sessionId) ?? null,
      // Operating-room log, newest first.
      simLogs: pick(live.simLogs).sort((a, b) => (a.id < b.id ? 1 : a.id > b.id ? -1 : 0)),
      robotResults: pick(live.robotResults).sort(byTime(r => toMs(r.at))),
      // The simulated patient advanced server-side by the module (patient_condition), when the coach started one.
      patientCondition: live.patientConditions.find(x => x.sessionId === sessionId) ?? null,
      commands: pick(live.commands).sort(byTime(c => toMs(c.requestedAt))),
      artifacts: pick(live.artifacts).sort(byTime(a => toMs(a.createdAt))),
      jobs: pick(live.jobs).sort(byTime(j => toMs(j.createdAt))),
      replay: live.replay.find(x => x.sessionId === sessionId) ?? null,
      media: live.media.find(x => x.sessionId === sessionId) ?? null,
    };
  }, [live, sessionId]);
}

export type SessionData = ReturnType<typeof useSession>;

/** Re-render periodically so relative times stay fresh. */
export function useNow(intervalMs = 1000) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs]);
  return now;
}
