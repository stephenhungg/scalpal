import { useRef, useState } from 'react';
import { useLive, type SessionData } from '../data/live';
import { ago, bytes, humanize, pct } from '../lib/format';
import { downloadUrl, uploadFile } from '../lib/grants';
import { uid } from '../lib/ids';
import { Empty, Panel, Pill, Progress, StatusPill, toast, useAction } from './ui';

export const DEFAULT_CONFIG_VERSION = 'motion-v1';

const INPUT_KINDS = ['raw_clip', 'capture_manifest', 'scene_timeline'];

export default function Motion({ data }: { data: SessionData }) {
  const { conn } = useLive();
  const act = useAction();
  const fileRef = useRef<HTMLInputElement>(null);
  const [kind, setKind] = useState('raw_clip');
  const [progress, setProgress] = useState<number | null>(null);
  const [showAll, setShowAll] = useState(false);
  const attemptId = data.currentAttempt?.attemptId;
  const active = data.session?.status === 'active';
  const canUpload = (data.isOperator || data.isHeadset) && active;

  const inputs = data.artifacts.filter(
    a => !a.jobId && a.status !== 'deleted' && (showAll || a.attemptId === attemptId)
  );
  const jobs = data.jobs.filter(j => showAll || j.attemptId === attemptId).reverse();
  const artifactById = new Map(data.artifacts.map(a => [a.artifactId, a]));
  const jobForInput = (id: string) => data.jobs.find(j => j.inputArtifactId === id);

  const onUpload = (file: File) =>
    act.run(async () => {
      if (!conn || !data.session || !attemptId) return;
      setProgress(0);
      try {
        await uploadFile(conn, { sessionId: data.session.sessionId, attemptId, kind, file }, setProgress);
        toast('Upload finished; verifying');
      } finally {
        setProgress(null);
        if (fileRef.current) fileRef.current.value = '';
      }
    });

  const process = (inputArtifactId: string) =>
    act.run(async () => {
      if (!conn) return;
      const extras = data.artifacts
        .filter(
          a =>
            a.attemptId === artifactById.get(inputArtifactId)?.attemptId &&
            !a.jobId &&
            a.status === 'available' &&
            (a.kind === 'capture_manifest' || a.kind === 'scene_timeline')
        )
        .map(a => a.artifactId);
      await conn.reducers.requestMotionJob({
        jobId: uid('job'),
        inputArtifactId,
        extraArtifactIds: extras,
        configVersion: DEFAULT_CONFIG_VERSION,
      });
    });

  const open = (artifactId: string) =>
    act.run(async () => {
      if (!conn) return;
      const url = await downloadUrl(conn, artifactId);
      window.open(url, '_blank', 'noopener');
    });

  return (
    <Panel
      title="Recording and motion processing"
      flush
      actions={
        <label className="row small muted" style={{ gap: 6 }}>
          <input type="checkbox" checked={showAll} onChange={e => setShowAll(e.target.checked)} />
          all attempts
        </label>
      }
    >
      {canUpload && (
        <div className="panel-body row" style={{ borderBottom: '1px solid var(--line)' }}>
          <select value={kind} onChange={e => setKind(e.target.value)} aria-label="Artifact kind">
            {INPUT_KINDS.map(k => (
              <option key={k} value={k}>
                {humanize(k)}
              </option>
            ))}
          </select>
          <input
            ref={fileRef}
            type="file"
            accept={kind === 'raw_clip' ? 'video/*' : 'application/json,.json'}
            onChange={e => e.target.files?.[0] && onUpload(e.target.files[0])}
            disabled={act.busy}
            aria-label="Choose file to upload"
          />
          {progress != null && (
            <div style={{ flex: 1, minWidth: 120 }}>
              <Progress value={progress} />
            </div>
          )}
          <span className="muted small" style={{ flexBasis: '100%' }}>
            Use nonpersonal test clips until capture permissions are settled. Files go to private storage through a
            short-lived signed URL; the database only stores metadata.
          </span>
        </div>
      )}

      <div className="table-wrap">
        <table className="data">
          <thead>
            <tr>
              <th>Recording</th>
              <th>Status</th>
              <th style={{ textAlign: 'right' }}></th>
            </tr>
          </thead>
          <tbody>
            {inputs.length === 0 && (
              <tr>
                <td colSpan={3} className="muted">
                  No recordings for this attempt yet.
                </td>
              </tr>
            )}
            {inputs.map(a => {
              const job = jobForInput(a.artifactId);
              return (
                <tr key={a.artifactId}>
                  <td>
                    <div>{a.filename}</div>
                    <div className="muted small">
                      {humanize(a.kind)} · {bytes(a.verifiedBytes ?? a.declaredBytes)} · {ago(a.createdAt)}
                    </div>
                  </td>
                  <td>
                    <StatusPill status={a.status} label={a.status === 'pending_upload' ? 'uploading' : undefined} />
                    {a.statusReason && <div className="muted small">{a.statusReason}</div>}
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <div className="row" style={{ justifyContent: 'flex-end', gap: 6 }}>
                      {a.status === 'available' && (
                        <button className="btn sm ghost" onClick={() => open(a.artifactId)}>
                          Open
                        </button>
                      )}
                      {a.kind === 'raw_clip' && a.status === 'available' && !job && (data.isOperator || data.isHeadset) && active && (
                        <button className="btn sm primary" disabled={act.busy} onClick={() => process(a.artifactId)}>
                          Process
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <div style={{ borderTop: '1px solid var(--line)' }}>
        {jobs.length === 0 ? (
          <Empty>No motion processing yet. Processing turns a recorded clip into robot joint targets offline.</Empty>
        ) : (
          <ul className="list">
            {jobs.map(j => {
              const q = j.quality;
              return (
                <li key={j.jobId} className="stack" style={{ gap: 8 }}>
                  <div className="row">
                    <StatusPill status={j.status} />
                    <span style={{ fontWeight: 600 }}>{j.status === 'running' ? humanize(j.stage ?? 'running') : 'Motion job'}</span>
                    <span className="muted small">
                      run {j.run}/{j.maxRuns} · {j.configVersion}
                      {j.workerId ? ` · ${j.workerId}` : ''}
                    </span>
                    <div className="spacer" />
                    {data.isOperator && active && (j.status === 'failed' || j.status === 'cancelled') && (
                      <button className="btn sm" disabled={act.busy} onClick={() => act.run(() => conn!.reducers.retryMotionJob({ jobId: j.jobId }))}>
                        Retry
                      </button>
                    )}
                    {data.isOperator && active && (j.status === 'queued' || j.status === 'running') && (
                      <button className="btn sm ghost" disabled={act.busy} onClick={() => act.run(() => conn!.reducers.cancelMotionJob({ jobId: j.jobId }))}>
                        Cancel
                      </button>
                    )}
                  </div>
                  {j.status === 'running' && <Progress value={j.progress ?? 0} />}
                  {j.status === 'queued' && <div className="muted small">Waiting for a motion worker{j.error ? ` · ${j.error}` : ''}.</div>}
                  {j.status === 'failed' && j.error && <div className="notice bad">{j.error}</div>}
                  {q && (
                    <div className="row small">
                      <Pill tone={q.framesTotal > 0 && q.framesValid / q.framesTotal >= 0.8 ? 'ok' : 'warn'} dot={false}>
                        {pct(q.framesTotal ? q.framesValid / q.framesTotal : 0)} frames valid ({q.framesValid}/{q.framesTotal})
                      </Pill>
                      <Pill tone={q.invalidIntervals ? 'warn' : 'ok'} dot={false}>
                        {q.invalidIntervals} invalid interval{q.invalidIntervals === 1 ? '' : 's'}
                      </Pill>
                      <Pill tone="muted" dot={false}>
                        {q.robotModel} · {q.replayKind}
                      </Pill>
                    </div>
                  )}
                  {q?.notes && <div className="muted small">{q.notes}</div>}
                  {j.outputArtifactIds.length > 0 && (
                    <div className="row small">
                      {j.outputArtifactIds.map(id => {
                        const a = artifactById.get(id);
                        return (
                          <button key={id} className="btn sm ghost" onClick={() => open(id)}>
                            {a ? `${humanize(a.kind)} (${bytes(a.verifiedBytes)})` : id}
                          </button>
                        );
                      })}
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
        )}
      </div>
      {act.error && <div className="panel-body error-text">{act.error}</div>}
    </Panel>
  );
}
