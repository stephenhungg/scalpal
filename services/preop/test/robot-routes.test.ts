import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { NOW } from "./helpers.js";

// One ControllerMotionCapture frame (scalpal.controller_motion.v1), patient space, right hand holding the marker.
const frame = (i: number, x = -0.09) => ({
  schema: "scalpal.controller_motion.v1", sessionId: "quest-abc", frameIndex: i, unityTime: 10 + i / 72, space: "patient",
  headTracked: true, headPosition: [-0.3, 0.5, 0.1], headRotation: [0, 0, 0, 1],
  controllers: [
    { hand: "left", tracked: true, position: [0.1, 0.2, 0], rotation: [0, 0, 0, 1], grip: 0, trigger: 0, heldInstrument: "" },
    { hand: "right", tracked: true, position: [x + i * 0.001, 0.02, -0.05], rotation: [0, 0, 0, 1], grip: 0.2, trigger: 1, heldInstrument: "skin_marker" },
  ],
});

async function setup() {
  const { createApp } = await import("../src/app.js");
  const { fixtureClient } = await import("./helpers.js");
  const logs: any[] = [];
  const realtime = { bound: true, coachMessage() {}, coachStatus() {}, attachEncounter() {}, encounterPhase() {}, encounterResult() {}, highlight: async () => null, simLog: (e: any) => logs.push(e) } as any;
  const dir = mkdtempSync(join(tmpdir(), "robot-"));
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime, robotDataDir: dir });
  const req = async (method: string, route: string, body?: unknown, headers: Record<string, string> = {}) => {
    const r = await app.request(route, { method, headers: body !== undefined ? { "Content-Type": "application/json", ...headers } : headers, body: body === undefined ? undefined : typeof body === "string" ? body : JSON.stringify(body) });
    const type = r.headers.get("content-type") ?? "";
    return { status: r.status, headers: r.headers, json: (type.includes("json") ? await r.json() : null) as any, bytes: type.includes("json") ? null : new Uint8Array(await r.arrayBuffer()) };
  };
  const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-sparse" })).json.sessionId as string;
  return { app, req, sid, logs, dir };
}

