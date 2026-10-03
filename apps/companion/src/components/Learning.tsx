import { useLive, type SessionData } from '../data/live';
import { ago } from '../lib/format';
import { Panel, StatusPill, useAction } from './ui';

/** The learning result, kept separate from motion processing. */
export default function Learning({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const retry = useAction();
  const a = data.currentAttempt;
  const past = data.attempts.filter(x => x.attemptId !== a?.attemptId).reverse();

  return (
    <Panel
      title="Learning result"
      actions={
        data.isOperator && data.session?.status === 'active' ? (
          <button
            className="btn sm"
            disabled={retry.busy}
            onClick={() =>
              retry.run(async () => {
                if (!conn || !data.session) return;
                if (!confirm('Start a new attempt? The current attempt keeps its results.')) return;
                await conn.reducers.startAttempt({ sessionId: data.session.sessionId, exerciseId: '', exerciseVersion: '' });
              })
            }
          >
            New attempt
          </button>
        ) : null
      }
    >
      {a ? (
        <div className="stack">
          <div className="row">
            <strong>Attempt #{a.ordinal}</strong>
            <StatusPill status={a.practiceStatus} />
            <span className="muted small">started {ago(a.startedAt)}</span>
          </div>
          <dl className="kv">
            <dt>Steps</dt>
            <dd>{a.stepsTotal > 0 ? `${a.stepsCompleted} / ${a.stepsTotal}` : '—'}</dd>
            <dt>Mistakes</dt>
            <dd>{a.stepsTotal > 0 || a.mistakes > 0 ? a.mistakes : '—'}</dd>
            <dt>Hints used</dt>
            <dd>{a.stepsTotal > 0 || a.hintsUsed > 0 ? a.hintsUsed : '—'}</dd>
          </dl>
          {a.resultSummary ? <p style={{ margin: 0 }}>{a.resultSummary}</p> : <div className="muted small">Feedback appears after the attempt is reviewed.</div>}
          {past.length > 0 && (
            <details>
              <summary className="muted small">Earlier attempts ({past.length})</summary>
              <ul className="list">
                {past.map(p => (
                  <li key={p.attemptId} className="row">
                    <span>#{p.ordinal}</span>
                    <StatusPill status={p.practiceStatus} />
                    <span className="muted small">
                      {p.stepsTotal > 0 ? `${p.stepsCompleted}/${p.stepsTotal} steps · ${p.mistakes} mistakes` : ''}
                    </span>
                  </li>
                ))}
              </ul>
            </details>
          )}
          {retry.error && <div className="error-text">{retry.error}</div>}
        </div>
      ) : (
        <div className="muted">No attempt.</div>
      )}
    </Panel>
  );
}
