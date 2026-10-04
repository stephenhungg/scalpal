import { Hono } from "hono";
import { registerCoachRoutes } from "../src/coach-routes.js";
import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { buildCase } from "../src/case-builder.js";
import { CoachSession, reflexLines, renderContext } from "../src/coach.js";
import { PROCEDURES_BY_ID } from "../src/catalog/procedures.js";
import { bodyAction } from "../src/open-body.js";
import { idealBodyActions } from "../src/open-body-fixtures.js";
import { NOW, fixture, fixtureClient } from "./helpers.js";

describe("Quest coach polling", () => {
  it("escalates idle hints without SSE, preserves cursor, and pauses on tracking loss", async () => {
    let time = NOW.getTime();
    const app = createApp({ client: fixtureClient(), now: () => new Date(time), coachTickMs: 0, stuckPolicy: { seconds: [15, 25, 40, 60], attempts: [2, 3, 5, 8] } });
    const request = async (method: string, route: string, body?: unknown) => {
      const response = await app.request(route, { method, headers: { "Content-Type": "application/json" }, body: body ? JSON.stringify(body) : undefined });
      expect(response.status).toBe(method === "POST" && route === "/coach/sessions" ? 201 : 200);
      return await response.json() as Record<string, any>;
    };
    const { sessionId } = await request("POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma", mode: "virtual" });
    const path = `/coach/sessions/${sessionId}`;
    time += 21000;
    const first = await request("GET", `${path}/alerts?after=0`);
    expect(first.alerts).toHaveLength(1);
    expect(first.alerts[0]).toMatchObject({ kind: "stuck", tier: "caution" });
    expect(first.snapshot).toMatchObject({ sessionId, hintTier: 1 });
    expect((await request("GET", `${path}/alerts?after=${first.latestSeq}`)).alerts).toEqual([]);
    time += 5000;
    const second = await request("GET", `${path}/alerts?after=${first.latestSeq}`);
    expect(second.snapshot.hintTier).toBe(2);
    expect(second.alerts).toHaveLength(1);
    await request("POST", `${path}/events`, { event: { type: "tracking", valid: false } });
    time += 100000;
    const paused = await request("GET", `${path}/alerts?after=${second.latestSeq}`);
    expect(paused.alerts.map((a: any) => a.kind)).toEqual(["tracking_lost"]);
    expect(paused.snapshot.status).toBe("paused");
    await request("POST", `${path}/events`, { event: { type: "tracking", valid: true } });
    const restored = await request("GET", `${path}/alerts?after=${paused.latestSeq}`);
    expect(restored.alerts.map((a: any) => a.kind)).toEqual(["tracking_restored"]);
  });
});

function openSession(clock = () => NOW) {
  const kase = buildCase(fixture("patient-demo-pediatric-asthma"), "", NOW);
  const procedure = PROCEDURES_BY_ID.get("open_appendectomy")!;
  return new CoachSession("coach-open", { ...kase, procedure, procedureId: procedure.id }, clock);
}

