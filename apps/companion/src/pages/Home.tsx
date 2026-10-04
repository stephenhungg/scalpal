import { useEffect, useState } from 'react';
import { Panel, StatusPill, useAction, Empty } from '../components/ui';
import { useLive } from '../data/live';
import { NAME_KEY } from '../config';
import { ago } from '../lib/format';
import { sessionId as newSessionId } from '../lib/ids';
import { Link, navigate } from '../lib/router';
import { load, save } from '../lib/storage';
import { joinWithCode } from '../lib/grants';

export default function Home() {
  const live = useLive();
  const [name, setName] = useState(load(NAME_KEY) ?? '');
  const [label, setLabel] = useState('');
  const [exerciseId, setExerciseId] = useState('lap_appendectomy');
  const [exerciseVersion, setExerciseVersion] = useState('0.1.0');
  const [code, setCode] = useState('');
  const create = useAction();
  const join = useAction();

  useEffect(() => save(NAME_KEY, name), [name]);

  const mine = [...live.sessions].sort(
    (a, b) => Number(b.createdAt.microsSinceUnixEpoch - a.createdAt.microsSinceUnixEpoch)
  );

  const onCreate = () =>
    create.run(async () => {
      const conn = live.conn;
      if (!conn) throw new Error('Not connected to the session database yet.');
      const id = newSessionId();
      await conn.reducers.createSession({
        sessionId: id,
        label: label.trim() || 'Scalpal session',
        exerciseId: exerciseId.trim(),
        exerciseVersion: exerciseVersion.trim(),
        displayName: name.trim() || 'Operator',
      });
      navigate(`/s/${id}`);
    });

  const onJoin = () =>
    join.run(async () => {
      const conn = live.conn;
      if (!conn) throw new Error('Not connected to the session database yet.');
      const sid = await joinWithCode(conn, code, name.trim());
      navigate(`/s/${sid}`);
    });

  const roleOf = (sid: string) =>
    live.memberships
      .filter(m => m.sessionId === sid)
      .map(m => m.role)
      .join(', ');

  return (
    <main className="page page-narrow">
      <section className="hero">
        <h1>Watch a Scalpal session live</h1>
        <p>
          Follow a guided mixed-reality practice session as it happens: the wearer's view with virtual anatomy, the
          exercise step, what Jarvis says, and the robot-hand replay derived from the recorded movement. Replay is
          retargeted motion, not a learned robot policy.
        </p>
      </section>

      <div className="two">
        <Panel title="Join a session">
          <div className="stack">
            <label className="field">
              Invite code
              <input
                className="code-box"
                value={code}
                onChange={e => setCode(e.target.value.toUpperCase())}
                placeholder="ABC123"
                maxLength={6}
                autoCapitalize="characters"
                spellCheck={false}
              />
            </label>
            <label className="field">
              Your name
              <input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Judge" maxLength={60} />
            </label>
            <div className="row">
              <button className="btn primary" disabled={!live.conn || code.trim().length < 6 || join.busy} onClick={onJoin}>
                {join.busy ? 'Joining…' : 'Join'}
              </button>
              <span className="muted small">The code decides your role: viewer, coach, headset or operator.</span>
            </div>
            {join.error && <div className="error-text">{join.error}</div>}
          </div>
        </Panel>

        <Panel title="Start a session">
          <div className="stack">
            <label className="field">
              Session label
              <input value={label} onChange={e => setLabel(e.target.value)} placeholder="MHacks demo table 12" maxLength={120} />
            </label>
            <div className="two">
              <label className="field">
                Exercise id
                <input value={exerciseId} onChange={e => setExerciseId(e.target.value)} maxLength={120} />
              </label>
              <label className="field">
                Exercise version
                <input value={exerciseVersion} onChange={e => setExerciseVersion(e.target.value)} maxLength={60} />
              </label>
            </div>
            <label className="field">
              Your name
              <input value={name} onChange={e => setName(e.target.value)} placeholder="Operator" maxLength={60} />
            </label>
            <div className="row">
              <button className="btn primary" disabled={!live.conn || !exerciseId.trim() || create.busy} onClick={onCreate}>
                {create.busy ? 'Creating…' : 'Create as operator'}
              </button>
            </div>
            {create.error && <div className="error-text">{create.error}</div>}
          </div>
        </Panel>
      </div>

      <div style={{ height: 16 }} />

      <Panel title="Your sessions" flush>
        {mine.length === 0 ? (
          <Empty>{live.ready ? 'No sessions yet. Create one or join with a code.' : 'Loading…'}</Empty>
        ) : (
          <div className="table-wrap">
            <table className="data">
              <thead>
                <tr>
                  <th>Session</th>
                  <th>Exercise</th>
                  <th>Your role</th>
                  <th>Status</th>
                  <th>Created</th>
                </tr>
              </thead>
              <tbody>
                {mine.map(s => (
                  <tr key={s.sessionId}>
                    <td>
                      <Link to={`/s/${s.sessionId}`}>{s.label}</Link>
                      <div className="muted small mono">{s.sessionId}</div>
                    </td>
                    <td>
                      {s.exerciseId} <span className="muted">@{s.exerciseVersion}</span>
                    </td>
                    <td>{roleOf(s.sessionId) || <span className="muted">service</span>}</td>
                    <td>
                      <StatusPill status={s.status} />
                    </td>
                    <td className="muted">{ago(s.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </main>
  );
}
