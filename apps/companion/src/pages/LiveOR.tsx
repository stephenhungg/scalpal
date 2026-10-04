import { useMemo, useState } from 'react';
import type { Timestamp } from 'spacetimedb';
import { Empty, Panel, Pill, StatusPill, useAction, type Tone } from '../components/ui';
import { useLive, useNow, useSession, type SessionData } from '../data/live';
import { ago, clock, humanize, toMs } from '../lib/format';
import { uid } from '../lib/ids';
import { Link } from '../lib/router';
import { parseData } from '../lib/simlog';

// The live operating room as a spectator / attending / scrub-nurse console. Everything on this page is
// a SpacetimeDB subscription: the headset (surgeon), the coach (Scalpal), this browser (nurse) and the
// robot learner all write to the same session through role-checked reducers, and every change lands
// here as it commits.

/** Open-case stand instruments, mirroring OPEN_CASE_INSTRUMENTS in services/realtime. */
const INSTRUMENTS = [
  'skin_marker',
  'scalpel',
  'toothed_forceps',
  'retractor',
  'babcock',
  'hemostat',
  'right_angle_clamp',
  'metzenbaum_scissors',
  'suture_tie',
  'suction_irrigator',
  'laparoscope_30',
];

type Hand = 'either' | 'left' | 'right';
const HAND_ARG: Record<Hand, number | undefined> = { either: undefined, left: 0, right: 1 };

type Actor = 'Surgeon' | 'Scalpal' | 'Nurse' | 'Robot' | 'Simulator';
const ACTOR_TONE: Record<Actor, Tone> = { Surgeon: 'accent', Scalpal: 'info', Nurse: 'warn', Robot: 'ok', Simulator: 'muted' };

/** Who asked for a command, from the role the module stamped on it. */
function actorOfRole(role: string): Actor {
  if (role === 'coach' || role === 'service') return 'Scalpal';
  if (role === 'headset') return 'Surgeon';
  return 'Nurse';
}

type Rec = Record<string, unknown>;
const isRec = (x: unknown): x is Rec => typeof x === 'object' && x !== null && !Array.isArray(x);
const num = (x: unknown): number | null => (typeof x === 'number' && Number.isFinite(x) ? x : null);

function describeCommand(c: { action: string; targetId?: string; argNumber?: number }) {
  const what = humanize(c.targetId).toLowerCase();
  if (c.action === 'handInstrument') return `hand ${what} → ${c.argNumber === 0 ? 'left' : c.argNumber === 1 ? 'right' : 'either'} hand`;
  if (c.action === 'highlightInstrument') return `highlight ${what}`;
  return `${humanize(c.action).toLowerCase()}${c.targetId ? ` · ${what}` : ''}`;
}

/** The patient as the coach last described it in the operating-room log. */
function usePatient(data: SessionData) {
  return useMemo(() => {
    const logs = data.simLogs; // newest first
    const latest = (kind: string) => logs.find(r => r.kind === kind) ?? null;
    const vitalsRow = latest('vitals');
    const vitals = vitalsRow ? parseData(vitalsRow.dataJson) : null;
    const checklistRow = latest('checklist');
    const checklistData = checklistRow ? parseData(checklistRow.dataJson) : null;
    const checklist = Array.isArray(checklistData)
      ? checklistData.filter(isRec).map(c => ({ id: String(c.id ?? c.title), title: String(c.title ?? c.id), done: Boolean(c.done), current: Boolean(c.current) }))
      : [];
    // Patient outcomes only: robot verdicts are outcome rows too, but belong to the robot.
    const outcomeRow = logs.find(r => r.kind === 'outcome' && !String(r.dataJson).includes('"robotResult"')) ?? null;
    const outcome = outcomeRow ? parseData(outcomeRow.dataJson) : null;
    // Bleeds open with a "bleeding" alert and close with "bleeding_controlled", within the coach's current case.
    const coachSession = (vitalsRow ?? checklistRow ?? logs[0])?.coachSessionId;
    let opened = 0;
    let closed = 0;
    let lastBleed: string | null = null;
    for (const r of logs) {
      if (r.kind !== 'alert' || r.coachSessionId !== coachSession) continue;
      const d = parseData(r.dataJson);
      const kind = isRec(d) ? d.kind : null;
      if (kind === 'bleeding') {
        opened += 1;
        lastBleed ??= r.text;
      } else if (kind === 'bleeding_controlled') closed += 1;
    }
    return {
      vitalsRow,
      vitals: isRec(vitals) ? vitals : null,
      checklist,
      outcomeRow,
      outcome: isRec(outcome) ? outcome : null,
      activeBleeds: Math.max(0, opened - closed),
      lastBleed: opened > closed ? lastBleed : null,
    };
  }, [data.simLogs]);
}

