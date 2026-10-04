import { useMemo, useState } from 'react';
import { ago, clock } from '../lib/format';
import { SIM_LOG_KINDS, compactVitals, parseData, toneOfLog, type SimLogKind } from '../lib/simlog';
import { useNow, type SessionData } from '../data/live';
import { Empty, Panel } from './ui';

// Operating-room log from the coach (sim_log): state tracker events, Jarvis alerts, vitals samples,
// checklist changes and the case outcome. Newest first; the latest vitals sample stays pinned on top.

const LABEL: Record<SimLogKind, string> = {
  event: 'Events',
  alert: 'Alerts',
  vitals: 'Vitals',
  checklist: 'Checklist',
  outcome: 'Outcome',
};

const SHOWN = 300;

export default function SimLogs({ data }: { data: SessionData }) {
  const [off, setOff] = useState<Set<string>>(() => new Set());
  const now = useNow(5000);

  const rows = useMemo(
    () => data.simLogs.map(r => ({ row: r, data: parseData(r.dataJson) })),
    [data.simLogs]
  );
  const counts = useMemo(() => {
    const c: Record<string, number> = {};
    for (const { row } of rows) c[row.kind] = (c[row.kind] ?? 0) + 1;
    return c;
  }, [rows]);
  const latestVitals = rows.find(r => r.row.kind === 'vitals') ?? null;
  const shown = rows.filter(r => !off.has(r.row.kind)).slice(0, SHOWN);

  const toggle = (kind: string) =>
    setOff(prev => {
      const next = new Set(prev);
      if (next.has(kind)) next.delete(kind);
      else next.add(kind);
      return next;
    });

  return (
    <Panel title="Live logs" flush>
      {latestVitals && (
        <div className="simlog-vitals">
          <span className="muted small">Vitals</span>
          <span className="mono">{compactVitals(latestVitals.data) ?? latestVitals.row.text}</span>
          <span className="spacer" />
          <span className="muted small">{ago(latestVitals.row.at, now)}</span>
        </div>
      )}
      <div className="simlog-filters" role="group" aria-label="Filter logs by kind">
        {SIM_LOG_KINDS.map(k => (
          <button
            key={k}
            type="button"
            className={`chip simlog-chip ${off.has(k) ? '' : 'on'}`}
            aria-pressed={!off.has(k)}
            onClick={() => toggle(k)}
          >
            {LABEL[k]} {counts[k] ?? 0}
          </button>
        ))}
      </div>
      {rows.length === 0 ? (
        <Empty>No operating-room logs yet. They stream in once Jarvis is coaching a case in this session.</Empty>
      ) : shown.length === 0 ? (
        <Empty>Every kind is filtered out.</Empty>
      ) : (
        <ul className="timeline simlog" aria-live="polite">
          {shown.map(({ row, data: d }) => {
            const tone = toneOfLog(row.kind, d);
            const vitals = row.kind === 'vitals' ? compactVitals(d) : null;
            return (
              <li key={String(row.id)} className={`simlog-${row.kind} ${tone}`}>
                <span className="t">{clock(row.at)}</span>
                <span className={`mark ${tone}`} />
                <span>
                  <span className="simlog-kind">{row.kind}</span>{' '}
                  {vitals ? <span className="mono">{vitals}</span> : <span className="simlog-text">{row.text}</span>}
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </Panel>
  );
}
