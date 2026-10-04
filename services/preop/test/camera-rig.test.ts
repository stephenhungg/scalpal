import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { createFrameLoop, createRigSession, type RigApiResult } from "../src/jarvis/camera-rig.js";
import { NOW, fixtureClient } from "./helpers.js";

// The camera page is a stand-in for the headset. A tab left open during a real Quest run must not ack
// Scalpal's commands (the first ack is final, so Scalpal would hear "applied" for a highlight the headset
// never drew) or post tracking loss that pauses headset scoring, unless the operator opts in.

function harness(actsAsHeadset: { on: boolean }) {
  const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
  const posts: string[] = [];
  const failing = new Set<string>();
  const api = async (method: string, path: string, body?: unknown): Promise<RigApiResult> => {
    if (method === "POST") posts.push(path);
    if (failing.has(path)) return { ok: false, json: { error: { message: "unavailable" } } };
    const res = await app.request(path, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { ok: res.ok, json: (await res.json()) as Record<string, unknown> };
  };
  const attached: unknown[] = [];
  const commands: unknown[] = [];
  const rig = createRigSession({ api, actsAsHeadset: () => actsAsHeadset.on, onAttach: (k) => attached.push(k), onSnapshot: () => {}, onCommand: (c) => commands.push(c), feed: () => {} });
  const startSurgery = async () => {
    const created = (await api("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId as string;
    expect((await api("POST", `/coach/sessions/${created}/commands`, { action: "highlight", structure: "appendix" })).ok).toBe(true);
    posts.length = 0;
    return created;
  };
  return { app, api, rig, posts, failing, attached, commands, startSurgery };
}

describe("camera rig session", () => {
  it("only observes by default: no acks and no events", async () => {
    const mode = { on: false };
    const h = harness(mode);
    const sid = await h.startSurgery();
    await h.rig.attach();
    expect(h.rig.sid).toBe(sid);
    expect(await h.rig.send({ type: "tracking", valid: false })).toBeNull();
    expect(h.posts).toEqual([]);
    expect((await h.api("GET", `/coach/sessions/${sid}/commands`)).json.commands).toHaveLength(1); // still pending for the real headset
    expect((await h.api("GET", `/coach/sessions/${sid}`)).json.snapshot.trackingValid).toBe(true);
  });

  it("acts as the headset only after opting in", async () => {
    const mode = { on: false };
    const h = harness(mode);
    const sid = await h.startSurgery();
    await h.rig.attach();
    mode.on = true;
    await h.rig.attach();
    expect(h.commands).toHaveLength(1);
    expect((await h.api("GET", `/coach/sessions/${sid}/commands`)).json.commands).toHaveLength(0);
    expect((await h.rig.send({ type: "tracking", valid: false }))?.ok).toBe(true);
    expect((await h.api("GET", `/coach/sessions/${sid}`)).json.snapshot.trackingValid).toBe(false);
  });

  it("does not half-attach when the case fails to load, and retries", async () => {
    const h = harness({ on: true });
    await h.startSurgery();
    h.failing.add("/patients/patient-demo-pediatric-asthma/case");
    await h.rig.attach();
    expect(h.rig.sid).toBe("");
    expect(h.posts).toEqual([]); // no acks for a session it could not load
    h.failing.clear();
    await h.rig.attach();
    expect(h.rig.sid).not.toBe("");
    expect(h.attached).toHaveLength(1);
  });
});

describe("camera frame loop", () => {
  const fakeRaf = () => {
    let queue: (() => void)[] = [];
    return {
      raf: (cb: () => void) => { queue.push(cb); },
      flush: () => { const run = queue; queue = []; run.forEach((cb) => cb()); },
      get pending() { return queue.length; },
    };
  };

  it("keeps one detection loop when the camera is switched", () => {
    const r = fakeRaf();
    let frames = 0;
    const loop = createFrameLoop(r.raf, () => { frames += 1; });
    expect(loop.start()).toBe(true);
    expect(loop.start()).toBe(false); // "Switch camera"
    for (let i = 0; i < 3; i++) r.flush();
    expect(frames).toBe(3);
    expect(r.pending).toBe(1);
  });

  it("survives a throwing frame and stops on request", () => {
    const r = fakeRaf();
    const errors: unknown[] = [];
    let n = 0;
    const loop = createFrameLoop(r.raf, () => { n += 1; if (n === 1) throw new Error("gpu lost"); }, (e) => errors.push(e));
    loop.start();
    r.flush();
    r.flush();
    expect(n).toBe(2);
    expect(errors).toHaveLength(1);
    loop.stop();
    r.flush();
    expect(n).toBe(2);
    expect(loop.running).toBe(false);
  });
});
