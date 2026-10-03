import { useEffect, useState, type ReactNode } from 'react';

export function Panel({
  title,
  actions,
  children,
  flush,
  className,
}: {
  title: ReactNode;
  actions?: ReactNode;
  children: ReactNode;
  flush?: boolean;
  className?: string;
}) {
  return (
    <section className={`panel ${className ?? ''}`}>
      <header className="panel-head">
        <h2>{title}</h2>
        <div className="spacer" />
        {actions}
      </header>
      <div className={`panel-body ${flush ? 'flush' : ''}`}>{children}</div>
    </section>
  );
}

export type Tone = 'ok' | 'warn' | 'bad' | 'info' | 'muted' | 'accent';

export function Pill({ tone, children, live, dot = true }: { tone: Tone; children: ReactNode; live?: boolean; dot?: boolean }) {
  return (
    <span className={`pill ${tone} ${live ? 'live' : ''}`}>
      {dot && <span className="dot" />}
      {children}
    </span>
  );
}

const TONES: Record<string, Tone> = {
  // generic
  active: 'ok',
  ended: 'muted',
  // registration
  valid: 'ok',
  uncertain: 'warn',
  unaligned: 'muted',
  // recording
  off: 'muted',
  recording: 'bad',
  complete: 'ok',
  // commands
  pending: 'info',
  applied: 'ok',
  rejected: 'warn',
  unavailable: 'warn',
  expired: 'muted',
  // artifacts
  pending_upload: 'info',
  verifying: 'info',
  available: 'ok',
  deleted: 'muted',
  // jobs
  queued: 'info',
  running: 'accent',
  ready: 'ok',
  failed: 'bad',
  cancelled: 'muted',
  // media
  starting: 'info',
  live: 'ok',
  stopped: 'muted',
  denied: 'warn',
  error: 'bad',
  // coach
  offline: 'muted',
  connecting: 'info',
  listening: 'ok',
  thinking: 'accent',
  speaking: 'accent',
  // attempts
  not_started: 'muted',
  in_progress: 'info',
  completed: 'ok',
  abandoned: 'muted',
};

export function toneOf(status: string | undefined | null): Tone {
  return (status && TONES[status]) || 'muted';
}

export function StatusPill({ status, label }: { status: string; label?: string }) {
  return (
    <Pill tone={toneOf(status)} live={status === 'live' || status === 'recording' || status === 'running'}>
      {label ?? status.replace(/_/g, ' ')}
    </Pill>
  );
}

export function Progress({ value }: { value: number }) {
  return (
    <div className="progress" role="progressbar" aria-valuenow={Math.round(value * 100)} aria-valuemin={0} aria-valuemax={100}>
      <span style={{ width: `${Math.max(0, Math.min(1, value)) * 100}%` }} />
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

/** Run an async action, tracking busy state and surfacing errors. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
    } catch (err: any) {
      setError(String(err?.message ?? err));
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, run, setError };
}

let toastSetter: ((msg: string | null) => void) | null = null;

export function toast(msg: string) {
  toastSetter?.(msg);
}

export function ToastHost() {
  const [msg, setMsg] = useState<string | null>(null);
  useEffect(() => {
    toastSetter = setMsg;
    return () => {
      toastSetter = null;
    };
  }, []);
  useEffect(() => {
    if (!msg) return;
    const id = setTimeout(() => setMsg(null), 2600);
    return () => clearTimeout(id);
  }, [msg]);
  return msg ? (
    <div className="toast" role="status">
      {msg}
    </div>
  ) : null;
}

export async function copy(text: string, what = 'Copied') {
  try {
    await navigator.clipboard.writeText(text);
    toast(`${what} to clipboard`);
  } catch {
    toast('Copy failed: select and copy manually');
  }
}

export function BrandMark() {
  return (
    <svg className="brand-mark" viewBox="0 0 32 32" aria-hidden>
      <rect width="32" height="32" rx="8" fill="var(--panel-2)" stroke="var(--line-strong)" />
      <path d="M8 22 L22 8 L25 11 L11 25 Z" fill="var(--accent)" />
      <circle cx="10" cy="22" r="2.5" fill="var(--text)" />
    </svg>
  );
}
