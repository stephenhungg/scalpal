// Robot trajectory artifact (proposed contract `scalpal.robot_trajectory.v1`,
// see packages/contracts/robot-trajectory.v1.schema.json). Applied robot
// joint targets over the clip's timebase, with explicit validity.

export type Joint = { name: string; unit: string; lower?: number; upper?: number };

export type Trajectory = {
  schema: 'scalpal.robot_trajectory.v1';
  kind: 'kinematic' | 'physics';
  robot: { model: string; version?: string; joints: Joint[] };
  timebase: { unit: 'ms'; clock: string; startMs?: number };
  frames: { t: number[]; q: number[][]; valid?: boolean[] };
  invalidIntervals?: { startMs: number; endMs: number; reason?: string }[];
  source?: {
    inputArtifactId?: string;
    handedness?: string;
    perception?: string;
    retargeting?: string;
  };
  notes?: string;
};

export class TrajectoryError extends Error {}

export function parseTrajectory(text: string): Trajectory {
  let raw: any;
  try {
    raw = JSON.parse(text);
  } catch {
    throw new TrajectoryError('Replay file is not valid JSON.');
  }
  if (raw?.schema !== 'scalpal.robot_trajectory.v1') {
    throw new TrajectoryError(`Unsupported replay schema: ${String(raw?.schema)}`);
  }
  const joints: Joint[] = raw.robot?.joints;
  if (!Array.isArray(joints) || joints.length === 0) throw new TrajectoryError('Replay has no robot joints.');
  const { t, q, valid } = raw.frames ?? {};
  if (!Array.isArray(t) || !Array.isArray(q) || t.length !== q.length || t.length === 0) {
    throw new TrajectoryError('Replay frames are missing or inconsistent.');
  }
  if (valid != null && (!Array.isArray(valid) || valid.length !== t.length)) {
    throw new TrajectoryError('Replay validity mask length does not match frames.');
  }
  for (let i = 0; i < q.length; i++) {
    if (!Array.isArray(q[i]) || q[i].length !== joints.length) {
      throw new TrajectoryError(`Frame ${i} has ${q[i]?.length ?? 0} joint values, expected ${joints.length}.`);
    }
    if (i > 0 && !(t[i] >= t[i - 1])) throw new TrajectoryError(`Frame times are not ordered at frame ${i}.`);
  }
  return raw as Trajectory;
}

export function durationMs(tr: Trajectory): number {
  const t = tr.frames.t;
  return t[t.length - 1] - t[0];
}

export function startMs(tr: Trajectory): number {
  return tr.frames.t[0];
}

/**
 * Joint values at time `ms` (relative to the first frame). Interpolates only
 * between two valid neighbouring frames; otherwise holds the nearest frame and
 * reports `valid: false` so the UI can mark the gap instead of inventing
 * motion.
 */
export function sample(tr: Trajectory, ms: number): { q: number[]; valid: boolean; index: number } {
  const t = tr.frames.t;
  const abs = t[0] + ms;
  let lo = 0;
  let hi = t.length - 1;
  if (abs <= t[0]) return { q: tr.frames.q[0], valid: isValid(tr, 0), index: 0 };
  if (abs >= t[hi]) return { q: tr.frames.q[hi], valid: isValid(tr, hi), index: hi };
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (t[mid] <= abs) lo = mid;
    else hi = mid;
  }
  const a = tr.frames.q[lo];
  const b = tr.frames.q[hi];
  if (!isValid(tr, lo) || !isValid(tr, hi)) {
    const nearest = abs - t[lo] < t[hi] - abs ? lo : hi;
    return { q: tr.frames.q[nearest], valid: false, index: nearest };
  }
  const f = (abs - t[lo]) / (t[hi] - t[lo] || 1);
  return { q: a.map((v, i) => v + (b[i] - v) * f), valid: true, index: lo };
}

export function isValid(tr: Trajectory, i: number): boolean {
  return tr.frames.valid ? tr.frames.valid[i] : true;
}

/** Invalid spans (relative ms), from the explicit list or derived from the mask. */
export function invalidSpans(tr: Trajectory): { startMs: number; endMs: number; reason?: string }[] {
  const t0 = tr.frames.t[0];
  if (tr.invalidIntervals?.length) {
    return tr.invalidIntervals.map(s => ({ ...s, startMs: s.startMs - t0, endMs: s.endMs - t0 }));
  }
  const out: { startMs: number; endMs: number }[] = [];
  const { t, valid } = tr.frames;
  if (!valid) return out;
  let start: number | null = null;
  for (let i = 0; i < t.length; i++) {
    if (!valid[i] && start == null) start = t[i];
    if (valid[i] && start != null) {
      out.push({ startMs: start - t0, endMs: t[i] - t0 });
      start = null;
    }
  }
  if (start != null) out.push({ startMs: start - t0, endMs: t[t.length - 1] - t0 });
  return out;
}

export function jointRange(tr: Trajectory, j: number): [number, number] {
  const joint = tr.robot.joints[j];
  if (joint.lower != null && joint.upper != null && joint.upper > joint.lower) return [joint.lower, joint.upper];
  let lo = Infinity;
  let hi = -Infinity;
  for (const q of tr.frames.q) {
    lo = Math.min(lo, q[j]);
    hi = Math.max(hi, q[j]);
  }
  if (!(hi > lo)) return [lo - 1, hi + 1];
  return [lo, hi];
}
