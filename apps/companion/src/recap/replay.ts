import type { RunResult } from './runResult';
export interface ReplayView {
  schemaVersion: 'scalpal.replay.v1'; sessionId: string; attemptId: string; jobId: string; jobRun: number;
  sourceArtifactId: string; replayArtifactId: string; source: RunResult['replay']['source'];
  status: RunResult['replay']['status']; reason: string;
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
  if (view.status === 'ready' && (view.source === 'unknown' || !view.replayArtifactId || !view.replayVideoUrl)) throw new Error('Replay provenance or artifact unavailable.');
  for (const url of [view.sourceVideoUrl, view.replayVideoUrl]) if (url && !['http:', 'https:'].includes(new URL(url).protocol)) throw new Error('Invalid replay URL.');
  return view;
}
