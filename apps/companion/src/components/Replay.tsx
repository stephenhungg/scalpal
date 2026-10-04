import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useLive, type SessionData } from '../data/live';
import { duration, humanize, toMs } from '../lib/format';
import { downloadUrl } from '../lib/grants';
import {
  durationMs,
  invalidSpans,
  jointRange,
  parseTrajectory,
  sample,
  startMs,
  TrajectoryError,
  type Trajectory,
} from '../lib/trajectory';
import { Empty, Panel, Pill } from './ui';

// Site palette first (green, white), then distinct hues so 20+ joints stay tellable apart.
const PALETTE = ['#8ef08a', '#ffffff', '#9ec5ff', '#f5c451', '#c7a6ff', '#5fd85d', '#ff9f80', '#7fe0ff', '#ff8fd6', '#e8ff7a'];

function Chart({ tr, positionMs }: { tr: Trajectory; positionMs: number }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const spans = useMemo(() => invalidSpans(tr), [tr]);
  const total = Math.max(durationMs(tr), 1);

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth;
    const h = canvas.clientHeight;
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    const g = canvas.getContext('2d')!;
    g.scale(dpr, dpr);
    g.clearRect(0, 0, w, h);
    const pad = 8;
    const x = (ms: number) => pad + ((w - 2 * pad) * ms) / total;
    const css = getComputedStyle(canvas);

    // Invalid spans: shaded, so gaps are visible rather than smoothed over.
    g.fillStyle = 'rgba(248,113,113,0.14)';
    for (const s of spans) g.fillRect(x(s.startMs), 0, Math.max(2, x(s.endMs) - x(s.startMs)), h);

    const t0 = tr.frames.t[0];
    const joints = tr.robot.joints.length;
    for (let j = 0; j < joints; j++) {
      const [lo, hi] = jointRange(tr, j);
      g.strokeStyle = PALETTE[j % PALETTE.length];
      g.globalAlpha = joints > 8 ? 0.7 : 0.9;
      g.lineWidth = 1.4;
      g.beginPath();
      let pen = false;
      for (let i = 0; i < tr.frames.t.length; i++) {
        const valid = tr.frames.valid ? tr.frames.valid[i] : true;
        if (!valid) {
          pen = false;
          continue;
        }
        const px = x(tr.frames.t[i] - t0);
        const py = h - pad - ((h - 2 * pad) * (tr.frames.q[i][j] - lo)) / (hi - lo || 1);
        if (pen) g.lineTo(px, py);
        else g.moveTo(px, py);
        pen = true;
      }
      g.stroke();
    }
    g.globalAlpha = 1;
    g.strokeStyle = css.getPropertyValue('--text') || '#fff';
    g.lineWidth = 1.5;
    g.beginPath();
    g.moveTo(x(positionMs), 0);
    g.lineTo(x(positionMs), h);
    g.stroke();
  }, [tr, positionMs, spans, total]);

  return <canvas ref={ref} className="chart" aria-label="Robot joint targets over time; shaded regions are invalid intervals" />;
}

function JointBars({ tr, positionMs }: { tr: Trajectory; positionMs: number }) {
  const { q, valid } = sample(tr, positionMs);
  return (
    <div className="joint-bars">
      {tr.robot.joints.map((joint, j) => {
        const [lo, hi] = jointRange(tr, j);
        const f = Math.max(0, Math.min(1, (q[j] - lo) / (hi - lo || 1)));
        return (
          <div key={joint.name} className={`joint-bar ${valid ? '' : 'invalid'}`} title={`${joint.name}: [${lo.toFixed(2)}, ${hi.toFixed(2)}] ${joint.unit}`}>
            <span className="name">
              <span style={{ color: PALETTE[j % PALETTE.length] }}>●</span> {joint.name}
            </span>
            <span className="track">
              <span className="fill" style={{ width: `${f * 100}%` }} />
            </span>
            <span className="val">{q[j].toFixed(2)}</span>
          </div>
        );
      })}
    </div>
  );
}

/** <video> slaved to an external clock. */
function SyncedVideo({ src, positionSec, playing, rate, label }: { src: string; positionSec: number; playing: boolean; rate: number; label: string }) {
  const ref = useRef<HTMLVideoElement>(null);
  useEffect(() => {
    const v = ref.current;
    if (!v || !Number.isFinite(positionSec)) return;
    if (Math.abs(v.currentTime - positionSec) > (playing ? 0.3 : 0.04)) v.currentTime = Math.max(0, positionSec);
    v.playbackRate = rate;
    if (playing && v.paused) v.play().catch(() => {});
    if (!playing && !v.paused) v.pause();
  }, [positionSec, playing, rate]);
  return (
    <figure style={{ margin: 0 }}>
      <video ref={ref} className="replay-video" src={src} muted playsInline preload="auto" />
      <figcaption className="muted small">{label}</figcaption>
    </figure>
  );
}