describe("robot demo -> sim policy result routes", () => {
  it("accepts a headset mark_incision demo in the exact capture schema and queues it for the worker", async () => {
    const { req, sid, logs } = await setup();
    const frames = Array.from({ length: 40 }, (_, i) => frame(i));
    const posted = await req("POST", `/coach/sessions/${sid}/robot-demo`, { stepId: "mark_incision", frames });
    expect(posted.status).toBe(202);
    expect(posted.json.demoId).toMatch(/^demo-[a-z0-9]+$/);
    // The worker discovers it by cursor and fetches the full frames unchanged.
    const list = (await req("GET", "/coach/robot/demos")).json;
    expect(list.demos).toEqual([expect.objectContaining({ demoId: posted.json.demoId, sessionId: sid, stepId: "mark_incision", stepTitle: "Mark McBurney incision", frames: 40 })]);
    const full = (await req("GET", `/coach/robot/demos/${posted.json.demoId}`)).json;
    expect(full.frames[3]).toEqual(frames[3]);
    expect(full.frameOrigin).toBeUndefined();
    const withOrigin = await req("POST", `/coach/sessions/${sid}/robot-demo`, { stepId: "mark_incision", frames, frameOrigin: [0, 1.3269, 0.2093] });
    expect((await req("GET", `/coach/robot/demos/${withOrigin.json.demoId}`)).json.frameOrigin).toEqual([0, 1.3269, 0.2093]);
    expect((await req("POST", `/coach/sessions/${sid}/robot-demo`, { stepId: "mark_incision", frames, frameOrigin: [0, "1"] })).status).toBe(400);
    expect((await req("GET", `/coach/robot/demos?after=${withOrigin.json.demoId}`)).json.demos).toEqual([]);
    expect(logs.some((l) => /Robot demo received for "Mark McBurney incision"/.test(l.text))).toBe(true);
    // Until the worker answers this demo, the session result is pending (never silently stale).
    const result = (await req("GET", `/coach/sessions/${sid}/robot-result`)).json;
    expect(result).toMatchObject({ status: "pending", stepId: "mark_incision", stepTitle: "Mark McBurney incision", pendingDemoId: withOrigin.json.demoId });
  });

  it("rejects frames that are not patient-space capture frames, unknown steps, and bodies over 3 MB", async () => {
    const { req, sid } = await setup();
    const bad = (frames: unknown[], stepId = "mark_incision") => req("POST", `/coach/sessions/${sid}/robot-demo`, { stepId, frames });
    expect((await bad([frame(0), { ...frame(1), space: "world" }])).json.error.message).toMatch(/space must be "patient"/);
    expect((await bad([frame(0), { ...frame(1), controllers: [{ hand: "right", position: [0, 0] }] }])).status).toBe(400);
    expect((await bad([frame(0), frame(1)], "fly_the_plane")).json.error.code).toBe("unknown_step");
    expect((await bad([frame(0)])).status).toBe(400); // one frame is not a stroke
    const huge = JSON.stringify({ stepId: "mark_incision", frames: Array.from({ length: 9000 }, (_, i) => frame(i)) });
    expect(huge.length).toBeGreaterThan(3 * 1024 * 1024);
    expect((await req("POST", `/coach/sessions/${sid}/robot-demo`, huge)).status).toBe(413);
    expect((await req("POST", "/coach/sessions/coach-nosuchsession/robot-demo", { stepId: "mark_incision", frames: [frame(0), frame(1)] })).status).toBe(404);
  });

  it("is unavailable before any worker result, then shows the synthetic baseline, then the session's own result", async () => {
    const { req, sid, logs } = await setup();
    expect((await req("GET", `/coach/sessions/${sid}/robot-result`)).json).toMatchObject({ status: "unavailable", videoUrl: null });
    const base = { stepId: "mark_incision", success: true, pathErrorMm: 4.2, policySuccessRate: 0.9, demos: { human: 0, synthetic: 20 }, synthetic: true, videoUrl: "/robot/replays/baseline.mp4" };
    expect((await req("POST", "/coach/robot/result", base)).status).toBe(201);
    expect((await req("GET", `/coach/sessions/${sid}/robot-result`)).json).toEqual(expect.objectContaining({
      status: "ready", stepId: "mark_incision", stepTitle: "Mark McBurney incision", success: true, pathErrorMm: 4.2, policySuccessRate: 0.9,
      demos: { human: 0, synthetic: 20 }, synthetic: true, videoUrl: "/robot/replays/baseline.mp4",
    }));
    const demoId = (await req("POST", `/coach/sessions/${sid}/robot-demo`, { stepId: "mark_incision", frames: [frame(0), frame(1), frame(2)] })).json.demoId;
    expect((await req("GET", `/coach/sessions/${sid}/robot-result`)).json.status).toBe("pending");
    const own = { ...base, success: false, pathErrorMm: 31, policySuccessRate: 0.4, demos: { human: 1, synthetic: 20 }, synthetic: false, demoId, videoUrl: "/robot/replays/x.mp4" };
    expect((await req("POST", `/coach/sessions/${sid}/robot-result`, { ...own, videoUrl: "https://evil.example/x.mp4" })).status).toBe(400);
    expect((await req("POST", `/coach/sessions/${sid}/robot-result`, own)).status).toBe(201);
    expect((await req("GET", `/coach/sessions/${sid}/robot-result`)).json).toMatchObject({ status: "ready", success: false, pathErrorMm: 31, demos: { human: 1, synthetic: 20 }, synthetic: false, demoId });
    // The policy rollout lands in the same /robot-attempts record and the dashboard sim log.
    const attempts = (await req("GET", `/coach/sessions/${sid}/robot-attempts`)).json.attempts;
    expect(attempts).toEqual([expect.objectContaining({ attemptId: demoId, stepId: "mark_incision", success: false, task: "robot_policy_rollout", source: "sim policy (headset demos)" })]);
    expect(logs.some((l) => /Simulated robot missed "Mark McBurney incision", path error 31.0 mm, policy 40% .*sim only/.test(l.text))).toBe(true);
  });

});

describe("robot replay streaming", () => {
  it("answers full, ranged, suffix-ranged and unsatisfiable requests", async () => {
    const { app } = await setup();
    const mp4 = new Uint8Array(1000).map((_, i) => i % 251);
    expect((await app.request("/robot/replays/run-1.mp4", { method: "PUT", body: mp4 })).status).toBe(201);
    expect((await app.request("/robot/replays/empty.mp4", { method: "PUT", body: new Uint8Array(0) })).status).toBe(400);
    expect((await app.request("/robot/replays/notes.txt", { method: "PUT", body: mp4 })).status).toBe(400);
    const full = await app.request("/robot/replays/run-1.mp4");
    expect(full.status).toBe(200);
    expect(full.headers.get("content-type")).toBe("video/mp4");
    expect(full.headers.get("accept-ranges")).toBe("bytes");
    expect(new Uint8Array(await full.arrayBuffer())).toEqual(mp4);
    const part = await app.request("/robot/replays/run-1.mp4", { headers: { Range: "bytes=100-199" } });
    expect(part.status).toBe(206);
    expect(part.headers.get("content-range")).toBe("bytes 100-199/1000");
    expect(part.headers.get("content-length")).toBe("100");
    expect(new Uint8Array(await part.arrayBuffer())).toEqual(mp4.slice(100, 200));
    const open = await app.request("/robot/replays/run-1.mp4", { headers: { Range: "bytes=900-" } });
    expect(open.headers.get("content-range")).toBe("bytes 900-999/1000");
    const suffix = await app.request("/robot/replays/run-1.mp4", { headers: { Range: "bytes=-10" } });
    expect(new Uint8Array(await suffix.arrayBuffer())).toEqual(mp4.slice(990));
    expect((await app.request("/robot/replays/run-1.mp4", { headers: { Range: "bytes=5000-" } })).status).toBe(416);
    expect((await app.request("/robot/replays/missing.mp4")).status).toBe(404);
  });
});
