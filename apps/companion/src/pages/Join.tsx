import { useEffect, useState } from 'react';
import { Panel, useAction } from '../components/ui';
import { NAME_KEY } from '../config';
import { useLive } from '../data/live';
import { joinWithCode } from '../lib/grants';
import { navigate } from '../lib/router';
import { load, save } from '../lib/storage';

/** Landing page for shared invite links: /join/ABC123 */
export default function Join({ code }: { code: string }) {
  const live = useLive();
  const [name, setName] = useState(load(NAME_KEY) ?? '');
  const join = useAction();

  useEffect(() => save(NAME_KEY, name), [name]);

  const onJoin = () =>
    join.run(async () => {
      if (!live.conn) throw new Error('Not connected to the session database yet.');
      const sid = await joinWithCode(live.conn, code, name.trim());
      navigate(`/s/${sid}`, true);
    });

  return (
    <main className="page page-narrow">
      <div style={{ maxWidth: 460, margin: '40px auto' }}>
        <Panel title="Join session">
          <form
            className="stack"
            onSubmit={e => {
              e.preventDefault();
              onJoin();
            }}
          >
            <div>
              <div className="muted small">Invite code</div>
              <div className="code-box">{code.toUpperCase()}</div>
            </div>
            <label className="field">
              Your name
              <input autoFocus value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Judge" maxLength={60} />
            </label>
            <button className="btn primary" type="submit" disabled={!live.conn || join.busy}>
              {join.busy ? 'Joining…' : live.conn ? 'Join' : 'Connecting…'}
            </button>
            {join.error && <div className="error-text">{join.error}</div>}
          </form>
        </Panel>
      </div>
    </main>
  );
}
