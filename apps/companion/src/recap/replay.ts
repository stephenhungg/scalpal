import { highlightWindow, type RunResult } from './runResult';
export interface ReplayView {
  schemaVersion: 'scalpal.replay.v1'; sessionId: string; attemptId: string; jobId: string; jobRun: number;
  sourceArtifactId: string; replayArtifactId: string; source: RunResult['replay']['source'];
  status: RunResult['replay']['status']; reason: string; replayKind: string;
  sourceVideoUrl: string; replayVideoUrl: string; expiresAtUnixMs: number;
}
export function playbackRefresh(old: Pick<ReplayView, 'replayArtifactId'> | null, next: Pick<ReplayView, 'replayArtifactId'>, position: number, expired: boolean) {
  const same = old?.replayArtifactId === next.replayArtifactId;
  return { replace: !same || expired, position: same ? position : 0 };
}
export function acceptReplayView(result: RunResult, view: ReplayView) {
  if (view.schemaVersion !== 'scalpal.replay.v1' || view.sessionId !== result.sessionId || view.attemptId !== result.attemptId || view.jobId !== result.replay.jobId)
    throw new Error('Replay belongs to a different session, attempt or job.');
  if (result.replay.jobRun && view.jobRun !== result.replay.jobRun) throw new Error('Replay belongs to a different job attempt.');
  if (!['learner', 'rehearsal', 'sample', 'unknown'].includes(view.source)) throw new Error('Replay provenance unavailable.');
  if (!['queued', 'processing', 'ready', 'failed'].includes(view.status)) throw new Error('Invalid replay state.');
  if (view.status === 'ready' && view.replayKind !== 'kinematic') throw new Error('Replay must be explicitly kinematic.');
  if (view.status === 'ready' && (view.source === 'unknown' || !view.replayArtifactId || !view.replayVideoUrl)) throw new Error('Replay provenance or artifact unavailable.');
  for (const url of [view.sourceVideoUrl, view.replayVideoUrl]) if (url && !['http:', 'https:'].includes(new URL(url).protocol)) throw new Error('Invalid replay URL.');
  return view;
}

// Gateway responses intentionally do not guess video duration; use the decoder's metadata.
export function loadedReplayMetadata(result: RunResult, duration: number, position: number) {
  if (!Number.isFinite(duration) || duration <= 0) throw new Error('Replay duration is unavailable.');
  const next = { ...result, replay: { ...result.replay, durationSeconds: duration } };
  const window = highlightWindow(next);
  return { result: next, position: Math.max(window.start, Math.min(window.end, Number.isFinite(position) ? position : 0)) };
}

export function secureGatewayBase(base: string) {
  const url = new URL(base);
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && local)) throw new Error('Replay credentials require HTTPS outside localhost.');
  if (url.username || url.password) throw new Error('Gateway URLs cannot contain credentials.');
  return url.toString().replace(/\/$/, '');
}
