import { legacyAppendectomyCase } from "./legacy-appendectomy-fixture.js";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { CoachSession, reflexLines } from "../src/coach.js";
import { createArbiter, semanticKey, type ArbiterAlert, type ArbiterSnapshot } from "../src/jarvis/arbiter.js";
import { ReflexAudio } from "../src/reflex.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

let seq = 0;
const alert = (kind: string, tier: ArbiterAlert["tier"], extra: Partial<ArbiterAlert> = {}): ArbiterAlert => ({
  id: `a${++seq}`,
  kind,
  tier,
  stepId: "s1",
  version: seq,
  say: kind,
  reflexKey: "",
  highlight: [],
  ...extra,
});
const snap = (stepId = "s1", stuckLevel = 1, status = "active"): ArbiterSnapshot => ({ status, stuckLevel, step: { id: stepId } });

function rig() {
  let t = 100_000;
  const arb = createArbiter({ now: () => t });
  arb.setSnapshot(snap());
  return { arb, advance: (ms: number) => void (t += ms) };
}

describe("arbiter", () => {
  it("keeps advisories silent and sends warnings with a clip to the reflex path", () => {
    const { arb } = rig();
    expect(arb.offer(alert("tracking_restored", "advisory")).action).toBe("silent");
    expect(arb.offer(alert("mistake", "warning", { reflexKey: "mistake.x" })).action).toBe("reflex");
    expect(arb.offer(alert("tracking_lost", "warning")).action).toBe("speak_now");
  });

  it("drops queued cautions that a warning supersedes", () => {
    const { arb, advance } = rig();
    arb.offer(alert("stuck", "caution"));
    arb.offer(alert("mistake", "warning", { reflexKey: "mistake.x" }));
    advance(10_000);
    expect(arb.next()).toBeNull();
    expect(arb.queueSize).toBe(0);
  });

  it("waits for the agent to finish and the learner to be quiet", () => {
    const { arb, advance } = rig();
    arb.offer(alert("step_complete", "caution"));
    arb.setMode("speaking");
    advance(10_000);
    expect(arb.next()).toBeNull();
    arb.setMode("listening");
    expect(arb.next()).toBeNull(); // settling after speech
    advance(800);
    arb.userSpoke();
    expect(arb.next()).toBeNull(); // learner just talked
    advance(1600);
    expect(arb.next()?.kind).toBe("step_complete");
  });

  it("uses the response-complete event when the SDK provides it", () => {
    const { arb, advance } = rig();
    arb.setMode("speaking");
    arb.setMode("listening");
    arb.offer(alert("step_complete", "caution"));
    advance(5000);
    arb.responseComplete();
    expect(arb.next()?.kind).toBe("step_complete");
    arb.offer(alert("step_complete", "caution"));
    advance(5000);
    expect(arb.next()).toBeNull(); // a turn was just sent; waiting for its completion
    arb.responseComplete();
    expect(arb.next()?.kind).toBe("step_complete");
  });

  it("does not stall after a clip delivery once the SDK reports response completion", () => {
    const { arb, advance } = rig();
    arb.responseComplete(); // the SDK has shown it emits completion events
    arb.offer(alert("step_complete", "caution", { reflexKey: "step.b" }));
    advance(5000);
    expect(arb.next()?.reflexKey).toBe("step.b");
    arb.deliveryDone(); // clip finished; no agent turn will complete
    arb.offer(alert("step_complete", "caution", { reflexKey: "step.c" }));
    advance(3000);
    expect(arb.next()?.reflexKey).toBe("step.c");
  });

  it("coalesces repeats, keeping only the newest", () => {
    const { arb, advance } = rig();
    arb.offer(alert("step_complete", "caution", { say: "first" }));
    arb.offer(alert("step_complete", "caution", { say: "second" }));
    expect(arb.queueSize).toBe(1);
    advance(5000);
    expect(arb.next()?.say).toBe("second");
  });

  it("enforces per-type cooldowns and spacing between proactive turns", () => {
    const { arb, advance } = rig();
    advance(10_000);
    arb.offer(alert("wrong_instrument", "caution", { highlight: ["x"] }));
    expect(arb.next()?.kind).toBe("wrong_instrument");
    expect(arb.offer(alert("wrong_instrument", "caution", { highlight: ["x"] })).action).toBe("dropped");
    arb.offer(alert("stuck", "caution"));
    advance(3000);
    arb.responseComplete();
    expect(arb.next()).toBeNull(); // 6 s gap before a non-urgent hint
    advance(3500);
    expect(arb.next()?.kind).toBe("stuck");
  });

  it("drops cautions that went stale before they could be spoken", () => {
    const { arb, advance } = rig();
    arb.offer(alert("stuck", "caution"));
    arb.offer(alert("danger_focus", "caution", { highlight: ["cbd"] }));
    arb.setSnapshot(snap("s2", 0));
    advance(10_000);
    expect(arb.next()).toBeNull();
    expect(arb.stats.droppedStale).toBe(2);
  });

  it("lets case completion replace pending step completions", () => {
    const { arb, advance } = rig();
    arb.offer(alert("step_complete", "caution"));
    arb.offer(alert("case_complete", "caution"));
    arb.setSnapshot(snap("", 0, "completed"));
    advance(10_000);
    expect(arb.next()?.kind).toBe("case_complete");
    expect(arb.queueSize).toBe(0);
  });

  it("ignores ticking timers when deciding whether context changed", () => {
    const { s } = { s: new CoachSession("coach-arb", buildCase(fixture("patient-demo-pediatric-asthma"), "", NOW), () => NOW) };
    const a = s.snapshot();
    const b = { ...a, secondsOnStep: a.secondsOnStep + 30, version: a.version + 1 };
    expect(semanticKey(b)).toBe(semanticKey(a));
    s.handle(s.nextCorrectEvent()!);
    expect(semanticKey(s.snapshot())).not.toBe(semanticKey(a));
  });
});

