import type { SessionData } from '../data/live';
import { useNow } from '../data/live';
import { ago, humanize } from '../lib/format';
import { Panel, Pill, Progress, StatusPill } from './ui';

export const MODES = [
  'Startup',
  'Selecting',
  'Confirmed',
  'Fitting',
  'Practicing',
  'Reviewing',
  'ProcessingMotion',
  'RobotReplay',
  'Recap',
] as const;

const MODE_LABEL: Record<string, string> = {
  Startup: 'Startup',
  Selecting: 'Selecting',
  Confirmed: 'Confirmed',
  Fitting: 'Fitting',
  Practicing: 'Practicing',
  Reviewing: 'Review',
  ProcessingMotion: 'Processing',
  RobotReplay: 'Replay',
  Recap: 'Recap',
};

const REGISTRATION_LABEL: Record<string, string> = {
  valid: 'Anatomy fit valid',
  uncertain: 'Fit uncertain',
  unaligned: 'Not aligned',
};

export default function ExerciseState({ data }: { data: SessionData }) {
  const now = useNow(5000);
  const st = data.state;
  if (!st) return null;
  const idx = MODES.indexOf(st.mode as (typeof MODES)[number]);
  const stepFraction = st.stepCount > 0 ? Math.min(st.stepIndex / st.stepCount, 1) : 0;
  const assessing = st.mode === 'Practicing';
  const blocked = assessing && st.registration !== 'valid';
  const stale = now - Number(st.updatedAt.microsSinceUnixEpoch / 1000n) > 60_000;

  return (
    <Panel
      title="Exercise"
      actions={
        <span className="muted small" title="Last state published by the headset">
          updated {ago(st.updatedAt, now)}
        </span>
      }
    >
      <div className="stack">
        <div className="stepper" aria-label="Session mode">
          {MODES.map((m, i) => (
            <div key={m} className={`s ${i < idx ? 'done' : ''} ${i === idx ? 'current' : ''}`}>
              {MODE_LABEL[m]}
            </div>
          ))}
        </div>

        <div className="big-step">
          <span className="label">{st.stepId ? humanize(st.stepId) : MODE_LABEL[st.mode] ?? st.mode}</span>
          {st.stepCount > 0 && (
            <span className="muted">
              step {Math.min(st.stepIndex + 1, st.stepCount)} of {st.stepCount}
            </span>
          )}
          {st.paused && <Pill tone="warn">Paused</Pill>}
        </div>
        {st.stepCount > 0 && <Progress value={stepFraction} />}

        <div className="row">
          <Pill tone={st.registration === 'valid' ? 'ok' : st.registration === 'uncertain' ? 'warn' : 'muted'}>
            {REGISTRATION_LABEL[st.registration] ?? st.registration}
          </Pill>
          <StatusPill status={st.recording} label={st.recording === 'off' ? 'Not recording' : `Recording ${st.recording === 'recording' ? '' : st.recording}`.trim()} />
          {st.previewRotating && st.mode === 'Selecting' && <Pill tone="info">Preview rotating</Pill>}
        </div>

        {blocked && (
          <div className="notice warn">
            Assessment is paused while the anatomy fit is {st.registration}
            {st.registrationReason ? `: ${st.registrationReason}` : '.'}
          </div>
        )}
        {stale && data.session?.status === 'active' && (
          <div className="notice info">No state update from the headset in over a minute.</div>
        )}

        <dl className="kv">
          <dt>Exercise</dt>
          <dd>
            {st.exerciseId} <span className="muted">@{st.exerciseVersion}</span>
          </dd>
          <dt>Selected structure</dt>
          <dd>{st.selectedStructureId ? humanize(st.selectedStructureId) : <span className="muted">none</span>}</dd>
          <dt>Highlighted</dt>
          <dd>{st.highlightedStructureId ? humanize(st.highlightedStructureId) : <span className="muted">none</span>}</dd>
          <dt>Attempt</dt>
          <dd>
            #{data.currentAttempt?.ordinal ?? '—'} <span className="muted mono small">{st.attemptId}</span>
          </dd>
          <dt>Versions</dt>
          <dd className="mono small">
            state v{String(st.stateVersion)} · step v{String(st.stepVersion)}
          </dd>
        </dl>
      </div>
    </Panel>
  );
}
