import { useLive, type SessionData } from '../data/live';
import { Link } from '../lib/router';
import { copy, Panel, Pill, useAction } from './ui';

const ROLE_HELP: Record<string, string> = {
  viewer: 'Watch only',
  coach: 'Scalpal bridge',
  headset: 'Quest app',
  operator: 'Full control',
};

export default function Members({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const act = useAction();
  const sessionId = data.session!.sessionId;
  const active = data.session?.status === 'active';
  const order = ['viewer', 'coach', 'headset', 'operator'];
  const invites = [...data.invites].sort((a, b) => order.indexOf(a.role) - order.indexOf(b.role));
  const link = (code: string) => `${location.origin}/join/${code}`;

  return (
    <Panel title="People and access" flush>
      {data.isOperator && active && (
        <div>
          {invites.map(inv => (
            <div className="invite" key={inv.code}>
              <div>
                <div style={{ fontWeight: 600, textTransform: 'capitalize' }}>{inv.role}</div>
                <div className="muted small">{ROLE_HELP[inv.role]}</div>
              </div>
              <div className="code-box" style={{ fontSize: 15 }}>
                {inv.code}
              </div>
              <div className="row" style={{ gap: 6 }}>
                <button className="btn sm" onClick={() => copy(link(inv.code), 'Invite link copied')}>
                  Copy link
                </button>
                <button
                  className="btn sm ghost"
                  title="Revoke this code and issue a new one"
                  disabled={act.busy}
                  onClick={() => act.run(() => conn!.reducers.rotateInvite({ sessionId, role: inv.role }))}
                >
                  Rotate
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
      <ul className="list" style={{ borderTop: data.isOperator && active ? '1px solid var(--line)' : undefined }}>
        {data.members.map(m => (
          <li key={String(m.membershipId)} className="row">
            <Pill tone={m.online ? 'ok' : 'muted'} dot>
              {m.online ? 'online' : 'away'}
            </Pill>
            <span style={{ fontWeight: 550 }}>{m.displayName || 'Unnamed'}</span>
            <span className="chip">{m.role}</span>
            {m.identity.toHexString() === data.me && <span className="muted small">you</span>}
            <div className="spacer" />
            {data.isOperator && active && m.identity.toHexString() !== data.me && (
              <button
                className="btn sm ghost"
                disabled={act.busy}
                onClick={() => act.run(() => conn!.reducers.removeMember({ membershipId: m.membershipId }))}
              >
                Remove
              </button>
            )}
          </li>
        ))}
      </ul>
      {data.isOperator && active && (
        <div className="panel-body row" style={{ borderTop: '1px solid var(--line)' }}>
          <Link to={`/s/${sessionId}/simulate`} className="btn sm">
            Synthetic headset
          </Link>
          <div className="spacer" />
          <button
            className="btn sm danger"
            disabled={act.busy}
            onClick={() =>
              act.run(async () => {
                if (confirm('End this session? Invite codes stop working and the live view stops.')) {
                  await conn!.reducers.endSession({ sessionId });
                }
              })
            }
          >
            End session
          </button>
        </div>
      )}
      {act.error && <div className="panel-body error-text">{act.error}</div>}
    </Panel>
  );
}
