// Synthetic headset: publishes exercise state, events and command
// acknowledgements exactly as the Quest app would, so the companion, Scalpal
// bridge and motion pipeline can be exercised without the headset. Everything
// it produces is labelled synthetic.

import { useEffect, useRef, useState } from 'react';
import { MODES } from '../components/ExerciseState';
import { Panel, StatusPill, useAction, toast } from '../components/ui';
import { useLive, useSession } from '../data/live';
import { humanize } from '../lib/format';
import { joinWithCode, uploadFile } from '../lib/grants';
import { Link } from '../lib/router';

type Draft = {
  mode: string;
  stepId: string;
  stepIndex: number;
  stepCount: number;
  selectedStructureId: string;
  highlightedStructureId: string;
  previewRotating: boolean;
  paused: boolean;
  registration: string;
  registrationReason: string;
  recording: string;
};

const STEPS = ['locate-target', 'grasp-instrument', 'transfer-to-target', 'avoid-restricted-region', 'release'];

/** Five seconds of moving shapes recorded from a canvas: a nonpersonal test clip. */
async function makeTestClip(): Promise<File> {
  const canvas = document.createElement('canvas');
  canvas.width = 640;
  canvas.height = 480;
  const g = canvas.getContext('2d')!;
  const stream = canvas.captureStream(30);
  const type = MediaRecorder.isTypeSupported('video/webm;codecs=vp9') ? 'video/webm;codecs=vp9' : 'video/webm';
  const rec = new MediaRecorder(stream, { mimeType: type });
  const chunks: Blob[] = [];
  rec.ondataavailable = e => e.data.size && chunks.push(e.data);
  const done = new Promise<void>(r => (rec.onstop = () => r()));
  rec.start(250);
  const start = performance.now();
  await new Promise<void>(resolve => {
    const draw = () => {
      const t = (performance.now() - start) / 1000;
      g.fillStyle = '#000000';
      g.fillRect(0, 0, 640, 480);
      g.fillStyle = '#8ef08a';
      g.beginPath();
      g.arc(320 + Math.cos(t * 2) * 160, 240 + Math.sin(t * 3) * 120, 30, 0, Math.PI * 2);
      g.fill();
      g.fillStyle = '#ffffff';
      g.font = '20px monospace';
      g.fillText(`SYNTHETIC TEST CLIP  t=${t.toFixed(2)}s`, 20, 36);
      if (t < 5) requestAnimationFrame(draw);
      else resolve();
    };
    draw();
  });
  rec.stop();
  await done;
  stream.getTracks().forEach(tr => tr.stop());
  return new File(chunks, `synthetic-clip-${Date.now()}.webm`, { type: 'video/webm' });
}