function Stat({ label, value, unit, tone }: { label: string; value: string; unit?: string; tone?: string }) {
  return (
    <div className="or-stat">
      <div className="muted small">{label}</div>
      <div className="or-stat-value" style={tone ? { color: `var(--${tone})` } : undefined}>
        {value}
        {unit && <span className="muted small"> {unit}</span>}
      </div>
    </div>
  );
}

const parseList = (json: string): Rec[] => {
  const d = parseData(json);
  return Array.isArray(d) ? d.filter(isRec) : [];
};

/** The simulated patient as SpacetimeDB itself computes it (patient_condition, advanced by the module at 1 Hz). */
function ConditionVitals({ c, now }: { c: NonNullable<SessionData['patientCondition']>; now: number }) {
  const died = c.outcomeResult === 'died';
  const bleeds = parseList(c.activeBleedsJson);
  const regions = parseList(c.regionInjuriesJson).filter(r => r.bleeding === true);
  const sources = [
    ...bleeds.map(b => `${String(b.name ?? 'bleed')} ${Math.round(num(b.rateMlPerMin) ?? 0)} ml/min`),
    ...regions.map(r => `${String(r.label ?? r.region)} ${Math.round(num(r.rawBleedMlPerMin) ?? 0)} ml/min`),
  ];
  return (
    <>
      <div className="or-vitals">
        <Stat label="HR" value={died ? '0' : String(c.hr)} unit="bpm" tone={died || c.hr > 110 ? 'bad' : undefined} />
        <Stat label="BP" value={`${c.sys}/${c.dia}`} tone={died || c.sys < 90 ? 'bad' : undefined} />
        <Stat label="SpO2" value={c.spo2 >= 0 ? String(c.spo2) : '--'} unit="%" />
        <Stat label="RR" value={String(c.rr)} unit="/min" />
      </div>
      <div className="row small">
        <span>
          Blood loss <b>{Math.round(c.bloodLossPct)}%</b>
          <span className="muted">
            {' '}
            · {Math.round(c.bloodLostMl)} of {Math.round(c.ebvMl)} ml · class {c.hemorrhageClass}
          </span>
        </span>
        <span className="spacer" />
        {sources.length > 0 && !died ? (
          <Pill tone="bad" live>
            {sources.length} active bleed{sources.length > 1 ? 's' : ''}
          </Pill>
        ) : (
          <Pill tone="muted">No active bleeding</Pill>
        )}
      </div>
      {sources.length > 0 && !died && <div className="notice bad small">Bleeding: {sources.join(' · ')} (raw)</div>}
      {c.outcomeResult !== 'in_progress' && (
        <div className={`notice ${died ? 'bad' : 'info'} small`}>
          {died ? 'Patient died' : humanize(c.outcomeResult)}
          {c.outcomeCause ? `: ${c.outcomeCause}` : ''}
        </div>
      )}
      <div className="muted small">
        <Pill tone="info">SpacetimeDB</Pill> Simulated physiology computed in the database · {c.label || `baseline ${c.baselineSource}`} · ×{c.scale} demo
        acceleration · {ago(c.updatedAt, now)}
      </div>
    </>
  );
}