describe("open-body coaching authority", () => {
  it("records off-path consequences, all guardrails, bleed control and retry identity without synthetic catch-up", () => {
    const session = openSession();
    // Leave the mark milestone incomplete while physically exposing each layer.
    for (const step of ["incise_skin", "open_fascia", "split_muscle", "open_peritoneum"])
      for (const evidence of idealBodyActions(step)) session.receive({ type: "surgery", evidence }, { eventId: evidence.actionId, stepId: "ligate_base" });
    expect(session.snapshot().desynced).toBe(false);
    expect(session.snapshot().resyncCount).toBe(0);
    expect(session.engine.completedMilestones.has("mark_incision")).toBe(false);
    const cut = { type: "surgery" as const, evidence: bodyAction("cut", "cecum", { lengthMm: 5 }) };
    const injury = session.receive(cut, { eventId: "injury-1", stepId: "close" });
    expect(injury.accepted).toBe(true);
    expect(injury.alerts.filter(a => a.kind === "mistake")).toHaveLength(2);
    expect(session.snapshot().bodyFacts).toContainEqual({ key: ":contamination", value: 1 });
    expect(session.receive(cut, { eventId: "injury-1" }).reason).toBe("duplicate");
    expect(session.snapshot().mistakeCount).toBe(2);
    const bleed = session.handle({ type: "surgery", evidence: bodyAction("cut", "mesoappendix", { lengthMm: 5 }) });
    expect(bleed.alerts.some(a => a.kind === "bleeding")).toBe(true);
    session.handle({ type: "surgery", evidence: bodyAction("clamp", "mesoappendix", { instrumentId: "hemostat", timeMs: 1000 }) });
    expect(session.snapshot().activeBleeds).toEqual([]);
    expect(session.snapshot().bloodLossMl).toBeGreaterThan(0);
    expect(renderContext(session.snapshot())).toContain("expected order is guidance");
    expect(session.handle({ type: "bleeding", structureId: "mesoappendix", active: true, rateMlPerMin: 999, totalMl: 999 }).accepted).toBe(false);
    expect(session.snapshot().activeBleeds).toEqual([]);
  });

  it("emits each 15/25/40/60 second cue once with an authored reflex and no auto-awarded milestone", () => {
    let elapsed = 0;
    const session = openSession(() => new Date(NOW.getTime() + elapsed));
    const lines = reflexLines(session.kase);
    for (const seconds of [15, 25, 40, 60]) {
      elapsed = seconds * 1000;
      const [alert] = session.tick();
      expect(alert?.kind).toBe("stuck");
      expect(alert!.say.split(/\s+/).length).toBeLessThan(12);
      expect(lines.find(line => line.key === alert!.reflexKey)?.text).toBe(alert!.say);
      expect(session.tick()).toEqual([]);
    }
    expect(session.snapshot().hintTier).toBe(4);
    expect(session.snapshot().completedCount).toBe(0);
    expect(session.snapshot().hintsUsed).toBe(4);
  });

  it("retains rejection on event retry and reports physically inaccessible tissue", () => {
    const session = openSession();
    const action = bodyAction("cut", "cecum", { lengthMm: 5 });
    const out = session.receive({ type: "surgery", evidence: action }, { eventId: "blocked" });
    expect(out.accepted).toBe(true); // the attempted action is recorded, with a physical obstruction
    expect(session.snapshot().lastEvent).toContain("not_exposed");
    expect(session.snapshot().mistakeCount).toBe(0);
    const invalid = { type: "surgery" as const, evidence: bodyAction("cut", "missing") };
    expect(session.receive(invalid, { eventId: "bad" }).reason).toContain("invalid:");
    expect(session.receive(invalid, { eventId: "bad" }).reason).toContain("invalid:");
  });
});


describe("body event HTTP boundary", () => {
  it("validates measured payloads and preserves duplicate receipts and per-session alerts", async () => {
    const app = new Hono();
    registerCoachRoutes(app, { loadCase: async () => openSession().kase, now: () => NOW, tickMs: 0 });
    const created = await app.request("/coach/sessions", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ patientId: "patient-demo-pediatric-asthma", mode: "virtual" }) });
    const { sessionId } = await created.json() as { sessionId: string };
    const post = async (event: unknown) => app.request(`/coach/sessions/${sessionId}/events`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ event }) });
    const event = { type: "surgery", evidence: bodyAction("mark", "skin", { instrumentId: "skin_marker", lengthMm: 60 }), eventId: "retry-mark", stepId: "close" };
    const accepted = await post(event);
    expect(accepted.status).toBe(200);
    expect((await accepted.json() as any).results[0]).toMatchObject({ accepted: true, applied: true });
    expect((await (await post(event)).json() as any).results[0]).toMatchObject({ accepted: true, applied: false, reason: "duplicate" });
    const invalid = await post({ ...event, eventId: "bad-frame", evidence: { ...event.evidence, coordinateFrame: "world" } });
    expect(invalid.status).toBe(400);
    const missing = await post({ ...event, eventId: "bad-position", evidence: { ...event.evidence, position: null } });
    expect(missing.status).toBe(400);
    expect((await app.request(`/coach/sessions/${sessionId}/alerts?after=-1`)).status).toBe(400);
    const feed = await (await app.request(`/coach/sessions/${sessionId}/alerts?after=0`)).json() as any;
    expect(feed.snapshot.sessionId).toBe(sessionId);
    expect(feed.snapshot.completedCount).toBe(1);
    expect(feed.alerts).toHaveLength(1);
    expect(feed.alerts[0].reflexKey).toBe("step.incise_skin");
    expect((await (await app.request(`/coach/sessions/${sessionId}/alerts?after=${feed.latestSeq}`)).json() as any).alerts).toEqual([]);
  });
});
