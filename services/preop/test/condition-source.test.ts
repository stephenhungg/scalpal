import { describe, expect, it } from "vitest";
import { createApp } from "../src/app.js";
import { PatientCondition, conditionFromRow, selectCondition, type ConditionView, type PatientConditionRow } from "../src/patient-condition.js";
import type { RealtimeBridge, RealtimeSink } from "../src/realtime-bridge.js";
import { NOW, fixtureClient } from "./helpers.js";

// The headset monitor, Jarvis and the dashboard read the coach snapshot's `condition`. While a fresh
// SpacetimeDB patient_condition row exists it must be the module's (the database is the authoritative
// patient); when the row is missing or stale the coach must keep working on its own local model, and say so.

const row = (over: Partial<PatientConditionRow> = {}): PatientConditionRow => ({
  baselineHr: 72, baselineRr: 14, baselineSource: "chart", weightKg: 70, scale: 8,
  hr: 131, rr: 35, sys: 99, dia: 68, spo2: 98, bloodLossPct: 35.5, hemorrhageClass: 3, label: "HR 131 · simulated from baseline 72 (chart)",
  bodyLostMl: 150, regionLostMl: 12.4, regionInjuriesJson: JSON.stringify([{ region: "neck", label: "neck", bleeding: true, rawBleedMlPerMin: 300, at: 1 }, { region: "bogus" }]),
  outcomeResult: "in_progress", outcomeCause: "", outcomeAt: undefined, ...over,
});

const local = (): ConditionView => new PatientCondition(() => 0).view();

describe("condition source selection", () => {
  it("uses the module's condition while its row is fresh", () => {
    const v = selectCondition(local(), { view: conditionFromRow(row()), ageMs: 900 });
    expect(v.source).toBe("spacetime");
    expect(v.vitals).toMatchObject({ hr: 131, sys: 99, hemorrhageClass: 3, simulated: true, scale: 8 });
    expect(v.rawBloodLossMl).toBe(162);
    expect(v.regions.map((r) => r.region)).toEqual(["neck"]); // unknown regions are dropped
  });

  it("falls back to the local model when the row is stale or missing", () => {
    expect(selectCondition(local(), { view: conditionFromRow(row()), ageMs: 3500 }).source).toBe("local");
    const v = selectCondition(local(), null);
    expect(v.source).toBe("local");
    expect(v.vitals.hr).toBe(72);
  });

  it("keeps a finished module outcome even though the row stopped ticking", () => {
    const died = conditionFromRow(row({ outcomeResult: "died", outcomeCause: "catastrophic injury to the head", hr: 0, outcomeAt: { toDate: () => new Date(NOW) } }));
    const v = selectCondition(local(), { view: died, ageMs: 60_000 });
    expect(v.source).toBe("spacetime");
    expect(v.outcome).toEqual({ result: "died", cause: "catastrophic injury to the head", at: new Date(NOW).toISOString() });
  });
});

function conditionSink(remote: () => { view: ConditionView; ageMs: number } | null) {
  const calls: { op: string; args: unknown[] }[] = [];
  const rec = (op: string) => (...args: unknown[]) => void calls.push({ op, args });
  const sink: RealtimeSink = {
    bound: true,
    coachMessage() {},
    coachStatus() {},
    attachEncounter() {},
    encounterPhase() {},
    encounterResult() {},
    highlight: async () => null,
    startCondition: rec("start"),
    setConditionBaseline: rec("baseline"),
    reportBody: rec("body"),
    reportInjury: rec("injury"),
    endCondition: rec("end"),
    readCondition: remote,
  };
  return { calls, sink };
}

