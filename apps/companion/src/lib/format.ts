import type { Timestamp } from 'spacetimedb';

export function toDate(ts: Timestamp | undefined | null): Date | null {
  if (!ts) return null;
  return new Date(Number(ts.microsSinceUnixEpoch / 1000n));
}

export function toMs(ts: Timestamp | undefined | null): number {
  return ts ? Number(ts.microsSinceUnixEpoch / 1000n) : 0;
}

export function clock(ts: Timestamp | undefined | null): string {
  const d = toDate(ts);
  return d ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }) : '—';
}

export function ago(ts: Timestamp | undefined | null, now = Date.now()): string {
  const d = toDate(ts);
  if (!d) return '—';
  const s = Math.max(0, Math.round((now - d.getTime()) / 1000));
  if (s < 5) return 'just now';
  if (s < 60) return `${s}s ago`;
  const m = Math.round(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60);
  return `${h}h ago`;
}

export function bytes(n: number | bigint | undefined | null): string {
  if (n == null) return '—';
  const v = Number(n);
  if (v < 1024) return `${v} B`;
  if (v < 1024 ** 2) return `${(v / 1024).toFixed(1)} KB`;
  if (v < 1024 ** 3) return `${(v / 1024 ** 2).toFixed(1)} MB`;
  return `${(v / 1024 ** 3).toFixed(2)} GB`;
}

export function duration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const s = ms / 1000;
  const m = Math.floor(s / 60);
  const rem = s - m * 60;
  return `${m}:${rem.toFixed(1).padStart(4, '0')}`;
}

export function pct(x: number | undefined | null): string {
  return x == null ? '—' : `${Math.round(x * 100)}%`;
}

/** Turn camelCase / snake_case identifiers into readable labels. */
export function humanize(id: string | undefined | null): string {
  if (!id) return '—';
  return id
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/^\w/, c => c.toUpperCase());
}
