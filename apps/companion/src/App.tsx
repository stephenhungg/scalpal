import { BrandMark, Pill, ToastHost } from './components/ui';
import { useLive } from './data/live';
import { Link, match, usePath } from './lib/router';
import Home from './pages/Home';
import Join from './pages/Join';
import SessionPage from './pages/Session';
import HeadsetSimulator from './pages/HeadsetSimulator';

export default function App() {
  const path = usePath();
  const { connected, connectionError, identity } = useLive();

  let page;
  let params: Record<string, string> | null;
  if ((params = match('/join/:code', path))) page = <Join code={params.code} />;
  else if ((params = match('/s/:id/simulate', path))) page = <HeadsetSimulator sessionId={params.id} />;
  else if ((params = match('/s/:id', path))) page = <SessionPage sessionId={params.id} />;
  else page = <Home />;

  return (
    <>
      <header className="topbar">
        <Link to="/" className="brand">
          <BrandMark />
          Scalpal <small>companion</small>
        </Link>
        <div className="spacer" />
        {connected ? (
          <Pill tone="ok">Database connected</Pill>
        ) : (
          <Pill tone={connectionError ? 'bad' : 'info'} live>
            {connectionError ? 'Database unreachable' : 'Connecting…'}
          </Pill>
        )}
        {identity && (
          <span className="chip mono" title={identity.toHexString()}>
            you · {identity.toHexString().slice(0, 8)}
          </span>
        )}
      </header>
      {connectionError && (
        <div className="conn-banner">
          Cannot reach the session database ({connectionError.message || 'connection error'}). Live state will resume
          when the connection returns; reload to retry now.
        </div>
      )}
      {page}
      <ToastHost />
    </>
  );
}
