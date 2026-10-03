import { useEffect, useRef } from 'react';
import type { SessionData } from '../data/live';
import { clock } from '../lib/format';
import { Empty, Panel, StatusPill } from './ui';

export default function Coach({ data }: { data: SessionData }) {
  const { coachMessages, coachStatus, currentAttempt } = data;
  const ref = useRef<HTMLDivElement>(null);
  const shown = coachMessages.filter(m => !currentAttempt || m.attemptId === currentAttempt.attemptId);

  useEffect(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [shown.length]);

  return (
    <Panel
      title="Jarvis"
      flush
      actions={coachStatus ? <StatusPill status={coachStatus.status} label={`Coach ${coachStatus.status}`} /> : null}
    >
      {coachStatus?.detail && <div className="panel-body small muted">{coachStatus.detail}</div>}
      {shown.length === 0 ? (
        <Empty>Nothing said yet in this attempt. The transcript appears as Jarvis talks with the learner.</Empty>
      ) : (
        <div className="transcript" ref={ref} aria-live="polite">
          {shown.map(m => (
            <div key={String(m.messageId)} className={`bubble ${m.speaker}`}>
              {m.speaker !== 'system' && (
                <span className="who">
                  {m.speaker === 'coach' ? 'Jarvis' : 'Learner'} · {clock(m.at)}
                </span>
              )}
              {m.text}
            </div>
          ))}
        </div>
      )}
    </Panel>
  );
}
