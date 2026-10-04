// Helpers for the operating-room log (sim_log rows posted by the coach).

export const SIM_LOG_KINDS = ['event', 'alert', 'vitals', 'checklist', 'outcome'] as const;
export type SimLogKind = (typeof SIM_LOG_KINDS)[number];

/** Parse a row's dataJson; null when empty or not valid JSON. */
export function parseData(dataJson: string): unknown {
  if (!dataJson) return null;
  try {
    return JSON.parse(dataJson);
  } catch {
    return null;
  }
}

type Rec = Record<string, unknown>;
const isRec = (x: unknown): x is Rec => typeof x === 'object' && x !== null && !Array.isArray(x);
const num = (x: unknown): number | null => (typeof x === 'number' && Number.isFinite(x) ? x : null);

function first(rec: Rec, keys: string[]): number | null {
  for (const k of keys) {
    const v = num(rec[k]);
    if (v != null) return v;
  }
  return null;
}

/**
 * Compact vitals line from a vitals payload, e.g. "HR 118 · BP 92/60 · RR 22 · loss 18%".
 * Accepts the coach's MonitorVitals shape (hr, rr, sys, dia, bloodLossPct) and a few common
 * aliases, optionally nested under `vitals`. Returns null when nothing recognizable is present.
 */
export function compactVitals(data: unknown): string | null {
  if (!isRec(data)) return null;
  const v = isRec(data.vitals) ? { ...data, ...data.vitals } : data;
  const parts: string[] = [];
  const hr = first(v, ['hr', 'heartRate', 'pulse']);
  if (hr != null) parts.push(`HR ${Math.round(hr)}`);
  const sys = first(v, ['sys', 'systolic', 'sbp']);
  const dia = first(v, ['dia', 'diastolic', 'dbp']);
  if (sys != null && dia != null) parts.push(`BP ${Math.round(sys)}/${Math.round(dia)}`);
  else if (typeof v.bp === 'string') parts.push(`BP ${v.bp}`);
  const rr = first(v, ['rr', 'respRate', 'breathing']);
  if (rr != null) parts.push(`RR ${Math.round(rr)}`);
  const spo2 = first(v, ['spo2', 'spO2', 'SpO2']);
  if (spo2 != null && spo2 >= 0) parts.push(`SpO2 ${Math.round(spo2)}%`); // -1: not charted
  const loss = first(v, ['bloodLossPct', 'lossPct', 'loss']);
  if (loss != null) parts.push(`loss ${Math.round(loss)}%`);
  return parts.length ? parts.join(' · ') : null;
}

/** Tone for a row: alerts are red, or amber when the payload marks them as a warning. */
export function toneOfLog(kind: string, data: unknown): 'bad' | 'warn' | 'ok' | 'info' | 'accent' | 'muted' {
  if (kind === 'alert') {
    // The coach sends {kind, tier}: warning = red, caution = amber, advisory (e.g. bleeding controlled) = muted.
    const tier = isRec(data) && typeof data.tier === 'string' ? data.tier : '';
    if (tier) return tier === 'warning' ? 'bad' : tier === 'caution' ? 'warn' : 'muted';
    const sev = isRec(data) ? String(data.severity ?? data.level ?? '') : '';
    return /warn|amber|low|moderate|caution/i.test(sev) ? 'warn' : 'bad';
  }
  if (kind === 'vitals') return 'info';
  if (kind === 'checklist') return 'ok';
  if (kind === 'outcome') return 'accent';
  return 'muted';
}