async function req(app: ReturnType<typeof createApp>, method: string, route: string, body?: unknown) {
  const res = await app.request(route, { method, headers: body ? { "Content-Type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined });
  return { status: res.status, json: (await res.json()) as Record<string, any> };
}

describe("coach <-> SpacetimeDB patient condition", () => {
  it("starts the module's patient from the chart, forwards facts, and serves the module's condition", async () => {
    let remote: { view: ConditionView; ageMs: number } | null = null;
    const { calls, sink } = conditionSink(() => remote);
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: sink as unknown as RealtimeBridge });
    const created = await req(app, "POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" });
    const sid = created.json.sessionId as string;
    expect(created.json.snapshot.condition.source).toBe("local"); // no module row yet: fallback
    const start = calls.find((c) => c.op === "start");
    expect(start?.args[0]).toBe(sid);
    expect(start?.args[1]).toMatchObject({ baseline: { source: expect.stringMatching(/chart|authored/) }, weightKg: expect.any(Number), mlPerKg: expect.any(Number) });

    await req(app, "POST", `/coach/sessions/${sid}/simulate`, { kind: "cut_arm" });
    expect(calls.filter((c) => c.op === "injury").map((c) => c.args.slice(1))).toEqual([["right_arm", false]]);
    await req(app, "POST", `/coach/sessions/${sid}/simulate`, { kind: "control_injury" });
    expect(calls.filter((c) => c.op === "injury").map((c) => c.args.slice(1))).toEqual([["right_arm", false], ["right_arm", true]]);
    // Open the abdomen far enough for a field bleed; the bleed reaches the module with its rate.
    let bleeds: { rateMlPerMin: number }[] = [];
    for (let i = 0; i < 8 && !bleeds.length; i++) {
      await req(app, "POST", `/coach/sessions/${sid}/simulate`, { kind: "complete_step" });
      bleeds = (await req(app, "POST", `/coach/sessions/${sid}/simulate`, { kind: "bleed" })).json.snapshot.activeBleeds;
    }
    expect(bleeds.length).toBeGreaterThan(0);
    const body = calls.filter((c) => c.op === "body").at(-1);
    expect((body?.args[2] as { rateMlPerMin: number }[]).map((b) => b.rateMlPerMin)).toEqual(bleeds.map((b) => b.rateMlPerMin));

    remote = { view: conditionFromRow(row()), ageMs: 500 };
    const snap = (await req(app, "GET", `/coach/sessions/${sid}`)).json.snapshot ?? (await req(app, "GET", `/coach/sessions/${sid}`)).json;
    expect(snap.condition.source).toBe("spacetime");
    expect(snap.condition.vitals.hr).toBe(131);

    remote = { view: conditionFromRow(row()), ageMs: 10_000 };
    const stale = (await req(app, "GET", `/coach/sessions/${sid}`)).json;
    expect((stale.snapshot ?? stale).condition.source).toBe("local");
  });

  it("forwards a Presage baseline captured at the Time-Out", async () => {
    const { calls, sink } = conditionSink(() => null);
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: sink as unknown as RealtimeBridge });
    const sid = (await req(app, "POST", "/coach/sessions", { patientId: "patient-demo-pediatric-asthma" })).json.sessionId;
    await req(app, "POST", `/coach/sessions/${sid}/vitals/baseline`, { baseline: { hr: 66, rr: 13, sys: 121, dia: 79, source: "measured" } });
    expect(calls.find((c) => c.op === "baseline")?.args[1]).toMatchObject({ hr: 66, rr: 13, source: "measured" });
  });
});

describe("one death answer for display, refusals and alerts", () => {
  it("refuses actions and announces death once when the shared row says the patient died", async () => {
    let remote: { view: ConditionView; ageMs: number } | null = null;
    const { sink } = conditionSink(() => remote);
    const app = createApp({ client: fixtureClient(), now: () => NOW, coachTickMs: 0, realtime: sink as unknown as RealtimeBridge });
    const sid = (await req(app, "POST", "/coach/sessions", { patientId: "patient-demo-multi-source" })).json.sessionId as string;
    const live = (await req(app, "GET", `/coach/sessions/${sid}`)).json.snapshot.condition as ConditionView;
    remote = { view: { ...live, source: "spacetime", outcome: { result: "died", cause: "hemorrhage (52% of blood volume lost, simulated)", at: new Date(NOW).toISOString() } } as ConditionView, ageMs: 500 };
    const first = await req(app, "POST", `/coach/sessions/${sid}/events`, { event: { type: "contact", instrumentId: "scalpel", structureId: "skin" } });
    expect(JSON.stringify(first.json)).toMatch(/patient_died/);
    await req(app, "POST", `/coach/sessions/${sid}/simulate`, { kind: "tracking_lost" });
    const alerts = (await req(app, "GET", `/coach/sessions/${sid}/alerts?after=0`)).json.alerts.filter((a: any) => a.kind === "patient_died");
    expect(alerts.length).toBe(1);
    await req(app, "GET", `/coach/sessions/${sid}/alerts?after=0`);
    expect((await req(app, "GET", `/coach/sessions/${sid}/alerts?after=0`)).json.alerts.filter((a: any) => a.kind === "patient_died")).toHaveLength(1);
    expect((await req(app, "POST", `/coach/sessions/${sid}/hint`)).json).toBeTruthy();
  });
});