function PatientPanel({ data }: { data: SessionData }) {
  const p = usePatient(data);
  const now = useNow(5000);
  const v = p.vitals;
  const hr = num(v?.hr);
  const sys = num(v?.sys);
  const dia = num(v?.dia);
  const spo2 = num(v?.spo2);
  const rr = num(v?.rr);
  const loss = num(v?.bloodLossPct);
  const klass = num(v?.hemorrhageClass);
  const result = typeof p.outcome?.result === 'string' ? p.outcome.result : null;
  const state = data.state;
  const step = p.checklist.find(c => c.current);
  const cond = data.patientCondition;

  return (
    <Panel
      title="Patient"
      actions={
        cond ? (
          cond.outcomeResult === 'died' ? (
            <Pill tone="bad">Died</Pill>
          ) : cond.outcomeResult === 'completed' ? (
            <Pill tone="ok">Case goals reached</Pill>
          ) : cond.outcomeResult === 'in_progress' ? (
            <Pill tone="ok" live>
              Live
            </Pill>
          ) : null
        ) : result === 'died' ? (
          <Pill tone="bad">Died</Pill>
        ) : result === 'completed' ? (
          <Pill tone="ok">Case goals reached</Pill>
        ) : p.vitalsRow ? (
          <Pill tone="ok" live>
            Live
          </Pill>
        ) : null
      }
    >
      <div className="stack">
        <div>
          <div className="muted small">Current step</div>
          <div className="or-step">
            {step?.title ?? humanize(state?.stepId)}
            {state && (
              <span className="muted small">
                {' '}
                · {state.stepIndex + 1}/{state.stepCount} · {humanize(state.mode).toLowerCase()}
                {state.paused ? ' · paused' : ''}
              </span>
            )}
          </div>
        </div>
        {cond ? (
          <ConditionVitals c={cond} now={now} />
        ) : p.vitalsRow ? (
          <>
            <div className="or-vitals">
              <Stat label="HR" value={hr != null ? String(Math.round(hr)) : '--'} unit="bpm" tone={hr != null && hr > 110 ? 'bad' : undefined} />
              <Stat label="BP" value={sys != null && dia != null ? `${Math.round(sys)}/${Math.round(dia)}` : '--'} tone={sys != null && sys < 90 ? 'bad' : undefined} />
              <Stat label="SpO2" value={spo2 != null && spo2 >= 0 ? String(Math.round(spo2)) : '--'} unit="%" />
              <Stat label="RR" value={rr != null ? String(Math.round(rr)) : '--'} unit="/min" />
            </div>
            <div className="row small">
              <span>
                Blood loss <b>{loss != null ? `${Math.round(loss)}%` : '—'}</b>
                {klass != null && <span className="muted"> · class {klass}</span>}
              </span>
              <span className="spacer" />
              {p.activeBleeds > 0 ? (
                <Pill tone="bad" live>
                  {p.activeBleeds} active bleed{p.activeBleeds > 1 ? 's' : ''}
                </Pill>
              ) : (
                <Pill tone="muted">No active bleeding</Pill>
              )}
            </div>
            {p.lastBleed && <div className="notice bad small">{p.lastBleed}</div>}
            <div className="muted small">
              Simulated monitor{typeof v?.label === 'string' && v.label ? ` · ${v.label}` : ''} · {ago(p.vitalsRow.at, now)}
            </div>
          </>
        ) : (
          <div className="muted small">Vitals appear when the coach starts tracking the case in this session.</div>
        )}
        {!cond && p.outcomeRow && result !== 'in_progress' && <div className={`notice ${result === 'died' ? 'bad' : 'info'} small`}>{p.outcomeRow.text}</div>}
        {p.checklist.length > 0 && (
          <ol className="or-checklist">
            {p.checklist.map(c => (
              <li key={c.id} className={c.current ? 'current' : c.done ? 'done' : ''}>
                <span className="or-check" aria-hidden>
                  {c.done ? '✓' : c.current ? '›' : ''}
                </span>
                {c.title}
              </li>
            ))}
          </ol>
        )}
      </div>
    </Panel>
  );
}