export default function Replay({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const ready = data.jobs.filter(j => j.status === 'ready');
  const shared = data.replay;
  const artifactById = useMemo(() => new Map(data.artifacts.map(a => [a.artifactId, a])), [data.artifacts]);

  const sharedJob = ready.find(j => shared?.artifactId && j.outputArtifactIds.includes(shared.artifactId));
  const [pickedJobId, setPickedJobId] = useState<string | null>(null);
  const [follow, setFollow] = useState(true);
  const job =
    (follow && sharedJob) ||
    ready.find(j => j.jobId === pickedJobId) ||
    [...ready].reverse().find(j => j.attemptId === data.currentAttempt?.attemptId) ||
    ready[ready.length - 1];

  const trajArt = job?.outputArtifactIds.map(id => artifactById.get(id)).find(a => a?.kind === 'robot_trajectory');
  const videoArt = job?.outputArtifactIds.map(id => artifactById.get(id)).find(a => a?.kind === 'replay_video');
  const sourceArt = job ? artifactById.get(job.inputArtifactId) : undefined;

  const [tr, setTr] = useState<Trajectory | null>(null);
  const [videoUrl, setVideoUrl] = useState<string | null>(null);
  const [sourceUrl, setSourceUrl] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!conn || !job) return;
    let cancelled = false;
    setTr(null);
    setVideoUrl(null);
    setSourceUrl(null);
    setLoadError(null);
    setLoading(true);
    (async () => {
      try {
        if (trajArt) {
          const res = await fetch(await downloadUrl(conn, trajArt.artifactId));
          if (!res.ok) throw new Error(`Could not download the trajectory (${res.status}).`);
          const parsed = parseTrajectory(await res.text());
          if (!cancelled) setTr(parsed);
        }
        if (videoArt) {
          const url = await downloadUrl(conn, videoArt.artifactId);
          if (!cancelled) setVideoUrl(url);
        }
        if (sourceArt?.status === 'available') {
          const url = await downloadUrl(conn, sourceArt.artifactId);
          if (!cancelled) setSourceUrl(url);
        }
      } catch (err: any) {
        if (!cancelled) setLoadError(err instanceof TrajectoryError ? err.message : String(err?.message ?? err));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
    // Only reload when the job changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conn, job?.jobId]);

  // ---- clock -------------------------------------------------------------
  const total = tr ? durationMs(tr) : 0;
  const [local, setLocal] = useState({ playing: false, positionMs: 0, rate: 1, at: performance.now() });
  const [, force] = useState(0);
  const sharedApplies = follow && shared && trajArt && shared.artifactId === trajArt.artifactId;

  const positionNow = useCallback((): { pos: number; playing: boolean; rate: number } => {
    if (sharedApplies && shared) {
      const elapsed = shared.playing ? (Date.now() - toMs(shared.updatedAt)) * shared.rate : 0;
      return { pos: Math.min(shared.positionMs + elapsed, total), playing: shared.playing && shared.positionMs + elapsed < total, rate: shared.rate };
    }
    const elapsed = local.playing ? (performance.now() - local.at) * local.rate : 0;
    return { pos: Math.min(local.positionMs + elapsed, total), playing: local.playing && local.positionMs + elapsed < total, rate: local.rate };
  }, [sharedApplies, shared, local, total]);

  const { pos, playing, rate } = positionNow();

  useEffect(() => {
    if (!playing) return;
    let raf = 0;
    const tick = () => {
      force(x => x + 1);
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [playing]);

  const canBroadcast = (data.isOperator || data.isHeadset) && data.session?.status === 'active';

  const control = (next: { playing: boolean; positionMs: number; rate?: number }) => {
    const r = next.rate ?? rate;
    if (canBroadcast && follow && conn && trajArt && data.session) {
      conn.reducers
        .setReplayState({
          sessionId: data.session.sessionId,
          artifactId: trajArt.artifactId,
          playing: next.playing,
          positionMs: Math.max(0, next.positionMs),
          rate: r,
        })
        .catch(err => console.warn('setReplayState failed', err));
    } else {
      setFollow(false);
    }
    setLocal({ playing: next.playing, positionMs: Math.max(0, next.positionMs), rate: r, at: performance.now() });
  };

  // ---- render ------------------------------------------------------------
  if (ready.length === 0) {
    return (
      <Panel title="Robot replay">
        <Empty>
          The replay appears here once a recorded clip has been processed. It shows the robot hand's joint targets derived
          from that recording, with invalid intervals marked.
        </Empty>
      </Panel>
    );
  }

  const clipClock = tr?.timebase.clock === 'clip_pts';
  const sourceSec = tr ? (startMs(tr) + pos) / 1000 : pos / 1000;
  const sampled = tr ? sample(tr, pos) : null;

  return (
    <Panel
      title="Robot replay"
      actions={
        <div className="row">
          {ready.length > 1 && (
            <select
              value={job?.jobId}
              onChange={e => {
                setPickedJobId(e.target.value);
                setFollow(false);
              }}
              aria-label="Replay"
            >
              {ready.map(j => (
                <option key={j.jobId} value={j.jobId}>
                  attempt #{data.attempts.find(a => a.attemptId === j.attemptId)?.ordinal ?? '?'} · run {j.run}
                </option>
              ))}
            </select>
          )}
          <label className="row small muted" style={{ gap: 6 }} title="Follow the operator's playback">
            <input type="checkbox" checked={follow} onChange={e => setFollow(e.target.checked)} /> follow
          </label>
        </div>
      }
    >
      <div className="stack">
        <div className="row small">
          <Pill tone="accent" dot={false}>
            Replay of recorded motion, not a learned policy
          </Pill>
          {job?.quality && (
            <Pill tone="muted" dot={false}>
              {job.quality.robotModel} · {job.quality.replayKind === 'physics' ? 'physics simulation' : 'kinematic replay'}
            </Pill>
          )}
          {sampled && !sampled.valid && <Pill tone="warn">No valid observation at this moment</Pill>}
        </div>

        {loading && <div className="muted">Loading replay…</div>}
        {loadError && <div className="notice bad">{loadError}</div>}
        {!loading && !trajArt && !videoArt && <div className="notice warn">This job produced no robot trajectory or replay video.</div>}

        {(sourceUrl || videoUrl) && (
          <div className={`replay-grid ${sourceUrl && videoUrl ? '' : 'single'}`}>
            {sourceUrl && (
              <SyncedVideo
                src={sourceUrl}
                positionSec={sourceSec}
                playing={playing}
                rate={rate}
                label={clipClock || !tr ? 'Source recording (raw passthrough clip)' : 'Source recording (timebase not clip-aligned; sync approximate)'}
              />
            )}
            {videoUrl && (
              <SyncedVideo src={videoUrl} positionSec={pos / 1000} playing={playing} rate={rate} label="Simulated robot replay (rendered)" />
            )}
          </div>
        )}

        {tr && (
          <>
            <Chart tr={tr} positionMs={pos} />
            <div className="row">
              <button className="btn sm" onClick={() => control({ playing: !playing, positionMs: pos >= total ? 0 : pos })}>
                {playing ? 'Pause' : pos >= total ? 'Replay' : 'Play'}
              </button>
              <input
                className="scrub"
                type="range"
                min={0}
                max={total}
                step={10}
                value={pos}
                onChange={e => control({ playing: false, positionMs: Number(e.target.value) })}
                style={{ flex: 1 }}
                aria-label="Replay position"
              />
              <span className="mono small">
                {duration(pos)} / {duration(total)}
              </span>
              <select value={rate} onChange={e => control({ playing, positionMs: pos, rate: Number(e.target.value) })} aria-label="Speed">
                {[0.25, 0.5, 1, 2].map(r => (
                  <option key={r} value={r}>
                    {r}×
                  </option>
                ))}
              </select>
            </div>
            <JointBars tr={tr} positionMs={pos} />
            <dl className="kv small">
              <dt>Robot</dt>
              <dd>
                {tr.robot.model}
                {tr.robot.version ? ` @${tr.robot.version}` : ''} · {tr.robot.joints.length} joints ({tr.robot.joints[0]?.unit})
              </dd>
              {tr.source?.handedness && (
                <>
                  <dt>Hand</dt>
                  <dd>{humanize(tr.source.handedness)}</dd>
                </>
              )}
              {tr.source?.perception && (
                <>
                  <dt>Perception</dt>
                  <dd>{tr.source.perception}</dd>
                </>
              )}
              {tr.source?.retargeting && (
                <>
                  <dt>Retargeting</dt>
                  <dd>{tr.source.retargeting}</dd>
                </>
              )}
              <dt>Invalid spans</dt>
              <dd>{invalidSpans(tr).length || 'none'}</dd>
              {tr.notes && (
                <>
                  <dt>Notes</dt>
                  <dd>{tr.notes}</dd>
                </>
              )}
            </dl>
          </>
        )}
      </div>
    </Panel>
  );
}
