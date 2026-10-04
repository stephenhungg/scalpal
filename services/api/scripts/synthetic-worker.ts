// Synthetic motion worker: exercises the gateway's worker API end to end.
//
// It does NOT reconstruct anything from the clip. It downloads the input (to
// prove access works), then emits a generated finger open/close trajectory
// that is explicitly labelled synthetic in the artifact and quality notes.
// Silas's real worker follows the same HTTP protocol (see
// packages/contracts/worker-api.md and services/api/examples/worker_client.py).
//
//   GATEWAY_URL=http://localhost:8788 WORKER_TOKEN=... npm run worker:synthetic

const GATEWAY = (process.env.GATEWAY_URL ?? 'http://localhost:8788').replace(/\/$/, '');
const TOKEN = process.env.WORKER_TOKEN ?? 'dev-synthetic-worker-token-0001';
const POLL_MS = Number(process.env.POLL_MS ?? 2000);
const ONCE = process.argv.includes('--once');

async function call(path: string, body: unknown = {}) {
  const res = await fetch(`${GATEWAY}${path}`, {
    method: 'POST',
    headers: { authorization: `Bearer ${TOKEN}`, 'content-type': 'application/json' },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : null };
}

const JOINTS = [
  'thumb_cmc', 'thumb_mcp', 'thumb_ip',
  'index_mcp', 'index_pip', 'index_dip',
  'middle_mcp', 'middle_pip', 'middle_dip',
  'ring_mcp', 'ring_pip', 'ring_dip',
  'pinky_mcp', 'pinky_pip', 'pinky_dip',
  'wrist_flex',
];

function syntheticTrajectory(inputArtifactId: string) {
  const fps = 30;
  const seconds = 5;
  const t: number[] = [];
  const q: number[][] = [];
  const valid: boolean[] = [];
  for (let i = 0; i < fps * seconds; i++) {
    const ms = (i * 1000) / fps;
    const grip = 0.5 - 0.5 * Math.cos((2 * Math.PI * ms) / 2500); // open -> closed -> open
    t.push(Math.round(ms));
    q.push(
      JOINTS.map((name, j) =>
        name === 'wrist_flex' ? 0.3 * Math.sin((2 * Math.PI * ms) / 5000) : grip * (1.2 + 0.1 * (j % 3)) * (name.startsWith('thumb') ? 0.7 : 1)
      )
    );
    // A simulated 0.4 s occlusion so the invalid-interval UI is exercised.
    valid.push(!(ms >= 2900 && ms < 3300));
  }
  return {
    schema: 'scalpal.robot_trajectory.v1',
    kind: 'kinematic',
    robot: {
      model: 'synthetic-hand',
      version: '0',
      joints: JOINTS.map(name => ({ name, unit: 'rad', lower: name === 'wrist_flex' ? -0.6 : 0, upper: name === 'wrist_flex' ? 0.6 : 1.6 })),
    },
    timebase: { unit: 'ms', clock: 'clip_pts', startMs: 0 },
    frames: { t, q, valid },
    invalidIntervals: [{ startMs: 2900, endMs: 3300, reason: 'synthetic occlusion' }],
    source: {
      inputArtifactId,
      handedness: 'right',
      perception: 'none (synthetic worker)',
      retargeting: 'none (synthetic worker)',
    },
    notes: 'SYNTHETIC: generated test motion, not derived from the input clip.',
  };
}

async function processOne(): Promise<boolean> {
  const claim = await call('/v1/worker/claim', { leaseMs: 60_000 });
  if (claim.status === 204) return false;
  if (claim.status !== 200) throw new Error(`claim failed: ${claim.status} ${JSON.stringify(claim.json)}`);
  const { job, inputs, endpoints } = claim.json;
  console.log(`claimed ${job.jobId} run ${job.run}`);

  try {
    await call(endpoints.heartbeat, { progress: 0.1, stage: 'downloading clip' });
    const input = inputs.find((i: any) => i.role === 'input');
    const res = await fetch(input.download.url);
    if (!res.ok) throw new Error(`input download failed: ${res.status}`);
    const clipBytes = (await res.arrayBuffer()).byteLength;

    for (const [p, stage] of [[0.4, 'hand inference (synthetic)'], [0.7, 'retargeting (synthetic)']] as const) {
      await new Promise(r => setTimeout(r, 800));
      const hb = await call(endpoints.heartbeat, { progress: p, stage });
      if (hb.status === 409) {
        console.log('run superseded; stopping');
        return true;
      }
    }

    const traj = syntheticTrajectory(input.artifactId);
    const out = await call(endpoints.outputs, {
      kind: 'robot_trajectory',
      filename: 'robot_trajectory.json',
      contentType: 'application/json',
    });
    if (out.status !== 200) throw new Error(`output registration failed: ${JSON.stringify(out.json)}`);
    const put = await fetch(out.json.upload.url, {
      method: 'PUT',
      headers: out.json.upload.headers,
      body: JSON.stringify(traj),
    });
    if (!put.ok) throw new Error(`output upload failed: ${put.status}`);

    const validCount = traj.frames.valid.filter(Boolean).length;
    const done = await call(endpoints.complete, {
      outputArtifactIds: [out.json.artifactId],
      quality: {
        framesTotal: traj.frames.t.length,
        framesValid: validCount,
        invalidIntervals: traj.invalidIntervals.length,
        robotModel: 'synthetic-hand',
        replayKind: 'kinematic',
        notes: `SYNTHETIC worker: motion is generated, not reconstructed from the ${clipBytes}-byte clip.`,
      },
    });
    if (done.status !== 200) throw new Error(`complete failed: ${done.status} ${JSON.stringify(done.json)}`);
    console.log(`completed ${job.jobId} run ${job.run}`);
  } catch (err) {
    console.error(err);
    await call(endpoints.fail, { error: String((err as Error).message), retryable: true });
  }
  return true;
}

async function main() {
  console.log(`synthetic worker polling ${GATEWAY}`);
  for (;;) {
    let worked = false;
    try {
      worked = await processOne();
    } catch (err) {
      console.error(String(err));
    }
    if (ONCE && worked) return;
    if (!worked) await new Promise(r => setTimeout(r, POLL_MS));
  }
}

main();
