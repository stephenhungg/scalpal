import { useEffect, useRef } from 'react';
import type { SessionData } from '../data/live';
import { clock, humanize } from '../lib/format';
import { Empty, Panel } from './ui';

const TONE: Record<string, string> = {
  step_completed: 'ok',
  motion_job_ready: 'ok',
  mistake: 'bad',
  motion_job_failed: 'bad',
  registration_lost: 'warn',
  hint: 'info',
  step_started: 'accent',
  attempt_started: 'accent',
};

export default function Events({ data }: { data: SessionData }) {
  const ref = useRef<HTMLUListElement>(null);
  const events = data.events.slice(-200);
  useEffect(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [events.length]);

  return (
    <Panel title="Timeline" flush>
      {events.length === 0 ? (
        <Empty>No events yet.</Empty>
      ) : (
        <ul className="timeline" ref={ref}>
          {events.map(e => (
            <li key={String(e.eventId)}>
              <span className="t">{clock(e.at)}</span>
              <span className={`mark ${TONE[e.kind] ?? ''}`} />
              <span>
                <span>{e.message || humanize(e.kind)}</span>
                {(e.stepId || e.structureId) && (
                  <span className="muted small">
                    {' '}
                    · {[e.stepId && humanize(e.stepId), e.structureId && humanize(e.structureId)].filter(Boolean).join(' · ')}
                  </span>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
