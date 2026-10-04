import { useState } from 'react';
import { useLive, type SessionData } from '../data/live';
import { clock, humanize, toMs } from '../lib/format';
import { uid } from '../lib/ids';
import { Empty, Panel, StatusPill, useAction } from './ui';

/** Mirrors the module's allowlist (packages/contracts/realtime-v1.md). */
export const ACTIONS: Record<string, 'none' | 'target' | 'bool' | 'number'> = {
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

function describe(c: { action: string; targetId?: string; argBool?: boolean; argNumber?: number }) {
  const arg =
    c.targetId != null ? humanize(c.targetId) : c.argBool != null ? (c.argBool ? 'on' : 'off') : c.argNumber != null ? String(c.argNumber) : '';
  return `${humanize(c.action)}${arg ? ` · ${arg}` : ''}`;
}

export default function Commands({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const [action, setAction] = useState('highlightStructure');
  const [target, setTarget] = useState('');
  const [flag, setFlag] = useState(true);
  const [num, setNum] = useState('1.5');
  const send = useAction();
  const kind = ACTIONS[action];
  const recent = [...data.commands].reverse().slice(0, 30);
  const canSend = (data.isOperator || data.isCoach) && data.session?.status === 'active';

  const submit = () =>
    send.run(async () => {
      if (!conn || !data.state || !data.session) return;
      await conn.reducers.requestCommand({
        commandId: uid('cmd'),
        sessionId: data.session.sessionId,
        action,
        targetId: kind === 'target' ? target.trim() : undefined,
        argBool: kind === 'bool' ? flag : undefined,
        argNumber: kind === 'number' ? Number(num) : undefined,
        expectedStepVersion: data.state.stepVersion,
      });
    });

  return (
    <Panel title="App actions" flush>
      {canSend && (
        <div className="panel-body" style={{ borderBottom: '1px solid var(--line)' }}>
          <form
            className="row"
            onSubmit={e => {
              e.preventDefault();
              submit();
            }}
          >
            <select value={action} onChange={e => setAction(e.target.value)} aria-label="Action">
              {Object.keys(ACTIONS).map(a => (
                <option key={a} value={a}>
                  {humanize(a)}
                </option>
              ))}
            </select>
            {kind === 'target' && (
              <input value={target} onChange={e => setTarget(e.target.value)} placeholder="structure id, e.g. gallbladder" style={{ flex: 1 }} aria-label="Target id" />
            )}
            {kind === 'bool' && (
              <select value={String(flag)} onChange={e => setFlag(e.target.value === 'true')} aria-label="Enabled">
                <option value="true">on</option>
                <option value="false">off</option>
              </select>
            )}
            {kind === 'number' && <input type="number" step="0.1" value={num} onChange={e => setNum(e.target.value)} style={{ width: 90 }} aria-label="Value" />}
            <button className="btn sm" disabled={send.busy || (kind === 'target' && !target.trim())}>
              Send
            </button>
          </form>
          <div className="muted small" style={{ marginTop: 6 }}>
            Requests go to the headset, which validates and applies or rejects them. Jarvis uses the same path.
          </div>
          {send.error && <div className="error-text">{send.error}</div>}
        </div>
      )}
      {recent.length === 0 ? (
        <Empty>No action requests yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table className="data">
            <tbody>
              {recent.map(c => {
                const latency = c.resolvedAt ? toMs(c.resolvedAt) - toMs(c.requestedAt) : null;
                return (
                  <tr key={c.commandId}>
                    <td className="muted mono small" style={{ width: 70 }}>
                      {clock(c.requestedAt)}
                    </td>
                    <td>
                      <div>{describe(c)}</div>
                      <div className="muted small">
                        by {c.requestedRole}
                        {c.reason ? ` · ${c.reason}` : ''}
                      </div>
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <StatusPill status={c.status} />
                      {latency != null && c.status === 'applied' && <div className="muted small">{latency} ms</div>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  );
}