const SPEAKER: Record<string, string> = { learner: 'Surgeon', coach: 'Scalpal', patient: 'Patient', system: 'Simulator' };

function TranscriptPanel({ data }: { data: SessionData }) {
  const lines = useMemo(() => [...data.coachMessages].reverse().slice(0, 60), [data.coachMessages]);
  return (
    <Panel title="Transcript" actions={data.coachStatus ? <StatusPill status={data.coachStatus.status} label={`Scalpal ${data.coachStatus.status}`} /> : null} flush>
      {lines.length === 0 ? (
        <Empty>Nothing said yet.</Empty>
      ) : (
        <ul className="or-transcript" aria-live="polite">
          {lines.map(m => (
            <li key={String(m.messageId)} className={`or-line ${m.speaker}`}>
              <span className="or-who">
                {SPEAKER[m.speaker] ?? m.speaker} <span className="muted">{clock(m.at)}</span>
              </span>
              <span>{m.text}</span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function TrayPanel({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const [hand, setHand] = useState<Hand>('either');
  const send = useAction();
  const active = data.session?.status === 'active';
  const member = data.roles.size > 0;
  // The module lets coach, operator and viewer request instrument actions.
  const canSend = active && (data.isOperator || data.isCoach || data.roles.has('viewer'));
  // Latest command per instrument, for the inline status.
  const latest = useMemo(() => {
    const m = new Map<string, (typeof data.commands)[number]>();
    for (const c of data.commands) if (c.targetId && (c.action === 'handInstrument' || c.action === 'highlightInstrument')) m.set(c.targetId, c);
    return m;
  }, [data.commands]);

  const request = (action: 'handInstrument' | 'highlightInstrument', targetId: string) =>
    send.run(async () => {
      if (!conn || !data.session) throw new Error('Not connected to the session database yet.');
      if (!data.state) throw new Error('The headset has not published its state yet.');
      await conn.reducers.requestCommand({
        commandId: uid('cmd'),
        sessionId: data.session.sessionId,
        action,
        targetId,
        argBool: undefined,
        argNumber: action === 'handInstrument' ? HAND_ARG[hand] : undefined,
        expectedStepVersion: data.state.stepVersion,
      });
    });

  return (
    <Panel
      title="Scrub nurse"
      actions={
        <div className="or-hands" role="group" aria-label="Hand">
          {(['left', 'either', 'right'] as Hand[]).map(h => (
            <button key={h} type="button" className={`chip ${hand === h ? 'on' : ''}`} aria-pressed={hand === h} onClick={() => setHand(h)}>
              {h}
            </button>
          ))}
        </div>
      }
      flush
    >
      <ul className="or-tray">
        {INSTRUMENTS.map(id => {
          const c = latest.get(id);
          return (
            <li key={id}>
              <button className="btn sm or-hand" disabled={!canSend || send.busy} onClick={() => request('handInstrument', id)} title={`Hand the ${humanize(id).toLowerCase()} to the ${hand} hand`}>
                {humanize(id)}
              </button>
              <button className="btn ghost sm" disabled={!canSend || send.busy} onClick={() => request('highlightInstrument', id)} title="Highlight on the stand" aria-label={`Highlight ${humanize(id)}`}>
                Show
              </button>
              <span className="or-tray-status">
                {c && (
                  <>
                    <StatusPill status={c.status} />
                    {c.reason && <span className="muted small" title={c.reason}> {c.reason}</span>}
                  </>
                )}
              </span>
            </li>
          );
        })}
      </ul>
      {!member && <div className="panel-body muted small">Join this session with an invite code to hand instruments.</div>}
      {send.error && <div className="panel-body error-text">{send.error}</div>}
    </Panel>
  );
}

interface Activity {
  key: string;
  at: number;
  actor: Actor;
  text: string;
  status?: string;
  reason?: string;
}

function ActivityPanel({ data }: { data: SessionData }) {
  const items = useMemo(() => {
    const out: Activity[] = [];
    for (const c of data.commands) {
      out.push({ key: `c${c.commandId}`, at: toMs(c.requestedAt), actor: actorOfRole(c.requestedRole), text: describeCommand(c), status: c.status, reason: c.reason });
    }
    for (const e of data.events) {
      if (data.currentAttempt && e.attemptId !== data.currentAttempt.attemptId) continue;
      out.push({ key: `e${e.eventId}`, at: toMs(e.at), actor: 'Surgeon', text: e.message || humanize(e.kind) });
    }
    for (const r of data.simLogs) {
      if (r.kind === 'vitals' || r.kind === 'checklist') continue; // shown in the patient panel
      const robot = /"robot(Attempt|Demo|Result)"/.test(r.dataJson);
      if (robot && r.kind === 'outcome' && data.robotResults.length) continue; // the robot_result row says it
      out.push({ key: `s${r.id}`, at: toMs(r.at), actor: robot ? 'Robot' : r.kind === 'alert' ? 'Scalpal' : 'Simulator', text: r.text, status: r.kind === 'alert' ? 'alert' : undefined });
    }
    for (const r of data.robotResults) {
      out.push({
        key: `r${r.id}`,
        at: toMs(r.at),
        actor: 'Robot',
        text: `${r.success ? 'reproduced' : 'missed'} ${humanize(r.stepId).toLowerCase()}${r.pathErrorMm != null ? ` · ${r.pathErrorMm.toFixed(1)} mm path error` : ''}`,
      });
    }
    return out.sort((a, b) => b.at - a.at).slice(0, 60);
  }, [data]);

  return (
    <Panel title="Activity" flush>
      {items.length === 0 ? (
        <Empty>No activity yet.</Empty>
      ) : (
        <ul className="or-activity" aria-live="polite">
          {items.map(i => (
            <li key={i.key}>
              <span className="muted mono small">{new Date(i.at).toLocaleTimeString([], { hourCycle: 'h23' })}</span>
              <Pill tone={ACTOR_TONE[i.actor]} dot={false}>
                {i.actor}
              </Pill>
              <span className="or-activity-text">
                {i.text}
                {i.reason && <span className="muted"> · {i.reason}</span>}
              </span>
              {i.status && (i.status === 'alert' ? <Pill tone="bad">alert</Pill> : <StatusPill status={i.status} />)}
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

interface Verdict {
  key: string;
  at: number;
  atTs: Timestamp;
  stepId: string;
  success: boolean;
  pathErrorMm: number | null;
  rate: number | null;
  human: number;
  synthetic: number;
}

/** Robot verdicts: robot_result rows, or the coach's robot outcome logs from before that table existed. */
function useVerdicts(data: SessionData): Verdict[] {
  return useMemo(() => {
    if (data.robotResults.length) {
      return data.robotResults.map(r => ({
        key: String(r.id),
        at: toMs(r.at),
        atTs: r.at,
        stepId: r.stepId,
        success: r.success,
        pathErrorMm: r.pathErrorMm ?? null,
        rate: r.policySuccessRate ?? null,
        human: r.demosHuman,
        synthetic: r.demosSynthetic,
      }));
    }
    const out: Verdict[] = [];
    for (const r of [...data.simLogs].reverse()) {
      const d = parseData(r.dataJson);
      const rr = isRec(d) && isRec(d.robotResult) ? d.robotResult : null;
      if (!rr) continue;
      const demos = isRec(rr.demos) ? rr.demos : {};
      out.push({
        key: `s${r.id}`,
        at: toMs(r.at),
        atTs: r.at,
        stepId: String(rr.stepId ?? ''),
        success: rr.success === true,
        pathErrorMm: num(rr.pathErrorMm),
        rate: num(rr.policySuccessRate),
        human: num(demos.human) ?? 0,
        synthetic: num(demos.synthetic) ?? 0,
      });
    }
    return out;
  }, [data.robotResults, data.simLogs]);
}

function Sparkline({ verdicts }: { verdicts: Verdict[] }) {
  const pts = verdicts.slice(-24);
  if (pts.length < 2) return null;
  const maxDemos = Math.max(1, ...pts.map(v => v.human + v.synthetic));
  const x = (i: number) => (i / (pts.length - 1)) * 100;
  const rate = pts.map((v, i) => `${x(i)},${30 - (v.rate ?? (v.success ? 1 : 0)) * 28}`).join(' ');
  const demos = pts.map((v, i) => `${x(i)},${30 - ((v.human + v.synthetic) / maxDemos) * 28}`).join(' ');
  return (
    <div>
      <svg className="or-spark" viewBox="0 0 100 32" preserveAspectRatio="none" role="img" aria-label="Robot success rate against demonstrations">
        <polyline points={demos} fill="none" stroke="var(--text-3)" strokeWidth="1" strokeDasharray="2 2" vectorEffect="non-scaling-stroke" />
        <polyline points={rate} fill="none" stroke="var(--accent)" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
      </svg>
      <div className="row muted small">
        <span style={{ color: 'var(--accent)' }}>— success</span>
        <span>- - demos</span>
      </div>
    </div>
  );
}

function RobotPanel({ data }: { data: SessionData }) {
  const verdicts = useVerdicts(data);
  const now = useNow(5000);
  const last = verdicts.at(-1);
  if (!last) return null;
  return (
    <Panel title="Robot learner" actions={<Pill tone={last.success ? 'ok' : 'warn'}>{last.success ? 'Reproduced' : 'Missed'}</Pill>}>
      <div className="stack">
        <div className="or-vitals">
          <Stat label="Step" value={humanize(last.stepId)} />
          <Stat label="Policy" value={last.rate != null ? `${Math.round(last.rate * 100)}%` : '—'} />
          <Stat label="Path error" value={last.pathErrorMm != null ? last.pathErrorMm.toFixed(1) : '—'} unit="mm" />
          <Stat label="Demos" value={`${last.human}+${last.synthetic}`} />
        </div>
        <Sparkline verdicts={verdicts} />
        <div className="muted small">Simulated robot, trained on headset + synthetic demos · {ago(last.atTs, now)}</div>
      </div>
    </Panel>
  );
}

export default function LiveOR({ sessionId }: { sessionId: string }) {
  const live = useLive();
  const data = useSession(sessionId);

  if (!data.session) {
    return (
      <main className="page page-narrow">
        <div style={{ maxWidth: 520, margin: '40px auto' }}>
          <Panel title="Live OR">
            {!live.ready ? (
              <div className="muted">Loading…</div>
            ) : (
              <div className="stack">
                <div>
                  You are not a member of <span className="mono">{sessionId}</span>. Open an invite link (/join/CODE) first.
                </div>
                <Link to="/" className="btn">
                  Back to start
                </Link>
              </div>
            )}
          </Panel>
        </div>
      </main>
    );
  }

  const online = data.members.filter(m => m.online);
  return (
    <main className="page">
      <div className="row" style={{ marginBottom: 16 }}>
        <div>
          <h1 style={{ fontSize: 40, marginBottom: 6 }}>Live OR</h1>
          <div className="muted small">
            {data.session.label} · one shared SpacetimeDB session · {online.length} connected
          </div>
        </div>
        <div className="spacer" />
        <Link to={`/s/${sessionId}`} className="btn sm">
          Full session
        </Link>
        {[...data.roles].map(r => (
          <span className="chip" key={r}>
            {r}
          </span>
        ))}
      </div>
      <div className="or-grid">
        <div className="col">
          <PatientPanel data={data} />
          <RobotPanel data={data} />
        </div>
        <div className="col">
          <TranscriptPanel data={data} />
        </div>
        <div className="col">
          <TrayPanel data={data} />
          <ActivityPanel data={data} />
        </div>
      </div>
    </main>
  );
}
