import { useEffect, useState } from 'react';
import { VITALS_URL } from '../config';
import { Panel, Pill, useAction } from './ui';

// Volunteer vitals from services/vitals (Presage). Values only show when Presage marks them
// stable; demo mode is labelled. The OR monitor in the headset uses the captured baseline.

type Reading = { bpm: number; confidence: number } | null;
type Snapshot = {
  mode: 'demo' | 'live';
  label: string;
  status: { ok: boolean; reason: string; code: number | null };
  pulse: Reading;
  breathing: Reading;
  traces: { pulse: number[]; breathing: number[] };
  updatedAt: number | null;
};
type Baseline = { hr: number; rr: number; source: 'measured' | 'demo' | 'authored'; note?: string };

function useVitals() {
  const [snap, setSnap] = useState<Snapshot | null>(null);
  const [connected, setConnected] = useState(false);
  useEffect(() => {
    const es = new EventSource(`${VITALS_URL}/vitals/stream`);
    es.onopen = () => setConnected(true);
    es.onerror = () => setConnected(false);
    es.onmessage = e => {
      try {
        setSnap(JSON.parse(e.data));
        setConnected(true);
      } catch {
        // ignore a malformed frame
      }
    };
    return () => es.close();
  }, []);
  return { snap, connected };
}

function Trace({ values, color }: { values: number[]; color: string }) {
  if (values.length < 2) return <div className="vitals-trace empty-trace" />;
  const min = Math.min(...values);
  const span = Math.max(...values) - min || 1;
  const pts = values.map((v, i) => `${(i / (values.length - 1)) * 100},${36 - ((v - min) / span) * 32}`).join(' ');
  return (
    <svg className="vitals-trace" viewBox="0 0 100 38" preserveAspectRatio="none" aria-hidden>
      <polyline points={pts} fill="none" stroke={color} strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

function Metric({ name, unit, reading, trace, color }: { name: string; unit: string; reading: Reading; trace: number[]; color: string }) {
  return (
    <div className="vitals-metric">
      <div className="muted small">{name}</div>
      <div className="vitals-value" style={{ color: reading ? color : undefined }}>
        {reading ? Math.round(reading.bpm) : '--'}
        <span className="muted small"> {unit}</span>
      </div>
      <div className="muted small">{reading ? `${Math.round(reading.confidence)}% confidence` : 'measuring'}</div>
      <Trace values={trace} color={color} />
    </div>
  );
}

export default function Vitals() {
  const { snap, connected } = useVitals();
  const [baseline, setBaseline] = useState<Baseline | null>(null);
  const capture = useAction();

  useEffect(() => {
    fetch(`${VITALS_URL}/baseline`)
      .then(r => r.json())
      .then(setBaseline)
      .catch(() => {});
  }, [connected]);

  const live = snap?.mode === 'live';
  return (
    <Panel
      title="Volunteer vitals"
      actions={
        connected && snap ? (
          <Pill tone={live ? 'ok' : 'warn'} live={live}>
            {live ? 'Live · Presage' : 'Demo'}
          </Pill>
        ) : (
          <Pill tone="muted">Offline</Pill>
        )
      }
    >
      {!connected || !snap ? (
        <div className="notice info">
          Vitals service not reachable at {VITALS_URL}. Start it with npm start in services/vitals.
        </div>
      ) : (
        <div className="stack">
          <div className="vitals-grid">
            <Metric name="Pulse" unit="bpm" reading={snap.pulse} trace={snap.traces.pulse} color="var(--bad)" />
            <Metric name="Breathing" unit="/min" reading={snap.breathing} trace={snap.traces.breathing} color="var(--info)" />
          </div>
          {!snap.status.ok && <div className="notice warn">{snap.status.reason}</div>}
          <div className="row">
            <span className="muted small">
              Baseline{' '}
              {baseline ? (
                <>
                  HR {baseline.hr} · RR {baseline.rr} <span className="muted">({baseline.source})</span>
                </>
              ) : (
                'not captured'
              )}
            </span>
            <div className="spacer" />
            <button
              className="btn sm"
              disabled={capture.busy}
              onClick={() =>
                capture.run(async () => {
                  const r = await fetch(`${VITALS_URL}/baseline/capture`, { method: 'POST' });
                  setBaseline(await r.json());
                })
              }
            >
              Capture baseline
            </button>
          </div>
          {capture.error && <div className="notice bad">{capture.error}</div>}
          <div className="muted small">{snap.label}</div>
        </div>
      )}
    </Panel>
  );
}
