import { closeSync, existsSync, fstatSync, mkdirSync, openSync, readFileSync, readSync, readdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Context, Hono } from "hono";
import type { CoachSession } from "./coach.js";
import type { RealtimeSink } from "./realtime-bridge.js";

// Robot learning from the headset (services/motion `robot-serve`). The Quest posts a learner's
// ControllerMotionCapture frames for one surgery step; the motion worker pulls them, retargets them to a
// simulated arm + hand in MuJoCo, behavior-clones a policy, grades its rollouts with the same milestone
// predicates as the learner, renders a replay and posts the result back here. Sim only, no physical robot.

export const MAX_DEMO_BYTES = 3 * 1024 * 1024;
export const MAX_REPLAY_BYTES = 20 * 1024 * 1024;
const MAX_FRAMES = 20_000;
const STEP_ID = /^[a-z0-9_]{1,80}$/;
const DEMO_ID = /^demo-[a-z0-9]{6,40}$/;
const REPLAY_FILE = /^[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.mp4$/;
const SESSION_DIR = /^coach-[a-z0-9]{6,40}$/;

export interface RobotDemoMeta {
  demoId: string;
  sessionId: string;
  stepId: string;
  stepTitle: string;
  frames: number;
  durationS: number;
  receivedAt: string;
}

export interface RobotResult {
  stepId: string;
  stepTitle: string;
  success: boolean;
  pathErrorMm: number | null;
  policySuccessRate: number | null;
  demos: { human: number; synthetic: number };
  synthetic: boolean;
  videoUrl: string | null;
  demoId: string | null;
  // Free-form measured extras from the worker (grader facts, rollouts, curve, timings). Never rendered as claims.
  details: Record<string, unknown>;
  createdAt: string;
}

export interface RobotRouteOptions {
  dataDir: string;
  now: () => Date;
  session: (sid: string) => CoachSession | null;
  realtime?: RealtimeSink;
  // Wires a posted result into the session's /robot-attempts list (same record the teleop path writes).
  recordAttempt: (sid: string, attempt: Record<string, unknown>) => void;
}

const finite = (v: unknown): v is number => typeof v === "number" && Number.isFinite(v);
const vec = (v: unknown, n: number) => Array.isArray(v) && v.length === n && v.every(finite);

// The exact scalpal.controller_motion.v1 frame (apps/quest/.../ControllerMotionCapture.cs), patient space only.
export function frameProblem(f: unknown, i: number): string | null {
  if (!f || typeof f !== "object") return `frame ${i} is not an object`;
  const o = f as Record<string, unknown>;
  if (o.schema !== "scalpal.controller_motion.v1") return `frame ${i}: schema must be "scalpal.controller_motion.v1"`;
  if (o.space !== "patient") return `frame ${i}: space must be "patient" (set Pose Root to PatientRoot)`;
  if (!finite(o.frameIndex) || !finite(o.unityTime)) return `frame ${i}: frameIndex and unityTime must be numbers`;
  if (typeof o.headTracked !== "boolean" || !vec(o.headPosition, 3) || !vec(o.headRotation, 4)) return `frame ${i}: headTracked, headPosition[3], headRotation[4] required`;
  if (!Array.isArray(o.controllers) || o.controllers.length < 1 || o.controllers.length > 2) return `frame ${i}: controllers must hold one or two samples`;
  for (const c of o.controllers as unknown[]) {
    const s = (c ?? {}) as Record<string, unknown>;
    if (s.hand !== "left" && s.hand !== "right") return `frame ${i}: controller hand must be "left" or "right"`;
    if (typeof s.tracked !== "boolean" || !vec(s.position, 3) || !vec(s.rotation, 4)) return `frame ${i}: controller tracked, position[3], rotation[4] required`;
    if (!finite(s.grip) || !finite(s.trigger) || typeof s.heldInstrument !== "string") return `frame ${i}: controller grip, trigger, heldInstrument required`;
  }
  return null;
}

export function registerRobotRoutes(app: Hono, options: RobotRouteOptions) {
  const demosDir = join(options.dataDir, "demos");
  const replaysDir = join(options.dataDir, "replays");
  const baselineFile = join(options.dataDir, "baseline-result.json");
  const demos: RobotDemoMeta[] = [];
  const results = new Map<string, RobotResult>();
  let baseline: RobotResult | null = null;
  try {
    if (existsSync(baselineFile)) baseline = JSON.parse(readFileSync(baselineFile, "utf8")) as RobotResult;
  } catch (err) {
    console.error("[robot] could not read the baseline result", err);
  }

  const fail = (c: Context, status: 400 | 404 | 413, code: string, message: string) => c.json({ error: { code, message }, actions: [] }, status);
  const noSession = (c: Context) => fail(c, 404, "coach_session_not_found", "No live coach session with that id. Start one from a patient.");
  const stepTitle = (s: CoachSession | null, stepId: string) => s?.kase.procedure.steps.find((x) => x.id === stepId)?.title ?? stepId;
  const sid = (c: Context) => c.req.param("sid") ?? "";

  app.post("/coach/sessions/:sid/robot-demo", async (c) => {
    const s = options.session(sid(c));
    if (!s) return noSession(c);
    const declared = Number(c.req.header("content-length") ?? 0);
    if (declared > MAX_DEMO_BYTES) return fail(c, 413, "demo_too_large", "Robot demos are limited to 3 MB.");
    const raw = await c.req.text();
    if (Buffer.byteLength(raw) > MAX_DEMO_BYTES) return fail(c, 413, "demo_too_large", "Robot demos are limited to 3 MB.");
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(raw) as Record<string, unknown>;
    } catch {
      return fail(c, 400, "invalid_robot_demo", "Send JSON {stepId, frames: [scalpal.controller_motion.v1 ...]}.");
    }
    const { stepId, frames, frameOrigin } = parsed;
    // Optional: where the umbilicus-origin torso frame sits inside PatientRoot (same axes). The worker defaults to
    // NativeSession.unity's AuthoredPatientTorsoFrame (0, 1.3269, 0.2093) when it is absent.
    if (frameOrigin !== undefined && !vec(frameOrigin, 3)) return fail(c, 400, "invalid_robot_demo", "frameOrigin must be [x, y, z] in metres.");
    if (typeof stepId !== "string" || !STEP_ID.test(stepId)) return fail(c, 400, "invalid_robot_demo", "stepId must be a procedure step id such as mark_incision.");
    if (!s.kase.procedure.steps.some((x) => x.id === stepId)) return fail(c, 400, "unknown_step", `This session's procedure has no step "${stepId}".`);
    if (!Array.isArray(frames) || frames.length < 2 || frames.length > MAX_FRAMES) return fail(c, 400, "invalid_robot_demo", `frames must hold 2 to ${MAX_FRAMES} controller motion frames.`);
    for (let i = 0; i < frames.length; i++) {
      const problem = frameProblem(frames[i], i);
      if (problem) return fail(c, 400, "invalid_robot_demo", problem);
    }
    const demoId = `demo-${crypto.randomUUID().replaceAll("-", "").slice(0, 16)}`;
    const times = frames.map((f) => (f as { unityTime: number }).unityTime);
    const meta: RobotDemoMeta = {
      demoId, sessionId: s.id, stepId, stepTitle: stepTitle(s, stepId), frames: frames.length,
      durationS: Math.max(0, Math.max(...times) - Math.min(...times)), receivedAt: options.now().toISOString(),
    };
    mkdirSync(join(demosDir, s.id), { recursive: true });
    writeFileSync(join(demosDir, s.id, `${demoId}.json`), JSON.stringify({ schema: "scalpal.robot_demo.v1", ...meta, ...(frameOrigin === undefined ? {} : { frameOrigin }), frames }));
    demos.push(meta);
    options.realtime?.simLog?.({
      coachSessionId: s.id, kind: "event",
      text: `Robot demo received for "${meta.stepTitle}": ${meta.frames} controller frames, ${meta.durationS.toFixed(1)} s. Queued for the simulated robot.`,
      data: { robotDemo: meta },
    });
    return c.json({ demoId }, 202);
  });

  // Worker side: list demos (oldest first) after a cursor, and fetch one.
  app.get("/coach/robot/demos", (c) => {
    const after = c.req.query("after") ?? "";
    const start = after ? demos.findIndex((d) => d.demoId === after) + 1 : 0;
    return c.json({ demos: demos.slice(start), total: demos.length });
  });
  app.get("/coach/robot/demos/:demoId", (c) => {
    const id = c.req.param("demoId");
    const meta = DEMO_ID.test(id) ? demos.find((d) => d.demoId === id) : undefined;
    if (!meta || !SESSION_DIR.test(meta.sessionId)) return fail(c, 404, "demo_not_found", "No robot demo with that id.");
    return c.body(readFileSync(join(demosDir, meta.sessionId, `${id}.json`), "utf8"), 200, { "Content-Type": "application/json" });
  });

  const parseResult = (b: Record<string, unknown>): RobotResult | string => {
    if (typeof b.stepId !== "string" || !STEP_ID.test(b.stepId)) return "stepId required";
    if (typeof b.success !== "boolean") return "success must be a boolean";
    if (typeof b.synthetic !== "boolean") return "synthetic must be a boolean";
    const d = (b.demos ?? {}) as Record<string, unknown>;
    if (!finite(d.human) || !finite(d.synthetic)) return "demos must be {human, synthetic} counts";
    const videoUrl = b.videoUrl === null || b.videoUrl === undefined ? null : String(b.videoUrl);
    if (videoUrl !== null && !/^\/robot\/replays\/[A-Za-z0-9][A-Za-z0-9._-]{0,120}\.mp4$/.test(videoUrl)) return "videoUrl must be /robot/replays/<file>.mp4";
    const rate = b.policySuccessRate;
    return {
      stepId: b.stepId, stepTitle: typeof b.stepTitle === "string" ? b.stepTitle.slice(0, 200) : b.stepId, success: b.success,
      pathErrorMm: finite(b.pathErrorMm) ? b.pathErrorMm : null,
      policySuccessRate: finite(rate) && rate >= 0 && rate <= 1 ? rate : null,
      demos: { human: d.human, synthetic: d.synthetic }, synthetic: b.synthetic, videoUrl,
      demoId: typeof b.demoId === "string" && DEMO_ID.test(b.demoId) ? b.demoId : null,
      details: b.details && typeof b.details === "object" ? (b.details as Record<string, unknown>) : {},
      createdAt: options.now().toISOString(),
    };
  };

  // Worker posts a session's result after training/evaluating on its demo...
  app.post("/coach/sessions/:sid/robot-result", async (c) => {
    const s = options.session(sid(c));
    if (!s) return noSession(c);
    const r = parseResult((await c.req.json().catch(() => ({}))) as Record<string, unknown>);
    if (typeof r === "string") return fail(c, 400, "invalid_robot_result", r);
    r.stepTitle = stepTitle(s, r.stepId);
    results.set(s.id, r);
    const rate = r.policySuccessRate === null ? "" : `, policy ${(r.policySuccessRate * 100).toFixed(0)}% over rollouts`;
    options.recordAttempt(s.id, {
      attemptId: r.demoId ?? `policy-${Date.now()}`, stepId: r.stepId, stepTitle: r.stepTitle, task: "robot_policy_rollout",
      success: r.success, frames: 0, durationS: 0, labeledFraction: 1, heldInstruments: ["skin_marker"],
      source: r.synthetic ? "sim policy (synthetic demos)" : "sim policy (headset demos)", createdAt: r.createdAt,
      pathErrorMm: r.pathErrorMm, policySuccessRate: r.policySuccessRate, videoUrl: r.videoUrl,
    });
    options.realtime?.simLog?.({
      coachSessionId: s.id, kind: "outcome",
      text: `Simulated robot ${r.success ? "completed" : "missed"} "${r.stepTitle}"${r.pathErrorMm === null ? "" : `, path error ${r.pathErrorMm.toFixed(1)} mm`}${rate} (${r.demos.human} headset + ${r.demos.synthetic} synthetic demos; sim only).`,
      data: { robotResult: r },
    });
    options.realtime?.robotResult?.({
      stepId: r.stepId, success: r.success, pathErrorMm: r.pathErrorMm, policySuccessRate: r.policySuccessRate,
      demosHuman: r.demos.human, demosSynthetic: r.demos.synthetic, videoUrl: r.videoUrl,
    });
    return c.json({ stored: true }, 201);
  });

  // ...and a session-independent baseline trained on synthetic demos only, so the panel is never empty.
  app.post("/coach/robot/result", async (c) => {
    const r = parseResult((await c.req.json().catch(() => ({}))) as Record<string, unknown>);
    if (typeof r === "string") return fail(c, 400, "invalid_robot_result", r);
    baseline = r;
    try {
      mkdirSync(options.dataDir, { recursive: true });
      writeFileSync(baselineFile, JSON.stringify(r));
    } catch (err) {
      console.error("[robot] could not persist the baseline result", err);
    }
    return c.json({ stored: true }, 201);
  });

  app.get("/coach/sessions/:sid/robot-result", (c) => {
    const s = options.session(sid(c));
    if (!s) return noSession(c);
    const own = results.get(s.id) ?? null;
    const latestDemo = [...demos].reverse().find((d) => d.sessionId === s.id) ?? null;
    const shown = own ?? baseline;
    // Pending while the session's newest demo has not been answered yet.
    const pending = latestDemo !== null && (own === null || own.demoId !== latestDemo.demoId);
    const status = pending ? "pending" : shown ? "ready" : "unavailable";
    const stepId = shown?.stepId ?? latestDemo?.stepId ?? "mark_incision";
    return c.json({
      status, stepId, stepTitle: stepTitle(s, stepId),
      success: shown?.success ?? null, pathErrorMm: shown?.pathErrorMm ?? null, policySuccessRate: shown?.policySuccessRate ?? null,
      demos: shown?.demos ?? { human: 0, synthetic: 0 }, synthetic: shown?.synthetic ?? true, videoUrl: shown?.videoUrl ?? null,
      demoId: shown?.demoId ?? null, pendingDemoId: pending ? latestDemo!.demoId : null, createdAt: shown?.createdAt ?? null,
      details: shown?.details ?? {},
    });
  });

  // Replay videos: the worker uploads the MP4, players stream it with Range requests.
  app.put("/robot/replays/:file", async (c) => {
    const file = c.req.param("file");
    if (!REPLAY_FILE.test(file)) return fail(c, 400, "invalid_replay_name", "Use a plain <name>.mp4 file name.");
    const bytes = new Uint8Array(await c.req.arrayBuffer());
    if (bytes.byteLength > MAX_REPLAY_BYTES) return fail(c, 413, "replay_too_large", "Replays are limited to 20 MB.");
    if (bytes.byteLength < 12) return fail(c, 400, "invalid_replay", "Empty or truncated MP4.");
    mkdirSync(replaysDir, { recursive: true });
    writeFileSync(join(replaysDir, file), bytes);
    return c.json({ videoUrl: `/robot/replays/${file}` }, 201);
  });
  app.get("/robot/replays/:file", (c) => {
    const file = c.req.param("file");
    const path = join(replaysDir, file);
    if (!REPLAY_FILE.test(file) || !existsSync(path)) return fail(c, 404, "replay_not_found", "No replay with that name.");
    const fd = openSync(path, "r");
    try {
      const size = fstatSync(fd).size;
      const headers: Record<string, string> = { "Content-Type": "video/mp4", "Accept-Ranges": "bytes", "Cache-Control": "no-cache" };
      const range = /^bytes=(\d*)-(\d*)$/.exec(c.req.header("range") ?? "");
      let start = 0;
      let end = size - 1;
      if (range && (range[1] || range[2])) {
        if (range[1]) {
          start = Number(range[1]);
          end = range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
        } else {
          start = Math.max(0, size - Number(range[2]));
        }
        if (start >= size || start > end) return c.body(null, 416, { ...headers, "Content-Range": `bytes */${size}` });
      }
      const length = end - start + 1;
      const chunk = Buffer.alloc(length);
      readSync(fd, chunk, 0, length, start);
      headers["Content-Length"] = String(length);
      if (range && (range[1] || range[2])) {
        headers["Content-Range"] = `bytes ${start}-${end}/${size}`;
        return c.body(new Uint8Array(chunk), 206, headers);
      }
      return c.body(new Uint8Array(chunk), 200, headers);
    } finally {
      closeSync(fd);
    }
  });

  // Reload demo metadata written before a coach restart (sessions themselves do not survive one).
  try {
    if (existsSync(demosDir))
      for (const dir of readdirSync(demosDir).filter((d) => SESSION_DIR.test(d)))
        for (const f of readdirSync(join(demosDir, dir)).filter((x) => x.endsWith(".json"))) {
          const d = JSON.parse(readFileSync(join(demosDir, dir, f), "utf8")) as RobotDemoMeta & { frames: unknown };
          demos.push({ demoId: d.demoId, sessionId: d.sessionId, stepId: d.stepId, stepTitle: d.stepTitle, frames: Array.isArray(d.frames) ? d.frames.length : 0, durationS: d.durationS, receivedAt: d.receivedAt });
        }
    demos.sort((a, b) => a.receivedAt.localeCompare(b.receivedAt));
  } catch (err) {
    console.error("[robot] could not reload stored demos", err);
  }
}
