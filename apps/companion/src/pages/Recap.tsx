import { useEffect, useRef, useState } from 'react';
import { Link } from '../lib/router';
import { errorMarkers, feedbackFromFacts, highlightWindow, parseRunResult, type RunResult } from '../recap/runResult';
import { initialResult } from '../recap/sample';
import '../recap/recap.css';
import { acceptReplayView, loadedReplayMetadata, playbackRefresh, secureGatewayBase, type ReplayView } from '../recap/replay';
import { TOKEN_KEY } from '../config';
import { load } from '../lib/storage';

export default function Recap({ sessionId }: { sessionId?: string }) {
  const [result, setResult] = useState<RunResult | null>(() => initialResult(!sessionId && new URLSearchParams(location.search).get('demo') === 'sample'));
  const [error, setError] = useState('');
  const [stage, setStage] = useState(0);
  const [fallback, setFallback] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [seconds, setSeconds] = useState(0);
  const [mediaError, setMediaError] = useState('');
  const [view, setView] = useState<ReplayView | null>(null);
  const [refresh, setRefresh] = useState(0);
  const viewRef = useRef<ReplayView | null>(null);
  const restore = useRef(0);
  const resume = useRef(false);
  const source = useRef<HTMLVideoElement>(null);
  const robot = useRef<HTMLVideoElement>(null);
  const feedback = result && feedbackFromFacts(result);
  const replay = result?.replay;
  const shownSource = fallback ? 'sample' : replay?.source;
  const hasVideo = fallback || replay?.status === 'ready';
  const window = result && !fallback ? highlightWindow(result) : { start: 0, end: 20 };
  const limit = window.end;
  const markers = result && !fallback ? errorMarkers(result).filter(f => f.clipSeconds >= window.start && f.clipSeconds <= limit) : [];
  const sampleVideo = fallback || replay?.source === 'sample' && !replay.jobId;

  useEffect(() => {
    if (!result?.replay.jobId || fallback) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;
    let polls = 0;
    async function resolve() {
      try {
        const token = load(TOKEN_KEY);
        if (!token) throw new Error('Connect to the session to resolve protected replay artifacts.');
        const base = secureGatewayBase(import.meta.env.VITE_API_URL ?? 'http://127.0.0.1:8788');
        const response = await fetch(`${base}/v1/sessions/${encodeURIComponent(result!.sessionId)}/replay/${encodeURIComponent(result!.replay.jobId)}`, { headers: { Authorization: `Bearer ${token}` } });
        if (!response.ok) throw new Error(`Replay resolution failed (${response.status}).`);
        const next = acceptReplayView(result!, await response.json());
        if (cancelled) return;
        const decision = playbackRefresh(viewRef.current, next, robot.current?.currentTime ?? restore.current, refresh > 0);
        if (decision.replace) {
          restore.current = decision.position || highlightWindow(result!).start;
          viewRef.current = next; setView(next);
        }
        setResult(current => current && ({ ...current, replay: { ...current.replay, status: next.status, failureReason: next.reason,
          source: next.source, sourceArtifactId: next.sourceArtifactId, replayArtifactId: next.replayArtifactId, jobRun: next.jobRun } }));
        setMediaError('');
        if (next.status === 'queued' || next.status === 'processing') timer = setTimeout(resolve, polls++ === 0 ? 3000 : 10000);
      } catch (e) { if (!cancelled) { stop(); setMediaError(e instanceof Error ? e.message : 'Unable to resolve replay.'); } }
    }
    void resolve();
    return () => { cancelled = true; clearTimeout(timer); };
    // The run/attempt owns a polling loop. Status changes do not restart it.
  }, [result?.runId, result?.attemptId, result?.replay.jobId, fallback, refresh]);

  function refreshMedia() {
    restore.current = robot.current?.currentTime ?? seconds; resume.current = playing; stop();
    if (!sampleVideo && refresh < 2) setRefresh(n => n + 1);
    else setMediaError('Replay could not load. Scores remain available; try the labeled sample fallback.');
  }

  function stop() { source.current?.pause(); robot.current?.pause(); setPlaying(false); }
  function seek(time: number) {
    const clipped = Math.max(window.start, Math.min(limit, time));
    if (source.current) source.current.currentTime = clipped;
    if (robot.current) robot.current.currentTime = clipped;
    setSeconds(clipped);
  }
  async function startPlayers() {
    try {
      await Promise.all([robot.current?.play(), source.current?.play()]);
      setPlaying(true);
    } catch { stop(); setMediaError('Video could not start. Check that the artifact URL is accessible and has not expired.'); }
  }
  function robotMetadata(video: HTMLVideoElement) {
    try {
      if (!result) return;
      const loaded = sampleVideo ? null : loadedReplayMetadata(result, video.duration, restore.current);
      if (loaded) setResult(loaded.result);
      const position = loaded?.position ?? 0;
      restore.current = position; video.currentTime = position;
      if (source.current?.readyState) source.current.currentTime = position;
      setSeconds(position);
      // Metadata changes the highlight bounds. Resume directly at the computed
      // position instead of calling play() with the previous zero-length window.
      if (resume.current) { resume.current = false; void startPlayers(); }
    } catch (e) { stop(); setMediaError(e instanceof Error ? e.message : 'Replay metadata is unavailable.'); }
  }
  async function play() {
    if (playing) { stop(); return; }
    if (!sampleVideo && view && view.expiresAtUnixMs <= Date.now()) { resume.current = true; restore.current = seconds; setRefresh(n => n + 1); return; }
    if (seconds >= limit || seconds < window.start) seek(window.start);
    await startPlayers();
  }
  async function read(file?: File) {
    if (!file) return;
    try {
      if (file.size > 262_144) throw new Error('RunResult must be smaller than 256 KiB.');
      const next = parseRunResult(await file.text(), sessionId);
      stop(); viewRef.current = null; setView(null); setRefresh(0); restore.current = highlightWindow(next).start; setResult(next); setStage(0); setFallback(false); setSeconds(0); setError(''); setMediaError('');
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to read result.'); }
  }
  function useSample() { stop(); restore.current = 0; resume.current = false; setFallback(true); setSeconds(0); setMediaError(''); }

  return <main className="recap-page">
    <div className="recap-flower" aria-hidden="true">✳</div>
    <div className="recap-top"><div><span className="recap-eyebrow">SCALPAL / RUN REFLECTION</span><h1>A moment to look back.</h1>
      <p>Two kinds of learning. One next step.</p></div>
      <label className="recap-button">Import RunResult JSON<input aria-label="Import RunResult JSON" type="file" accept=".json,application/json" onChange={e => { void read(e.target.files?.[0]); e.target.value = ''; }} /></label>
    </div>
    <div className="recap-notice">{result?.isSample ? 'SAMPLE RUN · Authored scorecard fixture and illustrative animation. No learner was assessed.' : result ? 'IMPORTED RUN · Displaying supplied grader facts; this view does not calculate grades.' : 'NO RESULT · Import a run result or explicitly select the sample demo.'}
      <span> Live RunResult delivery is not connected. Import the exported result from the run.</span></div>
    {error && <p role="alert" className="recap-error">{error}</p>}
    {!result ? <section className="recap-glass"><h2>No result for this run</h2><p>Import a result for session <code>{sessionId}</code> to show the same diagnosis, surgery grade and replay as the headset.</p><a href="/recap?demo=sample">Select sample demo</a></section> : <>
      <div className="recap-meta"><span>{result.patientId} / {result.procedureId}</span><span>{result.demo.enabled ? `Demo mode · ${Math.min(20, result.demo.replayHighlightSeconds)} s highlight` : 'Full replay'} · attempt {result.attemptId}</span></div>
      <section className="recap-glass recap-reflect">
        <span className="recap-eyebrow">{stage === 0 ? '01 / REACTION' : stage === 1 ? '02 / SELF-ASSESSMENT' : '03 / YOUR TAKE-AWAY'}</span>
        <h2>{stage === 0 ? 'How did that feel?' : stage === 1 ? 'One thing you’d do differently?' : feedback?.takeaway}</h2>
        <p>{stage < 2 ? 'Pause and reflect. This response is unscored and is not recorded by the companion.' : 'Based only on the logged facts below.'}</p>
        {stage < 2 && <button className="recap-button" onClick={() => setStage(stage + 1)}>{stage === 0 ? 'Continue reflection' : 'Reveal scorecards'} <span aria-hidden="true">→</span></button>}
      </section>
      {stage === 2 && <>
        <div className="recap-cards">
          <section className="recap-glass"><span className="recap-eyebrow">CLINICAL REASONING</span><h2>How you understood the case</h2>{result.demo.enabled && <p>Demo-assisted run · suggested questions may have been available.</p>}
            {result.diagnosis ? <><div className="recap-score">{result.diagnosis.total}<small> / {result.diagnosis.max}</small><span>{result.diagnosis.grade}</span></div>
              <p>Diagnosis: {result.diagnosis.diagnosisGiven || 'Not given'} · {result.diagnosis.diagnosisResult}</p>
              <p>Expected: {result.diagnosis.diagnosisExpected}. Procedure choice {result.diagnosis.procedureChosenCorrectly ? 'correct' : 'incorrect'}.</p>
              <dl>{result.diagnosis.sections.map(s => <div key={s.id}><dt>{s.label}</dt><dd>{s.score} / {s.max}</dd></div>)}</dl>
              <FactList title="Critical items covered" items={result.diagnosis.criticalFound.map(f => f.label)} />
              <FactList title="Critical items missed" items={result.diagnosis.criticalMissed.map(f => f.label)} />
            </> : <p>Encounter scorecard unavailable. No clinical score has been inferred.</p>}
          </section>
          <section className="recap-glass"><span className="recap-eyebrow">PROCEDURAL SKILL</span><h2>How you worked with your hands</h2>{result.surgery.demoAssisted && <p>Demo-assisted grade · assistance was fixed when this run started.</p>}
            {result.surgery.available ? <><div className="recap-score">{result.surgery.total}<small> / {result.surgery.max}</small><span>{result.surgery.grade}</span></div>
              <dl><div><dt>Milestones reached</dt><dd>{result.surgery.milestones.length}</dd></div><div><dt>Guardrail violations</dt><dd>{result.surgery.guardrailViolations.length}</dd></div>
                <div><dt>Blood loss</dt><dd>{result.surgery.bloodLossMl} mL</dd></div><div><dt>Decisions correct</dt><dd>{result.surgery.decisions.filter(d => d.correct).length} / {result.surgery.decisions.length}</dd></div>
                <div><dt>Hints</dt><dd>{result.surgery.hints.length}</dd></div><div><dt>Hand travel / time</dt><dd>{result.surgery.economy.available ? `${(result.surgery.economy.leftPathMeters + result.surgery.economy.rightPathMeters).toFixed(2)} m / ${result.surgery.economy.durationSeconds.toFixed(0)} s` : 'Not measured'}</dd></div></dl>
              <FactList title="Milestones" items={result.surgery.milestones.map(f => `${f.label} · ${f.atSeconds.toFixed(1)} s`)} />
              <FactList title="Guardrail violations" items={result.surgery.guardrailViolations.map(f => `${f.label} · ${f.atSeconds.toFixed(1)} s`)} />
            </> : <p>Surgery grade unavailable. No procedural score has been inferred.</p>}
          </section>
        </div>
        <section className="recap-glass recap-feedback"><FactList title="Keep building on" items={feedback!.strengths} /><FactList title="Next time, focus on" items={feedback!.improvements} /></section>
      </>}
      <section className="recap-glass recap-replay">
        <div className="recap-replay-heading"><div><span className="recap-eyebrow">ROBOT REPLAY</span><h2>See the motion again.</h2></div><span className={`recap-status status-${replay!.status}`}>{replay!.status}</span></div>
        <p>{shownSource === 'learner' ? 'Your hand motion, retargeted to a robot hand. Kinematic replay, not a trained robot.' : shownSource === 'unknown' ? 'Replay source is unverified. Learner playback is unavailable.' : shownSource === 'rehearsal' ? 'Rehearsal hand motion, retargeted to a robot hand. Kinematic replay, not a trained robot. Not this learner’s run.' : 'Sample robot-hand animation. Illustrative only; not derived from this learner’s motion and not a trained robot.'}</p>
        {replay!.status !== 'ready' && <p className={replay!.status === 'failed' ? 'recap-error' : ''}>{replay!.status === 'failed' ? `Processing failed: ${replay!.failureReason}` : replay!.status === 'queued' ? 'Your recording is queued. The learner replay is not ready yet.' : 'Your recording is being processed. The learner replay is not ready yet.'}</p>}
        {hasVideo && <><div className="recap-videos">
          <figure><figcaption>{shownSource === 'learner' ? 'Learner’s recorded segment' : shownSource === 'rehearsal' ? 'Rehearsal recording' : 'Source recording'}</figcaption>
            {!sampleVideo && view?.sourceVideoUrl ? <video ref={source} src={view!.sourceVideoUrl} onLoadedMetadata={e => { e.currentTarget.currentTime = restore.current; }} muted playsInline preload="metadata" onError={refreshMedia} /> : <div className="recap-video-placeholder">{shownSource === 'sample' ? 'No participant footage in this sample.' : 'No source recording URL supplied.'}</div>}</figure>
          <figure><figcaption>{fallback ? 'SAMPLE FALLBACK' : shownSource!.toUpperCase()} · robot-hand replay</figcaption><video key={sampleVideo ? 'sample' : replay!.replayArtifactId} ref={robot} src={sampleVideo ? '/recap-sample.mp4' : view?.replayVideoUrl} onLoadedMetadata={e => robotMetadata(e.currentTarget)} muted playsInline preload="auto"
            onTimeUpdate={e => { const t = e.currentTarget.currentTime; setSeconds(Math.min(t, limit)); if (t >= limit && limit > 0) { stop(); seek(limit); } else if (source.current && Math.abs(source.current.currentTime - t) > .3) source.current.currentTime = t; }}
            onEnded={stop} onError={refreshMedia} /></figure>
        </div><div className="recap-controls"><button className="recap-button" disabled={!!mediaError} onClick={() => void play()}>{playing ? 'Pause' : 'Play both'}</button>
          <label>Replay position <input type="range" min={window.start} max={limit || 1} step="0.1" value={seconds} onChange={e => seek(Number(e.target.value))} /></label><span>{seconds.toFixed(1)} / {limit.toFixed(1)} s</span></div>
          <div className="recap-markers">{markers.map((f, i) => <button key={`${f.id}-${i}`} className="recap-button" onClick={() => seek(f.clipSeconds)}>{f.clipSeconds.toFixed(1)} s · {f.label}</button>)}</div>
          <p className="recap-caption">{replay!.clockAligned && shownSource === 'learner' ? 'Markers use run-event time minus the capture start time.' : 'Error markers are hidden: this replay has no verified alignment to the learner’s run clock.'}</p>
        </>}
        {mediaError && <p role="alert" className="recap-error">{mediaError}</p>}
        {!fallback && (replay!.status !== 'ready' || mediaError) && <button className="recap-button" onClick={useSample}>Watch labeled sample fallback</button>}
        {fallback && <p className="recap-caption">Showing sample fallback. Actual job status remains {replay!.status}; this is not a successful learner replay.</p>}
      </section>
      <footer className="recap-footer"><span>Educational practice feedback · no combined score</span><Link to={sessionId ? `/s/${sessionId}` : '/'}>Back to companion →</Link></footer>
    </>}
  </main>;
}
function FactList({ title, items }: { title: string; items: string[] }) {
  return <div className="recap-facts"><h3>{title}</h3>{items.length ? <ul>{items.map((s, i) => <li key={i}>{s}</li>)}</ul> : <p className="recap-caption">None logged.</p>}</div>;
}
