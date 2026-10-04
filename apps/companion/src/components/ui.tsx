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

// Scalpal mark (traced from the team logo), white to match the site.
export function BrandMark() {
  return (
    <svg className="brand-mark" viewBox="0 0 100 100" role="img" aria-label="Scalpal">
      <path fill="var(--text)" d="M5.06 81.31 L5.41 81.60 L6.10 81.42 L44.94 58.58 L45.52 57.94 L45.70 57.47 L45.70 50.96 L45.81 50.67 L46.34 50.20 L46.92 50.20 L47.27 50.44 L58.78 62.12 L59.48 62.65 L60.35 62.82 L62.85 61.83 L93.72 47.94 L94.77 47.35 L94.94 47.01 L94.94 45.73 L94.53 45.20 L51.28 45.15 L50.29 44.91 L49.30 44.27 L21.92 18.98 L20.93 18.40 L17.50 18.40 L17.15 18.87 L17.27 19.33 L32.27 44.33 L32.73 44.80 L33.37 45.09 L39.13 45.09 L39.77 45.61 L39.83 46.25 L38.31 47.82 L5.29 78.28 L5.00 79.04 Z" />
    </svg>
  );
}