describe("warning alerts", () => {
  it("carry a reflex clip key, a version, and open with Stop", () => {
    const s = new CoachSession("coach-w", legacyAppendectomyCase(), () => NOW);
    while (s.engine.current?.id !== "divide_mesoappendix") s.handle(s.nextCorrectEvent()!);
    const [a] = s.handle({ type: "touch", structureId: "terminal_ileum", instrumentId: "vessel_sealer" }).alerts;
    expect(a).toMatchObject({ tier: "warning", reflexKey: "mistake.sealer_on_ileum", version: s.version });
    expect(a!.say).toMatch(/^Stop\. /);
    expect(reflexLines(s.kase).map((l) => l.key)).toContain(a!.reflexKey);
  });

  it("lists one clip per high-severity mistake plus tracking loss", () => {
    const lines = reflexLines(legacyAppendectomyCase());
    expect(new Set(lines.map((l) => l.key)).size).toBe(lines.length);
    expect(lines.map((l) => l.key)).toEqual(expect.arrayContaining(["mistake.sealer_on_ileum", "mistake.staple_ileum", "mistake.port_into_bladder", "tracking_lost"]));
    expect(lines.map((l) => l.key)).not.toContain("mistake.grab_appendix"); // moderate: spoken by the LLM instead
    expect(lines.find((l) => l.key === "step.find_appendix")?.text).toBe("Next step: locate the appendix.");
    expect(lines.some((l) => l.key === "step.access_umbilical")).toBe(false); // the first step is announced by the greeting
  });
});

describe("reflex routes", () => {
  it("renders each clip once, caches it, and serves the page modules", async () => {
    let calls = 0;
    const reflex = new ReflexAudio({
      apiKey: "test",
      voiceId: "voice",
      cacheDir: mkdtempSync(join(tmpdir(), "reflex-")),
      fetchImpl: (async () => {
        calls += 1;
        return new Response(new Uint8Array([1, 2, 3]), { status: 200 });
      }) as typeof fetch,
    });
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, reflex });
    const created = (await (await app.request("/coach/sessions", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-pediatric-asthma" }) })).json()) as { sessionId: string };
    const list = (await (await app.request(`/jarvis/reflex/${created.sessionId}`)).json()) as { configured: boolean; lines: { key: string; route: string }[] };
    expect(list.configured).toBe(true);
    const route = list.lines.find((l) => l.key === "tracking_lost")!.route;
    for (let i = 0; i < 2; i++) {
      const res = await app.request(route);
      expect(res.status).toBe(200);
      expect(res.headers.get("content-type")).toBe("audio/mpeg");
      expect(new Uint8Array(await res.arrayBuffer())).toEqual(new Uint8Array([1, 2, 3]));
    }
    expect(calls).toBe(1);
    expect((await app.request(`/jarvis/reflex/${created.sessionId}/mistake.nope`)).status).toBe(404);
    for (const f of ["app.js", "arbiter.js"]) expect((await app.request(`/jarvis/${f}`)).headers.get("content-type")).toMatch(/javascript/);
  });

  it("reports unconfigured reflex audio instead of failing silently", async () => {
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0 });
    const created = (await (await app.request("/coach/sessions", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-pediatric-asthma" }) })).json()) as { sessionId: string };
    expect(((await (await app.request(`/jarvis/reflex/${created.sessionId}`)).json()) as { configured: boolean }).configured).toBe(false);
    expect((await app.request(`/jarvis/reflex/${created.sessionId}/tracking_lost`)).status).toBe(503);
  });
});
