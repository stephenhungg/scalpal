import Coach from '../components/Coach';
import Commands from '../components/Commands';
import Events from '../components/Events';
import ExerciseState from '../components/ExerciseState';
import Learning from '../components/Learning';
import LiveView from '../components/LiveView';
import Members from '../components/Members';
import Motion from '../components/Motion';
import Replay from '../components/Replay';
import SimLogs from '../components/SimLogs';
import Vitals from '../components/Vitals';
import { copy, Panel, StatusPill } from '../components/ui';
import { useLive, useSession } from '../data/live';
import { Link } from '../lib/router';

export default function SessionPage({ sessionId }: { sessionId: string }) {
  const live = useLive();
  const data = useSession(sessionId);

  if (!data.session) {
    return (
      <main className="page page-narrow">
        <div style={{ maxWidth: 520, margin: '40px auto' }}>
          <Panel title="Session">
            {!live.ready ? (
              <div className="muted">Loading…</div>
            ) : (
              <div className="stack">
                <div>
                  You are not a member of <span className="mono">{sessionId}</span>, or it does not exist.
                </div>
                <div className="muted small">Ask the operator for an invite link or code.</div>
                <Link to="/" className="btn">
                  Back to start
                </Link>
              </div>
            )}
          </Panel>
        </div>
      </main>
    );
  }

  const s = data.session;
  const roles = [...data.roles];

  return (
    <main className="page">
      <div className="row" style={{ marginBottom: 16 }}>
        <div>
          <h1 style={{ fontSize: 40, marginBottom: 6 }}>{s.label}</h1>
          <div className="row muted small" style={{ gap: 8, marginTop: 2 }}>
            <button className="btn ghost sm mono" style={{ padding: '0 4px' }} onClick={() => copy(s.sessionId, 'Session id copied')}>
              {s.sessionId}
            </button>
            <span>
              {s.exerciseId}@{s.exerciseVersion}
            </span>
            <span>attempt #{data.currentAttempt?.ordinal ?? '—'}</span>
          </div>
        </div>
        <div className="spacer" />
        <Link to={`/s/${sessionId}/recap`} className="btn sm">Run recap</Link>
        <StatusPill status={s.status} label={s.status === 'active' ? 'Session active' : 'Session ended'} />
        {roles.map(r => (
          <span className="chip" key={r}>
            {r}
          </span>
        ))}
      </div>

      <div className="session-grid">
        <div className="col">
          <LiveView data={data} />
          <Replay data={data} />
          <Motion data={data} />
        </div>
        <div className="col">
          <ExerciseState data={data} />
          <Vitals />
          <SimLogs data={data} />
          <Coach data={data} />
          <Learning data={data} />
          <Commands data={data} />
          <Events data={data} />
          <Members data={data} />
        </div>
      </div>
    </main>
  );
}
