import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import type { CoachSnapshot } from "../src/coach.js";
import type { FrameDetector } from "../src/frame-detector.js";
import { describeMarks, type Frame, type SceneVision } from "../src/scene-vision.js";
import { NOW, fixtureClient } from "./helpers.js";

const JPEG = Buffer.from("fake jpeg bytes for tests").toString("base64");

function fakeVision() {
  const calls: { kind: string; frame: Frame; snapshot: CoachSnapshot; question?: string }[] = [];
  const vision: SceneVision = {
    look: async (frame, snapshot, question) => (calls.push({ kind: "look", frame, snapshot, question }), `You're looking at the ${frame.marks[0]?.label ?? "field"}.`),
    watch: async (frame, snapshot) => (calls.push({ kind: "watch", frame, snapshot }), `${frame.marks.length} labeled things in view.`),
  };
  return { calls, vision };
}

function rig(vision: SceneVision | null, watchMs = 4000, detector: FrameDetector | null = null) {
  let t = NOW.getTime();
  const app = createApp({ client: fixtureClient(), now: () => new Date(t), coachTickMs: 0, vision, watchMs, detector });
  const req = async (method: string, route: string, body?: unknown) => {
    const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
    return { status: res.status, json: (await res.json()) as Record<string, any> };
  };
  return { req, advance: (ms: number) => void (t += ms) };
}

const marks = [{ label: "Cecum", id: "cecum", source: "scene", box: { x: 0.1, y: 0.7, w: 0.2, h: 0.2 } }, { label: "scissors", id: "lap_scissors", source: "detector", box: { x: 0.6, y: 0.4, w: 0.1, h: 0.2 } }];

describe("scene vision", () => {
  it("watches frames at most every interval and puts the summary in Scalpal's context", async () => {
    const { calls, vision } = fakeVision();
    const { req, advance } = rig(vision);
    const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    expect((await req("POST", `/coach/sessions/${sid}/frame`, { image: `data:image/jpeg;base64,${JPEG}`, marks })).json).toMatchObject({ stored: true, marks: 2, watching: true });
    await new Promise((r) => setTimeout(r, 10));
    expect((await req("POST", `/coach/sessions/${sid}/frame`, { image: JPEG, marks })).json.watching).toBe(false); // within the interval
    advance(4000);
    expect((await req("POST", `/coach/sessions/${sid}/frame`, { image: JPEG, marks: [] })).json.watching).toBe(true);
    await new Promise((r) => setTimeout(r, 10));
    expect(calls.filter((c) => c.kind === "watch")).toHaveLength(2);
    expect(calls[0]!.frame.jpegBase64).toBe(JPEG); // data URL prefix stripped
    const state = (await req("GET", `/coach/sessions/${sid}`)).json;
    expect(state.snapshot.scene.summary).toBe("0 labeled things in view.");
    expect(state.context).toMatch(/In view \(camera\): 0 labeled things in view\./);
  });

  it("answers look_at_scene from the latest fresh frame", async () => {
    const { calls, vision } = fakeVision();
    const { req, advance } = rig(vision, 0);
    const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    const look = async () => (await req("POST", `/coach/sessions/${sid}/tools/look_at_scene`, { question: "what am I looking at?" })).json.result;
    expect(await look()).toMatch(/can't see your view/);
    await req("POST", `/coach/sessions/${sid}/frame`, { image: JPEG, marks, source: "quest" });
    expect(await look()).toBe("You're looking at the Cecum.");
    expect(calls[0]).toMatchObject({ kind: "look", question: "what am I looking at?" });
    expect(calls[0]!.frame.source).toBe("quest");
    advance(9000);
    expect(await look()).toMatch(/can't see your view/);
  });

  it("adds fresh detector boxes for the case's instruments to what Scalpal looks at", async () => {
    const { calls, vision } = fakeVision();
    const asked: string[][] = [];
    const detector: FrameDetector = {
      detect: async (_jpeg, labels) => (asked.push(labels), [{ label: "scissors", id: "lap_scissors", source: "detector", box: { x: 0.5, y: 0.5, w: 0.1, h: 0.1 } }]),
    };
    const { req, advance } = rig(vision, 0, detector);
    const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    await req("POST", `/coach/sessions/${sid}/frame`, { image: JPEG, marks: [] });
    await new Promise((r) => setTimeout(r, 10));
    expect(asked[0]).toContain("hand");
    expect(asked[0]!.length).toBeGreaterThan(1);
    await req("POST", `/coach/sessions/${sid}/tools/look_at_scene`, { question: "what's in my hand?" });
    expect(calls.at(-1)!.frame.marks.map((m) => m.label)).toEqual(["scissors"]);
    advance(5000); // the detection is now older than the newest frame allows
    await req("POST", `/coach/sessions/${sid}/frame`, { image: JPEG, marks: [] });
    await req("POST", `/coach/sessions/${sid}/tools/look_at_scene`, {});
    expect(calls.at(-1)!.frame.marks.map((m) => m.label)).toEqual(["scissors"]); // refreshed by the new detection
    // A detector that never answers adds nothing.
    const stale = rig(vision, 0, { detect: () => new Promise(() => {}) });
    const sid2 = (await stale.req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    await stale.req("POST", `/coach/sessions/${sid2}/frame`, { image: JPEG, marks: [] });
    await stale.req("POST", `/coach/sessions/${sid2}/tools/look_at_scene`, {});
    expect(calls.at(-1)!.frame.marks).toEqual([]);
  });

  it("says vision is not set up when no model is configured, and rejects bad frames", async () => {
    const { req } = rig(null);
    const sid = (await req("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    expect((await req("POST", `/coach/sessions/${sid}/tools/look_at_scene`, {})).json.result).toMatch(/vision isn't set up/);
    expect((await req("POST", `/coach/sessions/${sid}/frame`, { image: "not base64!!" })).status).toBe(400);
    expect((await req("POST", `/coach/sessions/${sid}/frame`, { image: JPEG })).json.watching).toBe(false);
  });

  it("describes labeled boxes with plain positions and marks which are exact", () => {
    const text = describeMarks(marks as Frame["marks"]);
    expect(text).toMatch(/Cecum \[cecum\] at the bottom left \(exact/);
    expect(text).toMatch(/scissors \[lap_scissors\] at the center \(detected/);
  });
});