export default function HeadsetSimulator({ sessionId }: { sessionId: string }) {
  const live = useLive();
  const data = useSession(sessionId);
  const act = useAction();
  const [autoApply, setAutoApply] = useState(true);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [eventKind, setEventKind] = useState('step_completed');
  const [eventMsg, setEventMsg] = useState('');
  const [line, setLine] = useState('');
  const [speaker, setSpeaker] = useState('coach');
  const [scriptRunning, setScriptRunning] = useState(false);
  const scriptStop = useRef(false);
  const handled = useRef(new Set<string>());

  const st = data.state;
  const conn = live.conn;
  const headsetInvite = data.invites.find(i => i.role === 'headset');

  // Seed the editable draft from the published state.
  useEffect(() => {
    if (st && !draft) {
      setDraft({
        mode: st.mode,
        stepId: st.stepId ?? '',
        stepIndex: st.stepIndex,
        stepCount: st.stepCount || STEPS.length,
        selectedStructureId: st.selectedStructureId ?? '',
        highlightedStructureId: st.highlightedStructureId ?? '',
        previewRotating: st.previewRotating,
        paused: st.paused,
        registration: st.registration,
        registrationReason: st.registrationReason ?? '',
        recording: st.recording,
      });
    }
  }, [st, draft]);

  const publish = async (d: Draft) => {
    if (!conn || !st) return;
    await conn.reducers.publishExerciseState({
      sessionId,
      attemptId: data.session!.currentAttemptId,
      mode: d.mode,
      stepId: d.stepId || undefined,
      stepIndex: d.stepIndex,
      stepCount: d.stepCount,
      selectedStructureId: d.selectedStructureId || undefined,
      clearSelectedStructure: !d.selectedStructureId,
      highlightedStructureId: d.highlightedStructureId || undefined,
      clearHighlightedStructure: !d.highlightedStructureId,
      previewRotating: d.previewRotating,
      paused: d.paused,
      registration: d.registration,
      registrationReason: d.registrationReason || undefined,
      recording: d.recording,
    });
  };

  const update = (patch: Partial<Draft>) => {
    if (!draft) return;
    const next = { ...draft, ...patch };
    setDraft(next);
    act.run(() => publish(next));
  };

  const event = (kind: string, message: string, extra: { stepId?: string; structureId?: string } = {}) =>
    conn!.reducers.appendExerciseEvent({
      sessionId,
      attemptId: data.session!.currentAttemptId,
      kind,
      stepId: extra.stepId,
      structureId: extra.structureId,
      message: `[synthetic] ${message}`,
      deviceTimeMs: performance.now(),
    });

  // Apply pending commands like the Quest app would.
  useEffect(() => {
    if (!autoApply || !conn || !draft || !data.isHeadset) return;
    for (const c of data.commands) {
      if (c.status !== 'pending' || handled.current.has(c.commandId)) continue;
      handled.current.add(c.commandId);
      const next = { ...draft };
      let ok = true;
      let reason: string | undefined;
      switch (c.action) {
        case 'highlightStructure':
          next.highlightedStructureId = c.targetId ?? '';
          break;
        case 'isolateStructure':
          next.selectedStructureId = c.targetId ?? '';
          break;
        case 'restoreContext':
          next.selectedStructureId = '';
          next.highlightedStructureId = '';
          break;
        case 'rotatePreview':
          next.previewRotating = Boolean(c.argBool);
          break;
        case 'pausePractice':
          next.paused = true;
          break;
        case 'resumePractice':
          next.paused = false;
          break;
        case 'previewExercise':
          next.mode = 'Selecting';
          break;
        case 'confirmExercise':
          next.mode = 'Confirmed';
          break;
        case 'requestHint':
          if (next.mode !== 'Practicing') {
            ok = false;
            reason = 'hints are only available while practicing';
          }
          break;
        case 'zoomPreview':
          if (next.mode !== 'Selecting') {
            ok = false;
            reason = 'preview is not shown';
          }
          break;
      }
      (async () => {
        if (ok) {
          setDraft(next);
          await publish(next);
          if (c.action === 'requestHint') await event('hint', 'Hint shown for the current step', { stepId: next.stepId });
        }
        await conn.reducers.resolveCommand({ commandId: c.commandId, status: ok ? 'applied' : 'rejected', reason });
      })().catch(err => console.warn('auto-apply failed', err));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data.commands, autoApply, data.isHeadset]);

  const sleep = (ms: number) =>
    new Promise<void>((resolve, reject) =>
      setTimeout(() => (scriptStop.current ? reject(new Error('script stopped')) : resolve()), ms)
    );

  const runScript = () =>
    act.run(async () => {
      if (!conn || !draft) return;
      scriptStop.current = false;
      setScriptRunning(true);
      const say = (speaker: string, text: string) => conn.reducers.postCoachMessage({ sessionId, speaker, text });
      let d: Draft = { ...draft, stepCount: STEPS.length };
      const step = async (patch: Partial<Draft>, wait = 1800) => {
        d = { ...d, ...patch };
        setDraft(d);
        await publish(d);
        await sleep(wait);
      };
      try {
        await step({ mode: 'Selecting', previewRotating: true, stepId: '', stepIndex: 0 }, 400);
        await say('learner', 'I want to practice something involving the abdomen.');
        await sleep(1200);
        await say('coach', '[synthetic] Let\'s try instrument transfer near the gallbladder. I\'ll show you the anatomy first.');
        await step({ selectedStructureId: 'gallbladder', highlightedStructureId: 'gallbladder' });
        await say('learner', "Let's do this one.");
        await step({ mode: 'Confirmed', previewRotating: false });
        await step({ mode: 'Fitting', registration: 'uncertain', registrationReason: 'shoulders not visible' });
        await step({ registration: 'valid', registrationReason: '' });
        await step({ mode: 'Practicing', recording: 'recording', stepId: STEPS[0], stepIndex: 0 }, 600);
        for (let i = 0; i < STEPS.length; i++) {
          await step({ stepId: STEPS[i], stepIndex: i }, 300);
          await event('step_started', `Started ${humanize(STEPS[i])}`, { stepId: STEPS[i] });
          if (i === 2) {
            await step({ registration: 'uncertain', registrationReason: 'participant moved' }, 1200);
            await event('registration_lost', 'Anatomy fit uncertain; assessment paused', { stepId: STEPS[i] });
            await step({ registration: 'valid', registrationReason: '' }, 800);
          }
          if (i === 3) await event('mistake', 'Instrument entered the restricted region', { stepId: STEPS[i], structureId: 'common-bile-duct' });
          await sleep(1200);
          await event('step_completed', `Completed ${humanize(STEPS[i])}`, { stepId: STEPS[i] });
        }
        await step({ mode: 'Reviewing', recording: 'complete', stepIndex: STEPS.length, stepId: '' });
        await conn.reducers.setAttemptResult({
          attemptId: data.session!.currentAttemptId,
          practiceStatus: 'completed',
          stepsCompleted: STEPS.length,
          stepsTotal: STEPS.length,
          mistakes: 1,
          hintsUsed: 0,
          resultSummary: '[synthetic] Completed all steps; one contact with the restricted region during transfer.',
        });
        await say('coach', '[synthetic] Nice work. Watch your path near the bile duct during transfer.');
        toast('Script finished');
      } finally {
        setScriptRunning(false);
      }
    });

  if (!data.session) {
    return (
      <main className="page page-narrow">
        <Panel title="Synthetic headset">
          <div className="muted">Session not found or not joined.</div>
        </Panel>
      </main>
    );
  }

  return (
    <main className="page page-narrow">
      <div className="stack">
        <div className="row">
          <Link to={`/s/${sessionId}`}>← Back to session</Link>
        </div>
        <div className="synthetic-banner">
          SYNTHETIC HEADSET: this page imitates the Quest app for testing. Nothing here comes from the physical headset.
        </div>

        {!data.isHeadset ? (
          <Panel title="Take the headset role">
            <div className="stack">
              <div>To publish exercise state, this browser identity needs the headset role in the session.</div>
              <button
                className="btn primary"
                disabled={!headsetInvite || act.busy}
                onClick={() => act.run(() => joinWithCode(conn!, headsetInvite!.code, 'Synthetic headset'))}
              >
                Join as synthetic headset
              </button>
              {!headsetInvite && <div className="muted small">Only operators can see the headset invite code.</div>}
            </div>
          </Panel>
        ) : (
          draft && (
            <>
              <Panel
                title="Exercise state"
                actions={
                  <div className="row">
                    <button className="btn sm primary" disabled={scriptRunning} onClick={runScript}>
                      Run demo script
                    </button>
                    {scriptRunning && (
                      <button className="btn sm" onClick={() => (scriptStop.current = true)}>
                        Stop
                      </button>
                    )}
                  </div>
                }
              >
                <div className="stack">
                  <div className="two">
                    <label className="field">
                      Mode
                      <select value={draft.mode} onChange={e => update({ mode: e.target.value })}>
                        {MODES.map(m => (
                          <option key={m}>{m}</option>
                        ))}
                      </select>
                    </label>
                    <label className="field">
                      Step
                      <select
                        value={draft.stepId}
                        onChange={e => update({ stepId: e.target.value, stepIndex: Math.max(0, STEPS.indexOf(e.target.value)) })}
                      >
                        <option value="">(none)</option>
                        {STEPS.map(s => (
                          <option key={s} value={s}>
                            {humanize(s)}
                          </option>
                        ))}
                      </select>
                    </label>
                    <label className="field">
                      Registration
                      <select value={draft.registration} onChange={e => update({ registration: e.target.value })}>
                        <option>unaligned</option>
                        <option>uncertain</option>
                        <option>valid</option>
                      </select>
                    </label>
                    <label className="field">
                      Registration reason
                      <input value={draft.registrationReason} onChange={e => setDraft({ ...draft, registrationReason: e.target.value })} onBlur={() => update({})} />
                    </label>
                    <label className="field">
                      Recording
                      <select value={draft.recording} onChange={e => update({ recording: e.target.value })}>
                        <option>off</option>
                        <option>recording</option>
                        <option>complete</option>
                        <option>failed</option>
                      </select>
                    </label>
                    <label className="field">
                      Selected structure
                      <input value={draft.selectedStructureId} onChange={e => setDraft({ ...draft, selectedStructureId: e.target.value })} onBlur={() => update({})} placeholder="gallbladder" />
                    </label>
                  </div>
                  <div className="row">
                    <label className="row small" style={{ gap: 6 }}>
                      <input type="checkbox" checked={draft.paused} onChange={e => update({ paused: e.target.checked })} /> paused
                    </label>
                    <label className="row small" style={{ gap: 6 }}>
                      <input type="checkbox" checked={draft.previewRotating} onChange={e => update({ previewRotating: e.target.checked })} /> preview rotating
                    </label>
                    <label className="row small" style={{ gap: 6 }}>
                      <input type="checkbox" checked={autoApply} onChange={e => setAutoApply(e.target.checked)} /> auto-apply app actions
                    </label>
                  </div>
                </div>
              </Panel>

              <div className="two">
                <Panel title="Emit event">
                  <form
                    className="stack"
                    onSubmit={e => {
                      e.preventDefault();
                      act.run(() => event(eventKind, eventMsg || humanize(eventKind), { stepId: draft.stepId || undefined }));
                      setEventMsg('');
                    }}
                  >
                    <select value={eventKind} onChange={e => setEventKind(e.target.value)}>
                      {['step_started', 'step_completed', 'mistake', 'hint', 'registration_lost'].map(k => (
                        <option key={k} value={k}>
                          {humanize(k)}
                        </option>
                      ))}
                    </select>
                    <input value={eventMsg} onChange={e => setEventMsg(e.target.value)} placeholder="message" />
                    <button className="btn sm">Emit</button>
                  </form>
                </Panel>
                <Panel title="Transcript line">
                  <form
                    className="stack"
                    onSubmit={e => {
                      e.preventDefault();
                      if (!line.trim()) return;
                      act.run(() => conn!.reducers.postCoachMessage({ sessionId, speaker, text: line }));
                      setLine('');
                    }}
                  >
                    <select value={speaker} onChange={e => setSpeaker(e.target.value)}>
                      <option value="coach">Scalpal</option>
                      <option value="learner">Learner</option>
                      <option value="system">System</option>
                    </select>
                    <input value={line} onChange={e => setLine(e.target.value)} placeholder="what was said" />
                    <button className="btn sm">Post</button>
                  </form>
                </Panel>
              </div>

              <Panel title="Recording">
                <div className="row">
                  <button
                    className="btn"
                    disabled={act.busy}
                    onClick={() =>
                      act.run(async () => {
                        const file = await makeTestClip();
                        await uploadFile(conn!, {
                          sessionId,
                          attemptId: data.session!.currentAttemptId,
                          kind: 'raw_clip',
                          file,
                        });
                        toast('Synthetic clip uploaded');
                      })
                    }
                  >
                    Record and upload a 5 s synthetic clip
                  </button>
                  <span className="muted small">Moving shapes only; no camera or people.</span>
                </div>
              </Panel>

              <Panel title="Pending app actions" flush>
                <ul className="list">
                  {data.commands.filter(c => c.status === 'pending').length === 0 && <li className="muted">None pending.</li>}
                  {data.commands
                    .filter(c => c.status === 'pending')
                    .map(c => (
                      <li key={c.commandId} className="row">
                        <span>
                          {humanize(c.action)} {c.targetId ? `· ${c.targetId}` : ''}
                        </span>
                        <StatusPill status={c.status} />
                        <div className="spacer" />
                        {['applied', 'rejected', 'unavailable'].map(s => (
                          <button
                            key={s}
                            className="btn sm"
                            onClick={() => act.run(() => conn!.reducers.resolveCommand({ commandId: c.commandId, status: s, reason: s === 'applied' ? undefined : 'synthetic headset' }))}
                          >
                            {s}
                          </button>
                        ))}
                      </li>
                    ))}
                </ul>
              </Panel>
            </>
          )
        )}
        {act.error && <div className="error-text">{act.error}</div>}
      </div>
    </main>
  );
}
