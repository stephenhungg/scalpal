import { Pill, ToastHost } from './components/ui';
import { DitherLogo } from './components/DitherLogo';
import { useLive } from './data/live';
import { Link, match, usePath } from './lib/router';
import Home from './pages/Home';
import Recap from './pages/Recap';
import Join from './pages/Join';
import SessionPage from './pages/Session';
import HeadsetSimulator from './pages/HeadsetSimulator';
import LiveOR from './pages/LiveOR';

export default function App() {
  const path = usePath();
  const { connected, connectionError, identity } = useLive();

  let page;
  let params: Record<string, string> | null;
  if (path === '/recap') page = <Recap />;
  else if ((params = match('/s/:id/recap', path))) page = <Recap sessionId={params.id} />;
  else if ((params = match('/join/:code', path))) page = <Join code={params.code} />;
  else if ((params = match('/s/:id/or', path))) page = <LiveOR sessionId={params.id} />;
  else if ((params = match('/s/:id/simulate', path))) page = <HeadsetSimulator sessionId={params.id} />;
  else if ((params = match('/s/:id', path))) page = <SessionPage sessionId={params.id} />;
  else page = <Home />;

  return (
    <>
      <header className="topbar">
        <Link to="/" className="brand">
          <DitherLogo />
          <span className="brand-dot" aria-hidden>.</span>
        </Link>
        <div className="spacer" />
        {path.endsWith('/recap') ? <Pill tone="info">Recap · local result viewer</Pill> : connected ? (
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
      {connectionError && !path.endsWith('/recap') && (
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
